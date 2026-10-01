namespace ServerMonitor.App.Services;

/// <summary>What a launch asked of the Debug-only component gallery.</summary>
public enum QaGalleryMode
{
    /// <summary>No gallery: the normal (production) composition runs.</summary>
    None,

    /// <summary><c>--qa-components</c>: open the gallery window.</summary>
    Components,

    /// <summary><c>--qa-tokens</c>: open the gallery, run the token self-check, write the report and exit.</summary>
    Tokens,

    /// <summary>A gallery flag was given but the launch is invalid; the process must exit 3 without composing anything.</summary>
    Refused
}

/// <summary>The resolved gallery request. Every field except <see cref="Mode"/> is null unless the mode is a gallery mode.</summary>
public sealed record QaGalleryRequest(
    QaGalleryMode Mode,
    string? Theme = null,
    string? Page = null,
    string? OutputDirectory = null,
    string? RefusalReason = null)
{
    public static QaGalleryRequest NotRequested { get; } = new(QaGalleryMode.None);

    public bool IsGallery => Mode is QaGalleryMode.Components or QaGalleryMode.Tokens;
}

/// <summary>
/// UI.2 S2. Decides whether a launch asked for the Debug-only component gallery (<c>--qa-components</c> /
/// <c>--qa-tokens</c>) and validates the gallery's own options. Pure and compiled in EVERY configuration - like
/// <see cref="QaUiLanguagePolicy"/> - so a test proves the Release guard: with <c>isDebugBuild: false</c> the
/// result is always <see cref="QaGalleryMode.None"/>. The gallery UI itself (Qa/Gallery/**) is Debug-only.
/// <para>
/// Fail-closed: the gallery is exclusive. Combining it with any other <c>--qa-*</c> harness, giving an unknown
/// theme or page, or pointing the report directory at the real user data folder is
/// <see cref="QaGalleryMode.Refused"/> (exit 3), never a silent fallback to another composition.
/// </para>
/// </summary>
public static class QaGalleryPolicy
{
    public const string ComponentsFlag = "--qa-components";
    public const string TokensFlag = "--qa-tokens";
    public const string ThemeFlag = "--qa-gallery-theme";
    public const string PageFlag = "--qa-gallery-page";
    public const string OutputFlag = "--qa-gallery-out";

    /// <summary>Gallery themes. <c>hc-sim</c> is a labelled resource simulation, never the real contrast theme.</summary>
    public static IReadOnlyList<string> Themes { get; } = ["dark", "light", "hc-sim"];

    /// <summary>Gallery page ids, in tab order.</summary>
    public static IReadOnlyList<string> Pages { get; } = ["tokens", "typography", "colors", "spacing", "materials", "status"];

    /// <summary>The only <c>--qa-*</c> switches that may accompany a gallery flag.</summary>
    private static readonly string[] OwnFlags = [ComponentsFlag, TokensFlag, ThemeFlag, PageFlag, OutputFlag, QaUiLanguagePolicy.LaunchFlag];

    /// <param name="commandLineArgs">The process arguments (element 0 may be the executable path).</param>
    /// <param name="isDebugBuild">False in Release: the gallery does not exist there.</param>
    /// <param name="realDataDirectory">The user's real data folder (<c>%LOCALAPPDATA%\ServerMonitor</c>); the report may never go there.</param>
    /// <param name="defaultOutputDirectory">Report folder when <c>--qa-gallery-out</c> is not given.</param>
    public static QaGalleryRequest Resolve(
        IReadOnlyList<string> commandLineArgs,
        bool isDebugBuild,
        string realDataDirectory,
        string defaultOutputDirectory)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);
        ArgumentException.ThrowIfNullOrWhiteSpace(realDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultOutputDirectory);

        if (!isDebugBuild)
        {
            return QaGalleryRequest.NotRequested;
        }

        var components = HasFlag(commandLineArgs, ComponentsFlag);
        var tokens = HasFlag(commandLineArgs, TokensFlag);
        if (!components && !tokens)
        {
            return QaGalleryRequest.NotRequested;
        }

        var foreign = commandLineArgs
            .Where(argument => argument.StartsWith("--qa-", StringComparison.OrdinalIgnoreCase))
            .Select(argument => argument.Split('=', 2)[0])
            .FirstOrDefault(flag => !OwnFlags.Contains(flag, StringComparer.OrdinalIgnoreCase));
        if (foreign is not null)
        {
            return Refuse($"The gallery is exclusive; it cannot be combined with {foreign}.");
        }

        var theme = ValueOf(commandLineArgs, ThemeFlag, out var themeGiven);
        if (themeGiven && (theme is null || !Themes.Contains(theme, StringComparer.OrdinalIgnoreCase)))
        {
            return Refuse($"Unknown {ThemeFlag} '{theme}'; expected one of {string.Join(", ", Themes)}.");
        }

        var page = ValueOf(commandLineArgs, PageFlag, out var pageGiven);
        if (pageGiven && (page is null || !Pages.Contains(page, StringComparer.OrdinalIgnoreCase)))
        {
            return Refuse($"Unknown {PageFlag} '{page}'; expected one of {string.Join(", ", Pages)}.");
        }

        var output = ValueOf(commandLineArgs, OutputFlag, out var outputGiven);
        if (outputGiven && string.IsNullOrWhiteSpace(output))
        {
            return Refuse($"{OutputFlag} needs a directory.");
        }

        string outputDirectory;
        try
        {
            outputDirectory = Path.GetFullPath(outputGiven ? output! : defaultOutputDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refuse($"{OutputFlag} '{output}' is not a valid path.");
        }

        if (IsSameOrUnder(outputDirectory, Path.GetFullPath(realDataDirectory)))
        {
            return Refuse($"The gallery report may not be written under the real data folder ({realDataDirectory}).");
        }

        return new QaGalleryRequest(
            tokens ? QaGalleryMode.Tokens : QaGalleryMode.Components,
            Theme: (theme ?? "dark").ToLowerInvariant(),
            Page: (page ?? Pages[0]).ToLowerInvariant(),
            OutputDirectory: outputDirectory);
    }

    private static QaGalleryRequest Refuse(string reason) => new(QaGalleryMode.Refused, RefusalReason: reason);

    private static bool HasFlag(IReadOnlyList<string> arguments, string flag) =>
        arguments.Any(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>The value of <c>flag value</c> or <c>flag=value</c>; <paramref name="given"/> is true when the flag appears at all.</summary>
    private static string? ValueOf(IReadOnlyList<string> arguments, string flag, out bool given)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
            {
                given = true;
                var next = index + 1 < arguments.Count ? arguments[index + 1] : null;
                return next is null || next.StartsWith("--", StringComparison.Ordinal) ? null : next;
            }

            if (argument.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                given = true;
                var value = argument[(flag.Length + 1)..];
                return value.Length == 0 ? null : value;
            }
        }

        given = false;
        return null;
    }

    private static bool IsSameOrUnder(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
