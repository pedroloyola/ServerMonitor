using System.Xml.Linq;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// SVA-6 (B12H-01): the server editor's example placeholders spell out "Por exemplo:" in both Portuguese cultures.
/// Narrator reads the abbreviation "Ex.:" letter by letter or as "ex", so the example is announced without its cue.
/// </summary>
public sealed class ServerEditorPlaceholderCopyTests
{
    private static readonly string[] PlaceholderKeys =
    [
        "ServerEditorNameInput.PlaceholderText",
        "ServerEditorHostInput.PlaceholderText",
        "ServerEditorUsernameInput.PlaceholderText",
        "ServerEditorJumpHostInput.PlaceholderText",
        "ServerEditorJumpUsernameInput.PlaceholderText",
    ];

    [Theory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void ExamplePlaceholders_SpellOutPorExemplo(string culture)
    {
        var resources = LoadResources(culture);
        foreach (var key in PlaceholderKeys)
        {
            Assert.True(resources.TryGetValue(key, out var value), $"{culture} is missing {key}.");
            Assert.StartsWith("Por exemplo: ", value, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void NoResourceValue_UsesTheExAbbreviation(string culture)
    {
        var offenders = LoadResources(culture)
            .Where(entry => entry.Value.Contains("Ex.:", StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToList();

        Assert.Empty(offenders);
    }

    private static IReadOnlyDictionary<string, string> LoadResources(string culture)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ServerMonitor.App",
            "Resources",
            culture,
            "Resources.resw");
        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ServerMonitor.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
