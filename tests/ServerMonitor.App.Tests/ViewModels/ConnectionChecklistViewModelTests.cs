using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.5 B2 — the four-step checklist as a state machine: live progress, then the result. The failing step is
/// the one after <see cref="SshConnectionResult.ReachedStage"/>, except where the error code's family names
/// the step (host-key mismatch → identity; jump-host failure → first step), because the reached stage is
/// monotonic and can be ahead of such a failure.
/// </summary>
public sealed class ConnectionChecklistViewModelTests
{
    private static ConnectionChecklistViewModel Checklist() => new(new FakeLocalizationService());

    private static ConnectionStepState[] States(ConnectionChecklistViewModel checklist) =>
        checklist.Steps.Select(step => step.State).ToArray();

    private static SshConnectionResult Failure(
        SshConnectionErrorCode code,
        SshConnectionStage reached,
        ServerConnectionState state = ServerConnectionState.Error) => new()
    {
        State = state,
        ErrorCode = code,
        ReachedStage = reached
    };

    private static HostKeyIdentity Key() =>
        HostKeyIdentity.Create("ssh-ed25519", Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()));

    [Fact]
    public void Steps_AreTheFourStagesInOrder()
    {
        var checklist = Checklist();

        Assert.Equal(
            [
                SshConnectionStage.PortReachable,
                SshConnectionStage.HostKeyVerified,
                SshConnectionStage.Authenticated,
                SshConnectionStage.OperatingSystemIdentified
            ],
            checklist.Steps.Select(step => step.Stage));
        Assert.Equal(
            [
                "ConnectionStepPortTitle",
                "ConnectionStepHostKeyTitle",
                "ConnectionStepAuthenticationTitle",
                "ConnectionStepOperatingSystemTitle"
            ],
            checklist.Steps.Select(step => step.Title));
        Assert.All(checklist.Steps, step => Assert.Equal(ConnectionStepState.Pending, step.State));
    }

    [Fact]
    public void Begin_RunsTheFirstStepAndLeavesTheRestPending()
    {
        var checklist = Checklist();

        checklist.Begin(jumpDisplay: null);

        Assert.Equal(
            [
                ConnectionStepState.Running,
                ConnectionStepState.Pending,
                ConnectionStepState.Pending,
                ConnectionStepState.Pending
            ],
            States(checklist));
        Assert.Equal("ConnectionStepPortTitle: ConnectionStepStateRunning", checklist.Announcement);
    }

    [Fact]
    public void Progress_PassesEachReportedStageAndRunsTheNext()
    {
        var checklist = Checklist();
        checklist.Begin(null);

        checklist.Report(SshConnectionStage.PortReachable);
        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Running,
                ConnectionStepState.Pending,
                ConnectionStepState.Pending
            ],
            States(checklist));
        Assert.Equal("ConnectionStepHostKeyTitle: ConnectionStepStateRunning", checklist.Announcement);

        checklist.Report(SshConnectionStage.HostKeyVerified);
        checklist.Report(SshConnectionStage.Authenticated);
        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.Running
            ],
            States(checklist));

        checklist.Report(SshConnectionStage.OperatingSystemIdentified);
        Assert.All(checklist.Steps, step => Assert.Equal(ConnectionStepState.Passed, step.State));
    }

    [Fact]
    public void Progress_NeverMovesBackwardsAndIgnoresUndefinedStages()
    {
        var checklist = Checklist();
        checklist.Begin(null);
        checklist.Report(SshConnectionStage.HostKeyVerified);

        checklist.Report(SshConnectionStage.PortReachable);
        checklist.Report(SshConnectionStage.None);
        checklist.Report((SshConnectionStage)99);

        Assert.Equal(SshConnectionStage.HostKeyVerified, checklist.ReportedStage);
        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.Running,
                ConnectionStepState.Pending
            ],
            States(checklist));
    }

    [Fact]
    public void Progress_IsIgnoredOutsideARunningTest()
    {
        var checklist = Checklist();

        checklist.Report(SshConnectionStage.Authenticated);
        Assert.All(checklist.Steps, step => Assert.Equal(ConnectionStepState.Pending, step.State));

        checklist.Begin(null);
        checklist.Complete(Failure(SshConnectionErrorCode.ConnectionRefused, SshConnectionStage.None));
        checklist.Report(SshConnectionStage.Authenticated);

        Assert.Equal(ConnectionStepState.Failed, checklist.Steps[0].State);
        Assert.Equal(ConnectionStepState.NotReached, checklist.Steps[2].State);
    }

    [Fact]
    public void Success_WithTheOperatingSystemIdentified_PassesEveryStepAndNamesTheSystem()
    {
        var checklist = Checklist();
        checklist.Begin(null);

        checklist.Complete(new SshConnectionResult
        {
            State = ServerConnectionState.Connected,
            ReachedStage = SshConnectionStage.OperatingSystemIdentified,
            DetectedOperatingSystem = ServerOperatingSystem.MacOS
        });

        Assert.All(checklist.Steps, step => Assert.Equal(ConnectionStepState.Passed, step.State));
        Assert.Equal("macOS", checklist.Steps[3].StateText);
        Assert.All(checklist.Steps, step => Assert.False(step.HasDetail));
    }

    [Fact]
    public void Success_WithAnUnknownOperatingSystem_IsAWarningNotAFailure()
    {
        var checklist = Checklist();
        checklist.Begin(null);

        checklist.Complete(new SshConnectionResult
        {
            State = ServerConnectionState.Connected,
            ReachedStage = SshConnectionStage.Authenticated,
            DetectedOperatingSystem = ServerOperatingSystem.Unknown
        });

        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.Warning
            ],
            States(checklist));
        var os = checklist.Steps[3];
        Assert.Equal("ConnectionStepStateOperatingSystemUnknown", os.StateText);
        Assert.Equal("ConnectionStepOperatingSystemUnknownHint", os.Detail);
        Assert.False(os.IsFailed);
        Assert.False(os.OffersPrepHelp);
    }

    [Fact]
    public void Success_NeverShowsAFailedOrUnreachedLoginEvenIfTheStageWasNotReported()
    {
        var checklist = Checklist();
        checklist.Begin(null);

        // A connected result cannot exist without the port, the trusted identity and the login.
        checklist.Complete(new SshConnectionResult { State = ServerConnectionState.Connected });

        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.Warning
            ],
            States(checklist));
    }

    [Theory]
    [InlineData(SshConnectionErrorCode.ConnectionRefused, SshConnectionStage.None, 0)]
    [InlineData(SshConnectionErrorCode.DnsResolutionFailed, SshConnectionStage.None, 0)]
    [InlineData(SshConnectionErrorCode.ProtocolError, SshConnectionStage.PortReachable, 1)]
    [InlineData(SshConnectionErrorCode.AuthenticationFailed, SshConnectionStage.HostKeyVerified, 2)]
    [InlineData(SshConnectionErrorCode.PrivateKeyInvalid, SshConnectionStage.HostKeyVerified, 2)]
    [InlineData(SshConnectionErrorCode.RemoteDisconnected, SshConnectionStage.Authenticated, 3)]
    [InlineData(SshConnectionErrorCode.Unexpected, SshConnectionStage.OperatingSystemIdentified, 3)]
    public void Failure_MarksTheStepAfterTheReachedStage(
        SshConnectionErrorCode code,
        SshConnectionStage reached,
        int failingIndex)
    {
        var checklist = Checklist();
        checklist.Begin(null);

        checklist.Complete(Failure(code, reached));

        for (var index = 0; index < checklist.Steps.Count; index++)
        {
            var expected = index < failingIndex
                ? ConnectionStepState.Passed
                : index == failingIndex
                    ? ConnectionStepState.Failed
                    : ConnectionStepState.NotReached;
            Assert.Equal(expected, checklist.Steps[index].State);
        }

        var failing = checklist.Steps[failingIndex];
        Assert.Equal($"ConnectionHint{code}", failing.Detail);
        Assert.All(checklist.Steps.Where(step => !ReferenceEquals(step, failing)), step => Assert.False(step.HasDetail));
    }

    [Fact]
    public void Failure_TakesTheStepFromTheReachedStageNotFromTheErrorCode()
    {
        var checklist = Checklist();
        checklist.Begin(null);

        // An authentication code with nothing reached is reported where it was observed: the first step.
        checklist.Complete(Failure(SshConnectionErrorCode.AuthenticationFailed, SshConnectionStage.None));

        Assert.Equal(ConnectionStepState.Failed, checklist.Steps[0].State);
        Assert.Equal(ConnectionStepState.NotReached, checklist.Steps[2].State);
    }

    [Fact]
    public void Failure_OverridesOptimisticProgress()
    {
        var checklist = Checklist();
        checklist.Begin(null);
        checklist.Report(SshConnectionStage.PortReachable);
        checklist.Report(SshConnectionStage.HostKeyVerified);

        checklist.Complete(Failure(SshConnectionErrorCode.AuthenticationFailed, SshConnectionStage.HostKeyVerified));

        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Passed,
                ConnectionStepState.Failed,
                ConnectionStepState.NotReached
            ],
            States(checklist));
    }

    [Fact]
    public void Failure_CarriesTheCommandAndThePrepLinkOnlyWhereTheyApply()
    {
        var checklist = Checklist();
        checklist.Begin(null);

        checklist.Complete(Failure(SshConnectionErrorCode.ConnectionRefused, SshConnectionStage.None));
        Assert.Equal("systemctl status ssh", checklist.Steps[0].Command);
        Assert.True(checklist.Steps[0].HasCommand);
        Assert.True(checklist.Steps[0].OffersPrepHelp);

        checklist.Begin(null);
        checklist.Complete(Failure(SshConnectionErrorCode.DnsResolutionFailed, SshConnectionStage.None));
        Assert.False(checklist.Steps[0].HasCommand);
        Assert.False(checklist.Steps[0].OffersPrepHelp);
    }

    [Fact]
    public void UnknownHostKey_AsksToConfirmTheIdentityInsteadOfFailing()
    {
        var checklist = Checklist();
        checklist.Begin(null);

        checklist.Complete(new SshConnectionResult
        {
            State = ServerConnectionState.HostKeyUnknown,
            ErrorCode = SshConnectionErrorCode.HostKeyUnknown,
            ReachedStage = SshConnectionStage.PortReachable,
            PresentedHostKey = Key()
        });

        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.ActionRequired,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached
            ],
            States(checklist));
        Assert.Equal("ConnectionStepStateActionRequired", checklist.Steps[1].StateText);
        Assert.Equal("ConnectionHintHostKeyUnknown", checklist.Steps[1].Detail);
        Assert.False(checklist.Steps[1].IsPassed);
    }

    [Fact]
    public void UnknownHostKey_WithoutAKeyToConfirm_IsAFailure()
    {
        var checklist = Checklist();
        checklist.Begin(null);

        // The jump host's unknown key arrives as an error with nothing to trust from this panel.
        checklist.Complete(Failure(SshConnectionErrorCode.JumpHostKeyUnknown, SshConnectionStage.None));

        Assert.Equal(ConnectionStepState.Failed, checklist.Steps[0].State);
        Assert.Equal("ConnectionHintJumpHostKeyUnknown", checklist.Steps[0].Detail);
    }

    [Fact]
    public void HostKeyMismatch_FailsTheIdentityStep()
    {
        var checklist = Checklist();
        checklist.Begin(null);

        checklist.Complete(new SshConnectionResult
        {
            State = ServerConnectionState.HostKeyMismatch,
            ErrorCode = SshConnectionErrorCode.HostKeyMismatch,
            ReachedStage = SshConnectionStage.PortReachable,
            PresentedHostKey = Key()
        });

        Assert.Equal(ConnectionStepState.Failed, checklist.Steps[1].State);
        Assert.Equal("ConnectionHintHostKeyMismatch", checklist.Steps[1].Detail);
        Assert.Equal(ConnectionStepState.NotReached, checklist.Steps[2].State);
    }

    [Fact]
    public void Cancelled_KeepsWhatPassedAndFailsNothing()
    {
        var checklist = Checklist();
        checklist.Begin(null);
        checklist.Report(SshConnectionStage.PortReachable);

        checklist.Complete(Failure(
            SshConnectionErrorCode.Cancelled,
            SshConnectionStage.PortReachable,
            ServerConnectionState.Cancelled));

        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached
            ],
            States(checklist));
        Assert.DoesNotContain(checklist.Steps, step => step.IsFailed || step.IsRunning);
        Assert.Equal("ConnectionHintCancelled", checklist.Steps[1].Detail);
    }

    [Fact]
    public void RoutedServer_NamesTheJumpOnThePortStepAndADirectOneDoesNot()
    {
        var checklist = Checklist();

        checklist.Begin("bastion.example.test:2222");
        Assert.Equal("ConnectionStepPortTitle (via bastion.example.test:2222)", checklist.Steps[0].Title);

        checklist.Begin(null);
        Assert.Equal("ConnectionStepPortTitle", checklist.Steps[0].Title);
    }

    [Fact]
    public void JumpFailure_IsShownOnTheFirstStepWithTheJumpDiagnosis()
    {
        var checklist = Checklist();
        checklist.Begin("bastion:22");

        checklist.Complete(Failure(SshConnectionErrorCode.JumpAuthenticationFailed, SshConnectionStage.None));

        var port = checklist.Steps[0];
        Assert.Equal(ConnectionStepState.Failed, port.State);
        Assert.Equal("ConnectionHintJumpAuthenticationFailed", port.Detail);
        // The helper prepares the TARGET; it is not offered for a jump host's login.
        Assert.False(port.OffersPrepHelp);
        Assert.All(checklist.Steps.Skip(1), step => Assert.Equal(ConnectionStepState.NotReached, step.State));
    }

    // Race (direct): the probe verified the key, then the session presented another one. The reached stage
    // says "identity verified", the code says the identity is what failed — the code's family wins.
    [Theory]
    [InlineData(SshConnectionErrorCode.HostKeyMismatch)]
    [InlineData(SshConnectionErrorCode.RoutedHostKeyMismatch)]
    public void HostKeyMismatchAfterTheIdentityWasVerified_StillFailsTheIdentityStep(SshConnectionErrorCode code)
    {
        var checklist = Checklist();
        checklist.Begin(null);
        checklist.Report(SshConnectionStage.PortReachable);
        checklist.Report(SshConnectionStage.HostKeyVerified);

        checklist.Complete(Failure(code, SshConnectionStage.HostKeyVerified, ServerConnectionState.HostKeyMismatch));

        Assert.Equal(
            [
                ConnectionStepState.Passed,
                ConnectionStepState.Failed,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached
            ],
            States(checklist));
        Assert.Equal($"ConnectionHint{code}", checklist.Steps[1].Detail);
        Assert.False(checklist.Steps[2].HasDetail);
    }

    // Race (routed): the jump dropped during the target session. The steps that did pass stay passed, and
    // the failure is diagnosed as a jump failure on the first step — never as the target's login.
    [Theory]
    [InlineData(SshConnectionErrorCode.JumpConnectionFailed)]
    [InlineData(SshConnectionErrorCode.JumpAuthenticationFailed)]
    [InlineData(SshConnectionErrorCode.JumpHostKeyUnknown)]
    [InlineData(SshConnectionErrorCode.JumpHostKeyMismatch)]
    [InlineData(SshConnectionErrorCode.JumpCredentialUnavailable)]
    [InlineData(SshConnectionErrorCode.TargetUnreachableViaJump)]
    [InlineData(SshConnectionErrorCode.LocalTunnelFailed)]
    public void JumpFamilyFailureAfterLaterStagesWereReached_IsDiagnosedOnTheFirstStep(SshConnectionErrorCode code)
    {
        var checklist = Checklist();
        checklist.Begin("bastion:22");
        checklist.Report(SshConnectionStage.PortReachable);
        checklist.Report(SshConnectionStage.HostKeyVerified);

        checklist.Complete(Failure(code, SshConnectionStage.HostKeyVerified));

        Assert.Equal(
            [
                ConnectionStepState.Failed,
                ConnectionStepState.Passed,
                ConnectionStepState.NotReached,
                ConnectionStepState.NotReached
            ],
            States(checklist));
        Assert.Equal($"ConnectionHint{code}", checklist.Steps[0].Detail);
        Assert.All(checklist.Steps.Skip(1), step => Assert.False(step.HasDetail));
        Assert.False(checklist.Steps[0].OffersPrepHelp);
    }

    [Fact]
    public void EveryJumpFamilyCode_IsCoveredByTheFamilyRule()
    {
        // A new Jump* code must not silently fall back to "the step after the reached stage".
        var jumpCodes = Enum.GetValues<SshConnectionErrorCode>()
            .Where(code => code.ToString().StartsWith("Jump", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(5, jumpCodes.Length);

        foreach (var code in jumpCodes)
        {
            var checklist = Checklist();
            checklist.Begin("bastion:22");
            checklist.Complete(Failure(code, SshConnectionStage.Authenticated));
            Assert.Equal(ConnectionStepState.Failed, checklist.Steps[0].State);
        }
    }

    // RemoteDisconnected before the banner arrives with nothing reached (the TCP connect is not observable):
    // the first step fails, but nothing may claim the port is closed or the service is down.
    [Fact]
    public void RemoteDisconnectedWithNothingReached_FailsTheFirstStepWithoutClaimingThePortIsClosed()
    {
        var checklist = Checklist();
        checklist.Begin(null);

        checklist.Complete(Failure(SshConnectionErrorCode.RemoteDisconnected, SshConnectionStage.None));

        var port = checklist.Steps[0];
        Assert.Equal(ConnectionStepState.Failed, port.State);
        Assert.Equal("ConnectionHintRemoteDisconnected", port.Detail);
        Assert.False(port.HasCommand);
        Assert.False(port.OffersPrepHelp);
    }

    [Fact]
    public void EveryRow_AnnouncesItsStateInItsAccessibleName()
    {
        var checklist = Checklist();
        checklist.Begin(null);
        checklist.Complete(Failure(SshConnectionErrorCode.AuthenticationFailed, SshConnectionStage.HostKeyVerified));

        Assert.Equal("ConnectionStepPortTitle: ConnectionStepStatePassed", checklist.Steps[0].AccessibleName);
        Assert.Equal("ConnectionStepAuthenticationTitle: ConnectionStepStateFailed", checklist.Steps[2].AccessibleName);
        Assert.Equal(
            "ConnectionStepOperatingSystemTitle: ConnectionStepStateNotReached",
            checklist.Steps[3].AccessibleName);
        Assert.Equal(
            "ConnectionStepPortTitle: ConnectionStepStatePassed. "
                + "ConnectionStepHostKeyTitle: ConnectionStepStatePassed. "
                + "ConnectionStepAuthenticationTitle: ConnectionStepStateFailed ConnectionHintAuthenticationFailed. "
                + "ConnectionStepOperatingSystemTitle: ConnectionStepStateNotReached.",
            checklist.Announcement);
    }

    [Fact]
    public void RowChanges_AreObservable()
    {
        var checklist = Checklist();
        var changes = 0;
        checklist.Steps[0].PropertyChanged += (_, _) => changes++;
        var announcements = new List<string>();
        checklist.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ConnectionChecklistViewModel.Announcement))
            {
                announcements.Add(checklist.Announcement);
            }
        };

        checklist.Begin(null);
        checklist.Report(SshConnectionStage.PortReachable);

        Assert.True(changes >= 2);
        Assert.Equal(2, announcements.Count);
    }

    [Fact]
    public void Reset_ReturnsEveryStepToPending()
    {
        var checklist = Checklist();
        checklist.Begin("bastion:22");
        checklist.Complete(Failure(SshConnectionErrorCode.ConnectionRefused, SshConnectionStage.None));

        checklist.Reset();

        Assert.All(checklist.Steps, step =>
        {
            Assert.Equal(ConnectionStepState.Pending, step.State);
            Assert.False(step.HasDetail);
            Assert.False(step.HasCommand);
            Assert.False(step.OffersPrepHelp);
        });
        Assert.Equal(SshConnectionStage.None, checklist.ReportedStage);
        Assert.Equal(string.Empty, checklist.Announcement);
    }
}
