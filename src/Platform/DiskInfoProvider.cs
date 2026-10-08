using System.Management;
using ImageTools.Model;

namespace ImageTools.Platform;

/// <param name="Succeeded">False when Windows couldn't be asked; nothing can then be concluded from a missing letter.</param>
/// <param name="ByLetter">Drive letter → the physical disk it lives on.</param>
/// <param name="PartitionLetters">Every drive letter backed by a real disk partition (including Storage Spaces and VHDs).</param>
public sealed record DiskQuery(bool Succeeded, Dictionary<char, PhysicalDisk> ByLetter, HashSet<char> PartitionLetters);

/// <summary>
/// Reads physical disks and their partitions from the Windows Storage Management API
/// (the same data as Get-PhysicalDisk / Get-Partition). Works without admin rights.
/// </summary>
public static class DiskInfoProvider
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    /// <summary>Maps drive letters to partitions and physical disks.</summary>
    public static DiskQuery Query()
    {
        try
        {
            var scope = new ManagementScope(StorageNamespace);
            scope.Connect();
            // A hung device shouldn't stall the app forever.
            var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(20) };

            var letters = new Dictionary<int, List<char>>();
            var partitionLetters = new HashSet<char>();
            foreach (var p in Select(scope, options, "SELECT DriveLetter, DiskNumber FROM MSFT_Partition"))
            {
                if (p["DriveLetter"] is not char letter || letter == '\0' || p["DiskNumber"] is not uint diskNumber)
                    continue;
                if (!letters.TryGetValue((int)diskNumber, out var list))
                    letters[(int)diskNumber] = list = [];
                list.Add(char.ToUpperInvariant(letter));
                partitionLetters.Add(char.ToUpperInvariant(letter));
            }

            var byLetter = new Dictionary<char, PhysicalDisk>();
            foreach (var d in Select(scope, options, "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus FROM MSFT_PhysicalDisk"))
            {
                if (!int.TryParse(d["DeviceId"] as string, out int number) || !letters.TryGetValue(number, out var diskLetters))
                    continue;
                diskLetters.Sort();
                var disk = new PhysicalDisk(
                    number,
                    (d["FriendlyName"] as string)?.Trim() ?? $"Disk {number}",
                    MediaTypeName(d["MediaType"]),
                    BusTypeName(d["BusType"]),
                    HealthFrom(d["HealthStatus"]),
                    diskLetters);
                foreach (char l in diskLetters)
                    byLetter[l] = disk;
            }
            return new DiskQuery(true, byLetter, partitionLetters);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                                       or TimeoutException or PlatformNotSupportedException)
        {
            return new DiskQuery(false, [], []);
        }
    }

    private static IEnumerable<ManagementBaseObject> Select(ManagementScope scope, EnumerationOptions options, string query)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query), options);
        using var results = searcher.Get();
        foreach (var item in results)
            yield return item;
    }

    private static string MediaTypeName(object? value) => Convert.ToInt32(value ?? 0) switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "",
    };

    private static string BusTypeName(object? value) => Convert.ToInt32(value ?? 0) switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 or 15 => "Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        19 => "UFS",
        _ => "",
    };

    private static DiskHealth HealthFrom(object? value) => Convert.ToInt32(value ?? 5) switch
    {
        0 => DiskHealth.Healthy,
        1 => DiskHealth.Warning,
        2 => DiskHealth.Unhealthy,
        _ => DiskHealth.Unknown,
    };
}
