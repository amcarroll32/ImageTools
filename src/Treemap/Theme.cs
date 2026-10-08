using System.Windows;
using System.Windows.Media;
using ImageTools.Model;

namespace ImageTools.Treemap;

public enum ColorMode
{
    Duplicates,
    Library,
    Format,
    Date,
}

/// <summary>
/// All app colors for the light and dark themes, matching Disk Visualizer (see DESIGN.md).
/// Category colors are the validated categorical slots (CVD-checked in this order, stepped
/// separately for each surface); dates use a validated single-hue ordinal ramp.
/// Call <see cref="Apply"/> to switch; consumers re-read the properties.
/// </summary>
public static class Theme
{
    private static readonly int[] CategoryDark = [0x3987e5, 0xd95926, 0x199e70, 0xc98500, 0xd55181, 0x008300, 0x9085e9, 0xe66767, 0x898781];
    private static readonly int[] CategoryLight = [0x2a78d6, 0xeb6834, 0x1baf7a, 0xeda100, 0xe87ba4, 0x008300, 0x4a3aa7, 0xe34948, 0x898781];

    // Recent → old. Old photos get the most contrast with the surface.
    private static readonly int[] AgeDark = [0x184f95, 0x2a78d6, 0x5598e7, 0x9ec5f4, 0xcde2fb];
    private static readonly int[] AgeLight = [0x86b6ef, 0x5598e7, 0x2a78d6, 0x1c5cab, 0x0d366b];

    private static Brush[] _folderFills = [];
    private static Brush[] _slotFills = [];
    private static Brush[] _slotSwatches = [];
    private static Brush[] _slotText = [];
    private static Brush[] _ageFills = [];
    private static Brush[] _ageSwatches = [];
    private static Brush[] _ageText = [];

    static Theme() => Apply(dark: true);

    public static bool IsDark { get; private set; }

    // ---- App chrome ----
    public static Brush WindowBackground { get; private set; } = null!;
    public static Brush SurfaceBrush { get; private set; } = null!;
    public static Brush CardBackground { get; private set; } = null!;
    public static Brush CardBorder { get; private set; } = null!;
    public static Brush Hairline { get; private set; } = null!;
    public static Brush TipBackground { get; private set; } = null!;
    public static Brush TipBorder { get; private set; } = null!;
    public static Brush BadgeBackground { get; private set; } = null!;
    public static Brush BarTrack { get; private set; } = null!;
    public static Brush LinkHover { get; private set; } = null!;

    public static Brush PrimaryText { get; private set; } = null!;
    public static Brush SecondaryText { get; private set; } = null!;
    public static Brush MutedText { get; private set; } = null!;
    public static Brush AccentText { get; private set; } = null!;
    public static Brush Accent { get; private set; } = null!;

    /// <summary>Capacity bars for drives that are more than 90% full.</summary>
    public static Brush CapacityCritical { get; private set; } = null!;

    // ---- Map ----
    public static Brush FolderBorder { get; private set; } = null!;
    public static Brush DriveBorder { get; private set; } = null!;
    public static Brush DriveHeaderFill { get; private set; } = null!;

    /// <summary>Images with no copy elsewhere.</summary>
    public static Brush UniqueFill { get; private set; } = null!;
    public static Brush UniqueSwatch { get; private set; } = null!;

    /// <summary>Images the duplicate check hasn't reached yet, or couldn't open.</summary>
    public static Brush PendingFill { get; private set; } = null!;
    public static Brush PendingSwatch { get; private set; } = null!;

    /// <summary>App and game images (shown only when "App assets" is ticked).</summary>
    public static Brush AssetFill { get; private set; } = null!;
    public static Brush AssetSwatch { get; private set; } = null!;

    /// <summary>Cloud placeholders: there, but never read.</summary>
    public static Brush OnlineFill { get; private set; } = null!;
    public static Brush OnlineSwatch { get; private set; } = null!;

    public static Brush UnknownAgeFill { get; private set; } = null!;
    public static Brush UnknownAgeSwatch { get; private set; } = null!;

