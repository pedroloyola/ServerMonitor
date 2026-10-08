using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Tests.Fakes;

namespace ServerMonitor.WidgetProvider.Tests;

/// <summary>
/// UI.9 B2 coordinator behaviour: <c>RefreshAll</c> paints only on-screen widgets (SPEC §7), and the
/// malformed-input table (SPEC test 2, provider half) goes through the REAL reader + validator + renderer
/// and still reaches <c>host.Update</c> without anything escaping. Files live in a per-test temp sentinel
/// directory — never the real user-data path.
/// </summary>
public sealed class WidgetProviderCoordinatorUi9Tests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-widget-ui9-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public WidgetProviderCoordinatorUi9Tests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, WidgetStateLocation.FileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WidgetActivation Widget(string id) =>
        new(id, "ServerAlyzer_Widget", WidgetSizeHint.Medium, CustomState: null);

    private WidgetProviderCoordinator NewCoordinator(FakeWidgetHost host, FakeTimeProvider clock) =>
        new(host, new WidgetSnapshotReader(_path, timeProvider: clock), clock);

    // ---- RefreshAll only repaints on-screen widgets (SPEC §7 SHOULD) ---------------------------------

    [Fact]
    public void RefreshAll_skips_a_deactivated_widget_and_activation_repaints_it()
    {
        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host, new FakeTimeProvider(Now));
        coordinator.OnWidgetActivated(Widget("seen"));
        coordinator.OnWidgetActivated(Widget("hidden"));
        coordinator.OnWidgetDeactivated("hidden");

        coordinator.RefreshAll();

        Assert.Equal(2, host.UpdateCountFor("seen"));   // create + refresh
        Assert.Equal(1, host.UpdateCountFor("hidden")); // create only: off screen, not repainted
        Assert.Equal(2, coordinator.ActiveWidgetCount);  // still registered

        coordinator.OnWidgetActivated(Widget("hidden")); // the host's Activate repaints it anyway
        Assert.Equal(2, host.UpdateCountFor("hidden"));
    }

    // ---- malformed table, end to end (SPEC test 2) ---------------------------------------------------

    private const string Head = """{"schemaVersion":1,"generatedAtUtc":"2026-08-30T12:00:00+00:00","overallHealth":"Healthy","servers":[""";
    private const string Server = """{"id":"11111111-1111-1111-1111-111111111111","displayName":"Home","health":"Healthy","cpuUsagePercent":10,"lastUpdatedUtc":"2026-08-30T11:59:50+00:00"{0}}""";

    private static string WithServerFields(string extra) => Head + Server.Replace("{0}", extra) + "]}";

    public static TheoryData<string, string, bool?> Inputs => new()
    {
        { "valid baseline", WithServerFields(string.Empty), true },
        { "valid with both new fields", WithServerFields(""","attentionMetric":"disk","staleAfterSeconds":120"""), true },
        { "unknown attentionMetric is a missing hint", WithServerFields(",\"attentionMetric\":\"nope\""), true },
        { "truncated", WithServerFields(string.Empty)[..80], false },
        { "staleAfterSeconds -1", WithServerFields(""","staleAfterSeconds":-1"""), false },
        { "staleAfterSeconds 0", WithServerFields(""","staleAfterSeconds":0"""), false },
        { "staleAfterSeconds 3601", WithServerFields(""","staleAfterSeconds":3601"""), false },
        { "staleAfterSeconds wrong type", WithServerFields(""","staleAfterSeconds":"60" """), false },
        { "attentionMetric wrong type", WithServerFields(""","attentionMetric":[1]"""), false },
        { "health wrong type", WithServerFields(string.Empty).Replace("\"health\":\"Healthy\"", "\"health\":true"), false },
        { "lastUpdated after generated", WithServerFields(string.Empty).Replace("11:59:50", "12:00:02"), false },
        { "schema 2", WithServerFields(string.Empty).Replace("\"schemaVersion\":1", "\"schemaVersion\":2"), false },
        { "UTF-8 BOM (outcome recorded, never a crash)", "﻿" + WithServerFields(string.Empty), null },
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Malformed_or_hostile_input_never_escapes_and_still_paints_a_card(string label, string json, bool? available)
    {
        File.WriteAllBytes(_path, Encoding.UTF8.GetBytes(json));
        AssertPaintsWithoutEscaping(label, available);
    }

    [Fact]
    public void Oversized_file_is_unavailable_and_still_paints()
    {
        var json = WithServerFields(string.Empty);
        var padded = json[..^2] + new string(' ', (int)WidgetStateLocation.MaxFileBytes) + "]}";
        File.WriteAllBytes(_path, Encoding.UTF8.GetBytes(padded));
        AssertPaintsWithoutEscaping("oversized", available: false);
    }

    private void AssertPaintsWithoutEscaping(string label, bool? available)
    {
        var clock = new FakeTimeProvider(Now);
        var read = new WidgetSnapshotReader(_path, timeProvider: clock).Read();
        if (available is { } expected)
        {
            Assert.True(expected == read.IsAvailable, $"{label}: expected available={expected}, got {read.Status}/{read.Reason}");
        }

        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host, clock);
        coordinator.OnWidgetActivated(Widget("a")); // must not throw

        var update = Assert.Single(host.Updates);
        Assert.False(string.IsNullOrWhiteSpace(update.Template), label);
    }
}

/// <summary>
/// A <see cref="FakeTimeProvider"/> that records every timer created on it, so a test can prove a timeout
/// runs on the INJECTED clock (CI-WP-BOUNDED-CLOCK) instead of inferring it from timing.
/// </summary>
internal sealed class TimerRecordingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    public ConcurrentQueue<TimeSpan> CreatedTimerDueTimes { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        CreatedTimerDueTimes.Enqueue(dueTime);
        return base.CreateTimer(callback, state, dueTime, period);
    }
}
