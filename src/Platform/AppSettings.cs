using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ImageTools.Platform;

/// <summary>
/// User preferences. Stored next to the exe (ImageTools.settings.json) so they travel with
/// the portable app; when that folder isn't writable they go to %LocalAppData%\ImageTools.
/// </summary>
public sealed class AppSettings
{
    private static readonly string PortablePath = Path.Combine(AppContext.BaseDirectory, "ImageTools.settings.json");

    private static readonly string LocalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageTools", "settings.json");

    /// <summary>"Light", "Dark", or null to follow Windows.</summary>
    public string? Theme { get; set; }

    /// <summary>Scan network and cloud/virtual drives too (off by default: local drives only).</summary>
    public bool IncludeNetworkDrives { get; set; }

    /// <summary>Per-drive overrides, e.g. {"G:": true, "D:": false}; drives not listed follow the default.</summary>
    public Dictionary<string, bool> DriveChoices { get; set; } = [];

    /// <summary>
    /// Camera card folder → archive folder pairings found on earlier scans, keyed by
    /// "volume serial-capacity|folder path on the card" (e.g. "01234567-62GB|DCIM\100CANON").
    /// </summary>
    public Dictionary<string, string> CardArchives { get; set; } = [];

    public static AppSettings Load()
    {
        // The portable file wins; the per-user file covers read-only locations.
        foreach (var path in new[] { PortablePath, LocalPath })
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Unreadable or corrupt settings: try the next location, then fall back to defaults.
            }
        }
        return new AppSettings();
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(this);
        if (TryWrite(PortablePath, json))
            return;
        TryWrite(LocalPath, json);
        // If neither works, not remembering a preference isn't worth interrupting anyone over.
    }

    private static bool TryWrite(string path, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [JsonIgnore]
    public bool PrefersDark => Theme switch
    {
        "Light" => false,
        "Dark" => true,
        _ => WindowsAppsUseDarkTheme(),
    };

    private static bool WindowsAppsUseDarkTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int light
        && light == 0;
}
