using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Enums;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// M14.5 A2: the production step tracker behind <see cref="Core.Models.SshConnectionRequest.StageProgress"/> —
/// forward only, every step once, never a skipped step, nothing after the result is built.
/// </summary>
public sealed class SshConnectionStageTrackerTests
{
    [Fact]
    public void A_jump_ahead_still_reports_every_intermediate_step_in_order()
    {
        var progress = new Recording();
        var tracker = new SshConnectionService.StageTracker(progress, NullLogger.Instance);

        tracker.Advance(SshConnectionStage.OperatingSystemIdentified);

        Assert.Equal(
            [
                SshConnectionStage.PortReachable,
                SshConnectionStage.HostKeyVerified,
                SshConnectionStage.Authenticated,
                SshConnectionStage.OperatingSystemIdentified
            ],
            progress.Reports);
        Assert.Equal(SshConnectionStage.OperatingSystemIdentified, tracker.Reached);
    }

    [Fact]
    public void Repeating_or_going_back_reports_nothing_and_never_lowers_the_stage()
    {
        var progress = new Recording();
        var tracker = new SshConnectionService.StageTracker(progress, NullLogger.Instance);

        tracker.Advance(SshConnectionStage.HostKeyVerified);
        tracker.Advance(SshConnectionStage.HostKeyVerified);
        tracker.Advance(SshConnectionStage.PortReachable);
        tracker.Advance(SshConnectionStage.None);

        Assert.Equal([SshConnectionStage.PortReachable, SshConnectionStage.HostKeyVerified], progress.Reports);
        Assert.Equal(SshConnectionStage.HostKeyVerified, tracker.Reached);
    }

    [Fact]
    public void Nothing_is_reported_after_the_result_is_built()
    {
        var progress = new Recording();
        var tracker = new SshConnectionService.StageTracker(progress, NullLogger.Instance);
        tracker.Advance(SshConnectionStage.PortReachable);

        var carried = tracker.Complete();
        tracker.Advance(SshConnectionStage.Authenticated);

        Assert.Equal(SshConnectionStage.PortReachable, carried);
        Assert.Equal([SshConnectionStage.PortReachable], progress.Reports);
        Assert.Equal(SshConnectionStage.PortReachable, tracker.Reached);
    }

    [Fact]
    public void Without_a_progress_sink_the_stage_is_still_tracked()
    {
        var tracker = new SshConnectionService.StageTracker(null, NullLogger.Instance);

        tracker.Advance(SshConnectionStage.Authenticated);

        Assert.Equal(SshConnectionStage.Authenticated, tracker.Complete());
    }

    [Fact]
    public void A_throwing_sink_does_not_stop_the_following_steps()
    {
        var calls = new List<SshConnectionStage>();
        var tracker = new SshConnectionService.StageTracker(
            new Throwing(calls),
            NullLogger.Instance);

        tracker.Advance(SshConnectionStage.Authenticated);

        Assert.Equal(
            [SshConnectionStage.PortReachable, SshConnectionStage.HostKeyVerified, SshConnectionStage.Authenticated],
            calls);
        Assert.Equal(SshConnectionStage.Authenticated, tracker.Reached);
    }

    private sealed class Recording : IProgress<SshConnectionStage>
    {
        public List<SshConnectionStage> Reports { get; } = [];

        public void Report(SshConnectionStage value) => Reports.Add(value);
    }

    private sealed class Throwing(List<SshConnectionStage> calls) : IProgress<SshConnectionStage>
    {
        public void Report(SshConnectionStage value)
        {
            calls.Add(value);
            throw new InvalidOperationException("observer failed");
        }
    }
}
