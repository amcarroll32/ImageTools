using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageTools.Model;
using ImageTools.Platform;
using ImageTools.Treemap;

namespace ImageTools;

// ---- Rows shown in the window ----

public sealed record FolderHeader(string Title, string PathLine, string Note);

public sealed record BucketHeader(string Heading, string Description);

/// <param name="nameInArchive">A different file with the same name is already in the archive folder.</param>
public sealed class ImportRow(CardFolder folder, ImportItem item, bool nameInArchive, Action changed) : INotifyPropertyChanged
{
    private bool _selected = item.Bucket == ImportBucket.New;

    public CardFolder Folder { get; } = folder;
    public ImportItem Item { get; } = item;
    public bool Suggested { get; } = item.Bucket == ImportBucket.New;
    public string Name => Item.Image.Name;
    public string DateText => Format.Date(Item.Image.DateTicks);
    public string SizeText => Format.Bytes(Item.Image.Size);
    public string Note => Item.CopyElsewhere is { } copy ? $"also at {copy.FullPath}"
        : nameInArchive ? $"a different “{Name}” is in the archive – perhaps an edited copy; would be saved with a suffix"
        : "";

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

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ChangeRow(string Glyph, Brush GlyphBrush, string Text, string Detail, string? Note)
{
    public Visibility NoteVisibility => Note != null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Import from a camera card in two steps: choose images (new ones are ticked), then preview the
/// exact changes. Nothing is applied from here yet: this beta only previews.
/// </summary>
public partial class ImportWindow : Window
{
    private readonly CardStatus _status;
    private readonly HashCache? _cache;
    private readonly List<ImportRow> _rows = [];
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
            items.Add(MakeFolderHeader(folder));
            var archiveNames = (folder.Archive?.Children ?? [])
                .Where(c => c.Kind == NodeKind.File)
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var bucket in new[] { ImportBucket.New, ImportBucket.OlderGap, ImportBucket.Elsewhere })
            {
                var list = folder.Items.Where(i => i.Bucket == bucket).ToList();
                if (list.Count == 0)
                    continue;
                int sameName = list.Count(i => archiveNames.Contains(i.Image.Name));
                items.Add(MakeBucketHeader(bucket, list.Count, sameName));
                foreach (var item in list)
                {
                    var row = new ImportRow(folder, item, archiveNames.Contains(item.Image.Name), SelectionChanged);
                    _rows.Add(row);
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

    private IEnumerable<ImportRow> SelectedRows => _rows.Where(r => r.Selected);

    private void SelectionChanged()
    {
        if (_bulk)
            return;
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
