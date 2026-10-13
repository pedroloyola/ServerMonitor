using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.12 B12-04 (Beacon, P3). After a SUCCESSFUL test the action bar still said "Testa a ligação antes de guardar." (the
/// controller had no "tested" branch). Over the production controller and view model (UI.7 world, scripted SSH): a
/// successful test → "ServerEditorHintTested"; any edit of a connection field invalidates it → back to "test before saving";
/// a failed test never says "tested".
/// </summary>
public sealed class Ui12EditorTestedHintTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task ASuccessfulTest_SaysTested_UntilAConnectionFieldChanges()
    {
        await OpenAddAsync();
        FillValid(_world.ViewModel);
        Assert.Equal("ServerEditorHintTestBeforeSave", _world.Page.Controller.ActionHintKey());

        await _world.Page.TestAsync();

        Assert.Equal(ServerConnectionState.Connected, _world.ViewModel.LastTestState);
        Assert.Equal("ServerEditorHintTested", _world.Page.Controller.ActionHintKey());

        _world.ViewModel.Host = "10.0.0.10";

        Assert.Null(_world.ViewModel.LastTestState);
        Assert.Equal("ServerEditorHintTestBeforeSave", _world.Page.Controller.ActionHintKey());
    }

    [Fact]
    public async Task AFailedTest_NeverSaysTested()
    {
        await OpenAddAsync();
        FillValid(_world.ViewModel);
        _world.Ssh.Result = TestData.Connected() with { State = ServerConnectionState.Error };

        await _world.Page.TestAsync();

        Assert.NotEqual("ServerEditorHintTested", _world.Page.Controller.ActionHintKey());
    }

    [Fact]
    public void TheTestedHint_ExistsInEveryLanguage()
    {
        foreach (var culture in Fakes.ResWLocalizationService.Cultures)
        {
            var text = new Fakes.ResWLocalizationService(culture).GetString("ServerEditorHintTested");
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual("ServerEditorHintTested", text);
        }
    }

    private async Task OpenAddAsync()
    {
        await _world.StartAsync();
        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
    }

    private static void FillValid(ServerEditorViewModel viewModel)
    {
        viewModel.Name = "db";
        viewModel.Host = "10.0.0.9";
        viewModel.Username = "ops";
        viewModel.PrivateKeyPath = @"C:\keys\id_ed25519";
    }
}
