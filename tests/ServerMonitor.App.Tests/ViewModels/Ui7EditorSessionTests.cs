using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7A over the production session, navigation, page controller, view model, dashboard persist path, profile service,
/// server service and trust stores (see <see cref="Ui7EditorWorld"/>): every exit path ends the visit and persists nothing
/// (Vigil CP-4, Cortex R-3/R-4), an accepted key is kept on Cancel (B-6 pin), one live editor (CP-13, R-13), a fresh page
/// and view model per open (CP-10), secrets die with the page (CP-7), leaving mid-test writes nothing late (R-11), Esc
/// (R-14), the B-4 save failure and the B-5 destinations.
/// </summary>
public sealed class Ui7EditorSessionTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    public static TheoryData<string> AddExitPaths => new() { "cancel", "escape", "sidebar", "breadcrumb", "activation", "second-editor" };

    public static TheoryData<string> EditExitPaths => new() { "cancel", "back", "escape", "sidebar", "breadcrumb", "activation" };

    // ---- CP-4 / R-3 / R-4: every exit persists nothing ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AddExitPaths))]
    public async Task Add_AfterASecretAndASuccessfulTest_EveryExitPath_PersistsNothing(string exit)
    {
        await _world.StartAsync();
        var visit = OpenAdd();
        FillDirect(_world.ViewModel, password: true);
        _world.Page.TypedPassword = "s3cret";
        await _world.Page.TestAsync();
        Assert.True(_world.ViewModel.HasConnectionStatus);
        var page = _world.Page;

        Exit(exit);

        Assert.True(page.Disposed, $"{exit}: the page was not released");
        Assert.NotSame(page, _world.Host.Content);
        if (exit != "second-editor")
        {
            await Ended(visit);
        }

        Assert.Equal(1, _world.Prompt.Asked.Count); // dirty: "Descartar alterações?" once, answered "discard"
        AssertNothingPersisted();
    }

    [Theory]
    [MemberData(nameof(EditExitPaths))]
    public async Task Edit_WithAnEditedHostASecretAndATest_EveryExitPath_PersistsNothing(string exit)
    {
        var server = await _world.SeedAsync(authentication: AuthenticationMethod.Password, password: "saved");
        await _world.StartAsync();
        var before = _world.PersistedSnapshot();
        var visit = _world.OpenEditAsync(server.Id);
        _world.ViewModel.Host = "10.0.0.99";
        _world.Page.TypedPassword = "new-secret";
        await _world.Page.TestAsync();

        Exit(exit);
        await Ended(visit);

        Assert.Equal(before, _world.PersistedSnapshot());
        Assert.Equal(0, _world.Credentials.Writes);
        Assert.Equal(0, _world.Credentials.Deletes);
        // F-3 / B-8: a test of the draft never wrote the SAVED server's connection state.
        Assert.Equal(0, _world.ConnectionStates.SetCount);
        Assert.Equal(server, (await _world.ServerService.GetAllAsync()).Single());
    }

    [Fact]
    public async Task UnacceptedTrustPrompt_ThenCancel_WritesNoTrustAnywhere()
    {
        await _world.StartAsync();
        var visit = OpenAdd();
        FillDirect(_world.ViewModel);
        _world.Ssh.Result = Unknown(SshEndpoint.Create("10.0.0.9", 22));
        await _world.Page.TestAsync();
        Assert.True(_world.ViewModel.HasUnknownHostKey);

        _world.Page.Controller.Cancel();
        await Ended(visit);

        Assert.Equal(0, _world.DirectTrust.Trusts);
        Assert.Equal(0, _world.RoutedTrust.Trusts);
        AssertNothingPersisted();
    }

    /// <summary>
    /// B-6 pin (F-1, existing M14.4b-2 semantics): an EXPLICITLY accepted key is written at once, to the one store of its
    /// hop, and Cancel does not revert it. Any change to this (trust on Save, revert on Cancel) must break this test.
    /// </summary>
    [Fact]
    public async Task AcceptedTrust_ThenCancel_KeepsExactlyThatOneTrustEntry_AndNothingElse()
    {
        await _world.StartAsync();
        var visit = OpenAdd();
        FillDirect(_world.ViewModel);
        var endpoint = SshEndpoint.Create("10.0.0.9", 22);
        _world.Ssh.Result = Unknown(endpoint);
        await _world.Page.TestAsync();
        _world.Ssh.Result = TestData.Connected();

        await _world.ViewModel.TrustAndConnectAsync();
        _world.Page.Controller.Cancel();
        await Ended(visit);

        Assert.Equal(1, _world.DirectTrust.Trusts);
        Assert.Equal(0, _world.RoutedTrust.Trusts);
        Assert.NotNull(await _world.DirectTrust.GetAsync(endpoint));
        var persisted = _world.PersistedSnapshot();
        Assert.NotNull(persisted["known-hosts"]);
        Assert.Null(persisted["known-hosts.routes"]);
        Assert.Null(persisted["servers"]);
        Assert.Null(persisted["routed-servers"]);
        Assert.Equal(0, _world.Credentials.Writes);
    }

    // ---- R-3 counterpart / B-4 / B-5: Save goes only through the opener's profile path --------------------------------

    [Fact]
    public async Task Add_Saved_PersistsOnceThroughTheProfilePath_AndLandsOnTheNewServersDetail()
    {
        await _world.StartAsync();
        var visit = OpenAdd();
        FillDirect(_world.ViewModel, password: true);
        _world.Page.TypedPassword = "s3cret";
        await _world.Page.TestAsync();
        var page = _world.Page;

        var outcome = await page.SubmitAsync();
        await Ended(visit);

        Assert.Equal(ServerEditorSaveStatus.Saved, outcome!.Status);
        var saved = Assert.Single(await _world.ServerService.GetAllAsync());
        Assert.Equal("db", saved.Name);
        Assert.Equal(1, _world.Credentials.Writes);
        // The test's result reaches the saved server's state only through the dashboard's post-save path.
        Assert.Equal(1, _world.ConnectionStates.SetCount);
        Assert.NotNull(_world.ConnectionStates.Get(saved.Id));
        Assert.Equal(saved.Id, Assert.IsType<Ui7EditorWorld.DetailPageDouble>(_world.Host.Content).ServerId);
        Assert.True(page.Disposed);
        Assert.Empty(_world.Prompt.Asked); // a saved visit leaves without asking
    }

    [Fact]
    public async Task Edit_Saved_ReturnsToThatServersDetail()
    {
        var server = await _world.SeedAsync();
        await _world.StartAsync();
        var visit = _world.OpenEditAsync(server.Id);
        _world.ViewModel.Name = "web-renamed";

        var outcome = await _world.Page.SubmitAsync();
        await Ended(visit);

        Assert.Equal(ServerEditorSaveStatus.Saved, outcome!.Status);
        Assert.Equal("web-renamed", (await _world.ServerService.GetAllAsync()).Single().Name);
        Assert.Equal(server.Id, Assert.IsType<Ui7EditorWorld.DetailPageDouble>(_world.Host.Content).ServerId);
    }

    /// <summary>R-7: the server vanished between open and Save → ServerNotFound; the page stays with the form (B-4).</summary>
    [Fact]
    public async Task Edit_OfAServerThatVanished_StaysOnThePage_WithTheForm_AndCreatesNothing()
    {
        var server = await _world.SeedAsync(authentication: AuthenticationMethod.Password, password: "saved");
        await _world.StartAsync();
        var visit = _world.OpenEditAsync(server.Id);
        _world.ViewModel.Name = "kept-name";
        _world.Page.TypedPassword = "typed";
        Assert.True(await _world.ServerService.RemoveAsync(server.Id));
        var page = _world.Page;

        var outcome = await page.SubmitAsync();

        Assert.Equal(ServerEditorSaveStatus.Failed, outcome!.Status);
        Assert.True(outcome.SecretsConsumed); // the page then says "introduz de novo"
        Assert.Same(page, _world.Host.Content);
        Assert.False(page.Disposed);
        Assert.Equal("kept-name", _world.ViewModel.Name);
        Assert.Empty(await _world.ServerService.GetAllAsync());
        Assert.False(visit.IsCompleted);
        Assert.False(_world.Dashboard.IsOperationErrorOpen); // the failure belongs to the page, not the dashboard
    }

    [Fact]
    public async Task Save_WhileARestoreHoldsTheConfiguration_IsConfigurationLocked_AndThePageStays()
    {
        await _world.StartAsync();
        var visit = OpenAdd();
        FillDirect(_world.ViewModel);
        Assert.NotNull(await _world.Gate.BeginRestoreAsync(TimeSpan.Zero));

        var outcome = await _world.Page.SubmitAsync();

        Assert.Equal(ServerEditorSaveStatus.ConfigurationLocked, outcome!.Status);
        Assert.IsType<EditorPageDouble>(_world.Host.Content);
        Assert.False(visit.IsCompleted);
        Assert.False(_world.Dashboard.IsConfigurationLockedOpen);
        AssertNothingPersisted();
    }

    [Theory]
    [InlineData(NavigationDestination.Overview)]
    [InlineData(NavigationDestination.Servers)]
    public async Task Cancel_ReturnsToTheOrigin_AndRemembersTheOpenerForFocus(NavigationDestination origin)
    {
        await _world.StartAsync();
        if (origin == NavigationDestination.Servers)
        {
            _world.Navigation.GoToServers();
        }

        _world.OpenerName = "AddButton";
        var visit = OpenAdd();
        _world.Page.Controller.Cancel();
        await Ended(visit);

        Assert.Equal(origin, _world.Navigation.CurrentDestination);
        Assert.Equal("AddButton", _world.ReturnFocus.Take(origin));
        Assert.Null(_world.ReturnFocus.Take(origin)); // taken once
    }

    [Fact]
    public async Task Edit_Back_ReturnsToItsDetail_AndRemembersEditar()
    {
        var server = await _world.SeedAsync();
        await _world.StartAsync();
        _world.OpenerName = "EditButton";
        var visit = _world.OpenEditAsync(server.Id);

        _world.Page.Controller.Cancel(); // "Voltar ao detalhe" and Cancelar are the same exit
        await Ended(visit);

        Assert.Equal(server.Id, Assert.IsType<Ui7EditorWorld.DetailPageDouble>(_world.Host.Content).ServerId);
        Assert.Equal("EditButton", _world.ReturnFocus.Take(NavigationDestination.Detail));
    }

    [Fact]
    public async Task KeepEditing_StaysOnThePage_DropsTheNavigation_AndRemembersNoReturnFocus()
    {
        await _world.StartAsync();
        var visit = OpenAdd();
        _world.ViewModel.Name = "dirty";
        _world.Prompt.AutoAnswer = false;
        var page = _world.Page;

        _world.Page.Controller.Cancel();
        _world.Navigation.GoToServers();
        _world.Navigation.LeaveCurrentPageThen(_world.Navigation.GoToDashboard);

        Assert.Same(page, _world.Host.Content);
        Assert.False(page.Disposed);
        Assert.Equal(3, _world.Prompt.Asked.Count);
        Assert.Equal(NavigationDestination.ServerEditor, _world.Navigation.CurrentDestination);
        Assert.Null(_world.ReturnFocus.Take(NavigationDestination.Overview));
        Assert.False(visit.IsCompleted);
    }

    [Fact]
    public async Task CleanForm_LeavesWithoutAsking()
    {
        await _world.StartAsync();
        var visit = OpenAdd();

        _world.Navigation.GoToServers();
        await Ended(visit);

        Assert.Empty(_world.Prompt.Asked);
        Assert.Equal(NavigationDestination.Servers, _world.Navigation.CurrentDestination);
    }

    // ---- CP-13 / R-13 / CP-10: one live editor, a fresh one per open ---------------------------------------------------

    [Fact]
    public async Task OpeningASecondEditor_DisposesTheFirstViewModelBeforeTheSecondExists()
    {
        await _world.StartAsync();
        var first = OpenAdd();
        _world.Page.TypedPassword = "first-secret";
        _world.ViewModel.SelectedAuthenticationIndex = 1;
        FillDirect(_world.ViewModel, password: true);
        await _world.Page.TestAsync();
        var firstSecret = Assert.IsType<SecretValue>(_world.Ssh.Requests.Single().CredentialOverride);
        var firstViewModel = _world.ViewModel;

        var second = ((AsyncRelayCommand)_world.Dashboard.ImportFromSshCommand).ExecuteAsync();
        await Ended(first);

        Assert.Equal([true, true], _world.PreviousEditorsDisposedAtLoad);
        Assert.Throws<ObjectDisposedException>(() => firstSecret.Reveal());
        Assert.NotSame(firstViewModel, _world.ViewModel);
        Assert.Same(_world.ViewModel, _world.Session.LiveViewModel);
        Assert.Equal(2, _world.Session.CreatedViewModels);
        _world.Page.Controller.Cancel();
        await Ended(second, page: 1);
    }

    [Fact]
    public async Task OpensWhileTheUserDecides_AreRefused_NeverASecondLiveEditor()
    {
        await _world.StartAsync();
        var first = OpenAdd();
        _world.ViewModel.Name = "dirty";
        _world.Prompt.AutoAnswer = null; // held: the user is deciding
        var firstViewModel = _world.ViewModel;

        var second = ((AsyncRelayCommand)_world.Dashboard.ImportFromSshCommand).ExecuteAsync();
        var third = _world.Session.OpenSshImportAsync(_ => throw new InvalidOperationException("never saved"));

        Assert.True(third.IsCompleted); // refused while a decision is open: no view model was ever created for it
        Assert.Equal(1, _world.Session.CreatedViewModels);
        Assert.Same(firstViewModel, _world.Session.LiveViewModel);

        _world.Prompt.Answer(discard: true);
        await Ended(first);

        Assert.Equal(2, _world.Session.CreatedViewModels);
        Assert.NotSame(firstViewModel, _world.Session.LiveViewModel);
        Assert.Equal([true, true], _world.PreviousEditorsDisposedAtLoad);
        _world.Page.Controller.Cancel();
        await Ended(second, page: 1);
    }

    [Fact]
    public async Task KeepingTheFirstEditor_RefusesTheSecond()
    {
        await _world.StartAsync();
        var first = OpenAdd();
        _world.ViewModel.Name = "dirty";
        _world.Prompt.AutoAnswer = false;
        var page = _world.Page;

        var second = ((AsyncRelayCommand)_world.Dashboard.ImportFromSshCommand).ExecuteAsync();
        Assert.True(second.IsCompleted, "the refused open did not end at once");

        Assert.Same(page, _world.Host.Content);
        Assert.Equal(1, _world.Session.CreatedViewModels);
        Assert.False(first.IsCompleted);
    }

    [Fact]
    public async Task TwoConsecutiveOpens_GetDistinctPagesAndViewModels_TheFirstDisposed()
    {
        await _world.StartAsync();
        var first = OpenAdd();
        var firstPage = _world.Page;
        var firstViewModel = _world.ViewModel;
        firstPage.Controller.Cancel();
        await Ended(first);

        var second = OpenAdd();

        Assert.NotSame(firstPage, _world.Page);
        Assert.NotSame(firstViewModel, _world.ViewModel);
        Assert.True(firstPage.Disposed);
        Assert.Same(_world.ViewModel, _world.Session.LiveViewModel);
        _world.Page.Controller.Cancel();
        await Ended(second, page: 1);
    }

    // ---- CP-7: the staged secret dies with the page ---------------------------------------------------------------------

    [Fact]
    public async Task LeavingThePage_DisposesTheStagedSecret()
    {
        await _world.StartAsync();
        var visit = OpenAdd();
        _world.ViewModel.SelectedAuthenticationIndex = 1;
        FillDirect(_world.ViewModel, password: true);
        _world.Page.TypedPassword = "s3cret";
        await _world.Page.TestAsync();
        var staged = Assert.IsType<SecretValue>(_world.Ssh.Requests.Single().CredentialOverride);
        Assert.Equal("s3cret", new string(staged.Reveal()));

        new ShellViewModel(_world.Navigation).Navigate(ShellDestination.History);
        await Ended(visit);

        Assert.Throws<ObjectDisposedException>(() => staged.Reveal());
    }

    // ---- R-11 / R-14: leaving during a test ------------------------------------------------------------------------------

    [Theory]
    [InlineData("sidebar")]
    [InlineData("breadcrumb")]
    [InlineData("activation")]
    public async Task LeavingDuringATest_CancelsIt_AndNothingResumesAfterwards(string exit)
    {
        var server = await _world.SeedAsync();
        await _world.StartAsync();
        var visit = _world.OpenEditAsync(server.Id);
        var viewModel = _world.ViewModel;
        var late = RecordLateChanges(viewModel, out var markDisposed);
        _world.Ssh.Hold = true;
        var arrived = _world.Ssh.Arrived.Task;
        var test = viewModel.TestConnectionAsync();
        await arrived;

        markDisposed();
        Exit(exit);
        Assert.True(test.IsCompleted, "leaving did not cancel the running test");
        await test;
        await Ended(visit);

        Assert.Empty(late);
        Assert.Equal(0, _world.ConnectionStates.SetCount);
        Assert.Equal(1, _world.Ssh.TestConnectionCount);
        Assert.Single(_world.EditorPages); // one Show, never a duplicate
    }

    [Fact]
    public async Task Escape_DuringATest_CancelsTheTestFirst_ThenLeavesThroughTheGuard()
    {
        await _world.StartAsync();
        var visit = OpenAdd();
        FillDirect(_world.ViewModel);
        _world.Ssh.Hold = true;
        var arrived = _world.Ssh.Arrived.Task;
        var test = _world.ViewModel.TestConnectionAsync();
        await arrived;
        Assert.True(_world.ViewModel.IsTestingConnection);

        _world.Page.Controller.Escape();
        // Atlas c2 N-3: the normative signal is the running test's own token (the M-1 idiom), not whether the
        // cancellation continuation happened to run inline on this thread.
        Assert.True(_world.Ssh.LastToken.IsCancellationRequested, "leaving did not cancel the running test");
        await test;
        await Ended(visit);

        Assert.Equal(1, _world.Prompt.Asked.Count); // the form was dirty: Esc asked before leaving
        Assert.Equal(NavigationDestination.Overview, _world.Navigation.CurrentDestination);
        AssertNothingPersisted();
    }

    // ---- CP-11: no submit while a test or a trust write runs -------------------------------------------------------------

    [Fact]
    public async Task Submit_WhileTesting_ProducesNoResult()
    {
        await _world.StartAsync();
        var visit = OpenAdd();
        FillDirect(_world.ViewModel);
        _world.Ssh.Hold = true;
        var arrived = _world.Ssh.Arrived.Task;
        var test = _world.ViewModel.TestConnectionAsync();
        await arrived;

        Assert.Null(await _world.Page.SubmitAsync());

        _world.Ssh.Release();
        await test;
        AssertNothingPersisted();
        _world.Page.Controller.Cancel();
        await Ended(visit);
    }

    [Fact]
    public async Task Submit_WhileAnAcceptedKeyIsBeingWritten_ProducesNoResult()
    {
        await _world.StartAsync();
        var visit = OpenAdd();
        FillDirect(_world.ViewModel);
        _world.Ssh.Result = Unknown(SshEndpoint.Create("10.0.0.9", 22));
        await _world.Page.TestAsync();
        _world.Ssh.Result = TestData.Connected();
        _world.DirectTrust.Barrier = new TaskCompletionSource();

        var trust = _world.ViewModel.TrustAndConnectAsync();
        Assert.True(_world.ViewModel.IsConnectionWorkInProgress);
        Assert.False(_world.ViewModel.IsTestingConnection); // the H7 window: not a test, still no submit
        Assert.Null(await _world.Page.SubmitAsync());

        _world.DirectTrust.Barrier.SetResult();
        await trust;
        Assert.Null((await _world.ServerService.GetAllAsync()).SingleOrDefault());
        _world.Page.Controller.Cancel();
        await Ended(visit);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    // Fails (instead of hanging) when an exit did not release the visit's page: the visit could then never end.
    private Task Ended(Task visit, int page = 0)
    {
        Assert.True(_world.EditorPages[page].Disposed, "the editor page of this visit was never released");
        return visit;
    }

    private Task OpenAdd() => ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync();

    private void Exit(string exit)
    {
        switch (exit)
        {
            case "cancel":
            case "back":
                _world.Page.Controller.Cancel();
                break;
            case "escape":
                _world.Page.Controller.Escape();
                break;
            case "sidebar":
                new ShellViewModel(_world.Navigation).Navigate(ShellDestination.History);
                break;
            case "breadcrumb":
                _world.Navigation.GoToDashboard();
                break;
            case "activation":
                // App.ExecuteActivationIntent: the production entry point (Atlas c2 N-2) - the same exit guard, then the
                // dashboard.
                _world.Navigation.LeaveCurrentPageForActivation(_world.Navigation.GoToDashboard);
                break;
            case "second-editor":
                _ = ((AsyncRelayCommand)_world.Dashboard.ImportFromSshCommand).ExecuteAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(exit));
        }
    }

    private void AssertNothingPersisted()
    {
        Assert.Equal(0, _world.Credentials.Writes);
        Assert.Equal(0, _world.Credentials.Deletes);
        Assert.All(_world.PersistedSnapshot(), pair => Assert.Null(pair.Value));
        Assert.Equal(0, _world.ConnectionStates.SetCount);
    }

    internal static void FillDirect(ServerEditorViewModel viewModel, bool password = false)
    {
        viewModel.Name = "db";
        viewModel.Host = "10.0.0.9";
        viewModel.Username = "ops";
        if (password)
        {
            viewModel.SelectedAuthenticationIndex = 1;
        }
        else
        {
            viewModel.PrivateKeyPath = Path.Combine(Path.GetTempPath(), "servermonitor-ui7-tests", "id_ed25519");
        }
    }

    internal static SshConnectionResult Unknown(SshEndpoint endpoint) => new()
    {
        State = ServerConnectionState.HostKeyUnknown,
        ErrorCode = SshConnectionErrorCode.HostKeyUnknown,
        ReachedStage = SshConnectionStage.PortReachable,
        PresentedHostKey = HostKeyIdentity.Create("ssh-ed25519", "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=')),
        HostKeyHop = SshHostKeyHop.Direct,
        HostKeyEndpoint = endpoint
    };

    /// <summary>Records every property change raised after the returned action is called (the moment the editor goes).</summary>
    internal static List<string> RecordLateChanges(ServerEditorViewModel viewModel, out Action markDisposed)
    {
        var late = new List<string>();
        var disposed = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (disposed)
            {
                lock (late)
                {
                    late.Add(e.PropertyName ?? "*");
                }
            }
        };
        viewModel.ConnectionChecklist.PropertyChanged += (_, e) =>
        {
            if (disposed)
            {
                lock (late)
                {
                    late.Add("Checklist." + e.PropertyName);
                }
            }
        };
        markDisposed = () => disposed = true;
        return late;
    }
}
