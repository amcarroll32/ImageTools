using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageTools.Model;
using ImageTools.Platform;
using ImageTools.Scanning;
using ImageTools.Treemap;

namespace ImageTools;

public partial class MainWindow : Window
{
    private const int MaxListRows = 1000;
    private const int MaxCards = 200;
    private const int MaxCardLocations = 6;

    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _thumbTimer;
    private readonly DispatcherTimer _deviceTimer;

    /// <summary>Drives being listed again by Scan for new; the map keeps showing the previous scan meanwhile.</summary>
    private readonly List<DriveScanner> _refreshing = [];
    private readonly bool _ready;
    private readonly Task<HashCache> _cacheTask = Task.Run(HashCache.Load);
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _analysisCts;
    private Task _analysisTask = Task.CompletedTask;
    private List<DriveScanner> _scanners = [];
    private FsNode? _pc;
    private FsNode? _current;
    private SearchMatcher? _matcher;
    private ColorMode _colorMode = ColorMode.Duplicates;
    private Stopwatch _elapsed = new();
    private TimeSpan _lastScanFinished;
    private readonly List<string> _probing = [];
    private Dictionary<char, PhysicalDisk>? _disks;
    private DiskQuery? _diskQuery;
    private List<DriveEntry> _entries = [];
    private int _activeBatches;
    private Analyzer? _analyzer;
    private DupResult? _dups;
    private List<CardStatus> _cardStatus = [];
    private FsNode? _tipNode;
    private Point _tipPoint;
    private int _tick;

    /// <summary>A drive letter found on this PC, whether or not it's being scanned.</summary>
    private sealed class DriveEntry(DriveInfo info, DriveSource source, string? networkPath)
    {
        public DriveInfo Info { get; } = info;
        public string Name => Info.Name;
        public string Key => Info.Name[..2].ToUpperInvariant(); // "C:"
        public DriveSource Source { get; set; } = source;
        public string? NetworkPath { get; } = networkPath;

        /// <summary>Included and handed to a scan (which may still be probing, scanning, or have found it not ready).</summary>
        public bool Active { get; set; }
        public bool NotReady { get; set; }
        public FsNode? Node { get; set; }
        public CancellationTokenSource? Cts { get; set; }

        public string SourceLabel => Source switch
        {
            DriveSource.Network => "network",
            DriveSource.Virtual => "cloud / virtual",
            _ => "local",
        };
    }

