using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (UI.1.7): a harness must never read or write the user's real
/// <c>%LOCALAPPDATA%\ServerMonitor\window-placement.json</c>. This re-roots the placement file at a
/// per-harness temp directory exactly the way <see cref="QaProxyJumpComposition"/> does - by registering a
/// <see cref="WindowPlacementStorageOptions"/> after the production one, so it wins for every resolve while
/// the real <see cref="JsonWindowPlacementStore"/> and window adapter run unchanged.
/// <para>
/// UI.2 G-5 (deterministic screenshots): every run starts from the DEFAULT placement - a placement file left by an
/// earlier run of the same harness is deleted first. The delete is fail-closed to the QA temp root: it refuses any
/// path outside <see cref="Root"/>.
/// </para>
/// Excluded from Release (see ServerMonitor.App.csproj).
/// </summary>
internal static class QaWindowPlacementIsolation
{
    public const string FileName = "window-placement.json";

    /// <summary>The only folder the isolation ever writes or deletes in.</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), "ServerMonitor-QA");

    public static string DirectoryFor(string harness) => Path.Combine(Root, harness);

    /// <summary>Registered last so it wins over the production registration for every resolve.</summary>
    public static void Apply(IServiceCollection services, string harness)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);

        var path = Path.Combine(DirectoryFor(harness), FileName);
        DeleteStalePlacement(path);
        services.AddSingleton(new WindowPlacementStorageOptions { FilePath = path });
    }

    /// <summary>Deletes a stale placement file - only ever under <see cref="Root"/>.</summary>
    internal static void DeleteStalePlacement(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"QA placement isolation refuses to delete outside {root}: {full}");
        }

        if (File.Exists(full))
        {
            File.Delete(full);
        }
    }
}
