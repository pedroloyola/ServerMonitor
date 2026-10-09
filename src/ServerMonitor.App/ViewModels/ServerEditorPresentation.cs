using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.SshConfig;

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

    /// <summary>
    /// The helper under the target's key picker. The view model's own hint ("Chave encontrada em .ssh", "no key found")
    /// wins; Edit with the saved key still chosen says it is the current one (Figma 05 112:4737) - "· pasta .ssh deste
    /// dispositivo" only when that key really is one discovered in .ssh; anything else gets the Add pointer.
    /// </summary>
    public static string Helper(
        bool isEdit,
        string? path,
        string? savedPath,
        bool discoveredInSshFolder,
        string hint,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        if (!string.IsNullOrEmpty(hint))
        {
            return hint;
        }

        var current = isEdit
            && !string.IsNullOrWhiteSpace(path)
            && !string.IsNullOrWhiteSpace(savedPath)
            && string.Equals(path.Trim(), savedPath.Trim(), StringComparison.OrdinalIgnoreCase);
        return localization.GetString(current
            ? discoveredInSshFolder ? "ServerEditorKeyPickerCurrentSshHelper" : "ServerEditorKeyPickerCurrentHelper"
            : "ServerEditorKeyPickerHelper");
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

    /// <summary>Final c1 (Prism F-2): "1 disponível · 2 bloqueados" - each count with its own singular / plural key.</summary>
    public static string CountText(int available, int blocked, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return string.Format(CultureInfo.CurrentCulture, localization.GetString(available == 1 ? "ServerEditorImportAvailableCountOne" : "ServerEditorImportAvailableCountOther"), available)
            + " · "
            + string.Format(CultureInfo.CurrentCulture, localization.GetString(blocked == 1 ? "ServerEditorImportBlockedCountOne" : "ServerEditorImportBlockedCountOther"), blocked);
    }

    /// <summary>
    /// Final c1 (Prism F-2, Figma 04): a row's compact line "{host} · {user} · porta {n}" - only the values the file gives
    /// (nothing is assumed: no default port is written), never the key path.
    /// </summary>
    public static string Detail(SshConfigHostEntry entry, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localization);
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(entry.HostName))
        {
            parts.Add(entry.HostName);
        }

        if (!string.IsNullOrWhiteSpace(entry.User))
        {
            parts.Add(entry.User);
        }

        if (entry.Port is { } port)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("ServerEditorImportRowPortFormat"), port.ToString(CultureInfo.InvariantCulture)));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"Via jump host bastion" for a routed profile, empty for a direct one.</summary>
    public static string Jump(SshConfigHostEntry entry, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localization);
        return entry.Jump is { } jump
            ? string.Format(CultureInfo.CurrentCulture, localization.GetString("ServerEditorImportRowJumpFormat"), jump.Name)
            : string.Empty;
    }

    /// <summary>Final c2 (Prism C2-4): what a row's state column says.</summary>
    public static SshConfigImportRowState RowState(bool importable, bool selected) =>
        !importable ? SshConfigImportRowState.Blocked : selected ? SshConfigImportRowState.Selected : SshConfigImportRowState.Available;

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

/// <summary>UI.7 final c2 (Prism C2-4): the import row's state column ("Disponível" / "Selecionado" / "Bloqueado").</summary>
public enum SshConfigImportRowState
{
    Available,
    Selected,
    Blocked
}

/// <summary>UI.7C (B-9, Figma 08): where the connection test is.</summary>
public enum ConnectionTestPhase
{
    None,
    Testing,
    Verified,
    Failed,
    Cancelled
}

