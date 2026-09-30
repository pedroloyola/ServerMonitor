using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Backup;

/// <summary>
/// Referential guard for credential deletion (M14.6 V4): a credential reference may be deleted only when it is
/// PROVABLY absent from a server configuration file. Conservative superset of the store parser: a reference id
/// counts as used when its text (both GUID forms, any case) appears anywhere in the raw bytes, or when any
/// JSON string value anywhere in the file parses to it. When the file is not valid JSON and contains JSON
/// escapes (which could hide an id from the text search) absence cannot be proved.
/// </summary>
internal static class ReferenceGuard
{
    /// <summary>False when absence cannot be proved for this file; otherwise the "is referenced" test.</summary>
    public static bool TryCreate(byte[]? file, out Func<Guid, bool> isReferenced)
    {
        if (file is null)
        {
            isReferenced = static _ => false;
            return true;
        }

        var text = Encoding.UTF8.GetString(file);
        HashSet<Guid>? parsed = null;
        try
        {
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 64 });
            parsed = [];
            Collect(document.RootElement, parsed);
        }
        catch (JsonException)
        {
            if (text.Contains("\\u", StringComparison.Ordinal))
            {
                isReferenced = static _ => true;
                return false;
            }
        }

        isReferenced = id =>
            (parsed?.Contains(id) ?? false)
            || text.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || text.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static void Collect(JsonElement element, HashSet<Guid> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, ids);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, ids);
                }

                break;
            case JsonValueKind.String when Guid.TryParse(element.GetString(), out var id):
                ids.Add(id);
                break;
        }
    }
}

internal enum RecoveryStatus
{
    /// <summary>Everything done; the journal was deleted.</summary>
    Done,

    /// <summary>Work remains (a write or a credential deletion failed); the journal was kept for the next start.</summary>
    Pending,

    /// <summary>The journal cannot be acted on safely (corrupt, or a guard violation); nothing destructive was
    /// done and the journal was kept.</summary>
    Stuck,
}

