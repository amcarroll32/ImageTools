using System.IO;

namespace ImageTools.Model;

/// <summary>Why a card image isn't in its archive folder, and so whether it's ticked by default.</summary>
public enum ImportBucket
{
    New,        // newer than anything already archived: shot since the last import (ticked)
    OlderGap,   // older than the newest archived image: perhaps deleted from the archive on purpose
    Elsewhere,  // a byte-identical copy already exists somewhere else on the PC
}

/// <summary>How the archive folder for a card folder was found.</summary>
public enum ArchiveSource
{
    Matched,         // it holds at least half of the card folder's images, or has the same name
    Remembered,      // matched on an earlier scan of this card
    NextToSiblings,  // the card's other folders are archived side by side; this one goes next to them
}

public sealed record ImportItem(FsNode Image, ImportBucket Bucket, FsNode? CopyElsewhere);

/// <param name="Archive">The archive folder in the scanned tree; null when it doesn't exist yet (or holds no images).</param>
/// <param name="Cutoff">Newest date among images already in the archive (UTC ticks); 0 when none are.</param>
public sealed record CardFolder(
    FsNode Folder,
    string ArchivePath,
    FsNode? Archive,
    ArchiveSource Source,
    int Shared,
    long Cutoff,
    List<ImportItem> Items)
{
    public int Count(ImportBucket bucket) => Items.Count(i => i.Bucket == bucket);
}

/// <param name="Unmatched">Camera folders on the card with no archive folder found.</param>
public sealed record CardStatus(FsNode Card, List<CardFolder> Folders, List<FsNode> Unmatched)
{
    public IEnumerable<ImportItem> NewItems => Folders.SelectMany(f => f.Items).Where(i => i.Bucket == ImportBucket.New);
    public int NewCount => NewItems.Count();
    public long NewBytes => NewItems.Sum(i => i.Image.Size);
    public int OtherCount => Folders.Sum(f => f.Items.Count(i => i.Bucket != ImportBucket.New));
    public int UnmatchedImages => Unmatched.Sum(f => CardSync.Images(f).Count());
    public bool UpToDate => Folders.Count > 0 && NewCount == 0 && OtherCount == 0 && Unmatched.Count == 0;
}

/// <summary>
/// Pairs each camera folder on a memory card with the folder it's archived to on the PC, and works
/// out which card images aren't there yet. Read-only: it only looks at the scan and duplicate results.
/// </summary>
public static class CardSync
{
    public static bool IsCard(FsNode? drive) =>
        drive is { Kind: NodeKind.Drive } && (drive.Hardware?.Kind == DriveKind.MemoryCard || (drive.IsRemovable && drive.HasDcimFolder));

    /// <summary>Images directly in a folder that were checked for duplicates.</summary>
    public static IEnumerable<FsNode> Images(FsNode folder) =>
        (folder.Children ?? []).Where(c => c.Kind == NodeKind.File && !c.IsAsset && c.Dup is DupStatus.Unique or DupStatus.Duplicate);

    public static string Key(FsNode card, FsNode folder) =>
        $"{card.VolumeSerial:X8}|{Path.GetRelativePath(card.Name, folder.FullPath)}";

