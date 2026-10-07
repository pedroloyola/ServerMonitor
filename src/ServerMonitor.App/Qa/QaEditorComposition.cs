using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (UI.7 B-23): the Add / Edit server harness, <c>--qa-editor</c>. Layered on the production root after
/// <see cref="QaStartupIsolation.Apply"/> (every data path under the per-process QA root, the in-memory credential store,
/// an empty SSH profile unless <c>--qa-ssh-config</c> hands one over): the REAL server service, profile service and both
/// trust stores run on that root, so a Save or a trust accept is observable on disk; monitoring and discovery are inert;
/// the SSH service is <see cref="QaScriptedSshConnectionService"/> - a CLOSED catalogue of outcomes, never a network.
/// <list type="bullet">
/// <item><c>--qa-editor-seed=direct|routed|password</c>: one saved server for an Edit, written through the real profile
/// path by the <c>--qa-start</c> step on the UI thread (never from a hosted service during startup, where its
/// ServersChanged would reach the live, UI-bound dashboard off the UI thread). Requires <c>--qa-start</c>.</item>
/// <item><c>--qa-editor-ssh=&lt;outcome&gt;[:held|:held-at-auth]</c>: what "Testar ligação" answers (default <c>ok-linux</c>);
/// <c>:held</c> keeps the test running until Cancel or the QA release signal (a named event per process; no wall clock),
/// after its last stage; <c>:held-at-auth</c> holds the same way with authentication still running.</item>
/// <item><c>--qa-editor-save=fail|locked</c>: Save fails (a validation failure / a restore holding the configuration).</item>
/// <item><c>--qa-start=editor-add|editor-import|editor-edit:&lt;n&gt;</c> (see <see cref="QaShellStartup"/>).</item>
/// </list>
/// Every value is exact and ordinal; anything else is refused (exit 3) by <see cref="Refusal"/>. Requires
/// <c>--qa-backup</c> (Settings must never reach the real backup picker). Excluded from Release.
/// </summary>
internal static class QaEditorComposition
{
    public const string LaunchFlag = "--qa-editor";
    public const string SeedFlag = "--qa-editor-seed";
    public const string SshFlag = "--qa-editor-ssh";
    public const string SaveFlag = "--qa-editor-save";
    public const string HeldSuffix = ":held";
    public const string HeldAtAuthSuffix = ":held-at-auth";

    public static IReadOnlyList<string> Seeds { get; } = ["direct", "routed", "password"];

    public static IReadOnlyList<string> SaveFailures { get; } = ["fail", "locked"];

    public static IReadOnlyList<string> Modifiers { get; } = [SeedFlag, SshFlag, SaveFlag];

    public static bool IsRequested() => IsRequested(Environment.GetCommandLineArgs());

    internal static bool IsRequested(IReadOnlyList<string> args) => args.Contains(LaunchFlag, StringComparer.Ordinal);

    /// <summary>Null when the editor modifiers are well-formed (or absent); the refusal text otherwise.</summary>
    internal static string? Refusal(IReadOnlyList<string> args)
    {
        var present = Modifiers.Where(flag => QaShellStartup.Present(args, flag)).ToList();
        if (!IsRequested(args))
        {
            return present.Count == 0 ? null : $"{string.Join(", ", present)} only apply to {LaunchFlag}.";
        }

        if (QaBackupPolicy.ResolveScenario(args, isDebugBuild: true) is null)
        {
            return $"{LaunchFlag} requires {QaBackupPolicy.LaunchFlag} <{string.Join("|", QaBackupPolicy.Scenarios)}>: " +
                "Settings must never reach the real backup picker.";
        }

        if (QaShellStartup.Present(args, SeedFlag) && Seed(args) is null)
        {
            return $"{SeedFlag} needs exactly one of: {string.Join(", ", Seeds)} (as {SeedFlag}=<value>).";
        }

        if (QaShellStartup.Present(args, SeedFlag) && !QaShellStartup.Present(args, QaShellStartup.StartFlag))
        {
            return $"{SeedFlag} is written by the {QaShellStartup.StartFlag} step: add {QaShellStartup.StartFlag}=editor-*.";
        }

        if (QaShellStartup.Present(args, SshFlag) && SshScript(args) is null)
        {
            return $"{SshFlag} needs exactly one of: {string.Join(", ", QaScriptedSshConnectionService.Outcomes)} " +
                $"(optionally {HeldSuffix} or {HeldAtAuthSuffix}), as {SshFlag}=<value>.";
        }

        if (QaShellStartup.Present(args, SaveFlag) && SaveFailure(args) is null)
        {
            return $"{SaveFlag} needs exactly one of: {string.Join(", ", SaveFailures)} (as {SaveFlag}=<value>).";
        }

        return null;
    }

    internal static string? Seed(IReadOnlyList<string> args) =>
        QaShellStartup.Value(args, SeedFlag) is { } value && Seeds.Contains(value, StringComparer.Ordinal) ? value : null;

