using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// Performs the one process-level host shutdown initiated by the main window. The synchronous
/// boundary is intentional: WinUI's Closed event is synchronous, and returning from an async-void
/// handler could let the process terminate before hosted services release schedulers and sockets.
/// Host shutdown itself runs without the UI synchronization context and is bounded.
/// </summary>
public sealed class AppShutdownCoordinator
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<IHost> _hostFactory;
    private readonly ILogger<AppShutdownCoordinator> _logger;
    private readonly TimeSpan _timeout;
    private int _shutdownRequested;

    internal AppShutdownCoordinator(
        Func<IHost> hostFactory,
        ILogger<AppShutdownCoordinator> logger,
        TimeSpan? timeout = null)
    {
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    /// <summary>Stops and disposes the app host once, waiting no longer than the configured bound.</summary>
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return;
        }

        // Release the single-instance key first so a launch that races this teardown becomes the
        // new primary rather than redirecting into a process that is exiting (Atlas reliability
        // review). No-op unless a key was actually registered (never in tests).
        Program.ReleaseSingleInstanceKey();

        var host = _hostFactory();
        var cancellation = new CancellationTokenSource();
        var disposalDeferred = false;
        try
        {
            // Running the host stop pipeline on the thread pool prevents any continuation from
            // waiting for the UI thread that is synchronously handling Window.Closed.
            var stopTask = Task.Run(
                () => host.StopAsync(cancellation.Token),
                CancellationToken.None);
            try
            {
                stopTask.WaitAsync(_timeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                cancellation.Cancel();
                disposalDeferred = true;
                DeferDisposalUntilStopCompletes(stopTask, host, cancellation);
                _logger.LogWarning("Application host shutdown exceeded the {Timeout} bound.", _timeout);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Application host shutdown failed; disposing remaining services.");
        }
        finally
        {
            if (!disposalDeferred)
            {
                cancellation.Dispose();
                DisposeHost(host);
            }
        }
    }

    private void DeferDisposalUntilStopCompletes(
        Task stopTask,
        IHost host,
        CancellationTokenSource cancellation)
    {
        _ = stopTask.ContinueWith(
            completed =>
            {
                try
                {
                    // Accessing Exception observes a late stop failure so it cannot become an
                    // unobserved task exception after the bounded Window.Closed path returns.
                    if (completed.IsFaulted)
                    {
                        _logger.LogError(
                            completed.Exception,
                            "Application host shutdown failed after the close timeout.");
                    }
                }
                finally
                {
                    cancellation.Dispose();
                    DisposeHost(host);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void DisposeHost(IHost host)
    {
        try
        {
            host.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Application host disposal failed.");
        }
    }
}
