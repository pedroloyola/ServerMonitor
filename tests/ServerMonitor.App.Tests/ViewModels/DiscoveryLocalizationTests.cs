using System.Xml.Linq;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class DiscoveryLocalizationTests
{
    private static readonly string[] Cultures = ["pt-BR", "pt-PT", "en-US"];

    private static readonly string[] DiscoveryKeys =
    [
        "DashboardDiscoveryTitle.Text",
        "DashboardDiscoveryDescription.Text",
        "DashboardDiscoveryCountName",
        "DiscoveredServerProtocol.Text",
        "DiscoveredServerState.Text",
        "DiscoveredServerAddButton.Content",
        "DiscoveredServerAddFor",
        "DiscoveredServerIgnoreFor",
        "DiscoveredServerAutomationSummary",
        "SettingsIgnoredDiscoveriesTitle.Text",
        "SettingsIgnoredDiscoveriesDescription.Text",
        "SettingsResetIgnoredButton.Content",
        "SettingsResetIgnoredSuccess.Title",
        "SettingsResetIgnoredSuccess.Message",
        "SettingsResetIgnoredError.Title",
        "SettingsResetIgnoredError.Message"
    ];

    [Fact]
    public void DiscoveryResources_ArePresentAndNonEmptyInEverySupportedCulture()
    {
        var resources = Cultures.ToDictionary(culture => culture, LoadResources);

        foreach (var key in DiscoveryKeys)
        {
            foreach (var culture in Cultures)
            {
                Assert.True(resources[culture].TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture} has an empty {key}.");
            }
        }
    }

    [Fact]
    public void DiscoveryResources_PreserveRequiredDeviceWordingAndLocalizedProtocolLabel()
    {
        var brazilianPortuguese = LoadResources("pt-BR");
        var europeanPortuguese = LoadResources("pt-PT");
        var english = LoadResources("en-US");

        Assert.Equal("Redefinir dispositivos ignorados", brazilianPortuguese["SettingsResetIgnoredButton.Content"]);
        Assert.Equal("Dispositivos ignorados redefinidos", brazilianPortuguese["SettingsResetIgnoredSuccess.Title"]);
        Assert.Equal("Repor dispositivos ignorados", europeanPortuguese["SettingsResetIgnoredButton.Content"]);
        Assert.Equal("Dispositivos ignorados repostos", europeanPortuguese["SettingsResetIgnoredSuccess.Title"]);
        Assert.Equal("Reset ignored devices", english["SettingsResetIgnoredButton.Content"]);
        Assert.Equal("Ignored devices reset", english["SettingsResetIgnoredSuccess.Title"]);

        foreach (var culture in Cultures)
        {
            Assert.Equal("SSH", LoadResources(culture)["DiscoveredServerProtocol.Text"]);
        }
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
