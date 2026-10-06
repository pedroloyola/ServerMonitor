using System.Xml.Linq;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.4b-2 jump-host editor strings, on the UI.7 editor page's keys (the M14 ServerForm* keys died with the modal, B-22):
/// present and non-empty in every supported culture, and every jump input announces itself distinctly from the target's
/// input with the same visible label (visual QA finding: both ports were exposed to UI Automation as a bare "Port"; UI.7C
/// Beacon: the same for the SSH user, the password/passphrase and the key picker).
/// </summary>
public sealed class ProxyJumpLocalizationTests
{
    private static readonly string[] Cultures = ["pt-BR", "pt-PT", "en-US"];

    private const string AutomationName = ".[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    private static readonly string[] RequiredKeys =
    [
        "ServerEditorUseJumpHost.Content",
        "ServerEditorJumpHostDescription.Text",
        "ServerEditorJumpHostTitle.Text",
        "ServerEditorJumpHostField.Header",
        "ServerEditorJumpHostInput.PlaceholderText",
        "ServerEditorJumpPortField.Header",
        "ServerEditorJumpPortInput" + AutomationName,
        "ServerEditorJumpUsernameField.Header",
        "ServerEditorJumpUsernameInput" + AutomationName,
        "ServerEditorJumpAuthTitle.Text",
        "ServerEditorJumpAuthMethodGroup" + AutomationName,
        "ServerEditorJumpPrivateKeyField.Header",
        "ServerEditorJumpKeyPickerAccessibleHeader",
        "ServerEditorJumpPassphraseField.Header",
        "ServerEditorJumpPassphraseInput" + AutomationName,
        "ServerEditorJumpPasswordField.Header",
        "ServerEditorJumpPasswordInput" + AutomationName,
        "ServerEditorRouteJumpPending",
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

    [Theory]
    [InlineData("ServerEditorPortField.Header", "ServerEditorJumpPortInput" + AutomationName)]
    [InlineData("ServerEditorUsernameField.Header", "ServerEditorJumpUsernameInput" + AutomationName)]
    [InlineData("ServerEditorPasswordHeader", "ServerEditorJumpPasswordInput" + AutomationName)]
    [InlineData("ServerEditorPassphraseHeader", "ServerEditorJumpPassphraseInput" + AutomationName)]
    [InlineData("ServerEditorPrivateKeyField.Header", "ServerEditorJumpKeyPickerAccessibleHeader")]
    public void EveryJumpInput_IsAnnouncedDistinctlyFromTheTargetInput(string targetLabel, string jumpName)
    {
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            Assert.NotEqual(resources[targetLabel], resources[jumpName], StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheJumpInputs_CarryTheirOwnName()
    {
        var form = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "ServerMonitor.App", "Controls", "ServerFormControl.xaml"));
        foreach (var uid in new[] { "ServerEditorJumpPortInput", "ServerEditorJumpUsernameInput", "ServerEditorJumpPasswordInput", "ServerEditorJumpPassphraseInput" })
        {
            Assert.Contains($"x:Uid=\"{uid}\"", form);
        }

        Assert.Contains("ServerEditorJumpKeyPickerAccessibleHeader", File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "ServerMonitor.App", "Controls", "ServerFormControl.xaml.cs")));
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
