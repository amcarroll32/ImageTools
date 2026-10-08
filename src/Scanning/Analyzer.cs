using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using ImageTools.Model;
using ImageTools.Platform;
using Microsoft.Win32.SafeHandles;

namespace ImageTools.Scanning;

/// <summary>
/// Runs after the scan: finds byte-identical images, then reads photo details.
/// Duplicates are found in stages so most files are never read in full:
/// same size → same first 64 KB → same SHA-256 of the whole file. Hashes and details are
/// cached per path. App assets and online-only files are never opened.
/// </summary>
public sealed class Analyzer(IReadOnlyList<FsNode> drives, HashCache cache)
{
    private const int HeadBytes = 64 * 1024;
    private const int MaxFoldersPerGroup = 40;
    private const int MaxMatches = 500;

    public enum Stage
    {
        Heads,
        Full,
        Details,
        Done,
    }

    private sealed class Item(FsNode node, string path)
    {
        public FsNode Node { get; } = node;
        public string Path { get; } = path;
        public HashCache.Entry Entry { get; set; } = null!;

        /// <summary>The file couldn't be read this time.</summary>
        public bool Failed { get; set; }
    }

    private List<Item> _eligible = [];
    private long _done;
    private long _total;

    public volatile Stage Current = Stage.Heads;

    /// <summary>Progress through the current stage: files for heads and details, bytes for full hashes.</summary>
    public long Done => Interlocked.Read(ref _done);
    public long Total => Interlocked.Read(ref _total);

    /// <summary>Files that share their size with another image, i.e. could be duplicates.</summary>
    public long Candidates { get; private set; }

    public IReadOnlyList<string> Roots => drives.Select(d => d.Name).ToList();

    // ---- Duplicates ----

    /// <summary>Images this check saw for the first time (or changed since), when the cache wasn't empty.</summary>
    public long NewFiles { get; private set; }

    /// <summary>The cache had entries when this check started, so <see cref="NewFiles"/> means "since the last check".</summary>
    public bool HadCache { get; private set; }

    public DupResult FindDuplicates(CancellationToken ct)
    {
        HadCache = cache.Count > 0;
        int run = cache.BeginRun();
        _eligible = CollectEligible();

        // Stage 0: a file with a unique size can't have a byte-identical copy.
        var candidates = new List<Item>();
        foreach (var group in _eligible.GroupBy(i => i.Node.Size))
        {
            if (group.Count() == 1 || group.Key == 0)
            {
                foreach (var i in group)
                    MarkUnique(i.Node);
            }
            else
            {
                candidates.AddRange(group);
            }
        }
        Candidates = candidates.Count;
        foreach (var item in _eligible)
            item.Entry = cache.Get(item.Path, item.Node.Size, item.Node.LastWrite);
        NewFiles = _eligible.Count(i => i.Entry.CreatedInRun == run);

        // Stage 1: hash the first 64 KB (the whole file when it's smaller).
        Current = Stage.Heads;
        var needHead = candidates.Where(i => !i.Entry.HasHead).ToList();
        Reset(needHead.Count);
        ForEachPerDrive(needHead, item =>
        {
            HashHead(item);
            Interlocked.Increment(ref _done);
        }, ct);

        // Stage 2: full hashes, only where size and head both collide.
        var sameHead = candidates
            .Where(i => !i.Failed)
            .GroupBy(i => (i.Node.Size, i.Entry.Head))
            .ToList();
        var needFull = new List<Item>();
        foreach (var group in sameHead)
        {
            if (group.Count() == 1)
                MarkUnique(group.First().Node);
            else
                needFull.AddRange(group.Where(i => !i.Entry.HasFull));
        }
        Current = Stage.Full;
        Reset(needFull.Sum(i => i.Node.Size));
        ForEachPerDrive(needFull, HashFull, ct);
        ct.ThrowIfCancellationRequested();

        // Folder totals are rebuilt from scratch now, so they don't flicker to zero while the check runs.
        ResetFolderTotals();
        var groups = new List<DuplicateGroup>();
        foreach (var group in sameHead.Where(g => g.Count() > 1).SelectMany(g => g)
                     .Where(i => !i.Failed && i.Entry.HasFull)
                     .GroupBy(i => (i.Node.Size, i.Entry.Full)))
        {
            var members = group.Select(i => i.Node).ToList();
            if (members.Count == 1)
            {
                MarkUnique(members[0]);
                continue;
            }
            var dup = new DuplicateGroup(group.Key.Size, members.OrderBy(m => m.FullPath, StringComparer.OrdinalIgnoreCase).ToList());
            foreach (var m in members)
            {
                m.Dup = DupStatus.Duplicate;
                m.Group = dup;
                for (var a = m.Parent; a != null; a = a.Parent)
                {
                    a.DupCount++;
                    a.DupBytes += m.Size;
                }
            }
            groups.Add(dup);
        }
        groups.Sort((a, b) => b.Extra.CompareTo(a.Extra));

        long unreadable = _eligible.Count(i => i.Failed);
        return new DupResult(
            groups,
            FindFolderMatches(groups),
            groups.Sum(g => (long)g.Members.Count),
            groups.Sum(g => g.Extra),
            unreadable,
            _eligible.Count - unreadable);
    }

