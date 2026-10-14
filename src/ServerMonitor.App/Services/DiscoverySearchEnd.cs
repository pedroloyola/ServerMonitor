namespace ServerMonitor.App.Services;

/// <summary>REL.0B: how the last user-started local-network search ended, for the empty state's end copy.</summary>
public enum DiscoverySearchEnd
{
    /// <summary>No search has ended yet in this session, or one is running.</summary>
    None,

    /// <summary>Stopped on purpose: window to background, exit.</summary>
    Stopped,

    /// <summary>Ended by itself: nothing visible within <see cref="DiscoveryOptions.NothingFoundTimeout"/>.</summary>
    NothingFound,

    /// <summary>Ended by itself at <see cref="DiscoveryOptions.MaxSearchDuration"/>.</summary>
    TimeLimit,

    /// <summary>The browser could not start (e.g. no usable network interface).</summary>
    Failed
}
