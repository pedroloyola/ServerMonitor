using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.ViewModels;

/// <summary>What the last load of <c>~/.ssh/config</c> ended with (UI.7B import dialog states, Figma 07).</summary>
public enum SshConfigLoadOutcome
{
    None,
    NotFound,
    Error,
    NoHosts,
    Hosts
}

public enum HostKeyPromptKind
{
    /// <summary>A key nobody trusted yet: it can be accepted explicitly (Figma 09 112:7242 / PJ 159:413).</summary>
    Unknown,

    /// <summary>A key that differs from the trusted one: blocked, never accepted from here (112:7621 / 159:809).</summary>
    Mismatch
}

/// <summary>
/// UI.7B (B-10): the content of the trust dialog, a snapshot of the view model's prompt. It only describes what is on
/// screen; accepting goes through <see cref="ServerEditorPageController.AcceptTrustAsync"/> to the SAME
/// <see cref="ServerEditorViewModel.TrustAndConnectAsync"/>, and only while the pending key is still this one.
/// "PASSO 1/2 DE 2" exists only for the hops of a routed server (jump = 1, target = 2).
/// </summary>
public sealed record HostKeyTrustPrompt(
    HostKeyPromptKind Kind,
    SshHostKeyHop Hop,
    string Subject,
    string Algorithm,
    string Fingerprint,
    string TrustedFingerprint)
{
    public bool CanAccept => Kind == HostKeyPromptKind.Unknown;

    /// <summary>1 or 2 of a two-step routed trust; null for a direct server.</summary>
    public int? Step => Hop switch
    {
        SshHostKeyHop.Jump => 1,
        SshHostKeyHop.Target => 2,
        _ => null
    };

    public string TitleKey => (Kind, Hop) switch
    {
        (HostKeyPromptKind.Unknown, SshHostKeyHop.Jump) => "ServerEditorTrustJumpTitle",
        (HostKeyPromptKind.Unknown, SshHostKeyHop.Target) => "ServerEditorTrustTargetTitle",
        (HostKeyPromptKind.Unknown, _) => "ServerEditorTrustUnknownTitle",
        (_, SshHostKeyHop.Jump) => "ServerEditorTrustJumpChangedTitle",
        (_, SshHostKeyHop.Target) => "ServerEditorTrustTargetChangedTitle",
        _ => "ServerEditorTrustMismatchTitle"
    };

    public string BodyKey => (Kind, Hop) switch
    {
        (HostKeyPromptKind.Unknown, SshHostKeyHop.Jump) => "ServerEditorTrustJumpBody",
        (HostKeyPromptKind.Unknown, SshHostKeyHop.Target) => "ServerEditorTrustTargetBody",
        (HostKeyPromptKind.Unknown, _) => "ServerEditorTrustUnknownBody",
        (_, SshHostKeyHop.Jump) => "ServerEditorTrustJumpChangedBody",
        (_, SshHostKeyHop.Target) => "ServerEditorTrustTargetChangedBody",
        _ => "ServerEditorTrustMismatchBody"
    };

    public string ScopeKey => (Kind, Hop) switch
    {
        (HostKeyPromptKind.Unknown, SshHostKeyHop.Jump) => "ServerEditorTrustJumpScope",
        (HostKeyPromptKind.Unknown, SshHostKeyHop.Target) => "ServerEditorTrustTargetScope",
        (HostKeyPromptKind.Unknown, _) => "ServerEditorTrustUnknownScope",
        _ => "ServerEditorTrustMismatchScope"
    };

    /// <summary>The accept button's text; a mismatch has none (only "Voltar ao formulário").</summary>
    public string? AcceptKey => Kind == HostKeyPromptKind.Unknown
        ? Hop == SshHostKeyHop.Direct ? "ServerEditorTrustAcceptTest" : "ServerEditorTrustAcceptContinue"
        : null;

    public string CloseKey => CanAccept ? "ServerEditorTrustCancel" : "ServerEditorTrustBack";

    /// <summary>The prompt the view model shows now, or null. Read-only: building it changes nothing.</summary>
    public static HostKeyTrustPrompt? From(ServerEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (viewModel.HostKeyPromptHop is not { } hop)
        {
            return null;
        }

        if (viewModel.HasHostKeyMismatch)
        {
            return new HostKeyTrustPrompt(
                HostKeyPromptKind.Mismatch,
                hop,
                viewModel.HostKeySubjectDisplay,
                viewModel.PresentedHostKeyAlgorithm,
                viewModel.PresentedHostKeyFingerprint,
                viewModel.TrustedHostKeyFingerprint);
        }

        return viewModel.HasUnknownHostKey && viewModel.PresentedHostKeyFingerprint.Length > 0
            ? new HostKeyTrustPrompt(
                HostKeyPromptKind.Unknown,
                hop,
                viewModel.HostKeySubjectDisplay,
                viewModel.PresentedHostKeyAlgorithm,
                viewModel.PresentedHostKeyFingerprint,
                string.Empty)
            : null;
    }
}

