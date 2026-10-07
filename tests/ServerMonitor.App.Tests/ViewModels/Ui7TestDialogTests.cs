using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using S = ServerMonitor.App.ViewModels.ConnectionStepState;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7C (B-9, Figma 08): the connection-test dialog in the editor page's ONE in-page layer, over the production session,
/// page controller and view model (scripted SSH, barriers - no clock). The four rows are the view model's real stages for
/// a direct AND a routed server, each row's state comes from <see cref="SshConnectionResult.ReachedStage"/> and the error
/// code's family only (the mapping table below), and the dialog's phase / wording from <see cref="ConnectionTestView"/>.
/// </summary>
public sealed class Ui7TestDialogTests : IDisposable
{
    private static readonly SshEndpoint Direct = SshEndpoint.Create("10.0.0.9", 22);

    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ---- The mapping table: stage x error code -> the four row states (and the dialog's family) ------------------------

    public static TheoryData<string, SshConnectionErrorCode, ServerConnectionState, SshConnectionStage, bool, S[], ConnectionFailureFamily> Families() => new()
    {
        // TCP / port: nothing answered.
        { "refused", SshConnectionErrorCode.ConnectionRefused, ServerConnectionState.Unreachable, SshConnectionStage.None, false, [S.Failed, S.NotReached, S.NotReached, S.NotReached], ConnectionFailureFamily.Network },
        { "timeout", SshConnectionErrorCode.ConnectionTimedOut, ServerConnectionState.TimedOut, SshConnectionStage.None, false, [S.Failed, S.NotReached, S.NotReached, S.NotReached], ConnectionFailureFamily.Network },
        { "dns", SshConnectionErrorCode.DnsResolutionFailed, ServerConnectionState.Unreachable, SshConnectionStage.None, false, [S.Failed, S.NotReached, S.NotReached, S.NotReached], ConnectionFailureFamily.Network },
        // Handshake / protocol: the port answered, the SSH session did not come up.
        { "protocol", SshConnectionErrorCode.ProtocolError, ServerConnectionState.Error, SshConnectionStage.PortReachable, false, [S.Passed, S.Failed, S.NotReached, S.NotReached], ConnectionFailureFamily.Protocol },
        { "algorithm", SshConnectionErrorCode.UnsupportedAlgorithm, ServerConnectionState.Error, SshConnectionStage.PortReachable, false, [S.Passed, S.Failed, S.NotReached, S.NotReached], ConnectionFailureFamily.Protocol },
        // Host key: unknown = confirm (the trust prompt), mismatch = the identity row failed (monotonic reached stage ahead).
        { "key unknown", SshConnectionErrorCode.HostKeyUnknown, ServerConnectionState.HostKeyUnknown, SshConnectionStage.PortReachable, true, [S.Passed, S.ActionRequired, S.NotReached, S.NotReached], ConnectionFailureFamily.Identity },
        { "key mismatch", SshConnectionErrorCode.HostKeyMismatch, ServerConnectionState.HostKeyMismatch, SshConnectionStage.HostKeyVerified, true, [S.Passed, S.Failed, S.NotReached, S.NotReached], ConnectionFailureFamily.Identity },
        { "routed key mismatch", SshConnectionErrorCode.RoutedHostKeyMismatch, ServerConnectionState.HostKeyMismatch, SshConnectionStage.PortReachable, true, [S.Passed, S.Failed, S.NotReached, S.NotReached], ConnectionFailureFamily.Identity },
        // Authentication.
        { "auth", SshConnectionErrorCode.AuthenticationFailed, ServerConnectionState.AuthenticationFailed, SshConnectionStage.HostKeyVerified, false, [S.Passed, S.Passed, S.Failed, S.NotReached], ConnectionFailureFamily.Authentication },
        { "key file", SshConnectionErrorCode.PrivateKeyUnavailable, ServerConnectionState.AuthenticationFailed, SshConnectionStage.HostKeyVerified, false, [S.Passed, S.Passed, S.Failed, S.NotReached], ConnectionFailureFamily.Authentication },
        // ProxyJump: every jump-stage failure names stage 1 (the port as reached through the jump).
        { "pj target unreachable", SshConnectionErrorCode.TargetUnreachableViaJump, ServerConnectionState.Unreachable, SshConnectionStage.None, false, [S.Failed, S.NotReached, S.NotReached, S.NotReached], ConnectionFailureFamily.TargetViaJump },
        { "jump auth", SshConnectionErrorCode.JumpAuthenticationFailed, ServerConnectionState.AuthenticationFailed, SshConnectionStage.None, false, [S.Failed, S.NotReached, S.NotReached, S.NotReached], ConnectionFailureFamily.Jump },
        { "jump connect", SshConnectionErrorCode.JumpConnectionFailed, ServerConnectionState.Unreachable, SshConnectionStage.None, false, [S.Failed, S.NotReached, S.NotReached, S.NotReached], ConnectionFailureFamily.Jump },
        { "jump trust", SshConnectionErrorCode.JumpHostKeyUnknown, ServerConnectionState.HostKeyUnknown, SshConnectionStage.None, true, [S.ActionRequired, S.NotReached, S.NotReached, S.NotReached], ConnectionFailureFamily.Jump },
        { "jump key changed", SshConnectionErrorCode.JumpHostKeyMismatch, ServerConnectionState.HostKeyMismatch, SshConnectionStage.None, true, [S.Failed, S.NotReached, S.NotReached, S.NotReached], ConnectionFailureFamily.Jump },
        { "tunnel", SshConnectionErrorCode.LocalTunnelFailed, ServerConnectionState.Error, SshConnectionStage.None, false, [S.Failed, S.NotReached, S.NotReached, S.NotReached], ConnectionFailureFamily.Tunnel },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void EachFailureFamily_MapsToItsRowStates_AndItsDialogWording(
        string name, SshConnectionErrorCode code, ServerConnectionState state, SshConnectionStage reached, bool presentsKey,
        S[] rows, ConnectionFailureFamily family)
    {
        _ = name;
        var checklist = new ConnectionChecklistViewModel(new FakeLocalizationService());
        checklist.Begin(null);

        checklist.Complete(new SshConnectionResult
        {
            State = state,
            ErrorCode = code,
            ReachedStage = reached,
            PresentedHostKey = presentsKey ? Key() : null
        });

        Assert.Equal(rows, checklist.Steps.Select(step => step.State));
        Assert.Equal(family, ConnectionTestView.FamilyOf(code));
        var view = new ConnectionTestView(ConnectionTestPhase.Failed, family, Routed: false);
        Assert.Equal($"ServerEditorTestFailed{family}Title", view.TitleKey);
        Assert.Equal("ServerEditorTestRetry", view.RetryKey);
    }

    [Fact]
    public void ASuccess_WithAnUnidentifiedOperatingSystem_WarnsOnTheFourthRow_AndAFullOneIsAllPassed()
    {
        var checklist = new ConnectionChecklistViewModel(new FakeLocalizationService());
        checklist.Begin(null);
        checklist.Complete(new SshConnectionResult { State = ServerConnectionState.Connected, ReachedStage = SshConnectionStage.Authenticated });
        Assert.Equal([S.Passed, S.Passed, S.Passed, S.Warning], checklist.Steps.Select(step => step.State));

        checklist.Begin(null);
        checklist.Complete(new SshConnectionResult
        {
            State = ServerConnectionState.Connected,
            ReachedStage = SshConnectionStage.OperatingSystemIdentified,
            DetectedOperatingSystem = ServerOperatingSystem.Linux
        });
        Assert.All(checklist.Steps, step => Assert.Equal(S.Passed, step.State));
    }

    [Fact]
    public void ACancelledTest_KeepsWhatPassed_AndTheRestIsNotReached()
    {
        var checklist = new ConnectionChecklistViewModel(new FakeLocalizationService());
        checklist.Begin(null);
        checklist.Complete(new SshConnectionResult
        {
            State = ServerConnectionState.Cancelled,
            ErrorCode = SshConnectionErrorCode.Cancelled,
            ReachedStage = SshConnectionStage.PortReachable
        });
        Assert.Equal([S.Passed, S.NotReached, S.NotReached, S.NotReached], checklist.Steps.Select(step => step.State));
    }

    [Fact]
    public void EveryErrorCode_HasAFamily_AndEveryFamilyIsWorded_InEveryCulture()
    {
        foreach (var code in Enum.GetValues<SshConnectionErrorCode>())
        {
            Assert.NotEqual(ConnectionFailureFamily.None, ConnectionTestView.FamilyOf(code));
        }

        var keys = Enum.GetValues<ConnectionFailureFamily>().Where(family => family != ConnectionFailureFamily.None)
            .SelectMany(family => new[] { $"ServerEditorTestFailed{family}Title", $"ServerEditorTestFailed{family}Body" })
            .Concat([
                "ServerEditorTestTestingTitle", "ServerEditorTestTestingBodyFormat", "ServerEditorTestVerifiedTitle",
                "ServerEditorTestVerifiedBody", "ServerEditorTestVerifiedDetail", "ServerEditorTestCancelledTitle",
                "ServerEditorTestCancelledBody", "ServerEditorTestReviewData", "ServerEditorTestReviewRoute",
                "ServerEditorTestReviewCredentials", "ServerEditorTestRetry", "ServerEditorTestAgain", "ServerEditorTestViaFormat"
            ]);
        Ui7Resources.AssertPresentInEveryCulture(keys);
    }

    [Fact]
    public void TheDialogWording_ComesFromTheErrorCode_NeverFromAMessage()
    {
        var presentation = File.ReadAllText(Path.Combine(
            Architecture.AppSourceTree.RepositoryRoot, "src", "ServerMonitor.App", "ViewModels", "ServerEditorPresentation.cs"));
        var start = presentation.IndexOf("public sealed record ConnectionTestView", StringComparison.Ordinal);
        var body = presentation[start..];
        Assert.DoesNotContain("ConnectionStatusMessage", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".Contains(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Message", body.Replace("ConnectionTestView", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // ---- The dialog in the page's layer ------------------------------------------------------------------------------

    [Fact]
    public async Task AnInvalidForm_OpensNoDialog_AndShowsItsFieldErrors()
    {
        await OpenAddAsync();
        _world.ViewModel.Name = string.Empty;

        var started = await _world.Page.StartTestAsync();

        Assert.False(started);
        Assert.Equal(0, _world.Ssh.TestConnectionCount);
        Assert.Equal(ServerEditorLayer.None, _world.Page.Layer());
        Assert.Equal(ServerEditorField.Name, _world.ViewModel.FirstInvalidField);
    }

    [Fact]
    public async Task Testing_ShowsCancelarTeste_ThenTheVerifiedResult()
    {
        await OpenAddAsync();
        _world.Ssh.Hold = true;
        var arrived = _world.Ssh.Arrived.Task;

        var test = _world.Page.StartTestAsync();
        await arrived;

        Assert.Equal(ServerEditorLayer.Test, _world.Page.Layer());
        var testing = _world.Page.Controller.TestToShow()!;
        Assert.Equal(ConnectionTestPhase.Testing, testing.Phase);
        Assert.Equal("SaIconRefreshData", testing.IconKey);
        Assert.Equal("ServerFormCancelTestButton.Content", testing.CloseKey);
        Assert.Null(testing.RetryKey);
        Assert.Null(testing.VerifiedDetailKey);
        Assert.Equal(S.Running, _world.ViewModel.ConnectionChecklist.Steps[0].State);

        _world.Ssh.Release(new SshConnectionResult
        {
            State = ServerConnectionState.Connected,
            ReachedStage = SshConnectionStage.OperatingSystemIdentified,
            DetectedOperatingSystem = ServerOperatingSystem.Linux
        });
        Assert.True(await test);

        var verified = _world.Page.Controller.TestToShow()!;
        Assert.Equal(ConnectionTestPhase.Verified, verified.Phase);
        Assert.Equal("SaIconShield01Data", verified.IconKey);
        Assert.Equal("ServerEditorTestVerifiedDetail", verified.VerifiedDetailKey);
        Assert.Equal("ServerEditorTrustBack", verified.CloseKey);
        Assert.Equal(ServerEditorLayer.Test, _world.Page.Layer());

        _world.Page.Controller.CloseLayer(ServerEditorLayer.Test, accepting: false);
        Assert.Equal(ServerEditorLayer.None, _world.Page.Layer());
        Assert.False(_world.Page.Disposed);
    }

    [Fact]
    public async Task EscDuringATest_CancelsTheTest_AndTheDialogStaysOnTeste_Cancelado()
    {
        await OpenAddAsync();
        _world.Ssh.Hold = true;
        var arrived = _world.Ssh.Arrived.Task;
        var test = _world.Page.StartTestAsync();
        await arrived;
        var token = _world.Ssh.LastToken;

        _world.Page.Controller.CloseLayer(ServerEditorLayer.Test, accepting: false); // what Esc does on the layer

        Assert.True(token.IsCancellationRequested); // cancelled synchronously, before anything else
        await test;
        var cancelled = _world.Page.Controller.TestToShow()!;
        Assert.Equal(ConnectionTestPhase.Cancelled, cancelled.Phase);
        Assert.Equal("ServerEditorTestCancelledTitle", cancelled.TitleKey);
        Assert.Equal("ServerEditorTestAgain", cancelled.RetryKey);
        Assert.Equal(ServerEditorLayer.Test, _world.Page.Layer());
        Assert.Empty(_world.Prompt.Asked); // Esc on the layer is never Back
        Assert.False(_world.Page.Disposed);

        _world.Page.Controller.CloseLayer(ServerEditorLayer.Test, accepting: false); // Esc again: back to the form
        Assert.Equal(ServerEditorLayer.None, _world.Page.Layer());
    }

    [Fact]
    public async Task Retry_RunsTheSameTestAgain_WithTheStagedSecret()
    {
        await OpenAddAsync();
        _world.Ssh.Result = new SshConnectionResult
        {
            State = ServerConnectionState.AuthenticationFailed,
            ErrorCode = SshConnectionErrorCode.AuthenticationFailed,
            ReachedStage = SshConnectionStage.HostKeyVerified
        };
        Assert.True(await _world.Page.StartTestAsync());
        var failed = _world.Page.Controller.TestToShow()!;
        Assert.Equal(ConnectionTestPhase.Failed, failed.Phase);
        Assert.Equal(ConnectionFailureFamily.Authentication, failed.Family);
        Assert.Equal("ServerEditorTestReviewData", failed.CloseKey);
        Assert.Equal("ServerEditorTestRetry", failed.RetryKey);
        Assert.True(_world.ViewModel.ConnectionChecklist.Steps[2].HasCommand); // the copyable command under the failed row
        Assert.True(_world.ViewModel.ConnectionChecklist.Steps[2].OffersPrepHelp);

        _world.Ssh.Result = TestData.Connected();
        Assert.True(await _world.Page.StartTestAsync()); // "Tentar novamente" (the password box is empty now)

        Assert.Equal(2, _world.Ssh.TestConnectionCount);
        Assert.NotNull(_world.Ssh.Requests[1].CredentialOverride); // the staged secret, not a retype
        Assert.Equal(ConnectionTestPhase.Verified, _world.Page.Controller.TestToShow()!.Phase);
    }

    [Fact]
    public async Task TheTrustPrompt_AppearsInTheSameLayerMidTest_ThenTheResult()
    {
        await OpenAddAsync();
        _world.Ssh.Queue.Enqueue(Ui7EditorSessionTests.Unknown(Direct));
        _world.Ssh.Result = TestData.Connected();

        Assert.True(await _world.Page.StartTestAsync());

        Assert.Equal(ServerEditorLayer.Trust, _world.Page.Layer());
        var prompt = _world.Page.Controller.PromptToShow()!;
        Assert.True(await _world.Page.Controller.AcceptTrustAsync(prompt));
        Assert.Equal(ServerEditorLayer.Test, _world.Page.Layer());
        Assert.Equal(ConnectionTestPhase.Verified, _world.Page.Controller.TestToShow()!.Phase);
        Assert.Equal(1, _world.DirectTrust.Trusts);
    }

    [Fact]
    public async Task ClosingTheTrustPrompt_GoesBackToTheForm_AndTrustsNothing()
    {
        await OpenAddAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        Assert.True(await _world.Page.StartTestAsync());
        Assert.Equal(ServerEditorLayer.Trust, _world.Page.Layer());

        _world.Page.Controller.CloseLayer(ServerEditorLayer.Trust, accepting: false);

        Assert.Equal(ServerEditorLayer.None, _world.Page.Layer());
        Assert.Equal(0, _world.DirectTrust.Trusts);
    }

    [Fact]
    public async Task ARoutedFailure_NamesTheJumpInStageOne_AndOffersRevisarCredenciais()
    {
        await OpenAddAsync();
        var viewModel = _world.ViewModel;
        viewModel.UseJumpHost = true;
        viewModel.JumpHost = "bastion.example.test";
        viewModel.JumpUsername = "jumper";
        viewModel.SelectedJumpAuthenticationIndex = 1;
        viewModel.CaptureJumpSecret("jump-secret");
        _world.Ssh.Result = new SshConnectionResult
        {
            State = ServerConnectionState.AuthenticationFailed,
            ErrorCode = SshConnectionErrorCode.JumpAuthenticationFailed,
            ReachedStage = SshConnectionStage.None
        };

        Assert.True(await _world.Page.StartTestAsync());

        var view = _world.Page.Controller.TestToShow()!;
        Assert.True(view.Routed);
        Assert.Equal(ConnectionFailureFamily.Jump, view.Family);
        Assert.Equal("ServerEditorTestReviewCredentials", view.CloseKey);
        var stageOne = viewModel.ConnectionChecklist.Steps[0];
        Assert.Equal(S.Failed, stageOne.State);
        Assert.Contains("bastion.example.test:22", stageOne.Title, StringComparison.Ordinal);
        Assert.Equal(4, viewModel.ConnectionChecklist.Steps.Count); // never six fake rows (G-7)
    }

    // ---- Vigil 7B L-1: nothing starts under "Descartar alterações?" --------------------------------------------------

    [Fact]
    public async Task L1_ARetestThatStartsUnderTheDiscardQuestion_IsCancelledAtOnce_AndKeepEditingFindsItCancelled()
    {
        await OpenAddAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        Assert.True(await _world.Page.StartTestAsync());
        var prompt = _world.Page.Controller.PromptToShow()!;
        var write = new TaskCompletionSource();
        _world.DirectTrust.Barrier = write;
        _world.Ssh.Result = TestData.Connected();
        _world.Ssh.Hold = true;

        var accept = _world.Page.Controller.AcceptTrustAsync(prompt); // the key write is in flight (held)
        _world.Prompt.AutoAnswer = null;
        _world.Page.Controller.Cancel(); // "Descartar alterações?" is now open
        Assert.Single(_world.Prompt.Asked);

        var arrived = _world.Ssh.Arrived.Task;
        write.SetResult(); // the write completes under the question; the retest it starts...
        await arrived;
        Assert.True(_world.Ssh.LastToken.IsCancellationRequested); // ...is cancelled as it starts
        await accept;

        _world.Prompt.Answer(false); // "Continuar a editar"
        Assert.False(_world.Page.Disposed);
        Assert.Equal(1, _world.DirectTrust.Trusts); // the accepted key stays (B-6)
        Assert.Equal(ConnectionTestPhase.Cancelled, _world.Page.Controller.TestToShow()!.Phase);
    }

    private async Task OpenAddAsync()
    {
        await _world.StartAsync();
        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
        Ui7EditorSessionTests.FillDirect(_world.ViewModel, password: true);
        _world.Page.TypedPassword = "s3cret";
        _world.ResetTrustCounts();
    }

    private static HostKeyIdentity Key() =>
        HostKeyIdentity.Create("ssh-ed25519", "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('='));
}
