using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7A fix round c1 (Cortex M-1, n-2, n-4) over the production session, navigation, controller and persist path, with
/// the persist of a Save held by a barrier in the recording credential store: while a Save is in flight the exit guard
/// refuses WITHOUT asking (it never asks about - or discards - an edit that is being committed), the session then decides
/// the destination; a saved visit is never left orphaned on screen; the dirty state is exact after a submit.
/// </summary>
public sealed class Ui7SaveGuardTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    public static TheoryData<string> ExitsDuringPersist => new() { "sidebar", "escape", "activation", "cancel", "breadcrumb" };

    /// <summary>M-1 (i): every exit during the persist is refused without a prompt; after the release it lands on Detail.</summary>
    [Theory]
    [MemberData(nameof(ExitsDuringPersist))]
    public async Task M1_AnExitDuringTheSavesPersist_IsRefusedWithoutAPrompt_AndTheSaveLandsOnDetail(string exit)
    {
        var page = await OpenDirtyAddAsync();
        var barrier = new TaskCompletionSource();
        _world.Credentials.WriteBarrier = barrier;

        var submit = page.SubmitAsync();
        await _world.Credentials.WriteArrived.Task;
        Assert.True(page.Controller.IsSaving);

        Exit(exit);

        Assert.Empty(_world.Prompt.Asked);
        Assert.Same(page, _world.Host.Content); // still the editor: the Save decides
        Assert.False(page.Disposed);

        barrier.SetResult();
        var outcome = await submit;

        Assert.Equal(ServerEditorSaveStatus.Saved, outcome!.Status);
        Assert.False(page.Controller.IsSaving);
        var detail = Assert.IsType<Ui7EditorWorld.DetailPageDouble>(_world.Host.Content);
        var saved = Assert.Single(await _world.ServerService.GetAllAsync());
        Assert.Equal(saved.Id, detail.ServerId);
        Assert.True(page.Disposed);
        Assert.Empty(_world.Prompt.Asked);
    }

    /// <summary>M-1 (ii): a persist that fails, with Esc pressed during it: the page stays, then leaves normally.</summary>
    [Fact]
    public async Task M1_AFailedPersist_WithEscDuringIt_Stays_ThenLeavesNormally()
    {
        var page = await OpenDirtyAddAsync();
        var barrier = new TaskCompletionSource();
        _world.Credentials.WriteBarrier = barrier;
        _world.Credentials.FailWrites = true;

        var submit = page.SubmitAsync();
        await _world.Credentials.WriteArrived.Task;
        page.Controller.Escape();
        Assert.Empty(_world.Prompt.Asked);

        barrier.SetResult();
        var outcome = await submit;

        Assert.Equal(ServerEditorSaveStatus.Failed, outcome!.Status);
        Assert.Same(page, _world.Host.Content);
        Assert.False(page.Disposed);
        Assert.Empty(await _world.ServerService.GetAllAsync());

        // Not saving any more: Esc is the ordinary Back through the guard (dirty → asked once → discard).
        _world.Credentials.WriteBarrier = null;
        page.Controller.Escape();
        Assert.Single(_world.Prompt.Asked);
        Assert.True(page.Disposed);
        Assert.IsNotType<EditorPageDouble>(_world.Host.Content);
    }

    /// <summary>M-1 (3): a visit that saved but stayed on screen (its navigation not taken) is never orphaned.</summary>
    [Fact]
    public async Task M1_ASavedVisitStillOnScreen_GoesToItsDetail_OnTheNextSubmit()
    {
        var page = await OpenDirtyAddAsync(keyAuth: true); // no secret: the second submit is valid too
        _world.Prompt.AutoAnswer = null;
        page.Controller.Cancel(); // "Descartar alterações?" is now pending: the save's own navigation will be dropped

        var outcome = await page.SubmitAsync(); // (a test can submit under the question; the UI cannot - it is modal)
        Assert.Equal(ServerEditorSaveStatus.Saved, outcome!.Status);
        Assert.Same(page, _world.Host.Content);
        _world.Prompt.Answer(false); // "Continuar a editar"
        Assert.Same(page, _world.Host.Content);

        var again = await page.SubmitAsync();

        Assert.Equal(ServerEditorSaveStatus.NotCurrent, again!.Status);
        var detail = Assert.IsType<Ui7EditorWorld.DetailPageDouble>(_world.Host.Content);
        Assert.Equal(Assert.Single(await _world.ServerService.GetAllAsync()).Id, detail.ServerId);
        Assert.True(page.Disposed);
    }

    /// <summary>Cortex n-2: after a failed persist the consumed secrets no longer count as an unsaved edit of their own.</summary>
    [Fact]
    public async Task N2_TheDirtyState_IsRecomputedOnceTheSubmitHandsTheSecretsOver()
    {
        var server = await _world.SeedAsync(authentication: Core.Enums.AuthenticationMethod.Password, password: "saved");
        await _world.StartAsync();
        _ = _world.OpenEditAsync(server.Id);
        var viewModel = _world.ViewModel;
        viewModel.CaptureSecret("new-password");
        Assert.True(viewModel.IsDirty); // a staged secret is an edit

        Assert.True(viewModel.TryCreateResult(out var result));
        result!.Dispose();

        Assert.False(viewModel.IsDirty); // the form is the opened one again: the secret went to the result
    }

    /// <summary>Cortex n-4: without a window there is nobody to ask - keep editing, discard nothing.</summary>
    [Fact]
    public async Task N4_NoWindow_TheDiscardQuestionAnswersKeepEditing()
    {
        var prompt = new ServerEditorDiscardPrompt(new NoWindow(), new FakeLocalizationService());

        Assert.False(await prompt.ConfirmDiscardAsync(new ServerEditorDiscardContext(ServerEditorMode.Add, string.Empty, "db")));
    }

    /// <summary>
    /// Final c3 (Atlas c3-L1): "Importar de SSH" during the Save's persist opens nothing - no load of ~/.ssh/config, no
    /// import state, no layer - and the same gesture after the Save works as before.
    /// </summary>
    [Fact]
    public async Task C3_ImportDuringTheSavesPersist_OpensNothing_NoLoad_NoLayer()
    {
        var source = new CountingImportSource();
        using var world = new Ui7EditorWorld(importSource: source);
        await world.StartAsync();
        _ = ((AsyncRelayCommand)world.Dashboard.AddServerCommand).ExecuteAsync();
        Ui7EditorSessionTests.FillDirect(world.ViewModel, password: true);
        world.Page.TypedPassword = "s3cret";
        world.Credentials.FailWrites = true; // the page stays after the Save, so the import can be tried again
        var barrier = new TaskCompletionSource();
        world.Credentials.WriteBarrier = barrier;

        var submit = world.Page.SubmitAsync();
        await world.Credentials.WriteArrived.Task;
        Assert.True(world.Page.Controller.IsSaving);

        await world.Page.Controller.OpenImportAsync();

        Assert.Equal(0, source.Loads);
        Assert.False(world.ViewModel.IsSshConfigImportOpen);
        Assert.False(world.ViewModel.IsLoadingSshConfig);
        Assert.Equal(ServerEditorLayer.None, world.Page.Controller.LayerToShow(accepting: false));

        barrier.SetResult();
        Assert.Equal(ServerEditorSaveStatus.Failed, (await submit)!.Status);
        Assert.False(world.Page.Controller.IsSaving);

        // Control: not saving any more, the same gesture loads and opens the import layer.
        world.Credentials.WriteBarrier = null;
        await world.Page.Controller.OpenImportAsync();
        Assert.Equal(1, source.Loads);
        Assert.Equal(ServerEditorLayer.Import, world.Page.Controller.LayerToShow(accepting: false));
    }

    private sealed class CountingImportSource : ServerMonitor.Core.Interfaces.ISshConfigImportSource
    {
        public int Loads { get; private set; }

        public Task<ServerMonitor.Core.SshConfig.SshConfigImportResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            return Task.FromResult(ServerMonitor.Core.SshConfig.SshConfigImportResult.NotFound);
        }
    }

    private async Task<EditorPageDouble> OpenDirtyAddAsync(bool keyAuth = false)
    {
        await _world.StartAsync();
        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
        Ui7EditorSessionTests.FillDirect(_world.ViewModel, password: !keyAuth);
        if (!keyAuth)
        {
            _world.Page.TypedPassword = "s3cret";
        }

        return _world.Page;
    }

    private void Exit(string exit)
    {
        switch (exit)
        {
            case "cancel":
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
                _world.Navigation.LeaveCurrentPageThen(_world.Navigation.GoToDashboard);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(exit));
        }
    }

    private sealed class NoWindow : IWindowContext
    {
        public XamlRoot XamlRoot => null!;

        public nint WindowHandle => 0;

        public ElementTheme ActualTheme => ElementTheme.Default;

        public Panel? ModalHost => null;

        public void Attach(Window window, FrameworkElement rootElement, Panel? modalHost = null)
        {
        }
    }
}
