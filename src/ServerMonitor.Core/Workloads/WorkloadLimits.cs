namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Bounds that keep a workload snapshot small regardless of how large (or hostile) a server's raw
/// output is (§44). A parser that hits a list cap sets the corresponding <c>Truncated</c> flag rather
/// than returning a silently partial result. Shared by the infrastructure parsers and the Core models.
/// </summary>
public static class WorkloadLimits
{
    /// <summary>Maximum containers materialized into a <see cref="DockerSnapshot"/>.</summary>
    public const int MaxContainers = 512;

    /// <summary>Maximum services materialized into a <see cref="ServiceSnapshot"/>.</summary>
    public const int MaxServices = 2048;

    /// <summary>Maximum length of any single sanitized text field (name, image, status, id, …).</summary>
    public const int MaxTextLength = 256;
}