    internal static string? SaveFailure(IReadOnlyList<string> args) =>
        QaShellStartup.Value(args, SaveFlag) is { } value && SaveFailures.Contains(value, StringComparer.Ordinal) ? value : null;

    /// <summary>
    /// The scripted outcome and how it is held (after its last stage, or with authentication running); null for an unknown
    /// value. Absent = ok-linux, not held.
    /// </summary>
    internal static (string Outcome, bool Held, bool HeldAtAuth)? SshScript(IReadOnlyList<string> args)
    {
        if (!QaShellStartup.Present(args, SshFlag))
        {
            return ("ok-linux", false, false);
        }

        if (QaShellStartup.Value(args, SshFlag) is not { } value)
        {
            return null;
        }

        var heldAtAuth = value.EndsWith(HeldAtAuthSuffix, StringComparison.Ordinal);
        var held = !heldAtAuth && value.EndsWith(HeldSuffix, StringComparison.Ordinal);
        var outcome = heldAtAuth ? value[..^HeldAtAuthSuffix.Length] : held ? value[..^HeldSuffix.Length] : value;
        return QaScriptedSshConnectionService.Outcomes.Contains(outcome, StringComparer.Ordinal) ? (outcome, held, heldAtAuth) : null;
    }

    /// <summary>How many saved servers the launch seeds (the range of <c>editor-edit:&lt;n&gt;</c>).</summary>
    internal static int SeededCount(IReadOnlyList<string> args) => Seed(args) is null ? 0 : 1;

    public static void Apply(IServiceCollection services) => Apply(services, Environment.GetCommandLineArgs());

    /// <summary>Registered last, after the isolation re-root, so it wins for every resolve. Throws for a malformed launch.</summary>
    internal static void Apply(IServiceCollection services, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (Refusal(args) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        QaWindowPlacementIsolation.Apply(services, "editor");

        // Inert data plane: nothing schedules, collects or discovers.
        services.AddSingleton<IMonitoringEngine, QaMonitoringEngine>();
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService([]));
        services.AddSingleton<IServerMonitoringStateStore>(new ServerMonitoringStateStore());

        // The scripted SSH service consults the REAL trust stores (on the QA root), so the two-step trust flow is visible.
        var (outcome, held, heldAtAuth) = SshScript(args)!.Value;
        services.AddSingleton(sp => new QaScriptedSshConnectionService(
            outcome,
            held,
            sp.GetRequiredService<IHostKeyTrustStore>(),
            sp.GetRequiredService<IRoutedHostKeyTrustStore>(),
            heldAtAuth));
        services.AddSingleton<ISshConnectionService>(sp => sp.GetRequiredService<QaScriptedSshConnectionService>());

        // The seed is written through the REAL profile path; a scripted save failure only wraps what the editor uses.
        services.AddSingleton(sp => new QaEditorSeed(
            Seed(args),
            new ServerProfileService(
                sp.GetRequiredService<IServerService>(),
                sp.GetRequiredService<IServerCredentialStore>(),
                sp.GetRequiredService<IConfigurationWriteGate>()),
            QaStartupIsolation.DefaultRoot()));
        if (SaveFailure(args) is { } failure)
        {
            services.AddSingleton<IServerProfileService>(sp => new QaFailingProfileService(
                failure,
                new ServerProfileService(
                    sp.GetRequiredService<IServerService>(),
                    sp.GetRequiredService<IServerCredentialStore>(),
                    sp.GetRequiredService<IConfigurationWriteGate>())));
        }
    }
}

/// <summary>
/// QA-ONLY: one saved server for an Edit, written ONCE through the real profile service on the QA root - by the
/// <c>--qa-start</c> step, on the UI thread, after the shell is up.
/// </summary>
internal sealed class QaEditorSeed(string? seed, IServerProfileService profiles, string root)
{
    private Task? _written;

    /// <summary>Writes the seed the first time it is asked (nothing without one); later calls return the same task.</summary>
    public Task EnsureWrittenAsync(CancellationToken cancellationToken = default) =>
        _written ??= seed is null ? Task.CompletedTask : QaEditorSeedData.WriteAsync(seed, profiles, root, cancellationToken);
}

/// <summary>QA-ONLY: Save answers a validation failure ("fail") or a restore holding the configuration ("locked").</summary>
internal sealed class QaFailingProfileService(string failure, IServerProfileService inner) : IServerProfileService
{
    public Task<ServerOperationResult> AddAsync(ServerProfileInput input, CancellationToken cancellationToken = default) => Fail();

    public Task<ServerOperationResult> UpdateAsync(Core.Models.Server existingServer, ServerProfileInput input, CancellationToken cancellationToken = default) => Fail();

    public Task<bool> RemoveAsync(Core.Models.Server server, CancellationToken cancellationToken = default) => inner.RemoveAsync(server, cancellationToken);

    private Task<ServerOperationResult> Fail() => failure == "locked"
        ? Task.FromException<ServerOperationResult>(new ConfigurationLockedException())
        : Task.FromResult(ServerOperationResult.Failure(new ServerValidationError("Id", ServerValidationErrorCode.ServerNotFound)));
}
