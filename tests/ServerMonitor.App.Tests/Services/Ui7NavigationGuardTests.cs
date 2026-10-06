using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// UI.7 H-UI7-3 on the REAL <see cref="NavigationService"/> (UI.4 seam): ONE exit hook. Every navigation entry point asks
/// the shown page's <see cref="INavigationExitGuard"/> exactly once; a clean answer navigates at once, a refusal stays, a
/// pending question drops other navigations, a broken guard keeps the page. The sidebar keeps the editor under
/// Servidores, and the external activation goes through the same hook.
/// </summary>
public sealed class Ui7NavigationGuardTests
{
    public static TheoryData<string> EntryPoints => new()
    {
        // GoToWorkloads casts to the concrete WorkloadsPage (no seam): covered by EveryNavigationMethod_PassesTheGuard.
        "dashboard", "servers", "history-sidebar", "history-server", "detail", "return-detail",
        "settings", "settings-data", "editor", "leave-then"
    };

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void EveryEntryPoint_AsksTheShownPagesGuard_Once_AndLeavesWhenItAgrees(string entry)
    {
        var world = new GuardWorld();
        var guard = world.ShowGuardedPage(answer: Task.FromResult(true));

        Navigate(world, entry);

        Assert.Equal(1, guard.Asked);
        Assert.NotSame(guard, world.Host.Content);
        Assert.True(guard.Disposed);
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void EveryEntryPoint_StaysWhenTheGuardRefuses(string entry)
    {
        var world = new GuardWorld();
        var guard = world.ShowGuardedPage(answer: Task.FromResult(false));

        Navigate(world, entry);

        Assert.Equal(1, guard.Asked);
        Assert.Same(guard, world.Host.Content);
        Assert.False(guard.Disposed);
        Assert.Equal(NavigationDestination.ServerEditor, world.Navigation.CurrentDestination);
    }

    /// <summary>The one hook covers every public navigation method (incl. those the seam cannot instantiate).</summary>
    [Theory]
    [InlineData("public void NavigateTo<TPage>()")]
    [InlineData("public void GoToSettings(SettingsSection section)")]
    [InlineData("public void GoToHistory(Guid serverId, string serverName)")]
    [InlineData("public void GoToWorkloads(Guid serverId, string serverName)")]
    [InlineData("public void GoToServers()")]
    [InlineData("public void GoToServerDetail(Guid serverId, ServerDetailOrigin origin)")]
    [InlineData("public void GoToHistory()")]
    [InlineData("public void GoToServerEditor(ServerEditorRequest request, Action? refused = null)")]
    [InlineData("public void LeaveCurrentPageThen(Action continuation)")]
    public void EveryNavigationMethod_PassesTheGuard_BeforeShowingAnything(string signature)
    {
        var code = AppSourceTree.CodeWithoutComments("Services/NavigationService.cs").Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = code[start..end];
        var guard = body.IndexOf("DeferToExitGuard(", StringComparison.Ordinal);
        Assert.True(guard > 0, $"{signature} does not ask the exit guard");
        var show = body.IndexOf("Show(", StringComparison.Ordinal);
        Assert.True(show < 0 || show > guard, $"{signature} shows a page before asking the exit guard");
    }

    [Fact]
    public void WithoutAGuard_NavigationIsUnchanged_AndNeverAsks()
    {
        var world = new GuardWorld();
        world.Navigation.GoToServers();
        world.Navigation.GoToDashboard();
        Assert.Equal(NavigationDestination.Overview, world.Navigation.CurrentDestination);
    }

    [Fact]
    public void APendingQuestion_DropsEveryOtherNavigation_ThenOnlyTheAskedOneRuns()
    {
        var world = new GuardWorld();
        var answer = new TaskCompletionSource<bool>();
        var guard = world.ShowGuardedPage(answer.Task);
        var refused = 0;

        world.Navigation.GoToServers();
        world.Navigation.GoToDashboard();
        world.Navigation.GoToServerEditor(GuardWorld.Request(), () => refused++);
        world.Navigation.LeaveCurrentPageThen(() => throw new InvalidOperationException("must not run"));
        Assert.Equal(1, guard.Asked);
        Assert.Equal(1, refused);
        Assert.Same(guard, world.Host.Content);

        answer.SetResult(true);

        Assert.Equal(NavigationDestination.Servers, world.Navigation.CurrentDestination);
        Assert.True(guard.Disposed);
    }

    [Fact]
    public void AGuardThatThrows_KeepsThePage()
    {
        var world = new GuardWorld();
        var guard = world.ShowGuardedPage(Task.FromException<bool>(new InvalidOperationException("synthetic")));
        var refused = 0;

        world.Navigation.GoToServerEditor(GuardWorld.Request(), () => refused++);

        Assert.Same(guard, world.Host.Content);
        Assert.Equal(1, refused);
    }

    [Fact]
    public void TheServerEditor_IsUnderServidoresInTheSidebar()
    {
        var navigation = new Fakes.FakeNavigationService { CurrentDestination = NavigationDestination.ServerEditor };
        using var shell = new ShellViewModel(navigation);

        Assert.Equal(ShellDestination.Servers, shell.SelectedDestination);
        Assert.True(shell.IsServersSelected);
    }

    [Fact]
    public void TheExternalActivation_LeavesThroughTheSameGuard_BeforeTheDashboard()
    {
        var code = AppSourceTree.CodeWithoutComments("App.xaml.cs");
        var start = code.IndexOf("private void ExecuteActivationIntent", StringComparison.Ordinal);
        var body = code[start..code.IndexOf("public static IHost ServicesHost", start, StringComparison.Ordinal)];

        // UI.7A fix c1 (m-2): the activation variant of the same guarded exit (latest-wins while the question is open).
        var leave = body.IndexOf("navigation.LeaveCurrentPageForActivation(", StringComparison.Ordinal);
        Assert.True(leave > 0, "the activation does not leave through the exit guard");
        Assert.True(body.IndexOf("navigation.GoToDashboard()", StringComparison.Ordinal) > leave);
        Assert.True(body.IndexOf("dashboard.FocusServer(serverId)", StringComparison.Ordinal) > leave);
        Assert.Equal(1, CountOf(body, "GoToDashboard()"));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static void Navigate(GuardWorld world, string entry)
    {
        var navigation = world.Navigation;
        var serverId = world.ServerId;
        switch (entry)
        {
            case "dashboard": navigation.GoToDashboard(); break;
            case "servers": navigation.GoToServers(); break;
            case "history-sidebar": navigation.GoToHistory(); break;
            case "history-server": navigation.GoToHistory(serverId, "web"); break;
            case "workloads": navigation.GoToWorkloads(serverId, "web"); break;
            case "detail": navigation.GoToServerDetail(serverId, ServerDetailOrigin.Overview); break;
            case "return-detail": navigation.ReturnToServerDetail(serverId); break;
            case "settings": navigation.GoToSettings(); break;
            case "settings-data": navigation.GoToSettings(SettingsSection.Data); break;
            case "editor": navigation.GoToServerEditor(GuardWorld.Request()); break;
            case "leave-then": navigation.LeaveCurrentPageThen(navigation.GoToDashboard); break;
            default: throw new ArgumentOutOfRangeException(nameof(entry));
        }
    }

    private sealed class GuardWorld
    {
        private GuardedPage? _next;

        public GuardWorld()
        {
            var kit = ViewModels.Ui4TestKit.Create(new ViewModels.Ui4TestKit.Fleet().Add("web", Core.Enums.ServerHealth.Healthy, 1, 2, 3));
            kit.Dashboard.LoadAsync().GetAwaiter().GetResult();
            ServerId = kit.Dashboard.VisibleServers.Single().Server.Id;
            Navigation = new NavigationService(new Provider(kit.Dashboard), NullLogger<NavigationService>.Instance, CreatePage);
            Navigation.Initialize(Host);
        }

        public Guid ServerId { get; }

        public NavigationService Navigation { get; }

        public RecordingHost Host { get; } = new();

        public static ServerEditorRequest Request() => new(ServerEditorMode.Add, null, null, false, ServerEditorOrigin.Overview);

        /// <summary>Shows an editor page whose guard answers <paramref name="answer"/>; later editor pages are fresh.</summary>
        public GuardedPage ShowGuardedPage(Task<bool> answer)
        {
            _next = new GuardedPage(answer);
            Navigation.GoToServerEditor(Request());
            _next = null;
            return Assert.IsType<GuardedPage>(Host.Content);
        }

        private object CreatePage(Type type) => type == typeof(ServerEditorPage)
            ? _next ?? new GuardedPage(Task.FromResult(true))
            : type == typeof(ServerDetailPage) ? new DetailPage()
            : type == typeof(HistoryPage) ? new HistoryView()
            : new PlainPage();

        private sealed class Provider(ServerMonitor.App.ViewModels.DashboardViewModel dashboard) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(ServerMonitor.App.ViewModels.DashboardViewModel) ? dashboard : null;
        }

        internal sealed class RecordingHost : INavigationHost
        {
            public object? Content { get; set; }
        }

        private sealed class PlainPage : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private sealed class DetailPage : IServerDetailView, IDisposable
        {
            public void Load(Guid serverId, ServerDetailOrigin origin)
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class HistoryView : IHistoryView, IDisposable
        {
            public void Load(Guid? serverId, string serverName, bool fromDetail)
            {
            }

            public void LoadSidebar(Guid? lastDetailServer)
            {
            }

            public void Dispose()
            {
            }
        }
    }

    internal sealed class GuardedPage(Task<bool> answer) : INavigationExitGuard, IServerEditorView, IDisposable
    {
        public int Asked { get; private set; }

        public bool Disposed { get; private set; }

        public Task<bool> ConfirmLeaveAsync()
        {
            Asked++;
            return answer;
        }

        public void Load(ServerEditorRequest request)
        {
        }

        public void Dispose() => Disposed = true;
    }
}