    /// <summary>
    /// Lists every image that may be opened, with its path. Earlier results stay on the images
    /// until this check replaces them, so a re-check after Scan for new doesn't blank the map.
    /// </summary>
    private List<Item> CollectEligible()
    {
        var items = new List<Item>();
        var stack = new Stack<(FsNode Node, string Path)>();
        foreach (var d in drives)
            stack.Push((d, d.Name));
        while (stack.Count > 0)
        {
            var (node, path) = stack.Pop();
            if (node.Kind == NodeKind.File)
            {
                if (!node.IsAsset && !node.IsCloudOnly)
                {
                    items.Add(new Item(node, path));
                }
                else
                {
                    node.Group = null;
                    node.Dup = DupStatus.NotChecked;
                }
                continue;
            }
            foreach (var c in node.Children ?? [])
                stack.Push((c, Path.Join(path, c.Name)));
        }
        return items;
    }

    private static void MarkUnique(FsNode file)
    {
        file.Group = null;
        file.Dup = DupStatus.Unique;
    }

    private void ResetFolderTotals()
    {
        var stack = new Stack<FsNode>(drives);
        if (drives.Count > 0 && drives[0].Parent is { } root)
            (root.DupCount, root.DupBytes) = (0, 0);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            (node.DupCount, node.DupBytes) = (0, 0);
            foreach (var c in node.Children ?? [])
                if (c.Kind != NodeKind.File)
                    stack.Push(c);
        }
    }

    private void Reset(long total)
    {
        Interlocked.Exchange(ref _done, 0);
        Interlocked.Exchange(ref _total, total);
    }

    private static SafeFileHandle Open(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);

    private static UInt128 ToHash(ReadOnlySpan<byte> sha256) => BinaryPrimitives.ReadUInt128LittleEndian(sha256);

    private void HashHead(Item item)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(HeadBytes);
        try
        {
            using var handle = Open(item.Path);
            int want = (int)Math.Min(item.Node.Size, HeadBytes), read = 0;
            while (read < want)
            {
                int n = RandomAccess.Read(handle, buffer.AsSpan(read, want - read), read);
                if (n == 0)
                    break;
                read += n;
            }
            var hash = ToHash(SHA256.HashData(buffer.AsSpan(0, read)));
            item.Entry.Head = hash;
            item.Entry.HasHead = true;
            if (item.Node.Size <= HeadBytes)
            {
                item.Entry.Full = hash;
                item.Entry.HasFull = true;
            }
            cache.MarkDirty();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.Failed = true;
            item.Node.Group = null;
            item.Node.Dup = DupStatus.Unreadable;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HashFull(Item item)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            using var handle = Open(item.Path);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            int n;
            while ((n = RandomAccess.Read(handle, buffer, offset)) > 0)
            {
                sha.AppendData(buffer, 0, n);
                offset += n;
                Interlocked.Add(ref _done, n);
            }
            item.Entry.Full = ToHash(sha.GetHashAndReset());
            item.Entry.HasFull = true;
            cache.MarkDirty();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.Failed = true;
            item.Node.Group = null;
            item.Node.Dup = DupStatus.Unreadable;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---- Folder matches ----

    private sealed class PairStats
    {
        public int InFirst;
        public int InSecond;
        public long BytesFirst;
        public long BytesSecond;
    }

    /// <summary>
    /// Pairs of folders that hold the same images, e.g. a camera folder copied twice where one
    /// copy has 300 more photos. Counts only images that were checked, directly inside each folder.
    /// </summary>
    private List<FolderMatch> FindFolderMatches(List<DuplicateGroup> groups)
    {
        var checkedCount = new Dictionary<FsNode, int>();
        foreach (var i in _eligible)
            if (i.Node.Dup is DupStatus.Unique or DupStatus.Duplicate)
                checkedCount[i.Node.Parent!] = checkedCount.GetValueOrDefault(i.Node.Parent!) + 1;

        var ids = new Dictionary<FsNode, int>();
        int Id(FsNode f) => ids.TryGetValue(f, out int id) ? id : ids[f] = ids.Count;

        var pairs = new Dictionary<(FsNode, FsNode), PairStats>();
        foreach (var group in groups)
        {
            var byFolder = group.Members.GroupBy(m => m.Parent!).Select(g => (Folder: g.Key, Count: g.Count())).ToList();
            if (byFolder.Count < 2 || byFolder.Count > MaxFoldersPerGroup)
                continue;
            for (int i = 0; i < byFolder.Count; i++)
            {
                for (int j = i + 1; j < byFolder.Count; j++)
                {
                    var (x, y) = Id(byFolder[i].Folder) < Id(byFolder[j].Folder) ? (byFolder[i], byFolder[j]) : (byFolder[j], byFolder[i]);
                    if (!pairs.TryGetValue((x.Folder, y.Folder), out var stats))
                        pairs[(x.Folder, y.Folder)] = stats = new PairStats();
                    stats.InFirst += x.Count;
                    stats.InSecond += y.Count;
                    stats.BytesFirst += group.Size * x.Count;
                    stats.BytesSecond += group.Size * y.Count;
                }
            }
        }

        var matches = new List<FolderMatch>();
        foreach (var ((first, second), s) in pairs)
        {
            int nFirst = checkedCount.GetValueOrDefault(first), nSecond = checkedCount.GetValueOrDefault(second);
            if (nFirst == 0 || nSecond == 0)
                continue;
            bool firstInside = s.InFirst >= nFirst, secondInside = s.InSecond >= nSecond;
            FolderMatch match;
            if (firstInside && secondInside)
                match = new FolderMatch(first, second, MatchKind.Identical, s.InFirst, nFirst, nSecond, s.BytesFirst);
            else if (firstInside)
                match = new FolderMatch(first, second, MatchKind.Contained, s.InFirst, nFirst, nSecond, s.BytesFirst);
            else if (secondInside)
                match = new FolderMatch(second, first, MatchKind.Contained, s.InSecond, nSecond, nFirst, s.BytesSecond);
            else
            {
                // Partial overlaps only matter when a good share of one folder is repeated.
                double fFirst = (double)s.InFirst / nFirst, fSecond = (double)s.InSecond / nSecond;
                if (Math.Max(fFirst, fSecond) < 0.25 || Math.Min(s.InFirst, s.InSecond) < 2)
                    continue;
                match = fFirst >= fSecond
                    ? new FolderMatch(first, second, MatchKind.Partial, s.InFirst, nFirst, nSecond, s.BytesFirst)
                    : new FolderMatch(second, first, MatchKind.Partial, s.InSecond, nSecond, nFirst, s.BytesSecond);
            }
            matches.Add(match);
        }

        // Most shared first: a big folder that's 98% inside another matters more than a tiny exact match.
        return matches
            .OrderByDescending(m => m.SharedBytes)
            .Take(MaxMatches)
            .ToList();
    }

    // ---- Photo details ----

    /// <summary>Date taken, camera, size, rating and tags for every image that may be opened.</summary>
    public void ReadDetails(CancellationToken ct)
    {
        var need = new List<Item>();
        foreach (var item in _eligible)
        {
            if (item.Failed)
                continue;
            if (item.Entry.Photo is { } photo)
                item.Node.Photo = photo;
            else
                need.Add(item);
        }

        Current = Stage.Details;
        Reset(need.Count);
        ForEachPerDrive(need, item =>
        {
            var info = PhotoProperties.Read(item.Path) ?? PhotoInfo.Empty;
            item.Entry.Photo = info;
            item.Node.Photo = info;
            cache.MarkDirty();
            Interlocked.Increment(ref _done);
        }, ct);
        Current = Stage.Done;
    }

    /// <summary>
    /// What a full rescan would read again, judged from the cache: the whole of every file that was
    /// hashed in full, the first 64 KB of the rest that were compared, and details for every image.
    /// </summary>
    public static (long Images, long Bytes) EstimateFullCheck(IReadOnlyList<FsNode> drives, HashCache cache)
    {
        long images = 0, bytes = 0;
        var stack = new Stack<(FsNode Node, string Path)>();
        foreach (var d in drives)
            stack.Push((d, d.Name));
        while (stack.Count > 0)
        {
            var (node, path) = stack.Pop();
            if (node.Kind == NodeKind.File)
            {
                if (node.IsAsset || node.IsCloudOnly)
                    continue;
                images++;
                var entry = cache.Peek(path, node.Size, node.LastWrite);
                if (entry is { HasFull: true } && node.Size > HeadBytes)
                    bytes += node.Size;
                else if (entry is { HasHead: true })
                    bytes += Math.Min(node.Size, HeadBytes);
                continue;
            }
            foreach (var c in node.Children ?? [])
                stack.Push((c, Path.Join(path, c.Name)));
        }
        return (images, bytes);
    }

    // ---- Parallelism ----

    /// <summary>
    /// Runs every drive at once, each with as many readers as its hardware handles well:
    /// SSDs take many parallel reads, while hard drives, cards and network shares thrash.
    /// </summary>
    private static void ForEachPerDrive(List<Item> items, Action<Item> work, CancellationToken ct)
    {
        var tasks = items
            .GroupBy(i => i.Node.Drive!)
            .Select(g => Task.Run(() => Parallel.ForEach(g, new ParallelOptions { MaxDegreeOfParallelism = Readers(g.Key), CancellationToken = ct }, work)))
            .ToArray();
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(ct);
        }
    }

    private static int Readers(FsNode drive) => drive.Hardware?.Kind switch
    {
        DriveKind.Ssd => 8,
        DriveKind.Hdd or DriveKind.UsbDrive or DriveKind.MemoryCard or DriveKind.Network or DriveKind.Cloud => 2,
        _ => 4,
    };
}
