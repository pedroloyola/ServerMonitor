using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Tests.Fakes;

namespace ServerMonitor.WidgetProvider.Tests;

/// <summary>
/// THE test the defect needed (M13 QA-9). Everything else in this suite proves that <c>RefreshAll</c>
/// works — which was already true when a widget on an open board never repainted, because nothing in the
/// running provider ever called it. This test proves the whole chain instead:
/// <para>
/// <b>real snapshot mutation on disk → real OS watcher callback → real debounce → RefreshAll → host.Update
/// carrying the NEW values.</b>
/// </para>
/// Everything here is a POSITIVE boundary test: it waits for something to happen. Claims of the shape
/// "and then nothing happens" live in <see cref="WidgetProviderCoordinatorPumpTests"/>, driven by a
/// controllable change source on a fake clock, because no bounded wall-clock wait can distinguish "the
/// pump is correctly silent" from "the event has not arrived yet".
/// <para>
/// It builds the coordinator through <see cref="WidgetProviderCoordinator.CreateWithFileSystemPump"/> —
/// the exact composition <c>Program.Main</c> uses — and commits with the writer's own primitives (a
/// uniquely-named temp in the same directory, then <c>File.Replace</c>, or <c>File.Move</c> for the first
/// write). Only the Windows Widgets host itself is faked. Waits are event-driven with a generous timeout;
/// there are no fixed sleeps.
/// </para>
/// </summary>
public sealed class WidgetRepaintIntegrationTests : IDisposable
{
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Short enough to keep the test quick, long enough to still coalesce one commit's burst.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    /// <summary>Far out of the way: these tests must be driven by real events, never by the backstop.</summary>
    private static readonly TimeSpan Backstop = TimeSpan.FromMinutes(10);

    private readonly string _dir;
    private readonly string _path;

    public WidgetRepaintIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sm-widgetrepaint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, WidgetStateLocation.FileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WidgetActivation Widget(string id) =>
        new(id, "ServerAlyzer_Widget", WidgetSizeHint.Medium, CustomState: null);

