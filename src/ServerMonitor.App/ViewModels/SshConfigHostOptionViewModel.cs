using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// One concrete alias from <c>~/.ssh/config</c> as previewed in the editor: the values the import
/// would offer and how every other keyword was classified. Immutable; built once per load.
/// </summary>
public sealed class SshConfigHostOptionViewModel
{
    public SshConfigHostOptionViewModel(SshConfigHostEntry entry, ILocalizationService localization)
    {
        Entry = entry;
        UseAutomationName = Format(localization, "SshConfigHostUseForFormat", entry.Alias);
        Preview = string.Join(" · ", PreviewParts(entry, localization));
        RequirementText = entry.Blocker == SshConfigHostBlocker.None
            ? string.Empty
            : localization.GetString($"SshConfigHostBlocked{entry.Blocker}");
        // The jump host's own notes (e.g. an ambiguous jump key) follow the host's, on one line.
        var jumpClassification = entry.Jump is { } jump
            ? string.Join("; ", Classify(jump.Findings, jump.FindingsTruncated, localization))
            : string.Empty;
        ClassificationText = string.Join(
            Environment.NewLine,
            Classify(entry.Findings, entry.FindingsTruncated, localization)
                .Concat(jumpClassification.Length == 0
                    ? []
                    : [string.Format(
                        CultureInfo.CurrentCulture,
                        localization.GetString("SshConfigHostJumpClassificationFormat"),
                        entry.Jump!.Name,
                        jumpClassification)]));

        // What a screen reader announces for the list item: alias, importable/blocked (with why), and
        // the classification notes (e.g. an ambiguous key) on one line.
        var state = entry.IsImportable
            ? Format(localization, "SshConfigHostAccessibleImportableFormat", entry.Alias)
            : string.Format(
                CultureInfo.CurrentCulture,
                localization.GetString("SshConfigHostAccessibleBlockedFormat"),
                entry.Alias,
                RequirementText);
        AccessibleName = ClassificationText.Length == 0
            ? state
            : state + ". " + Format(
                localization,
                "SshConfigHostAccessibleNotesFormat",
                ClassificationText.Replace(Environment.NewLine, "; ", StringComparison.Ordinal));
    }

    public SshConfigHostEntry Entry { get; }

    public string Alias => Entry.Alias;

    public bool IsImportable => Entry.IsImportable;

    /// <summary>UI.7B: the resolver blocked this profile; the import dialog shows why and never offers to use it.</summary>
    public bool IsBlocked => !Entry.IsImportable;

    public string UseAutomationName { get; }

    /// <summary>The list item's UI Automation name (set on the ListViewItem container).</summary>
    public string AccessibleName { get; }

    public string Preview { get; }

    /// <summary>
    /// False when the alias offers no value to preview (e.g. <c>HostName %h</c> alone, which is not
    /// resolved): the preview line is then omitted rather than shown blank with an empty UIA name.
    /// </summary>
    public bool HasPreview => Preview.Length > 0;

    public string RequirementText { get; }

    public bool HasRequirement => RequirementText.Length > 0;

    public string ClassificationText { get; }

    public bool HasClassification => ClassificationText.Length > 0;

    /// <summary>Defense in depth: any UIA fallback to ToString reads the same text, never the type name.</summary>
    public override string ToString() => AccessibleName;

    private static IEnumerable<string> Classify(
        IReadOnlyList<SshConfigFinding> findings,
        bool truncated,
        ILocalizationService localization) =>
        new[]
        {
            (Kind: SshConfigFindingKind.Invalid, Key: "SshConfigHostInvalidFormat"),
            (Kind: SshConfigFindingKind.Unsupported, Key: "SshConfigHostUnsupportedFormat"),
            (Kind: SshConfigFindingKind.Ambiguous, Key: "SshConfigHostAmbiguousFormat"),
            (Kind: SshConfigFindingKind.Ignored, Key: "SshConfigHostIgnoredFormat")
        }
        .Select(group => (group.Key, Keywords: findings
            .Where(finding => finding.Kind == group.Kind)
            .Select(finding => finding.Keyword)
            .ToList()))
        .Where(group => group.Keywords.Count > 0)
        .Select(group => Format(localization, group.Key, string.Join(", ", group.Keywords)))
        .Concat(truncated ? ["…"] : []);

    private static IEnumerable<string> PreviewParts(SshConfigHostEntry entry, ILocalizationService localization)
    {
        if (entry.HostName is not null)
        {
            yield return Format(localization, "SshConfigPreviewHostNameFormat", entry.HostName);
        }

        if (entry.User is not null)
        {
            yield return Format(localization, "SshConfigPreviewUserFormat", entry.User);
        }

        if (entry.Port is { } port)
        {
            yield return Format(localization, "SshConfigPreviewPortFormat", port.ToString(CultureInfo.InvariantCulture));
        }

        if (entry.IdentityFile is not null)
        {
            yield return Format(localization, "SshConfigPreviewIdentityFileFormat", entry.IdentityFile);
        }

        if (entry.Jump is { } jump)
        {
            yield return Format(localization, "SshConfigPreviewJumpFormat", jump.Name);
        }
    }

    private static string Format(ILocalizationService localization, string key, string argument) =>
        string.Format(CultureInfo.CurrentCulture, localization.GetString(key), argument);
}
