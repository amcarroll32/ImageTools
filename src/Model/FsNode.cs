using System.IO;

namespace ImageTools.Model;

public enum NodeKind
{
    Root,        // "This PC" – parent of all drives
    Drive,
    Directory,
    File,        // always an image; other files aren't kept
}

/// <summary>Where an image stands after the duplicate check.</summary>
public enum DupStatus : byte
{
    NotChecked,  // app asset, online-only, or the check hasn't reached it yet
    Unique,
    Duplicate,   // at least one byte-identical copy exists elsewhere
    Unreadable,  // the file couldn't be opened to compare it
}

/// <summary>
/// One box in the treemap. Only images and the folders that lead to them are kept.
/// Sizes and counts are rolled up bottom-up once a drive finishes.
/// </summary>
public sealed class FsNode
{
    public FsNode(string name, NodeKind kind, FsNode? parent)
    {
        Name = name;
        Kind = kind;
        Parent = parent;
    }

    public string Name { get; }
    public NodeKind Kind { get; }
    public FsNode? Parent { get; }

    /// <summary>Bytes of images inside, app assets included.</summary>
    public long Size { get; set; }

    /// <summary>Bytes of app-asset images inside (hidden unless "App assets" is ticked).</summary>
    public long AssetSize { get; set; }

    /// <summary>Images inside, app assets included.</summary>
    public long FileCount { get; set; }

    public long AssetCount { get; set; }
    public long DirCount { get; set; }

    /// <summary>Last-modified time (UTC ticks); for folders, the newest image inside. 0 = unknown.</summary>
    public long LastWrite { get; set; }

    public List<FsNode>? Children { get; set; }

    // ---- Images ----

    public ImageFormat Format { get; set; }
    public Library Library { get; set; }

    /// <summary>
    /// Files: an icon, texture or other app/game image, hidden by default.
    /// Folders: everything inside is treated as app assets.
    /// </summary>
    public bool IsAsset { get; set; }

    /// <summary>Why this folder (or tiny file) counts as app assets; set where the rule matched.</summary>
    public string? AssetReason { get; set; }

    /// <summary>A cloud placeholder (OneDrive etc.). Never opened, since reading it would download it.</summary>
    public bool IsCloudOnly { get; set; }

    public DupStatus Dup { get; set; }
    public DuplicateGroup? Group { get; set; }

    /// <summary>Date taken, camera, tags… read from Windows' property system. Null until read.</summary>
    public PhotoInfo? Photo { get; set; }

    /// <summary>Folders: images inside that have a copy somewhere else, and their bytes.</summary>
    public long DupCount { get; set; }
    public long DupBytes { get; set; }

    /// <summary>A folder whose library differs from its parent's, e.g. the Screenshots folder.</summary>
    public bool IsLibraryRoot { get; set; }

    // ---- Folders and drives ----

    /// <summary>The directory itself could not be listed.</summary>
    public bool AccessDenied { get; set; }

    /// <summary>Number of unreadable folders anywhere below this node.</summary>
    public long DeniedCount { get; set; }

    public string? Label { get; set; }

    /// <summary>Drive capacity and free space as reported by Windows (drives only).</summary>
    public long Capacity { get; set; }
    public long FreeSize { get; set; }

    public bool IsRemovable { get; set; }

    /// <summary>Removable volume with a DCIM folder at its root, i.e. almost certainly a camera card.</summary>
    public bool HasDcimFolder { get; set; }

    /// <summary>Volume serial number (removable drives only), so a camera card is recognized next time.</summary>
    public uint VolumeSerial { get; set; }

    /// <summary>A memory card whose camera folders are paired with archive folders on the PC: the photos' original source.</summary>
    public bool IsSourceCard { get; set; }

    /// <summary>
    /// Folders on either side of a card pairing, e.g. "archived in E:\…\Card2\100CANON" on the card
    /// and "archive of card I:" on the PC; shown in the folder's header.
    /// </summary>
    public string? PairNote { get; set; }

    /// <summary>Camera cards: "33 new to import" or "Archived"; drawn in the drive's title band.</summary>
    public string? ImportBadge { get; set; }
    public bool ImportBadgeGood { get; set; }

    /// <summary>Physical disk, device kind and health (drives only; null until the disk query finishes).</summary>
    public DriveHardware? Hardware { get; set; }

    /// <summary>Drives only: local disk, network share, or cloud/virtual drive.</summary>
    public DriveSource Source { get; set; }

    /// <summary>
    /// A scanning drive's used space: sizes its tile (so it shows up and can show progress)
    /// without counting as images.
    /// </summary>
    public long PendingSize { get; set; }

    public bool IsContentOnlyDrive => Kind == NodeKind.Drive && Source != DriveSource.Local;

    /// <summary>A placeholder for a drive that was skipped (network, cloud, or unticked by the user).</summary>
    public bool NotScanned { get; set; }

    // Drive scan state (UI thread only).
    public bool IsScanning { get; set; }
    public double ScanProgress { get; set; }
    public string? ScanError { get; set; }

    public bool IsContainer => Kind is NodeKind.Root or NodeKind.Drive or NodeKind.Directory;

    public string DisplayName => Kind switch
    {
        NodeKind.Drive when !string.IsNullOrEmpty(Label) => $"{Name.TrimEnd('\\')} {Label}",
        NodeKind.Drive => Name.TrimEnd('\\'),
        _ => Name,
    };

    public string FullPath => Kind switch
    {
        NodeKind.Root => Name,
        NodeKind.Drive => Name,
        _ => Path.Join(Parent!.FullPath, Name),
    };

    public bool HasRealPath => Kind is NodeKind.Drive or NodeKind.Directory or NodeKind.File;

    public long VisibleSize(bool showAssets) => showAssets ? Size : Size - AssetSize;

    public long VisibleCount(bool showAssets) => showAssets ? FileCount : FileCount - AssetCount;

    /// <summary>Date taken when the photo has one, otherwise last modified.</summary>
    public long DateTicks => Photo is { DateTaken: > 0 } p ? p.DateTaken : LastWrite;

    /// <summary>The rule that made this image (or folder) an app asset, from the nearest folder that matched.</summary>
    public string? EffectiveAssetReason
    {
        get
        {
            for (var n = this; n != null; n = n.Parent)
                if (n.AssetReason != null)
                    return n.AssetReason;
            return null;
        }
    }

    public FsNode? Drive
    {
        get
        {
            for (var n = this; n != null; n = n.Parent)
                if (n.Kind == NodeKind.Drive)
                    return n;
            return null;
        }
    }

    public IEnumerable<FsNode> Ancestors()
    {
        for (var n = Parent; n != null; n = n.Parent)
            yield return n;
    }

    public bool IsAncestorOf(FsNode node) => node.Ancestors().Contains(this);

    /// <summary>This node is <paramref name="view"/> or inside it.</summary>
    public bool IsUnder(FsNode view)
    {
        if (view.Kind == NodeKind.Root)
            return true;
        for (var n = this; n != null; n = n.Parent)
            if (n == view)
                return true;
        return false;
    }
}
