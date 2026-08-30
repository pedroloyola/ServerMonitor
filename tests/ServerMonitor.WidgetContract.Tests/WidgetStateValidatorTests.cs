using ServerMonitor.WidgetContract;

namespace ServerMonitor.WidgetContract.Tests;

public sealed class WidgetStateValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static WidgetServerState ValidServer() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Home Server",
        Health = WidgetHealth.Healthy,
        CpuUsagePercent = 10,
        MemoryUsagePercent = 20,
        DiskUsagePercent = 30,
        LastUpdatedUtc = Now
    };

    private static WidgetStateSnapshot ValidSnapshot(params WidgetServerState[] servers) => new()
    {
        SchemaVersion = WidgetSchema.CurrentVersion,
        GeneratedAtUtc = Now,
        OverallHealth = WidgetHealth.Healthy,
        Servers = servers.Length == 0 ? new[] { ValidServer() } : servers
    };

    [Fact]
    public void Valid_snapshot_passes()
    {
        Assert.True(WidgetStateValidator.Validate(ValidSnapshot(), Now).IsValid);
    }

    [Fact]
    public void Empty_fleet_is_valid()
    {
        var snapshot = new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = Now,
            OverallHealth = WidgetHealth.Unknown,
            Servers = Array.Empty<WidgetServerState>()
        };

        Assert.True(WidgetStateValidator.Validate(snapshot, Now).IsValid);
    }

    [Fact]
    public void Null_snapshot_fails()
    {
        var result = WidgetStateValidator.Validate(null, Now);
        Assert.False(result.IsValid);
        Assert.Equal(WidgetValidationFailure.NullSnapshot, result.Failure);
    }

    [Fact]
    public void Unknown_schema_version_fails()
    {
        var snapshot = ValidSnapshot() with { SchemaVersion = 2 };
        Assert.Equal(WidgetValidationFailure.UnsupportedSchemaVersion,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Fact]
    public void Too_many_servers_fails()
    {
        var servers = Enumerable.Range(0, WidgetSchema.MaxServers + 1).Select(_ => ValidServer()).ToArray();
        var snapshot = ValidSnapshot(servers);
        Assert.Equal(WidgetValidationFailure.TooManyServers,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Fact]
    public void Max_servers_exactly_is_valid()
    {
        var servers = Enumerable.Range(0, WidgetSchema.MaxServers).Select(_ => ValidServer()).ToArray();
        Assert.True(WidgetStateValidator.Validate(ValidSnapshot(servers), Now).IsValid);
    }

    [Fact]
    public void Empty_server_id_fails()
    {
        var snapshot = ValidSnapshot(ValidServer() with { Id = Guid.Empty });
        Assert.Equal(WidgetValidationFailure.EmptyServerId,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Fact]
    public void Unsanitized_display_name_fails()
    {
        var snapshot = ValidSnapshot(ValidServer() with { DisplayName = "bad" + (char)0x07 + "name" });
        Assert.Equal(WidgetValidationFailure.DisplayNameNotSanitized,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Fact]
    public void Oversized_display_name_fails()
    {
        var snapshot = ValidSnapshot(ValidServer() with
        {
            DisplayName = new string('x', WidgetSchema.MaxDisplayNameLength + 1)
        });
        Assert.Equal(WidgetValidationFailure.DisplayNameNotSanitized,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Fact]
    public void Undefined_health_enum_fails()
    {
        var snapshot = ValidSnapshot(ValidServer() with { Health = (WidgetHealth)99 });
        Assert.Equal(WidgetValidationFailure.UndefinedHealth,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Fact]
    public void Undefined_overall_health_fails()
    {
        var snapshot = ValidSnapshot() with { OverallHealth = (WidgetHealth)42 };
        Assert.Equal(WidgetValidationFailure.UndefinedOverallHealth,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Out_of_range_metric_fails(double value)
    {
        var snapshot = ValidSnapshot(ValidServer() with { CpuUsagePercent = value });
        Assert.Equal(WidgetValidationFailure.MetricOutOfRange,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(100.0)]
    [InlineData(null)]
    public void Boundary_and_null_metrics_are_valid(double? value)
    {
        var snapshot = ValidSnapshot(ValidServer() with
        {
            CpuUsagePercent = value,
            MemoryUsagePercent = value,
            DiskUsagePercent = value
        });
        Assert.True(WidgetStateValidator.Validate(snapshot, Now).IsValid);
    }

    [Fact]
    public void Generated_timestamp_before_floor_fails()
    {
        var snapshot = ValidSnapshot() with { GeneratedAtUtc = WidgetSchema.MinTimestampUtc };
        Assert.Equal(WidgetValidationFailure.GeneratedTimestampOutOfRange,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Fact]
    public void Generated_timestamp_far_in_future_fails()
    {
        var snapshot = ValidSnapshot() with { GeneratedAtUtc = Now.AddHours(1) };
        Assert.Equal(WidgetValidationFailure.GeneratedTimestampOutOfRange,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Fact]
    public void Generated_timestamp_within_skew_is_valid()
    {
        var snapshot = ValidSnapshot() with { GeneratedAtUtc = Now.AddMinutes(1) };
        Assert.True(WidgetStateValidator.Validate(snapshot, Now).IsValid);
    }

    [Fact]
    public void LastUpdated_out_of_range_fails()
    {
        var snapshot = ValidSnapshot(ValidServer() with { LastUpdatedUtc = Now.AddDays(1) });
        Assert.Equal(WidgetValidationFailure.LastUpdatedOutOfRange,
            WidgetStateValidator.Validate(snapshot, Now).Failure);
    }

    [Fact]
    public void Deserialized_then_validated_end_to_end()
    {
        var bytes = WidgetStateSerializer.SerializeToUtf8Bytes(ValidSnapshot());
        var restored = WidgetStateSerializer.TryDeserialize(bytes);
        Assert.True(WidgetStateValidator.Validate(restored, Now).IsValid);
    }
}
