namespace ServerMonitor.App.Tests;

/// <summary>
/// Tests that deliberately PARK threads — a host stopping on a barrier, a watchdog waiting — share this
/// collection so xUnit never runs them alongside each other.
/// <para>
/// Running them in parallel starves the thread pool: each one holds a pool thread for the length of its
/// barrier, and a completion signal that needs another pool thread then waits behind them. That produced
/// one unattributed failure in 38 suite runs (a 30 s timeout in
/// <c>NonCooperativeStop_ReturnsWithinBoundAndNeverDisposes</c>) — a test-harness flake, not a product
/// defect, but one that would have kept reappearing and eroding trust in the suite.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadBlockingTests
{
    public const string Name = "thread-blocking";
}
