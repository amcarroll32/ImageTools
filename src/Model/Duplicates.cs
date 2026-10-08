namespace ImageTools.Model;

/// <summary>Details from Windows' property system, kept for a future Consolidate step.</summary>
/// <param name="DateTaken">UTC ticks; 0 when the image has no date taken.</param>
/// <param name="Rating">Stars, 0–5 (Windows stores 1–99).</param>
/// <param name="Tags">Windows tags (keywords), joined with "; ".</param>
/// <param name="Orientation">EXIF orientation: 1 normal, 3 upside down, 6 and 8 turned a quarter.</param>
public sealed record PhotoInfo(long DateTaken, string? Camera, int Width, int Height, int Rating, string? Tags, int Orientation)
{
    public static readonly PhotoInfo Empty = new(0, null, 0, 0, 0, null, 0);

    public string Stars => Rating > 0 ? new string('★', Rating) + new string('☆', 5 - Rating) : "";
}

/// <summary>Byte-identical images.</summary>
public sealed class DuplicateGroup(long size, List<FsNode> members)
{
    public long Size { get; } = size;
    public List<FsNode> Members { get; } = members;

    /// <summary>Space taken by the extra copies.</summary>
    public long Extra => Size * (Members.Count - 1);
}

public enum MatchKind
{
    Identical,  // both folders hold exactly the same images
    Contained,  // every image in A is also in B, which has more
    Partial,    // some of A's images are in B
}

/// <summary>Two folders that share images. A is the folder whose images are (mostly) in B.</summary>
public sealed record FolderMatch(
    FsNode A,
    FsNode B,
    MatchKind Kind,
    int SharedInA,
    int CountA,
    int CountB,
    long SharedBytes);

public sealed record DupResult(
    List<DuplicateGroup> Groups,
    List<FolderMatch> Matches,
    long DuplicateFiles,
    long ExtraBytes,
    long Unreadable,
    long Checked);
