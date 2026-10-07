using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.ViewModels;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.7 B-23 runtime-smoke regression (Boss): <c>--qa-start=editor-edit:&lt;n&gt;</c> must land on the editor of the seeded
/// server - written by the start step itself, then opened from its Detail page - and nowhere else; <c>editor-add</c> on
/// the Add editor. Over the production navigation, session, dashboard and profile path (<see cref="Ui7EditorWorld"/>).
/// </summary>
public sealed class QaEditorStartTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("direct")]
    [InlineData("routed")]
    [InlineData("password")]
    public async Task EditorEdit_LandsOnTheEditorOfTheSeededServer(string seed)
    {
        await QaShellStartup.StartEditorAsync(
            "editor-edit:1", _world.Navigation, _world.Dashboard, new QaEditorSeed(seed, _world.Profiles, _world.Directory));

        Assert.Equal(NavigationDestination.ServerEditor, _world.Navigation.CurrentDestination);
        var request = Assert.IsType<EditorPageDouble>(_world.Host.Content).Controller.Request!;
        Assert.Equal(ServerEditorMode.Edit, request.Mode);
        Assert.Equal((await _world.ServerService.GetAllAsync()).Single().Id, request.Existing!.Id);
        Assert.Equal(NavigationDestination.Detail, request.Origin.Destination);
    }

    [Fact]
    public async Task EditorAdd_LandsOnTheAddEditor_OverTheVisaoGeral()
    {
        await QaShellStartup.StartEditorAsync("editor-add", _world.Navigation, _world.Dashboard, new QaEditorSeed(null, _world.Profiles, _world.Directory));

        Assert.Equal(NavigationDestination.ServerEditor, _world.Navigation.CurrentDestination);
        var request = Assert.IsType<EditorPageDouble>(_world.Host.Content).Controller.Request!;
        Assert.Equal(ServerEditorMode.Add, request.Mode);
        Assert.Equal(NavigationDestination.Overview, request.Origin.Destination);
        Assert.Empty(await _world.ServerService.GetAllAsync());
    }
}
