using System.Text.RegularExpressions;
using System.Xml.Linq;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>M14.6: every key the backup/restore UI can ask for exists, non-empty, in all three cultures,
/// with the same placeholders.</summary>
public sealed class BackupLocalizationTests
{
    private static readonly string[] Cultures = ["pt-BR", "pt-PT", "en-US"];

    private static readonly string[] FixedKeys =
    [
        "BackupSectionTitle.Text",
        "BackupSectionDescription.Text",
        "BackupCreateButton.Content",
        "BackupRestoreButton.Content",
        "BackupFileTypeLabel",
        "BackupCreateTitle",
        "BackupPassphraseLabel",
        "BackupPassphraseConfirmLabel",
        "BackupPassphraseHint",
        "BackupNoRecoveryWarning",
        "BackupContainsSecretsNote",
        "BackupCreatePrimary",
        "Cancel",
        "BackupCreatedTitle",
        "BackupCreatedMessage",
        "BackupMissingCredentialsWarning",
        "BackupExcludedEntriesWarning",
        "BackupExcludedTrustNote",
        "BackupFailedTitle",
        "RestoreOpenTitle",
        "RestoreOpenMessage",
        "RestoreOpenPrimary",
        "RestoreOpening",
        "RestoreConfirmTitle",
        "RestoreConfirmIntro",
        "RestoreSummaryServers",
        "RestoreSummaryTrust",
        "RestoreSummaryTrustRemoved",
        "RestoreSummaryTrustDelegated",
        "RestoreSummaryPasswords",
        "RestoreSummaryMissingPasswords",
        "RestoreSummarySettings",
        "RestoreSummaryHistoryKept",
        "RestoreSummaryUnknown",
        "RestoreSummarySome",
        "RestoreConfirmNote",
        "RestoreConfirmPrimary",
        "RestoreApplying",
        "RestoreCompletedTitle",
        "RestoreCompletedPrimary",
        "RestoreRolledBackTitle",
        BackupMessageKeys.ConfigurationLocked,
        BackupMessageKeys.Blocked,
        // Existing resources the flow reuses.
        BackupMessageKeys.Generic,
        "ServerOperationError.Title",
        "AppWindowTitle",
        "ServerFormSshConfigCloseButton.Content"
    ];

    public static IEnumerable<string> RequiredKeys() => FixedKeys
        .Concat(Enum.GetValues<BackupPassphraseProblem>().Select(BackupMessageKeys.ForPassphraseProblem).OfType<string>())
        .Concat(Enum.GetValues<BackupError>().Select(BackupMessageKeys.ForExportError).OfType<string>())
        .Concat(Enum.GetValues<BackupError>().Select(BackupMessageKeys.ForInspectError).OfType<string>())
        .Concat(Enum.GetValues<RestoreApplyOutcome>().Select(BackupMessageKeys.ForApplyOutcome))
        .Concat(Enum.GetValues<RestoreRecoveryOutcome>().Select(BackupMessageKeys.ForStartupRecovery).OfType<string>())
        .Concat(Enum.GetValues<KeyPathStatus>().Select(BackupMessageKeys.ForKeyPathStatus))
        .Distinct(StringComparer.Ordinal);

    [Fact]
    public void Every_key_the_flow_can_ask_for_exists_in_every_culture()
    {
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var key in RequiredKeys())
            {
                Assert.True(resources.TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture} has an empty {key}.");
            }
        }
    }

    [Fact]
    public void Placeholders_match_across_cultures()
    {
        var reference = LoadResources("en-US");
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var key in RequiredKeys())
            {
                Assert.Equal(Placeholders(reference[key]), Placeholders(resources[key]));
            }
        }
    }

    /// <summary>Spec §6.4: the two causes cannot be told apart and the wording says so, in every language.</summary>
    [Fact]
    public void The_wrong_passphrase_message_is_the_exact_contract_wording_in_english()
    {
        Assert.Equal(
            "The passphrase is wrong, or the file is damaged or was modified. Nothing was changed.",
            LoadResources("en-US")["RestoreErrorWrongPassphraseOrDamaged"]);
    }

    [Fact]
    public void Every_passphrase_problem_except_none_has_a_message()
    {
        foreach (var problem in Enum.GetValues<BackupPassphraseProblem>())
        {
            var key = BackupMessageKeys.ForPassphraseProblem(problem);
            Assert.Equal(problem == BackupPassphraseProblem.None, key is null);
        }
    }

    private static string Placeholders(string value) =>
        string.Join(",", Regex.Matches(value, @"\{\d+\}").Select(match => match.Value).Distinct().Order());

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
