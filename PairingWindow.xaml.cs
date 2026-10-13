using System.IO;
using System.Windows;
using System.Windows.Media;
using ImageTools.Model;
using ImageTools.Treemap;
using Microsoft.Win32;

namespace ImageTools;

public sealed record PairingNote(string Glyph, Brush Brush, string Text);

/// <summary>
/// Previews a change to which PC folder a card folder is archived to: what Image Tools uses now,
/// what it would use afterwards, and what that means for the next import. Cancel is the default.
/// </summary>
public partial class PairingWindow : Window
{
    public PairingWindow(string heading, string message, (string Path, string Detail) before, (string Path, string Detail) after,
        List<PairingNote> notes, string? confirm)
    {
        InitializeComponent();
        HeadingText.Text = heading;
        MessageText.Text = message;
        (BeforePath.Text, BeforeDetail.Text) = before;
        (AfterPath.Text, AfterDetail.Text) = after;
        WarningList.ItemsSource = notes;
        WarningList.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = confirm;
        ConfirmButton.Visibility = confirm != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

/// <summary>
/// The user's corrections to card pairing: choose a card folder's archive folder, rule out a wrong
/// match, or go back to automatic. Each change is previewed before it's saved to the settings.
/// </summary>
public static class ArchivePairing
{
    private const string Warn = Theme.WarningGlyph, Info = "";

    /// <summary>Why the card's folders can't be paired by hand, or null when they can.</summary>
    public static string? CantPair(FsNode card) => CardSync.CanRemember(card)
        ? null
        : "This card reports no serial number, so a choice couldn't be remembered for it next time.";

    /// <summary>Asks for a folder, then previews pairing the card folder with it.</summary>
    public static bool Choose(Window owner, IReadOnlyList<FsNode> drives, FsNode card, FsNode folder, CardFolder? current)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Archive folder for {folder.Name} on camera card {card.DisplayName}",
            InitialDirectory = StartFolder(current),
        };
        if (dialog.ShowDialog(owner) != true)
            return false;
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.FolderName));

        string key = CardSync.Key(card, folder);
        var proposed = App.Settings.Pairings.Copy();
        proposed.Chosen[key] = path;
        proposed.Rejected.Remove(key); // a fresh choice starts over; the chosen folder may have been ruled out before

        var notes = new List<PairingNote>();
        bool blocked = false;
        var drive = drives.FirstOrDefault(d => d.Name.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase));
        if (CardSync.IsCard(drive) || IsRemovableCard(path))
        {
            notes.Add(new PairingNote(Warn, Theme.StatusCritical, "That folder is on a memory card. Choose a folder on the PC to archive the card's photos to."));
            blocked = true;
        }
        else if (drive == null || drive.NotScanned)
        {
            notes.Add(new PairingNote(Warn, Theme.StatusWarning,
                $"{Path.GetPathRoot(path)?.TrimEnd('\\')} isn't part of this scan, so Image Tools can't see which photos are already there " +
                "and every photo would be listed as new. Tick the drive in the Drives menu to check."));
        }
        if (current?.ArchivePath.Equals(path, StringComparison.OrdinalIgnoreCase) == true && current.Source != ArchiveSource.Chosen)
            notes.Add(new PairingNote(Info, Theme.SecondaryText, "That's the folder Image Tools found already. Choosing it keeps it, even if matching would pick another later."));

        return Confirm(owner, drives, card, folder, current, proposed,
            "Change archive folder", blocked ? null : "Use this folder", notes, checkAfter: !blocked);
    }

    /// <summary>Previews ruling out the folder a card folder is paired with now.</summary>
    public static bool RuleOut(Window owner, IReadOnlyList<FsNode> drives, FsNode card, CardFolder current)
    {
        string key = CardSync.Key(card, current.Folder);
        var proposed = App.Settings.Pairings.Copy();
        proposed.Chosen.Remove(key);
        if (!proposed.IsRejected(key, current.ArchivePath))
        {
            if (!proposed.Rejected.TryGetValue(key, out var list))
                proposed.Rejected[key] = list = [];
            list.Add(current.ArchivePath);
        }
        var notes = new List<PairingNote>();
        if (current.Shared > 0 && AfterChange(drives, card, current.Folder, proposed) != null)
            notes.Add(new PairingNote(Info, Theme.SecondaryText,
                $"The {Format.Count(current.Shared, "photo", "photos")} already in {Format.ShortPath(current.ArchivePath)} stay there. " +
                "They'd be listed as already elsewhere on this PC, and not ticked."));
        return Confirm(owner, drives, card, current.Folder, current, proposed, "Not the archive", "Rule it out", notes, checkAfter: false);
    }

    /// <summary>Previews forgetting the user's choice and ruled-out folders for a card folder.</summary>
    public static bool Reset(Window owner, IReadOnlyList<FsNode> drives, FsNode card, FsNode folder, CardFolder? current)
    {
        string key = CardSync.Key(card, folder);
        var proposed = App.Settings.Pairings.Copy();
        proposed.Chosen.Remove(key);
        proposed.Rejected.Remove(key);
        int ruledOut = App.Settings.Pairings.RejectedCount(key);
        var notes = new List<PairingNote>();
        if (ruledOut > 0)
            notes.Add(new PairingNote(Info, Theme.SecondaryText,
                $"Forgets {Format.Count(ruledOut, "folder", "folders")} you ruled out: " +
                string.Join(", ", App.Settings.CardArchiveRejects[key].Select(Format.ShortPath)) + "."));
        return Confirm(owner, drives, card, folder, current, proposed, "Let Image Tools choose", "Go back to automatic", notes, checkAfter: false);
    }

    /// <summary>Shows before and after; on confirm, saves the proposed choices.</summary>
    private static bool Confirm(Window owner, IReadOnlyList<FsNode> drives, FsNode card, FsNode folder, CardFolder? current,
        PairingChoices proposed, string heading, string? confirm, List<PairingNote> notes, bool checkAfter)
    {
        var after = AfterChange(drives, card, folder, proposed);
        int total = CardSync.Images(folder).Count();

        if (after == null)
            notes.Add(new PairingNote(Warn, Theme.StatusWarning,
                "Image Tools finds no other archive folder, so this card folder's photos won't be listed for import until you choose one."));
        else if (checkAfter && after.Archive != null && after.Shared == 0)
            notes.Add(new PairingNote(Warn, Theme.StatusWarning,
                "None of the photos in that folder are on this card. Check it's the right folder: every photo would be listed for import."));
        else if (checkAfter && after.Archive == null && Directory.Exists(after.ArchivePath))
            notes.Add(new PairingNote(Info, Theme.SecondaryText, "That folder has no photos in it yet, so every photo on the card would be listed for import."));
        if (checkAfter && after != null)
            foreach (var other in OtherUses(drives, card, folder, after.ArchivePath))
                notes.Add(new PairingNote(Info, Theme.SecondaryText,
                    $"It's also the archive of {other}. Both would import into it; a name that's taken gets a “ (2)” suffix."));

        var window = new PairingWindow(
            heading,
            $"{folder.FullPath} on camera card {card.DisplayName} – {Format.Count(total, "image", "images")}.",
            Describe(current, total), Describe(after, total), notes, confirm)
        { Owner = owner, Title = heading };
        if (window.ShowDialog() != true)
            return false;

        App.Settings.CardArchiveChoices = proposed.Chosen;
        App.Settings.CardArchiveRejects = proposed.Rejected;
        App.Settings.Save();
        return true;
    }

    /// <summary>How the card folder would be paired with <paramref name="proposed"/> choices; nothing is saved.</summary>
    private static CardFolder? AfterChange(IReadOnlyList<FsNode> drives, FsNode card, FsNode folder, PairingChoices proposed) =>
        CardSync.Find(drives, new Dictionary<string, string>(App.Settings.CardArchives), proposed, out _)
            .FirstOrDefault(s => s.Card == card)?.Folders.FirstOrDefault(f => f.Folder == folder);

    /// <summary>One side of the preview: the folder, how it was found, and what an import would list.</summary>
    private static (string Path, string Detail) Describe(CardFolder? pairing, int total)
    {
        if (pairing == null)
            return ("No archive folder", "This card folder's photos aren't listed for import.");
        bool exists = pairing.Archive != null || Directory.Exists(pairing.ArchivePath);
        string how = pairing.Source switch
        {
            ArchiveSource.Chosen => "Chosen by you.",
            ArchiveSource.Remembered => "Remembered from an earlier scan of this card.",
            ArchiveSource.NextToSiblings => "Next to where this card's other folders are archived.",
            _ => "Found automatically: it holds the same photos.",
        };
        string there = !exists ? "A new folder: nothing is there yet."
            : $"{Format.Count(pairing.Shared)} of {Format.Count(total, "image is", "images are")} already there.";
        int added = pairing.Count(ImportBucket.New), older = pairing.Count(ImportBucket.OlderGap), elsewhere = pairing.Count(ImportBucket.Elsewhere);
        var parts = new List<string>();
        if (added > 0)
            parts.Add($"{Format.Count(added)} new (ticked)");
        if (older > 0)
            parts.Add($"{Format.Count(older)} older");
        if (elsewhere > 0)
            parts.Add($"{Format.Count(elsewhere)} already elsewhere");
        string import = parts.Count > 0 ? $"An import would list {string.Join(", ", parts)}." : "Nothing left to import.";
        return (pairing.ArchivePath + (exists ? "" : "  (new folder)"), $"{how} {there} {import}");
    }

    /// <summary>Other card folders already archived to <paramref name="path"/>, e.g. "101CANON on card I:".</summary>
    private static IEnumerable<string> OtherUses(IReadOnlyList<FsNode> drives, FsNode card, FsNode folder, string path) =>
        CardSync.Find(drives, new Dictionary<string, string>(App.Settings.CardArchives), App.Settings.Pairings, out _)
            .SelectMany(s => s.Folders.Select(f => (s.Card, f)))
            .Where(x => x.f.Folder != folder && x.f.ArchivePath.Equals(path, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Card == card ? x.f.Folder.Name : $"{x.f.Folder.Name} on card {x.Card.DisplayName}");

    private static string StartFolder(CardFolder? current)
    {
        for (string? dir = current?.ArchivePath; dir != null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(dir))
                return dir;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    /// <summary>A removable drive with a DCIM folder, for drives that aren't in the scan.</summary>
    private static bool IsRemovableCard(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return root != null && new DriveInfo(root).DriveType == DriveType.Removable && Directory.Exists(Path.Join(root, "DCIM"));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
