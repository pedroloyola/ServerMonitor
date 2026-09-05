using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// Stops the monitoring host, once, under a bound. It is a STEP of the authoritative exit owned by
/// <see cref="AppLifecycleController"/> — never a shutdown decision of its own (M13 S2 §C).
/// <para>
/// Two things changed in S2, both from the returned review:
/// </para>
/// <para>
/// <b>It no longer releases the single-instance key.</b> It used to call
/// <c>Program.ReleaseSingleInstanceKey()</c> BEFORE <c>StopAsync</c>, which left up to a full drain in
/// which this process was alive and unowned: a launch in that window became primary and started a second
/// monitoring host writing the same snapshot. Ownership now ends only when the process ends (§F.2).
/// </para>
/// <para>
/// <b>Disposal is bounded by construction.</b> <c>host.Dispose()</c> is synchronous and unbounded, so a
/// wedged disposal used to be able to hold the process in a dying state forever — the zombie by another
/// route. It now runs ONLY when the stop genuinely completed, never after a timeout, and even then on a
/// background thread that is never awaited: the services are already drained, so anything still held is
/// reclaimed by the OS at termination anyway.
/// </para>
/// </summary>
public sealed class AppShutdownCoordinator
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<IHost> _hostFactory;
    private readonly ILogger<AppShutdownCoordinator> _logger;
    private readonly TimeSpan _timeout;
    private int _shutdownRequested;
    private int _stopCompleted;

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

    /// <summary>
    /// Stops the app host once, waiting no longer than the configured bound.
    /// </summary>
    /// <returns>
    /// True when the host actually stopped inside the bound. False on timeout or failure — and the
    /// caller must not then wait for anything else: the exit continues regardless.
    /// </returns>
    public bool Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return Volatile.Read(ref _stopCompleted) != 0;
        }

        var host = _hostFactory();
        var cancellation = new CancellationTokenSource();
        var stopped = false;
        try
        {
            // Running the host stop pipeline on the thread pool prevents any continuation from
            // waiting for the UI thread that requested the exit.
            var stopTask = Task.Run(
                () => host.StopAsync(cancellation.Token),
                CancellationToken.None);
            try
            {
                stopTask.WaitAsync(_timeout).GetAwaiter().GetResult();
                stopped = true;
            }
            catch (TimeoutException)
            {
                cancellation.Cancel();
                // Deliberately NOT disposing here, now or later: a stop that did not finish means
                // hosted services are still running, and host.Dispose() is synchronous and unbounded.
                // Observe the late result so it never becomes an unobserved task exception.
                ObserveLateStop(stopTask);
                _logger.LogWarning(
                    "Application host shutdown exceeded the {Timeout} bound; skipping disposal.",
                    _timeout);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Application host shutdown failed.");
        }
        finally
        {
            cancellation.Dispose();
            if (stopped)
            {
                Volatile.Write(ref _stopCompleted, 1);
                DisposeHostOffCriticalPath(host);
            }
        }

        return stopped;
    }

    private void ObserveLateStop(Task stopTask) =>
        _ = stopTask.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    _logger.LogError(
                        completed.Exception,
                        "Application host shutdown failed after the stop timeout.");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

    /// <summary>
    /// Disposal runs on a background thread-pool thread and is NEVER awaited. The stop has already
    /// drained the hosted services, so this only releases handles that process termination would release
    /// anyway — it must not be able to delay the exit (M13 S2 §F.3).
    /// </summary>
    private void DisposeHostOffCriticalPath(IHost host) =>
        _ = Task.Run(
            () =>
            {
                try
                {
                    host.Dispose();
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Application host disposal failed.");
                }
            },
            CancellationToken.None);
}
