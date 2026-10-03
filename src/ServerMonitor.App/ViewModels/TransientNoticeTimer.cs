using Microsoft.UI.Dispatching;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.5 fix round 2 (Boss decision 2, Beacon C1 M5, Cortex C1 N-C2): the auto-dismiss of a transient notice (the
/// Servidores return notice and the Dados success toast). One fixed <see cref="Duration"/>, measured on an injected
/// <see cref="TimeProvider"/> (FakeTimeProvider in tests - no wall clock), restarted by each new notice and cancelled when
/// the notice is closed or its page is left. The elapsed callback runs on the UI thread that started it (inline when
/// there is no WinUI dispatcher, as in unit tests). A superseded timer never closes a newer notice (generation check).
/// </summary>
public sealed class TransientNoticeTimer : IDisposable
{
    /// <summary>
    /// DERIVED (no Figma rule): long enough to read a two-line toast and hear it announced, short enough not to linger.
    /// The notice also has its own close button.
    /// </summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(8);

    private readonly TimeProvider _timeProvider;
    private readonly DispatcherQueue? _dispatcherQueue = TryGetDispatcher();
    private ITimer? _timer;
    private int _generation;

    public TransientNoticeTimer(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>True while a notice is counting down.</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>(Re)starts the countdown; <paramref name="onElapsed"/> runs once, unless cancelled or restarted first.</summary>
    public void Start(Action onElapsed)
    {
        ArgumentNullException.ThrowIfNull(onElapsed);
        Cancel();
        var generation = _generation;
        _timer = _timeProvider.CreateTimer(_ => Elapse(generation, onElapsed), null, Duration, Timeout.InfiniteTimeSpan);
    }

    public void Cancel()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Cancel();

    private void Elapse(int generation, Action onElapsed)
    {
        void Run()
        {
            if (generation != _generation)
            {
                return;
            }

            _timer?.Dispose();
            _timer = null;
            onElapsed();
        }

        if (_dispatcherQueue is null || !_dispatcherQueue.TryEnqueue(Run))
        {
            Run();
        }
    }

    private static DispatcherQueue? TryGetDispatcher()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
