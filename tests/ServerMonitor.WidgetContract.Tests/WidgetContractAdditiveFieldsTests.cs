using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.WidgetContract.Tests;

/// <summary>
/// UI.9 B1 (SPEC §1 D-UI9-2/3, tests 2 and 7): the two additive optional v1 fields. The schema version
/// stays 1, so compatibility is proven both ways against checked-in golden files: the new reader reads a
/// file an old app wrote, and a byte-for-byte copy of the 1.1.1 reader record reads a file the new app
/// writes. Unknown <c>attentionMetric</c> values are a missing hint, never a corrupt snapshot.
/// </summary>
public sealed class WidgetContractAdditiveFieldsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid HomeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LabId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), Encoding.UTF8);

    /// <summary>The snapshot both golden files describe (the new one with the two fields set).</summary>
    private static WidgetStateSnapshot NewSample() => new()
    {
        SchemaVersion = WidgetSchema.CurrentVersion,
        GeneratedAtUtc = Now,
        OverallHealth = WidgetHealth.Warning,
        Servers = new[]
        {
            new WidgetServerState
            {
                Id = HomeId, DisplayName = "Home Server", Health = WidgetHealth.Warning,
                CpuUsagePercent = 12.5, MemoryUsagePercent = 40, DiskUsagePercent = 85.5,
                MemoryUsedGb = 3.25, MemoryTotalGb = 8, DiskUsedGb = 100, DiskTotalGb = 117,
                UptimeSeconds = 3_780_000, LastUpdatedUtc = Now.AddSeconds(-10),
                AttentionMetric = WidgetAttentionMetrics.Disk, StaleAfterSeconds = 60
            },
            new WidgetServerState { Id = LabId, DisplayName = "Lab", Health = WidgetHealth.Unknown }
        }
    };

    private static WidgetStateSnapshot WithServer(WidgetServerState server) => new()
    {
        SchemaVersion = WidgetSchema.CurrentVersion,
        GeneratedAtUtc = Now,
        OverallHealth = server.Health,
        Servers = new[] { server }
    };

    private static WidgetServerState Healthy() => new()
    {
        Id = HomeId, DisplayName = "Home", Health = WidgetHealth.Healthy, LastUpdatedUtc = Now
    };

    // ---- golden compatibility matrix (SPEC §1, test 7) ----------------------------------------------

    [Fact]
    public void Serializer_is_deterministic_and_matches_the_new_golden_file()
    {
        Assert.Equal(Fixture("widget-state-v1-new.json"), WidgetStateSerializer.Serialize(NewSample()));
        Assert.Equal(WidgetStateSerializer.Serialize(NewSample()), WidgetStateSerializer.Serialize(NewSample()));
    }

    [Fact]
    public void New_reader_reads_an_old_file_with_both_fields_null()
    {
        var snapshot = WidgetStateSerializer.TryDeserialize(Fixture("widget-state-v1-old.json"));

        Assert.NotNull(snapshot);
        Assert.True(WidgetStateValidator.Validate(snapshot, Now).IsValid);
        Assert.All(snapshot!.Servers, s =>
        {
            Assert.Null(s.AttentionMetric);
            Assert.Null(s.StaleAfterSeconds);
        });
        Assert.Equal(NewSample() with { Servers = snapshot.Servers }, snapshot); // header identical
        Assert.Equal(85.5, snapshot.Servers[0].DiskUsagePercent);
    }

    [Fact]
    public void New_reader_reads_the_new_file_completely()
    {
        var snapshot = WidgetStateSerializer.TryDeserialize(Fixture("widget-state-v1-new.json"));

        Assert.NotNull(snapshot);
        Assert.True(WidgetStateValidator.Validate(snapshot, Now).IsValid);
        Assert.Equal(WidgetAttentionMetric.Disk, WidgetAttentionMetrics.TryParse(snapshot!.Servers[0].AttentionMetric));
        Assert.Equal(60, snapshot.Servers[0].StaleAfterSeconds);
        Assert.Null(snapshot.Servers[1].StaleAfterSeconds);
    }

    [Fact]
    public void Old_1_1_1_reader_record_reads_the_new_file_as_valid()
    {
        // The installed 1.1.1 provider has no member for the new fields; System.Text.Json must skip them
        // (default JsonUnmappedMemberHandling.Skip) instead of failing the snapshot.
        var legacy = JsonSerializer.Deserialize(
            Fixture("widget-state-v1-new.json"), LegacyV111JsonContext.Default.LegacyWidgetStateSnapshot);

        Assert.NotNull(legacy);
        Assert.Equal(1, legacy!.SchemaVersion);
        Assert.Equal(2, legacy.Servers.Count);
        Assert.Equal(HomeId, legacy.Servers[0].Id);
        Assert.Equal(WidgetHealth.Warning, legacy.Servers[0].Health);
        Assert.Equal(85.5, legacy.Servers[0].DiskUsagePercent);
        Assert.Equal(Now.AddSeconds(-10), legacy.Servers[0].LastUpdatedUtc);
    }

    [Fact]
    public void Old_1_1_1_reader_record_has_no_member_for_the_new_fields()
    {
        // Guards the guard: if someone "updates" the legacy copy, the test above stops proving anything.
        var names = typeof(LegacyWidgetServerState).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(nameof(WidgetServerState.AttentionMetric), names);
        Assert.DoesNotContain(nameof(WidgetServerState.StaleAfterSeconds), names);
    }

    // ---- staleAfterSeconds bounds (D-UI9-3 / V-RC-4) -------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(WidgetSchema.MinStaleAfterSeconds)]
    [InlineData(60)]
    [InlineData(WidgetSchema.MaxStaleAfterSeconds)]
    public void StaleAfterSeconds_null_or_in_bounds_is_valid(int? value)
    {
        Assert.True(WidgetStateValidator.Validate(WithServer(Healthy() with { StaleAfterSeconds = value }), Now).IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(WidgetSchema.MinStaleAfterSeconds - 1)]
    [InlineData(WidgetSchema.MaxStaleAfterSeconds + 1)]
    [InlineData(3600)]
    [InlineData(3601)]
    [InlineData(int.MaxValue)]
    public void StaleAfterSeconds_out_of_bounds_is_invalid(int value)
    {
        Assert.Equal(WidgetValidationFailure.MetricOutOfRange,
            WidgetStateValidator.Validate(WithServer(Healthy() with { StaleAfterSeconds = value }), Now).Failure);
    }

    [Fact]
    public void StaleAfterSeconds_bounds_are_the_stale_policy_domain()
    {
        // 20 s floor .. 2 × the slowest supported interval (300 s). The App proves its policy stays inside.
        Assert.Equal(20, WidgetSchema.MinStaleAfterSeconds);
        Assert.Equal(600, WidgetSchema.MaxStaleAfterSeconds);
    }

    // ---- lastUpdatedUtc never fresher than the snapshot (V-RC-5b) ------------------------------------

    [Fact]
    public void LastUpdated_up_to_the_allowed_lead_is_valid()
    {
        var server = Healthy() with { LastUpdatedUtc = Now + WidgetSchema.MaxLastUpdatedLead };
        Assert.True(WidgetStateValidator.Validate(WithServer(server), Now).IsValid);
    }

    [Fact]
    public void LastUpdated_after_generated_beyond_the_lead_is_invalid()
    {
        // Within the 5-minute clock-skew window of "now", so only the new cross-check can reject it.
        var server = Healthy() with { LastUpdatedUtc = Now + WidgetSchema.MaxLastUpdatedLead + TimeSpan.FromTicks(1) };
        Assert.Equal(WidgetValidationFailure.LastUpdatedOutOfRange,
            WidgetStateValidator.Validate(WithServer(server), Now).Failure);
    }

    // ---- attentionMetric allowlist (D-UI9-2 / V-RC-6) ------------------------------------------------

    [Theory]
    [InlineData("cpu", WidgetAttentionMetric.Cpu)]
    [InlineData("memory", WidgetAttentionMetric.Memory)]
    [InlineData("disk", WidgetAttentionMetric.Disk)]
    public void AttentionMetric_allowlisted_values_parse(string wire, WidgetAttentionMetric expected)
    {
        Assert.Equal(expected, WidgetAttentionMetrics.TryParse(wire));
        Assert.Equal(wire, WidgetAttentionMetrics.ToWire(expected));
    }

    public static TheoryData<string?> HostileAttentionMetrics => new()
    {
        null, "", "CPU", "cpu ", " cpu", "Disk", "ram", "${x}", "${$host.hostTheme}", "disk\u0000",
        new string('d', 10 * 1024)
    };

    [Theory]
    [MemberData(nameof(HostileAttentionMetrics))]
    public void AttentionMetric_unknown_value_is_null_and_never_invalidates_the_snapshot(string? wire)
    {
        Assert.Null(WidgetAttentionMetrics.TryParse(wire));

        var snapshot = WithServer(Healthy() with { Health = WidgetHealth.Warning, AttentionMetric = wire });
        var restored = WidgetStateSerializer.TryDeserialize(WidgetStateSerializer.SerializeToUtf8Bytes(snapshot));

        Assert.NotNull(restored); // a string, not an enum: an unknown value cannot fail deserialization
        Assert.True(WidgetStateValidator.Validate(restored, Now).IsValid);
        Assert.Null(WidgetAttentionMetrics.TryParse(restored!.Servers[0].AttentionMetric));
    }

    // ---- malformed table (test 2, contract half) -----------------------------------------------------

    public static TheoryData<string> MalformedNewFields => new()
    {
        """{"schemaVersion":1,"generatedAtUtc":"2026-08-30T12:00:00+00:00","overallHealth":"Healthy","servers":[{"id":"11111111-1111-1111-1111-111111111111","displayName":"Home","health":"Healthy","staleAfterSeconds":"60"}]}""",
        """{"schemaVersion":1,"generatedAtUtc":"2026-08-30T12:00:00+00:00","overallHealth":"Healthy","servers":[{"id":"11111111-1111-1111-1111-111111111111","displayName":"Home","health":"Healthy","staleAfterSeconds":60.5}]}""",
        """{"schemaVersion":1,"generatedAtUtc":"2026-08-30T12:00:00+00:00","overallHealth":"Healthy","servers":[{"id":"11111111-1111-1111-1111-111111111111","displayName":"Home","health":"Healthy","attentionMetric":7}]}""",
        """{"schemaVersion":1,"generatedAtUtc":"2026-08-30T12:00:00+00:00","overallHealth":"Healthy","servers":[{"id":"11111111-1111-1111-1111-111111111111","displayName":"Home","health":"Healthy","attentionMetric":{"$when":"true"}}]}""",
    };

    [Theory]
    [MemberData(nameof(MalformedNewFields))]
    public void Wrong_types_in_the_new_fields_fail_neutral_without_throwing(string json)
    {
        Assert.Null(WidgetStateSerializer.TryDeserialize(json));
    }

    [Fact]
    public void An_unknown_extra_member_is_skipped_not_fatal()
    {
        // Forward compatibility inside v1: a member this reader does not know is ignored.
        const string json = """{"schemaVersion":1,"generatedAtUtc":"2026-08-30T12:00:00+00:00","overallHealth":"Healthy","futureField":{"a":[1,2]},"servers":[]}""";
        var snapshot = WidgetStateSerializer.TryDeserialize(json);
        Assert.NotNull(snapshot);
        Assert.True(WidgetStateValidator.Validate(snapshot, Now).IsValid);
    }
}

// ---- the 1.1.1 reader, frozen ------------------------------------------------------------------------
// A copy of the contract records exactly as shipped in 1.1.1 (before UI.9), with the same source-gen
// options. Only used to prove that an installed old provider still reads a file the new app writes.

public sealed record LegacyWidgetStateSnapshot
{
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required WidgetHealth OverallHealth { get; init; }
    public required IReadOnlyList<LegacyWidgetServerState> Servers { get; init; }
}

public sealed record LegacyWidgetServerState
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public required WidgetHealth Health { get; init; }
    public double? CpuUsagePercent { get; init; }
    public double? MemoryUsagePercent { get; init; }
    public double? DiskUsagePercent { get; init; }
    public double? MemoryUsedGb { get; init; }
    public double? MemoryTotalGb { get; init; }
    public double? DiskUsedGb { get; init; }
    public double? DiskTotalGb { get; init; }
    public long? UptimeSeconds { get; init; }
    public DateTimeOffset? LastUpdatedUtc { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(LegacyWidgetStateSnapshot))]
public sealed partial class LegacyV111JsonContext : JsonSerializerContext
{
}
