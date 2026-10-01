using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (UI.3 gate 1A). Every Debug <c>--qa-*</c> harness composition starts from the production root, which registers
/// the real <c>*StorageOptions.ForCurrentUser()</c>, the Windows Credential Manager and the real <c>~/.ssh</c> sources. Most
/// harnesses replaced only their data plane, so a harness launch still resolved the real <c>%LOCALAPPDATA%\ServerMonitor</c>
/// files - <c>OnLaunched</c> ran the known-host orphan cleanup on the real trust path and the settings services read the
/// real settings files. Two halves close that:
/// <list type="bullet">
/// <item><see cref="Apply"/> re-roots every production data path at a per-process folder under the QA root, swaps the
/// credential store for a process-local one and points the SSH import/key sources at an empty profile. It is registered
/// BEFORE the harness, so a harness with its own directory (<c>--qa-proxyjump-dir</c>, <c>--qa-ssh-config</c>) still
/// wins.</item>
/// <item><see cref="VerifyOrThrow(IServiceProvider)"/> runs in the App constructor before the host starts and aborts the
/// launch when any resolved path is outside the allowed roots, under a real data folder, or crosses a reparse point.
/// It does not depend on <see cref="Apply"/> having run: a harness composition that lost its re-root fails here.</item>
/// </list>
/// Nothing is created, read or deleted here; the folder is created lazily by the stores on first write.
/// Excluded from Release (see ServerMonitor.App.csproj).
/// </summary>
internal static class QaStartupIsolation
{
    /// <summary>True for the launches that select a Debug harness composition (the composition root's qaMode).</summary>
    public static bool IsHarnessLaunch() =>
        QaHealthComposition.IsRequested()
        || QaDiscoveryComposition.IsRequested()
        || QaNotificationComposition.IsRequested()
        || QaCompactComposition.IsRequested()
        || QaHistoryComposition.IsRequested()
        || QaWorkloadsComposition.IsRequested()
        || QaStoreScreenshotComposition.IsRequested()
        || QaProxyJumpComposition.IsRequested();

    /// <summary>The per-process folder every re-rooted path lives in.</summary>
    public static string DefaultRoot() => Path.Combine(
        QaWindowPlacementIsolation.Root,
        "isolated",
        Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Registered before the harness composition, so a harness that brings its own directory wins.</summary>
    public static void Apply(IServiceCollection services, string root, bool rerootSshProfile)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("The QA isolation root must be an absolute path.", nameof(root));
        }

        var trust = new HostKeyTrustStorageOptions { FilePath = Path.Combine(root, "known-hosts.json") };
        services.AddSingleton(new ServerStorageOptions { FilePath = Path.Combine(root, "servers.json") });
        services.AddSingleton(trust);
        services.AddSingleton(RoutedHostKeyTrustStorageOptions.From(trust));
        services.AddSingleton(new BackgroundSettingsStorageOptions { FilePath = Path.Combine(root, "background-settings.json") });
        services.AddSingleton(new NotificationSettingsStorageOptions { FilePath = Path.Combine(root, "notification-settings.json") });
        services.AddSingleton(new WindowPlacementStorageOptions { FilePath = Path.Combine(root, "window-placement.json") });

        // The raw store is swapped; ordinary callers still get it through the gated decorator (M14.6 §5.5).
        services.AddSingleton(new UngatedCredentialStore(new QaInMemoryCredentialStore()));