    /// <summary>
    /// Status of every memory card among <paramref name="drives"/>. Confident matches are added to
    /// <paramref name="remembered"/>, so the card is recognized even after its folders stop matching.
    /// </summary>
    public static List<CardStatus> Find(IReadOnlyList<FsNode> drives, Dictionary<string, string> remembered, out bool rememberedChanged)
    {
        rememberedChanged = false;
        var result = new List<CardStatus>();
        foreach (var card in drives.Where(IsCard))
        {
            var folders = CameraFolders(card);
            if (folders.Count == 0)
                continue;

            var archives = new Dictionary<FsNode, (FsNode? Archive, string Path, ArchiveSource Source)>();

            // 1. A folder that holds at least half of the card folder's images (or shares its name).
            foreach (var f in folders)
            {
                if (StrongMatch(f) is not { } match)
                    continue;
                archives[f] = (match, match.FullPath, ArchiveSource.Matched);
                if (card.VolumeSerial != 0)
                {
                    string key = Key(card, f);
                    if (!remembered.TryGetValue(key, out var old) || !old.Equals(match.FullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        remembered[key] = match.FullPath;
                        rememberedChanged = true;
                    }
                }
            }

            // 2. The archive this card folder matched last time.
            foreach (var f in folders.Where(f => !archives.ContainsKey(f)))
            {
                if (card.VolumeSerial != 0 && remembered.TryGetValue(Key(card, f), out var path) && FindByPath(drives, path) is { } node)
                    archives[f] = (node, path, ArchiveSource.Remembered);
            }

            // 3. Next to where the card's other folders are archived (e.g. a new 102CANON beside 100CANON and 101CANON).
            foreach (var f in folders.Where(f => !archives.ContainsKey(f)))
            {
                var parent = archives
                    .Where(kv => kv.Key.Parent == f.Parent && kv.Value.Archive?.Parent is { Kind: NodeKind.Directory or NodeKind.Drive })
                    .GroupBy(kv => kv.Value.Archive!.Parent!)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault();
                if (parent == null)
                    continue;
                var existing = parent.Children?.FirstOrDefault(c => c.Kind == NodeKind.Directory && c.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
                archives[f] = (existing, Path.Join(parent.FullPath, f.Name), ArchiveSource.NextToSiblings);
            }

            var planned = folders.Where(archives.ContainsKey)
                .Select(f => BuildFolder(f, archives[f].Archive, archives[f].Path, archives[f].Source))
                .ToList();
            result.Add(new CardStatus(card, planned, folders.Where(f => !archives.ContainsKey(f)).ToList()));
        }
        return result;
    }

    /// <summary>Folders on the card that directly hold checked images (DCIM\100CANON…), Recycle Bin excluded.</summary>
    private static List<FsNode> CameraFolders(FsNode card)
    {
        var folders = new List<FsNode>();
        var stack = new Stack<FsNode>();
        stack.Push(card);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.Library == Library.RecycleBin)
                continue;
            if (n.Kind == NodeKind.Directory && Images(n).Any())
                folders.Add(n);
            foreach (var c in n.Children ?? [])
                if (c.Kind == NodeKind.Directory)
                    stack.Push(c);
        }
        folders.Sort((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
        return folders;
    }

    /// <summary>
    /// The PC folder that shares the most images with a card folder, if it's clearly its archive:
    /// it holds at least half of the card folder's images, or has the same name (100CANON ↔ 100CANON).
    /// A handful of photos picked out into some project folder doesn't count.
    /// </summary>
    private static FsNode? StrongMatch(FsNode folder)
    {
        var images = Images(folder).ToList();
        var counts = new Dictionary<FsNode, int>();
        foreach (var img in images)
        {
            if (img.Group == null)
                continue;
            var seen = new HashSet<FsNode>();
            foreach (var m in img.Group.Members)
                if (m != img && !m.IsAsset && !IsCard(m.Drive) && seen.Add(m.Parent!))
                    counts[m.Parent!] = counts.GetValueOrDefault(m.Parent!) + 1;
        }
        return counts
            .Where(kv => kv.Value * 2 >= images.Count
                         || (kv.Key.Name.Equals(folder.Name, StringComparison.OrdinalIgnoreCase) && kv.Value >= Math.Min(3, images.Count)))
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .FirstOrDefault();
    }

    private static CardFolder BuildFolder(FsNode folder, FsNode? archive, string archivePath, ArchiveSource source)
    {
        var images = Images(folder).ToList();
        var shared = archive == null
            ? []
            : images.Where(i => i.Group?.Members.Any(m => m.Parent == archive) == true).ToList();
        long cutoff = shared.Count > 0 ? shared.Max(i => i.DateTicks) : 0;
        var sharedSet = shared.ToHashSet();

        var items = new List<ImportItem>();
        foreach (var img in images.Where(i => !sharedSet.Contains(i)))
        {
            var elsewhere = img.Group?.Members.FirstOrDefault(m => m != img && !m.IsAsset && !IsCard(m.Drive));
            var bucket = elsewhere != null ? ImportBucket.Elsewhere
                : img.DateTicks > cutoff ? ImportBucket.New
                : ImportBucket.OlderGap;
            items.Add(new ImportItem(img, bucket, elsewhere));
        }
        items.Sort((a, b) => a.Image.DateTicks != b.Image.DateTicks
            ? a.Image.DateTicks.CompareTo(b.Image.DateTicks)
            : string.Compare(a.Image.Name, b.Image.Name, StringComparison.OrdinalIgnoreCase));
        return new CardFolder(folder, archivePath, archive, source, shared.Count, cutoff, items);
    }

    /// <summary>The folder at <paramref name="path"/> in the scanned tree, or null if it isn't there (or holds no images).</summary>
    public static FsNode? FindByPath(IReadOnlyList<FsNode> drives, string path)
    {
        string? root = Path.GetPathRoot(path);
        var node = drives.FirstOrDefault(d => d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
        if (node == null || root == null)
            return null;
        foreach (var segment in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children?.FirstOrDefault(c => c.Kind == NodeKind.Directory && c.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (node == null)
                return null;
        }
        return node;
    }
}
