using System.IO;
using System.Text;
using ImageTools.Model;

namespace ImageTools.Platform;

/// <summary>One file to copy: an image or a sidecar that travels with it.</summary>
/// <param name="Note">Why the target name differs from the source name, if it does.</param>
/// <param name="Image">The image on the card (null for sidecars), so its details can be cached for the copy.</param>
public sealed record PlannedCopy(string Source, string Target, long Bytes, string? Note, bool IsSidecar, FsNode? Image = null);

public sealed record ImportPlan(
    string CardName,
    List<string> NewFolders,
    List<PlannedCopy> Copies,
    long Bytes,
    List<(string Drive, long Free)> Space)
{
    public int Renamed => Copies.Count(c => c.Note != null);
    public int Images => Copies.Count(c => !c.IsSidecar);
    public int Sidecars => Copies.Count(c => c.IsSidecar);
}

/// <summary>
/// Turns a selection of card images into the exact list of changes an import would make:
/// folders to create, every copy with its final name, and any renames. Read-only: it only lists
/// what's on disk. Nothing is ever overwritten: a name that's taken gets a " (2)" suffix.
/// </summary>
public static class ImportPlanner
{
    /// <summary>Files that belong with an image: edit settings, camera thumbnails, voice memos.</summary>
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xmp", ".thm", ".aae", ".wav", ".dop", ".pp3",
    };

    public static ImportPlan Build(string cardName, IEnumerable<(CardFolder Folder, List<ImportItem> Items)> selection)
    {
        var newFolders = new List<string>();
        var copies = new List<PlannedCopy>();

        foreach (var (folder, items) in selection)
        {
            if (items.Count == 0)
                continue;
            string target = folder.ArchivePath;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(target))
                foreach (var f in Directory.EnumerateFileSystemEntries(target))
                    taken.Add(Path.GetFileName(f));
            else
                newFolders.Add(target);

            // Sidecars on the card, by name without their last extension: "IMG_0001" or "IMG_0001.CR2".
            string source = folder.Folder.FullPath;
            var sidecars = Directory.Exists(source)
                ? Directory.EnumerateFiles(source)
                    .Where(f => SidecarExtensions.Contains(Path.GetExtension(f)))
                    .ToLookup(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<string>().ToLookup(f => f);
            var usedSidecars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items.OrderBy(i => i.Image.Name, StringComparer.OrdinalIgnoreCase))
            {
                string name = item.Image.Name;
                string? note = null;
                if (taken.Contains(name))
                {
                    note = $"renamed: a different “{name}” is already in the archive folder";
                    name = FreeName(name, taken);
                }
                taken.Add(name);
                copies.Add(new PlannedCopy(item.Image.FullPath, Path.Join(target, name), item.Image.Size, note, false, item.Image));

                // Sidecars follow the image's (possibly new) name.
                string oldBase = Path.GetFileNameWithoutExtension(item.Image.Name), newBase = Path.GetFileNameWithoutExtension(name);
                foreach (var (key, newKey) in new[] { (item.Image.Name, name), (oldBase, newBase) })
                {
                    foreach (var sidecar in sidecars[key])
                    {
                        if (!usedSidecars.Add(sidecar))
                            continue;
                        string sidecarName = newKey + Path.GetExtension(sidecar);
                        string? sidecarNote = note != null ? "renamed to follow its image" : null;
                        if (taken.Contains(sidecarName))
                        {
                            sidecarNote = $"renamed: a different “{sidecarName}” is already in the archive folder";
                            sidecarName = FreeName(sidecarName, taken);
                        }
                        taken.Add(sidecarName);
                        copies.Add(new PlannedCopy(sidecar, Path.Join(target, sidecarName), new FileInfo(sidecar).Length, sidecarNote, true));
                    }
                }
            }
        }

        var space = copies
            .GroupBy(c => Path.GetPathRoot(c.Target)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Drive: g.Key, Free: FreeSpace(g.Key)))
            .ToList();
        return new ImportPlan(cardName, newFolders, copies, copies.Sum(c => c.Bytes), space);
    }

    /// <summary>"IMG_0001.CR2" → "IMG_0001 (2).CR2", or the next number that's free.</summary>
    private static string FreeName(string name, HashSet<string> taken)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} ({n}){ext}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static long FreeSpace(string root)
    {
        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>The plan as plain text, for the clipboard or a log.</summary>
    public static string Report(ImportPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Image Tools import preview  ·  {DateTime.Now:d MMM yyyy HH:mm}");
        sb.AppendLine($"From camera card {plan.CardName}");
        sb.AppendLine($"Copy {Format.Count(plan.Images, "image", "images")}"
                      + (plan.Sidecars > 0 ? $" and {Format.Count(plan.Sidecars, "sidecar file", "sidecar files")}" : "")
                      + $"  ·  {Format.Bytes(plan.Bytes)}");
        foreach (var (drive, free) in plan.Space)
            sb.AppendLine($"{drive.TrimEnd('\\')} has {(free < 0 ? "unknown" : Format.Bytes(free))} free");
        sb.AppendLine("Nothing on the card is changed or deleted. Nothing in the archive is overwritten.");
        sb.AppendLine();
        foreach (var folder in plan.NewFolders)
            sb.AppendLine($"Create folder\t{folder}");
        foreach (var c in plan.Copies)
            sb.AppendLine($"Copy\t{c.Source}\t→\t{c.Target}" + (c.Note != null ? $"\t({c.Note})" : ""));
        return sb.ToString();
    }
}
