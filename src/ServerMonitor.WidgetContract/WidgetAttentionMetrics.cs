namespace ServerMonitor.WidgetContract;

/// <summary>The metric behind a Warning/Critical health (UI.9 D-UI9-2), as readers use it internally.</summary>
public enum WidgetAttentionMetric
{
    Cpu,
    Memory,
    Disk
}

/// <summary>
/// The wire allowlist for <see cref="WidgetServerState.AttentionMetric"/>. The wire value is a string so an
/// unknown value degrades to "no reason" instead of failing the snapshot (an unknown STJ enum would make
/// the whole file unreadable). Parsing is ordinal and exact: case, whitespace or any other text is not a
/// match. The raw string never reaches rendered output — readers keep only the parsed enum (V-RC-6).
/// </summary>
public static class WidgetAttentionMetrics
{
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Disk = "disk";

    /// <summary>The exact wire value for a metric.</summary>
    public static string ToWire(WidgetAttentionMetric metric) => metric switch
    {
        WidgetAttentionMetric.Cpu => Cpu,
        WidgetAttentionMetric.Memory => Memory,
        WidgetAttentionMetric.Disk => Disk,
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    /// <summary>Parses an allowlisted wire value; anything else (including <c>null</c>) yields <c>null</c>.</summary>
    public static WidgetAttentionMetric? TryParse(string? value) => value switch
    {
        Cpu => WidgetAttentionMetric.Cpu,
        Memory => WidgetAttentionMetric.Memory,
        Disk => WidgetAttentionMetric.Disk,
        _ => null
    };
}
