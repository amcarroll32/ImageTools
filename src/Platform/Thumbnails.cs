using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace ImageTools.Platform;

/// <summary>
/// Small previews for the map tooltip, decoded off the UI thread with Windows' imaging codecs
/// (HEIC, WebP and RAW need the matching Store extensions). Keeps the last few in memory.
/// Online-only files are never previewed, since opening them would download them.
/// </summary>
public static class Thumbnails
{
    public const int Width = 240;
    private const int Keep = 64;
    private const long MaxBytes = 200L * 1024 * 1024;

    private static readonly Dictionary<string, BitmapSource?> Recent = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Order = new();

    /// <summary>UI thread only. A cached null means the file couldn't be decoded.</summary>
    public static bool TryGetCached(string path, out BitmapSource? image) => Recent.TryGetValue(path, out image);

    /// <summary>UI thread only.</summary>
    public static void Remember(string path, BitmapSource? image)
    {
        if (Recent.ContainsKey(path))
            return;
        Recent[path] = image;
        Order.Enqueue(path);
        while (Order.Count > Keep)
            Recent.Remove(Order.Dequeue());
    }

    public static Task<BitmapSource?> LoadAsync(string path, long size) => size > MaxBytes
        ? Task.FromResult<BitmapSource?>(null)
        : Task.Run(() => Load(path));

    private static BitmapSource? Load(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = Width;
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
