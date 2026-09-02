using System.Text.RegularExpressions;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// Coverage O (M13 S2 §I.3), closing Atlas's caveat on `8472cf0`.
/// <para>
/// The QA-10 investigation established that the Widgets board is a <c>WS_EX_TOPMOST</c> window, so no
/// amount of z-order work on our side can out-rank it — and the human's closed list forbids trying. But
/// <c>IsAlwaysOnTop</c> cannot simply be banned: Compact mode uses it legitimately, at the user's
/// request. So the property proved here is not "the API is absent" but <b>"navigation and lifecycle paths
/// never MUTATE the topmost state"</b>, with a control test showing Compact still does.
/// </para>
/// </summary>
public sealed class TopmostMutationBoundaryTests
{
    /// <summary>The only file allowed to write the flag: the placement adapter, on the mode path.</summary>
    private const string PlacementAdapter = "AppWindowPlacementAdapter.cs";

    /// <summary>Files that must never mutate it — everything on the activation/lifecycle route.</summary>
    private static readonly string[] LifecycleAndNavigationFiles =
    [
        "ApplicationWindowController.cs",
        "AppLifecycleController.cs",
        "ExitSequence.cs",
        "WindowCloseCoordinator.cs",
        "ActivationDispatch.cs",
        "ProtocolActivationReader.cs",
        "TrayService.cs",
        "WindowsAppNotificationService.cs",
        "BackgroundNoticePresenter.cs",
        "NotificationActivationContract.cs",
        "LaunchModePolicy.cs",
        "Program.cs"
    ];

    [Fact]
    public void No_navigation_or_lifecycle_file_touches_the_topmost_api()
    {
        var offenders = new List<string>();
        foreach (var path in AppSourceFiles())
        {
            var name = Path.GetFileName(path);
            if (!LifecycleAndNavigationFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = StripComments(File.ReadAllText(path));
            if (source.Contains("IsAlwaysOnTop", StringComparison.Ordinal)
                || source.Contains("SetAlwaysOnTop", StringComparison.Ordinal))
            {
                offenders.Add(name);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_placement_adapter_writes_the_topmost_flag()
    {
        var offenders = AppSourceFiles()
            .Where(path => !Path.GetFileName(path).Equals(PlacementAdapter, StringComparison.OrdinalIgnoreCase))
            .Where(path => StripComments(File.ReadAllText(path))
                .Contains("IsAlwaysOnTop", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The behavioural half: driving the window-mode coordinator through Standard never mutates topmost,
    /// while the control below shows Compact does. A source scan alone could be satisfied by a rename.
    /// </summary>
    [Fact]
    public void Standard_mode_never_mutates_the_topmost_state()
    {
        var adapter = new RecordingPlacementAdapter();
        var coordinator = NewCoordinator(adapter, WindowMode.Standard, alwaysOnTop: false);

        coordinator.Initialize();
        coordinator.SwitchTo(WindowMode.Standard);
        coordinator.PersistCurrentBounds();

        Assert.Equal([false], adapter.TopmostMutations); // the single explicit "not on top" at init
        Assert.DoesNotContain(true, adapter.TopmostMutations);
    }

    /// <summary>
    /// CONTROL: the legitimate use must keep working. If this ever fails, the boundary above has been
    /// tightened into a regression instead of a guard.
    /// </summary>
    [Fact]
    public void Compact_mode_still_applies_the_user_requested_always_on_top()
    {
        var adapter = new RecordingPlacementAdapter();
        var coordinator = NewCoordinator(adapter, WindowMode.Compact, alwaysOnTop: true);

        coordinator.Initialize();

        Assert.Contains(true, adapter.TopmostMutations);
    }

    private static WindowModeCoordinator NewCoordinator(
        RecordingPlacementAdapter adapter, WindowMode mode, bool alwaysOnTop) =>
        new(
            adapter,
            new FakeWindowPlacementStore(new WindowPlacementSettings
            {
                Mode = mode,
                CompactAlwaysOnTop = alwaysOnTop
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowModeCoordinator>.Instance);

    private static IEnumerable<string> AppSourceFiles() =>
        Directory.EnumerateFiles(
                Path.Combine(FindRepositoryRoot(), "src", "ServerMonitor.App"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate ServerMonitor.slnx from test output.");
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", string.Empty, RegexOptions.None);
}
