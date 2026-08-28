namespace ServerMonitor.App.Services;

/// <summary>Supplies the running application's version for display (Settings / About).</summary>
public interface IAppVersionProvider
{
    /// <summary>Product version as <c>Major.Minor.Build</c>, e.g. <c>1.0.0</c>.</summary>
    string DisplayVersion { get; }

    /// <summary>True when the version came from the MSIX package identity (packaged run).</summary>
    bool IsPackaged { get; }
}

/// <summary>
/// Reads the real version at runtime: the MSIX package identity when packaged, falling back to the
/// app assembly version when unpackaged/dev (where <c>Package.Current</c> throws). The resolution
/// core is delegate-injected so the packaged/unpackaged branches are unit-testable without a real
/// package (M12/ADR-017 §7; §104 — never crash when Package.Current is unavailable).
/// </summary>
public sealed class AppVersionProvider : IAppVersionProvider
{
    private readonly Lazy<(string Version, bool Packaged)> _info;

    public AppVersionProvider()
        : this(ReadPackageVersion, () => typeof(AppVersionProvider).Assembly.GetName().Version)
    {
    }

    internal AppVersionProvider(Func<Version?> packageVersion, Func<Version?> assemblyVersion)
    {
        _info = new Lazy<(string, bool)>(() => Resolve(packageVersion, assemblyVersion));
    }

    public string DisplayVersion => _info.Value.Version;

    public bool IsPackaged => _info.Value.Packaged;

    internal static (string Version, bool Packaged) Resolve(
        Func<Version?> packageVersion,
        Func<Version?> assemblyVersion)
    {
        var packaged = SafeInvoke(packageVersion);
        if (packaged is not null)
        {
            return (Format(packaged), true);
        }

        var assembly = SafeInvoke(assemblyVersion) ?? new Version(0, 0, 0);
        return (Format(assembly), false);
    }

    internal static string Format(Version version) =>
        $"{Math.Max(version.Major, 0)}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";

    private static Version? SafeInvoke(Func<Version?> source)
    {
        try
        {
            return source();
        }
        catch
        {
            // Package.Current throws when unpackaged; any failure falls back to the next source.
            return null;
        }
    }

    private static Version? ReadPackageVersion()
    {
        var version = global::Windows.ApplicationModel.Package.Current.Id.Version;
        return new Version(version.Major, version.Minor, version.Build, version.Revision);
    }
}
