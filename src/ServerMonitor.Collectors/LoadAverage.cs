using System.Globalization;

namespace ServerMonitor.Collectors;

/// <summary>
/// The 1, 5 and 15 minute load averages, as the kernel reports them. A load of 0 is a real, idle value;
/// a missing or malformed reading is never turned into 0: the parsers return <c>null</c> instead.
/// </summary>
public readonly record struct LoadAverage(double OneMinute, double FiveMinutes, double FifteenMinutes)
{
    /// <summary>
    /// Longest token accepted for one load value; anything longer is malformed. Real loads print as
    /// "0.52" or "1234.56" (at most a handful of digits); 12 leaves room without accepting digit runs.
    /// </summary>
    private const int MaxTokenLength = 12;

    /// <summary>
    /// Reads three load tokens. All three must be plain, finite, non-negative decimals in the invariant
    /// culture ("0.52"); one bad token makes the whole reading unknown rather than partly invented.
    /// </summary>
    internal static LoadAverage? FromTokens(string one, string five, string fifteen) =>
        TryParseValue(one, out var oneMinute) &&
        TryParseValue(five, out var fiveMinutes) &&
        TryParseValue(fifteen, out var fifteenMinutes)
            ? new LoadAverage(oneMinute, fiveMinutes, fifteenMinutes)
            : null;

    private static bool TryParseValue(string token, out double value)
    {
        value = 0;
        if (token.Length is 0 or > MaxTokenLength)
        {
            return false;
        }

        // AllowDecimalPoint only: no sign, exponent, thousands separator or surrounding space.
        return double.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value) &&
               value >= 0;
    }
}
