using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageTools.Model;
using ImageTools.Platform;
using ImageTools.Treemap;

namespace ImageTools;

// ---- Rows shown in the window ----

/// <summary>A heading whose checkbox ticks or unticks every photo under it; partly ticked shows as indeterminate.</summary>
public abstract class GroupHeader : INotifyPropertyChanged
{
    public List<ImportRow> Rows { get; } = [];

    /// <summary>Set by the window: ticks or unticks every row in one go.</summary>
    internal Action<GroupHeader, bool>? SetAll { get; set; }

    public bool? AllSelected
    {
        get
        {
            int ticked = Rows.Count(r => r.Selected);
            return ticked == 0 ? false : ticked == Rows.Count ? true : null;
        }
        set
        {
            if (value is bool all)
                SetAll?.Invoke(this, all);
        }
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AllSelected)));

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class FolderHeader(string title, string pathLine, string note) : GroupHeader
{
    public string Title { get; } = title;
    public string PathLine { get; } = pathLine;
    public string Note { get; } = note;
}

public sealed class BucketHeader(string heading, string description) : GroupHeader
{
    public string Heading { get; } = heading;
    public string Description { get; } = description;
}

/// <param name="sameName">A different file with the same name in the archive folder, if there is one.</param>
public sealed class ImportRow(CardFolder folder, ImportItem item, FsNode? sameName, Action changed) : INotifyPropertyChanged
{
    private bool _selected = item.Bucket == ImportBucket.New;
    private bool _current;

    public CardFolder Folder { get; } = folder;
    public ImportItem Item { get; } = item;
    public FsNode? SameName { get; } = sameName;
    public bool Suggested { get; } = item.Bucket == ImportBucket.New;
    public string Name => Item.Image.Name;
    public string DateText => Format.Date(Item.Image.DateTicks);
    public string SizeText => Format.Bytes(Item.Image.Size);
    public string Note => Item.CopyElsewhere is { } copy ? $"also at {copy.FullPath}"
        : SameName != null ? $"a different “{Name}” is in the archive – perhaps an edited copy; would be saved with a suffix"
        : "";

