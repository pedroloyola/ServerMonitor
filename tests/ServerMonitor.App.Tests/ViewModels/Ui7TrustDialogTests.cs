using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7B (B-6, B-10, Vigil H1/H6, TR-1..TR-3) over the production session, page controller, view model and BOTH real JSON
/// trust stores (recorded): the page's dialog host only SHOWS a prompt; a key is written only by an explicit accept of the
/// key on screen, through the same <see cref="ServerEditorViewModel.TrustAndConnectAsync"/>, into its hop's store. No
/// TOFU, no trust on Save, a mismatch is never accepted, and a prompt dies with its editor (Cortex R-8).
/// </summary>
public sealed class Ui7TrustDialogTests : IDisposable
{
    private static readonly SshEndpoint Direct = SshEndpoint.Create("10.0.0.9", 22);
    private static readonly SshEndpoint JumpEndpoint = SshEndpoint.Create("bastion.example.test", 2222);
    private static readonly SshEndpoint TargetEndpoint = SshEndpoint.Create("10.0.0.5", 22);
    private static readonly SshRoute Route = SshRoute.Create(JumpEndpoint, TargetEndpoint);

    private readonly Ui7EditorWorld _world = new();
    // The open command completes when its visit ENDS (UI.7A): started, never awaited while the editor is open.
    private Task _visit = Task.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ---- CP-3 (TR-1): showing is not trusting ---------------------------------------------------------------------------

