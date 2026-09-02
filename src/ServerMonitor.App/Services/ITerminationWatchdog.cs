using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// The last resort that makes "no zombie" unconditional (M13 S2 §F.3). Armed once, at the moment the
/// process commits to exiting, it terminates the process if the ordered shutdown has not managed to end
/// it within the global deadline.
/// <para>
/// It must be <b>independent of the dispatcher and of the thread pool being drained</b>: a stuck host or
/// a saturated pool is exactly the situation it exists for, so it cannot be scheduled on either.
/// </para>
/// </summary>
public interface ITerminationWatchdog
{
    /// <summary>
    /// Arms the deadline. Monotonic and NOT restartable: the first call wins and later calls are ignored,
    /// so nothing can push the deadline out. Production never disarms — see <see cref="Disarm"/>.
    /// </summary>
    void Arm(TimeSpan deadline, Action onDeadlineReached);

    /// <summary>
    /// Releases the watchdog's own resources. Production does NOT call this after requesting the exit:
    /// disarming there would defeat the purpose, because a process that fails to die is precisely what
    /// the deadline is for. It exists so tests (and disposal) do not leak a waiting thread.
    /// </summary>
    void Disarm();
}

/// <summary>Terminates this process. Separated so the watchdog path can be tested without dying.</summary>
public interface IProcessTerminator
{
    void Terminate(int exitCode);
}

/// <summary>
/// Real watchdog: one dedicated background thread that waits on an event with a timeout.
/// <para>
/// A dedicated thread, not a timer: <see cref="TimeProvider"/>/<c>Timer</c> callbacks run on the thread
/// pool, which is one of the things a wedged shutdown can starve. The thread is
/// <see cref="Thread.IsBackground"/>, so its existence can never by itself keep the process alive, and
/// the wait is on a monotonic timeout rather than on wall-clock arithmetic.
/// </para>
/// </summary>
public sealed class TerminationWatchdog(ILogger<TerminationWatchdog> logger) : ITerminationWatchdog, IDisposable
{
    private readonly ManualResetEventSlim _disarmed = new(initialState: false);
    private int _armed;

    public void Arm(TimeSpan deadline, Action onDeadlineReached)
    {
        ArgumentNullException.ThrowIfNull(onDeadlineReached);
        if (Interlocked.Exchange(ref _armed, 1) != 0)
        {
            return; // monotonic and non-restartable: the first deadline is the deadline
        }

        var thread = new Thread(() =>
        {
            try
            {
                if (_disarmed.Wait(deadline))
                {
                    return; // explicitly disarmed (tests/disposal)
                }

                logger.LogWarning(
                    "Shutdown exceeded the {Deadline} termination deadline; terminating the process.",
                    deadline);
                onDeadlineReached();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The termination watchdog failed.");
            }
        })
        {
            IsBackground = true,
            Name = "ServerAlyzer termination watchdog",
            Priority = ThreadPriority.AboveNormal
        };

        thread.Start();
    }

    public void Disarm() => _disarmed.Set();

    public void Dispose()
    {
        _disarmed.Set();
        _disarmed.Dispose();
    }
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
/// a torn file — only, at worst, one known temporary (see <c>OrphanTemporaryCleaner</c>).
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