/// <summary>
/// UI.7B (B-12, G-3/G-4/G-17): the key picker button. It shows the chosen file's NAME (the full path only in the
/// tooltip and the UIA HelpText), and its menu lists the keys discovery found (metadata only) plus a browse entry.
/// There is no editable path box.
/// </summary>
public static class ServerEditorKeyPicker
{
    /// <summary>The file name of a chosen key, or null when none is chosen.</summary>
    public static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return name.Length == 0 ? trimmed : name;
    }

    /// <summary>"id_ed25519 · Alterar ficheiro…" once a key is chosen, "Escolher ficheiro…" before.</summary>
    public static string ButtonText(string? path, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return FileName(path) is { } name
            ? string.Format(CultureInfo.CurrentCulture, localization.GetString("ServerEditorKeyPickerChosenFormat"), name)
            : localization.GetString("ServerEditorKeyPickerChoose");
    }

    /// <summary>
    /// The menu: the discovered keys followed by the view model's own browse entry, or - when discovery found
    /// nothing - a single browse entry. Never a key that was not discovered.
    /// </summary>
    public static IReadOnlyList<LocalKeyOptionViewModel> MenuItems(
        IReadOnlyList<LocalKeyOptionViewModel> discovered,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(localization);
        return discovered.Count > 0
            ? discovered
            : [new LocalKeyOptionViewModel(null, localization.GetString("ServerEditorKeyPickerBrowse"))];
    }
}

/// <summary>UI.7B (B-13): the derived route line under the jump cards (Figma 155:1165), never stored anywhere.</summary>
public static class ServerEditorRouteLine
{
    public static string Describe(string jumpHost, string host, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        var jump = (jumpHost ?? string.Empty).Trim();
        if (jump.Length == 0)
        {
            return localization.GetString("ServerEditorRouteJumpPending");
        }

        var target = (host ?? string.Empty).Trim();
        return string.Format(
            CultureInfo.CurrentCulture,
            localization.GetString("ServerEditorRouteFormat"),
            jump,
            target.Length == 0 ? localization.GetString("ServerEditorRouteTargetPending") : target);
    }
}

/// <summary>
/// UI.7B (B-11): the import dialog's state, from the view model's existing load result and the resolver's own
/// classification (<see cref="SshConfigHostOptionViewModel.IsImportable"/>). A blocked profile is never offered as
/// fixable: it can be read, never used.
/// </summary>
public static class SshConfigImportPresentation
{
    public static int Available(IReadOnlyList<SshConfigHostOptionViewModel> hosts) => hosts.Count(host => host.IsImportable);

    public static int Blocked(IReadOnlyList<SshConfigHostOptionViewModel> hosts) => hosts.Count(host => !host.IsImportable);

    /// <summary>"Usar perfil" is possible only for a selected, importable profile.</summary>
    public static bool CanUse(SshConfigHostOptionViewModel? selected) => selected is { IsImportable: true };

    public static string StateTitleKey(SshConfigLoadOutcome outcome) => outcome switch
    {
        SshConfigLoadOutcome.NotFound => "ServerEditorImportNotFoundTitle",
        SshConfigLoadOutcome.Error => "ServerEditorImportErrorTitle",
        SshConfigLoadOutcome.NoHosts => "ServerEditorImportNoHostsTitle",
        _ => string.Empty
    };
}
