using System.Xml.Linq;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.4b-2 jump-host editor strings: present and non-empty in every supported culture, and the jump port
/// announces itself distinctly from the target port to assistive technology (visual QA finding: both fields
/// were exposed to UI Automation as a bare "Port").
/// </summary>
public sealed class ProxyJumpLocalizationTests
{
    private static readonly string[] Cultures = ["pt-BR", "pt-PT", "en-US"];

    private const string JumpPortAutomationName =
        "ServerFormJumpPortField.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    private static readonly string[] RequiredKeys =
    [
        "ServerFormRouteTitle.Text",
        "ServerFormUseJumpHost.Content",
        "ServerFormJumpHostHint.Text",
        "ServerFormJumpHostField.Header",
        "ServerFormJumpHostField.PlaceholderText",
        "ServerFormJumpPortField.Header",
        JumpPortAutomationName,
        "ServerFormJumpUsernameField.Header",
        "ServerFormJumpAuthenticationMethodField.Header",
        "JumpAuthenticationSshKeyOption.Content",
        "JumpAuthenticationPasswordOption.Content",
        "ServerFormJumpPrivateKeyPathField.Header",
        "ServerFormChooseJumpPrivateKeyButton.Content",
        "ServerFormJumpPassphraseField.Header",
        "ServerFormJumpPasswordField.Header",
        "ServerFormSavedJumpSecretHint.Text",
        "HostKeySubjectJumpFormat",
        "HostKeySubjectTargetFormat",
    ];

    [Fact]
    public void JumpHostResources_ArePresentAndNonEmptyInEverySupportedCulture()
    {
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var key in RequiredKeys)
            {
                Assert.True(resources.TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture} has an empty {key}.");
            }
        }
    }

    [Fact]
    public void JumpPort_IsAnnouncedDistinctlyFromTheTargetPort()
    {
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);

            Assert.NotEqual(
                resources["ServerFormPortField.Header"],
                resources[JumpPortAutomationName],
                StringComparer.OrdinalIgnoreCase);
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
