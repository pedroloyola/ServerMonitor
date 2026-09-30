using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Backup;

public sealed record BackupServiceOptions
{
    /// <summary>Written into every backup as <c>appVersion</c>.</summary>
    public string AppVersion { get; init; } = "0.0.0";

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>Holds what startup recovery reported, set by the App before the host starts (spec §5.2).</summary>
public sealed class RestoreRecoveryStatus
{
    public RestoreRecoveryReport? Report { get; set; }
}

/// <summary>
/// The backup/restore engine (M14.6 §4, §5). Export: validated servers → referenced trust → credentials →
/// strict payload in a zeroing buffer → encrypt (fresh salt/nonce) → unique CreateNew temp → verify → move.
/// Inspect: decode → strict parse → domain validation → re-key → summary, all in memory. Apply: gate + drain
/// → journal (pre copies, manifest last) → new credentials (additive) → six files in the fixed order →
/// verify → COMMIT (manifest) → seal → old-credential deletion under the referential guard → journal removed.
/// Any failure up to and including verify is rolled back by the same engine startup recovery uses.
/// <para>Logs and results carry codes, counts and exception type names only (Vigil C-6). No entitlement
/// check exists on this path (C-12).</para>
/// </summary>
public sealed class ConfigurationBackupService : IConfigurationBackupService
{
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly IServerService _serverService;
    private readonly IServerValidator _validator;
    private readonly JsonServerRepository _repository;
    private readonly JsonHostKeyTrustStore _directTrust;
    private readonly JsonRoutedHostKeyTrustStore _routedTrust;
    private readonly IServerCredentialStore _credentials;
    private readonly IPortableSettingsParticipant _settings;
    private readonly IConfigurationWriteGate _gate;
    private readonly IRestoreMonitoringControl _monitoring;
    private readonly RestoreParticipantMap _map;
    private readonly BackupServiceOptions _options;
    private readonly RestoreRecoveryStatus _recoveryStatus;
    private readonly ILogger<ConfigurationBackupService> _logger;

