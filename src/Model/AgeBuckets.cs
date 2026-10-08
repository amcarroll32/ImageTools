namespace ImageTools.Model;

/// <summary>
/// Ordinal age buckets for the "Color: Date" mode, by date taken (or last modified when a
/// photo has no date). Five, because that's the most steps a single-hue ramp can hold while
/// staying distinguishable on a light surface. Photo libraries span decades, so the steps are years.
/// </summary>
public static class AgeBuckets
{
    public const int Count = 5;
    public const int Unknown = -1;

    private static readonly long[] Limits =
    [
        TimeSpan.FromDays(365).Ticks,
        TimeSpan.FromDays(3 * 365).Ticks,
        TimeSpan.FromDays(5 * 365).Ticks,
        TimeSpan.FromDays(10 * 365).Ticks,
    ];

    public static int Of(long utcTicks, long nowUtcTicks)
    {
        if (utcTicks <= 0)
            return Unknown;
        long age = nowUtcTicks - utcTicks;
        for (int i = 0; i < Limits.Length; i++)
            if (age < Limits[i])
                return i;
        return Limits.Length;
    }

    public static string Label(int bucket) => bucket switch
    {
        0 => "Under a year",
        1 => "1 – 3 years",
        2 => "3 – 5 years",
        3 => "5 – 10 years",
        4 => "Over 10 years",
        _ => "Unknown date",
    };
}
