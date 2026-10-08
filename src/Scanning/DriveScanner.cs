using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;
using ImageTools.Model;

namespace ImageTools.Scanning;

/// <summary>
/// Scans one drive with a pool of worker threads, keeping only image files. Each worker lists
/// one directory at a time and queues its subdirectories, so the tree is built breadth-first
/// without locking. Libraries and app-asset folders are decided per folder as it's listed and
/// inherited downwards. Sizes roll up in one pass at the end, and folders without images are dropped.
/// </summary>
public sealed class DriveScanner
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudOnlyMask = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false,   // we want the exception so we can flag the folder
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,         // include hidden and system files
        BufferSize = 64 * 1024,
    };

    private readonly ConcurrentQueue<(FsNode Node, string Path, int Depth)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentBag<FsNode> _libraryRoots = [];
    private int _pending;
    private volatile bool _done;

    private long _bytes;
    private long _images;
    private long _imageBytes;
    private long _dirs;
    private long _denied;

    public DriveScanner(FsNode driveNode, long totalSize, long freeSize)
    {
        Drive = driveNode;
        TotalSize = totalSize;
        FreeSize = freeSize;
    }

    public FsNode Drive { get; }
    public long TotalSize { get; }
    public long FreeSize { get; }
    public long UsedSize => TotalSize - FreeSize;

    /// <summary>Bytes of all files listed so far (images or not), for the progress bar.</summary>
    public long ScannedBytes => Interlocked.Read(ref _bytes);
    public long ImagesFound => Interlocked.Read(ref _images);
    public long ImageBytesFound => Interlocked.Read(ref _imageBytes);
    public long ScannedDirs => Interlocked.Read(ref _dirs);
    public long DeniedDirs => Interlocked.Read(ref _denied);
    public volatile string? CurrentPath;

    /// <summary>Folders where a library starts (Screenshots, DCIM, …) that still hold images after the scan.</summary>
    public IReadOnlyList<FsNode> LibraryRoots { get; private set; } = [];

    public Task ScanAsync(CancellationToken ct) => Task.Run(() => Scan(ct), ct);

    private void Scan(CancellationToken ct)
    {
        int workerCount = Math.Clamp(Environment.ProcessorCount, 4, 12);
        Enqueue(Drive, Drive.Name, 0);

        var workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = new Thread(() => Worker(workerCount, ct))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"Scan {Drive.Name} #{i}",
            };
            workers[i].Start();
        }
        foreach (var w in workers)
            w.Join();

        ct.ThrowIfCancellationRequested();
        _totals = RollUp(Drive);
        LibraryRoots = _libraryRoots.Where(r => r.FileCount > r.AssetCount).ToList();
    }

    private void Enqueue(FsNode node, string path, int depth)
    {
        Interlocked.Increment(ref _pending);
        _queue.Enqueue((node, path, depth));
        _signal.Release();
    }

    private void Worker(int workerCount, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                _signal.Wait(ct);
                if (_done)
                    return;
                if (!_queue.TryDequeue(out var item))
                    continue;

                ListDirectory(item.Node, item.Path, item.Depth);

                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    _done = true;
                    _signal.Release(workerCount);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private enum EntryKind : byte { Directory, Image, Marker, Other }

    private readonly record struct Entry(EntryKind Kind, string? Name, long Length, FileAttributes Attributes, ImageFormat Format, long Modified);

    private static Entry Transform(ref FileSystemEntry e)
    {
        var attributes = e.Attributes;
        if (e.IsDirectory)
        {
            bool marker = Classifier.IsProjectMarkerDir(e.FileName);
            return new Entry(EntryKind.Directory, e.FileName.ToString(), marker ? 1 : 0, attributes, default, 0);
        }

        bool cloudOnly = (attributes & CloudOnlyMask) != 0;
        if (ImageFormats.TryGet(e.FileName, out var format))
            return new Entry(EntryKind.Image, e.FileName.ToString(), e.Length, attributes, format, e.LastWriteTimeUtc.UtcTicks);

        // Cloud placeholders report their full size but use no local space.
        long length = cloudOnly ? 0 : e.Length;
        return Classifier.IsProjectMarkerFile(e.FileName)
            ? new Entry(EntryKind.Marker, null, length, attributes, default, 0)
            : new Entry(EntryKind.Other, null, length, attributes, default, 0);
    }

    private void ListDirectory(FsNode dir, string path, int depth)
    {
        CurrentPath = path;
        var dirNames = new List<string>();
        var images = new List<Entry>();
        bool projectMarker = false;
        long bytes = 0;

        try
        {
            foreach (var e in new FileSystemEnumerable<Entry>(path, Transform, Options))
            {
                switch (e.Kind)
                {
                    case EntryKind.Directory:
                        if (e.Length == 1)
                            projectMarker = true;
                        // Junctions and symlinks point at folders that are scanned elsewhere (or on another drive).
                        if ((e.Attributes & FileAttributes.ReparsePoint) == 0)
                            dirNames.Add(e.Name!);
                        break;
                    case EntryKind.Image:
                        images.Add(e);
                        if ((e.Attributes & CloudOnlyMask) == 0)
                            bytes += e.Length;
                        break;
                    case EntryKind.Marker:
                        projectMarker = true;
                        bytes += e.Length;
                        break;
                    default:
                        bytes += e.Length;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            dir.AccessDenied = true;
            Interlocked.Increment(ref _denied);
        }

        if (projectMarker && !dir.IsAsset && Classifier.ProjectMarkerApplies(dir, depth))
        {
            dir.IsAsset = true;
            dir.AssetReason = Classifier.ProjectReason;
        }

        var children = new List<FsNode>(images.Count + dirNames.Count);
        long imageBytes = 0;
        foreach (var e in images)
        {
            bool tiny = !dir.IsAsset && e.Length < Classifier.TinyImage;
            children.Add(new FsNode(e.Name!, NodeKind.File, dir)
            {
                Size = e.Length,
                FileCount = 1,
                AssetSize = dir.IsAsset || tiny ? e.Length : 0,
                AssetCount = dir.IsAsset || tiny ? 1 : 0,
                IsAsset = dir.IsAsset || tiny,
                AssetReason = tiny ? Classifier.TinyReason : null,
                IsCloudOnly = (e.Attributes & CloudOnlyMask) != 0,
                Format = e.Format,
                Library = Classifier.ForFile(e.Name, dir.Library),
                LastWrite = e.Modified,
            });
            imageBytes += e.Length;
        }

        var subdirs = new List<FsNode>(dirNames.Count);
        foreach (var name in dirNames)
        {
            var sub = new FsNode(name, NodeKind.Directory, dir) { Library = Classifier.ForDirectory(name, dir.Library) };
            if (dir.IsAsset)
            {
                // Received photos live under AppData for some messaging apps; they're the user's.
                if (dir.EffectiveAssetReason == Classifier.AppDataReason && Classifier.IsUserMediaInsideAppData(name))
                {
                    sub.Library = Library.Messaging;
                    sub.IsLibraryRoot = true;
                    _libraryRoots.Add(sub);
                }
                else
                {
                    sub.IsAsset = true;
                }
            }
            else if (Classifier.AssetReason(name, depth + 1) is { } reason)
            {
                sub.IsAsset = true;
                sub.AssetReason = reason;
            }
            else if (sub.Library != dir.Library)
            {
                sub.IsLibraryRoot = true;
                _libraryRoots.Add(sub);
            }
            children.Add(sub);
            subdirs.Add(sub);
        }

        dir.Children = children;

        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _images, images.Count);
        Interlocked.Add(ref _imageBytes, imageBytes);
        Interlocked.Add(ref _dirs, subdirs.Count);

        foreach (var sub in subdirs)
            Enqueue(sub, Path.Join(path, sub.Name), depth + 1);
    }

    /// <summary>
    /// Iterative post-order pass: sums sizes and counts, drops folders without images,
    /// and sorts children largest first.
    /// </summary>
    private static Totals RollUp(FsNode root)
    {
        var stack = new Stack<(FsNode Node, bool ChildrenDone)>();
        stack.Push((root, false));
        Comparison<FsNode> bySizeDesc = (a, b) => b.Size.CompareTo(a.Size);

        while (stack.Count > 0)
        {
            var (node, childrenDone) = stack.Pop();
            var children = node.Children;
            if (children == null)
                continue;

            if (!childrenDone)
            {
                stack.Push((node, true));
                foreach (var c in children)
                    if (c.Kind == NodeKind.Directory)
                        stack.Push((c, false));
                continue;
            }

            long size = 0, assetSize = 0, files = 0, assets = 0, dirs = 0, denied = node.AccessDenied ? 1 : 0, newest = 0;
            foreach (var c in children)
            {
                if (c.Kind == NodeKind.Directory)
                    denied += c.DeniedCount;
                if (c.FileCount == 0)
                    continue;
                size += c.Size;
                assetSize += c.AssetSize;
                files += c.FileCount;
                assets += c.AssetCount;
                newest = Math.Max(newest, c.LastWrite);
                if (c.Kind == NodeKind.Directory)
                    dirs += 1 + c.DirCount;
            }
            children.RemoveAll(c => c.FileCount == 0);
            children.Sort(bySizeDesc);
            children.TrimExcess();
            if (node == root)
                return new Totals(size, assetSize, files, assets, dirs, denied, newest);

            node.Size = size;
            node.AssetSize = assetSize;
            node.FileCount = files;
            node.AssetCount = assets;
            node.DirCount = dirs;
            node.DeniedCount = denied;
            node.LastWrite = newest;
        }
        return default;
    }

    private readonly record struct Totals(long Size, long AssetSize, long Files, long Assets, long Dirs, long Denied, long Newest);
    private Totals _totals;

    /// <summary>Called on the UI thread after <see cref="ScanAsync"/> completes.</summary>
    public void Finish()
    {
        Drive.Children ??= [];
        Drive.Size = _totals.Size;
        Drive.AssetSize = _totals.AssetSize;
        Drive.FileCount = _totals.Files;
        Drive.AssetCount = _totals.Assets;
        Drive.DirCount = _totals.Dirs;
        Drive.DeniedCount = _totals.Denied;
        Drive.LastWrite = _totals.Newest;
        Drive.IsScanning = false;
        Drive.ScanProgress = 1;
    }
}
