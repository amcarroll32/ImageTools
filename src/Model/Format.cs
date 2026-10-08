using System.Globalization;

namespace ImageTools.Model;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Binary units with Windows-style labels (1 KB = 1024 B).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString("N0", CultureInfo.CurrentCulture) + " B";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string pattern = value >= 100 ? "N0" : value >= 10 ? "N1" : "N2";
        return value.ToString(pattern, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>"1 folder", "2 folders".</summary>
    public static string Count(long n, string singular, string plural) => $"{Count(n)} {(n == 1 ? singular : plural)}";

    public static string Percent(double fraction)
    {
        if (fraction <= 0) return "0%";
        if (fraction < 0.001) return "<0.1%";
        return (fraction * 100).ToString(fraction < 0.1 ? "N1" : "N0", CultureInfo.CurrentCulture) + "%";
    }

    public static string Date(long utcTicks) =>
        utcTicks <= 0 ? "unknown" : new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string Ago(long utcTicks, long nowUtcTicks)
    {
        if (utcTicks <= 0)
            return "unknown";
        var span = TimeSpan.FromTicks(Math.Max(0, nowUtcTicks - utcTicks));
        if (span.TotalHours < 1) return "just now";
        if (span.TotalDays < 1) return Count((long)span.TotalHours, "hour", "hours") + " ago";
        if (span.TotalDays < 31) return Count((long)span.TotalDays, "day", "days") + " ago";
        if (span.TotalDays < 365) return Count((long)(span.TotalDays / 30.44), "month", "months") + " ago";
        double years = span.TotalDays / 365.25;
        return years < 10
            ? years.ToString("0.#", CultureInfo.CurrentCulture) + (years < 1.05 ? " year ago" : " years ago")
            : $"{(int)years} years ago";
    }

    /// <summary>"E:\Photos\Canon EOS 60D\Card2\100CANON" → "E:\…\Card2\100CANON": the drive and the last two folders.</summary>
    public static string ShortPath(string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? path : $@"{parts[0]}\…\{parts[^2]}\{parts[^1]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:N1}s";
}
