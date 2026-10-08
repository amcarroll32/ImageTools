using System.Text.RegularExpressions;

namespace ImageTools.Model;

/// <summary>
/// Where an image seems to come from, judged by its folder (and a few file-name patterns).
/// One per categorical palette slot; order matches the palette in <see cref="Treemap.Theme"/>.
/// </summary>
public enum Library
{
    Camera,
    Screenshots,
    Messaging,
    Downloads,
    Pictures,
    Desktop,
    Backups,
    RecycleBin,
    Other,
}

/// <summary>
/// Folder-name rules for libraries and for app assets. Deliberately simple and explainable:
/// every asset decision carries a reason that the tooltip shows.
/// </summary>
public static partial class Classifier
{
    /// <summary>Images smaller than this are icons, buttons and thumbnails, not photos.</summary>
    public const long TinyImage = 4 * 1024;

    // ---- Asset reasons (shown in tooltips as "Hidden as an app asset: …") ----
    public const string WindowsReason = "Windows system folder";
    public const string ProgramsReason = "installed programs";
    public const string AppDataReason = "app data (AppData)";
    public const string GamesReason = "game library";
    public const string DevReason = "developer tools and packages";
    public const string ProjectReason = "inside a code or game project";
    public const string CacheReason = "cache, temp or thumbnail folder";
    public const string TinyReason = "under 4 KB (icons and thumbnails)";

    public static string DisplayName(Library library) => library switch
    {
        Library.Camera => "Camera",
        Library.Screenshots => "Screenshots",
        Library.Messaging => "Messaging apps",
        Library.Downloads => "Downloads",
        Library.Pictures => "Pictures & photos",
        Library.Desktop => "Desktop & documents",
        Library.Backups => "Backups & exports",
        Library.RecycleBin => "Recycle Bin",
        _ => "Other folders",
    };