    /// <summary>Leaves that don't match the search.</summary>
    public static Brush DimFill { get; private set; } = null!;
    public static Brush ProgressTrack { get; private set; } = null!;

    public static Pen HoverPen { get; private set; } = null!;
    public static Pen TargetPen { get; private set; } = null!;
    public static Pen SelectPen { get; private set; } = null!;
    public static Pen MatchPen { get; private set; } = null!;
    public static Brush SelectWash { get; private set; } = null!;

    // Status colors for badge icons (always paired with a text label).
    public static Brush StatusGood { get; private set; } = null!;
    public static Brush StatusWarning { get; private set; } = null!;
    public static Brush StatusCritical { get; private set; } = null!;

    public static void Apply(bool dark)
    {
        IsDark = dark;
        int[] categories = dark ? CategoryDark : CategoryLight;
        int[] ages = dark ? AgeDark : AgeLight;

        _slotFills = categories.Select(c => Cushion(Rgb(c))).ToArray();
        _slotSwatches = categories.Select(c => Solid(Rgb(c))).ToArray();
        _slotText = categories.Select(c => TextFor(Rgb(c))).ToArray();
        _ageFills = ages.Select(c => Cushion(Rgb(c))).ToArray();
        _ageSwatches = ages.Select(c => Solid(Rgb(c))).ToArray();
        _ageText = ages.Select(c => TextFor(Rgb(c))).ToArray();

        StatusGood = Solid(0x0ca30c);
        StatusWarning = Solid(0xfab219);

        if (dark)
        {
            WindowBackground = Solid(0x1a1a19);
            SurfaceBrush = Solid(0x1e1e1c);
            CardBackground = Solid(0x242422);
            CardBorder = Solid(0x383835);
            Hairline = Solid(0x383835);
            TipBackground = Solid(Color.FromArgb(0xF2, 0x23, 0x23, 0x21));
            TipBorder = Solid(0x4a4a46);
            BadgeBackground = Solid(0x1f2d40);
            BarTrack = Solid(0x2c2c2a);
            LinkHover = Solid(0x2e2e2b);

            PrimaryText = Solid(0xffffff);
            SecondaryText = Solid(0xc3c2b7);
            MutedText = Solid(0x898781);
            AccentText = Solid(0x6da7ec);
            Accent = Solid(0x3987e5);
            CapacityCritical = Solid(0xe66767);

            _folderFills = [Solid(0x262624), Solid(0x2e2e2b), Solid(0x353532), Solid(0x2b2b29), Solid(0x32322f)];
            FolderBorder = Solid(0x3d3d3a);
            DriveBorder = Solid(0x6b6a65);
            DriveHeaderFill = Solid(0x2f2f2c);
            UniqueFill = Cushion(Rgb(0x5e5d58));
            UniqueSwatch = Solid(0x5e5d58);
            PendingFill = Solid(0x3b3b37);
            PendingSwatch = Solid(0x3b3b37);
            AssetFill = Hatch(Rgb(0x2a2a28), Rgb(0x383835), 45);
            AssetSwatch = Solid(0x383835);
            OnlineFill = Hatch(Rgb(0x3a3533), Rgb(0x5a4f4b), 135);
            OnlineSwatch = Solid(0x5a4f4b);
            UnknownAgeFill = Cushion(Rgb(0x5e5d58));
            UnknownAgeSwatch = Solid(0x5e5d58);
            DimFill = Solid(0x2a2a28);
            ProgressTrack = Solid(0x383835);

            HoverPen = MakePen(Rgb(0xffffff), 2);
            TargetPen = MakePen(Rgb(0x6da7ec), 2);
            SelectPen = MakePen(Rgb(0xfab219), 2);
            MatchPen = MakePen(Rgb(0xf0efec), 1.5);
            SelectWash = Solid(Color.FromArgb(0x40, 0xfa, 0xb2, 0x19));
            StatusCritical = Solid(0xe66767);
        }
        else
        {
            WindowBackground = Solid(0xf9f9f7);
            SurfaceBrush = Solid(0xfcfcfb);
            CardBackground = Solid(0xffffff);
            CardBorder = Solid(0xe1e0d9);
            Hairline = Solid(0xe1e0d9);
            TipBackground = Solid(Color.FromArgb(0xF7, 0xff, 0xff, 0xff));
            TipBorder = Solid(0xc9c8c0);
            BadgeBackground = Solid(0xe1ecfb);
            BarTrack = Solid(0xe8e7e2);
            LinkHover = Solid(0xefeee9);

            PrimaryText = Solid(0x0b0b0b);
            SecondaryText = Solid(0x52514e);
            MutedText = Solid(0x6f6e69);
            AccentText = Solid(0x1c5cab);
            Accent = Solid(0x2a78d6);
            CapacityCritical = Solid(0xd03b3b);

            _folderFills = [Solid(0xf1f0ec), Solid(0xe8e7e2), Solid(0xe0dfd9), Solid(0xecebe6), Solid(0xe4e3dd)];
            FolderBorder = Solid(0xc9c8c0);
            DriveBorder = Solid(0x8f8e87);
            DriveHeaderFill = Solid(0xe3e2dc);
            UniqueFill = Cushion(Rgb(0xb4b3ac));
            UniqueSwatch = Solid(0xb4b3ac);
            PendingFill = Solid(0xdcdbd4);
            PendingSwatch = Solid(0xdcdbd4);
            AssetFill = Hatch(Rgb(0xf3f2ee), Rgb(0xdcdbd4), 45);
            AssetSwatch = Solid(0xdcdbd4);
            OnlineFill = Hatch(Rgb(0xece4e1), Rgb(0xcdbfb9), 135);
            OnlineSwatch = Solid(0xcdbfb9);
            UnknownAgeFill = Cushion(Rgb(0xb4b3ac));
            UnknownAgeSwatch = Solid(0xb4b3ac);
            DimFill = Solid(0xeeede9);
            ProgressTrack = Solid(0xdcdbd4);

            HoverPen = MakePen(Rgb(0x0b0b0b), 2);
            TargetPen = MakePen(Rgb(0x2a78d6), 2);
            SelectPen = MakePen(Rgb(0xc98500), 2);
            MatchPen = MakePen(Rgb(0x0b0b0b), 1.5);
            SelectWash = Solid(Color.FromArgb(0x40, 0xed, 0xa1, 0x00));
            StatusCritical = Solid(0xd03b3b);
        }
    }

