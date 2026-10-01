using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (UI.3 gate 1A, PERF-UI2-DICT). Startup milestones of a Debug harness launch as user-mode ETW events, for
/// <c>tools/perf/startup-perf.ps1</c>. Every event carries the milliseconds since the process was created, so a
/// listener needs no kernel session. Silent outside a harness launch (and EventSource writes nothing without a
/// listener). Excluded from Release (see ServerMonitor.App.csproj).
/// <list type="number">
/// <item><c>ResourcesLoaded</c>: <c>App.InitializeComponent</c> returned - App.xaml and every merged dictionary are
/// parsed; also carries the duration of that call alone.</item>
/// <item><c>ShellActivated</c>: <c>MainWindow.Activate()</c> returned.</item>
/// <item><c>ContentLoaded</c>: the window's root content raised <c>Loaded</c> (first layout done).</item>
/// <item><c>FirstFrame</c>: the first <c>CompositionTarget.Rendering</c> after <c>ContentLoaded</c> - the first frame
/// that carries the shell content.</item>
/// </list>
/// </summary>
internal static class QaStartupMarker
{
    private static readonly bool Enabled = QaStartupIsolation.IsHarnessLaunch();
    private static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static void ResourcesLoaded(long initializeStartedTimestamp)
    {
        if (Enabled)
        {
            QaStartupEventSource.Log.ResourcesLoaded(
                SinceProcessStart(), Stopwatch.GetElapsedTime(initializeStartedTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>Called right before <c>Activate()</c>, so the content's Loaded cannot be missed.</summary>
    public static void Arm(Window window)
    {
        if (!Enabled || window.Content is not FrameworkElement root)
        {
            return;
        }

        root.Loaded += OnContentLoaded;

        void OnContentLoaded(object sender, RoutedEventArgs e)
        {
            root.Loaded -= OnContentLoaded;
            QaStartupEventSource.Log.ContentLoaded(SinceProcessStart());
            CompositionTarget.Rendering += OnFirstRendering;
        }

        static void OnFirstRendering(object? sender, object e)
        {
            CompositionTarget.Rendering -= OnFirstRendering;
            QaStartupEventSource.Log.FirstFrame(SinceProcessStart());
        }
    }

    public static void ShellActivated()
    {
        if (Enabled)
        {
            QaStartupEventSource.Log.ShellActivated(SinceProcessStart());
        }
    }

    private static double SinceProcessStart() => (DateTime.UtcNow - ProcessStartUtc).TotalMilliseconds;
}

/// <summary>The provider <c>tools/perf/startup-perf.ps1</c> enables by this fixed GUID.</summary>
[EventSource(Name = "ServerMonitor-QaStartup", Guid = "6b0f3d52-8a1e-4c55-9d0e-3f7a2c41b9e1")]
internal sealed class QaStartupEventSource : EventSource
{
    public static readonly QaStartupEventSource Log = new();

    private QaStartupEventSource()
        : base(EventSourceSettings.EtwSelfDescribingEventFormat)
    {
    }

    [Event(1, Level = EventLevel.Informational)]
    public void ResourcesLoaded(double msSinceProcessStart, double initializeComponentMs) =>
        WriteEvent(1, msSinceProcessStart, initializeComponentMs);

    [Event(2, Level = EventLevel.Informational)]
    public void ShellActivated(double msSinceProcessStart) => WriteEvent(2, msSinceProcessStart);

    [Event(3, Level = EventLevel.Informational)]
    public void ContentLoaded(double msSinceProcessStart) => WriteEvent(3, msSinceProcessStart);

    [Event(4, Level = EventLevel.Informational)]
    public void FirstFrame(double msSinceProcessStart) => WriteEvent(4, msSinceProcessStart);
}
