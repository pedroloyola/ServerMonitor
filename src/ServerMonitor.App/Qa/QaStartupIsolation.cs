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
internal static partial class QaStartupIsolation
{
    /// <summary>Exit code of a refused launch (the harness-error code the gallery also uses).</summary>
    public const int RefusedExitCode = 3;

    /// <summary>The isolated data harnesses: the only flags that select a QA composition of the application.</summary>
    public static IReadOnlyList<string> HarnessFlags { get; } =
    [
        QaHealthComposition.LaunchFlag,
        QaDiscoveryComposition.LaunchFlag,
        QaNotificationComposition.LaunchFlag,
        QaCompactComposition.LaunchFlag,
        QaHistoryComposition.LaunchFlag,
        QaWorkloadsComposition.LaunchFlag,
        QaStoreScreenshotComposition.LaunchFlag,
        QaProxyJumpPolicy.LaunchFlag,
        QaOverviewComposition.LaunchFlag
    ];

    /// <summary>The modifiers a harness may carry, each as <c>flag value</c> or <c>flag=value</c>.</summary>
    public static IReadOnlyList<string> ModifierFlags { get; } =
    [
        QaSshConfigProfilePolicy.LaunchFlag,
        QaUiLanguagePolicy.LaunchFlag,
        QaBackupPolicy.LaunchFlag,
        QaProxyJumpPolicy.DirectoryFlag,
        QaOverviewScenarioPolicy.LaunchFlag
    ];

    /// <summary>
    /// THE harness parser (Vigil M-1A-1): the composition root's qaMode, the launch refusal, the launch-time guard and the
    /// startup markers all use it, so no argument can be "a harness" for one and "production" for another. Exact and
    /// ordinal: <c>--qa-health=1</c>, <c>--qa-health:x</c>, <c>--QA-HEALTH</c> are not harness flags (and are refused).
    /// The one documented value form is <c>--qa-compact:&lt;digits&gt;</c>.
    /// </summary>
    internal static bool IsHarnessArgument(string argument) =>
        HarnessFlags.Contains(argument, StringComparer.Ordinal) || CompactCountForm().IsMatch(argument);

    /// <summary>True for the launches that select a Debug harness composition (the composition root's qaMode).</summary>
    public static bool IsHarnessLaunch() => IsHarnessLaunch(Environment.GetCommandLineArgs());

    internal static bool IsHarnessLaunch(IReadOnlyList<string> commandLineArgs) => commandLineArgs.Any(IsHarnessArgument);

    /// <summary>Any argument that looks like a QA switch, in any case or form (the refusal and the guard key on this).</summary>
    internal static bool IsQaLike(string argument) => argument.StartsWith("--qa", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs in the App constructor before the host is built. A launch whose <c>--qa*</c> arguments are not exactly an
    /// isolated harness plus well-formed modifiers would run - or might run - the PRODUCTION composition (real data,
    /// Credential Manager, SSH), so the process ends here (exit 3) instead.
    /// </summary>
    public static void RefuseUnisolatedLaunch()
    {
        if (LaunchRefusal(Environment.GetCommandLineArgs()) is { } refusal)
        {
            System.Diagnostics.Debug.WriteLine("QA launch refused: " + refusal);
            Console.Error.WriteLine("QA launch refused: " + refusal);
            Environment.Exit(RefusedExitCode);
        }
    }

    /// <summary>
    /// Null when the launch is allowed: no <c>--qa*</c> argument at all (production); an exact gallery flag (the exclusive
    /// gallery owns its options and refuses its own misuse - QaGalleryPolicy - and it short-circuits before this runs); or
    /// at least one exact harness flag with every other <c>--qa*</c> argument an exact harness flag or a well-formed
    /// modifier. Anything else - a modifier alone, an unknown switch, a malformed or differently-cased flag - is refused.
    /// </summary>
    internal static string? LaunchRefusal(IReadOnlyList<string> commandLineArgs)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);
        var qa = commandLineArgs.Where(IsQaLike).ToList();
        if (qa.Count == 0
            || qa.Any(argument => argument == QaGalleryPolicy.ComponentsFlag || argument == QaGalleryPolicy.TokensFlag))
        {
            return null;
        }

        var unknown = qa.FirstOrDefault(argument => !IsHarnessArgument(argument) && !IsModifierArgument(argument));
        if (unknown is not null)
        {
            return $"'{unknown}' is not a recognised QA switch (exact, lower-case: {string.Join(", ", HarnessFlags)}; " +
                $"modifiers {string.Join(", ", ModifierFlags)} as 'flag value' or 'flag=value'). Refusing rather than " +
                "risk the real composition.";
        }

        if (!qa.Any(IsHarnessArgument))
        {
            return $"{qa[0].Split('=')[0]} is not an isolated QA harness: on its own it would run the real composition " +
                $"(real user data, Credential Manager, SSH). Combine it with one of: {string.Join(", ", HarnessFlags)}.";
        }