    public static Brush FolderFill(int depth) => _folderFills[depth % _folderFills.Length];

    /// <summary>Exclamation-in-triangle glyph for disks in Warning or Unhealthy state.</summary>
    public const string AlertGlyph = "";

    /// <summary>Icon-font glyph for a drive: SSD, hard drive, USB stick or memory card.</summary>
    public static string DriveGlyph(FsNode drive) =>
        (drive.Hardware?.Kind ?? (drive.IsRemovable ? DriveKind.UsbDrive : DriveKind.Unknown)) switch
        {
            DriveKind.Ssd => "",
            DriveKind.UsbDrive => "",
            DriveKind.MemoryCard => "",
            DriveKind.Network => "",
            DriveKind.Cloud => "",
            _ => "",
        };

    /// <summary>Green / yellow / red for Healthy / Warning / Unhealthy; null when the disk doesn't report health.</summary>
    public static Brush? HealthBrush(DiskHealth health) => health switch
    {
        DiskHealth.Healthy => StatusGood,
        DiskHealth.Warning => StatusWarning,
        DiskHealth.Unhealthy => StatusCritical,
        _ => null,
    };

    // ---- Image colors ----

    /// <summary>What an image's color means in each mode, as one of the legend's entries.</summary>
    public enum Key
    {
        Slot,      // categorical slot in Value
        Age,       // age bucket in Value (-1 unknown)
        Unique,
        Pending,     // not checked yet
        Unreadable,  // couldn't be opened to compare
        Asset,
        Online,
    }

