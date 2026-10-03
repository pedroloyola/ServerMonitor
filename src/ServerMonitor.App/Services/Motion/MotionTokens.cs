using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Services.Motion;

/// <summary>The two control points of a cubic KeySpline, each coordinate in [0, 1].</summary>
public readonly record struct MotionKeySpline(double X1, double Y1, double X2, double Y2)
{
    /// <summary>The token form <c>x1,y1,x2,y2</c>, always invariant, so it round-trips through <see cref="MotionTokens.ParseKeySpline"/>.</summary>
    public override string ToString() =>
        string.Join(",", new[] { X1, Y1, X2, Y2 }.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
}

/// <summary>
/// UI.2 S3 (F-4). C# access to the motion tokens in Styles/Tokens/Motion.xaml. They are stored as <c>x:String</c>
/// so XAML converts them wherever a KeyTime, Duration or KeySpline consumes them; code that builds an animation
/// must parse them the same way on every machine. The parsers are pure and CULTURE-INVARIANT (a pt-PT decimal
/// comma never changes the meaning of <c>0:0:0.180</c> or <c>0.55,0.55,0,1</c>), and they fail closed: anything
/// malformed throws an <see cref="InvalidOperationException"/> that names the key and the value instead of
/// silently becoming zero.
/// <para>
/// Consumers must honour <c>UISettings.AnimationsEnabled</c>; this type only turns tokens into values.
/// Compiled in every configuration. No production consumer since UI.5 removed the FullCard focus pulse; the tokens stay.
/// </para>
/// </summary>
public static partial class MotionTokens
{
    public const string FocusPeakTime = "SaMotionFocusPeakTime";
    public const string FocusFadeEndTime = "SaMotionFocusFadeEndTime";
    public const string FocusReducedHoldTime = "SaMotionFocusReducedHoldTime";
    public const string FadeDuration = "SaMotionFadeDuration";
    public const string FastDuration = "SaMotionFastDuration";
    public const string NormalDuration = "SaMotionNormalDuration";
    public const string SlowDuration = "SaMotionSlowDuration";
    public const string LinearKeySpline = "SaMotionLinearKeySpline";
    public const string DirectKeySpline = "SaMotionDirectKeySpline";
    public const string PointToPointKeySpline = "SaMotionPointToPointKeySpline";

    /// <summary>Every time/duration token, in Motion.xaml order.</summary>
    public static IReadOnlyList<string> TimeKeys { get; } =
        [FocusPeakTime, FocusFadeEndTime, FocusReducedHoldTime, FadeDuration, FastDuration, NormalDuration, SlowDuration];

    /// <summary>Every KeySpline token, in Motion.xaml order.</summary>
    public static IReadOnlyList<string> KeySplineKeys { get; } = [LinearKeySpline, DirectKeySpline, PointToPointKeySpline];

    /// <summary>Parses a time token (<c>h:m:s</c> with an optional <c>.fraction</c>, invariant) into a positive TimeSpan.</summary>
    public static TimeSpan ParseTime(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (value is not string text || !TimeShape().IsMatch(text)
            || !TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time)
            || time <= TimeSpan.Zero)
        {
            throw Invalid(key, value, "a positive time 'h:m:s[.fraction]' with an invariant '.' separator");
        }

        return time;
    }

    /// <summary>Parses a KeySpline token (<c>x1,y1,x2,y2</c>, invariant, every coordinate in [0, 1]).</summary>
    public static MotionKeySpline ParseKeySpline(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var parts = (value as string)?.Split(',');
        if (parts is not { Length: 4 })
        {
            throw Invalid(key, value, "four invariant numbers 'x1,y1,x2,y2'");
        }

        var coordinates = new double[4];
        for (var index = 0; index < 4; index++)
        {
            if (!double.TryParse(parts[index].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out coordinates[index])
                || coordinates[index] is < 0 or > 1)
            {
                throw Invalid(key, value, "four invariant numbers 'x1,y1,x2,y2', each in [0, 1]");
            }
        }

        return new MotionKeySpline(coordinates[0], coordinates[1], coordinates[2], coordinates[3]);
    }

    /// <summary>Reads and parses a time token from a resource dictionary (normally Application.Current.Resources).</summary>
    public static TimeSpan GetTime(ResourceDictionary resources, string key)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return ParseTime(key, resources.TryGetValue(key, out var value) ? value : null);
    }

    /// <summary>Reads and parses a KeySpline token from a resource dictionary (normally Application.Current.Resources).</summary>
    public static MotionKeySpline GetKeySpline(ResourceDictionary resources, string key)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return ParseKeySpline(key, resources.TryGetValue(key, out var value) ? value : null);
    }

    private static InvalidOperationException Invalid(string key, object? value, string expected) =>
        new($"Motion token '{key}' has the invalid value '{value ?? "<missing>"}'; expected {expected}.");

    [GeneratedRegex(@"\A\d{1,2}:\d{1,2}:\d{1,2}(\.\d{1,7})?\z")]
    private static partial Regex TimeShape();
}
