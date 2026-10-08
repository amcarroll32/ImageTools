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
    private readonly List<ImportRow> _rows = [];
    private ImportPlan? _plan;
    private bool _bulk;

    public ImportWindow(CardStatus status)
    {
        InitializeComponent();
        _status = status;

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
        foreach (var (root, free) in plan.Space)
        {
            long need = plan.Copies.Where(c => c.Target.StartsWith(root, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Bytes);
            string drive = root.TrimEnd('\\');
            if (free < 0)
                AddSummary($"Couldn't check the free space on {drive}.", Theme.StatusWarning);
            else if (free < need)
                AddSummary($"Not enough space on {drive}: {Format.Bytes(need)} needed, {Format.Bytes(free)} free.", Theme.StatusCritical);
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
        FooterNote.Text = "Applying isn't switched on yet: this beta only previews. Copy report keeps this list.";
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
