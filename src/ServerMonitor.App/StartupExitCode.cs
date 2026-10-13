namespace ServerMonitor.App;

/// <summary>
/// UI.12 APP-STARTUP-FAILURE-EXIT0 (Cortex UI.12A C-S1a): the process exit code <c>Program.Main</c> returns after
/// <c>Application.Start</c>. A startup failure caught by <c>OnLaunched</c> used to end through the same orderly exit as a
/// user's "Sair" and <c>Main</c> returned 0, so a launch that never showed anything read as success to whoever launched it
/// (QA, scripts, CI). Now the failure is recorded here first and <c>Main</c> returns <see cref="StartupFailure"/>.
/// <para>
/// Process-scoped and set-once: the first recorded failure wins and nothing resets it. Not a persisted record - a
/// recoverable on-disk record is a separate, untaken product decision (C-S1b).
/// </para>
/// </summary>
internal sealed class StartupExitCode
{
    /// <summary>Distinct from 0, from the QA refusal (3) and from the termination watchdog (0x5352); greppable.</summary>
    public const int StartupFailure = 0x5353;

    private int _value;

    /// <summary>0 unless a startup failure was recorded.</summary>
    public int Value => Volatile.Read(ref _value);

    /// <summary>Records the startup failure. Idempotent; never overwrites a code already recorded.</summary>
    public void RecordStartupFailure() => Interlocked.CompareExchange(ref _value, StartupFailure, 0);
}
