namespace ServerMonitor.Core.Probes;

/// <summary>
/// Fixed limits on remote probes, so registered probes can never turn a poll into a long or heavy session.
/// </summary>
public static class RemoteProbeLimits
{
    /// <summary>The most probes run on one server in one poll. Due probes beyond it wait for a later poll.</summary>
    public const int MaxProbesPerPoll = 4;

    /// <summary>The most standard output one poll may read across all its probes, in bytes (the sum of their limits).</summary>
    public const int TotalOutputBudgetBytes = 1024 * 1024;

    /// <summary>The largest output limit one probe may declare, in bytes.</summary>
    public const int MaxOutputLimitBytes = 256 * 1024;

    /// <summary>The longest timeout one probe may declare.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest command line, in characters.</summary>
    public const int MaxCommandLength = 1024;

    /// <summary>The longest probe id, in characters.</summary>
    public const int MaxIdLength = 64;
}
