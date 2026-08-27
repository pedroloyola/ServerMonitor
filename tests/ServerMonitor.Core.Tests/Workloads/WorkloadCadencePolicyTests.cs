using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Core.Tests.Workloads;

public sealed class WorkloadCadencePolicyTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsDue_NoPrior_First()
    {
        var policy = new WorkloadCadencePolicy();

        Assert.True(policy.IsDue(null, Base));
    }

    [Fact]
    public void IsDue_WithinMinInterval_False()
    {
        var policy = new WorkloadCadencePolicy(TimeSpan.FromSeconds(60));

        Assert.False(policy.IsDue(Base, Base + TimeSpan.FromSeconds(59)));
    }

    [Fact]
    public void IsDue_ExactlyAtMinInterval_True()
    {
        var policy = new WorkloadCadencePolicy(TimeSpan.FromSeconds(60));

        Assert.True(policy.IsDue(Base, Base + TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void IsDue_CandidateNotAfterLast_False()
    {
        var policy = new WorkloadCadencePolicy(TimeSpan.FromSeconds(60));

        // Duplicate/replayed cycle or non-monotonic clock: never due at or before the last stamp.
        Assert.False(policy.IsDue(Base, Base));
        Assert.False(policy.IsDue(Base, Base - TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Polling10s_Against60sPolicy_ThrottlesToHostFloor()
    {
        // Host polls every 10s over 5 minutes; workloads should be due at most once per 60s.
        var policy = new WorkloadCadencePolicy(TimeSpan.FromSeconds(60));
        DateTimeOffset? last = null;
        var due = 0;

        for (var i = 0; i < 30; i++) // 30 * 10s = 300s
        {
            var candidate = Base + TimeSpan.FromSeconds(i * 10);
            if (policy.IsDue(last, candidate))
            {
                due++;
                last = candidate;
            }
        }

        Assert.Equal(5, due); // first + every 60s over 5 minutes
    }

    [Fact]
    public void Polling300s_Against60sPolicy_FollowsHostCadence()
    {
        // Host polls slower than the floor (every 5 min): workloads simply follow the host, never faster.
        var policy = new WorkloadCadencePolicy(TimeSpan.FromSeconds(60));
        DateTimeOffset? last = null;
        var due = 0;

        for (var i = 0; i < 4; i++)
        {
            var candidate = Base + TimeSpan.FromSeconds(i * 300);
            if (policy.IsDue(last, candidate))
            {
                due++;
                last = candidate;
            }
        }

        Assert.Equal(4, due);
    }

    [Fact]
    public void Constructor_NegativeInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkloadCadencePolicy(TimeSpan.FromSeconds(-1)));
    }
}