        // UI.4 (Cortex r1 NIT-4): the scenario modifier belongs to --qa-overview only; next to another harness it would be
        // silently ignored, so it is refused.
        if (QaOverviewScenarioPolicy.IsPresent(commandLineArgs) && !commandLineArgs.Contains(QaOverviewComposition.LaunchFlag, StringComparer.Ordinal))
        {
            return $"{QaOverviewScenarioPolicy.LaunchFlag} only applies to {QaOverviewComposition.LaunchFlag}.";
        }

        // UI.4: an unknown overview scenario is refused here (exit 3), before anything is composed - never another scenario.
        if (QaOverviewScenarioPolicy.IsPresent(commandLineArgs)
            && QaOverviewScenarioPolicy.ResolveScenario(commandLineArgs, isDebugBuild: true) is null)
        {
            return $"{QaOverviewScenarioPolicy.LaunchFlag} needs one of: {string.Join(", ", QaOverviewScenarioPolicy.Scenarios)}.";
        }

        return null;
    }

    private static bool IsModifierArgument(string argument) =>
        ModifierFlags.Any(flag => argument == flag || (argument.StartsWith(flag + "=", StringComparison.Ordinal) && argument.Length > flag.Length + 1));

    [System.Text.RegularExpressions.GeneratedRegex(@"^--qa-compact:\d{1,4}$")]
    private static partial System.Text.RegularExpressions.Regex CompactCountForm();

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
        // Keyed on ANY --qa* argument, not only an exact harness: should the refusal ever be bypassed, a malformed QA
        // launch that fell through to the production composition still dies here, before the host starts.
        if (!Environment.GetCommandLineArgs().Any(IsQaLike))
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

        // Any store is acceptable except the native Credential Manager one, wherever the composition exposes it.
        if (services.GetService<UngatedCredentialStore>() is { Store: WindowsCredentialStore })
        {
            violations.Add("UngatedCredentialStore = Windows Credential Manager");
        }

        if (services.GetService<IServerCredentialStore>() is WindowsCredentialStore)
        {
            violations.Add("IServerCredentialStore = Windows Credential Manager");
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "QA isolation refused the launch: the harness composition reaches real user data." + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
        }
    }

    /// <summary>The options the production root always registers; a composition missing one is refused.</summary>
    internal static IReadOnlyList<Type> RequiredOptions { get; } =
    [
        typeof(ServerStorageOptions),
        typeof(HostKeyTrustStorageOptions),
        typeof(RoutedHostKeyTrustStorageOptions),
        typeof(BackgroundSettingsStorageOptions),
        typeof(NotificationSettingsStorageOptions),
        typeof(WindowPlacementStorageOptions)
    ];

    /// <summary>
    /// Every path the composition carries, as it resolves it (no store is constructed). DISCOVERED, not listed (Vigil
    /// L-1A): every non-generic class named <c>*Options</c> in a ServerMonitor assembly that the container resolves
    /// contributes each public string property named <c>*Path</c> or <c>*Directory</c>; a new options type is checked
    /// without anyone remembering to add it here. The SSH sources and the key picker carry their roots elsewhere and are
    /// read explicitly.
    /// </summary>
    internal static IEnumerable<(string Name, string? Path)> ResolvedPaths(IServiceProvider services) =>
        ResolvedPaths(services, AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("ServerMonitor", StringComparison.Ordinal) == true));

    internal static IEnumerable<(string Name, string? Path)> ResolvedPaths(
        IServiceProvider services,
        IEnumerable<System.Reflection.Assembly> assemblies)
    {
        var optionTypes = assemblies
            .SelectMany(LoadableTypes)
            .Concat(RequiredOptions)
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false, ContainsGenericParameters: false }
                && type.Name.EndsWith("Options", StringComparison.Ordinal))
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (var type in optionTypes)
        {
            var instance = services.GetService(type);
            if (instance is null)
            {
                if (RequiredOptions.Contains(type))
                {
                    yield return ($"{type.Name} (not registered)", null);
                }

                continue;
            }

            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0
                    && (property.Name.EndsWith("Path", StringComparison.Ordinal) || property.Name.EndsWith("Directory", StringComparison.Ordinal)))
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                yield return ($"{type.Name}.{property.Name}", (string?)property.GetValue(instance));
            }
        }

        if (services.GetService<ISshConfigImportSource>() is SshConfigFileImportSource import)
        {
            yield return ($"{nameof(SshConfigFileImportSource)}.{nameof(import.ConfigPath)}", import.ConfigPath);
        }

        if (services.GetService<ILocalSshKeyDiscovery>() is LocalSshKeyDiscovery keys)
        {
            yield return ($"{nameof(LocalSshKeyDiscovery)}.{nameof(keys.SshDirectory)}", keys.SshDirectory);
        }

        if (services.GetService<IPrivateKeyFilePicker>() is PrivateKeyFilePicker picker)
        {
            // A null profile means the picker opens in the real user profile.
            yield return ($"{nameof(PrivateKeyFilePicker)}.{nameof(picker.UserProfile)}", picker.UserProfile);
        }
    }

    private static IEnumerable<Type> LoadableTypes(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
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
