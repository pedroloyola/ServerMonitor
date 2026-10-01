using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (UI.1.7): a harness must never read or write the user's real
/// <c>%LOCALAPPDATA%\ServerMonitor\window-placement.json</c>. This re-roots the placement file at a
/// per-harness temp directory exactly the way <see cref="QaProxyJumpComposition"/> does - by registering a
/// <see cref="WindowPlacementStorageOptions"/> after the production one, so it wins for every resolve while
/// the real <see cref="JsonWindowPlacementStore"/> and window adapter run unchanged.
/// Excluded from Release (see ServerMonitor.App.csproj).
/// </summary>
internal static class QaWindowPlacementIsolation
{
    public static string DirectoryFor(string harness) =>
        Path.Combine(Path.GetTempPath(), "ServerMonitor-QA", harness);

    /// <summary>Registered last so it wins over the production registration for every resolve.</summary>
    public static void Apply(IServiceCollection services, string harness)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);

        services.AddSingleton(new WindowPlacementStorageOptions
        {
            FilePath = Path.Combine(DirectoryFor(harness), "window-placement.json")
        });
    }
}
