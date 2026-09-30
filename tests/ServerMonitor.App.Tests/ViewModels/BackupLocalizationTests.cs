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

    // Vigil-3: every member of every code the UI shows maps EXPLICITLY to a real message. A future enum member
    // then fails here, instead of silently falling back to the generic text (or to a missing key) in the UI.
    [Fact]
    public void Every_code_member_maps_explicitly_to_a_key_present_in_every_culture()
    {
        var keys = new List<(string Member, string Key)>();

        foreach (var problem in Enum.GetValues<BackupPassphraseProblem>().Where(p => p != BackupPassphraseProblem.None))
        {
            keys.Add(($"{nameof(BackupPassphraseProblem)}.{problem}", Specific(BackupMessageKeys.ForPassphraseProblem(problem), problem)));
        }

        // Codes no operation reports to the UI by contract (RolledBack/PartialRestoreRollbackPending live on
        // RestoreApplyOutcome; Canceled is silent). Anything else must have an export or inspect message.
        BackupError[] notSurfaced = [BackupError.RolledBack, BackupError.PartialRestoreRollbackPending, BackupError.Canceled];
        foreach (var error in Enum.GetValues<BackupError>().Except(notSurfaced))
        {
            var export = BackupMessageKeys.ForExportError(error);
            var inspect = BackupMessageKeys.ForInspectError(error);
            var specific = new[] { export, inspect }.FirstOrDefault(key => key is not null && key != BackupMessageKeys.Generic);
            keys.Add(($"{nameof(BackupError)}.{error}", Specific(specific, error)));
        }

        foreach (var outcome in Enum.GetValues<RestoreApplyOutcome>())
        {
            keys.Add(($"{nameof(RestoreApplyOutcome)}.{outcome}", Specific(BackupMessageKeys.ForApplyOutcome(outcome), outcome)));
        }

        foreach (var outcome in Enum.GetValues<RestoreRecoveryOutcome>().Where(o => o != RestoreRecoveryOutcome.NothingToRecover))
        {
            keys.Add(($"{nameof(RestoreRecoveryOutcome)}.{outcome}", Specific(BackupMessageKeys.ForStartupRecovery(outcome), outcome)));
        }

        foreach (var status in Enum.GetValues<KeyPathStatus>())
        {
            keys.Add(($"{nameof(KeyPathStatus)}.{status}", Specific(BackupMessageKeys.ForKeyPathStatus(status), status)));
        }

        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var (member, key) in keys)
            {
                Assert.True(resources.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value), $"{culture}: {member} -> {key} is missing.");
            }
        }
    }

    // Beacon-1 (2nd plural occurrence, BOSS §16 — change the mechanism, not the string): in every M14.6 string a
    // numeric placeholder is a COUNT and must use the "Label: N" form, so no count ever precedes a noun that would
    // need singular/plural. The only exceptions are placeholders that are not counts, or a count continuing a
    // "Label: N now → M" comparison; each is listed here with its reason, so a new plural-prone string fails.
    private static readonly Dictionary<string, string[]> NonCountPlaceholders = new(StringComparer.Ordinal)
    {
        ["BackupCreatedMessage"] = ["{3}"],           // destination path
        ["RestoreOpenMessage"] = ["{0}"],             // file name
        ["RestoreConfirmIntro"] = ["{0}"],            // backup date
        [BackupMessageKeys.Blocked] = ["{0}"],        // journal folder
        ["RestoreSummaryServers"] = ["{1}", "{2}"],   // "Servers: N now → M from the backup (K through a jump host)"
        ["RestoreSummaryTrust"] = ["{1}"],            // "Trusted host keys: N now → M from the backup"
    };

    [Fact]
    public void Every_count_uses_the_label_colon_n_form_in_every_culture()
    {
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var key in RequiredKeys())
            {
                var value = resources[key];
                foreach (Match placeholder in Regex.Matches(value, @"\{\d+\}"))
                {
                    var labelled = placeholder.Index >= 2 && value.Substring(placeholder.Index - 2, 2) == ": ";
                    var allowed = NonCountPlaceholders.TryGetValue(key, out var exceptions) && exceptions.Contains(placeholder.Value);
                    Assert.True(labelled || allowed, $"{culture} {key}: {placeholder.Value} is not in the \"Label: N\" form: {value}");
                }
            }
        }
    }

    private static string Specific(string? key, object member)
    {
        Assert.False(key is null || key == BackupMessageKeys.Generic, $"{member.GetType().Name}.{member} has no explicit message key.");
        return key!;
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
