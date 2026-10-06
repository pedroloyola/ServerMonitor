using System.Xml.Linq;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.5 strings. The editor resolves <c>ConnectionHint{code}</c> by name, so EVERY
/// <see cref="SshConnectionErrorCode"/> except None needs one in every shipped culture; every key the new UI
/// resolves (by x:Uid or from code) must exist and be non-empty; and no diagnosis or helper text may tell the
/// user to use sudo/root.
/// </summary>
public sealed class OnboardingLocalizationTests
{
    private static readonly string[] Cultures = ["pt-BR", "pt-PT", "en-US"];

    private const string AutomationName = ".[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    // Resolved from code (ILocalizationService.GetString).
    private static readonly string[] CodeKeys =
    [
        "LocalKeyOptionRecommendedFormat",
        "LocalKeyOptionBrowse",
        "LocalKeyAutoSelectedHint",
        "LocalKeyNoneFoundHint",
        "ServerPrepCopied",
        "ServerPrepUserPlaceholder",
        "ServerPrepHostPlaceholder",
        "ServerPrepJumpPlaceholder",
        "ConnectionStepPortTitle",
        "ConnectionStepPortViaJumpFormat",
        "ConnectionStepHostKeyTitle",
        "ConnectionStepAuthenticationTitle",
        "ConnectionStepOperatingSystemTitle",
        "ConnectionStepStateOperatingSystemUnknown",
        "ConnectionStepOperatingSystemUnknownHint",
        "ConnectionStepAccessibleFormat",
        "DashboardEmptyDiscoveryFoundFormat",
    ];

    // Resolved by x:Uid in ServerFormControl.xaml / DashboardPage.xaml.
    private static readonly string[] XamlKeys =
    [
        // UI.7B: the two "found keys" combo boxes became the key picker's menu (ServerEditorKeyPicker*), so their
        // ServerForm*LocalKeySelector keys are no longer resolved by a view (dead keys: removal with B-22, 7C).
        "ServerFormPrepHelpLink.Content",
        "ServerPrepTitle.Text",
        "ServerPrepIntro.Text",
        "ServerPrepKeygenStep.Text",
        "ServerPrepCopyKeyStep.Text",
        "ServerPrepPlaceholderNote.Text",
        "ServerPrepServiceStep.Text",
        "ServerPrepCopyKeygenButton.Content",
        "ServerPrepCopyKeygenButton" + AutomationName,
        "ServerPrepCopyKeyButton.Content",
        "ServerPrepCopyKeyButton" + AutomationName,
        "ConnectionStepCopyCommandButton.Content",
        "ConnectionStepCopyCommandButton" + AutomationName,
        // UI.4: the empty dashboard is the SaEmptyState of the Visão geral (Add + Import from SSH + discovery).
        "FirstServerTitle.Text",
        "FirstServerBody.Text",
        "OverviewEmptyAddButton.Content",
        "OverviewEmptyImportButton.Content",
        "DashboardEmptyDiscoverySearching.Text",
    ];

