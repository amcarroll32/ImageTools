using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace ImageTools.Platform;

/// <summary>
/// Previews for the map tooltip and the import compare panel, decoded off the UI thread with
/// Windows' imaging codecs (HEIC, WebP and RAW need the matching Store extensions). Keeps the
/// last few in memory. Online-only files are never previewed, since opening them would download them.
/// </summary>
public static class Thumbnails
{
    /// <summary>Tooltip size.</summary>
    public const int Width = 240;

    /// <summary>Compare panel size: two of these sit side by side.</summary>
    public const int CompareWidth = 480;

    private const int Keep = 64;
    private const long MaxBytes = 200L * 1024 * 1024;

    private static readonly Dictionary<string, BitmapSource?> Recent = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Order = new();

    private static string Key(string path, int width) => $"{width}|{path}";

    /// <summary>UI thread only. A cached null means the file couldn't be decoded.</summary>
    public static bool TryGetCached(string path, out BitmapSource? image, int width = Width) =>
        Recent.TryGetValue(Key(path, width), out image);

    /// <summary>UI thread only.</summary>
    public static void Remember(string path, BitmapSource? image, int width = Width)
    {
        string key = Key(path, width);
        if (Recent.ContainsKey(key))
            return;
        Recent[key] = image;
        Order.Enqueue(key);
        while (Order.Count > Keep)
            Recent.Remove(Order.Dequeue());
    }

    public static Task<BitmapSource?> LoadAsync(string path, long size, int width = Width) => size > MaxBytes
        ? Task.FromResult<BitmapSource?>(null)
        : Task.Run(() => Load(path, width));

    private static BitmapSource? Load(string path, int width)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = width;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                       or ArgumentException or COMException or OverflowException)
        {
            return null;
        }
    }
}
