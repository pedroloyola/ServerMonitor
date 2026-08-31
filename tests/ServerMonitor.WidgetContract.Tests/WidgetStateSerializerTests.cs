using System.Globalization;
using System.Text.Json;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.WidgetContract.Tests;

public sealed class WidgetStateSerializerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static WidgetStateSnapshot Sample(params WidgetServerState[] servers) => new()
    {
        SchemaVersion = WidgetSchema.CurrentVersion,
        GeneratedAtUtc = Now,
        OverallHealth = WidgetHealthPrecedence.Worst(servers.Select(s => s.Health)),
        Servers = servers
    };

    private static WidgetServerState Server(
        string name = "Home Server",
        WidgetHealth health = WidgetHealth.Healthy,
        double? cpu = 12.5,
        double? mem = 40.0,
        double? disk = 55.5) => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        DisplayName = name,
        Health = health,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk,
        LastUpdatedUtc = Now
    };

    [Fact]
    public void Round_trips_a_valid_snapshot()
    {
        var original = Sample(Server(), Server() with { Id = Guid.NewGuid(), Health = WidgetHealth.Warning });

        var bytes = WidgetStateSerializer.SerializeToUtf8Bytes(original);
        var restored = WidgetStateSerializer.TryDeserialize(bytes);

        Assert.NotNull(restored);
        Assert.Equal(original.SchemaVersion, restored!.SchemaVersion);
        Assert.Equal(original.GeneratedAtUtc, restored.GeneratedAtUtc);
        Assert.Equal(original.OverallHealth, restored.OverallHealth);
        // Records compare list-typed properties by reference, so compare the fleet element-wise
        // (each WidgetServerState is a record and compares structurally).
        Assert.Equal(original.Servers, restored.Servers);
    }

    [Fact]
    public void Null_metrics_survive_as_null_not_zero()
    {
        var original = Sample(Server(cpu: null, mem: null, disk: null) with { Health = WidgetHealth.Offline });

        var restored = WidgetStateSerializer.TryDeserialize(WidgetStateSerializer.SerializeToUtf8Bytes(original));

        Assert.NotNull(restored);
        var server = Assert.Single(restored!.Servers);
        Assert.Null(server.CpuUsagePercent);
        Assert.Null(server.MemoryUsagePercent);
        Assert.Null(server.DiskUsagePercent);
    }

    [Fact]
    public void Unicode_display_name_round_trips()
    {
        var name = "Café 日本語 " + char.ConvertFromUtf32(0x1F600);
        var original = Sample(Server(name: name));

        var restored = WidgetStateSerializer.TryDeserialize(WidgetStateSerializer.SerializeToUtf8Bytes(original));

        Assert.Equal(name, Assert.Single(restored!.Servers).DisplayName);
    }

    [Fact]
    public void Numbers_use_invariant_formatting()
    {
        // A value that formats differently under a comma-decimal culture must still use '.' on the wire.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("pt-PT");
        try
        {
            var json = WidgetStateSerializer.Serialize(Sample(Server(cpu: 33.5)));
            Assert.Contains("33.5", json);
            Assert.DoesNotContain("33,5", json);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Enums_are_written_as_strings()
    {
        var json = WidgetStateSerializer.Serialize(Sample(Server(health: WidgetHealth.Critical)));
        Assert.Contains("Critical", json);
        // The numeric enum value must not leak as a bare number for health.
        Assert.Contains("\"health\":\"Critical\"", json);
    }

    [Fact]
    public void Property_names_are_camelCase()
    {
        var json = WidgetStateSerializer.Serialize(Sample(Server()));
        Assert.Contains("\"schemaVersion\":", json);
        Assert.Contains("\"generatedAtUtc\":", json);
        Assert.Contains("\"overallHealth\":", json);
        Assert.Contains("\"servers\":", json);
        Assert.Contains("\"cpuUsagePercent\":", json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":1,\"servers\":")]
    public void Malformed_input_deserializes_to_null(string json)
    {
        Assert.Null(WidgetStateSerializer.TryDeserialize(json));
    }

    [Fact]
    public void NaN_and_Infinity_are_rejected_on_read()
    {
        // System.Text.Json rejects non-finite numbers by default → fail neutral.
        var nan = "{\"schemaVersion\":1,\"generatedAtUtc\":\"2026-08-30T12:00:00+00:00\"," +
                  "\"overallHealth\":\"Healthy\",\"servers\":[{\"id\":\"11111111-1111-1111-1111-111111111111\"," +
                  "\"displayName\":\"x\",\"health\":\"Healthy\",\"cpuUsagePercent\":NaN}]}";
        Assert.Null(WidgetStateSerializer.TryDeserialize(nan));
    }

    [Fact]
    public void Unknown_enum_string_deserializes_to_null()
    {
        var json = "{\"schemaVersion\":1,\"generatedAtUtc\":\"2026-08-30T12:00:00+00:00\"," +
                   "\"overallHealth\":\"Meltdown\",\"servers\":[]}";
        Assert.Null(WidgetStateSerializer.TryDeserialize(json));
    }

    [Fact]
    public void Empty_fleet_serializes_and_restores()
    {
        var original = new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = Now,
            OverallHealth = WidgetHealth.Unknown,
            Servers = Array.Empty<WidgetServerState>()
        };

        var restored = WidgetStateSerializer.TryDeserialize(WidgetStateSerializer.SerializeToUtf8Bytes(original));

        Assert.NotNull(restored);
        Assert.Empty(restored!.Servers);
        Assert.Equal(WidgetHealth.Unknown, restored.OverallHealth);
    }
}
