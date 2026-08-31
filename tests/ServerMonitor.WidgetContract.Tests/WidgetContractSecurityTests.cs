using System.Text.Json;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.WidgetContract.Tests;

/// <summary>
/// Guards the data-minimization invariant (§9) at the wire boundary. The contract's TYPES must have no
/// field capable of carrying an infrastructure identifier or secret, and a serialized snapshot must
/// expose only the allowed property names — so nothing sensitive can leak even if a future caller tries.
/// </summary>
public sealed class WidgetContractSecurityTests
{
    private static readonly string[] ForbiddenPropertyFragments =
    {
        "host", "ip", "address", "port", "user", "username", "login",
        "password", "secret", "credential", "key", "privatekey", "token",
        "command", "service", "container", "process", "os", "hostname", "raw"
    };

    [Fact]
    public void ServerState_type_exposes_only_allowed_properties()
    {
        var allowed = new[]
        {
            nameof(WidgetServerState.Id),
            nameof(WidgetServerState.DisplayName),
            nameof(WidgetServerState.Health),
            nameof(WidgetServerState.CpuUsagePercent),
            nameof(WidgetServerState.MemoryUsagePercent),
            nameof(WidgetServerState.DiskUsagePercent),
            // M13 redesign: absolute memory/disk sizes + uptime — low-sensitivity RESOURCE metrics shown
            // on the Large widget. Deliberately allowed (NOT host/network/credential/OS — §9 still holds).
            nameof(WidgetServerState.MemoryUsedGb),
            nameof(WidgetServerState.MemoryTotalGb),
            nameof(WidgetServerState.DiskUsedGb),
            nameof(WidgetServerState.DiskTotalGb),
            nameof(WidgetServerState.UptimeSeconds),
            nameof(WidgetServerState.LastUpdatedUtc)
        };

        var actual = typeof(WidgetServerState)
            .GetProperties()
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .ToArray();

        Assert.Equal(allowed.OrderBy(n => n), actual.OrderBy(n => n));
    }

    [Fact]
    public void No_contract_property_name_matches_a_sensitive_fragment()
    {
        var types = new[] { typeof(WidgetStateSnapshot), typeof(WidgetServerState) };
        foreach (var type in types)
        {
            foreach (var property in type.GetProperties())
            {
                if (property.Name is "EqualityContract")
                {
                    continue;
                }

                var lower = property.Name.ToLowerInvariant();
                foreach (var fragment in ForbiddenPropertyFragments)
                {
                    // Allow benign substrings: "Health"/"DisplayName" don't contain any fragment; the
                    // fragments are chosen to only hit genuine infra identifiers.
                    Assert.False(lower.Contains(fragment),
                        $"{type.Name}.{property.Name} contains forbidden fragment '{fragment}'.");
                }
            }
        }
    }

    [Fact]
    public void Serialized_snapshot_leaks_no_sensitive_keys()
    {
        // A server whose *display name* deliberately contains hostile-looking substrings must not cause
        // any sensitive JSON KEY to appear — the name is a value, never a key, and no other field exists.
        var snapshot = new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
            OverallHealth = WidgetHealth.Healthy,
            Servers = new[]
            {
                new WidgetServerState
                {
                    Id = Guid.NewGuid(),
                    DisplayName = "prod",
                    Health = WidgetHealth.Healthy,
                    CpuUsagePercent = 10,
                    MemoryUsagePercent = 20,
                    DiskUsagePercent = 30,
                    LastUpdatedUtc = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero)
                }
            }
        };

        var json = WidgetStateSerializer.Serialize(snapshot);
        using var document = JsonDocument.Parse(json);

        var keys = CollectKeys(document.RootElement).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = new[]
        {
            "schemaVersion", "generatedAtUtc", "overallHealth", "servers",
            "id", "displayName", "health", "cpuUsagePercent", "memoryUsagePercent",
            "diskUsagePercent", "memoryUsedGb", "memoryTotalGb", "diskUsedGb", "diskTotalGb",
            "uptimeSeconds", "lastUpdatedUtc"
        };

        Assert.Equal(expected.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
            keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> CollectKeys(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in CollectKeys(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in CollectKeys(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
