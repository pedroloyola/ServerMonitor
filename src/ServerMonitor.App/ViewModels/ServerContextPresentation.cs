using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

/// <summary>Shared server-context text for the UI.3 screens (History selector, Workloads header).</summary>
public static class ServerContextPresentation
{
    /// <summary>
    /// The detected OS ("Ubuntu 24.04 LTS") from the last metrics snapshot, or <c>null</c> when the
    /// collector has not reported one — callers omit the segment rather than invent a value.
    /// </summary>
    public static string? OperatingSystemDisplay(ServerMetricsSnapshot? snapshot)
    {
        var name = snapshot?.OperatingSystemName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var version = snapshot!.OperatingSystemVersion?.Trim();
        return string.IsNullOrEmpty(version) || name.Contains(version, StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name} {version}";
    }

    /// <summary>Joins the non-empty segments with the screens' " · " separator.</summary>
    public static string Join(params string?[] segments) =>
        string.Join(" · ", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
}
