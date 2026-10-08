using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.9 D-UI9-6 runtime caller (BOSS §10: "is there a runtime caller, proven by test?"). The recorder's
/// fleet-change subscription and startup write only exist once <see cref="WidgetSnapshotRecorder.Start"/>
/// runs; the recorder unit tests call it themselves, so without this guard deleting the one production call
/// would leave every test green while the deleted-server name persisted again. App.xaml.cs is WinUI and
/// cannot be composed in a unit test, so the guard pins the call on the code (comments stripped) and its
/// ORDER: after the host started (the engine and stores are up), before the window is shown.
/// </summary>
public sealed class WidgetSnapshotStartupWiringTests
{
    private const string HostStarted = "await ServicesHost.StartAsync();";
    private const string RecorderStarted = "ServicesHost.Services.GetService<WidgetSnapshotRecorder>()?.Start();";
    private const string WindowShown = "_mainWindow!.Activate();";

    [Fact]
    public void App_starts_the_widget_recorder_once_after_the_host_starts()
    {
        var code = AppSourceTree.CodeWithoutComments("App.xaml.cs");

        var host = code.IndexOf(HostStarted, StringComparison.Ordinal);
        var recorder = code.IndexOf(RecorderStarted, StringComparison.Ordinal);
        var window = code.IndexOf(WindowShown, StringComparison.Ordinal);

        Assert.True(host >= 0, $"App.xaml.cs no longer contains '{HostStarted}'");
        Assert.True(recorder >= 0, "App.xaml.cs no longer starts the widget recorder (D-UI9-6)");
        Assert.True(window >= 0, $"App.xaml.cs no longer contains '{WindowShown}'");
        Assert.True(host < recorder && recorder < window, "the recorder must start after the host and before the window");
        Assert.Equal(recorder, code.LastIndexOf(RecorderStarted, StringComparison.Ordinal)); // exactly once
    }
}