    public ConfigurationBackupService(
        IServerService serverService,
        IServerValidator validator,
        JsonServerRepository repository,
        JsonHostKeyTrustStore directTrust,
        JsonRoutedHostKeyTrustStore routedTrust,
        UngatedCredentialStore credentials,
        IPortableSettingsParticipant settings,
        IConfigurationWriteGate gate,
        IRestoreMonitoringControl monitoring,
        ServerStorageOptions serverStorage,
        HostKeyTrustStorageOptions trustStorage,
        RoutedHostKeyTrustStorageOptions routedTrustStorage,
        BackupServiceOptions options,
        RestoreRecoveryStatus recoveryStatus,
        ILogger<ConfigurationBackupService> logger)
    {
        _serverService = serverService ?? throw new ArgumentNullException(nameof(serverService));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _directTrust = directTrust ?? throw new ArgumentNullException(nameof(directTrust));
        _routedTrust = routedTrust ?? throw new ArgumentNullException(nameof(routedTrust));
        _credentials = (credentials ?? throw new ArgumentNullException(nameof(credentials))).Store;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _recoveryStatus = recoveryStatus ?? throw new ArgumentNullException(nameof(recoveryStatus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _map = new RestoreParticipantMap(
            serverStorage,
            trustStorage,
            routedTrustStorage,
            settings.NotificationSettingsFilePath,
            settings.BackgroundSettingsFilePath);
    }

    public RestoreRecoveryReport StartupRecovery =>
        _recoveryStatus.Report ?? RestoreRecoveryReport.None(_map.JournalDirectory);

    // ---------------------------------------------------------------- test seams (never set in production)

    internal BackupFileCodec Codec { get; init; } = new();

    /// <summary>Invoked with a step name before each fault point; a test throws to fail the step there.</summary>
    internal Action<string>? FaultInjector { get; init; }

    internal Func<Guid> NewId { get; init; } = Guid.NewGuid;

    internal LocalKeyFileAccess KeyFileAccess { get; init; } = LocalKeyFileAccess.Real;

    internal TimeSpan GateDrainTimeout { get; init; } = DrainTimeout;

    private RestoreJournal Journal => new(_map.JournalDirectory);

    // ---------------------------------------------------------------- export (§4)

    public async Task<BackupExportResult> ExportAsync(
        string destinationPath,
        ReadOnlyMemory<char> passphrase,
        ReadOnlyMemory<char> confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        // Vigil L3: the real confirmation, before anything else and before any KDF.
        var problem = BackupPassphrasePolicy.ValidateForExport(passphrase.Span, confirmation.Span);
        if (problem != BackupPassphraseProblem.None)
        {
            return new BackupExportResult { PassphraseProblem = problem };
        }

        if (Journal.Exists)
        {
            return new BackupExportResult { Error = BackupError.RestorePending, PendingJournalDirectory = _map.JournalDirectory };
        }

        if (_gate.IsLocked)
        {
            return ExportFailed(BackupError.Busy, null);
        }

        BackupPayload? payload = null;
        string? temporaryFile = null;
        try
        {
            var built = await BuildExportPayloadAsync(cancellationToken);
            if (built.Error is { } buildError)
            {
                return ExportFailed(buildError, null);
            }

            payload = built.Payload!;
            byte[] file;
            using (var buffer = new ZeroingBufferWriter())
            {
                BackupPayloadSerializer.Write(buffer, payload);
                var encoded = Codec.Encrypt(buffer.WrittenSpan, passphrase.Span);
                if (!encoded.IsSuccess)
                {
                    return encoded.PassphraseProblem != BackupPassphraseProblem.None
                        ? new BackupExportResult { PassphraseProblem = encoded.PassphraseProblem }
                        : ExportFailed(encoded.Error ?? BackupError.WriteFailed, null);
                }

                file = encoded.File!;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(destinationPath);
            temporaryFile = CreateTemporaryCopy(destination, file);

            // V6: verify the temp BEFORE it replaces anything; a previous backup at the destination survives.
            FaultInjector?.Invoke("export-verify");
            if (!VerifyExport(temporaryFile, passphrase.Span, payload))
            {
                return ExportFailed(BackupError.WriteFailed, null);
            }

            FaultInjector?.Invoke("export-move");
            File.Move(temporaryFile, destination, overwrite: true);
            temporaryFile = null;

            _logger.LogInformation(
                "Configuration backup written: {ServerCount} servers, {CredentialCount} credentials, {TrustCount} trusted host keys.",
                payload.Servers.Count,
                payload.Credentials.Count,
                payload.KnownHosts.Count + payload.RoutedKnownHosts.Count);
            return new BackupExportResult { Summary = built.Summary };
        }
        catch (BackupPayloadTooLargeException exception)
        {
            return ExportFailed(BackupError.TooLarge, exception);
        }
        catch (OperationCanceledException exception)
        {
            return ExportFailed(BackupError.Canceled, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ExportFailed(BackupError.WriteFailed, exception);
        }
        finally
        {
            payload?.Dispose();
            if (temporaryFile is not null)
            {
                TryDelete(temporaryFile);
            }
        }
    }

    private async Task<(BackupPayload? Payload, BackupExportSummary? Summary, BackupError? Error)> BuildExportPayloadAsync(
        CancellationToken cancellationToken)
    {
        var loaded = await _serverService.GetAllAsync(cancellationToken);
        var servers = new List<Server>(loaded.Count);
        foreach (var server in loaded)
        {
            Server normalized;
            try
            {
                normalized = ServerNormalizer.Normalize(server);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return (null, null, BackupError.InvalidContent);
            }

            // Never produce a backup this version's own reader would reject.
            if (!BackupPayloadSerializer.IsValidServer(normalized, _validator))
            {
                return (null, null, BackupError.InvalidContent);
            }

            servers.Add(normalized);
        }

        var excludedUnreadable = await _repository.CountUnloadableAsync(cancellationToken)
            + Math.Max(0, (await _repository.GetAllAsync(cancellationToken)).Count - servers.Count);

        var referenced = TrustReferenceSet.From(servers);
        IReadOnlyList<TrustedHostKey> knownHosts;
        IReadOnlyList<TrustedRoutedHostKey> routedKnownHosts;
        int excludedTrust;
        try
        {
            var direct = await _directTrust.ExportReferencedAsync(referenced.Direct, cancellationToken);
            var routed = await _routedTrust.ExportReferencedAsync(referenced.Routed, cancellationToken);
            knownHosts = direct.Entries;
            routedKnownHosts = routed.Entries;
            excludedTrust = direct.Excluded + routed.Excluded;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Backup export stopped: a trust file is unreadable ({ExceptionType}).", exception.GetType().Name);
            return (null, null, BackupError.TrustStoreUnreadable);
        }

        var credentials = new List<BackupCredential>();
        var missing = new List<(Guid, Core.Enums.ServerCredentialKind)>();
        var missingFlags = new List<BackupCredentialFlag>();
        foreach (var server in servers)
        {
            foreach (var reference in ServerCredentialReferences.All(server))
            {
                SecretValue? secret;
                try
                {
                    secret = await _credentials.ReadAsync(reference, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Unknown is not missing (D6): a read error fails the export.
                    foreach (var credential in credentials)
                    {
                        credential.Secret.Dispose();
                    }

                    _logger.LogWarning("Backup export stopped: a credential could not be read ({ExceptionType}).", exception.GetType().Name);
                    return (null, null, BackupError.CredentialStoreUnavailable);
                }

                if (secret is null)
                {
                    missing.Add((reference.ServerId, reference.Kind));
                    missingFlags.Add(new BackupCredentialFlag(server.Id, server.Name, ServerCredentialReferences.IsJumpKind(reference.Kind)));
                    continue;
                }

                credentials.Add(new BackupCredential(reference.ServerId, reference.Kind, reference.ReferenceId, secret));
            }
        }

        var payload = new BackupPayload
        {
            CreatedAt = _options.TimeProvider.GetUtcNow(),
            AppVersion = _options.AppVersion,
            Servers = servers,
            Credentials = credentials,
            MissingCredentials = missing,
            KnownHosts = knownHosts,
            RoutedKnownHosts = routedKnownHosts,
            Settings = _settings.ReadCurrent(),
        };

        var summary = new BackupExportSummary
        {
            DirectServers = servers.Count(server => server.Route is null),
            RoutedServers = servers.Count(server => server.Route is not null),
            Credentials = credentials.Count,
            DirectTrustedHostKeys = knownHosts.Count,
            RoutedTrustedHostKeys = routedKnownHosts.Count,
            ExcludedUnreferencedTrustedHostKeys = excludedTrust,
            ExcludedUnreadableServers = excludedUnreadable,
            MissingCredentials = missingFlags,
        };

        return (payload, summary, null);
    }

    // CreateNew with an unpredictable name next to the destination: an existing user file is never clobbered.
    private static string CreateTemporaryCopy(string destination, byte[] file)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new IOException("The destination has no directory.");
        var temporaryFile = Path.Combine(
            directory,
            Path.GetFileName(destination) + "." + RandomNumberGenerator.GetHexString(16, lowercase: true) + ".tmp");
        var created = false;
        try
        {
            using var stream = new FileStream(
                temporaryFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            created = true;
            stream.Write(file);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            // Only a file WE created may be removed; a CreateNew that failed never touched anything.
            if (created)
            {
                TryDelete(temporaryFile);
            }

            throw;
        }

        return temporaryFile;
    }

    private bool VerifyExport(string temporaryFile, ReadOnlySpan<char> passphrase, BackupPayload expected)
    {
        using var decoded = Codec.DecryptFile(temporaryFile, passphrase);
        if (!decoded.IsSuccess)
        {
            return false;
        }

        var read = BackupPayloadSerializer.Read(decoded.Plaintext!.Memory);
        using var actual = read.Payload;
        return actual is not null
            && BackupPayloadSerializer.Validate(actual, _validator)
            && actual.Servers.SequenceEqual(expected.Servers)
            && actual.KnownHosts.SequenceEqual(expected.KnownHosts)
            && actual.RoutedKnownHosts.SequenceEqual(expected.RoutedKnownHosts)
            && actual.MissingCredentials.SequenceEqual(expected.MissingCredentials)
            && actual.Settings == expected.Settings
            && actual.Credentials.Count == expected.Credentials.Count
            && actual.Credentials.Zip(expected.Credentials).All(pair =>
                pair.First.ServerId == pair.Second.ServerId
                && pair.First.Kind == pair.Second.Kind
                && pair.First.ReferenceId == pair.Second.ReferenceId
                && pair.First.Secret.Reveal().SequenceEqual(pair.Second.Secret.Reveal()));
    }

    private BackupExportResult ExportFailed(BackupError error, Exception? exception)
    {
        _logger.LogWarning(
            "Backup export failed: {Error} ({ExceptionType}).",
            error,
            exception?.GetType().Name ?? "none");
        return new BackupExportResult { Error = error };
    }

    // ---------------------------------------------------------------- inspect (§5.1)

    public async Task<RestoreInspectResult> InspectAsync(
        string sourcePath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        if (Journal.Exists)
        {
            return new RestoreInspectResult { Error = BackupError.RestorePending, PendingJournalDirectory = _map.JournalDirectory };
        }

        BackupDecodeResult decoded;
        try
        {
            decoded = Codec.DecryptFile(sourcePath, passphrase.Span);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return InspectFailed(BackupError.ReadFailed, exception);
        }

        using (decoded)
        {
            if (decoded.PassphraseProblem != BackupPassphraseProblem.None)
            {
                return new RestoreInspectResult { PassphraseProblem = decoded.PassphraseProblem };
            }

            if (!decoded.IsSuccess)
            {
                return InspectFailed(decoded.Error ?? BackupError.Damaged, null);
            }

            var read = BackupPayloadSerializer.Read(decoded.Plaintext!.Memory);
            var payload = read.Payload;
            if (payload is null)
            {
                return InspectFailed(read.Error ?? BackupError.InvalidContent, null);
            }

            try
            {
                if (!BackupPayloadSerializer.Validate(payload, _validator))
                {
                    return InspectFailed(BackupError.InvalidContent, null);
                }

                var rekeyed = RestoreRekeyer.Rekey(payload.Servers, payload.Credentials, NewId);
                var summary = await BuildRestoreSummaryAsync(payload, rekeyed, cancellationToken);
                var plan = new ConfigurationRestorePlan(this, summary, payload, rekeyed);
                payload = null;
                return new RestoreInspectResult { Plan = plan };
            }
            catch (OperationCanceledException exception)
            {
                return InspectFailed(BackupError.Canceled, exception);
            }
            finally
            {
                payload?.Dispose();
            }
        }
    }

    private async Task<RestoreSummary> BuildRestoreSummaryAsync(
        BackupPayload payload,
        RekeyedRestore rekeyed,
        CancellationToken cancellationToken)
    {
        var current = await _serverService.GetAllAsync(cancellationToken);
        var currentDirectTrust = await TryAsync(() => _directTrust.ExportAllAsync(cancellationToken));
        var currentRoutedTrust = await TryAsync(() => _routedTrust.ExportAllAsync(cancellationToken));
        var backupEndpoints = payload.KnownHosts.Select(entry => entry.Endpoint).ToHashSet();
        var backupRoutes = payload.RoutedKnownHosts.Select(entry => entry.Route).ToHashSet();
        var names = rekeyed.Servers.ToDictionary(server => server.Id, server => server.Name);

        var warnings = new List<KeyPathWarning>();
        foreach (var server in rekeyed.Servers)
        {
            AddWarning(warnings, server, server.PrivateKeyPath, isJump: false);
            AddWarning(warnings, server, server.Route?.Jump?.PrivateKeyPath, isJump: true);
        }

        return new RestoreSummary
        {
            BackupCreatedAt = payload.CreatedAt,
            BackupAppVersion = payload.AppVersion,
            Backup = new RestoreCounts(
                rekeyed.Servers.Count(server => server.Route is null),
                rekeyed.Servers.Count(server => server.Route is not null),
                rekeyed.Credentials.Count,
                payload.KnownHosts.Count,
                payload.RoutedKnownHosts.Count),
            Current = new RestoreCounts(
                current.Count(server => server.Route is null),
                current.Count(server => server.Route is not null),
                current.SelectMany(ServerCredentialReferences.All).Count(),
                currentDirectTrust?.Count,
                currentRoutedTrust?.Count),
            DirectTrustedHostKeysToRemove = currentDirectTrust?.Count(entry => !backupEndpoints.Contains(entry.Endpoint)),
            RoutedTrustedHostKeysToRemove = currentRoutedTrust?.Count(entry => !backupRoutes.Contains(entry.Route)),
            BackupSettings = payload.Settings,
            CurrentSettings = _settings.ReadCurrent(),
            MissingCredentials = rekeyed.ReferencesWithoutSecret
                .Select(reference => new BackupCredentialFlag(
                    reference.ServerId,
                    names.GetValueOrDefault(reference.ServerId, string.Empty),
                    ServerCredentialReferences.IsJumpKind(reference.Kind)))
                .ToList(),
            KeyPathWarnings = warnings,
        };
    }

    // C-7: only through the shared local predicate; metadata only; network paths are never touched.
    private void AddWarning(List<KeyPathWarning> warnings, Server server, string? path, bool isJump)
    {
        if (path is not null && LocalKeyPathPolicy.Probe(path, KeyFileAccess) is { } status)
        {
            warnings.Add(new KeyPathWarning(server.Id, server.Name, isJump, status));
        }
    }

    private static async Task<IReadOnlyList<T>?> TryAsync<T>(Func<Task<IReadOnlyList<T>>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private RestoreInspectResult InspectFailed(BackupError error, Exception? exception)
    {
        _logger.LogWarning(
            "Backup inspection failed: {Error} ({ExceptionType}).",
            error,
            exception?.GetType().Name ?? "none");
        return new RestoreInspectResult { Error = error };
    }

    // ---------------------------------------------------------------- apply (§5.2)

    public async Task<RestoreApplyResult> ApplyAsync(RestorePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan is not ConfigurationRestorePlan restore || !ReferenceEquals(restore.Owner, this))
        {
            throw new ArgumentException("The plan was not created by this service.", nameof(plan));
        }

        ObjectDisposedException.ThrowIf(restore.IsDisposed, restore);
        restore.MarkApplied();

        var journal = Journal;
        if (journal.Exists)
        {
            return ApplyEnded(RestoreApplyOutcome.RestorePending, BackupError.RestorePending, _map.JournalDirectory, null);
        }

        RestoreWriteToken? token;
        try
        {
            token = await _gate.BeginRestoreAsync(GateDrainTimeout, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            return ApplyEnded(RestoreApplyOutcome.Canceled, BackupError.Canceled, null, exception);
        }

        if (token is null)
        {
            return ApplyEnded(RestoreApplyOutcome.Busy, BackupError.Busy, null, null);
        }

        var engine = new RestoreRecoveryEngine(
            _map,
            _credentials,
            (participant, content, ct) => WriteParticipantAsync(token, participant, content, ct),
            _logger,
            FaultInjector);
        var monitoringStopped = false;
        var journalStarted = false;
        try
        {
            await _monitoring.StopAsync(cancellationToken);
            monitoringStopped = true;
            cancellationToken.ThrowIfCancellationRequested();

            // Step 1: pre copies, then the manifest LAST, before the first mutation.
            var oldReferences = (await _serverService.GetAllAsync(CancellationToken.None))
                .SelectMany(ServerCredentialReferences.All)
                .ToList();
            var newReferences = restore.Credentials.Select(credential => credential.Reference).ToList();
            journalStarted = true;
            var files = journal.CopyPre(_map);
            FaultInjector?.Invoke("journal");
            var manifest = new JournalManifest(JournalState.Applying, files, newReferences, oldReferences);
            journal.WriteManifest(manifest);

            // Step 2: new credentials under fresh ids — additive, nothing current is overwritten.
            for (var index = 0; index < restore.Credentials.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FaultInjector?.Invoke("credential:" + index);
                var credential = restore.Credentials[index];
                using (var existing = await _credentials.ReadAsync(credential.Reference, CancellationToken.None))
                {
                    if (existing is not null)
                    {
                        throw new RestoreConflictException();
                    }
                }

                await _credentials.WriteAsync(credential.Reference, credential.Secret, CancellationToken.None);
            }

            // Step 3: the six files, in the fixed order.
            var rendered = Render(restore);
            foreach (var participant in RestoreParticipantMap.WriteOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FaultInjector?.Invoke("write:" + participant);
                await WriteParticipantAsync(token, participant, rendered[participant], CancellationToken.None);
            }

            // Step 4: verify with the stores' own parsers.
            cancellationToken.ThrowIfCancellationRequested();
            FaultInjector?.Invoke("verify");
            if (!await VerifyAppliedAsync(restore, rendered))
            {
                throw new RestoreVerificationException();
            }

            // Step 5: COMMIT POINT, then the seal (irreversible until the process exits).
            cancellationToken.ThrowIfCancellationRequested();
            FaultInjector?.Invoke("commit");
            journal.WriteManifest(manifest with { State = JournalState.Committed });
            _gate.Seal(token);
        }
        catch (RestoreAbandonedException)
        {
            // Test-only simulated crash: the process "dies" here; startup recovery handles the journal.
            throw;
        }
        catch (Exception exception)
        {
            var cause = exception switch
            {
                OperationCanceledException => BackupError.Canceled,
                CredentialStoreException => BackupError.CredentialStoreUnavailable,
                _ => BackupError.WriteFailed
            };

            var status = journalStarted
                ? await RecoverSafelyAsync(engine, journal)
                : RecoveryStatus.Done;

            _gate.Release(token);
            if (monitoringStopped)
            {
                await ResumeMonitoringAsync();
            }

            return status == RecoveryStatus.Done
                ? ApplyEnded(
                    cause == BackupError.Canceled ? RestoreApplyOutcome.Canceled : RestoreApplyOutcome.RolledBack,
                    cause,
                    null,
                    exception)
                : ApplyEnded(RestoreApplyOutcome.PartialRestoreRollbackPending, cause, _map.JournalDirectory, exception);
        }

        // Steps 6–7 after the commit: never canceled; failures stay in the journal for the next start.
        FaultInjector?.Invoke("cleanup");
        var cleanup = await RecoverSafelyAsync(engine, journal);
        _logger.LogInformation(
            "Configuration restored: {ServerCount} servers, {CredentialCount} credentials. Old credential clean-up: {Cleanup}.",
            restore.Servers.Count,
            restore.Credentials.Count,
            cleanup);
        return new RestoreApplyResult
        {
            Outcome = RestoreApplyOutcome.Completed,
            OldCredentialCleanupPending = cleanup != RecoveryStatus.Done,
            JournalDirectory = cleanup == RecoveryStatus.Done ? null : _map.JournalDirectory,
        };
    }

    private async Task<RecoveryStatus> RecoverSafelyAsync(RestoreRecoveryEngine engine, RestoreJournal journal)
    {
        try
        {
            return await engine.RecoverAsync(journal, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not RestoreAbandonedException)
        {
            _logger.LogError("Restore recovery failed ({ExceptionType}); the journal was kept.", exception.GetType().Name);
            return RecoveryStatus.Pending;
        }
    }

    private async Task ResumeMonitoringAsync()
    {
        try
        {
            await _monitoring.ResumeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Monitoring could not be restarted after the restore ended ({ExceptionType}).", exception.GetType().Name);
        }
    }

    private Dictionary<string, byte[]> Render(ConfigurationRestorePlan plan)
    {
        var (direct, routed) = JsonServerRepository.RenderReplacement(plan.Servers);
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [RestoreParticipantMap.TrustDirect] = JsonHostKeyTrustStore.Render(plan.KnownHosts),
            [RestoreParticipantMap.TrustRouted] = JsonRoutedHostKeyTrustStore.Render(plan.RoutedKnownHosts),
            [RestoreParticipantMap.ServersRouted] = routed,
            [RestoreParticipantMap.ServersDirect] = direct,
            [RestoreParticipantMap.SettingsNotification] = _settings.RenderNotificationSettings(plan.Settings),
            [RestoreParticipantMap.SettingsBackground] = _settings.RenderBackgroundSettings(plan.Settings),
        };
    }

    private async Task WriteParticipantAsync(
        RestoreWriteToken token,
        string participant,
        byte[]? content,
        CancellationToken cancellationToken)
    {
        switch (participant)
        {
            case RestoreParticipantMap.TrustDirect:
                await _directTrust.ReplaceForRestoreAsync(token, content, cancellationToken);
                break;
            case RestoreParticipantMap.TrustRouted:
                await _routedTrust.ReplaceForRestoreAsync(token, content, cancellationToken);
                break;
            case RestoreParticipantMap.ServersRouted:
                await _repository.ReplaceFileForRestoreAsync(token, routed: true, content, cancellationToken);
                break;
            case RestoreParticipantMap.ServersDirect:
                await _repository.ReplaceFileForRestoreAsync(token, routed: false, content, cancellationToken);
                break;
            case RestoreParticipantMap.SettingsNotification:
                _settings.ReplaceNotificationSettings(token, content);
                break;
            case RestoreParticipantMap.SettingsBackground:
                _settings.ReplaceBackgroundSettings(token, content);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(participant));
        }
    }

    private async Task<bool> VerifyAppliedAsync(ConfigurationRestorePlan plan, Dictionary<string, byte[]> rendered)
    {
        foreach (var (participant, expected) in rendered)
        {
            var actual = DurableFile.ReadOrNull(_map.PathOf(participant));
            if (actual is null || !actual.AsSpan().SequenceEqual(expected))
            {
                return false;
            }
        }

        var servers = await _repository.ReadForVerifyAsync(CancellationToken.None);
        if (servers is null
            || servers.Count != plan.Servers.Count
            || !servers.ToHashSet().SetEquals(plan.Servers))
        {
            return false;
        }

        if (!(await _directTrust.ExportAllAsync(CancellationToken.None)).SequenceEqual(plan.KnownHosts)
            || !(await _routedTrust.ExportAllAsync(CancellationToken.None)).SequenceEqual(plan.RoutedKnownHosts))
        {
            return false;
        }

        if (_settings.ReadNotificationAndBackground(
                rendered[RestoreParticipantMap.SettingsNotification],
                rendered[RestoreParticipantMap.SettingsBackground]) != plan.Settings)
        {
            return false;
        }

        foreach (var credential in plan.Credentials)
        {
            using var stored = await _credentials.ReadAsync(credential.Reference, CancellationToken.None);
            if (stored is null || !stored.Reveal().SequenceEqual(credential.Secret.Reveal()))
            {
                return false;
            }
        }

        return true;
    }

    private RestoreApplyResult ApplyEnded(
        RestoreApplyOutcome outcome,
        BackupError? error,
        string? journalDirectory,
        Exception? exception)
    {
        _logger.LogWarning(
            "Restore ended: {Outcome}, cause {Error} ({ExceptionType}).",
            outcome,
            error,
            exception?.GetType().Name ?? "none");
        return new RestoreApplyResult { Outcome = outcome, Error = error, JournalDirectory = journalDirectory };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover ciphertext temp in the user's folder is the documented residual (V6).
        }
    }
}

/// <summary>A validated, re-keyed restore held in memory (owns the backup's secrets).</summary>
internal sealed class ConfigurationRestorePlan : RestorePlan
{
    private readonly BackupPayload _payload;
    private readonly RekeyedRestore _rekeyed;
    private int _disposed;
    private int _applied;

    public ConfigurationRestorePlan(object owner, RestoreSummary summary, BackupPayload payload, RekeyedRestore rekeyed)
        : base(summary)
    {
        Owner = owner;
        _payload = payload;
        _rekeyed = rekeyed;
    }

    public object Owner { get; }

    public override bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public IReadOnlyList<Server> Servers => _rekeyed.Servers;

    public IReadOnlyList<RekeyedCredential> Credentials => _rekeyed.Credentials;

    public IReadOnlyList<TrustedHostKey> KnownHosts => _payload.KnownHosts;

    public IReadOnlyList<TrustedRoutedHostKey> RoutedKnownHosts => _payload.RoutedKnownHosts;

    public PortableSettings Settings => _payload.Settings;

    public void MarkApplied()
    {
        if (Interlocked.Exchange(ref _applied, 1) != 0)
        {
            throw new InvalidOperationException("A restore plan can be applied only once.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _payload.Dispose();
        }
    }
}

/// <summary>A fresh credential target already exists: abort before overwriting anything.</summary>
internal sealed class RestoreConflictException : InvalidOperationException;

internal sealed class RestoreVerificationException : InvalidOperationException;

/// <summary>TEST-ONLY: thrown by a fault injector to simulate the process dying at that point (no rollback).</summary>
internal sealed class RestoreAbandonedException : Exception;