    /// <summary>What the compare panel shows next to the card's photo: the identical copy, or the same-named archive file.</summary>
    public FsNode? Counterpart => Item.CopyElsewhere ?? SameName;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            changed();
        }
    }

    /// <summary>The row shown in the compare panel.</summary>
    public bool IsCurrent
    {
        get => _current;
        set
        {
            _current = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
        }
    }

    public Brush RowBackground => _current ? Theme.LinkHover : Brushes.Transparent;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ChangeRow(string Glyph, Brush GlyphBrush, string Text, string Detail, string? Note)
{
    public Visibility NoteVisibility => Note != null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Import from a camera card: choose images (new ones are ticked; any heading ticks its whole
/// group; click a row to compare it with the archive), preview the exact changes, then apply them
/// as verified copies.
/// </summary>
public partial class ImportWindow : Window
{
    private readonly CardStatus _status;
    private readonly HashCache? _cache;
    private readonly List<ImportRow> _rows = [];
    private readonly List<GroupHeader> _headers = [];
    private ImportRow? _compared;
    private int _compareVersion;
    private ImportPlan? _plan;
    private bool _bulk;
    private CancellationTokenSource? _copyCts;
    private string? _logPath;

    /// <summary>Images copied by this window, so the main window knows to look for them.</summary>
    public int CopiedImages { get; private set; }

    public ImportWindow(CardStatus status, HashCache? cache)
    {
        InitializeComponent();
        _status = status;
        _cache = cache;

        var items = new List<object>();
        foreach (var folder in status.Folders.Where(f => f.Items.Count > 0))
        {
            var folderHeader = MakeFolderHeader(folder);
            AddHeader(items, folderHeader);
            var archiveFiles = new Dictionary<string, FsNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in (folder.Archive?.Children ?? []).Where(c => c.Kind == NodeKind.File))
                archiveFiles.TryAdd(f.Name, f);
            foreach (var bucket in new[] { ImportBucket.New, ImportBucket.OlderGap, ImportBucket.Elsewhere })
            {
                var list = folder.Items.Where(i => i.Bucket == bucket).ToList();
                if (list.Count == 0)
                    continue;
                int sameName = list.Count(i => archiveFiles.ContainsKey(i.Image.Name));
                var bucketHeader = MakeBucketHeader(bucket, list.Count, sameName);
                AddHeader(items, bucketHeader);
                foreach (var item in list)
                {
                    var row = new ImportRow(folder, item, archiveFiles.GetValueOrDefault(item.Image.Name), SelectionChanged);
                    _rows.Add(row);
                    folderHeader.Rows.Add(row);
                    bucketHeader.Rows.Add(row);
                    items.Add(row);
                }
            }
        }
        RowsList.ItemsSource = items;

        HeaderTitle.Text = $"Import from camera card {status.Card.DisplayName}";
        int folders = status.Folders.Count(f => f.Items.Count > 0);
        string subtitle = $"{Format.Count(status.NewCount, "new image", "new images")} ({Format.Bytes(status.NewBytes)}) in " +
                          $"{Format.Count(folders, "folder", "folders")} aren't in their archive folders yet. " +
                          "Nothing is copied until you preview the changes and apply them.";
        if (status.Unmatched.Count > 0)
            subtitle += $"\n{Format.Count(status.Unmatched.Count, "folder", "folders")} on the card ({string.Join(", ", status.Unmatched.Select(f => f.Name))}) " +
                        "have no archive folder yet and aren't listed. Import them once by hand and Image Tools will follow them from then on.";
        HeaderSubtitle.Text = subtitle;
        SelectionChanged();
    }

    private static FolderHeader MakeFolderHeader(CardFolder folder)
    {
        bool exists = folder.Archive != null || Directory.Exists(folder.ArchivePath);
        string note = folder.Source switch
        {
            ArchiveSource.NextToSiblings when !exists =>
                "New folder, next to where this card's other folders are archived.",
            ArchiveSource.NextToSiblings =>
                "Next to where this card's other folders are archived.",
            ArchiveSource.Remembered =>
                "Archive folder remembered from an earlier scan of this card.",
            _ => "",
        };
        if (folder.Shared > 0)
            note = (note + $" {Format.Count(folder.Shared, "image is", "images are")} already there; the newest was taken {Format.Date(folder.Cutoff)}.").Trim();
        return new FolderHeader(
            $"{folder.Folder.Name}  →  {Format.ShortPath(folder.ArchivePath)}",
            $"{folder.Folder.FullPath}  →  {folder.ArchivePath}",
            note);
    }

    /// <param name="sameName">How many of these have a different file with the same name in the archive.</param>
    private static BucketHeader MakeBucketHeader(ImportBucket bucket, int count, int sameName)
    {
        string names = sameName == 0 ? ""
            : sameName == count ? " Every one has a different file with the same name in the archive – perhaps edited copies of the same photos."
            : $" {Format.Count(sameName, "has", "have")} a different file with the same name in the archive – perhaps edited copies.";
        return bucket switch
        {
            ImportBucket.New => new BucketHeader($"NEW SINCE THE LAST IMPORT ({Format.Count(count)})",
                "Taken after the newest image already in the archive folder. Ticked." + names),
            ImportBucket.OlderGap => new BucketHeader($"OLDER, NOT IN THE ARCHIVE ({Format.Count(count)})",
                "Older than the newest archived image, so perhaps deleted from the archive on purpose. Not ticked." + names),
            _ => new BucketHeader($"ALREADY ELSEWHERE ON THIS PC ({Format.Count(count)})",
                "Byte-identical copies are already in other folders. Not ticked." + names),
        };
    }

    private void AddHeader(List<object> items, GroupHeader header)
    {
        header.SetAll = SetGroup;
        _headers.Add(header);
        items.Add(header);
    }

    /// <summary>A heading's checkbox: ticks or unticks every photo under it.</summary>
    private void SetGroup(GroupHeader header, bool selected)
    {
        _bulk = true;
        foreach (var row in header.Rows)
            row.Selected = selected;
        _bulk = false;
        SelectionChanged();
    }

    private IEnumerable<ImportRow> SelectedRows => _rows.Where(r => r.Selected);

    private void SelectionChanged()
    {
        if (_bulk)
            return;
        foreach (var header in _headers)
            header.Refresh();
        int count = SelectedRows.Count();
        SelectionSummary.Text = $"{Format.Count(count)} of {Format.Count(_rows.Count, "image", "images")} selected  ·  " +
                                Format.Bytes(SelectedRows.Sum(r => r.Item.Image.Size));
        PreviewButton.IsEnabled = count > 0;
        FooterNote.Text = count > 0 ? "" : "Tick at least one image to preview the changes.";
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        bool all = SelectAllCheck.IsChecked == true;
        _bulk = true;
        foreach (var row in _rows)
            row.Selected = all || row.Suggested;
        _bulk = false;
        SelectionChanged();
    }

    // ---- Compare: the card's photo next to its counterpart ----

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ImportRow row)
            ShowCompare(row);
    }

    private async void ShowCompare(ImportRow row)
    {
        if (_compared != null)
            _compared.IsCurrent = false;
        _compared = row;
        row.IsCurrent = true;
        int version = ++_compareVersion;

        var card = row.Item.Image;
        var other = row.Counterpart;
        CompareHint.Visibility = Visibility.Collapsed;
        CompareGrid.Visibility = Visibility.Visible;
        RightLabel.Text = row.Item.CopyElsewhere != null ? "ALREADY ON THIS PC" : other != null ? "IN THE ARCHIVE, SAME NAME" : "IN THE ARCHIVE";
        LeftImage.Source = null;
        RightImage.Source = null;
        RightPlaceholder.Text = other == null ? "No file with this name in the archive folder." : "Loading…";
        RightPlaceholder.Visibility = Visibility.Visible;
        LeftPlaceholder.Text = "Loading…";
        LeftPlaceholder.Visibility = Visibility.Visible;
        ShowDetails(card, other);
        CompareVerdict.Text = Verdict(row);

        // Details first (they're small), then the two previews. Only the clicked row is ever read.
        await EnsurePhotoInfo(card);
        if (other != null)
            await EnsurePhotoInfo(other);
        if (version != _compareVersion)
            return;
        ShowDetails(card, other);
        CompareVerdict.Text = Verdict(row);

        await ShowPreview(card, LeftImage, LeftPlaceholder, version);
        if (other != null)
            await ShowPreview(other, RightImage, RightPlaceholder, version);
    }

    private static async Task EnsurePhotoInfo(FsNode node)
    {
        if (node.Photo != null || node.IsCloudOnly)
            return;
        string path = node.FullPath;
        node.Photo = await Task.Run(() => PhotoProperties.Read(path)) ?? PhotoInfo.Empty;
    }

    private async Task ShowPreview(FsNode node, Image image, TextBlock placeholder, int version)
    {
        string path = node.FullPath;
        if (node.IsCloudOnly || !ImageFormats.CanPreview(node.Format))
        {
            placeholder.Text = node.IsCloudOnly ? "Online-only: not opened, so it isn't downloaded." : "No preview for this format.";
            return;
        }
        if (!Thumbnails.TryGetCached(path, out var bitmap, Thumbnails.CompareWidth))
        {
            bitmap = await Thumbnails.LoadAsync(path, node.Size, Thumbnails.CompareWidth);
            Thumbnails.Remember(path, bitmap, Thumbnails.CompareWidth);
        }
        if (version != _compareVersion)
            return;
        if (bitmap == null)
        {
            placeholder.Text = "Windows couldn't make a preview of this file.";
            return;
        }
        image.Source = bitmap;
        image.LayoutTransform = (node.Photo?.Orientation ?? 1) switch
        {
            3 => new RotateTransform(180),
            6 => new RotateTransform(90),
            8 => new RotateTransform(270),
            _ => Transform.Identity,
        };
        placeholder.Visibility = Visibility.Collapsed;
    }

    /// <summary>Two columns of details; values that differ are highlighted on both sides.</summary>
    private void ShowDetails(FsNode card, FsNode? other)
    {
        var fields = new List<(string Left, string? Right, bool Differs)>();
        void Add(string left, string? right) => fields.Add((left, right, other != null && left != right));
        var a = card.Photo;
        var b = other?.Photo;

        Add(card.Name, other?.Name);
        Add($"{Format.Bytes(card.Size)} ({card.Size:N0} bytes)", other != null ? $"{Format.Bytes(other.Size)} ({other.Size:N0} bytes)" : null);
        Add(Dimensions(a), other != null ? Dimensions(b) : null);
        Add(Taken(a), other != null ? Taken(b) : null);
        Add($"Modified {Stamp(card.LastWrite)}", other != null ? $"Modified {Stamp(other.LastWrite)}" : null);
        Add(a?.Camera ?? "Camera not recorded", other != null ? b?.Camera ?? "Camera not recorded" : null);
        if (a is { Rating: > 0 } || b is { Rating: > 0 } || a?.Tags != null || b?.Tags != null)
            Add(Extras(a), other != null ? Extras(b) : null);

        LeftDetails.Children.Clear();
        RightDetails.Children.Clear();
        foreach (var (left, right, differs) in fields)
        {
            LeftDetails.Children.Add(DetailLine(left, differs));
            if (other != null)
                RightDetails.Children.Add(DetailLine(right ?? "", differs));
        }
        LeftDetails.Children.Add(PathLine(card.Parent?.FullPath ?? ""));
        if (other != null)
            RightDetails.Children.Add(PathLine(other.Parent?.FullPath ?? ""));
    }

    private static string Dimensions(PhotoInfo? p) => p is { Width: > 0, Height: > 0 } ? $"{p.Width} × {p.Height}" : "Size not recorded";

    private static string Taken(PhotoInfo? p) => p is { DateTaken: > 0 } ? $"Taken {Stamp(p.DateTaken)}" : "No date taken";

    private static string Extras(PhotoInfo? p) =>
        string.Join("  ·  ", new[] { p?.Stars ?? "", p?.Tags != null ? $"Tags: {p.Tags}" : "" }.Where(s => s.Length > 0)) is { Length: > 0 } s ? s : "No rating or tags";

    private static string Stamp(long utcTicks) =>
        utcTicks <= 0 ? "unknown" : new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy HH:mm:ss");

    private static TextBlock DetailLine(string text, bool differs) => new()
    {
        Text = text,
        Foreground = differs ? Theme.AccentText : Theme.SecondaryText,
        FontWeight = differs ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        ToolTip = text,
        Margin = new Thickness(0, 1, 0, 1),
    };

    private static TextBlock PathLine(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Theme.MutedText,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
    };

    /// <summary>A one-line reading of the comparison, to help decide copy or skip.</summary>
    private static string Verdict(ImportRow row)
    {
        var card = row.Item.Image;
        if (row.Item.CopyElsewhere is { } copy)
            return $"An identical copy is already at {copy.Parent?.FullPath}" +
                   (copy.Group?.ByFingerprint == true ? " (matched by fingerprint)." : ".") +
                   " Copying would add another.";
        if (row.SameName is not { } other)
            return row.Item.Bucket == ImportBucket.New
                ? "Not in the archive yet, and not anywhere else on this PC."
                : "Not in the archive under this name, and not anywhere else on this PC – perhaps deleted from the archive on purpose.";

        var (a, b) = (card.Photo, other.Photo);
        bool bothDated = a is { DateTaken: > 0 } && b is { DateTaken: > 0 };
        bool sameShot = bothDated && a!.DateTaken == b!.DateTaken;
        bool sameSize = a is { Width: > 0 } && b is { Width: > 0 } && a.Width == b.Width && a.Height == b.Height;
        string keep = $" Copying keeps both: the card's would be saved as “{System.IO.Path.GetFileNameWithoutExtension(card.Name)} (2){System.IO.Path.GetExtension(card.Name)}”.";
        bool metadataChanged = a != null && b != null && (a.Rating != b.Rating || a.Tags != b.Tags);
        if (sameShot && sameSize && metadataChanged)
            return "Same shot – the archive copy has a different rating or tags. Rating or tagging a photo in Windows rewrites the file, " +
                   "so they no longer match byte for byte; the picture itself is very likely unchanged." + keep;
        if (sameShot && sameSize)
            return "Same shot (same date taken and dimensions), but the files differ – probably edited, rotated or re-saved." + keep;
        if (sameShot)
            return "Same shot (same date taken), different dimensions – probably resized or cropped." + keep;
        if (bothDated)
            return "A different photo that happens to share the name – the camera's counter has probably wrapped around." + keep;
        return "Same name, different contents. Compare the previews to decide." + keep;
    }

    // ---- Step 2: preview changes ----

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        var selection = SelectedRows
            .GroupBy(r => r.Folder)
            .Select(g => (g.Key, g.Select(r => r.Item).ToList()))
            .ToList();
        string card = _status.Card.DisplayName;
        PreviewButton.IsEnabled = false;
        FooterNote.Text = "Working out the changes…";
        try
        {
            // Lists the archive folders and the card's sidecar files; nothing is written.
            _plan = await Task.Run(() => ImportPlanner.Build(card, selection));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FooterNote.Text = $"Couldn't preview the changes: {ex.Message}";
            PreviewButton.IsEnabled = true;
            return;
        }
        PreviewButton.IsEnabled = true;
        ShowPreview(_plan);
    }

    private void ShowPreview(ImportPlan plan)
    {
        HeaderTitle.Text = "Preview changes";
        HeaderSubtitle.Text = $"From camera card {plan.CardName} – check every change below before applying.";

        PreviewSummary.Children.Clear();
        AddSummary($"Copy {Format.Count(plan.Images, "image", "images")}"
                   + (plan.Sidecars > 0 ? $" and {Format.Count(plan.Sidecars, "sidecar file", "sidecar files")}" : "")
                   + $" ({Format.Bytes(plan.Bytes)}).", Theme.PrimaryText);
        if (plan.NewFolders.Count > 0)
            AddSummary($"Create {Format.Count(plan.NewFolders.Count, "new folder", "new folders")}.", Theme.PrimaryText);
        if (plan.Renamed > 0)
            AddSummary($"{Format.Count(plan.Renamed, "file gets", "files get")} a “ (2)” suffix because a different file with the same name " +
                       "is already there. Each is marked below and listed in the report.", Theme.AccentText);
        bool enoughSpace = true;
        foreach (var (root, free) in plan.Space)
        {
            long need = plan.Copies.Where(c => c.Target.StartsWith(root, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Bytes);
            string drive = root.TrimEnd('\\');
            if (free < 0)
                AddSummary($"Couldn't check the free space on {drive}.", Theme.StatusWarning);
            else if (free < need)
            {
                AddSummary($"Not enough space on {drive}: {Format.Bytes(need)} needed, {Format.Bytes(free)} free.", Theme.StatusCritical);
                enoughSpace = false;
            }
            else
                AddSummary($"{drive} has {Format.Bytes(free)} free; {Format.Bytes(free - need)} left afterwards.", Theme.SecondaryText);
        }
        AddSummary("Nothing on the card is changed or deleted. Nothing in the archive is overwritten.", Theme.SecondaryText);

        var rows = new List<ChangeRow>();
        foreach (var folder in plan.NewFolders)
            rows.Add(new ChangeRow("", Theme.AccentText, $"Create folder  {folder}", "", null));
        foreach (var c in plan.Copies)
            rows.Add(new ChangeRow(c.IsSidecar ? "" : "", c.Note != null ? Theme.AccentText : Theme.SecondaryText,
                $"{Path.GetFileName(c.Source)}  →  {c.Target}",
                $"from {c.Source}  ·  {Format.Bytes(c.Bytes)}" + (c.IsSidecar ? "  ·  sidecar" : ""),
                c.Note));
        ChangesList.ItemsSource = rows;

        SelectPanel.Visibility = Visibility.Collapsed;
        SelectButtons.Visibility = Visibility.Collapsed;
        PreviewPanel.Visibility = Visibility.Visible;
        PreviewButtons.Visibility = Visibility.Visible;
        ApplyButton.Content = $"Apply – copy {Format.Count(plan.Copies.Count, "file", "files")}";
        ApplyButton.IsEnabled = enoughSpace && plan.Copies.Count > 0;
        FooterNote.Text = enoughSpace
            ? "Each copy is checked against the original before it gets its final name. A log of every file is kept."
            : "Free up space on the archive drive, or select fewer images, before applying.";
    }

    // ---- Step 3: apply ----

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is not { } plan)
            return;

        HeaderTitle.Text = $"Copying from camera card {plan.CardName}";
        HeaderSubtitle.Text = "Copies only: nothing on the card is changed or deleted, and nothing in the archive is overwritten.";
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewButtons.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        CopyingButtons.Visibility = Visibility.Visible;
        FooterNote.Text = "";
        var results = new System.Collections.ObjectModel.ObservableCollection<ChangeRow>();
        ResultsList.ItemsSource = results;

        _copyCts = new CancellationTokenSource();
        var importer = new Importer(plan, _cache);
        var progress = new Progress<ImportProgress>(p => ShowProgress(plan, p, results));
        List<CopyResult> done;
        try
        {
            done = await Task.Run(() => importer.Run(progress, _copyCts.Token));
        }
        catch (Exception ex)
        {
            done = [];
            FooterNote.Text = $"Couldn't finish the import: {ex.Message}";
        }
        _logPath = importer.LogPath;
        bool stopped = _copyCts.IsCancellationRequested;
        _copyCts = null;
        CopiedImages = done.Count(r => r.Outcome == CopyOutcome.Copied && !r.Copy.IsSidecar);
        ShowFinished(plan, done, stopped);
        _cache?.Save([]); // keep the copies' hashes; prune nothing (that's the scan's job)
    }

    private void ShowProgress(ImportPlan plan, ImportProgress p, ICollection<ChangeRow> results)
    {
        double fraction = plan.Bytes > 0 ? Math.Clamp((double)p.BytesDone / plan.Bytes, 0, 1) : 0;
        ProgressDone.Width = new GridLength(fraction, GridUnitType.Star);
        ProgressLeft.Width = new GridLength(1 - fraction, GridUnitType.Star);
        ProgressText.Text = $"Copying {Format.Count(p.FilesDone + 1)} of {Format.Count(plan.Copies.Count, "file", "files")}…  " +
                            $"{Format.Bytes(p.BytesDone)} of {Format.Bytes(plan.Bytes)}";
        ProgressDetail.Text = p.Current;
        if (p.Last is { } r)
            results.Add(MakeResultRow(r));
    }

    private static ChangeRow MakeResultRow(CopyResult r) => r.Outcome switch
    {
        CopyOutcome.Copied => new ChangeRow("", Theme.StatusGood, $"{Path.GetFileName(r.Copy.Source)}  →  {r.Copy.Target}",
            $"copied and verified  ·  {Format.Bytes(r.Copy.Bytes)}" + (r.Copy.IsSidecar ? "  ·  sidecar" : ""), r.Copy.Note),
        CopyOutcome.Skipped => new ChangeRow("", Theme.StatusWarning, $"{Path.GetFileName(r.Copy.Source)}  →  {r.Copy.Target}",
            "skipped", r.Reason),
        _ => new ChangeRow("", Theme.StatusCritical, $"{Path.GetFileName(r.Copy.Source)}  →  {r.Copy.Target}",
            "not copied", r.Reason),
    };

    private void ShowFinished(ImportPlan plan, List<CopyResult> results, bool stopped)
    {
        int copied = results.Count(r => r.Outcome == CopyOutcome.Copied);
        int skipped = results.Count(r => r.Outcome == CopyOutcome.Skipped);
        int failed = results.Count(r => r.Outcome == CopyOutcome.Failed);
        int renamed = results.Count(r => r.Outcome == CopyOutcome.Copied && r.Copy.Note != null);
        long bytes = results.Where(r => r.Outcome == CopyOutcome.Copied).Sum(r => r.Copy.Bytes);

        HeaderTitle.Text = stopped ? "Import stopped" : failed + skipped > 0 ? "Import finished, with problems" : "Import finished";
        HeaderSubtitle.Text = $"From camera card {plan.CardName}. Nothing on the card was changed or deleted, and nothing in the archive was overwritten.";
        var parts = new List<string> { $"Copied {Format.Count(copied, "file", "files")} ({Format.Bytes(bytes)}), each checked against its original." };
        if (renamed > 0)
            parts.Add($"{Format.Count(renamed, "was", "were")} saved with a suffix because the name was taken.");
        if (skipped > 0)
            parts.Add($"{Format.Count(skipped, "file was", "files were")} skipped because something already had that name.");
        if (failed > 0)
            parts.Add($"{Format.Count(failed, "file", "files")} couldn't be copied – see below.");
        if (stopped && results.Count < plan.Copies.Count)
            parts.Add($"{Format.Count(plan.Copies.Count - results.Count, "file wasn't", "files weren't")} started.");
        ProgressText.Text = string.Join(" ", parts);
        ProgressDone.Width = new GridLength(1, GridUnitType.Star);
        ProgressLeft.Width = new GridLength(0, GridUnitType.Star);
        ProgressFill.Background = failed > 0 ? Theme.StatusCritical : skipped > 0 ? Theme.StatusWarning : Theme.StatusGood;
        ProgressDetail.Text = _logPath != null ? $"Log: {_logPath}" : "The log couldn't be written.";

        // Problems first, so they aren't lost among hundreds of successful copies.
        ResultsList.ItemsSource = results
            .OrderBy(r => r.Outcome switch { CopyOutcome.Failed => 0, CopyOutcome.Skipped => 1, _ => 2 })
            .Select(MakeResultRow)
            .ToList();

        CopyingButtons.Visibility = Visibility.Collapsed;
        DoneButtons.Visibility = Visibility.Visible;
        OpenLogButton.IsEnabled = _logPath != null;
        FooterNote.Text = copied > 0 ? "The map is updated with a Scan for new when you close this window." : "";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _copyCts?.Cancel();
        StopButton.IsEnabled = false;
        FooterNote.Text = "Stopping… the file being copied now is abandoned and its unfinished copy removed.";
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logPath == null)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_logPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FooterNote.Text = $"Couldn't open the log: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closing mid-copy stops after the current file instead of leaving it half done.</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_copyCts == null)
            return;
        e.Cancel = true;
        Stop_Click(this, new RoutedEventArgs());
    }

    private void AddSummary(string text, Brush brush) =>
        PreviewSummary.Children.Add(new TextBlock { Text = text, Foreground = brush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        HeaderTitle.Text = $"Import from camera card {_status.Card.DisplayName}";
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewButtons.Visibility = Visibility.Collapsed;
        SelectPanel.Visibility = Visibility.Visible;
        SelectButtons.Visibility = Visibility.Visible;
        HeaderSubtitle.Text = $"Choose the images to import from {_status.Card.DisplayName}. Nothing is copied until you preview the changes and apply them.";
        SelectionChanged();
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null)
            return;
        try
        {
            Clipboard.SetText(ImportPlanner.Report(_plan));
            FooterNote.Text = "Report copied to the clipboard.";
        }
        catch (Exception ex)
        {
            FooterNote.Text = $"Couldn't copy to the clipboard: {ex.Message}";
        }
    }
}
