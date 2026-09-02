namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// Budget B: how many recovery episodes may be <b>admitted</b> in a sliding monotonic window.
/// <para>
/// <b>The independence from budget A is structural, not a convention.</b> This type has exactly one
/// public method. There is no <c>Reset</c>, no <c>OnSuccess</c>, no shared field and no constructor
/// parameter through which an outcome could reach it — so a successful recovery <i>cannot</i> clear the
/// history, because nothing in the program has a way to tell it that a recovery succeeded. That is the
/// bypass the original CV-2 defect relied on, removed by making it unrepresentable rather than
/// forbidden.
/// </para>
/// <para>
/// It counts <b>admissions</b>, not outcomes: an adversarial flood in which every <c>NIM_ADD</c>
/// succeeds must still converge on suppression, which is exactly what counting admissions gives.
/// </para>
/// </summary>
internal sealed class EpisodeFrequencyLimiter
{
    /// <summary>
    /// Approved envelope: ~5 episodes per 60 s. This is also a CV-8/CV-10 architectural dependency —
    /// the synchronous <c>Shell_NotifyIcon</c> is only acceptable on the UI thread while B stays inside
    /// this envelope — so it is not widened without re-measuring the UI cost.
    /// </summary>
    internal const int DefaultCapacity = 5;

    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);

    private readonly int _capacity;
    private readonly long _windowTicks;
    private readonly long[] _admissions;

    private int _count;
    private int _next;

    internal EpisodeFrequencyLimiter(TimeProvider timeProvider, int? capacity = null, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _capacity = capacity ?? DefaultCapacity;
        if (_capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), _capacity, "The capacity must be positive.");
        }

        // Stored in the provider's own timestamp unit so no conversion is needed on the hot path.
        _windowTicks = (long)((window ?? DefaultWindow).TotalSeconds * timeProvider.TimestampFrequency);
        _admissions = new long[_capacity];
    }

    /// <summary>
    /// The only public method: may an episode start now? Nothing else is exposed, and in particular
    /// nothing can report an outcome back.
    /// </summary>
    /// <param name="monotonicTimestamp">A monotonic timestamp from the same provider.</param>
    /// <returns>True when the admission is allowed and has been recorded.</returns>
    internal bool TryBeginEpisode(long monotonicTimestamp)
    {
        var cutoff = monotonicTimestamp - _windowTicks;

        // Drop admissions that have slid out of the window. Only the passage of time does this.
        var live = 0;
        for (var index = 0; index < _count; index++)
        {
            if (_admissions[index] > cutoff)
            {
                _admissions[live++] = _admissions[index];
            }
        }

        _count = live;
        _next = live;

        if (_count >= _capacity)
        {
            return false;
        }

        _admissions[_next++] = monotonicTimestamp;
        _count++;
        return true;
    }
}
