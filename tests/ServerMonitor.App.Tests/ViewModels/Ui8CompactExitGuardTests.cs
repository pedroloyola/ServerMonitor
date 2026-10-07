using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.8 D-UI8-7 / D-UI8-8 / R-8 / Cortex RC-3 (SPEC §4.6). Over the PRODUCTION navigation, editor session and exit guard
/// (the UI.7 world) and the REAL mode coordinator: with an editor left dirty behind Compact (D-UI8-9), a row's "open
/// Detail", the empty state's "Adicionar servidor" and the all-hidden "Gerir servidores ocultos" first bring the window
/// back to Standard, and only THEN run the existing command - so "Descartar alterações?" is asked once, in Standard (its
/// dialog does not fit the Compact window), and the destination opens only after "Descartar". "Manter" stays on the
/// editor, in Standard. Deterministic: scripted prompt, no dispatcher, no window.
/// </summary>
public sealed class Ui8CompactExitGuardTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();
    private readonly WindowModeCoordinator _coordinator;
    private readonly WindowModeViewModel _windowMode;
    private readonly SurfacingController _controller;

    public Ui8CompactExitGuardTests()
    {
        _coordinator = new WindowModeCoordinator(new RecordingPlacementAdapter(), new FakeWindowPlacementStore(), NullLogger<WindowModeCoordinator>.Instance);
        _coordinator.Initialize();
        _controller = new SurfacingController(_coordinator);
        _windowMode = new WindowModeViewModel(_coordinator, _controller, _world.Dashboard);
    }

    public void Dispose()
    {
        _windowMode.Dispose();
        _world.Dispose();
    }

    public static TheoryData<string> Exits => ["detail", "add", "hidden"];

    [Theory]
    [MemberData(nameof(Exits))]
    public async Task WithADirtyEditorBehindCompact_TheGuardAsksOnce_InStandard_AndNavigatesOnlyAfterDiscard(string exit)
    {
        var editor = await DirtyEditorBehindCompactAsync();
        var modeWhenAsked = new List<WindowMode>();
        _world.Prompt.AutoAnswer = null;
        _world.Prompt.OnAsked = () => modeWhenAsked.Add(_coordinator.CurrentMode);

        Run(exit, await ServerIdAsync());

        Assert.Equal([WindowMode.Standard], modeWhenAsked);
        Assert.Same(editor, _world.Host.Content); // nothing navigated while the question is open
        Assert.False(editor.Disposed);

        _world.Prompt.Answer(true);

        Assert.True(editor.Disposed);
        AssertArrivedAt(exit);
        Assert.Equal(WindowMode.Standard, _coordinator.CurrentMode);
        Assert.Equal(1, _controller.Surfaced);
    }

    [Theory]
    [MemberData(nameof(Exits))]
    public async Task KeepEditing_StaysOnTheEditor_InStandard(string exit)
    {
        var editor = await DirtyEditorBehindCompactAsync();
        _world.Prompt.AutoAnswer = false;

        Run(exit, await ServerIdAsync());

        Assert.Single(_world.Prompt.Asked);
        Assert.Same(editor, _world.Host.Content);
        Assert.False(editor.Disposed);
        Assert.Equal(WindowMode.Standard, _coordinator.CurrentMode);
    }

    [Fact]
    public async Task WithoutAnEditor_ARowOpensItsDetail_FromCompact_WithNoQuestion()
    {
        var server = await _world.SeedAsync("web-01");
        _world.Navigation.GoToDashboard();
        _coordinator.SwitchTo(WindowMode.Compact);

        _windowMode.OpenServerDetailCommand.Execute(server.Id);

        Assert.Empty(_world.Prompt.Asked);
        Assert.Equal(server.Id, Assert.IsType<Ui7EditorWorld.DetailPageDouble>(_world.Host.Content).ServerId);
        Assert.Equal(WindowMode.Standard, _coordinator.CurrentMode);
        Assert.False(_windowMode.OpenServerDetailCommand.CanExecute("not-a-guid"));
    }

    private async Task<EditorPageDouble> DirtyEditorBehindCompactAsync()
    {
        await _world.SeedAsync("web-01");
        await _world.StartAsync();
        // Opened through "Importar do SSH" so the dashboard's Add command stays free for the "add" exit.
        _ = ((AsyncRelayCommand)_world.Dashboard.ImportFromSshCommand).ExecuteAsync(); // completes when the visit ends
        Ui7EditorSessionTests.FillDirect(_world.ViewModel, password: true);
        var editor = _world.Page;
        _coordinator.SwitchTo(WindowMode.Compact); // D-UI8-9: the editor stays hidden and intact; nothing saved or dropped
        Assert.Same(editor, _world.Host.Content);
        return editor;
    }

    private async Task<Guid> ServerIdAsync()
    {
        await Task.CompletedTask;
        return _world.Dashboard.VisibleServers.Single().Server.Id;
    }

    private void Run(string exit, Guid serverId)
    {
        switch (exit)
        {
            case "detail": _windowMode.OpenServerDetailCommand.Execute(serverId); break;
            case "add": _windowMode.AddServerCommand.Execute(null); break;
            default: _windowMode.ManageHiddenServersCommand.Execute(null); break;
        }
    }

    private void AssertArrivedAt(string exit)
    {
        switch (exit)
        {
            case "detail":
                Assert.IsType<Ui7EditorWorld.DetailPageDouble>(_world.Host.Content);
                break;
            case "add":
                Assert.IsType<EditorPageDouble>(_world.Host.Content); // a NEW Add editor
                Assert.Equal(2, _world.EditorPages.Count);
                break;
            default:
                // Definições › Dados e servidores, where hidden servers are restored (the existing action).
                Assert.Equal(typeof(SettingsDataPage), Assert.IsType<Ui7EditorWorld.SingletonPageDouble>(_world.Host.Content).PageType);
                break;
        }
    }

    /// <summary>
    /// The controller's Standard surfacing over the real coordinator, with the window already materialized and visible
    /// (the Compact window the user clicks in). Everything else is out of scope here.
    /// </summary>
    private sealed class SurfacingController(IWindowModeCoordinator coordinator) : IApplicationWindowController
    {
        public int Surfaced { get; private set; }

        public bool IsAttached => true;

        public bool IsMaterialized => true;

        public void Attach(Window window) => throw new NotSupportedException();

        public void AttachWindowFactory(Func<Window> factory) => throw new NotSupportedException();

        public void RestoreAndActivate() => throw new NotSupportedException("the Compact exits must surface in Standard");

        public void RestoreAndActivateStandard()
        {
            Surfaced++;
            StandardSurfacing.Run(() => true, coordinator, () => false, () => { });
        }

        public void OpenSettings() => throw new NotSupportedException();

        public void OpenBackgroundSettings() => throw new NotSupportedException();

        public void ToggleCompactMode() => throw new NotSupportedException();

        public void RequestClose() => throw new NotSupportedException();

        public void BeginShutdown() => throw new NotSupportedException();
    }
}
