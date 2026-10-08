using System.Windows;
using System.Windows.Media;
using ImageTools.Platform;
using ImageTools.Treemap;

namespace ImageTools;

public partial class App : Application
{
    public App()
    {
        Settings = AppSettings.Load();
        Resources["Icons"] = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        ApplyTheme(Settings.PrefersDark);
    }

    public static AppSettings Settings { get; private set; } = new();

    /// <summary>
    /// Switches the Fluent control theme and the app's own palette, and pushes the palette into
    /// the application's brush resources, which every window uses via DynamicResource.
    /// </summary>
    public static void ApplyTheme(bool dark)
    {
        Theme.Apply(dark);
        Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        var r = Current.Resources;
        r["WindowBg"] = Theme.WindowBackground;
        r["Hairline"] = Theme.Hairline;
        r["InkPrimary"] = Theme.PrimaryText;
        r["InkSecondary"] = Theme.SecondaryText;
        r["InkMuted"] = Theme.MutedText;
        r["AccentInk"] = Theme.AccentText;
        r["AccentFill"] = Theme.Accent;
        r["CardBg"] = Theme.CardBackground;
        r["CardBorder"] = Theme.CardBorder;
        r["TipBg"] = Theme.TipBackground;
        r["TipBorder"] = Theme.TipBorder;
        r["BadgeBg"] = Theme.BadgeBackground;
        r["BarTrack"] = Theme.BarTrack;
        r["LinkHover"] = Theme.LinkHover;
    }
}
