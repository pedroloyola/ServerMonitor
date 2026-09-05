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

    private readonly object _armGate = new();

    private int _armed;

    public bool IsArmed => Volatile.Read(ref _armed) == 1;

    public void Arm(TimeSpan deadline, Action onDeadlineReached)
    {
        ArgumentNullException.ThrowIfNull(onDeadlineReached);
        if (deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        // CV-21 A. The flag used to be set BEFORE the schedule was established, so a scheduler that threw
        // left an object reporting IsArmed == true with no escalation behind it: a claim of a guarantee
        // that did not exist, which is the same defect class as a discarded Shell_NotifyIcon BOOL one
        // level down. The lock keeps "check, establish, publish" indivisible, so a concurrent caller can
        // never observe the window between establishing the schedule and publishing the flag.
        lock (_armGate)
        {
            if (Volatile.Read(ref _armed) == 1)
            {
                return; // already established: the first deadline is the deadline
            }

            try
            {
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
            catch (Exception exception)
            {
                // Fail closed: nothing was established, so nothing may claim to be armed.
                Volatile.Write(ref _armed, 0);
                logger.LogError(exception, "The termination watchdog could not be armed.");
                throw new TerminationWatchdogArmingException(deadline, exception);
            }

            // Only now is the escalation real.
            Volatile.Write(ref _armed, 1);
        }
    }
}

/// <summary>
/// The terminal escalation could not be established (CV-21 A). Deterministic and impossible to ignore by
/// accident - a return value could be dropped silently, which is exactly the defect class this slice
/// corrects elsewhere. The watchdog is left NOT armed.
/// </summary>
public sealed class TerminationWatchdogArmingException(TimeSpan deadline, Exception innerException)
    : InvalidOperationException(
        $"The termination watchdog could not be armed for {deadline}.", innerException)
{
    /// <summary>The deadline that could not be established.</summary>
    public TimeSpan Deadline { get; } = deadline;
}

/// <summary>
/// What Windows answered when asked to end this process (CV-21 B).
/// </summary>
/// <param name="Requested">True only when <c>TerminateProcess</c> itself returned TRUE.</param>
/// <param name="Win32Error">
/// The error captured immediately after a FALSE return, before any other call could overwrite it. Zero
/// when the request succeeded.
/// </param>
public readonly record struct ProcessTerminationResult(bool Requested, int Win32Error)
{
    public static ProcessTerminationResult Success { get; } = new(Requested: true, Win32Error: 0);

    public static ProcessTerminationResult Failed(int win32Error) => new(Requested: false, win32Error);
}

/// <summary>Terminates this process. Separated so the watchdog path can be tested without dying.</summary>
public interface IProcessTerminator
{
    /// <summary>
    /// Asks Windows to terminate this process and REPORTS what it answered. The result is not advisory:
    /// discarding it is exactly the defect that made the tray registration fictional, one level down.
    /// </summary>
    ProcessTerminationResult Terminate(int exitCode);
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

    public ProcessTerminationResult Terminate(int exitCode) =>
        TerminateHandle(GetCurrentProcess(), exitCode);

    /// <summary>
    /// The single place the native call happens, with the handle as a parameter so the REAL P/Invoke, the
    /// REAL BOOL inspection and the REAL error capture can be exercised by a test without the test host
    /// dying (a null handle is refused by Windows with ERROR_INVALID_HANDLE and terminates nothing).
    /// <para>
    /// The BOOL used to be discarded. <c>TerminateProcess</c> can legitimately fail - a handle without
    /// PROCESS_TERMINATE, a process already being torn down - and a discarded FALSE meant the watchdog
    /// reported a completed escalation while the process carried on. The last error is read IMMEDIATELY
    /// after the call, before logging or anything else that could overwrite it.
    /// </para>
    /// </summary>
    internal ProcessTerminationResult TerminateHandle(IntPtr processHandle, int exitCode)
    {
        var terminated = TerminateProcess(processHandle, unchecked((uint)exitCode));
        if (terminated)
        {
            return ProcessTerminationResult.Success;
        }

        var error = Marshal.GetLastWin32Error();
        return ProcessTerminationResult.Failed(error);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