        if (rerootSshProfile)
        {
            // An empty profile: import finds no config and key discovery finds no keys - never the real ~/.ssh.
            var profile = Path.Combine(root, "profile");
            services.AddSingleton<ISshConfigImportSource>(new SshConfigFileImportSource(profile));
            services.AddSingleton<ILocalSshKeyDiscovery>(new LocalSshKeyDiscovery(profile));
            services.AddSingleton<IPrivateKeyFilePicker>(sp =>
                new PrivateKeyFilePicker(sp.GetRequiredService<IWindowContext>(), profile));
        }
    }

    /// <summary>The launch-time check: a harness launch whose composition reaches real data aborts before the host starts.</summary>
    public static void VerifyOrThrow(IServiceProvider services)
    {
        if (!IsHarnessLaunch())
        {
            return;
        }

        VerifyOrThrow(services, AllowedRoots(), RealDataRoots());
    }

    /// <summary>
    /// Resolves every data path the composition carries (no store is constructed) and throws when one is not fully
    /// qualified, not under an allowed root, under a forbidden root, or crosses a reparse point; also when the raw
    /// credential store is the Windows Credential Manager.
    /// </summary>
    internal static void VerifyOrThrow(
        IServiceProvider services,
        IReadOnlyList<string> allowedRoots,
        IReadOnlyList<string> forbiddenRoots)
    {
        ArgumentNullException.ThrowIfNull(services);
        var violations = new List<string>();
        foreach (var (name, path) in ResolvedPaths(services))
        {
            if (Violation(path, allowedRoots, forbiddenRoots) is { } reason)
            {
                violations.Add($"{name} = {path ?? "<null>"} ({reason})");
            }
        }

        if (services.GetService<UngatedCredentialStore>() is { Store: WindowsCredentialStore })
        {
            violations.Add("UngatedCredentialStore = Windows Credential Manager");
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "QA isolation refused the launch: the harness composition reaches real user data." + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
        }
    }

    /// <summary>Every path-bearing registration the production root makes, as the composition resolves it.</summary>
    internal static IEnumerable<(string Name, string? Path)> ResolvedPaths(IServiceProvider services)
    {
        yield return (nameof(ServerStorageOptions), services.GetRequiredService<ServerStorageOptions>().FilePath);
        yield return (nameof(HostKeyTrustStorageOptions), services.GetRequiredService<HostKeyTrustStorageOptions>().FilePath);
        yield return (nameof(RoutedHostKeyTrustStorageOptions), services.GetRequiredService<RoutedHostKeyTrustStorageOptions>().FilePath);
        yield return (nameof(BackgroundSettingsStorageOptions), services.GetRequiredService<BackgroundSettingsStorageOptions>().FilePath);
        yield return (nameof(NotificationSettingsStorageOptions), services.GetRequiredService<NotificationSettingsStorageOptions>().FilePath);
        yield return (nameof(WindowPlacementStorageOptions), services.GetRequiredService<WindowPlacementStorageOptions>().FilePath);

        // Registered only by the production feature modules (never in a harness today); checked if one ever appears.
        if (services.GetService<HistoryStorageOptions>() is { } history)
        {
            yield return (nameof(HistoryStorageOptions), history.DatabasePath);
        }

        if (services.GetService<IgnoredDeviceStorageOptions>() is { } ignored)
        {
            yield return (nameof(IgnoredDeviceStorageOptions), ignored.FilePath);
        }

        if (services.GetService<WidgetStateOptions>() is { } widget)
        {
            yield return (nameof(WidgetStateOptions), widget.SnapshotPath);
        }

        if (services.GetService<ISshConfigImportSource>() is SshConfigFileImportSource import)
        {
            yield return (nameof(SshConfigFileImportSource), import.ConfigPath);
        }

        if (services.GetService<ILocalSshKeyDiscovery>() is LocalSshKeyDiscovery keys)
        {
            yield return (nameof(LocalSshKeyDiscovery), keys.SshDirectory);
        }

        if (services.GetService<IPrivateKeyFilePicker>() is PrivateKeyFilePicker picker)
        {
            // A null profile means the picker opens in the real user profile.
            yield return (nameof(PrivateKeyFilePicker), picker.UserProfile);
        }
    }

    internal static string? Violation(string? path, IReadOnlyList<string> allowedRoots, IReadOnlyList<string> forbiddenRoots)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return "not an absolute path";
        }

        var full = Path.GetFullPath(path);
        if (forbiddenRoots.Any(root => IsUnder(full, root)))
        {
            return "under a real data folder";
        }

        if (!allowedRoots.Any(root => IsUnder(full, root)))
        {
            return "outside the QA roots";
        }

        return QaPathSafety.CrossesReparsePoint(full) ? "crosses a reparse point" : null;
    }

    internal static bool IsUnder(string fullPath, string root)
    {
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (fullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The QA root, plus the directories a harness flag explicitly hands over.</summary>
    private static IReadOnlyList<string> AllowedRoots()
    {
        var roots = new List<string> { QaWindowPlacementIsolation.Root };
        if (QaProxyJumpComposition.IsRequested())
        {
            roots.Add(QaProxyJumpComposition.RequiredDirectory());
        }

        if (QaSshConfigComposition.RequestedProfile() is { } profile)
        {
            roots.Add(profile);
        }

        return roots;
    }

    /// <summary>The real data folders, derived from the production options themselves (computed, never touched).</summary>
    private static IReadOnlyList<string> RealDataRoots() =>
    [
        DirectoryOf(ServerStorageOptions.ForCurrentUser().FilePath),
        DirectoryOf(HostKeyTrustStorageOptions.ForCurrentUser().FilePath),
        DirectoryOf(BackgroundSettingsStorageOptions.ForCurrentUser().FilePath),
        DirectoryOf(NotificationSettingsStorageOptions.ForCurrentUser().FilePath),
        DirectoryOf(WindowPlacementStorageOptions.ForCurrentUser().FilePath),
        DirectoryOf(HistoryStorageOptions.ForCurrentUser().DatabasePath),
        DirectoryOf(IgnoredDeviceStorageOptions.ForCurrentUser().FilePath),
        DirectoryOf(WidgetStateOptions.ForCurrentUser().SnapshotPath),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")
    ];

    private static string DirectoryOf(string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new InvalidOperationException($"No directory: {path}");
}