    [Fact]
    public async Task CP3_AnUnknownKey_IsShown_AndNothingIsTrusted_UntilTheExplicitAccept()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);

        await _world.Page.TestAsync();

        var prompt = Assert.Single(_world.Page.ShownPrompts);
        Assert.Equal(HostKeyPromptKind.Unknown, prompt.Kind);
        Assert.True(prompt.CanAccept);
        Assert.Null(prompt.Step);
        Assert.Equal("10.0.0.9:22", prompt.Subject);
        Assert.Equal("ServerEditorTrustAcceptTest", prompt.AcceptKey);
        Assert.False(string.IsNullOrEmpty(prompt.Fingerprint));
        AssertNoTrustWritten();

        // The dialog is evaluated again (e.g. the window re-renders): still shown, still nothing written.
        Assert.Equal(prompt, _world.Page.EvaluateDialogs());
        AssertNoTrustWritten();

        Assert.True(await _world.Page.AcceptAsync(prompt));
        Assert.Equal(1, _world.DirectTrust.Trusts);
        Assert.Equal(0, _world.RoutedTrust.Trusts);
        Assert.NotNull(await _world.DirectTrust.GetAsync(Direct));
        Assert.Equal(2, _world.Ssh.TestConnectionCount); // the SAME flow retests after the accept
    }

    [Fact]
    public async Task CP3_SavingWithoutAnAccept_WritesNoTrust_AndTheSaveIsNotATrust()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        await _world.Page.TestAsync();
        Assert.NotNull(_world.Page.EvaluateDialogs());

        _world.Page.TypedPassword = "s3cret";
        var outcome = await _world.Page.SubmitAsync();

        Assert.Equal(ServerEditorSaveStatus.Saved, outcome!.Status);
        AssertNoTrustWritten();
        Assert.Null(_world.PersistedSnapshot()["known-hosts"]);
        Assert.Null(_world.PersistedSnapshot()["known-hosts.routes"]);
        Assert.Single(await _world.ServerService.GetAllAsync()); // the server was saved; its key was not trusted
    }

    [Fact]
    public async Task Dismissing_DropsThePrompt_AndWritesNothing()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        await _world.Page.TestAsync();
        var shown = Assert.Single(_world.Page.ShownPrompts);

        _world.Page.Controller.DismissTrustPrompt();

        Assert.Null(_world.Page.Controller.PromptToShow());
        Assert.False(await _world.Page.Controller.AcceptTrustAsync(shown));
        AssertNoTrustWritten();
        Assert.Equal(1, _world.Ssh.TestConnectionCount);
    }

    // ---- CP-2 (TR-2/TR-3): only the key that is still pending for the form on screen -----------------------------------

    /// <summary>NEW (Vigil CP-2): a direct server whose host was edited after the prompt is never trusted.</summary>
    [Fact]
    public async Task CP2_ADirectHostEditedAfterThePrompt_IsNeverTrusted()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        await _world.Page.TestAsync();
        var shown = Assert.Single(_world.Page.ShownPrompts);

        _world.ViewModel.Host = "10.0.0.10";

        Assert.Null(_world.Page.EvaluateDialogs()); // the prompt is gone with the result it came from
        Assert.False(await _world.Page.AcceptAsync(shown));
        await _world.ViewModel.TrustAndConnectAsync(); // even the view model's own entry point has nothing to trust
        AssertNoTrustWritten();
        Assert.Equal(1, _world.Ssh.TestConnectionCount);
    }

    /// <summary>
    /// Vigil CP-2 (direct twin of A_key_for_a_route_the_form_no_longer_describes_is_not_trusted): a key presented for an
    /// endpoint that is not the form's is refused even when accepted explicitly - only the endpoint guard stops it.
    /// </summary>
    [Fact]
    public async Task CP2_AKeyPresentedForAnotherEndpoint_IsRefusedEvenWhenAccepted()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(SshEndpoint.Create("10.0.0.99", 22));
        await _world.Page.TestAsync();
        var shown = Assert.Single(_world.Page.ShownPrompts);
        Assert.Equal("10.0.0.99:22", shown.Subject); // the dialog names who presented the key, not the form

        await _world.Page.AcceptAsync(shown);

        AssertNoTrustWritten();
        Assert.Null(await _world.DirectTrust.GetAsync(Direct));
        Assert.Null(await _world.DirectTrust.GetAsync(SshEndpoint.Create("10.0.0.99", 22)));
    }

    /// <summary>Vigil CP-2 (routed twin): a target key presented for a route the form does not describe is never trusted.</summary>
    [Fact]
    public async Task CP2_ATargetKeyPresentedForAnotherRoute_IsRefusedEvenWhenAccepted()
    {
        await OpenAddRoutedAsync();
        var otherRoute = SshRoute.Create(SshEndpoint.Create("other-bastion.example.test", 2222), TargetEndpoint);
        _world.Ssh.Result = TargetUnknown() with { HostKeyRoute = otherRoute };
        await _world.Page.TestAsync();
        var shown = Assert.Single(_world.Page.ShownPrompts);

        await _world.Page.AcceptAsync(shown);

        AssertNoTrustWritten();
        Assert.Null(await _world.RoutedTrust.GetAsync(Route));
        Assert.Null(await _world.RoutedTrust.GetAsync(otherRoute));
    }

    // ---- Cortex section 8: the layer and the exit guard -----------------------------------------------------------------

    [Fact]
    public async Task Layer_LeavingWithThePromptOpen_AsksOverIt_AndKeepEditingFindsThePromptAsItWas()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        await _world.Page.TestAsync();
        var shown = Assert.Single(_world.Page.ShownPrompts);
        Assert.Equal(ServerEditorLayer.Trust, _world.Page.Controller.LayerToShow(accepting: false));
        _world.Prompt.AutoAnswer = false;

        new ShellViewModel(_world.Navigation).Navigate(ShellDestination.History);

        Assert.Single(_world.Prompt.Asked); // the guard's question opened over the layer
        Assert.False(_world.Page.Disposed);
        Assert.Equal(ServerEditorLayer.Trust, _world.Page.Controller.LayerToShow(accepting: false));
        Assert.Equal(shown, _world.Page.Controller.PromptToShow()); // nothing lost silently
        AssertNoTrustWritten();

        _world.Prompt.AutoAnswer = true;
        var page = _world.Page;
        _world.Navigation.LeaveCurrentPageForActivation(_world.Navigation.GoToDashboard);
        Assert.True(page.Disposed);
        AssertNoTrustWritten();
    }

    [Fact]
    public async Task Layer_LeavingDuringTheRetestAfterAnAccept_CancelsTheTestFirst_ThenAsks()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        await _world.Page.TestAsync();
        var shown = Assert.Single(_world.Page.ShownPrompts);
        _world.Ssh.Hold = true;
        var arrived = _world.Ssh.Arrived.Task;
        var accept = _world.Page.Controller.AcceptTrustAsync(shown);
        await arrived; // the key is written; the SAME retest is running (held)
        Assert.Equal(ServerEditorLayer.Trust, _world.Page.Controller.LayerToShow(accepting: true));
        var page = _world.Page;
        var viewModel = _world.ViewModel;
        var retest = _world.Ssh.LastToken; // the held retest's own token
        bool? cancelledWhenAsked = null;
        _world.Prompt.OnAsked = () => cancelledWhenAsked = retest.IsCancellationRequested;

        new ShellViewModel(_world.Navigation).Navigate(ShellDestination.History);
        await accept;

        Assert.Single(_world.Prompt.Asked);
        Assert.True(cancelledWhenAsked, "the running retest must be cancelled BEFORE the question is asked");
        Assert.True(page.Disposed);
        Assert.Equal(1, _world.DirectTrust.Trusts); // the accepted key stays (B-6); nothing else
        Assert.Equal(2, _world.Ssh.TestConnectionCount); // no further test after the leave
    }

    [Fact]
    public async Task Layer_EscDuringTheRetest_CancelsThatTest_AndTheLayerThenCloses()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        await _world.Page.TestAsync();
        var shown = Assert.Single(_world.Page.ShownPrompts);
        _world.Ssh.Hold = true;
        var arrived = _world.Ssh.Arrived.Task;
        var accept = _world.Page.Controller.AcceptTrustAsync(shown);
        await arrived;

        _world.Page.Controller.CloseLayer(ServerEditorLayer.Trust, accepting: true);
        await accept;

        Assert.False(_world.ViewModel.IsTestingConnection);
        Assert.Equal(ServerEditorLayer.None, _world.Page.Controller.LayerToShow(accepting: false));
        Assert.False(_world.Page.Disposed); // Esc in the layer is not Back
        Assert.Empty(_world.Prompt.Asked);
    }

    [Fact]
    public async Task Layer_BackToTheFormOnAMismatch_ClosesIt_AndTheNextMismatchShowsAgain()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Mismatch(SshHostKeyHop.Direct);
        await _world.Page.TestAsync();
        Assert.Equal(ServerEditorLayer.Trust, _world.Page.Controller.LayerToShow(accepting: false));

        _world.Page.Controller.CloseLayer(ServerEditorLayer.Trust, accepting: false);
        Assert.Equal(ServerEditorLayer.None, _world.Page.Controller.LayerToShow(accepting: false));

        await _world.Page.TestAsync();
        Assert.Equal(ServerEditorLayer.Trust, _world.Page.Controller.LayerToShow(accepting: false));
        AssertNoTrustWritten();
    }

    [Fact]
    public async Task AStaleDialog_CannotAcceptADifferentKey_ThanTheOneItShows()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        await _world.Page.TestAsync();
        var shown = Assert.Single(_world.Page.ShownPrompts);

        // A second test meets ANOTHER key for the same endpoint; the old dialog content must not accept it.
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct) with
        {
            PresentedHostKey = HostKeyIdentity.Create("ssh-ed25519", "SHA256:" + Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()).TrimEnd('='))
        };
        await _world.ViewModel.TestConnectionAsync();

        Assert.False(await _world.Page.Controller.AcceptTrustAsync(shown));
        AssertNoTrustWritten();
        Assert.NotEqual(shown, _world.Page.Controller.PromptToShow());
    }

    // ---- CP-1 + B-10: the two-step routed trust through the dialog host ------------------------------------------------

    [Fact]
    public async Task CP1_TheRoutedTwoStepTrust_ThroughTheDialog_WritesEachHopToItsOwnStore()
    {
        await OpenAddRoutedAsync();
        _world.Ssh.Queue.Enqueue(JumpUnknown());
        _world.Ssh.Queue.Enqueue(TargetUnknown());
        _world.Ssh.Result = TestData.Connected();

        await _world.Page.TestAsync();
        var step1 = Assert.Single(_world.Page.ShownPrompts);
        Assert.Equal((HostKeyPromptKind.Unknown, SshHostKeyHop.Jump, 1), (step1.Kind, step1.Hop, step1.Step));
        Assert.Equal("ServerEditorTrustJumpTitle", step1.TitleKey);
        Assert.Equal("ServerEditorTrustAcceptContinue", step1.AcceptKey);
        Assert.Equal("jump host bastion.example.test:2222", step1.Subject);
        AssertNoTrustWritten();

        Assert.True(await _world.Page.AcceptAsync(step1));
        Assert.Equal(1, _world.DirectTrust.Trusts);
        Assert.Equal(0, _world.RoutedTrust.Trusts);
        Assert.NotNull(await _world.DirectTrust.GetAsync(JumpEndpoint));
        var step2 = _world.Page.ShownPrompts[^1];
        Assert.Equal((SshHostKeyHop.Target, 2), (step2.Hop, step2.Step));
        Assert.Equal("ServerEditorTrustTargetTitle", step2.TitleKey);
        Assert.Equal("target 10.0.0.5:22 via bastion.example.test:2222", step2.Subject);

        Assert.True(await _world.Page.AcceptAsync(step2));
        Assert.Equal(1, _world.DirectTrust.Trusts);
        Assert.Equal(1, _world.RoutedTrust.Trusts);
        Assert.NotNull(await _world.RoutedTrust.GetAsync(Route));
        Assert.Null(await _world.DirectTrust.GetAsync(TargetEndpoint)); // a target key never reaches the direct store
        Assert.Null(_world.Page.Controller.PromptToShow());
        Assert.Equal(3, _world.Ssh.TestConnectionCount);
    }

    // ---- B-10 / TR-5: a mismatch is shown, never accepted ---------------------------------------------------------------

    [Theory]
    [InlineData(SshHostKeyHop.Direct, "ServerEditorTrustMismatchTitle", null)]
    [InlineData(SshHostKeyHop.Jump, "ServerEditorTrustJumpChangedTitle", 1)]
    [InlineData(SshHostKeyHop.Target, "ServerEditorTrustTargetChangedTitle", 2)]
    public async Task AMismatch_HasOnlyBackToTheForm_AndCanNeverBeAccepted(SshHostKeyHop hop, string title, int? step)
    {
        if (hop == SshHostKeyHop.Direct)
        {
            await OpenAddDirectAsync();
        }
        else
        {
            await OpenAddRoutedAsync();
        }

        _world.Ssh.Result = Mismatch(hop);
        await _world.Page.TestAsync();

        var prompt = Assert.Single(_world.Page.ShownPrompts);
        Assert.Equal(HostKeyPromptKind.Mismatch, prompt.Kind);
        Assert.False(prompt.CanAccept);
        Assert.Null(prompt.AcceptKey);
        Assert.Equal("ServerEditorTrustBack", prompt.CloseKey);
        Assert.Equal(title, prompt.TitleKey);
        Assert.Equal(step, prompt.Step);
        Assert.False(string.IsNullOrEmpty(prompt.TrustedFingerprint));
        Assert.NotEqual(prompt.TrustedFingerprint, prompt.Fingerprint);

        Assert.False(await _world.Page.AcceptAsync(prompt));
        Assert.False(await _world.Page.Controller.AcceptTrustAsync(prompt with { Kind = HostKeyPromptKind.Unknown }));
        _world.Page.Controller.DismissTrustPrompt(); // "Voltar ao formulário": nothing to dismiss, nothing written
        AssertNoTrustWritten();
        Assert.Equal(1, _world.Ssh.TestConnectionCount);
    }

    [Fact]
    public async Task NoAcceptWhileAnotherTestRuns()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        await _world.Page.TestAsync();
        var shown = Assert.Single(_world.Page.ShownPrompts);

        _world.Ssh.Hold = true;
        var arrived = _world.Ssh.Arrived.Task; // the scripted service swaps the signal as the held test arrives
        var running = _world.ViewModel.TestConnectionAsync();
        await arrived;

        Assert.False(await _world.Page.Controller.AcceptTrustAsync(shown));
        _world.Ssh.Release(TestData.Connected());
        await running;
        AssertNoTrustWritten();
    }

    // ---- Cortex R-8: a prompt never survives its editor ----------------------------------------------------------------

    [Fact]
    public async Task R8_LeavingWithAPendingPrompt_AndComingBack_StartsWithNoPrompt_AndNothingTrusted()
    {
        await OpenAddDirectAsync();
        _world.Ssh.Result = Ui7EditorSessionTests.Unknown(Direct);
        await _world.Page.TestAsync();
        var first = _world.Page;
        var firstViewModel = _world.ViewModel;
        var shown = Assert.Single(first.ShownPrompts);

        first.Controller.Cancel(); // dirty → "Descartar alterações?" → discard
        await _visit; // the first visit is over
        Assert.True(first.Disposed);
        Assert.False(await first.Controller.AcceptTrustAsync(shown)); // the old dialog's accept is dead
        Assert.Null(first.Controller.PromptToShow());

        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
        Assert.NotSame(firstViewModel, _world.ViewModel);
        Assert.False(_world.ViewModel.HasUnknownHostKey);
        Assert.Null(_world.Page.EvaluateDialogs());
        AssertNoTrustWritten();
    }

    // ---- presentation mapping (pure) -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HostKeyPromptKind.Unknown, SshHostKeyHop.Direct, "ServerEditorTrustUnknownTitle", "ServerEditorTrustUnknownBody", "ServerEditorTrustUnknownScope", "ServerEditorTrustAcceptTest", "ServerEditorTrustCancel", null)]
    [InlineData(HostKeyPromptKind.Unknown, SshHostKeyHop.Jump, "ServerEditorTrustJumpTitle", "ServerEditorTrustJumpBody", "ServerEditorTrustJumpScope", "ServerEditorTrustAcceptContinue", "ServerEditorTrustCancel", 1)]
    [InlineData(HostKeyPromptKind.Unknown, SshHostKeyHop.Target, "ServerEditorTrustTargetTitle", "ServerEditorTrustTargetBody", "ServerEditorTrustTargetScope", "ServerEditorTrustAcceptContinue", "ServerEditorTrustCancel", 2)]
    [InlineData(HostKeyPromptKind.Mismatch, SshHostKeyHop.Direct, "ServerEditorTrustMismatchTitle", "ServerEditorTrustMismatchBody", "ServerEditorTrustMismatchScope", null, "ServerEditorTrustBack", null)]
    [InlineData(HostKeyPromptKind.Mismatch, SshHostKeyHop.Jump, "ServerEditorTrustJumpChangedTitle", "ServerEditorTrustJumpChangedBody", "ServerEditorTrustMismatchScope", null, "ServerEditorTrustBack", 1)]
    [InlineData(HostKeyPromptKind.Mismatch, SshHostKeyHop.Target, "ServerEditorTrustTargetChangedTitle", "ServerEditorTrustTargetChangedBody", "ServerEditorTrustMismatchScope", null, "ServerEditorTrustBack", 2)]
    public void ThePromptCopy_FollowsTheKindAndTheHop(
        HostKeyPromptKind kind, SshHostKeyHop hop, string title, string body, string scope, string? accept, string close, int? step)
    {
        var prompt = new HostKeyTrustPrompt(kind, hop, "subject", "ED25519", "SHA256:x", "SHA256:y");

        Assert.Equal((title, body, scope, accept, close, step), (prompt.TitleKey, prompt.BodyKey, prompt.ScopeKey, prompt.AcceptKey, prompt.CloseKey, prompt.Step));
    }

    [Fact]
    public void EveryTrustDialogKey_ExistsInEveryCulture_AndNeverSaysTheServerIsSafeBecauseItAnswered()
    {
        var keys = new[]
        {
            "ServerEditorTrustUnknownTitle", "ServerEditorTrustJumpTitle", "ServerEditorTrustTargetTitle", "ServerEditorTrustMismatchTitle",
            "ServerEditorTrustJumpChangedTitle", "ServerEditorTrustTargetChangedTitle", "ServerEditorTrustUnknownBody", "ServerEditorTrustJumpBody",
            "ServerEditorTrustTargetBody", "ServerEditorTrustMismatchBody", "ServerEditorTrustJumpChangedBody", "ServerEditorTrustTargetChangedBody",
            "ServerEditorTrustUnknownScope", "ServerEditorTrustJumpScope", "ServerEditorTrustTargetScope", "ServerEditorTrustMismatchScope",
            "ServerEditorTrustAcceptTest", "ServerEditorTrustAcceptContinue", "ServerEditorTrustCancel", "ServerEditorTrustBack",
            "ServerEditorTrustStepFormat", "ServerEditorTrustPresentedFormat", "ServerEditorTrustReceivedFormat", "ServerEditorTrustSavedLabel",
            "ServerEditorTrustWorking"
        };
        foreach (var culture in new[] { "pt-PT", "pt-BR", "en-US" })
        {
            var resources = Ui7Resources.Load(culture);
            foreach (var key in keys)
            {
                var value = Assert.Contains(key, resources);
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture}: {key}");
                foreach (var claim in new[] { "segur", "safe", "seguro" })
                {
                    Assert.DoesNotContain(claim, value, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private async Task OpenAddDirectAsync()
    {
        await _world.StartAsync();
        _visit = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync();
        Ui7EditorSessionTests.FillDirect(_world.ViewModel, password: true);
        _world.Page.TypedPassword = "s3cret";
        _world.ResetTrustCounts();
    }

    private async Task OpenAddRoutedAsync()
    {
        await _world.StartAsync();
        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
        var viewModel = _world.ViewModel;
        viewModel.Name = "private-db";
        viewModel.Host = "10.0.0.5";
        viewModel.Username = "deploy";
        viewModel.SelectedAuthenticationIndex = 1;
        viewModel.UseJumpHost = true;
        viewModel.JumpHost = "bastion.example.test";
        viewModel.JumpPort = "2222";
        viewModel.JumpUsername = "jumper";
        viewModel.SelectedJumpAuthenticationIndex = 1;
        viewModel.CaptureJumpSecret("jump-secret");
        _world.Page.TypedPassword = "s3cret";
        _world.ResetTrustCounts();
    }

    private void AssertNoTrustWritten()
    {
        Assert.Equal(0, _world.DirectTrust.Trusts);
        Assert.Equal(0, _world.RoutedTrust.Trusts);
    }

    private static HostKeyIdentity Key(byte value) =>
        HostKeyIdentity.Create("ssh-ed25519", "SHA256:" + Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray()).TrimEnd('='));

    private static SshConnectionResult JumpUnknown() => new()
    {
        State = ServerConnectionState.HostKeyUnknown,
        ErrorCode = SshConnectionErrorCode.JumpHostKeyUnknown,
        PresentedHostKey = Key(1),
        HostKeyHop = SshHostKeyHop.Jump,
        HostKeyEndpoint = JumpEndpoint
    };

    private static SshConnectionResult TargetUnknown() => new()
    {
        State = ServerConnectionState.HostKeyUnknown,
        ErrorCode = SshConnectionErrorCode.RoutedHostKeyUnknown,
        ReachedStage = SshConnectionStage.PortReachable,
        PresentedHostKey = Key(2),
        HostKeyHop = SshHostKeyHop.Target,
        HostKeyRoute = Route
    };

    private static SshConnectionResult Mismatch(SshHostKeyHop hop) => hop switch
    {
        SshHostKeyHop.Target => new SshConnectionResult
        {
            State = ServerConnectionState.HostKeyMismatch,
            ErrorCode = SshConnectionErrorCode.RoutedHostKeyMismatch,
            PresentedHostKey = Key(9),
            HostKeyHop = SshHostKeyHop.Target,
            HostKeyRoute = Route,
            TrustedRoutedHostKey = new TrustedRoutedHostKey { Route = Route, Identity = Key(2), ConfirmedAt = DateTimeOffset.UnixEpoch }
        },
        SshHostKeyHop.Jump => new SshConnectionResult
        {
            State = ServerConnectionState.HostKeyMismatch,
            ErrorCode = SshConnectionErrorCode.JumpHostKeyMismatch,
            PresentedHostKey = Key(9),
            HostKeyHop = SshHostKeyHop.Jump,
            HostKeyEndpoint = JumpEndpoint,
            TrustedHostKey = new TrustedHostKey { Endpoint = JumpEndpoint, Identity = Key(1), ConfirmedAt = DateTimeOffset.UnixEpoch }
        },
        _ => new SshConnectionResult
        {
            State = ServerConnectionState.HostKeyMismatch,
            ErrorCode = SshConnectionErrorCode.HostKeyMismatch,
            ReachedStage = SshConnectionStage.PortReachable,
            PresentedHostKey = Key(9),
            HostKeyHop = SshHostKeyHop.Direct,
            HostKeyEndpoint = Direct,
            TrustedHostKey = new TrustedHostKey { Endpoint = Direct, Identity = Key(3), ConfirmedAt = DateTimeOffset.UnixEpoch }
        }
    };
}
