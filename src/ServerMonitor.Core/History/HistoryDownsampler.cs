namespace ServerMonitor.Core.History;

/// <summary>
/// Deterministic, gap-aware downsampling from raw samples to a bounded, chart-ready series
/// (ADR-015 §6; spec §37, §79). Guarantees: preserves temporal order; never turns <c>null</c> into
/// <c>0</c>; never invents data across empty periods; never hides a peak (per bucket the
/// worst-case/maximum value is kept); output is bounded by <paramref name="targetPoints"/>. When the
/// raw count already fits the target, samples are returned as-is so short ranges keep full detail.
/// </summary>
public static class HistoryDownsampler
{
    public const int DefaultTargetPoints = 300;

    /// <summary>Floor for the connect gap, tied to the documented 30s sampling policy: three missed
    /// samples. Ensures densely-sampled short ranges draw a continuous line.</summary>
    private static readonly TimeSpan MinConnectGap = TimeSpan.FromSeconds(90);

    public static HistorySeries Build(
        IReadOnlyList<ServerHistorySample> ascendingSamples,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Func<ServerHistorySample, double?> selector,
        int targetPoints = DefaultTargetPoints)
    {
        ArgumentNullException.ThrowIfNull(ascendingSamples);
        ArgumentNullException.ThrowIfNull(selector);
        if (targetPoints < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPoints));
        }

        if (endUtc < startUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(endUtc));
        }

        var range = endUtc - startUtc;
        var bucketDuration = range <= TimeSpan.Zero ? TimeSpan.Zero : range / targetPoints;
        var maxConnectGap = ComputeMaxConnectGap(bucketDuration);

        // Latest/Maximum for the accessible summary come from the raw data, independent of bucketing.
        double? latest = null;
        double? maximum = null;
        foreach (var sample in ascendingSamples)
        {
            var value = selector(sample);
            if (value is null)
            {
                continue;
            }

            latest = value; // ascending order → the last non-null wins.
            if (maximum is null || value > maximum)
            {
                maximum = value;
            }
        }

        var points = ascendingSamples.Count <= targetPoints || bucketDuration <= TimeSpan.Zero
            ? BuildRaw(ascendingSamples, selector)
            : BuildBucketed(ascendingSamples, startUtc, bucketDuration, targetPoints, selector);

        return new HistorySeries
        {
            Points = points,
            MaxConnectGap = maxConnectGap,
            Latest = latest,
            Maximum = maximum
        };
    }

    private static TimeSpan ComputeMaxConnectGap(TimeSpan bucketDuration)
    {
        var scaled = TimeSpan.FromSeconds(2.5 * bucketDuration.TotalSeconds);
        return scaled > MinConnectGap ? scaled : MinConnectGap;
    }

    private static List<HistoryChartPoint> BuildRaw(
        IReadOnlyList<ServerHistorySample> samples,
        Func<ServerHistorySample, double?> selector)
    {
        var points = new List<HistoryChartPoint>(samples.Count);
        foreach (var sample in samples)
        {
            points.Add(new HistoryChartPoint
            {
                TimestampUtc = sample.CapturedAtUtc,
                Value = selector(sample)
            });
        }

        return points;
    }

    private static List<HistoryChartPoint> BuildBucketed(
        IReadOnlyList<ServerHistorySample> samples,
        DateTimeOffset startUtc,
        TimeSpan bucketDuration,
        int targetPoints,
        Func<ServerHistorySample, double?> selector)
    {
        var points = new List<HistoryChartPoint>(Math.Min(samples.Count, targetPoints));
        var currentBucket = -1;
        double? bucketMax = null;
        var bucketMaxTime = default(DateTimeOffset);
        var bucketLastTime = default(DateTimeOffset);
        var bucketHasSample = false;

        void Flush()
        {
            if (!bucketHasSample)
            {
                return;
            }

            // A bucket with only null readings (offline) still emits a point with a null value at
            // the last observed time — a break in the line, distinct from an empty (no-data) bucket.
            points.Add(new HistoryChartPoint
            {
                TimestampUtc = bucketMax is not null ? bucketMaxTime : bucketLastTime,
                Value = bucketMax
            });
        }

        foreach (var sample in samples)
        {
            var offsetTicks = (sample.CapturedAtUtc - startUtc).Ticks;
            var index = (int)Math.Clamp(offsetTicks / bucketDuration.Ticks, 0, targetPoints - 1);
            if (index != currentBucket)
            {
                Flush();
                currentBucket = index;
                bucketMax = null;
                bucketHasSample = false;
            }

            bucketHasSample = true;
            bucketLastTime = sample.CapturedAtUtc;
            var value = selector(sample);
            if (value is not null && (bucketMax is null || value > bucketMax))
            {
                bucketMax = value;
                bucketMaxTime = sample.CapturedAtUtc;
            }
        }

        Flush();
        return points;
    }
}
