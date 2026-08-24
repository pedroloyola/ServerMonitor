using System.Globalization;

namespace ServerMonitor.Collectors.Linux.Parsing;

/// <summary>
/// Computes CPU usage percentage from two "cat /proc/stat" samples taken
/// CpuSampleInterval apart. Returns null when the samples cannot be
/// interpreted; a computed 0% (fully idle) is a real value, not unknown.
/// All arithmetic is unsigned and checked: a counter regression (second
/// sample lower than the first) underflows and is treated as unknown,
/// same as an outright numeric overflow.
/// </summary>
public static class ProcStatCpuParser
{
    public static double? CalculateUsagePercent(string? firstSample, string? secondSample)
    {
        if (!TryParseAggregateCpuLine(firstSample, out var first) ||
            !TryParseAggregateCpuLine(secondSample, out var second))
        {
            return null;
        }

        try
        {
            checked
            {
                var totalDelta = second.Total - first.Total;
                var idleDelta = second.Idle - first.Idle;

                if (totalDelta == 0 || idleDelta > totalDelta)
                {
                    return null;
                }

                var busyDelta = totalDelta - idleDelta;
                return busyDelta / (double)totalDelta * 100d;
            }
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool TryParseAggregateCpuLine(string? sample, out CpuTimes times)
    {
        times = default;
        if (string.IsNullOrWhiteSpace(sample))
        {
            return false;
        }

        foreach (var rawLine in sample.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0] != "cpu")
            {
                // Not the aggregate line (e.g. "cpu0"), keep looking.
                continue;
            }

            if (fields.Length < 5)
            {
                return false;
            }

            var values = new ulong[fields.Length - 1];
            for (var i = 1; i < fields.Length; i++)
            {
                if (!ulong.TryParse(
                        fields[i],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    return false;
                }

                values[i - 1] = value;
            }

            try
            {
                checked
                {
                    // user, nice, system, idle, iowait, irq, softirq, steal.
                    // guest/guest_nice (indices 8/9) are already folded into
                    // user/nice upstream and must not be summed again.
                    var countedFields = Math.Min(values.Length, 8);
                    ulong total = 0;
                    for (var i = 0; i < countedFields; i++)
                    {
                        total += values[i];
                    }

                    var idle = values[3] + (values.Length > 4 ? values[4] : 0UL);
                    times = new CpuTimes(total, idle);
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private readonly record struct CpuTimes(ulong Total, ulong Idle);
}
