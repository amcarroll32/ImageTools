namespace ImageTools.Model;

/// <summary>
/// Image formats in scope, one per categorical palette slot; order matches the palette in
/// <see cref="Treemap.Theme"/>. SVG, ICO, DDS, TGA, EXR and HDR are deliberately left out:
/// they're nearly always app or game assets.
/// </summary>
public enum ImageFormat
{
    Jpeg,
    Png,
    Heic,
    Raw,
    WebpAvif,
    Gif,
    Tiff,
    Psd,
    Other,
}

public static class ImageFormats
{
    private static readonly Dictionary<string, ImageFormat> Map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageFormat>.AlternateLookup<ReadOnlySpan<char>> Lookup;

    static ImageFormats()
    {
        Add(ImageFormat.Jpeg, ".jpg .jpeg .jpe .jfif");
        Add(ImageFormat.Png, ".png");
        Add(ImageFormat.Heic, ".heic .heif .hif");
        Add(ImageFormat.Raw, ".cr2 .cr3 .crw .nef .nrw .arw .srf .sr2 .dng .orf .rw2 .raf .pef .srw .x3f .3fr .iiq .rwl .mrw .erf .kdc .dcr .mos .raw");
        Add(ImageFormat.WebpAvif, ".webp .avif");
        Add(ImageFormat.Gif, ".gif");
        Add(ImageFormat.Tiff, ".tif .tiff");
        Add(ImageFormat.Psd, ".psd .psb");
        Add(ImageFormat.Other, ".bmp .dib .jxl .jp2 .j2k .jxr .wdp");
        Lookup = Map.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    private static void Add(ImageFormat format, string extensions)
    {
        foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Map.TryAdd(ext, format);
    }

    public static bool TryGet(ReadOnlySpan<char> fileName, out ImageFormat format)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            format = default;
            return false;
        }
        return Lookup.TryGetValue(fileName[dot..], out format);
    }

    public static string DisplayName(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.Png => "PNG",
        ImageFormat.Heic => "HEIC / HEIF",
        ImageFormat.Raw => "Camera RAW",
        ImageFormat.WebpAvif => "WebP / AVIF",
        ImageFormat.Gif => "GIF",
        ImageFormat.Tiff => "TIFF",
        ImageFormat.Psd => "Photoshop",
        _ => "Other (BMP, JPEG XL…)",
    };

    /// <summary>Formats WPF can usually decode for a tooltip preview (HEIC, WebP and RAW need Windows' codecs).</summary>
    public static bool CanPreview(ImageFormat format) => format is not (ImageFormat.Psd);
}