    public MainWindow()
    {
        InitializeComponent();
        ApplyThemeResources();

        if (Elevation.IsElevated)
        {
            bool backup = Elevation.TryEnableBackupPrivilege();
            Title += " (Administrator)";
            ElevateButton.Visibility = Visibility.Collapsed;
            AdminBadge.Visibility = Visibility.Visible;
            AdminBadge.ToolTip = backup
                ? "Running as administrator with the backup privilege on, so protected folders are listed too. Still read-only."
                : "Running as administrator, but the backup privilege couldn't be enabled, so some system folders may still be unreadable.";
        }

        Map.HoverChanged += Map_HoverChanged;
        Map.NodeClicked += Map_NodeClicked;
        Map.NodeRightClicked += Map_NodeRightClicked;
        Map.MouseMove += Map_MouseMove;
        Tip.SizeChanged += (_, _) => PositionTip(_tipPoint);
        Map.ColorMode = _colorMode;

        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, ProgressTick, Dispatcher);
        _progressTimer.Stop();
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Input, (_, _) => ApplySearch(), Dispatcher);
        _searchTimer.Stop();
        _thumbTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Input, (_, _) => LoadTipThumbnail(), Dispatcher);
        _thumbTimer.Stop();
        // Plugging in a card fires several notifications; act once they've settled.
        _deviceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) => DeviceSettled(), Dispatcher);
        _deviceTimer.Stop();

        Loaded += (_, _) => StartScan();
        Closed += Window_Closed;
        _ready = true;
    }

    private bool ShowAssets => AssetsCheck.IsChecked == true;

    private void Window_Closed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        // Keep whatever was hashed, unless a check is still writing to the cache.
        if (_analysisTask.IsCompleted && _cacheTask.IsCompletedSuccessfully && _scanners.Count > 0)
            _cacheTask.Result.Save(_scanners.Select(s => s.Drive.Name).ToList());
    }

    // ---- Scanning ----

    private async void StartScan()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        var pc = new FsNode("This PC", NodeKind.Root, null) { Children = [] };
        _pc = pc;
        _scanners = [];
        _refreshing.Clear();
        _probing.Clear();
        foreach (var old in _entries)
            old.Cts?.Cancel();
        _entries = [];
        _activeBatches = 0;
        _elapsed = Stopwatch.StartNew();
        _lastScanFinished = TimeSpan.Zero;
        _analysisCts?.Cancel();
        _analyzer = null;
        _dups = null;
        _cardStatus = [];
        _disks = null;
        _diskQuery = null;
        NavigateTo(pc);
        _progressTimer.Start();
        UpdateStatus();

        // Listing drives and asking Windows which letters sit on real disk partitions is quick,
        // and it lets network and cloud drives be skipped without ever touching them (an offline
        // one can take 20+ seconds just to say it isn't ready). Off the UI thread regardless.
        var (entries, query) = await Task.Run(() =>
        {
            var q = DiskInfoProvider.Query();
            var list = DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                .Select(d => new DriveEntry(d, ClassifySource(d, q),
                    d.DriveType == DriveType.Network ? NetworkDrives.RemotePath(d.Name) : null))
                .ToList();
            return (list, q);
        });
        if (cts.IsCancellationRequested)
            return;
        _diskQuery = query;
        _disks = query.ByLetter;
        _entries = entries;
        RefreshSidePanel();

        await ScanEntries(entries.Where(IsIncluded).ToList(), pc, cts.Token);
    }

    /// <summary>
    /// Network drives say so. Cloud-sync drives and subst aliases claim to be local fixed disks,
    /// but no disk partition backs them. (Removable drives are always local; an empty card reader
    /// has no partition either.)
    /// </summary>
    private static DriveSource ClassifySource(DriveInfo drive, DiskQuery query)
    {
        if (drive.DriveType == DriveType.Network)
            return DriveSource.Network;
        if (drive.DriveType == DriveType.Fixed && query.Succeeded && !query.PartitionLetters.Contains(char.ToUpperInvariant(drive.Name[0])))
            return DriveSource.Virtual;
        return DriveSource.Local;
    }

    /// <summary>File systems a real local disk would have; anything else on a "fixed" drive is virtual.</summary>
    private static bool IsLocalFileSystem(string? format) =>
        format is not null && (format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || format.Equals("ReFS", StringComparison.OrdinalIgnoreCase)
            || format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || format.Equals("exFAT", StringComparison.OrdinalIgnoreCase)
            || format.Equals("UDF", StringComparison.OrdinalIgnoreCase));

    private static bool DefaultIncluded(DriveEntry entry) =>
        entry.Source == DriveSource.Local || App.Settings.IncludeNetworkDrives;

    private static bool IsIncluded(DriveEntry entry) =>
        App.Settings.DriveChoices.TryGetValue(entry.Key, out bool choice) ? choice : DefaultIncluded(entry);

    /// <summary>Remembers a per-drive choice only when it differs from the default.</summary>
    private static void SetIncluded(DriveEntry entry, bool include)
    {
        if (include == DefaultIncluded(entry))
            App.Settings.DriveChoices.Remove(entry.Key);
        else
            App.Settings.DriveChoices[entry.Key] = include;
    }

    /// <summary>Probes and scans a set of drives; can run again later when drives are added from the Drives menu.</summary>
    private async Task ScanEntries(List<DriveEntry> entries, FsNode pc, CancellationToken ct)
    {
        if (entries.Count > 0)
        {
            if (_activeBatches == 0)
            {
                _elapsed = Stopwatch.StartNew();
                _progressTimer.Start();
            }
            _activeBatches++;
            foreach (var entry in entries)
            {
                entry.Active = true;
                entry.NotReady = false;
                entry.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _probing.Add(entry.Name);
            }
            UpdateStatus();

            await Task.WhenAll(entries.Select(e => ProbeAndScan(e, pc, e.Cts!.Token)));

            if (ct.IsCancellationRequested || pc != _pc)
                return;
            _activeBatches--;
        }

        // A Scan for new still re-listing drives starts the check itself once it's done.
        if (_activeBatches == 0 && _refreshing.Count == 0)
        {
            _elapsed.Stop();
            StartAnalysis();
        }
        UpdateStatus();
    }

    private sealed record DriveProbe(string Name, string Label, long TotalSize, long FreeSize, bool IsRemovable, bool HasDcim, string? Format, uint Serial);

    /// <summary>Asks Windows about a drive; slow for unready drives, so call it off the UI thread. Null when not ready.</summary>
    private static DriveProbe? Probe(DriveInfo drive)
    {
        try
        {
            return drive.IsReady
                ? new DriveProbe(drive.Name, drive.VolumeLabel, drive.TotalSize, drive.TotalFreeSpace, drive.DriveType == DriveType.Removable,
                    drive.DriveType == DriveType.Removable && Directory.Exists(Path.Join(drive.Name, "DCIM")), drive.DriveFormat,
                    drive.DriveType == DriveType.Removable ? VolumeInfo.Serial(drive.Name) : 0)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // vanished or locked (e.g. BitLocker)
        }
    }

    private FsNode NewDriveNode(DriveProbe probe, DriveEntry entry, FsNode pc)
    {
        bool local = entry.Source == DriveSource.Local;
        var node = new FsNode(probe.Name, NodeKind.Drive, pc)
        {
            Label = probe.Label,
            PendingSize = local ? probe.TotalSize - probe.FreeSize : 0,
            Capacity = local ? probe.TotalSize : 0,
            IsRemovable = probe.IsRemovable,
            HasDcimFolder = probe.HasDcim,
            VolumeSerial = probe.Serial,
            FreeSize = local ? probe.FreeSize : 0,
            IsScanning = true,
            Source = entry.Source,
        };
        ApplyHardware(node, entry);
        return node;
    }

    /// <summary>
    /// Checks a drive on a background thread (an unready drive can take 20+ seconds to say so),
    /// then adds it to the overview and scans it. Drives appear as soon as they respond.
    /// </summary>
    private async Task ProbeAndScan(DriveEntry entry, FsNode pc, CancellationToken ct)
    {
        var drive = entry.Info;
        var probe = await Task.Run(() => Probe(drive));

        if (pc != _pc)
            return;
        _probing.Remove(drive.Name);
        if (ct.IsCancellationRequested)
        {
            UpdateStatus(); // unticked in the Drives menu while probing
            return;
        }
        if (probe == null)
        {
            entry.NotReady = true;
            UpdateStatus();
            return;
        }

        // Fallback when Windows couldn't list partitions: a "fixed" drive with an unusual file
        // system is a cloud or virtual drive.
        if (_diskQuery?.Succeeded != true && entry.Source == DriveSource.Local && drive.DriveType == DriveType.Fixed
            && !IsLocalFileSystem(probe.Format))
        {
            entry.Source = DriveSource.Virtual;
            if (!IsIncluded(entry))
            {
                entry.Active = false;
                RefreshSidePanel();
                UpdateStatus();
                return;
            }
        }

        var node = NewDriveNode(probe, entry, pc);
        entry.Node = node;
        pc.Children!.Add(node);
        var scanner = new DriveScanner(node, probe.TotalSize, probe.FreeSize);
        _scanners.Add(scanner);
        RecomputeRoot(pc);
        Map.Invalidate();
        RefreshSidePanel();
        UpdateStatus();

        await RunScanner(scanner, pc, ct);
    }

    private async Task RunScanner(DriveScanner scanner, FsNode pc, CancellationToken ct)
    {
        try
        {
            await scanner.ScanAsync(ct);
            scanner.Finish();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            scanner.Drive.ScanError = ex.Message;
        }

        if (ct.IsCancellationRequested || pc != _pc)
            return;

        _lastScanFinished = _elapsed.Elapsed;
        RecomputeRoot(pc);
        Map.Invalidate();
        RefreshSidePanel();
        RefreshSearch();
        UpdateStatus();
    }

    // ---- Scan for new ----

    /// <summary>
    /// Picks up drives that appeared or vanished (a card plugged in or pulled out), then lists every
    /// scanned drive again in the background and swaps each in when it's done, so the map stays usable.
    /// The duplicate check that follows only reads new or changed images; the rest comes from the cache.
    /// </summary>
    private async void ScanForNew()
    {
        var pc = _pc;
        if (pc == null || _cts == null)
            return;
        if (_refreshing.Count > 0)
            return; // already looking
        if (_scanners.Count == 0 && _activeBatches == 0)
        {
            StartScan();
            return;
        }
        if (_activeBatches > 0)
        {
            StatusText.Text = "Still scanning – Scan for new will be ready once it finishes.";
            return;
        }

        var ct = _cts.Token;
        _elapsed = Stopwatch.StartNew();
        _progressTimer.Start();
        await RefreshDriveList();
        if (ct.IsCancellationRequested || pc != _pc)
            return;

        var jobs = _scanners
            .Where(s => !s.Drive.IsScanning && s.Drive.ScanError == null)
            .ToList()
            .Select(s => RescanInPlace(s, pc, ct))
            .ToList();
        UpdateStatus();
        await Task.WhenAll(jobs);
        if (ct.IsCancellationRequested || pc != _pc)
            return;

        _lastScanFinished = _elapsed.Elapsed;
        if (_activeBatches == 0)
        {
            _elapsed.Stop();
            StartAnalysis();
        }
        UpdateStatus();
    }

    /// <summary>Lists a drive again without touching what's on screen, then swaps the new tree in.</summary>
    private async Task RescanInPlace(DriveScanner old, FsNode pc, CancellationToken ct)
    {
        var oldNode = old.Drive;
        var entry = _entries.FirstOrDefault(e => e.Node == oldNode);
        if (entry == null)
            return;
        var probe = await Task.Run(() => Probe(entry.Info));
        if (ct.IsCancellationRequested || pc != _pc || entry.Node != oldNode)
            return;
        if (probe == null)
        {
            RemoveDrive(entry, pc); // the card was pulled out
            return;
        }

        var node = NewDriveNode(probe, entry, pc);
        var scanner = new DriveScanner(node, probe.TotalSize, probe.FreeSize);
        _refreshing.Add(scanner);
        try
        {
            await scanner.ScanAsync(entry.Cts?.Token ?? ct);
            scanner.Finish();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't scan {oldNode.DisplayName} again: {ex.Message}. Showing the previous scan.";
            return;
        }
        finally
        {
            _refreshing.Remove(scanner);
        }
        if (ct.IsCancellationRequested || pc != _pc || entry.Node != oldNode)
            return;

        int i = pc.Children!.IndexOf(oldNode);
        int s = _scanners.IndexOf(old);
        if (i < 0 || s < 0)
            return;
        CarryOver(oldNode, node);
        pc.Children[i] = node;
        _scanners[s] = scanner;
        entry.Node = node;
        RecomputeRoot(pc);

        // Stay where you were: the same folder in the new tree, or the nearest one that still exists.
        if (_current != null && (_current == oldNode || oldNode.IsAncestorOf(_current)))
        {
            FsNode? same = null;
            for (var n = _current; n != null && same == null; n = n.Parent)
                same = n == oldNode ? node : CardSync.FindByPath([node], n.FullPath);
            NavigateTo(same ?? node);
        }
        else
        {
            Map.Invalidate();
            RefreshSidePanel();
        }
    }

    /// <summary>
    /// Copies what's already known (duplicate status, copies, photo details, folder totals) from the
    /// previous scan of a drive to the new one, for every image whose size and date haven't changed,
    /// so the map keeps its colors until the duplicate check has looked again.
    /// </summary>
    private static void CarryOver(FsNode from, FsNode to)
    {
        to.ImportBadge = from.ImportBadge;
        to.ImportBadgeGood = from.ImportBadgeGood;
        var stack = new Stack<(FsNode From, FsNode To)>();
        stack.Push((from, to));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            b.DupCount = a.DupCount;
            b.DupBytes = a.DupBytes;
            if (a.Children == null || b.Children == null)
                continue;
            var old = new Dictionary<string, FsNode>(a.Children.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var c in a.Children)
                old.TryAdd(c.Name, c);
            foreach (var c in b.Children)
            {
                if (!old.TryGetValue(c.Name, out var was) || was.Kind != c.Kind)
                    continue;
                if (c.Kind == NodeKind.Directory)
                    stack.Push((was, c));
                else if (was.Size == c.Size && was.LastWrite == c.LastWrite)
                    (c.Dup, c.Group, c.Photo) = (was.Dup, was.Group, was.Photo);
            }
        }
    }

    /// <summary>
    /// Compares the drive letters with what's listed: new letters (and card readers that now have a card)
    /// are scanned if they're included, and drives that are gone are removed.
    /// </summary>
    private async Task RefreshDriveList()
    {
        var pc = _pc;
        if (pc == null || _cts == null || _diskQuery == null)
            return; // the first look at the drives hasn't finished
        var ct = _cts.Token;

        // Readiness is only asked of removable drives: it's instant for card readers, slow for network shares.
        var (infos, ready, query) = await Task.Run(() =>
        {
            var q = DiskInfoProvider.Query();
            var list = DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                .ToList();
            var r = list.Where(d => d.DriveType == DriveType.Removable)
                .ToDictionary(d => d.Name, d => { try { return d.IsReady; } catch (IOException) { return false; } }, StringComparer.OrdinalIgnoreCase);
            return (list, r, q);
        });
        if (ct.IsCancellationRequested || pc != _pc)
            return;
        _diskQuery = query;
        _disks = query.ByLetter;

        bool removed = false;
        var letters = infos.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries.ToList())
        {
            bool gone = !letters.Contains(entry.Name) || (ready.TryGetValue(entry.Name, out bool isReady) && !isReady);
            if (gone && entry.Node != null)
            {
                RemoveDrive(entry, pc);
                removed = true;
            }
            if (!letters.Contains(entry.Name))
                _entries.Remove(entry);
        }

        var added = infos
            .Where(d => !_entries.Any(e => e.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(d => new DriveEntry(d, ClassifySource(d, query), d.DriveType == DriveType.Network ? NetworkDrives.RemotePath(d.Name) : null))
            .ToList();
        _entries.AddRange(added);

        // Removable drives that are ready but not on the map (a card just inserted), and new letters.
        // Other drives that weren't ready aren't retried here: an offline network share can take 20+ seconds to answer.
        var toScan = _entries
            .Where(e => IsIncluded(e) && e.Node == null && !_probing.Contains(e.Name)
                        && (ready.TryGetValue(e.Name, out bool isReady) ? isReady : added.Contains(e)))
            .ToList();

        RefreshSidePanel();
        UpdateStatus();
        if (toScan.Count > 0)
            _ = ScanEntries(toScan, pc, ct);
        else if (removed && _activeBatches == 0 && _refreshing.Count == 0)
            StartAnalysis();
    }

    /// <summary>Takes a drive off the map, e.g. a card that was pulled out; it's listed again if it comes back.</summary>
    private void RemoveDrive(DriveEntry entry, FsNode pc)
    {
        entry.Cts?.Cancel();
        entry.Active = false;
        entry.NotReady = true;
        if (entry.Node is not { } node)
            return;
        pc.Children!.Remove(node);
        _scanners.RemoveAll(s => s.Drive == node);
        _cardStatus.RemoveAll(c => c.Card == node);
        entry.Node = null;
        RecomputeRoot(pc);
        if (_current != null && (_current == node || node.IsAncestorOf(_current)))
            NavigateTo(pc);
        else
        {
            Map.Invalidate();
            RefreshSidePanel();
        }
    }

    // ---- Full rescan ----

    /// <summary>Forgets every hash and photo detail and scans from scratch, after saying how much will be read.</summary>
    private async void FullRescan()
    {
        var cache = await _cacheTask;
        var (images, bytes) = _pc != null ? Analyzer.EstimateFullCheck(ViewRoots(_pc), cache) : (0, 0);
        string slow = bytes > 0
            ? $"Slow: about {Format.Bytes(bytes)} is read again, plus the details of {Format.Count(images, "image", "images")}. " +
              "On memory cards and hard drives this can take many minutes."
            : "Slow: every image is read again. On memory cards and hard drives this can take many minutes.";
        var confirm = new ConfirmWindow(
            "Full rescan",
            "Full rescan?",
            "Lists every folder again, forgets every saved hash and photo detail, and rebuilds the duplicate check from scratch. " +
            "For everyday changes, like a card you've just plugged in, use Scan for new: it only reads new or changed images.",
            slow,
            "Full rescan")
        { Owner = this };
        if (confirm.ShowDialog() != true)
            return;

        _analysisCts?.Cancel();
        try
        {
            await _analysisTask; // it writes to the cache, so let it stop first
        }
        catch (Exception)
        {
            // Cancelled or failed; either way it's finished.
        }
        cache.Clear();
        StartScan();
    }

    private void ScanMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = RescanButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var forNew = MenuItem("Scan for new", "", ScanForNew);
        forNew.InputGestureText = "F5";
        forNew.ToolTip = "Look for new and changed images. Only those are read; everything else comes from the cache.";
        menu.Items.Add(forNew);

        var full = MenuItem("Full rescan…", "", FullRescan);
        full.InputGestureText = "Ctrl+F5";
        full.ToolTip = "Slow: forgets every saved hash and photo detail and reads every image again. Asks first.";
        ((TextBlock)full.Icon).Foreground = Theme.StatusWarning;
        menu.Items.Add(full);
        menu.IsOpen = true;
    }

    private void ScanForNew_Click(object sender, RoutedEventArgs e) => ScanForNew();

    /// <summary>Windows says a device or card came or went: look at the drive letters again shortly.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;
        if (msg == WM_DEVICECHANGE && (wParam == DBT_DEVICEARRIVAL || wParam == DBT_DEVICEREMOVECOMPLETE))
        {
            _deviceTimer.Stop();
            _deviceTimer.Start();
        }
        return IntPtr.Zero;
    }

    private void DeviceSettled()
    {
        _deviceTimer.Stop();
        if (_refreshing.Count == 0 && _scanners.Count > 0)
            _ = RefreshDriveList();
    }

    private void ApplyHardware(FsNode drive, DriveEntry entry)
    {
        char letter = char.ToUpperInvariant(drive.Name[0]);
        PhysicalDisk? disk = null;
        _disks?.TryGetValue(letter, out disk);
        drive.Hardware = DriveHardware.Classify(letter, entry.Source, entry.NetworkPath, disk, drive.IsRemovable, drive.HasDcimFolder);
    }

    private static void RecomputeRoot(FsNode pc)
    {
        var drives = pc.Children!;
        drives.Sort((a, b) => b.Size.CompareTo(a.Size));
        pc.Size = drives.Sum(d => d.Size);
        pc.AssetSize = drives.Sum(d => d.AssetSize);
        pc.FileCount = drives.Sum(d => d.FileCount);
        pc.AssetCount = drives.Sum(d => d.AssetCount);
        pc.Capacity = drives.Sum(d => d.Capacity);
        pc.FreeSize = drives.Sum(d => d.FreeSize);
        pc.DirCount = drives.Sum(d => d.DirCount);
        pc.DeniedCount = drives.Sum(d => d.DeniedCount);
        pc.LastWrite = drives.Count > 0 ? drives.Max(d => d.LastWrite) : 0;
    }

    /// <summary>Finished drives under the view (scanning drives have no tree yet).</summary>
    private static List<FsNode> ViewRoots(FsNode view) =>
        view.Kind == NodeKind.Root
            ? view.Children!.Where(d => !d.IsScanning && d.ScanError == null).ToList()
            : [view];

    private void ProgressTick(object? sender, EventArgs e)
    {
        bool anyScanning = false;
        foreach (var s in _scanners)
        {
            if (!s.Drive.IsScanning || s.Drive.ScanError != null)
                continue;
            anyScanning = true;
            if (!s.Drive.IsContentOnlyDrive)
                s.Drive.ScanProgress = s.UsedSize > 0 ? Math.Min(0.99, (double)s.ScannedBytes / s.UsedSize) : 0;
        }

        // Only the overview shows live progress tiles; duplicate colors fill in as the check runs.
        bool analyzing = _analyzer is { Current: not Analyzer.Stage.Done };
        if (anyScanning && _current == _pc)
            Map.Invalidate();
        if (analyzing && _colorMode == ColorMode.Duplicates && ++_tick % 4 == 0)
        {
            Map.Invalidate();
            RefreshLegend();
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        long images = _scanners.Sum(s => s.ImagesFound);
        long denied = _scanners.Sum(s => s.DeniedDirs);
        var active = _scanners.Where(s => s.Drive.IsScanning && s.Drive.ScanError == null).ToList();
        string waiting = _probing.Count > 0 ? $"  ·  waiting for {string.Join(", ", _probing)} to respond" : "";

        var skipped = _entries.Where(e => !IsIncluded(e)).ToList();
        string skippedText = skipped.Count > 0
            ? $"  ·  skipped: {string.Join(", ", skipped.Select(e => $"{e.Key} ({e.SourceLabel})"))}"
            : "";

        if (_scanners.Count == 0)
        {
            StatusText.Text = _probing.Count > 0 || _activeBatches > 0 || (_elapsed.IsRunning && _entries.Count == 0)
                ? "Looking for drives…" + waiting
                : _entries.Count > 0 && skipped.Count == _entries.Count
                    ? "No drives selected. Use Drives to choose what to scan." + skippedText
                    : "No ready drives found." + skippedText;
            return;
        }
        if (_refreshing.Count > 0 && active.Count == 0)
        {
            var first = _refreshing[0];
            StatusText.Text = $"Looking for new images on {Format.Count(_refreshing.Count, "drive", "drives")}…  " +
                              $"{Format.Count(_refreshing.Sum(s => s.ImagesFound))} images listed  ·  {Format.Duration(_elapsed.Elapsed)}  ·  " +
                              (first.CurrentPath ?? first.Drive.Name);
            return;
        }
        if (active.Count > 0)
        {
            string where = active[0].CurrentPath ?? active[0].Drive.Name;
            StatusText.Text = $"Scanning {active.Count} of {_scanners.Count} drives…  {Format.Count(images)} images found  ·  " +
                              $"{Format.Duration(_elapsed.Elapsed)}{waiting}  ·  {where}";
            return;
        }

        var pc = _pc!;
        string text = $"Scanned {Format.Count(_scanners.Count, "drive", "drives")} in {Format.Duration(_lastScanFinished)}  ·  " +
                      Format.Count(pc.VisibleCount(ShowAssets), "image", "images");
        if (_analyzer is { HadCache: true } checkedRun)
            text += $"  ·  {Format.Count(checkedRun.NewFiles, "new or changed image", "new or changed images")} since the last check";
        if (_analyzer is { Current: not Analyzer.Stage.Done } a)
            text += "  ·  " + AnalysisProgress(a);
        else if (_dups != null)
            text += $"  ·  {Format.Count(_dups.DuplicateFiles)} have copies ({Format.Bytes(_dups.ExtraBytes)} in extra copies)";
        if (!ShowAssets && pc.AssetCount > 0)
            text += $"  ·  {Format.Count(pc.AssetCount, "app or game image", "app and game images")} hidden";
        text += skippedText;
        if (denied > 0)
            text += Elevation.IsElevated
                ? $"  ·  {Format.Count(denied, "folder", "folders")} still couldn't be read"
                : $"  ·  {Format.Count(denied, "folder", "folders")} couldn't be read – Run as admin to include them";
        var failed = _scanners.Where(s => s.Drive.ScanError != null).ToList();
        if (failed.Count > 0)
            text += $"  ·  failed: {string.Join(", ", failed.Select(f => f.Drive.Name))}";
        var notReady = _entries.Where(e => e.Active && e.NotReady).Select(e => e.Name).ToList();
        if (notReady.Count > 0)
            text += $"  ·  not ready: {string.Join(", ", notReady)}";
        StatusText.Text = text + waiting;
    }

    private static string AnalysisProgress(Analyzer a) => a.Current switch
    {
        Analyzer.Stage.Heads => $"Checking {Format.Count(a.Candidates)} possible duplicates…  {Format.Count(a.Done)} of {Format.Count(a.Total)} read",
        Analyzer.Stage.Full => $"Comparing matching files in full…  {Format.Bytes(a.Done)} of {Format.Bytes(a.Total)}",
        _ => $"Reading photo details…  {Format.Count(a.Done)} of {Format.Count(a.Total)}",
    };

    // ---- Duplicate check and photo details ----

    /// <summary>
    /// Finds duplicates across every finished drive, then reads photo details. Runs again (quickly,
    /// thanks to the cache) whenever the set of drives changes; a previous run is stopped first.
    /// </summary>
    private async void StartAnalysis()
    {
        var pc = _pc;
        if (pc == null || _cts == null)
            return;

        _analysisCts?.Cancel();
        var cts = _analysisCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            await _analysisTask; // the previous run touches the same nodes; let it stop first
        }
        catch (Exception)
        {
            // It was cancelled or failed; either way it's finished.
        }
        if (cts.IsCancellationRequested || pc != _pc)
            return;

        var drives = ViewRoots(pc);
        if (drives.Count == 0)
        {
            _analyzer = null;
            _dups = null;
            RefreshCards();
            return;
        }

        var cache = await _cacheTask;
        var analyzer = _analyzer = new Analyzer(drives, cache);
        _dups = null;
        _progressTimer.Start();
        RefreshCards();

        var run = Task.Run(() => analyzer.FindDuplicates(cts.Token), cts.Token);
        _analysisTask = run;
        try
        {
            var dups = await run;
            if (cts.IsCancellationRequested || _analyzer != analyzer)
                return;
            _dups = dups;
            UpdateCardSync();
            Map.Invalidate();
            RefreshSidePanel();
            RefreshCards();
            UpdateStatus();

            var roots = analyzer.Roots;
            var details = Task.Run(() =>
            {
                cache.Save(roots);
                analyzer.ReadDetails(cts.Token);
                cache.Save(roots);
            }, cts.Token);
            _analysisTask = details;
            await details;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (_analyzer == analyzer)
            {
                _analyzer = null;
                StatusText.Text = $"Couldn't finish checking for duplicates: {ex.Message}";
            }
            return;
        }
        if (cts.IsCancellationRequested || _analyzer != analyzer)
            return;

        if (_activeBatches == 0)
            _progressTimer.Stop();
        UpdateCardSync(); // dates taken are known now, so "new since the last import" is more exact
        Map.Invalidate();
        RefreshSidePanel();
        UpdateStatus();
    }

    // ---- Camera cards ----

    private sealed record ImportBanner(
        CardStatus Status,
        string Title,
        string SizeText,
        string Glyph,
        string BadgeText,
        Brush BadgeBrush,
        string Advice,
        Visibility ReviewVisibility);

    /// <summary>Pairs each memory card's folders with their archive folders and badges the card.</summary>
    private void UpdateCardSync()
    {
        var pc = _pc;
        if (pc == null || _dups == null)
        {
            _cardStatus = [];
            return;
        }
        foreach (var d in pc.Children!)
            d.ImportBadge = null;

        _cardStatus = CardSync.Find(ViewRoots(pc), App.Settings.CardArchives, out bool remembered);
        if (remembered)
            App.Settings.Save();

        foreach (var s in _cardStatus)
        {
            if (s.NewCount > 0)
                (s.Card.ImportBadge, s.Card.ImportBadgeGood) = ($"{Format.Count(s.NewCount)} new to import", false);
            else if (s.UpToDate)
                (s.Card.ImportBadge, s.Card.ImportBadgeGood) = ("Archived", true);
        }
    }

    private void ShowImportBanners(FsNode view)
    {
        ImportBanners.ItemsSource = _cardStatus
            .Where(s => view.Kind == NodeKind.Root || view.Drive == s.Card)
            .Select(MakeImportBanner)
            .ToList();
    }

    private static ImportBanner MakeImportBanner(CardStatus s)
    {
        string title = $"Camera card {s.Card.DisplayName}";
        var targets = s.Folders.Where(f => f.Count(ImportBucket.New) > 0).Select(f => Format.ShortPath(f.ArchivePath)).ToList();
        string where = targets.Count switch
        {
            0 => "",
            1 => targets[0],
            _ => $"{targets[0]} and {Format.Count(targets.Count - 1, "more folder", "more folders")}",
        };
        string others = s.OtherCount > 0
            ? $" {Format.Count(s.OtherCount, "older image or copy", "older images or copies")} found elsewhere aren't ticked."
            : "";
        string unmatched = s.Unmatched.Count > 0
            ? $" {Format.Count(s.UnmatchedImages, "image", "images")} in {string.Join(", ", s.Unmatched.Select(f => f.Name))} have no archive folder yet."
            : "";

        if (s.NewCount > 0)
        {
            long first = s.NewItems.Min(i => i.Image.DateTicks), last = s.NewItems.Max(i => i.Image.DateTicks);
            string taken = Format.Date(first) == Format.Date(last) ? Format.Date(first) : $"{Format.Date(first)} – {Format.Date(last)}";
            return new ImportBanner(s, title, Format.Bytes(s.NewBytes), "", Format.Count(s.NewCount, "new image", "new images"), Theme.AccentText,
                $"Taken {taken}. Not yet in {where}.{others}{unmatched}", Visibility.Visible);
        }
        if (s.OtherCount > 0)
            return new ImportBanner(s, title, "", "", "No new images", Theme.StatusGood,
                $"Nothing newer than the archive.{others} Review to sync the whole card.{unmatched}", Visibility.Visible);
        if (s.UpToDate)
            return new ImportBanner(s, title, "", "", "Archived", Theme.StatusGood,
                $"Every image on the card is already in {string.Join(", ", s.Folders.Select(f => Format.ShortPath(f.ArchivePath)).Distinct())}.",
                Visibility.Collapsed);
        return new ImportBanner(s, title, "", "", "No archive folder found", Theme.MutedText,
            $"{Format.Count(s.UnmatchedImages, "image", "images")} on the card aren't in any archive folder. Import them once by hand and " +
            "Image Tools will follow the card from then on.", Visibility.Collapsed);
    }

    private void ReviewImport_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is CardStatus status)
            new ImportWindow(status) { Owner = this }.ShowDialog();
    }

    // ---- Navigation ----

    private void NavigateTo(FsNode node)
    {
        _current = node;
        Map.Selected = null;
        Map.Root = node;
        UpButton.IsEnabled = node.Parent != null;
        Tip.Visibility = Visibility.Collapsed;
        BuildBreadcrumbs();
        RefreshSidePanel();
        RefreshCards();
        RefreshSearch();
    }

    private void GoUp()
    {
        if (_current?.Parent is { } parent)
            NavigateTo(parent);
    }

    private bool CanOpen(FsNode node) => node.IsContainer && !node.IsScanning && node.ScanError == null && !node.NotScanned;

    private void TryOpen(FsNode node)
    {
        if (node == _current)
            return;
        if (CanOpen(node))
            NavigateTo(node);
        else if (node.IsScanning)
            StatusText.Text = $"{node.DisplayName} is still being scanned – it will open once the scan finishes.";
    }

    /// <summary>Opens the node's folder and highlights the node in it.</summary>
    private void ShowOnMap(FsNode node)
    {
        if (node.IsAsset && !ShowAssets)
            AssetsCheck.IsChecked = true;
        var parent = node.Parent;
        if (parent == null)
        {
            NavigateTo(node);
            return;
        }
        if (parent != _current)
        {
            if (!CanOpen(parent))
                return;
            NavigateTo(parent);
        }
        Map.Selected = node;
    }

    private void BuildBreadcrumbs()
    {
        Breadcrumbs.Children.Clear();
        if (_current == null)
            return;

        var chain = _current.Ancestors().Reverse().Append(_current).ToList();
        for (int i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            if (i > 0)
            {
                var chevron = new TextBlock
                {
                    Text = "",
                    FontFamily = (FontFamily)FindResource("Icons"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0),
                };
                chevron.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
                Breadcrumbs.Children.Add(chevron);
            }

            var button = new Button
            {
                Content = node.Kind == NodeKind.Root ? "This PC" : node.DisplayName,
                Style = (Style)FindResource("CrumbButton"),
                FontWeight = node == _current ? FontWeights.SemiBold : FontWeights.Normal,
                Tag = node,
            };
            button.Click += (s, _) => NavigateTo((FsNode)((Button)s).Tag);
            Breadcrumbs.Children.Add(button);
        }
        CrumbScroller.ScrollToRightEnd();
    }

    // ---- Side panel: Contents, Libraries and the legend ----

    private sealed record RowItem(
        FsNode Node,
        string Name,
        string SizeText,
        string Detail,
        Brush SwatchFill,
        Brush SwatchStroke,
        Thickness SwatchThickness,
        FontWeight NameWeight,
        GridLength BarFilled,
        GridLength BarEmpty,
        Brush BarBrush,
        string Glyph = "",
        Visibility GlyphVisibility = Visibility.Collapsed,
        Visibility SwatchVisibility = Visibility.Visible,
        Brush? HealthBrush = null,
        Visibility HealthVisibility = Visibility.Collapsed,
        string HealthText = "",
        Visibility AlertVisibility = Visibility.Collapsed,
        string Detail2 = "",
        Visibility Detail2Visibility = Visibility.Collapsed)
    {
        public string AlertGlyph => Theme.AlertGlyph;
    }

    private sealed record LegendItem(string Name, Brush Swatch, string SizeText, string PercentText);

    private static GridLength Star(double v) => new(Math.Max(v, 0.0001), GridUnitType.Star);

    private RowItem MakeRow(FsNode node, long size, double fraction, string detail, long now)
    {
        bool folder = node.IsContainer;
        var swatch = Theme.Swatch(node, _colorMode, now);
        fraction = Math.Clamp(fraction, 0, 1);
        return new RowItem(
            node,
            node.DisplayName,
            Format.Bytes(size),
            detail,
            folder ? Brushes.Transparent : swatch,
            folder ? Theme.SecondaryText : Brushes.Transparent,
            new Thickness(folder ? 1.5 : 0),
            folder ? FontWeights.SemiBold : FontWeights.Normal,
            Star(fraction),
            Star(1 - fraction),
            folder ? Theme.Accent : swatch);
    }

    /// <summary>A drive row: its images, and a bar showing how full the disk is.</summary>
    private RowItem MakeDriveRow(FsNode drive)
    {
        bool contentOnly = drive.IsContentOnlyDrive;
        long used = Math.Max(0, drive.Capacity - drive.FreeSize);
        double full = !contentOnly && drive.Capacity > 0 ? (double)used / drive.Capacity : 0;
        string disk = contentOnly ? "" : $"  ·  {Format.Bytes(drive.FreeSize)} free of {Format.Bytes(drive.Capacity)}";
        string detail = drive.IsScanning
            ? (drive.ScanError != null ? "scan failed" : "scanning…") + disk
            : (drive.ImportBadge is { } badge ? badge + "  ·  " : "")
              + Format.Count(drive.VisibleCount(ShowAssets), "image", "images") + DupSuffix(drive) + disk;
        var hw = drive.Hardware;
        var health = hw?.Health ?? DiskHealth.Unknown;
        var healthBrush = Theme.HealthBrush(health);
        return new RowItem(
            drive,
            drive.DisplayName,
            drive.IsScanning ? "" : Format.Bytes(Map.DisplaySize(drive)),
            detail,
            Brushes.Transparent,
            Brushes.Transparent,
            new Thickness(0),
            FontWeights.SemiBold,
            Star(full),
            Star(1 - full),
            contentOnly ? Brushes.Transparent : full > 0.9 ? Theme.CapacityCritical : Theme.Accent,
            Theme.DriveGlyph(drive),
            Visibility.Visible,
            Visibility.Collapsed,
            healthBrush,
            healthBrush != null ? Visibility.Visible : Visibility.Collapsed,
            hw?.HealthLabel ?? "",
            health is DiskHealth.Warning or DiskHealth.Unhealthy ? Visibility.Visible : Visibility.Collapsed,
            hw == null ? "" : contentOnly ? hw.DiskLine : $"{hw.HealthLabel}  ·  {hw.DiskLine}",
            hw != null ? Visibility.Visible : Visibility.Collapsed);
    }

    /// <summary>A muted row for a drive that isn't scanned, saying why and how to include it.</summary>
    private static RowItem MakeSkippedRow(DriveEntry entry)
    {
        var stub = new FsNode(entry.Name, NodeKind.Drive, null) { NotScanned = true, Source = entry.Source };
        string why = entry.Source switch
        {
            DriveSource.Network => entry.NetworkPath != null ? $"Network drive ({entry.NetworkPath})" : "Network drive",
            DriveSource.Virtual => "Cloud or virtual drive (no local disk behind it)",
            _ => "Unticked in Drives",
        };
        string glyph = entry.Source switch
        {
            DriveSource.Network => "",
            DriveSource.Virtual => "",
            _ => "",
        };
        return new RowItem(
            stub,
            entry.Key,
            "not scanned",
            $"{why}  ·  skipped. Double-click or use Drives to include it.",
            Brushes.Transparent,
            Brushes.Transparent,
            new Thickness(0),
            FontWeights.Normal,
            Star(0),
            Star(1),
            Brushes.Transparent,
            glyph,
            Visibility.Visible,
            Visibility.Collapsed);
    }

    private void RefreshSidePanel()
    {
        var node = _current;
        if (node == null)
            return;

        long now = DateTime.UtcNow.Ticks;
        long viewSize = Map.DisplaySize(node);
        ViewTitle.Text = node.Kind == NodeKind.Root ? "This PC" : node.DisplayName;
        ViewSubtitle.Text = DescribeView(node);

        var rows = new List<RowItem>();
        if (node.Children != null)
        {
            var visible = node.Children
                .Where(c => c.VisibleCount(ShowAssets) > 0 || c.IsScanning || (c.Kind == NodeKind.Drive && c.ScanError != null))
                .OrderByDescending(Map.DisplaySize)
                .Take(MaxListRows);

            foreach (var c in visible)
            {
                if (c.Kind == NodeKind.Drive)
                {
                    rows.Add(MakeDriveRow(c));
                    continue;
                }
                long size = Map.DisplaySize(c);
                double fraction = viewSize > 0 ? (double)size / viewSize : 0;
                rows.Add(MakeRow(c, size, fraction, $"{Format.Percent(fraction)}  ·  {DescribeShort(c, now)}", now));
            }
        }
        if (node.Kind == NodeKind.Root)
            rows.AddRange(_entries.Where(e => !IsIncluded(e)).Select(MakeSkippedRow));
        ContentsList.ItemsSource = rows;
        ShowCapacity(node);
        ShowImportBanners(node);
        ShowLibraries(node);
        RefreshLegend();
    }

    private void RefreshLegend()
    {
        var node = _current;
        if (node == null)
            return;
        LegendTitle.Text = _colorMode switch
        {
            ColorMode.Library => "IMAGES BY LIBRARY",
            ColorMode.Format => "IMAGES BY FORMAT",
            ColorMode.Date => "IMAGES BY AGE (DATE TAKEN, ELSE MODIFIED)",
            _ => "IMAGES BY DUPLICATE STATUS",
        };
        Legend.ItemsSource = BuildLegend(node, Map.DisplaySize(node), DateTime.UtcNow.Ticks);
    }

    /// <summary>Capacity bar under the title for a drive, or for all drives combined.</summary>
    private void ShowCapacity(FsNode node)
    {
        long capacity = node.Capacity, free = node.FreeSize;
        if (node.Kind is not (NodeKind.Drive or NodeKind.Root) || capacity <= 0)
        {
            ViewCapacity.Visibility = Visibility.Collapsed;
            return;
        }
        double full = Math.Clamp((double)(capacity - free) / capacity, 0, 1);
        CapacityUsed.Width = new GridLength(full, GridUnitType.Star);
        CapacityFree.Width = new GridLength(1 - full, GridUnitType.Star);
        CapacityBar.Background = full > 0.9 ? Theme.CapacityCritical : Theme.Accent;
        ViewCapacity.Visibility = Visibility.Visible;
        ViewCapacity.ToolTip = $"Disk space: {Format.Bytes(capacity - free)} used of {Format.Bytes(capacity)}  ·  {Format.Bytes(free)} free";
    }

    private string DescribeView(FsNode node)
    {
        string images = $"{Format.Count(node.VisibleCount(ShowAssets), "image", "images")}  ·  {Format.Bytes(Map.DisplaySize(node))}";
        string dups = node.DupCount > 0
            ? $"\n{Format.Count(node.DupCount, "image has", "images have")} a copy elsewhere ({Format.Bytes(node.DupBytes)})"
            : "";
        string hidden = !ShowAssets && node.AssetCount > 0
            ? $"\n{Format.Count(node.AssetCount, "app or game image", "app and game images")} hidden"
            : "";
        switch (node.Kind)
        {
            case NodeKind.Root:
                return $"{images}  ·  across {Format.Count(node.Children!.Count, "drive", "drives")}{dups}{hidden}";
            case NodeKind.Drive:
                return images + dups + hidden + DeniedSuffix(node) + (node.Hardware is { } hw ? $"\n{hw.HealthLabel}  ·  {hw.DiskLine}" : "");
            default:
                if (node.AccessDenied)
                    return "Access denied – this folder couldn't be read.";
                string library = node.Library != Library.Other ? $"\nLibrary: {Classifier.DisplayName(node.Library)}" : "";
                string asset = node.EffectiveAssetReason is { } reason ? $"\nApp assets: {reason}" : "";
                return images + dups + hidden + library + asset + DeniedSuffix(node);
        }
    }

    private static string DeniedSuffix(FsNode node) =>
        node.DeniedCount > 0 ? $"\n{Format.Count(node.DeniedCount, "folder", "folders")} inside couldn't be read" : "";

    private static string DupSuffix(FsNode node) =>
        node.DupCount > 0 ? $"  ·  {Format.Count(node.DupCount)} with copies" : "";

    private string DescribeShort(FsNode node, long now)
    {
        if (node.Kind != NodeKind.File)
        {
            if (node.AccessDenied)
                return "access denied";
            return Format.Count(node.VisibleCount(ShowAssets), "image", "images") + DupSuffix(node);
        }
        if (node.IsAsset)
            return "app asset";
        if (node.IsCloudOnly && _colorMode != ColorMode.Format)
            return "online-only";
        return _colorMode switch
        {
            ColorMode.Library => Classifier.DisplayName(node.Library),
            ColorMode.Format => ImageFormats.DisplayName(node.Format),
            ColorMode.Date => node.Photo is { DateTaken: > 0 } p ? $"taken {Format.Ago(p.DateTaken, now)}" : $"modified {Format.Ago(node.LastWrite, now)}",
            _ => DupText(node),
        };
    }

    private string DupText(FsNode file) => file.Dup switch
    {
        DupStatus.Duplicate => Format.Count(file.Group!.Members.Count - 1, "other copy", "other copies"),
        DupStatus.Unique => "no copies",
        DupStatus.Unreadable => "couldn't be read",
        _ when file.IsAsset => "app asset, not checked",
        _ when file.IsCloudOnly => "online-only, not checked",
        _ => "not checked yet",
    };

    private List<LegendItem> BuildLegend(FsNode root, long viewSize, long now)
    {
        var sizes = new Dictionary<(Theme.Key, int), long>();
        var stack = new Stack<FsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.Kind == NodeKind.File)
            {
                if (n.IsAsset && !ShowAssets)
                    continue;
                var key = Theme.Classify(n, _colorMode, now);
                sizes[key] = sizes.GetValueOrDefault(key) + n.Size;
            }
            else if (n.Children != null && !n.IsScanning)
            {
                foreach (var c in n.Children)
                    stack.Push(c);
            }
        }

        string Name(Theme.Key key, int value) => key switch
        {
            Theme.Key.Slot => _colorMode switch
            {
                ColorMode.Duplicates => "Has a copy elsewhere",
                ColorMode.Library => Classifier.DisplayName((Library)value),
                _ => ImageFormats.DisplayName((ImageFormat)value),
            },
            Theme.Key.Age => AgeBuckets.Label(value),
            Theme.Key.Unique => "No copies",
            Theme.Key.Pending => "Not checked yet",
            Theme.Key.Unreadable => "Couldn't be read",
            Theme.Key.Asset => "App & game assets",
            _ => "Online-only (not read)",
        };

        // Ordinal and status legends keep their order; categories read best largest first.
        IEnumerable<KeyValuePair<(Theme.Key Key, int Value), long>> ordered = _colorMode switch
        {
            ColorMode.Date => sizes.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2 < 0 ? int.MaxValue : kv.Key.Item2),
            ColorMode.Duplicates => sizes.OrderBy(kv => kv.Key.Item1 switch
            {
                Theme.Key.Slot => 0,
                Theme.Key.Unique => 1,
                Theme.Key.Pending => 2,
                Theme.Key.Unreadable => 3,
                Theme.Key.Online => 4,
                _ => 5,
            }),
            _ => sizes.OrderBy(kv => kv.Key.Item1 != Theme.Key.Slot).ThenByDescending(kv => kv.Value),
        };
        return ordered
            .Where(kv => kv.Value > 0)
            .Select(kv => new LegendItem(Name(kv.Key.Item1, kv.Key.Item2), Theme.Swatch(kv.Key.Item1, kv.Key.Item2), Format.Bytes(kv.Value),
                viewSize > 0 ? Format.Percent((double)kv.Value / viewSize) : ""))
            .ToList();
    }

    /// <summary>Folders recognized as libraries (Screenshots, DCIM, WhatsApp…) inside the current view.</summary>
    private void ShowLibraries(FsNode view)
    {
        var roots = _scanners
            .Where(s => !s.Drive.IsScanning)
            .SelectMany(s => s.LibraryRoots)
            .Where(r => r.IsUnder(view) && r.VisibleCount(ShowAssets) > 0)
            .OrderBy(r => r.Library)
            .ThenByDescending(Map.DisplaySize)
            .Take(MaxListRows)
            .ToList();
        long top = roots.Count > 0 ? roots.Max(Map.DisplaySize) : 0;
        LibrariesList.ItemsSource = roots.Select(r =>
        {
            long size = Map.DisplaySize(r);
            var swatch = Theme.SlotSwatch((int)r.Library);
            return new RowItem(
                r,
                r.Name,
                Format.Bytes(size),
                $"{Classifier.DisplayName(r.Library)}  ·  {Format.Count(r.VisibleCount(ShowAssets), "image", "images")}{DupSuffix(r)}",
                swatch,
                Brushes.Transparent,
                new Thickness(0),
                FontWeights.SemiBold,
                Star(top > 0 ? (double)size / top : 0),
                Star(top > 0 ? 1 - (double)size / top : 1),
                swatch,
                Detail2: r.Parent?.FullPath ?? "",
                Detail2Visibility: Visibility.Visible);
        }).ToList();
                LibrariesSummary.Text = roots.Count > 0
            ? $"{Format.Count(roots.Count, "folder", "folders")} recognized as image libraries by their names. Double-click one to open it on the map."
            : _scanners.Any(s => s.Drive.IsScanning) ? "Libraries are listed as drives finish scanning…" : "No known image libraries here.";
    }

    private void SideTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The legend describes the map; the cards need the room.
        if (e.OriginalSource == SideTabs)
            LegendPanel.Visibility = SideTabs.SelectedItem == DupesTab || SideTabs.SelectedItem == MatchesTab
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    // ---- Side panel: Duplicates and Folder matches ----

    private sealed record LocationRow(FsNode Node, string Path, string SizeText);

    private sealed record CardRow(
        string Title,
        string SizeText,
        string BadgeGlyph,
        string BadgeText,
        Brush BadgeBrush,
        string Advice,
        List<LocationRow> Locations,
        string MoreText,
        Visibility MoreVisibility,
        string ExplorerText,
        List<FsNode> ExplorerTargets);

    private void RefreshCards()
    {
        ShowDuplicates();
        ShowMatches();
    }

    private string ScopeName(FsNode view) => view.Kind == NodeKind.Root ? "all drives" : $"“{view.DisplayName}”";

    private void ShowDuplicates()
    {
        var view = _current;
        if (view == null)
            return;
        if (_dups == null)
        {
            DupesList.ItemsSource = null;
            DupesTab.Header = "Duplicates";
            DupesSummary.Text = _analyzer != null || _scanners.Any(s => s.Drive.IsScanning) || _activeBatches > 0
                ? "Duplicates are listed once the scan and the check finish…"
                : "No drives were scanned.";
            return;
        }

        var groups = _dups.Groups.Where(g => g.Members.Any(m => m.IsUnder(view))).ToList();
        long files = groups.Sum(g => (long)g.Members.Count), extra = groups.Sum(g => g.Extra);
        DupesTab.Header = groups.Count > 0 ? $"Duplicates ({Format.Count(groups.Count)})" : "Duplicates";
        DupesSummary.Text = groups.Count == 0
            ? $"No duplicate images in {ScopeName(view)}."
            : $"{Format.Count(files)} images in {ScopeName(view)} are byte-identical copies, in {Format.Count(groups.Count, "group", "groups")}  ·  " +
              $"{Format.Bytes(extra)} in extra copies. Nothing is moved or deleted." +
              (groups.Count > MaxCards ? $" Showing the largest {MaxCards}." : "");
        DupesList.ItemsSource = groups.Take(MaxCards).Select(MakeDupCard).ToList();
    }

    private CardRow MakeDupCard(DuplicateGroup g)
    {
        var first = g.Members[0];
        bool sameName = g.Members.All(m => string.Equals(m.Name, first.Name, StringComparison.OrdinalIgnoreCase));
        var photo = g.Members.Select(m => m.Photo).FirstOrDefault(p => p is { DateTaken: > 0 });
        string advice = $"{Format.Bytes(g.Size)} each  ·  {ImageFormats.DisplayName(first.Format)}"
                        + (photo != null ? $"  ·  taken {Format.Date(photo.DateTaken)}" : "")
                        + (photo?.Camera is { } camera ? $"  ·  {camera}" : "")
                        + (sameName ? "" : "\nSaved under different names.");
        var locations = g.Members
            .Take(MaxCardLocations)
            .Select(m => new LocationRow(m, m.FullPath, Classifier.DisplayName(m.Library)))
            .ToList();
        int more = g.Members.Count - locations.Count;
        return new CardRow(
            first.Name,
            $"{Format.Bytes(g.Extra)} extra",
            "",
            Format.Count(g.Members.Count, "identical copy", "identical copies"),
            Theme.AccentText,
            advice,
            locations,
            more > 0 ? $"+ {Format.Count(more, "more copy", "more copies")}" : "",
            more > 0 ? Visibility.Visible : Visibility.Collapsed,
            "Show in Explorer",
            [first]);
    }

    private void ShowMatches()
    {
        var view = _current;
        if (view == null)
            return;
        if (_dups == null)
        {
            MatchesList.ItemsSource = null;
            MatchesTab.Header = "Matches";
            MatchesSummary.Text = _analyzer != null || _scanners.Any(s => s.Drive.IsScanning) || _activeBatches > 0
                ? "Folders holding the same images are listed once the check finishes…"
                : "No drives were scanned.";
            return;
        }

        var matches = _dups.Matches.Where(m => m.A.IsUnder(view) || m.B.IsUnder(view)).ToList();
        int easy = matches.Count(m => m.Kind != MatchKind.Partial);
        MatchesTab.Header = matches.Count > 0 ? $"Matches ({Format.Count(matches.Count)})" : "Matches";
        MatchesSummary.Text = matches.Count == 0
            ? $"No folders in {ScopeName(view)} share images with another folder."
            : $"Folders that hold the same images, most shared first. {Format.Count(easy, "pair is", "pairs are")} identical or fully " +
              "inside the other – the easiest to consolidate later. Nothing is moved or deleted." +
              (matches.Count > MaxCards ? $" Showing the first {MaxCards}." : "");
        MatchesList.ItemsSource = matches.Take(MaxCards).Select(MakeMatchCard).ToList();
    }

    private static CardRow MakeMatchCard(FolderMatch m)
    {
        var (a, b) = DistinctNames(m.A, m.B);
        string title = $"{a}  ·  {b}";
        // Never round a partial overlap up to 100%.
        double shared = m.SharedInA < m.CountA ? Math.Min(0.99, (double)m.SharedInA / m.CountA) : 1;
        var (glyph, badge, brush, advice) = m.Kind switch
        {
            MatchKind.Identical => ("", "Same images", Theme.StatusGood,
                $"Both folders hold the same {Format.Count(m.CountA, "image", "images")}."),
            MatchKind.Contained => ("", "All inside the other", Theme.StatusGood,
                $"All {Format.Count(m.CountA, "image", "images")} in “{a}” are also in “{b}”, which has {Format.Count(m.CountB)} in all."),
            _ => ("", m.SharedInA >= m.CountA * 0.9 ? "Nearly all inside the other" : "Partly the same", Theme.StatusWarning,
                $"{Format.Count(m.SharedInA)} of the {Format.Count(m.CountA, "image", "images")} in “{a}” ({Format.Percent(shared)}) " +
                $"are also in “{b}”, which has {Format.Count(m.CountB)} in all. {Format.Count(m.CountA - m.SharedInA, "is", "are")} only in “{a}”."),
        };
        return new CardRow(
            title,
            $"{Format.Bytes(m.SharedBytes)} shared",
            glyph,
            badge,
            brush,
            advice,
            [
                new LocationRow(m.A, m.A.FullPath, Format.Count(m.CountA, "image", "images")),
                new LocationRow(m.B, m.B.FullPath, Format.Count(m.CountB, "image", "images")),
            ],
            "",
            Visibility.Collapsed,
            "Open both in Explorer",
            [m.A, m.B]);
    }

    /// <summary>
    /// Folder names that tell two folders apart: "100CANON" twice becomes "Card2\100CANON" and
    /// "DCIM\100CANON", falling back to the drive letter.
    /// </summary>
    private static (string A, string B) DistinctNames(FsNode a, FsNode b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
            return (a.Name, b.Name);
        string pa = a.Parent is { Kind: NodeKind.Directory } x ? Path.Join(x.Name, a.Name) : a.FullPath;
        string pb = b.Parent is { Kind: NodeKind.Directory } y ? Path.Join(y.Name, b.Name) : b.FullPath;
        if (!string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase))
            return (pa, pb);
        return (Path.Join(a.Drive?.Name ?? "", pa), Path.Join(b.Drive?.Name ?? "", pb));
    }

    private void CardLocation_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not FsNode node)
            return;
        if (node.IsContainer)
            TryOpen(node);
        else
            ShowOnMap(node);
    }

    private void CardExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is CardRow card)
            foreach (var node in card.ExplorerTargets)
                OpenInExplorer(node);
    }

    // ---- Search ----

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer?.Stop();
        _searchTimer?.Start();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            ApplySearch();
            Map.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ApplySearch();
            e.Handled = true;
        }
    }

    private void ApplySearch()
    {
        _searchTimer.Stop();
        var matcher = SearchMatcher.Parse(SearchBox.Text);
        if (matcher?.Text == _matcher?.Text)
            return;
        _matcher = matcher;
        Map.Filter = matcher;
        Map.FilterTrail = null;
        RefreshSearch();
    }

    private async void RefreshSearch()
    {
        var view = _current;
        var matcher = _matcher;
        _searchCts?.Cancel();
        if (view == null || matcher == null)
        {
            SearchSummary.Text = "";
            Map.FilterTrail = null;
            return;
        }

        var cts = _searchCts = new CancellationTokenSource();
        var roots = ViewRoots(view);
        bool viewLit = SearchMatcher.IsInsideMatch(view, matcher);
        bool showAssets = ShowAssets;
        SearchResult result;
        try
        {
            result = await Task.Run(() => SearchQuery.Run(roots, viewLit, matcher, showAssets, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || view != _current)
            return;

        bool partial = view.Kind == NodeKind.Root && view.Children!.Any(d => d.IsScanning && d.ScanError == null);
        if (viewLit)
        {
            SearchSummary.Text = $"All of {view.DisplayName} matches";
        }
        else if (result.MatchFiles + result.MatchFolders == 0)
        {
            SearchSummary.Text = partial ? "No matches yet…" : "No matches";
        }
        else
        {
            var parts = new List<string>();
            if (result.MatchFolders > 0) parts.Add(Format.Count(result.MatchFolders, "folder", "folders"));
            if (result.MatchFiles > 0) parts.Add(Format.Count(result.MatchFiles, "image", "images"));
            SearchSummary.Text = $"{string.Join(", ", parts)}  ·  {Format.Bytes(result.MatchBytes)}" + (partial ? " so far" : "");
        }

        // Keep the path to each hit readable on the map.
        Map.FilterTrail = !viewLit ? result.MatchAncestors : null;
    }

    // ---- Treemap interaction ----

    private void Map_HoverChanged(object? sender, FsNode? node)
    {
        _tipNode = node;
        _thumbTimer.Stop();
        if (node == null || _current == null || node == _current)
        {
            Tip.Visibility = Visibility.Collapsed;
            return;
        }

        long now = DateTime.UtcNow.Ticks;
        long viewSize = Map.DisplaySize(_current);
        long size = Map.DisplaySize(node);
        string viewName = _current.Kind == NodeKind.Root ? "all drives" : _current.DisplayName;

        TipTitle.Text = node.DisplayName;
        TipSize.Text = $"{Format.Bytes(size)}   ·   {Format.Percent(viewSize > 0 ? (double)size / viewSize : 0)} of {viewName}";
        TipDetail.Text = node.Kind == NodeKind.File ? DescribeFile(node) : DescribeFolder(node);
        TipAge.Text = node.Kind switch
        {
            NodeKind.File when node.Photo is { DateTaken: > 0 } p => $"Taken {Format.Date(p.DateTaken)}  ·  {Format.Ago(p.DateTaken, now)}",
            NodeKind.File => $"Modified {Format.Date(node.LastWrite)}  ·  {Format.Ago(node.LastWrite, now)}",
            NodeKind.Directory or NodeKind.Drive when node.LastWrite > 0 && !node.IsScanning =>
                $"Newest image modified {Format.Ago(node.LastWrite, now)} ({Format.Date(node.LastWrite)})",
            _ => "",
        };
        TipAge.Visibility = TipAge.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TipPath.Text = node.FullPath;

        var target = Map.ClickTarget(node);
        string hint = target == null ? "" : target.IsScanning ? "Still scanning…" : $"Click to open {target.DisplayName}";
        if (node.Group != null)
            hint += (hint.Length > 0 ? "  ·  " : "") + "copies on screen are outlined";
        TipHint.Text = hint;
        TipHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Preview: straight from the cache, or after a short pause so sweeping the mouse stays smooth.
        TipImage.Visibility = Visibility.Collapsed;
        if (CanPreview(node))
        {
            if (Thumbnails.TryGetCached(node.FullPath, out var cached))
                ShowThumbnail(node, cached);
            else
                _thumbTimer.Start();
        }

        Tip.Visibility = Visibility.Visible;
        PositionTip(Mouse.GetPosition(Map));
    }

    private static bool CanPreview(FsNode node) =>
        node.Kind == NodeKind.File && !node.IsCloudOnly && ImageFormats.CanPreview(node.Format);

    private string DescribeFile(FsNode node)
    {
        var lines = new List<string>();
        var photo = node.Photo;
        var first = new List<string> { ImageFormats.DisplayName(node.Format) };
        if (photo is { Width: > 0, Height: > 0 })
            first.Add($"{photo.Width} × {photo.Height}");
        if (photo?.Camera is { } camera)
            first.Add(camera);
        lines.Add(string.Join("  ·  ", first));

        if (node.IsAsset)
            lines.Add($"Hidden as an app asset: {node.EffectiveAssetReason}. Not checked for duplicates.");
        else if (node.IsCloudOnly)
            lines.Add("Online-only: not read, so it isn't downloaded. Not checked for duplicates.");
        else if (node.Group is { } group)
        {
            var others = group.Members.Where(m => m != node).ToList();
            lines.Add($"{Format.Count(others.Count, "other copy", "other copies")}: {others[0].FullPath}" +
                      (others.Count > 1 ? $" (+ {Format.Count(others.Count - 1)} more)" : ""));
        }
        else
            lines.Add(node.Dup switch
            {
                DupStatus.Unique => "No copies found",
                DupStatus.Unreadable => "Couldn't be read to compare",
                _ => "Not checked yet",
            });

        lines.Add($"Library: {Classifier.DisplayName(node.Library)}");
        if (photo is { Rating: > 0 } || photo?.Tags != null)
            lines.Add(string.Join("  ·  ", new[] { photo!.Stars, photo.Tags != null ? $"Tags: {photo.Tags}" : "" }.Where(s => s.Length > 0)));
        return string.Join("\n", lines);
    }

    private string DescribeFolder(FsNode node)
    {
        if (node.Kind == NodeKind.Drive && node.IsScanning)
            return node.ScanError != null ? $"Scan failed: {node.ScanError}" : $"Scanning… {node.ScanProgress:P0}";
        if (node.AccessDenied)
            return "Access denied – this folder's contents couldn't be read.";
        string text = $"{Format.Count(node.VisibleCount(ShowAssets), "image", "images")}  ·  {Format.Count(node.DirCount, "folder", "folders")}";
        if (node.DupCount > 0)
            text += $"\n{Format.Count(node.DupCount)} with copies elsewhere ({Format.Bytes(node.DupBytes)})";
        if (node.Kind == NodeKind.Directory && node.Library != Library.Other)
            text += $"\nLibrary: {Classifier.DisplayName(node.Library)}";
        if (node.EffectiveAssetReason is { } reason && node.Kind == NodeKind.Directory)
            text += $"\nApp assets: {reason}";
        if (node.Kind == NodeKind.Drive && node.Hardware is { } hardware)
            text += $"\n{hardware.HealthLabel}  ·  {hardware.DiskLine}";
        return text;
    }

    private async void LoadTipThumbnail()
    {
        _thumbTimer.Stop();
        var node = _tipNode;
        if (node == null || !CanPreview(node))
            return;
        string path = node.FullPath;
        var image = await Thumbnails.LoadAsync(path, node.Size);
        Thumbnails.Remember(path, image);
        if (_tipNode == node && Tip.Visibility == Visibility.Visible)
            ShowThumbnail(node, image);
    }

    private void ShowThumbnail(FsNode node, BitmapSource? image)
    {
        if (image == null)
        {
            TipImage.Visibility = Visibility.Collapsed;
            return;
        }
        TipImage.Source = image;
        TipImage.LayoutTransform = (node.Photo?.Orientation ?? 1) switch
        {
            3 => new RotateTransform(180),
            6 => new RotateTransform(90),
            8 => new RotateTransform(270),
            _ => Transform.Identity,
        };
        TipImage.Visibility = Visibility.Visible;
    }

    private void Map_MouseMove(object sender, MouseEventArgs e)
    {
        if (Tip.Visibility == Visibility.Visible)
            PositionTip(e.GetPosition(Map));
    }

    private void PositionTip(Point p)
    {
        _tipPoint = p;
        Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Tip.DesiredSize;
        double x = p.X + 16, y = p.Y + 20;
        if (x + size.Width > Map.ActualWidth)
            x = p.X - size.Width - 12;
        if (y + size.Height > Map.ActualHeight)
            y = p.Y - size.Height - 12;
        Canvas.SetLeft(Tip, Math.Max(0, x));
        Canvas.SetTop(Tip, Math.Max(0, y));
    }

    private void Map_NodeClicked(object? sender, FsNode node)
    {
        Map.Focus();
        if (Map.ClickTarget(node) is { } target)
            TryOpen(target);
    }

    private void Map_NodeRightClicked(object? sender, FsNode node)
    {
        var menu = new ContextMenu();
        var target = Map.ClickTarget(node);

        if (target != null && CanOpen(target))
            menu.Items.Add(MenuItem($"Zoom into “{target.DisplayName}”", "", () => NavigateTo(target)));

        if (node.HasRealPath && !node.IsScanning)
        {
            if (node.Kind == NodeKind.File)
                menu.Items.Add(MenuItem("Open image", "", () => OpenFile(node)));
            menu.Items.Add(MenuItem(node.Kind == NodeKind.File ? "Show in Explorer" : "Open in Explorer", "", () => OpenInExplorer(node)));
            menu.Items.Add(MenuItem("Copy path", "", () => CopyPath(node)));
        }

        if (node.Group is { } group)
        {
            menu.Items.Add(new Separator());
            foreach (var copy in group.Members.Where(m => m != node).Take(5))
                menu.Items.Add(MenuItem($"Go to copy in “{copy.Parent!.FullPath}”", "", () => ShowOnMap(copy)));
        }

        if (_current?.Parent != null)
        {
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Up one level", "", GoUp));
        }

        if (menu.Items.Count == 0)
            return;
        menu.PlacementTarget = Map;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        Tip.Visibility = Visibility.Collapsed;
        menu.IsOpen = true;
    }

    private MenuItem MenuItem(string header, string glyph, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14 },
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenFile(FsNode node)
    {
        try
        {
            Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open it: {ex.Message}";
        }
    }

    private void OpenInExplorer(FsNode node)
    {
        try
        {
            string path = node.FullPath;
            string args = node.Kind == NodeKind.File ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open Explorer: {ex.Message}";
        }
    }

    private void CopyPath(FsNode node)
    {
        try
        {
            Clipboard.SetText(node.FullPath);
            StatusText.Text = $"Copied {node.FullPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't copy to clipboard: {ex.Message}";
        }
    }

    // ---- List interaction (Contents and Libraries) ----

    private void Row_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: RowItem row })
            Map.Selected = row.Node;
    }

    private void Row_MouseLeave(object sender, MouseEventArgs e)
    {
        var list = ItemsControl.ItemsControlFromItemContainer((DependencyObject)sender) as ListBox;
        Map.Selected = (list?.SelectedItem as RowItem)?.Node;
    }

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: RowItem row })
            OpenRow(row);
    }

    private void OpenRow(RowItem row)
    {
        if (row.Node.NotScanned)
            OpenDrivesMenu();
        else if (row.Node.IsContainer)
            TryOpen(row.Node);
        else
            ShowOnMap(row.Node);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Map.Selected = (((ListBox)sender).SelectedItem as RowItem)?.Node;
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ((ListBox)sender).SelectedItem is RowItem row)
        {
            OpenRow(row);
            e.Handled = true;
        }
    }

    // ---- Toolbar & keyboard ----

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    // ---- Drives menu ----

    private void DrivesButton_Click(object sender, RoutedEventArgs e) => OpenDrivesMenu();

    /// <summary>
    /// A checklist of drives plus the network/cloud toggle. Changes apply when the menu closes:
    /// newly ticked drives are scanned and unticked ones removed, without rescanning the rest.
    /// </summary>
    private void OpenDrivesMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = DrivesButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var driveItems = new List<(MenuItem Item, DriveEntry Entry)>();
        var toggle = new MenuItem
        {
            Header = "Include network & cloud drives",
            Icon = CheckIcon(App.Settings.IncludeNetworkDrives),
            StaysOpenOnClick = true,
            ToolTip = "Network shares and cloud-sync drives (Google Drive, pCloud…) are skipped by default. " +
                      "Online-only files on them are listed but never read.",
        };
        toggle.Click += (_, _) =>
        {
            App.Settings.IncludeNetworkDrives = !App.Settings.IncludeNetworkDrives;
            toggle.Icon = CheckIcon(App.Settings.IncludeNetworkDrives);
            foreach (var (item, entry) in driveItems)
                item.Icon = CheckIcon(IsIncluded(entry));
        };
        menu.Items.Add(toggle);
        menu.Items.Add(new Separator());

        if (_entries.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Looking for drives…", IsEnabled = false });

        foreach (var entry in _entries.OrderBy(e => e.Key))
        {
            var item = new MenuItem
            {
                Header = DriveMenuHeader(entry),
                Icon = CheckIcon(IsIncluded(entry)),
                StaysOpenOnClick = true,
            };
            item.Click += (_, _) =>
            {
                SetIncluded(entry, !IsIncluded(entry));
                item.Icon = CheckIcon(IsIncluded(entry));
            };
            driveItems.Add((item, entry));
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) =>
        {
            App.Settings.Save();
            ApplyDriveSelection();
        };
        menu.IsOpen = true;
    }

    private TextBlock CheckIcon(bool on) => new()
    {
        Text = on ? "" : "",
        FontFamily = (FontFamily)FindResource("Icons"),
        FontSize = 14,
    };

    private UIElement DriveMenuHeader(DriveEntry entry)
    {
        string glyph = entry.Node != null ? Theme.DriveGlyph(entry.Node) : entry.Source switch
        {
            DriveSource.Network => "",
            DriveSource.Virtual => "",
            _ => entry.Info.DriveType == DriveType.Removable ? "" : "",
        };
        string name = entry.Node?.DisplayName ?? entry.Key;
        string detail = entry.Source switch
        {
            DriveSource.Network => entry.NetworkPath ?? "network",
            DriveSource.Virtual => "cloud / virtual",
            _ when entry.NotReady => "not ready",
            _ => entry.Node?.Hardware?.KindLabel ?? "local",
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("Icons"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        var muted = new TextBlock { Text = $"  ({detail})", VerticalAlignment = VerticalAlignment.Center };
        muted.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        panel.Children.Add(muted);
        return panel;
    }

    /// <summary>Scans newly included drives and drops excluded ones, leaving the rest as they are.</summary>
    private void ApplyDriveSelection()
    {
        var pc = _pc;
        if (pc == null || _cts == null || _cts.IsCancellationRequested)
            return;

        var toScan = new List<DriveEntry>();
        bool removed = false;
        foreach (var entry in _entries)
        {
            bool want = IsIncluded(entry);
            if (want && !entry.Active)
            {
                toScan.Add(entry);
            }
            else if (!want && entry.Active)
            {
                entry.Cts?.Cancel();
                entry.Active = false;
                entry.NotReady = false;
                _probing.Remove(entry.Name);
                if (entry.Node is { } node)
                {
                    pc.Children!.Remove(node);
                    _scanners.RemoveAll(s => s.Drive == node);
                    if (_current != null && (_current == node || node.IsAncestorOf(_current)))
                        _current = null;
                    entry.Node = null;
                }
                removed = true;
            }
        }

        if (removed)
        {
            RecomputeRoot(pc);
            if (_current == null)
                NavigateTo(pc);
            Map.Invalidate();
            RefreshSearch();
            // Duplicate groups may point into the removed drive; check again (fast, from the cache).
            if (toScan.Count == 0 && _activeBatches == 0)
                StartAnalysis();
        }
        RefreshSidePanel();
        UpdateStatus();
        if (toScan.Count > 0)
            _ = ScanEntries(toScan, pc, _cts.Token);
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        bool dark = !Theme.IsDark;
        App.ApplyTheme(dark);
        App.Settings.Theme = dark ? "Dark" : "Light";
        App.Settings.Save();

        ApplyThemeResources();
        Map.Invalidate();
        RefreshSidePanel();
        RefreshCards();
    }

    private void ApplyThemeResources()
    {
        // The button shows the mode you'd switch to.
        ThemeGlyph.Text = Theme.IsDark ? "" : "";
        ThemeButton.ToolTip = Theme.IsDark ? "Switch to light mode" : "Switch to dark mode";
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        switch (Elevation.RelaunchElevated(out string? error))
        {
            case Elevation.RelaunchResult.Started:
                Close(); // the elevated copy takes over
                break;
            case Elevation.RelaunchResult.Cancelled:
                StatusText.Text = "Administrator restart was cancelled; still running as a normal user.";
                break;
            default:
                StatusText.Text = $"Couldn't restart as administrator: {error}";
                break;
        }
    }

    private void Assets_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return; // fires during InitializeComponent
        Map.ShowAssets = ShowAssets;
        RefreshSidePanel();
        RefreshSearch();
        UpdateStatus();
    }

    private void ColorMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        _colorMode = (ColorMode)ColorModeBox.SelectedIndex;
        Map.ColorMode = _colorMode;
        RefreshSidePanel();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        bool inText = Keyboard.FocusedElement is TextBox;

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (!inText && (e.Key == Key.Back || (e.Key == Key.System && e.SystemKey == Key.Left)))
        {
            GoUp();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FullRescan();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            ScanForNew();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !inText)
        {
            ContentsList.SelectedItem = null;
            LibrariesList.SelectedItem = null;
            Map.Selected = null;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.XButton1)
        {
            GoUp();
            e.Handled = true;
        }
    }
}
