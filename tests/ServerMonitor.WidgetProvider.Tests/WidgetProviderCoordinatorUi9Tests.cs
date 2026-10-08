using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
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

    // Expected outcome per row: "Healthy" for the valid CONTROLS (they prove the healthy detection below is
    // not vacuous), otherwise the exact reader reason. Every malformed/hostile row must paint the neutral
    // Unavailable card — never a healthy state, healthy copy, healthy count or the good colour (Atlas final).
    private const string HealthyControl = "Healthy";
    // A UTF-8 BOM is not JSON to the source-generated deserializer: the file reads Corrupt and paints the
    // neutral card (fail-closed). The app's writer never emits a BOM (serializer bytes, no preamble).
    private const string BomOutcome = nameof(WidgetReadUnavailableReason.Corrupt);

    public static TheoryData<string, string, string> Inputs => new()
    {
        { "valid baseline", WithServerFields(string.Empty), HealthyControl },
        { "valid with both new fields", WithServerFields(""","attentionMetric":"disk","staleAfterSeconds":120"""), HealthyControl },
        { "unknown attentionMetric is a missing hint", WithServerFields(",\"attentionMetric\":\"nope\""), HealthyControl },
        { "truncated", WithServerFields(string.Empty)[..80], nameof(WidgetReadUnavailableReason.Corrupt) },
        { "staleAfterSeconds -1", WithServerFields(""","staleAfterSeconds":-1"""), nameof(WidgetReadUnavailableReason.Invalid) },
        { "staleAfterSeconds 0", WithServerFields(""","staleAfterSeconds":0"""), nameof(WidgetReadUnavailableReason.Invalid) },
        { "staleAfterSeconds 3601", WithServerFields(""","staleAfterSeconds":3601"""), nameof(WidgetReadUnavailableReason.Invalid) },
        { "staleAfterSeconds wrong type", WithServerFields(""","staleAfterSeconds":"60" """), nameof(WidgetReadUnavailableReason.Corrupt) },
        { "attentionMetric wrong type", WithServerFields(""","attentionMetric":[1]"""), nameof(WidgetReadUnavailableReason.Corrupt) },
        { "health wrong type", WithServerFields(string.Empty).Replace("\"health\":\"Healthy\"", "\"health\":true"), nameof(WidgetReadUnavailableReason.Corrupt) },
        { "lastUpdated after generated", WithServerFields(string.Empty).Replace("11:59:50", "12:00:02"), nameof(WidgetReadUnavailableReason.Invalid) },
        { "schema 2", WithServerFields(string.Empty).Replace("\"schemaVersion\":1", "\"schemaVersion\":2"), nameof(WidgetReadUnavailableReason.Invalid) },
        { "UTF-8 BOM", "﻿" + WithServerFields(string.Empty), BomOutcome },
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Malformed_or_hostile_input_never_escapes_and_never_renders_healthy(string label, string json, string expected)
    {
        File.WriteAllBytes(_path, Encoding.UTF8.GetBytes(json));
        AssertOutcomeAndCard(label, expected);
    }

    [Fact]
    public void Oversized_file_is_unavailable_and_never_renders_healthy()
    {
        var json = WithServerFields(string.Empty);
        var padded = json[..^2] + new string(' ', (int)WidgetStateLocation.MaxFileBytes) + "]}";
        File.WriteAllBytes(_path, Encoding.UTF8.GetBytes(padded));
        AssertOutcomeAndCard("oversized", nameof(WidgetReadUnavailableReason.Oversized));
    }

    // Healthy copy in every shipped culture (en-US / pt-PT / pt-BR), matched case-insensitively, so the
    // check does not depend on the test machine's UI culture.
    private static readonly string[] HealthyWords = ["healthy", "saudáv"];

    private void AssertOutcomeAndCard(string label, string expected)
    {
        var clock = new FakeTimeProvider(Now);
        var read = new WidgetSnapshotReader(_path, timeProvider: clock).Read();
        var outcome = read.IsAvailable ? HealthyControl : read.Reason.ToString();
        Assert.True(expected == outcome, $"{label}: expected {expected}, got {read.Status}/{read.Reason}");

        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host, clock);
        coordinator.OnWidgetActivated(Widget("a")); // must not throw

        var update = Assert.Single(host.Updates);
        Assert.False(string.IsNullOrWhiteSpace(update.Template), label);
        var data = JsonNode.Parse(update.Data)!.AsObject();
        var card = Rendering.CardTemplateHarness.Expand(update.Template, update.Data);
        Assert.Empty(Rendering.CardTemplateHarness.ShapeErrors(card));
        var visible = Rendering.CardTemplateHarness.VisibleTexts(card);
        var colours = Rendering.CardTemplateHarness.Strings(card).Where(s => s.Key == "color").Select(s => s.Value).ToList();

        if (expected == HealthyControl)
        {
            // Control: a valid healthy snapshot really reads healthy, so the negative checks can fire.
            Assert.True((bool)data["isHealthy"]!, label);
            Assert.Contains(visible, t => HealthyWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("good", colours);
            return;
        }

        // The neutral Unavailable card, and nothing else.
        Assert.True((bool)data["isUnavailable"]!, label);
        foreach (var flag in new[] { "isFleet", "isHealthy", "isAttention", "isNoCurrentData", "isStale", "isEmpty" })
        {
            Assert.False((bool)data[flag]!, $"{label}: {flag}");
        }

        Assert.Empty(data["rows"]!.AsArray());
        Assert.Equal(string.Empty, (string?)data["fraction"]);
        Assert.Equal(string.Empty, (string?)data["rowsOfTotal"]);
        Assert.NotEqual("good", (string?)data["stateColor"]);

        // No healthy word or good colour anywhere in the data or on the expanded card.
        Assert.DoesNotContain(Rendering.CardTemplateHarness.Strings(data),
            s => HealthyWords.Any(w => s.Value.Contains(w, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(visible, t => HealthyWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain("good", colours);
        Assert.NotEmpty(visible); // the neutral title/body is really painted
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
