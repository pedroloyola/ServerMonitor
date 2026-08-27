using ServerMonitor.Core.History;

namespace ServerMonitor.Core.Tests.History;

public sealed class HistorySamplingPolicyTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldPersist_NoPriorSample_PersistsFirst()
    {
        var policy = new HistorySamplingPolicy();

        Assert.True(policy.ShouldPersist(null, Base));
    }

    [Fact]
    public void ShouldPersist_WithinMinInterval_Skips()
    {
        var policy = new HistorySamplingPolicy(TimeSpan.FromSeconds(30));

        Assert.False(policy.ShouldPersist(Base, Base + TimeSpan.FromSeconds(29)));
    }

    [Fact]
    public void ShouldPersist_ExactlyAtMinInterval_Persists()
    {
        var policy = new HistorySamplingPolicy(TimeSpan.FromSeconds(30));

        Assert.True(policy.ShouldPersist(Base, Base + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ShouldPersist_CandidateNotAfterLast_Skips()
    {
        var policy = new HistorySamplingPolicy(TimeSpan.FromSeconds(30));

        // Duplicate/replayed or non-monotonic clock: never re-persist at or before the last stamp.
        Assert.False(policy.ShouldPersist(Base, Base));
        Assert.False(policy.ShouldPersist(Base, Base - TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Polling10s_Against30sPolicy_YieldsBoundedSampleCadence()
    {
        // Simulate 10s polling over 5 minutes; the 30s policy should admit roughly one per 30s.
        var policy = new HistorySamplingPolicy(TimeSpan.FromSeconds(30));
        DateTimeOffset? last = null;
        var persisted = 0;

        for (var i = 0; i < 30; i++) // 30 polls * 10s = 300s = 5 minutes
        {
            var candidate = Base + TimeSpan.FromSeconds(i * 10);
            if (policy.ShouldPersist(last, candidate))
            {
                persisted++;
                last = candidate;
            }
        }

        // 5 minutes at one-per-30s ⇒ ~10 samples (first is immediate, then every 30s).
        Assert.Equal(10, persisted);
    }

    [Fact]
    public void Polling30s_Against30sPolicy_PersistsEveryCycle()
    {
        var policy = new HistorySamplingPolicy(TimeSpan.FromSeconds(30));
        DateTimeOffset? last = null;
        var persisted = 0;

        for (var i = 0; i < 10; i++)
        {
            var candidate = Base + TimeSpan.FromSeconds(i * 30);
            if (policy.ShouldPersist(last, candidate))
            {
                persisted++;
                last = candidate;
            }
        }

        Assert.Equal(10, persisted);
    }

    [Fact]
    public void Constructor_NegativeInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistorySamplingPolicy(TimeSpan.FromSeconds(-1)));
    }
}