    public static (Key Key, int Value) Classify(FsNode file, ColorMode mode, long nowTicks)
    {
        if (mode == ColorMode.Format)
            return (Key.Slot, (int)file.Format);
        if (file.IsAsset)
            return (Key.Asset, 0);
        if (file.IsCloudOnly)
            return (Key.Online, 0);
        return mode switch
        {
            ColorMode.Duplicates => file.Dup switch
            {
                DupStatus.Duplicate => (Key.Slot, 0),
                DupStatus.Unique => (Key.Unique, 0),
                DupStatus.Unreadable => (Key.Unreadable, 0),
                _ => (Key.Pending, 0),
            },
            ColorMode.Library => (Key.Slot, (int)file.Library),
            _ => (Key.Age, AgeBuckets.Of(file.DateTicks, nowTicks)),
        };
    }

    public static Brush Fill(FsNode node, ColorMode mode, long nowTicks)
    {
        if (node.Kind != NodeKind.File)
            return FolderFill(0);
        var (key, value) = Classify(node, mode, nowTicks);
        return key switch
        {
            Key.Slot => _slotFills[value],
            Key.Age => value >= 0 ? _ageFills[value] : UnknownAgeFill,
            Key.Unique => UniqueFill,
            Key.Pending => PendingFill,
            Key.Asset => AssetFill,
            _ => OnlineFill, // online-only and unreadable: both there, but not read
        };
    }

    public static Brush LabelBrush(FsNode node, ColorMode mode, long nowTicks)
    {
        if (node.Kind != NodeKind.File)
            return SecondaryText;
        var (key, value) = Classify(node, mode, nowTicks);
        return key switch
        {
            Key.Slot => _slotText[value],
            Key.Age => value >= 0 ? _ageText[value] : PrimaryText,
            Key.Unique => IsDark ? PrimaryText : DarkInk,
            Key.Pending => PrimaryText,
            _ => SecondaryText,
        };
    }

    public static Brush Swatch(Key key, int value) => key switch
    {
        Key.Slot => _slotSwatches[value],
        Key.Age => value >= 0 ? _ageSwatches[value] : UnknownAgeSwatch,
        Key.Unique => UniqueSwatch,
        Key.Pending => PendingSwatch,
        Key.Asset => AssetSwatch,
        _ => OnlineSwatch, // online-only and unreadable
    };

    public static Brush Swatch(FsNode node, ColorMode mode, long nowTicks)
    {
        if (node.Kind != NodeKind.File)
            return FolderBorder;
        var (key, value) = Classify(node, mode, nowTicks);
        return Swatch(key, value);
    }

    public static Brush SlotSwatch(int slot) => _slotSwatches[slot];

    private static readonly Brush DarkInk = Solid(0x0b0b0b);

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Brush Solid(int rgb) => Solid(Rgb(rgb));

    private static Brush Solid(Color c) => Frozen(new SolidColorBrush(c));

    private static Pen MakePen(Color c, double thickness) => Frozen(new Pen(Solid(c), thickness));

    /// <summary>Subtle top-left highlight so adjacent same-colored images read as separate blocks.</summary>
    private static Brush Cushion(Color c)
    {
        var light = Blend(c, Colors.White, 0.18);
        var dark = Blend(c, Colors.Black, 0.18);
        return Frozen(new LinearGradientBrush(light, dark, new Point(0, 0), new Point(1, 1)));
    }

    private static Brush Hatch(Color background, Color line, double angle)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(new SolidColorBrush(line), null, new Rect(0, 0, 8, 1.5));
        }
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 8, 8),
            ViewboxUnits = BrushMappingMode.Absolute,
            Transform = new RotateTransform(angle),
        };
        return Frozen(brush);
    }

    /// <summary>Whichever of near-black or white ink has more contrast against the fill.</summary>
    private static Brush TextFor(Color c)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        double lum = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        double vsWhite = 1.05 / (lum + 0.05);
        double vsBlack = (lum + 0.05) / 0.05;
        return vsBlack >= vsWhite ? Solid(Rgb(0x0b0b0b)) : Solid(Colors.White);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
