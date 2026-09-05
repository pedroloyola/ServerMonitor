using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// Records lifecycle transitions and exit requests without doing any of them. Every collaborator that
/// used to imply shutdown now asks this, so a test can assert exactly who asked and how often.
/// </summary>
internal sealed class FakeAppLifecycleController : IAppLifecycleController
{
    private AppLifecycleState _state;

    public FakeAppLifecycleController(
        AppLifecycleState initialState = AppLifecycleState.Foreground,
        bool startedInBackground = false)
    {
        _state = initialState;
        StartedInBackground = startedInBackground;
    }

    public AppLifecycleState State => _state;

    public bool StartedInBackground { get; }

    public bool IsExiting => _state == AppLifecycleState.Exiting;

    public int ExitRequests { get; private set; }

    public List<ExitReason> ExitReasons { get; } = new();

    public int ForegroundTransitions { get; private set; }

    public int BackgroundTransitions { get; private set; }

    public void EnterForeground()
    {
        if (IsExiting)
        {
            return;
        }

        ForegroundTransitions++;
        _state = AppLifecycleState.Foreground;
    }

    public void EnterBackground()
    {
        if (IsExiting)
        {
            return;
        }

        BackgroundTransitions++;
        _state = AppLifecycleState.Background;
    }

    public void RequestExit(ExitReason reason)
    {
        ExitRequests++;
        ExitReasons.Add(reason);
        _state = AppLifecycleState.Exiting;
    }
}
