using System.Text.RegularExpressions;

namespace ImageTools.Model;

/// <summary>What kind of device a drive letter lives on; picks the drive icon.</summary>
public enum DriveKind
{
    Unknown,
    Hdd,
    Ssd,
    UsbDrive,
    MemoryCard,
    Network,
    Cloud,
}

/// <summary>
/// Where a drive letter's data really lives. Cloud-sync drives (Google Drive, pCloud…) and
/// subst aliases claim to be local disks, but no disk partition backs them.
/// </summary>
public enum DriveSource
{
    Local,
    Network,
    Virtual,
}

/// <summary>Windows' own health verdict for a physical disk (MSFT_PhysicalDisk.HealthStatus).</summary>
public enum DiskHealth
{
    Unknown,
    Healthy,
    Warning,
    Unhealthy,
}

/// <summary>A physical disk and the drive letters of the partitions on it.</summary>
public sealed record PhysicalDisk(
    int Number,
    string Model,
    string MediaType, // "SSD", "HDD", "SCM" or ""
    string Bus,       // "NVMe", "SATA", "USB", "SD", …
    DiskHealth Health,
    IReadOnlyList<char> Letters);

/// <summary>Hardware details attached to a drive node once the disk query finishes.</summary>
public sealed record DriveHardware(DriveKind Kind, PhysicalDisk? Disk, char Letter, string? NetworkPath = null)
{
    private static readonly Regex CardReaderName = new(
        @"\b(sd|sdhc|sdxc|micro\s?sd|mmc|cf|xd|card|reader|multi-?card|memory\s?stick)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DiskHealth Health => Disk?.Health ?? DiskHealth.Unknown;

    /// <summary>Other drive letters on the same physical disk.</summary>
    public IEnumerable<char> SharedWith => Disk?.Letters.Where(l => l != Letter) ?? [];

    /// <summary>
    /// SD and MMC buses are unambiguous. Cards in USB readers look like any USB stick, so a reader
    /// with a card-ish name, or a removable volume with a camera's DCIM folder, counts as a card.
    /// </summary>
    public static DriveHardware Classify(char letter, DriveSource source, string? networkPath, PhysicalDisk? disk, bool removable, bool hasDcimFolder)
    {
        if (source == DriveSource.Network)
            return new DriveHardware(DriveKind.Network, null, letter, networkPath);
        if (source == DriveSource.Virtual)
            return new DriveHardware(DriveKind.Cloud, null, letter);

        string bus = disk?.Bus ?? "";
        string media = disk?.MediaType ?? "";

        DriveKind kind;
        if (bus is "SD" or "MMC")
            kind = DriveKind.MemoryCard;
        else if (bus == "USB" || removable)
        {
            if (hasDcimFolder || (disk != null && CardReaderName.IsMatch(disk.Model)))
                kind = DriveKind.MemoryCard;
            else if (!removable && media is "SSD" or "SCM")
                kind = DriveKind.Ssd;   // external USB SSD
            else if (!removable && media == "HDD")
                kind = DriveKind.Hdd;   // external USB hard drive
            else
                kind = DriveKind.UsbDrive;
        }
        else
        {
            kind = media switch
            {
                "SSD" or "SCM" => DriveKind.Ssd,
                "HDD" => DriveKind.Hdd,
                _ => DriveKind.Unknown,
            };
        }
        return new DriveHardware(kind, disk, letter);
    }

    /// <summary>E.g. "NVMe SSD", "SATA hard drive", "USB drive", "Memory card".</summary>
    public string KindLabel => Kind switch
    {
        DriveKind.Ssd => string.IsNullOrEmpty(Disk?.Bus) ? "SSD" : $"{Disk!.Bus} SSD",
        DriveKind.Hdd => string.IsNullOrEmpty(Disk?.Bus) ? "Hard drive" : $"{Disk!.Bus} hard drive",
        DriveKind.UsbDrive => "USB drive",
        DriveKind.MemoryCard => "Memory card",
        DriveKind.Network => "Network drive",
        DriveKind.Cloud => "Cloud or virtual drive",
        _ => "Drive",
    };

    public string HealthLabel => Health switch
    {
        DiskHealth.Healthy => "Healthy",
        DiskHealth.Warning => "Warning",
        DiskHealth.Unhealthy => "Unhealthy",
        _ => "Health not reported",
    };

    /// <summary>One line describing the physical disk, e.g. "Disk 0 · PNY SSD · SATA SSD · also holds F:".</summary>
    public string DiskLine
    {
        get
        {
            if (Kind == DriveKind.Network)
                return NetworkPath != null ? $"Network drive  ·  {NetworkPath}" : "Network drive";
            if (Kind == DriveKind.Cloud)
                return "Cloud or virtual drive (no local disk behind it)  ·  sizes may include files stored only online";
            if (Disk == null)
                return KindLabel;
            string line = $"Disk {Disk.Number}  ·  {Disk.Model}  ·  {KindLabel}";
            var others = SharedWith.ToList();
            if (others.Count > 0)
                line += $"  ·  also holds {string.Join(", ", others.Select(l => l + ":"))}";
            return line;
        }
    }
}