/// <summary>
/// UI.7C (B-9, Figma 08 112:5594 / PJ 160:413): the test dialog's chrome, a read-only snapshot of the view model. The
/// phase comes from <see cref="ServerEditorViewModel.IsTestingConnection"/> and the last result's state; the failure
/// family (title, body, "Rever …" wording) only from its <see cref="SshConnectionErrorCode"/> - never from any message
/// text. The rows themselves are the view model's four real stages (<see cref="ConnectionChecklistViewModel"/>), for a
/// direct AND a routed server: the jump's name and cause live in stage 1 (G-7, no fake six-row list).
/// </summary>
public sealed record ConnectionTestView(
    ConnectionTestPhase Phase,
    ConnectionFailureFamily Family,
    bool Routed)
{
    /// <summary>
    /// UI.10 one sign per state (F18/F20): "a testar" is progress (<see cref="ShowsProgress"/>, no icon - Refresh is an
    /// ACTION), passed = Tick (as each passed stage), failed = Alert; the Shield stays for host identity only (F17).
    /// </summary>
    public string? IconKey => Phase switch
    {
        ConnectionTestPhase.Testing => null,
        ConnectionTestPhase.Verified => "SaIconTick02Data",
        ConnectionTestPhase.Cancelled => "SaIconInformationCircleData",
        _ => "SaIconAlert02Data"
    };

    /// <summary>UI.10 F18: while testing, the title shows the progress indicator the stages already use.</summary>
    public bool ShowsProgress => Phase == ConnectionTestPhase.Testing;

    public string TitleKey => Phase switch
    {
        ConnectionTestPhase.Testing => "ServerEditorTestTestingTitle",
        ConnectionTestPhase.Verified => "ServerEditorTestVerifiedTitle",
        ConnectionTestPhase.Cancelled => "ServerEditorTestCancelledTitle",
        _ => $"ServerEditorTestFailed{Family}Title"
    };

    /// <summary>The body; the testing one is a format with the endpoint ({0}).</summary>
    public string BodyKey => Phase switch
    {
        ConnectionTestPhase.Testing => "ServerEditorTestTestingBodyFormat",
        ConnectionTestPhase.Verified => "ServerEditorTestVerifiedBody",
        ConnectionTestPhase.Cancelled => "ServerEditorTestCancelledBody",
        _ => $"ServerEditorTestFailed{Family}Body"
    };

    /// <summary>"SSH autenticado · Identidade confirmada" - only after a complete, verified test.</summary>
    public string? VerifiedDetailKey => Phase == ConnectionTestPhase.Verified ? "ServerEditorTestVerifiedDetail" : null;

    /// <summary>The safe (left) action: Cancelar teste while testing, else back to the form ("Rever …" after a failure).</summary>
    public string CloseKey => Phase switch
    {
        ConnectionTestPhase.Testing => "ServerFormCancelTestButton.Content",
        ConnectionTestPhase.Failed when Routed && Family is ConnectionFailureFamily.Authentication or ConnectionFailureFamily.Jump
            => "ServerEditorTestReviewCredentials",
        ConnectionTestPhase.Failed when Routed => "ServerEditorTestReviewRoute",
        ConnectionTestPhase.Failed => "ServerEditorTestReviewData",
        _ => "ServerEditorTrustBack"
    };

    /// <summary>The emphasised (right) action, or null: Tentar novamente after a failure, Testar novamente after a cancel.</summary>
    public string? RetryKey => Phase switch
    {
        ConnectionTestPhase.Failed => "ServerEditorTestRetry",
        ConnectionTestPhase.Cancelled => "ServerEditorTestAgain",
        _ => null
    };

    /// <summary>The dialog for the view model now; <see cref="ConnectionTestPhase.None"/> before any test.</summary>
    public static ConnectionTestView From(ServerEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var routed = viewModel.UseJumpHost;
        if (viewModel.IsTestingConnection)
        {
            return new ConnectionTestView(ConnectionTestPhase.Testing, ConnectionFailureFamily.None, routed);
        }

        return viewModel.LastTestState switch
        {
            null => new ConnectionTestView(ConnectionTestPhase.None, ConnectionFailureFamily.None, routed),
            ServerConnectionState.Connected => new ConnectionTestView(ConnectionTestPhase.Verified, ConnectionFailureFamily.None, routed),
            ServerConnectionState.Cancelled => new ConnectionTestView(ConnectionTestPhase.Cancelled, ConnectionFailureFamily.None, routed),
            _ when viewModel.LastTestErrorCode == SshConnectionErrorCode.Cancelled =>
                new ConnectionTestView(ConnectionTestPhase.Cancelled, ConnectionFailureFamily.None, routed),
            _ => new ConnectionTestView(ConnectionTestPhase.Failed, FamilyOf(viewModel.LastTestErrorCode), routed)
        };
    }

    /// <summary>The failure family of an error code (the mapping table the tests pin, one row per code).</summary>
    public static ConnectionFailureFamily FamilyOf(SshConnectionErrorCode code) => code switch
    {
        SshConnectionErrorCode.None => ConnectionFailureFamily.Protocol,
        SshConnectionErrorCode.InvalidConfiguration
            or SshConnectionErrorCode.DnsResolutionFailed
            or SshConnectionErrorCode.ConnectionRefused
            or SshConnectionErrorCode.HostUnreachable
            or SshConnectionErrorCode.NetworkUnavailable
            or SshConnectionErrorCode.ConnectionTimedOut => ConnectionFailureFamily.Network,
        SshConnectionErrorCode.CredentialNotConfigured
            or SshConnectionErrorCode.CredentialUnavailable
            or SshConnectionErrorCode.PrivateKeyUnavailable
            or SshConnectionErrorCode.PrivateKeyInvalid
            or SshConnectionErrorCode.AuthenticationFailed => ConnectionFailureFamily.Authentication,
        SshConnectionErrorCode.HostKeyUnknown
            or SshConnectionErrorCode.HostKeyMismatch
            or SshConnectionErrorCode.RoutedHostKeyUnknown
            or SshConnectionErrorCode.RoutedHostKeyMismatch => ConnectionFailureFamily.Identity,
        SshConnectionErrorCode.JumpConnectionFailed
            or SshConnectionErrorCode.JumpAuthenticationFailed
            or SshConnectionErrorCode.JumpHostKeyUnknown
            or SshConnectionErrorCode.JumpHostKeyMismatch
            or SshConnectionErrorCode.JumpCredentialUnavailable => ConnectionFailureFamily.Jump,
        SshConnectionErrorCode.TargetUnreachableViaJump => ConnectionFailureFamily.TargetViaJump,
        SshConnectionErrorCode.LocalTunnelFailed => ConnectionFailureFamily.Tunnel,
        _ => ConnectionFailureFamily.Protocol
    };
}

/// <summary>UI.7C: the families the test dialog words differently (from the error code only).</summary>
public enum ConnectionFailureFamily
{
    None,
    Network,
    Protocol,
    Authentication,
    Identity,
    Jump,
    TargetViaJump,
    Tunnel
}
