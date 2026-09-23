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
        ClassificationText = string.Join(
            Environment.NewLine,
            new[]
            {
                (Kind: SshConfigFindingKind.Invalid, Key: "SshConfigHostInvalidFormat"),
                (Kind: SshConfigFindingKind.Unsupported, Key: "SshConfigHostUnsupportedFormat"),
                (Kind: SshConfigFindingKind.Ambiguous, Key: "SshConfigHostAmbiguousFormat"),
                (Kind: SshConfigFindingKind.Ignored, Key: "SshConfigHostIgnoredFormat")
            }
            .Select(group => (group.Key, Keywords: entry.Findings
                .Where(finding => finding.Kind == group.Kind)
                .Select(finding => finding.Keyword)
                .ToList()))
            .Where(group => group.Keywords.Count > 0)
            .Select(group => Format(localization, group.Key, string.Join(", ", group.Keywords)))
            .Concat(entry.FindingsTruncated ? ["…"] : []));
    }

    public SshConfigHostEntry Entry { get; }

    public string Alias => Entry.Alias;

    public bool IsImportable => Entry.IsImportable;

    public string UseAutomationName { get; }

    public string Preview { get; }

    public string RequirementText { get; }

    public bool HasRequirement => RequirementText.Length > 0;

    public string ClassificationText { get; }

    public bool HasClassification => ClassificationText.Length > 0;

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
    }

    private static string Format(ILocalizationService localization, string key, string argument) =>
        string.Format(CultureInfo.CurrentCulture, localization.GetString(key), argument);
}
