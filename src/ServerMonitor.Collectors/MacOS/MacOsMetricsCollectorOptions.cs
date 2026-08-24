namespace ServerMonitor.Collectors.MacOS;

public sealed record MacOsMetricsCollectorOptions
{
    public static readonly MacOsMetricsCollectorOptions Default = new();

    // macOS CPU sampling uses "top -l 2", which self-samples over ~1s, so the
    // deadline is a little wider than the Linux collector's.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}
