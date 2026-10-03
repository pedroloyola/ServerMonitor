using System.Globalization;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.5 Server Detail metric visuals (Figma 112:1818 / 112:1855 / 112:1890), as pure rules the View only binds.
/// </summary>
public static class MetricVisualPresentation
{
    /// <summary>Memory card: 28 segments (Figma 112:1855).</summary>
    public const int MemorySegmentCount = 28;

    /// <summary>Disk card: 14 segments (Figma 112:1890).</summary>
    public const int DiskSegmentCount = 14;

    /// <summary>CPU card: at most 30 recent samples (Figma 112:1818 "pulse / 30 samples").</summary>
    public const int CpuPulseSampleCount = 30;

    /// <summary>
    /// A-4 (Boss): the number of lit segments for a percentage. <c>round(total × p / 100)</c> (midpoint away from zero),
    /// at least 1 when p &gt; 0, never more than <paramref name="total"/>. Unknown (null / NaN) is <see langword="null"/>:
    /// an empty track with "—", never 0 lit segments presented as a reading.
    /// </summary>
    public static int? LitSegments(double? percent, int total)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(total);
        if (percent is not { } value || double.IsNaN(value))
        {
            return null;
        }

        var clamped = Math.Clamp(value, 0, 100);
        if (clamped <= 0)
        {
            return 0;
        }

        var lit = (int)Math.Round(total * clamped / 100, MidpointRounding.AwayFromZero);
        return Math.Clamp(lit, 1, total);
    }

    /// <summary>
    /// Figma legend "9,9 GB de 16 GB" / "240 GB de 500 GB": a byte count in the same binary units the card has always used
    /// (GB = 1024³), one decimal below 10 GB and whole numbers above, a trailing ",0" dropped; MB below 1 GB. Display only:
    /// nothing here derives a percentage.
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        const double gib = 1024d * 1024 * 1024;
        const double mib = 1024d * 1024;
        if (bytes >= gib)
        {
            var value = bytes / gib;
            return string.Format(CultureInfo.CurrentUICulture, value < 10 ? "{0:0.#} GB" : "{0:0} GB", value);
        }

        return string.Format(CultureInfo.CurrentUICulture, "{0:0} MB", Math.Max(0, bytes) / mib);
    }

    /// <summary>
    /// Boss decision (fix round 2), a deliberate derivation: the pulse's scale ceiling - the smallest of 25 / 50 / 75 / 100 %
    /// at or above the highest visible sample (NaN ignored; no samples → 25). Quantised so the scale is deterministic and
    /// can be stated in words ("escala até 25 %").
    /// </summary>
    public static int PulseCeiling(IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var max = 0d;
        foreach (var sample in samples)
        {
            if (!double.IsNaN(sample) && sample > max)
            {
                max = sample;
            }
        }

        return max <= 25 ? 25 : max <= 50 ? 50 : max <= 75 ? 75 : 100;
    }

    /// <summary>
    /// H-UI5-3: the CPU pulse bars — REAL samples only. Takes the most recent measured CPU values of an existing history
    /// series (oldest → newest, at most <see cref="CpuPulseSampleCount"/>). Unmeasured points (offline: null) are not
    /// drawn and nothing is padded: fewer than 30 samples give fewer bars (the View right-aligns them), none give none.
    /// </summary>
    public static IReadOnlyList<double> CpuPulse(HistorySeries? series)
    {
        if (series?.Points is not { Count: > 0 } points)
        {
            return [];
        }

        var recent = new List<double>(CpuPulseSampleCount);
        for (var index = points.Count - 1; index >= 0 && recent.Count < CpuPulseSampleCount; index--)
        {
            if (points[index].Value is { } value && !double.IsNaN(value))
            {
                recent.Add(Math.Clamp(value, 0, 100));
            }
        }

        recent.Reverse();
        return recent;
    }
}