    private static readonly HashSet<string> RootSystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "$SysReset", "$GetCurrent",
        "System Volume Information", "Recovery", "PerfLogs", "MSOCache", "Config.Msi", "drivers", "inetpub",
        "Intel", "AMD", "NVIDIA", "OneDriveTemp",
    };

    private static readonly HashSet<string> RootProgramFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "ProgramData", "Games",
    };

    private static readonly HashSet<string> GameFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "steamapps", "SteamLibrary", "Steam", "Epic Games", "GOG Galaxy", "GOG Games", "Origin Games", "EA Games",
        "Ubisoft", "Ubisoft Game Launcher", "Riot Games", "Battle.net", "XboxGames", "WindowsApps", "ModifiableWindowsApps",
    };

    private static readonly HashSet<string> DevFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components", ".git", ".svn", ".hg", "site-packages", "dist-packages", "__pycache__",
        ".venv", "venv", ".nuget", ".gradle", ".m2", ".cargo", ".rustup", ".npm", ".yarn", ".vscode", ".vs", ".idea",
        ".android", ".conda", "anaconda3", "miniconda3", "Windows Kits", "Microsoft Visual Studio", "Microsoft SDKs",
        "msys64", "cygwin64", "Android Studio",
    };

    private static readonly HashSet<string> CacheFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache", ".cache", "caches", "Cache_Data", "temp", "tmp", ".thumbnails", "thumbnails", "$Recycle.Bin.tmp",
    };

    /// <summary>Folders and files that mark a code or game project; images inside are its assets.</summary>
    private static readonly HashSet<string> ProjectMarkerDirs = new(StringComparer.OrdinalIgnoreCase) { ".git", "ProjectSettings" };

    private static readonly HashSet<string> ProjectMarkerFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "project.godot", "package.json", "Cargo.toml", "pom.xml", "build.gradle", "CMakeLists.txt", "pyproject.toml", "go.mod",
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> MarkerDirLookup = ProjectMarkerDirs.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> MarkerFileLookup = ProjectMarkerFiles.GetAlternateLookup<ReadOnlySpan<char>>();

    public static bool IsProjectMarkerDir(ReadOnlySpan<char> name) => MarkerDirLookup.Contains(name);

    public static bool IsProjectMarkerFile(ReadOnlySpan<char> name) =>
        MarkerFileLookup.Contains(name)
        || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A project marker only counts below the top of a profile: a .git in C:\ or C:\Users\name
    /// (a dotfiles repo) mustn't hide every picture on the PC.
    /// </summary>
    public static bool ProjectMarkerApplies(FsNode dir, int depth) =>
        depth >= 2 && !(depth == 2 && string.Equals(dir.Parent?.Name, "Users", StringComparison.OrdinalIgnoreCase));

    /// <summary>The reason a folder's contents are app assets, or null. <paramref name="depth"/> is 1 for a drive's top-level folders.</summary>
    public static string? AssetReason(string name, int depth)
    {
        if (depth == 1)
        {
            if (RootSystemFolders.Contains(name)) return WindowsReason;
            if (RootProgramFolders.Contains(name)) return ProgramsReason;
        }
        if (name.Equals("AppData", StringComparison.OrdinalIgnoreCase)) return AppDataReason;
        if (GameFolders.Contains(name)) return GamesReason;
        if (DevFolders.Contains(name) || PythonFolder().IsMatch(name)) return DevReason;
        if (CacheFolders.Contains(name)) return CacheReason;
        return null;
    }

    /// <summary>Messaging apps keep received photos under AppData; those are the user's, not the app's.</summary>
    public static bool IsUserMediaInsideAppData(string name) =>
        name.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) || name.Contains("Telegram", StringComparison.OrdinalIgnoreCase);

    /// <summary>The library a folder belongs to: the deepest matching rule wins, except the Recycle Bin, which sticks.</summary>
    public static Library ForDirectory(string name, Library parent)
    {
        if (parent == Library.RecycleBin)
            return parent;
        if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || name.Equals("RECYCLER", StringComparison.OrdinalIgnoreCase))
            return Library.RecycleBin;
        if (ScreenshotFolder().IsMatch(name))
            return Library.Screenshots;
        if (name.Equals("DCIM", StringComparison.OrdinalIgnoreCase) || CameraFolder().IsMatch(name) || DcfFolder().IsMatch(name))
            return Library.Camera;
        if (MessagingFolder().IsMatch(name))
            return Library.Messaging;
        if (BackupFolder().IsMatch(name))
            return Library.Backups;
        if (name.Equals("Downloads", StringComparison.OrdinalIgnoreCase))
            return Library.Downloads;
        if (PicturesFolder().IsMatch(name))
            return Library.Pictures;
        if (DesktopFolder().IsMatch(name))
            return Library.Desktop;
        return parent;
    }

    /// <summary>File names that give the source away wherever they're saved, e.g. "Screenshot 2024-…png" in Downloads.</summary>
    public static Library ForFile(ReadOnlySpan<char> name, Library folder)
    {
        if (folder is Library.RecycleBin or Library.Screenshots)
            return folder;
        if (name.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Screen Shot", StringComparison.OrdinalIgnoreCase))
            return Library.Screenshots;
        if (WhatsAppFile().IsMatch(name))
            return Library.Messaging;
        return folder;
    }

    [GeneratedRegex(@"^(screenshots?|screen ?shots|screen ?captures?|screencaps?|captures|sharex|greenshot|lightshot|snips?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenshotFolder();

    [GeneratedRegex(@"^(camera( roll| uploads| imports)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex CameraFolder();

    /// <summary>Camera folders under DCIM: three digits and five letters, e.g. 100CANON, 101APPLE, 100MSDCF.</summary>
    [GeneratedRegex(@"^\d{3}[A-Z0-9_]{5}$", RegexOptions.IgnoreCase)]
    private static partial Regex DcfFolder();

    [GeneratedRegex(@"whatsapp|telegram|^signal$|^messenger$|^wechat( files)?$|^viber$", RegexOptions.IgnoreCase)]
    private static partial Regex MessagingFolder();

    [GeneratedRegex(@"backup|takeout|^(google|icloud) photos$|^archives?$|^old (photos|pictures)$|mobilesync", RegexOptions.IgnoreCase)]
    private static partial Regex BackupFolder();

    [GeneratedRegex(@"^(my )?(pictures|photos)$|^photography$", RegexOptions.IgnoreCase)]
    private static partial Regex PicturesFolder();

    [GeneratedRegex(@"^(desktop|(my )?documents)$", RegexOptions.IgnoreCase)]
    private static partial Regex DesktopFolder();

    [GeneratedRegex(@"^IMG-\d{8}-WA\d+", RegexOptions.IgnoreCase)]
    private static partial Regex WhatsAppFile();

    [GeneratedRegex(@"^Python\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex PythonFolder();
}