    [Fact]
    public void EveryConnectionErrorCodeButNone_HasAHintInEverySupportedCulture()
    {
        var codes = Enum.GetValues<SshConnectionErrorCode>()
            .Where(code => code != SshConnectionErrorCode.None)
            .ToArray();
        // Guard against a vacuous pass: the enum is what it is today or larger, never an empty sweep.
        Assert.True(codes.Length >= 27, $"Only {codes.Length} error codes were swept.");
        Assert.Contains(SshConnectionErrorCode.HostKeyUnknown, codes);
        Assert.Contains(SshConnectionErrorCode.LocalTunnelFailed, codes);

        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var code in codes)
            {
                var key = ConnectionDiagnosis.HintKey(code);
                Assert.Equal($"ConnectionHint{code}", key);
                Assert.True(resources.TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture} has an empty {key}.");
            }
        }
    }

    [Fact]
    public void EveryStepState_HasALabelInEverySupportedCulture()
    {
        var states = Enum.GetValues<ConnectionStepState>();
        Assert.Equal(7, states.Length);

        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var state in states)
            {
                var key = $"ConnectionStepState{state}";
                Assert.True(resources.TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture} has an empty {key}.");
            }
        }
    }

    [Fact]
    public void OnboardingResources_ArePresentAndNonEmptyInEverySupportedCulture()
    {
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var key in CodeKeys.Concat(XamlKeys))
            {
                Assert.True(resources.TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture} has an empty {key}.");
            }
        }
    }

    [Fact]
    public void FormatStrings_KeepTheirPlaceholders()
    {
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            Assert.Contains("{0}", resources["LocalKeyOptionRecommendedFormat"]);
            Assert.Contains("{0}", resources["ConnectionStepPortViaJumpFormat"]);
            Assert.Contains("{0}", resources["DashboardEmptyDiscoveryFoundFormat"]);
            Assert.Contains("{0}", resources["ConnectionStepAccessibleFormat"]);
            Assert.Contains("{1}", resources["ConnectionStepAccessibleFormat"]);
        }
    }

    [Fact]
    public void Placeholders_AreVisiblyNotRealValues()
    {
        // A placeholder that reached PowerShell unedited must fail to parse, not run against a real name.
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var key in new[] { "ServerPrepUserPlaceholder", "ServerPrepHostPlaceholder", "ServerPrepJumpPlaceholder" })
            {
                Assert.StartsWith("<", resources[key]);
                Assert.EndsWith(">", resources[key]);
                Assert.DoesNotContain(' ', resources[key]);
            }
        }
    }

    [Fact]
    public void NoHintOrHelperText_MentionsSudoOrRootCommands()
    {
        foreach (var culture in Cultures)
        {
            var swept = 0;
            foreach (var (key, value) in LoadResources(culture))
            {
                if (!key.StartsWith("ConnectionHint", StringComparison.Ordinal)
                    && !key.StartsWith("ServerPrep", StringComparison.Ordinal)
                    && !key.StartsWith("ConnectionStep", StringComparison.Ordinal))
                {
                    continue;
                }

                swept++;
                Assert.DoesNotContain("sudo", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("su -", value, StringComparison.OrdinalIgnoreCase);
                // The helper's intro is the one place "root" appears, and only to say it is NOT needed.
                if (key != "ServerPrepIntro.Text")
                {
                    Assert.DoesNotContain("root", value, StringComparison.OrdinalIgnoreCase);
                }
            }

            Assert.True(swept >= 50, $"{culture}: only {swept} onboarding strings were swept.");
        }
    }

    [Fact]
    public void DiagnosisCommands_AreFixedReadOnlyAndNeverElevated()
    {
        var commands = Enum.GetValues<SshConnectionErrorCode>()
            .Select(ConnectionDiagnosis.CommandFor)
            .Where(command => command is not null)
            .ToArray();
        Assert.NotEmpty(commands);
        Assert.Null(ConnectionDiagnosis.CommandFor(SshConnectionErrorCode.None));
        Assert.Equal("systemctl status ssh", ConnectionDiagnosis.CommandFor(SshConnectionErrorCode.ConnectionRefused));

        foreach (var command in commands)
        {
            Assert.DoesNotContain("sudo", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{", command);
            Assert.DoesNotContain(">", command);
            Assert.DoesNotContain("rm ", command);
        }
    }

    // RemoteDisconnected can arrive with nothing reached (the TCP connect itself is not observable), so its
    // hint sits under the port step: it must not state that the port is closed or refused.
    [Fact]
    public void RemoteDisconnectedHint_DoesNotClaimThePortIsClosed()
    {
        string[] forbidden = ["porta", "port ", "port.", "recus", "refus", "systemctl"];

        foreach (var culture in Cultures)
        {
            var hint = LoadResources(culture)[ConnectionDiagnosis.HintKey(SshConnectionErrorCode.RemoteDisconnected)];
            foreach (var word in forbidden)
            {
                Assert.DoesNotContain(word, hint, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // The unit is "ssh" on Debian/Ubuntu and "sshd" on RHEL/Fedora: the refused-port hint names both.
    [Fact]
    public void ConnectionRefusedHint_NamesTheSshdUnitToo()
    {
        foreach (var culture in Cultures)
        {
            var hint = LoadResources(culture)[ConnectionDiagnosis.HintKey(SshConnectionErrorCode.ConnectionRefused)];
            Assert.Contains("systemctl status sshd", hint);
            Assert.DoesNotContain("sudo", hint, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void HostKeyMismatchHints_NeverSuggestAcceptingTheNewKey()
    {
        var codes = new[]
        {
            SshConnectionErrorCode.HostKeyMismatch,
            SshConnectionErrorCode.JumpHostKeyMismatch,
            SshConnectionErrorCode.RoutedHostKeyMismatch
        };
        string[] forbidden = ["accept", "aceita", "aceite", "confiar", "confie", "trust it", "ignor"];

        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var code in codes)
            {
                var hint = resources[ConnectionDiagnosis.HintKey(code)];
                foreach (var word in forbidden)
                {
                    Assert.DoesNotContain(word, hint, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void JumpHints_SayItIsTheJumpHost()
    {
        var jumpCodes = Enum.GetValues<SshConnectionErrorCode>()
            .Where(code => code.ToString().StartsWith("Jump", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(5, jumpCodes.Length);

        foreach (var (culture, term) in new[] { ("pt-PT", "anfitrião de salto"), ("pt-BR", "host de salto"), ("en-US", "jump host") })
        {
            var resources = LoadResources(culture);
            foreach (var code in jumpCodes)
            {
                Assert.Contains(term, resources[ConnectionDiagnosis.HintKey(code)], StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void RemovedInfoBarResource_IsGoneFromResourcesAndXaml()
    {
        // The status InfoBar became the checklist. Its ".Title" left on another element type would crash the
        // x:Uid lookup at runtime, so neither the key nor the uid may survive.
        foreach (var culture in Cultures)
        {
            Assert.DoesNotContain(
                LoadResources(culture).Keys,
                key => key.StartsWith("ServerFormConnectionStatus.", StringComparison.Ordinal));
        }

        var form = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "ServerMonitor.App", "Controls", "ServerFormControl.xaml"));
        Assert.DoesNotContain("ServerFormConnectionStatus", form);
        // UI.7C (B-9): the checklist is the test dialog's stage list now; no inline checklist (or its title) in the form.
        Assert.DoesNotContain("ServerFormChecklistTitle", form);
        var testPanel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "ServerMonitor.App", "Views", "ServerEditorTestPanel.xaml"));
        Assert.Contains("x:Name=\"StageList\"", testPanel);
    }

    [Fact]
    public void EveryXamlKey_IsActuallyReferencedByItsView()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "ServerMonitor.App", "Controls", "ServerFormControl.xaml"))
            + File.ReadAllText(Path.Combine(root, "src", "ServerMonitor.App", "Views", "DashboardPage.xaml"))
            + File.ReadAllText(Path.Combine(root, "src", "ServerMonitor.App", "Views", "ServerEditorTestPanel.xaml"));

        foreach (var uid in XamlKeys.Select(key => key[..key.IndexOf('.')]).Distinct())
        {
            Assert.Contains($"x:Uid=\"{uid}\"", xaml);
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