    private static WidgetStateSnapshot Snapshot(double cpu) => new()
    {
        SchemaVersion = WidgetSchema.CurrentVersion,
        GeneratedAtUtc = DateTimeOffset.UtcNow,
        OverallHealth = WidgetHealth.Healthy,
        Servers = new[]
        {
            new WidgetServerState
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Home",
                Health = WidgetHealth.Healthy,
                CpuUsagePercent = cpu,
                MemoryUsagePercent = 20,
                DiskUsagePercent = 30,
                LastUpdatedUtc = DateTimeOffset.UtcNow
            }
        }
    };

    /// <summary>The writer's first-write primitive: unique temp in the same folder, then a plain rename.</summary>
    private void FirstWrite(double cpu)
    {
        var temp = WidgetStateLocation.NewTempPath(_dir);
        File.WriteAllBytes(temp, WidgetStateSerializer.SerializeToUtf8Bytes(Snapshot(cpu)));
        File.Move(temp, _path);
    }

    /// <summary>The writer's steady-state commit: unique temp in the same folder, then ReplaceFile.</summary>
    private void AtomicReplace(double cpu)
    {
        var temp = WidgetStateLocation.NewTempPath(_dir);
        File.WriteAllBytes(temp, WidgetStateSerializer.SerializeToUtf8Bytes(Snapshot(cpu)));
        var backup = _path + ".bak";
        File.Replace(temp, _path, backup);
        try { File.Delete(backup); } catch { }
    }

    /// <summary>
    /// How a CPU percentage appears in the rendered Adaptive Card: the renderer emits the number and its
    /// unit as separate text blocks, so the marker is the number's own text node. Matching this — rather
    /// than merely counting updates — is what proves the values actually CHANGED on the card (QA-8 H).
    /// </summary>
    private static string CpuMarker(int cpu) => $"\"text\":\"{cpu}\"";

    private WidgetProviderCoordinator NewCoordinator(FakeWidgetHost host) =>
        WidgetProviderCoordinator.CreateWithFileSystemPump(
            host,
            _path,
            debounce: Debounce,
            backstopInterval: Backstop);

    [Fact]
    public void An_atomic_snapshot_replace_repaints_an_on_screen_widget_with_the_new_values()
    {
        FirstWrite(cpu: 11);
        var host = new FakeWidgetHost();
        using var repainted = new ManualResetEventSlim(false);
        var coordinator = NewCoordinator(host);
        try
        {
            coordinator.OnWidgetActivated(Widget("a"));
            Assert.Equal(1, host.UpdateCountFor("a")); // the host callback's own paint

            host.Updated = (_, card, _) =>
            {
                if (card.Contains(CpuMarker(77), StringComparison.Ordinal))
                {
                    repainted.Set();
                }
            };

            AtomicReplace(cpu: 77);

            Assert.True(
                repainted.Wait(UpdateTimeout),
                "a real atomic snapshot replace never reached the widget - the repaint pump is dead");
            Assert.True(host.UpdateCountFor("a") >= 2);
        }
        finally
        {
            coordinator.Shutdown();
        }
    }

    [Fact]
    public void The_widget_keeps_updating_across_many_consecutive_snapshot_commits()
    {
        FirstWrite(cpu: 1);
        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host);
        try
        {
            coordinator.OnWidgetActivated(Widget("a"));

            using var seen = new ManualResetEventSlim(false);
            var wanted = string.Empty;
            host.Updated = (_, card, _) =>
            {
                var target = Volatile.Read(ref wanted);
                if (target.Length > 0 && card.Contains(target, StringComparison.Ordinal))
                {
                    seen.Set();
                }
            };

            // Five successive monitoring cycles, each observed on its own before the next is written, so
            // this proves sustained liveness rather than one lucky event (QA-8 G/H).
            for (var cycle = 1; cycle <= 5; cycle++)
            {
                var marker = (cycle * 10) + 1; // 11, 21, 31, 41, 51 - each distinct in the rendered card
                seen.Reset();
                Volatile.Write(ref wanted, CpuMarker(marker));

                AtomicReplace(marker);

                Assert.True(seen.Wait(UpdateTimeout), $"cycle {cycle} never reached the widget");
            }
        }
        finally
        {
            coordinator.Shutdown();
        }
    }

    [Fact]
    public void Reopening_the_board_resumes_repainting()
    {
        FirstWrite(cpu: 1);
        var host = new FakeWidgetHost();
        using var repainted = new ManualResetEventSlim(false);
        var coordinator = NewCoordinator(host);
        try
        {
            coordinator.OnWidgetActivated(Widget("a"));
            coordinator.OnWidgetDeactivated("a");
            coordinator.OnWidgetActivated(Widget("a"));

            host.Updated = (_, card, _) =>
            {
                if (card.Contains(CpuMarker(77), StringComparison.Ordinal))
                {
                    repainted.Set();
                }
            };

            AtomicReplace(cpu: 77);

            Assert.True(repainted.Wait(UpdateTimeout), "the pump did not resume after the board reopened");
        }
        finally
        {
            coordinator.Shutdown();
        }
    }

    /// <summary>
    /// A provider relaunched while the board is already open recovers its widgets through GetWidgetInfos.
    /// The Windows App SDK promises no <c>Activate</c> after that recovery, so the pump must already be
    /// running — otherwise the recovered widget paints once and then freezes, which is exactly the defect.
    /// </summary>
    [Fact]
    public void A_provider_relaunched_with_widgets_already_pinned_keeps_repainting_them()
    {
        FirstWrite(cpu: 1);
        var host = new FakeWidgetHost();
        host.Existing.Add(Widget("a"));
        using var repainted = new ManualResetEventSlim(false);
        var coordinator = NewCoordinator(host);
        try
        {
            coordinator.RehydrateFromHost();
            Assert.Equal(1, host.UpdateCountFor("a"));

            host.Updated = (_, card, _) =>
            {
                if (card.Contains(CpuMarker(77), StringComparison.Ordinal))
                {
                    repainted.Set();
                }
            };

            AtomicReplace(cpu: 77);

            Assert.True(repainted.Wait(UpdateTimeout), "a rehydrated widget never repainted");
        }
        finally
        {
            coordinator.Shutdown();
        }
    }

    [Fact]
    public void The_pump_starts_even_when_the_snapshot_directory_does_not_exist_yet()
    {
        // The provider can be launched before the app has ever written a snapshot: the watch cannot be
        // established, and the source must stay inert rather than throwing at the COM boundary.
        var missing = Path.Combine(_dir, "not-created-yet", WidgetStateLocation.FileName);
        var host = new FakeWidgetHost();
        var coordinator = WidgetProviderCoordinator.CreateWithFileSystemPump(
            host, missing, debounce: Debounce, backstopInterval: Backstop);
        try
        {
            coordinator.OnWidgetActivated(Widget("a"));

            // Painted the neutral "unavailable" card and survived; the backstop retries the watch later.
            Assert.Equal(1, host.UpdateCountFor("a"));
            Assert.Equal(1, coordinator.OnScreenWidgetCount);
        }
        finally
        {
            coordinator.Shutdown();
        }
    }

    /// <summary>
    /// Shutdown on the real composition: the pump is torn down and the process can exit. The negative
    /// half — that a later commit paints NOTHING — is proved deterministically in
    /// <c>WidgetProviderCoordinatorPumpTests</c>, where the change source is driven by the test instead
    /// of being waited on.
    /// </summary>
    [Fact]
    public void Shutdown_tears_the_pump_down_and_a_later_commit_is_harmless()
    {
        FirstWrite(cpu: 1);
        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host);
        coordinator.OnWidgetActivated(Widget("a"));
        var painted = host.UpdateCountFor("a");

        coordinator.Shutdown();
        AtomicReplace(cpu: 77);
        coordinator.Shutdown(); // idempotent, and still safe after a commit

        Assert.Equal(painted, host.UpdateCountFor("a"));
    }
}
