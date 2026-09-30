using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// M14.5 — the four-step "Test connection" checklist: SSH port → server identity (SHA-256) → authentication →
/// operating system. Live progress comes from <see cref="SshConnectionRequest.StageProgress"/>; the final
/// picture comes from the result's <see cref="SshConnectionResult.ReachedStage"/> and error code. The step
/// after the reached stage is the one that failed, with two exceptions where the error code's FAMILY names
/// the step outright, because the reached stage is monotonic and can be ahead of such a failure: a host-key
/// mismatch is always the identity step, and a jump-host failure is always the first step (the port, as
/// reached through the jump). Steps that did pass stay passed.
/// </summary>
public sealed class ConnectionChecklistViewModel : ObservableObject
{
    private readonly ILocalizationService _localizationService;
    private readonly ConnectionStepViewModel[] _steps;
    private SshConnectionStage _reported = SshConnectionStage.None;
    private bool _isRunning;
    private string _announcement = string.Empty;

    public ConnectionChecklistViewModel(ILocalizationService localizationService)
    {
        _localizationService = localizationService;
        _steps =
        [
            new(SshConnectionStage.PortReachable, Text("ConnectionStepPortTitle"), localizationService),
            new(SshConnectionStage.HostKeyVerified, Text("ConnectionStepHostKeyTitle"), localizationService),
            new(SshConnectionStage.Authenticated, Text("ConnectionStepAuthenticationTitle"), localizationService),
            new(SshConnectionStage.OperatingSystemIdentified, Text("ConnectionStepOperatingSystemTitle"), localizationService)
        ];
    }

    public IReadOnlyList<ConnectionStepViewModel> Steps => _steps;

    /// <summary>The last stage reported or reached in the current test.</summary>
    public SshConnectionStage ReportedStage => _reported;

    /// <summary>What changed last, as one sentence for assistive technology (raised as a live notification).</summary>
    public string Announcement
    {
        get => _announcement;
        private set => SetProperty(ref _announcement, value);
    }

    /// <summary>Starts a test: every step pending, the first one running. A routed server names its jump host.</summary>
    public void Begin(string? jumpDisplay)
    {
        _reported = SshConnectionStage.None;
        _isRunning = true;
        _steps[0].Title = string.IsNullOrWhiteSpace(jumpDisplay)
            ? Text("ConnectionStepPortTitle")
            : string.Format(CultureInfo.CurrentCulture, Text("ConnectionStepPortViaJumpFormat"), jumpDisplay);
        ApplyRunning();
    }

    /// <summary>Live progress. Ignored outside a running test, and never moves backwards.</summary>
    public void Report(SshConnectionStage stage)
    {
        if (!_isRunning || stage <= _reported || !Enum.IsDefined(stage))
        {
            return;
        }

        _reported = stage;
        ApplyRunning();
    }

    /// <summary>Ends the test with its result.</summary>
    public void Complete(SshConnectionResult result)
    {
        _isRunning = false;
        var reached = result.ReachedStage;
        if (result.IsSuccess && reached < SshConnectionStage.Authenticated)
        {
            // A verified connection cannot exist without the port, the trusted identity and the login.
            reached = SshConnectionStage.Authenticated;
        }

        _reported = reached;
        var failing = result.IsSuccess
            ? null
            : StepNamedByFamily(result.ErrorCode) ?? _steps.FirstOrDefault(step => step.Stage > reached) ?? _steps[^1];
        foreach (var step in _steps)
        {
            if (ReferenceEquals(step, failing))
            {
                ApplyFailure(step, result);
            }
            else if (step.Stage <= reached)
            {
                step.Set(
                    ConnectionStepState.Passed,
                    step.Stage == SshConnectionStage.OperatingSystemIdentified
                        ? OperatingSystemName(result.DetectedOperatingSystem)
                        : null);
            }
            else if (result.IsSuccess)
            {
                // Only the operating system can be left over on a success: connected, but uname told us nothing.
                step.Set(
                    ConnectionStepState.Warning,
                    Text("ConnectionStepStateOperatingSystemUnknown"),
                    Text("ConnectionStepOperatingSystemUnknownHint"));
            }
            else
            {
                step.Set(ConnectionStepState.NotReached);
            }
        }

        Announcement = string.Join(" ", _steps.Select(step => EndSentence(step.Summary)));
    }

    public void Reset()
    {
        _isRunning = false;
        _reported = SshConnectionStage.None;
        foreach (var step in _steps)
        {
            step.Set(ConnectionStepState.Pending);
        }

        Announcement = string.Empty;
    }

    // A key that changes after the probe verified it, or a jump that drops during the target session, fails
    // with a reached stage beyond the step that actually broke; the code's family says which step that is.
    private ConnectionStepViewModel? StepNamedByFamily(SshConnectionErrorCode code) => code switch
    {
        SshConnectionErrorCode.HostKeyMismatch
            or SshConnectionErrorCode.RoutedHostKeyMismatch => _steps[1],
        SshConnectionErrorCode.JumpConnectionFailed
            or SshConnectionErrorCode.JumpAuthenticationFailed
            or SshConnectionErrorCode.JumpHostKeyUnknown
            or SshConnectionErrorCode.JumpHostKeyMismatch
            or SshConnectionErrorCode.JumpCredentialUnavailable
            or SshConnectionErrorCode.TargetUnreachableViaJump
            or SshConnectionErrorCode.LocalTunnelFailed => _steps[0],
        _ => null
    };

    private void ApplyFailure(ConnectionStepViewModel step, SshConnectionResult result)
    {
        var code = result.ErrorCode;
        var hint = code == SshConnectionErrorCode.None ? null : Text(ConnectionDiagnosis.HintKey(code));
        if (result.State == ServerConnectionState.Cancelled || code == SshConnectionErrorCode.Cancelled)
        {
            step.Set(ConnectionStepState.NotReached, detail: hint);
        }
        else if (result.State == ServerConnectionState.HostKeyUnknown && result.PresentedHostKey is not null)
        {
            // First sight of this key: nothing failed, the user confirms it in the trust panel, then retests.
            step.Set(ConnectionStepState.ActionRequired, detail: hint);
        }
        else
        {
            step.Set(
                ConnectionStepState.Failed,
                detail: hint,
                command: ConnectionDiagnosis.CommandFor(code),
                offersPrepHelp: ConnectionDiagnosis.OffersPrepHelp(code));
        }
    }

    private void ApplyRunning()
    {
        ConnectionStepViewModel? running = null;
        foreach (var step in _steps)
        {
            if (step.Stage <= _reported)
            {
                step.Set(ConnectionStepState.Passed);
            }
            else if (running is null)
            {
                running = step;
                step.Set(ConnectionStepState.Running);
            }
            else
            {
                step.Set(ConnectionStepState.Pending);
            }
        }

        if (running is not null)
        {
            Announcement = running.AccessibleName;
        }
    }

    private static string? OperatingSystemName(ServerOperatingSystem operatingSystem) => operatingSystem switch
    {
        ServerOperatingSystem.Linux => "Linux",
        ServerOperatingSystem.MacOS => "macOS",
        _ => null
    };

    private static string EndSentence(string text) =>
        text.Length > 0 && text[^1] is '.' or '?' or '!' or '…' ? text : text + ".";

    private string Text(string key) => _localizationService.GetString(key);
}
