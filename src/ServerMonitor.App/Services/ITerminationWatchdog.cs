using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// The last resort that makes "no zombie" unconditional (M13 S2 §F.3).
/// <para>
/// <b>It is PROCESS lifetime infrastructure, not a host service.</b> The first implementation was a
/// container-created singleton inside the very <c>IHost</c> that the exit stops and disposes, so
/// <c>host.Dispose()</c> disposed the watchdog and, if <c>Application.Exit()</c> then failed to end the
/// process, no escalation was left — an indefinite zombie still holding the AppInstance key, which is the
/// exact failure the watchdog exists to prevent. It is now created by <c>Program</c> before the host
/// exists, it does NOT implement <see cref="IDisposable"/> so no container can dispose it, and it has no
/// disarm at all: nothing short of the process ending makes it inert.
/// </para>
/// </summary>
public interface ITerminationWatchdog
{
    /// <summary>True once armed. Arming is one-way.</summary>
    bool IsArmed { get; }

    /// <summary>
    /// Arms the deadline. Monotonic and NOT restartable: the first call wins and every later call is
    /// ignored, so nothing can push the deadline out. There is deliberately no way to cancel it — a
    /// shutdown that completed normally ends the process, and the process ending is what ends this.
    /// </summary>
    void Arm(TimeSpan deadline, Action onDeadlineReached);
}

/// <summary>
/// How the watchdog waits. The only seam: it must not be the thread pool or the dispatcher, because a
/// wedged shutdown can starve both — and it must be replaceable in tests so the PRODUCTION state machine
/// can be exercised on deterministic time (M13 S2; test-integrity rule §10 of BOSS.md).
/// </summary>
public interface IWatchdogScheduler
{
    /// <summary>Invokes <paramref name="callback"/> once, <paramref name="delay"/> from now.</summary>
    void ScheduleOnce(TimeSpan delay, Action callback);
}

/// <summary>
/// Production scheduler: one dedicated thread that waits on a monotonic timeout.
/// <para>
/// A dedicated thread, never a <see cref="TimeProvider"/>/<c>Timer</c> callback, because those run on the
/// thread pool — one of the things a wedged shutdown starves. The thread is
/// <see cref="Thread.IsBackground"/>, so it can never by itself keep the process alive, and the wait is
/// measured with <see cref="Stopwatch"/> so a wall-clock change cannot move the deadline.
/// </para>
/// </summary>
public sealed class DedicatedThreadWatchdogScheduler : IWatchdogScheduler
{
    public void ScheduleOnce(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var thread = new Thread(() =>
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                var remaining = delay - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                // Sleep in bounded slices so a suspended/resumed machine cannot overshoot silently.
                Thread.Sleep(remaining > TimeSpan.FromMilliseconds(250)
                    ? TimeSpan.FromMilliseconds(250)
                    : remaining);
            }

            callback();
        })
        {
            IsBackground = true,
            Name = "ServerAlyzer termination watchdog",
            Priority = ThreadPriority.AboveNormal
        };

        thread.Start();
    }
}

/// <summary>
/// The watchdog state machine: armed once, never restarted, never cancelled.
/// <para>
/// Owned by the process (see <c>Program</c>), never by the <c>IHost</c>. It has no <c>Dispose</c> and no
/// <c>Disarm</c> on purpose: the only state that proves termination is inevitable is termination itself,
/// and anything else would reopen the pre-termination gap this class exists to close.
/// </para>
/// </summary>
public sealed class TerminationWatchdog(IWatchdogScheduler scheduler, ILogger<TerminationWatchdog> logger)
    : ITerminationWatchdog
{
    private readonly IWatchdogScheduler _scheduler =
        scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    private int _armed;

    public bool IsArmed => Volatile.Read(ref _armed) == 1;

    public void Arm(TimeSpan deadline, Action onDeadlineReached)
    {
        ArgumentNullException.ThrowIfNull(onDeadlineReached);
        if (deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        if (Interlocked.Exchange(ref _armed, 1) != 0)
        {
            return; // monotonic and non-restartable: the first deadline is the deadline
        }

        _scheduler.ScheduleOnce(deadline, () =>
        {
            try
            {
                logger.LogWarning(
                    "Shutdown exceeded the {Deadline} termination deadline; terminating the process.",
                    deadline);
                onDeadlineReached();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The termination watchdog failed.");
            }
        });
    }
}

/// <summary>Terminates this process. Separated so the watchdog path can be tested without dying.</summary>
public interface IProcessTerminator
{
    void Terminate(int exitCode);
}

/// <summary>
/// Real terminator: <c>TerminateProcess</c> on the current process with a non-zero exit code.
/// <para>
/// Deliberately the bluntest primitive available. It cannot itself block (unlike
/// <see cref="Environment.Exit"/>, which runs finalizers and process-exit handlers that a wedged
/// shutdown may be stuck inside), and unlike <see cref="Environment.FailFast"/> it writes NO Windows
/// Error Reporting report — M13-QA-7 requires the product to produce no WER artefact.
/// </para>
/// It is safe here because the snapshot writer commits atomically (temp + <c>File.Replace</c>) and the
/// host-key store writes through an exact temp path that startup cleans up, so an abrupt end cannot leave
/// a torn file — only, at worst, one known temporary (see <see cref="OrphanTemporaryCleaner"/>).
/// </summary>
public sealed class ProcessTerminator : IProcessTerminator
{
    /// <summary>Distinct, non-zero, and not a common Windows error code, so it is recognizable in logs.</summary>
    public const int WatchdogExitCode = 0x5352;

    public void Terminate(int exitCode) => TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode));

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
