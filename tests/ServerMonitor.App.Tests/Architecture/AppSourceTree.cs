using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// Source-tree access for the UI.1 XAML guards: the checked-in <c>src/ServerMonitor.App</c> sources, never
/// <c>bin/</c> or <c>obj/</c> copies. Paths are returned app-relative with forward slashes
/// (<c>Views/DashboardPage.xaml</c>) so failure messages and baselines are stable across machines.
/// </summary>
internal static partial class AppSourceTree
{
    public static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static string AppRoot { get; } = Path.Combine(FindRepositoryRoot(), "src", "ServerMonitor.App");

    public static string Full(string relative) => Path.Combine(AppRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>App-relative paths of every source file with the extension, excluding build output.</summary>
    public static IReadOnlyList<string> Files(string extension) =>
        Directory.EnumerateFiles(AppRoot, "*" + extension, SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(AppRoot, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    public static bool IsUnderStyles(string relative) => relative.StartsWith("Styles/", StringComparison.OrdinalIgnoreCase);

    public static XDocument LoadXaml(string relative) => XDocument.Load(Full(relative));

    /// <summary>C# source with // and /* */ comments blanked, so prose in doc comments is never scanned.
    /// String literals are preserved (a "//" inside a string is kept).</summary>
    public static string CodeWithoutComments(string relative) =>
        CommentOrString().Replace(File.ReadAllText(Full(relative)), match =>
            match.Value.StartsWith("//", StringComparison.Ordinal) || match.Value.StartsWith("/*", StringComparison.Ordinal)
                ? " "
                : match.Value);

    /// <summary>
    /// Resource references in a XAML document as (kind, key): markup extensions in attribute values
    /// (<c>{StaticResource X}</c>, <c>{ThemeResource X}</c>, nested or with <c>ResourceKey=</c>; comments are never
    /// scanned) plus the element forms <c>&lt;StaticResource ResourceKey="X"/&gt;</c> / <c>&lt;ThemeResource .../&gt;</c>.
    /// </summary>
    public static IEnumerable<(string Kind, string Key)> XamlResourceReferences(XDocument document)
    {
        foreach (var attribute in document.Descendants().Attributes())
        {
            foreach (Match match in ResourceMarkup().Matches(attribute.Value))
            {
                yield return (match.Groups[1].Value, match.Groups[2].Value);
            }
        }

        foreach (var element in document.Descendants().Where(e => e.Name.LocalName is "StaticResource" or "ThemeResource"))
        {
            if (element.Attribute("ResourceKey") is { } key)
            {
                yield return (element.Name.LocalName, key.Value);
            }
        }
    }

    [GeneratedRegex(@"\{(StaticResource|ThemeResource)\s+(?:ResourceKey\s*=\s*)?([^\s,}]+)")]
    private static partial Regex ResourceMarkup();

    [GeneratedRegex("""//[^\n]*|/\*.*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])+'""", RegexOptions.Singleline)]
    private static partial Regex CommentOrString();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate ServerMonitor.slnx from test output.");
    }
}