/// <summary>
/// Rolls a journal back (state <c>Applying</c>) or forward (state <c>Committed</c>) — the SAME code for an
/// in-process failure and for startup recovery (§5.2). Paths come only from <see cref="RestoreParticipantMap"/>.
/// Credential deletions go through <see cref="ReferenceGuard"/> on both sides; any violation stops everything.
/// </summary>
internal sealed class RestoreRecoveryEngine(
    RestoreParticipantMap map,
    IServerCredentialStore credentials,
    Func<string, byte[]?, CancellationToken, Task> writeParticipant,
    ILogger logger,
    Action<string>? faultInjector = null)
{
    public async Task<RecoveryStatus> RecoverAsync(RestoreJournal journal, CancellationToken cancellationToken)
    {
        var read = journal.Read();
        switch (read.Status)
        {
            case JournalReadStatus.None:
                return RecoveryStatus.Done;
            case JournalReadStatus.Orphan:
                // Crash during step 1: nothing had been mutated yet (spec table, step 1).
                journal.Delete();
                logger.LogInformation("Removed an incomplete restore journal left before any change was made.");
                return RecoveryStatus.Done;
            case JournalReadStatus.Corrupt:
                logger.LogError("A restore journal is inconsistent; nothing was changed and it was kept.");
                return RecoveryStatus.Stuck;
        }

        var manifest = read.Manifest!;
        return manifest.State == JournalState.Applying
            ? await RollBackAsync(journal, manifest, read.PreContent!, cancellationToken)
            : await RollForwardAsync(journal, manifest, cancellationToken);
    }

    private async Task<RecoveryStatus> RollBackAsync(
        RestoreJournal journal,
        JournalManifest manifest,
        IReadOnlyDictionary<string, byte[]> pre,
        CancellationToken cancellationToken)
    {
        var failed = false;
        foreach (var participant in RestoreParticipantMap.WriteOrder.Reverse())
        {
            var entry = manifest.Files.Single(file => file.Participant == participant);
            var desired = entry.Existed ? pre[participant] : null;
            try
            {
                var current = DurableFile.ReadOrNull(map.PathOf(participant));
                if (SameContent(current, desired))
                {
                    continue;
                }

                faultInjector?.Invoke("restore:" + participant);
                await writeParticipant(participant, desired, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed = true;
                logger.LogError("A file could not be restored during rollback ({ExceptionType}).", exception.GetType().Name);
            }
        }

        if (failed)
        {
            return RecoveryStatus.Pending;
        }

        // The configuration is back to the pre-restore bytes: a new credential may go only if that configuration
        // provably does not reference it.
        if (!TryCreateGuard(pre.GetValueOrDefault(RestoreParticipantMap.ServersDirect), pre.GetValueOrDefault(RestoreParticipantMap.ServersRouted), out var isReferenced))
        {
            logger.LogWarning("The restored configuration could not be checked for credential use; no credential was deleted.");
            return RecoveryStatus.Pending;
        }

        return await DeleteGuardedAsync(journal, manifest.NewCredentials, isReferenced, cancellationToken);
    }

    private async Task<RecoveryStatus> RollForwardAsync(
        RestoreJournal journal,
        JournalManifest manifest,
        CancellationToken cancellationToken)
    {
        if (!TryCreateGuard(
                DurableFile.ReadOrNull(map.PathOf(RestoreParticipantMap.ServersDirect)),
                DurableFile.ReadOrNull(map.PathOf(RestoreParticipantMap.ServersRouted)),
                out var isReferenced))
        {
            logger.LogError("The committed configuration could not be checked for credential use; the journal was kept.");
            return RecoveryStatus.Stuck;
        }

        return await DeleteGuardedAsync(journal, manifest.OldCredentials, isReferenced, cancellationToken);
    }

    private async Task<RecoveryStatus> DeleteGuardedAsync(
        RestoreJournal journal,
        IReadOnlyList<CredentialReference> references,
        Func<Guid, bool> isReferenced,
        CancellationToken cancellationToken)
    {
        if (references.Any(reference => isReferenced(reference.ReferenceId)))
        {
            // A journal that lists a credential the live configuration uses is wrong: never delete anything.
            logger.LogError("A restore journal lists a credential that is still in use; nothing was deleted and the journal was kept.");
            return RecoveryStatus.Stuck;
        }

        var failed = false;
        foreach (var reference in references)
        {
            try
            {
                faultInjector?.Invoke("delete:" + reference.ReferenceId.ToString("N"));
                await credentials.DeleteAsync(reference, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed = true;
                logger.LogWarning("A credential could not be deleted ({ExceptionType}); it will be retried at next start.", exception.GetType().Name);
            }
        }

        if (failed)
        {
            return RecoveryStatus.Pending;
        }

        journal.Delete();
        return RecoveryStatus.Done;
    }

    private static bool TryCreateGuard(byte[]? direct, byte[]? routed, out Func<Guid, bool> isReferenced)
    {
        if (!ReferenceGuard.TryCreate(direct, out var inDirect) || !ReferenceGuard.TryCreate(routed, out var inRouted))
        {
            isReferenced = static _ => true;
            return false;
        }

        isReferenced = id => inDirect(id) || inRouted(id);
        return true;
    }

    private static bool SameContent(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
}

/// <summary>
/// Startup recovery (§5.2), run by the App BEFORE the host builds any store or engine (single instance: no
/// concurrent writer). Rolls an <c>Applying</c> journal back and a <c>Committed</c> one forward; an
/// inconsistent journal is kept and nothing destructive is done (<see cref="RestoreRecoveryOutcome.Stuck"/>).
/// Logs carry outcome codes only (C-6).
/// </summary>
public static class RestoreJournalRecovery
{
    /// <summary>The exact temporaries the journal writer can orphan when killed mid-write: the manifest's and
    /// each <c>pre\</c> copy's <c>.tmp</c>. Derived from the fixed layout, never from journal content.</summary>
    public static IReadOnlyList<string> JournalTemporaryFiles(string journalDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(journalDirectory);
        var journal = new RestoreJournal(journalDirectory);
        return
        [
            journal.ManifestPath + DurableFile.TemporarySuffix,
            .. RestoreParticipantMap.WriteOrder.Select(participant =>
                Path.Combine(journal.PreDirectory, participant) + DurableFile.TemporarySuffix),
        ];
    }

    public static async Task<RestoreRecoveryReport> RecoverAsync(
        ServerStorageOptions servers,
        HostKeyTrustStorageOptions trust,
        RoutedHostKeyTrustStorageOptions routedTrust,
        string notificationSettingsPath,
        string backgroundSettingsPath,
        IServerCredentialStore credentialStore,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(logger);

        var map = new RestoreParticipantMap(servers, trust, routedTrust, notificationSettingsPath, backgroundSettingsPath);
        var journal = new RestoreJournal(map.JournalDirectory);
        var read = journal.Read();
        if (read.Status == JournalReadStatus.None)
        {
            return RestoreRecoveryReport.None(map.JournalDirectory);
        }

        var wasCommitted = read.Manifest?.State == JournalState.Committed;
        var engine = new RestoreRecoveryEngine(
            map,
            credentialStore,
            (participant, content, _) =>
            {
                DurableFile.Replace(map.PathOf(participant), content);
                return Task.CompletedTask;
            },
            logger);

        RecoveryStatus status;
        try
        {
            status = await engine.RecoverAsync(journal, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Restore recovery failed ({ExceptionType}); the journal was kept.", exception.GetType().Name);
            status = RecoveryStatus.Stuck;
        }

        var outcome = (status, read.Status, wasCommitted) switch
        {
            (RecoveryStatus.Done, JournalReadStatus.Orphan, _) => RestoreRecoveryOutcome.NothingToRecover,
            (RecoveryStatus.Done, _, true) => RestoreRecoveryOutcome.Completed,
            (RecoveryStatus.Done, _, false) => RestoreRecoveryOutcome.RolledBack,

            // A committed restore whose old-credential clean-up must be retried is still a completed restore.
            (RecoveryStatus.Pending, _, true) => RestoreRecoveryOutcome.Completed,
            _ => RestoreRecoveryOutcome.Stuck
        };

        logger.LogInformation("Restore recovery outcome: {Outcome}.", outcome);
        return new RestoreRecoveryReport(outcome, map.JournalDirectory);
    }
}
