namespace ServerMonitor.Core.History;

/// <summary>
/// Sanitizes metric values before they enter history, so malformed collector output can never
/// corrupt a chart (ADR-015 §12; spec §57). Absence stays absence: <c>unknown ≠ zero</c>.
/// </summary>
public static class HistorySampleValidator
{
    /// <summary>Tolerance band around [0,100] within which a slightly-off reading is clamped
    /// rather than dropped (rounding noise). Beyond it, the value is treated as absurd → null.</summary>
    private const double Tolerance = 0.5;

    /// <summary>
    /// Returns a percentage in [0,100], or <c>null</c> when the input is absent, NaN, infinite, or
    /// absurdly out of range. Never returns <c>0</c> for a missing value.
    /// </summary>
    public static double? SanitizePercent(double? value)
    {
        if (value is null)
        {
            return null;
        }

        var v = value.Value;
        if (double.IsNaN(v) || double.IsInfinity(v))
        {
            return null;
        }

        if (v < -Tolerance || v > 100 + Tolerance)
        {
            return null;
        }

        return Math.Clamp(v, 0, 100);
    }

    /// <summary>True when the timestamp is a usable, non-default UTC instant.</summary>
    public static bool IsValidTimestamp(DateTimeOffset capturedAtUtc) =>
        capturedAtUtc != default && capturedAtUtc.Year > 2000;
}
