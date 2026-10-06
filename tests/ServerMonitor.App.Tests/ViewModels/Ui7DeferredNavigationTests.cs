using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7A fix round c1 over the production navigation, session and controller: a deferred navigation that throws still
/// ends its editor visit and releases the opener (Cortex m-1); an external activation that arrives while "Descartar
/// alterações?" is open is kept latest-wins and runs only if the user discards (Cortex m-2, Boss decision DERIVED).
/// </summary>
public sealed class Ui7DeferredNavigationTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task M1c_ADeferredEditorOpenThatThrows_EndsItsVisit_AndReleasesTheOpenersCommand()
    {
        var first = await OpenDirtyAddAsync();
        _world.Prompt.AutoAnswer = null;
        var import = (AsyncRelayCommand)_world.Dashboard.ImportFromSshCommand;

        var second = import.ExecuteAsync(); // another editor: the guard asks first (deferred)
        Assert.False(import.CanExecute(null));
        _world.ThrowOnNextEditorPage = true;
        _world.Prompt.Answer(true); // "Descartar" → the deferred GoToServerEditor runs and its page factory throws

        Assert.Equal(1, _world.Session.OpenVisits); // the second visit ended at once (only the first editor's remains)
        await second; // the visit of the second editor ended: its opener resumes
        Assert.True(import.CanExecute(null));
        Assert.Equal(1, _world.Session.CreatedViewModels); // the second editor never got a view model
        Assert.False(first.Disposed); // the failed navigation replaced nothing
    }

    [Fact]
    public async Task M2_ActivationsDuringTheQuestion_TheLatestRuns_OnlyIfTheUserDiscards()
    {
        var page = await OpenDirtyAddAsync();
        _world.Prompt.AutoAnswer = null;
        var ran = new List<string>();
        page.Controller.Cancel(); // "Descartar alterações?" is open

        _world.Navigation.LeaveCurrentPageForActivation(() => { ran.Add("toast"); _world.Navigation.GoToDashboard(); });
        _world.Navigation.LeaveCurrentPageForActivation(() => { ran.Add("widget"); _world.Navigation.GoToServers(); });
        _world.Navigation.GoToHistory(); // a click while the question is modal stays dropped
        Assert.Single(_world.Prompt.Asked);
        Assert.Empty(ran);

        _world.Prompt.Answer(true);

        Assert.Equal(["widget"], ran); // latest-wins, instead of the Cancel's own return
        Assert.True(page.Disposed);
        Assert.Equal(typeof(ServersPage), Assert.IsType<Ui7EditorWorld.PerVisitPageDouble>(_world.Host.Content).PageType);
    }

    [Fact]
    public async Task M2_KeepEditing_DropsThePendingActivation()
    {
        var page = await OpenDirtyAddAsync();
        _world.Prompt.AutoAnswer = null;
        var ran = new List<string>();
        page.Controller.Cancel();
        _world.Navigation.LeaveCurrentPageForActivation(() => { ran.Add("toast"); _world.Navigation.GoToDashboard(); });

        _world.Prompt.Answer(false);

        Assert.Empty(ran);
        Assert.Same(page, _world.Host.Content);
        Assert.False(page.Disposed);

        // Nothing was left behind: the next activation is asked about afresh.
        _world.Prompt.AutoAnswer = true;
        _world.Navigation.LeaveCurrentPageForActivation(() => { ran.Add("later"); _world.Navigation.GoToDashboard(); });
        Assert.Equal(["later"], ran);
    }

    [Fact]
    public async Task M2_WithNoQuestionOpen_AnActivationIsTheOrdinaryGuardedExit()
    {
        var page = await OpenDirtyAddAsync();
        var ran = new List<string>();

        _world.Navigation.LeaveCurrentPageForActivation(() => { ran.Add("toast"); _world.Navigation.GoToDashboard(); });

        Assert.Single(_world.Prompt.Asked); // asked once, answered "discard" by the scripted prompt
        Assert.Equal(["toast"], ran);
        Assert.True(page.Disposed);
    }

    private async Task<EditorPageDouble> OpenDirtyAddAsync()
    {
        await _world.StartAsync();
        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
        Ui7EditorSessionTests.FillDirect(_world.ViewModel, password: true);
        return _world.Page;
    }
}
