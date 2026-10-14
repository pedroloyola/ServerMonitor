using System.Text.RegularExpressions;

namespace ServerMonitor.App.Services;

/// <summary>
/// REL.0D. The Debug-only <c>--qa-system-culture &lt;name&gt;</c> modifier makes the language service read
/// <c>&lt;name&gt;</c> as the Windows UI culture for one QA launch (e.g. <c>fr-FR</c>, <c>pt-AO</c>), so the legacy
/// language hint and "Use system language" can be verified WITHOUT changing the user's Windows language. A modifier,
/// never a harness. Strict: a culture-shaped value, at most once; anything else is refused, never silently ignored.
/// Compiled in every configuration so a test can prove the Release guard: with <c>isDebugBuild: false</c> it is never on.
/// </summary>
public static partial class QaSystemCulturePolicy
{
    public const string LaunchFlag = "--qa-system-culture";

    /// <summary>The requested culture name, or null in Release, without the flag, or for a malformed launch.</summary>
    public static string? ResolveCulture(IReadOnlyList<string> commandLineArgs, bool isDebugBuild)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);
        if (!isDebugBuild || Refusal(commandLineArgs) is not null)
        {
            return null;
        }

        return Values(commandLineArgs).SingleOrDefault();
    }

    /// <summary>Null when the flag is absent or well-formed; otherwise why the launch must be refused.</summary>
    public static string? Refusal(IReadOnlyList<string> commandLineArgs)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);
        var values = Values(commandLineArgs).ToList();
        if (values.Count > 1)
        {
            return $"{LaunchFlag} may be given at most once.";
        }

        return values.Count == 1 && (values[0] is null || !CultureName().IsMatch(values[0]!))
            ? $"{LaunchFlag} needs a culture name such as fr-FR, en-GB or pt-AO."
            : null;
    }

    /// <summary>One entry per occurrence of the flag: its value, or null when it has none.</summary>
    private static IEnumerable<string?> Values(IReadOnlyList<string> commandLineArgs)
    {
        for (var index = 0; index < commandLineArgs.Count; index++)
        {
            var argument = commandLineArgs[index];
            if (string.Equals(argument, LaunchFlag, StringComparison.Ordinal))
            {
                yield return index + 1 < commandLineArgs.Count ? commandLineArgs[index + 1] : null;
            }
            else if (argument.StartsWith(LaunchFlag + "=", StringComparison.Ordinal))
            {
                yield return argument[(LaunchFlag.Length + 1)..];
            }
        }
    }

    [GeneratedRegex(@"\A[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}\z")]
    private static partial Regex CultureName();
}
