using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.Infrastructure.Tests.Backup;

// M14.6 §5.2 journaled REPLACE: zero net change on any failure up to verify, exact pre- or post-state after a
// crash + recovery, sealed gate after commit. Real stores, real gate, real journal, real codec.
public sealed class ConfigurationRestoreApplyTests
{
    private static readonly ReadOnlyMemory<char> Pass = BackupHarness.Passphrase.AsMemory();

    // Every fault point up to and including verify, in order (steps 1–4).
    public static TheoryData<string> PreCommitSteps() =>
    [
        "journal", "credential:0", "credential:3", "credential:5",
        "write:trust.direct", "write:trust.routed", "write:servers.routed", "write:servers.direct",
        "write:settings.notification", "write:settings.background", "verify",
    ];

    public static TheoryData<string> Participants() => [.. RestoreParticipantMap.WriteOrder];

    // ---------------------------------------------------------------- success + round trip

    [Fact]
    public async Task Restore_ReplacesEverything_UnderFreshReferences_AndRoundTrips()
    {
        using var target = await BackupScenarios.TargetAsync();
        var oldReference = ServerCredentialReferences.Target(BackupHarness.Direct(10, "other.example.com"))!.Value;
        var (plan, service) = await InspectSourceAsync(target);

        var result = await service.ApplyAsync(plan);
        plan.Dispose();

        Assert.Equal(RestoreApplyOutcome.Completed, result.Outcome);
        Assert.False(result.OldCredentialCleanupPending);
        Assert.False(Directory.Exists(target.JournalDirectory));
        Assert.False(target.Raw.Contains(oldReference));

        target.Reopen();
        var servers = await target.Servers.GetAllAsync();
        Assert.Equal(
            BackupScenarios.SourceServers().Select(s => s.Id).Order(),
            servers.Select(s => s.Id).Order());

        // Direct servers only in servers.json, routed only in routed-servers.json, route+jump preserved.
        var directFile = await File.ReadAllTextAsync(target.ServerOptions.FilePath);
        var routedFile = await File.ReadAllTextAsync(target.ServerOptions.RoutedFilePath);
        Assert.Contains(BackupScenarios.Web.ToString(), directFile, StringComparison.Ordinal);
        Assert.DoesNotContain(BackupScenarios.RoutedPassword.ToString(), directFile, StringComparison.Ordinal);
        Assert.DoesNotContain("route", directFile, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(BackupScenarios.RoutedPassword.ToString(), routedFile, StringComparison.Ordinal);
        Assert.DoesNotContain(BackupScenarios.Web.ToString(), routedFile, StringComparison.Ordinal);
        var routed = servers.Single(s => s.Id == BackupScenarios.RoutedPassword);
        Assert.Equal("bastion.example.com", routed.Route!.Jump!.Host);

        // Every reference is fresh, and each secret arrived intact under its new reference.
        foreach (var source in BackupScenarios.SourceServers())
        {
            var restored = servers.Single(s => s.Id == source.Id);
            foreach (var (before, after) in ServerCredentialReferences.All(source).Zip(ServerCredentialReferences.All(restored)))
            {
                Assert.NotEqual(before.ReferenceId, after.ReferenceId);
                Assert.Equal(before.Kind, after.Kind);
                Assert.StartsWith(BackupHarness.SecretMarker + before.Kind, target.Raw.Get(after), StringComparison.Ordinal);
            }
        }

        // Trust replaced with exactly the backup's referenced set: the unrelated current entries are gone.
        var direct = await target.DirectTrust.ExportAllAsync(CancellationToken.None);
        Assert.Equal(["bastion.example.com", "web.example.com"], direct.Select(e => e.Endpoint.Host));
        Assert.Equal(2, (await target.RoutedTrust.ExportAllAsync(CancellationToken.None)).Count);

        // Settings restored; the target's one-shot notice flag is kept.
        Assert.Equal(new PortableSettings(false, false), target.Settings.ReadCurrent());
        Assert.Contains("\"backgroundNoticeShown\":false", await File.ReadAllTextAsync(target.BackgroundPath), StringComparison.Ordinal);

        // Re-export from the restored machine equals the source (modulo the fresh reference ids).
        var reexport = await target.CreateService().ExportAsync(target.OutPath("again"), Pass, Pass);
        Assert.True(reexport.Succeeded);
        var again = await ConfigurationBackupExportTests.PayloadOf(target.OutPath("again"));
        var original = JsonNode.Parse((await BackupScenarios.SourceBackupAsync()).Plaintext)!.AsObject();
        Assert.Equal(WithoutReferences(original), WithoutReferences(again));

        Assert.Equal(1, target.Monitoring.Stops);
        Assert.Equal(0, target.Monitoring.Resumes);
        target.AssertNoSecretsLeaked();
    }

    // V5 / cp 18: after commit every ordinary writer is refused until the process exits.
    [Fact]
    public async Task AfterCommit_EveryOrdinaryWriterIsRefused()
    {
        using var target = await BackupScenarios.TargetAsync();
        var (plan, service) = await InspectSourceAsync(target);
        Assert.Equal(RestoreApplyOutcome.Completed, (await service.ApplyAsync(plan)).Outcome);
        plan.Dispose();
        var server = (await target.Servers.GetAllAsync())[0];
        var reference = CredentialReference.Create(server.Id, ServerCredentialKind.Password);
        using var secret = new SecretValue("s");

        await Assert.ThrowsAsync<ConfigurationLockedException>(() => target.Servers.HideAsync(server.Id));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => target.Profiles.RemoveAsync(server));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => target.Repository.SaveAllAsync([]));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => target.DirectTrust.TrustAsync(
            SshEndpoint.Create("x.example.com", 22), BackupHarness.DirectTrustEntry("x").Identity));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => target.DirectTrust.RemoveAsync(SshEndpoint.Create("web.example.com", 22)));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => target.RoutedTrust.RemoveAsync(
            SshRoute.Create(SshEndpoint.Create("bastion.example.com", 22), SshEndpoint.Create("10.0.0.5", 22))));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => target.Credentials.WriteAsync(reference, secret));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => target.Credentials.DeleteAsync(reference));
        Assert.True(target.Gate.IsLocked);
    }

    [Fact]
    public async Task MissingCredential_IsRestoredUnderAFreshReferenceWithoutSecret_AndFlagged()
    {
        using var source = await BackupScenarios.SourceAsync();
        Assert.True(await source.Raw.DeleteAsync(new CredentialReference(BackupScenarios.Web, ServerCredentialKind.Password, BackupHarness.Id(1001))));
        Assert.True((await source.CreateService().ExportAsync(source.OutPath(), Pass, Pass)).Succeeded);
        using var target = await BackupScenarios.TargetAsync();
        var service = target.CreateService();
        using var plan = (await service.InspectAsync(source.OutPath(), Pass)).Plan!;

        Assert.Equal([new BackupCredentialFlag(BackupScenarios.Web, "direct-1", false)], plan.Summary.MissingCredentials);
        Assert.Equal(RestoreApplyOutcome.Completed, (await service.ApplyAsync(plan)).Outcome);

        target.Reopen();
        var web = (await target.Servers.GetAllAsync()).Single(s => s.Id == BackupScenarios.Web);
        Assert.NotNull(web.CredentialReferenceId);
        Assert.NotEqual(BackupHarness.Id(1001), web.CredentialReferenceId);
        Assert.Null(target.Raw.Get(ServerCredentialReferences.Target(web)!.Value));
    }

    // cp 5: a same-machine restore (backup references == live references) must not overwrite the live secrets.
    [Fact]
    public async Task SameMachineRestore_Succeeds_AndNeverOverwritesALiveSecretBeforeCommit()
    {
        using var machine = await BackupScenarios.SourceAsync();
        var path = await BackupScenarios.WriteSourceBackupAsync(machine);
        var live = machine.Raw.References.ToDictionary(r => r, r => machine.Raw.Get(r));
        var writes = new List<CredentialReference>();
        machine.Raw.WriteFault = (reference, _) =>
        {
            writes.Add(reference);
            return null;
        };
        var service = machine.CreateService();
        using var plan = (await service.InspectAsync(path, Pass)).Plan!;

        var result = await service.ApplyAsync(plan);

        Assert.Equal(RestoreApplyOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(writes, live.ContainsKey);
    }

    // ---------------------------------------------------------------- failures before commit → zero change

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task CredentialManagerFailure_AtTheKthWrite_RollsBack_ZeroChange(int failingWrite)
    {
        using var target = await BackupScenarios.TargetAsync();
        var (plan, service) = await InspectSourceAsync(target);
        var before = target.Snapshot();
        var attempts = 0;
        target.Raw.WriteFault = (_, _) => attempts++ == failingWrite
            ? new CredentialStoreException(CredentialStoreOperation.Write, 1312)
            : null;

        var result = await service.ApplyAsync(plan);
        plan.Dispose();

        Assert.Equal(RestoreApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(BackupError.CredentialStoreUnavailable, result.Error);
        AssertUntouched(target, before);
        target.AssertNoSecretsLeaked();
    }

    // cp 7: every file written before a failure is restored byte-for-byte.
    [Theory]
    [MemberData(nameof(Participants))]
    public async Task FileWriteFailure_AtEachParticipant_RollsBack_ZeroChange(string participant)
    {
        using var target = await BackupScenarios.TargetAsync();
        var (plan, service) = await InspectSourceAsync(target, faults: step =>
        {
            if (step == "write:" + participant)
            {
                throw new IOException("disk full");
            }
        });
        var before = target.Snapshot();

        var result = await service.ApplyAsync(plan);
        plan.Dispose();

        Assert.Equal(RestoreApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(BackupError.WriteFailed, result.Error);
        AssertUntouched(target, before);
    }

    [Fact]
    public async Task VerifyFailure_RollsBack_ZeroChange()
    {
        using var target = await BackupScenarios.TargetAsync();
        var (plan, service) = await InspectSourceAsync(target, faults: step =>
        {
            if (step == "verify")
            {
                throw new InvalidOperationException("verify mismatch");
            }
        });
        var before = target.Snapshot();

        var result = await service.ApplyAsync(plan);
        plan.Dispose();

        Assert.Equal(RestoreApplyOutcome.RolledBack, result.Outcome);
        AssertUntouched(target, before);
    }

    // Cancellation is honoured at every step up to verify (and becomes a rollback).
    [Theory]
    [MemberData(nameof(PreCommitSteps))]
    public async Task Cancel_AtEachStepBeforeCommit_ZeroChange(string step)
    {
        using var target = await BackupScenarios.TargetAsync();
        using var cancellation = new CancellationTokenSource();
        var (plan, service) = await InspectSourceAsync(target, faults: at =>
        {
            if (at == step)
            {
                cancellation.Cancel();
            }
        });
        var before = target.Snapshot();

        var result = await service.ApplyAsync(plan, cancellation.Token);
        plan.Dispose();

        Assert.Equal(RestoreApplyOutcome.Canceled, result.Outcome);
        AssertUntouched(target, before);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("cleanup")]
    public async Task Cancel_AtOrAfterTheCommitPoint_IsIgnored(string step)
    {
        using var target = await BackupScenarios.TargetAsync();
        using var cancellation = new CancellationTokenSource();
        var (plan, service) = await InspectSourceAsync(target, faults: at =>
        {
            if (at == step)
            {
                cancellation.Cancel();
            }
        });

        var result = await service.ApplyAsync(plan, cancellation.Token);
        plan.Dispose();

        Assert.Equal(RestoreApplyOutcome.Completed, result.Outcome);
        Assert.False(Directory.Exists(target.JournalDirectory));
    }

    [Fact]
    public async Task RollbackThatCannotFinish_IsPartialPending_ThenStartupRecoveryCompletesIt()
    {
        using var target = await BackupScenarios.TargetAsync();
        var failRestore = true;
        var (plan, service) = await InspectSourceAsync(target, faults: step =>
        {
            if (step == "write:settings.background")
            {
                throw new IOException("write failed");
            }

            if (failRestore && step == "restore:servers.direct")
            {
                throw new IOException("restore failed");
            }
        });
        var before = target.Snapshot();

        var result = await service.ApplyAsync(plan);
        plan.Dispose();

        Assert.Equal(RestoreApplyOutcome.PartialRestoreRollbackPending, result.Outcome);
        Assert.Equal(target.JournalDirectory, result.JournalDirectory);
        Assert.True(File.Exists(Path.Combine(target.JournalDirectory, "journal.json")));

        failRestore = false;
        var report = await RecoverAsync(target);

        Assert.Equal(RestoreRecoveryOutcome.RolledBack, report.Outcome);
        AssertUntouched(target, before);
    }

    // ---------------------------------------------------------------- crash + startup recovery (cp 6, 8)

    [Theory]
    [MemberData(nameof(PreCommitSteps))]
    [InlineData("commit")]
    public async Task CrashBeforeTheCommitPoint_RecoveryRestoresExactlyThePreState(string step)
    {
        using var target = await BackupScenarios.TargetAsync();
        var (plan, service) = await InspectSourceAsync(target, faults: at =>
        {
            if (at == step)
            {
                throw new RestoreAbandonedException();
            }
        });
        var before = target.Snapshot();

        await Assert.ThrowsAsync<RestoreAbandonedException>(() => service.ApplyAsync(plan));
        plan.Dispose();

        var report = await RecoverAsync(target);

        Assert.Equal(step == "journal" ? RestoreRecoveryOutcome.NothingToRecover : RestoreRecoveryOutcome.RolledBack, report.Outcome);
        AssertUntouched(target, before);
        target.AssertNoSecretsLeaked();
    }

    [Fact]
    public async Task CrashAfterTheCommitPoint_RecoveryRollsForwardToExactlyThePostState()
    {
        using var reference = await BackupScenarios.TargetAsync();
        var (referencePlan, referenceService) = await InspectSourceAsync(reference);
        Assert.Equal(RestoreApplyOutcome.Completed, (await referenceService.ApplyAsync(referencePlan)).Outcome);
        referencePlan.Dispose();
        var expected = reference.Snapshot();

        using var target = await BackupScenarios.TargetAsync();
        var (plan, service) = await InspectSourceAsync(target, faults: at =>
        {
            if (at == "cleanup")
            {
                throw new RestoreAbandonedException();
            }
        });
        await Assert.ThrowsAsync<RestoreAbandonedException>(() => service.ApplyAsync(plan));
        plan.Dispose();
        Assert.NotEqual(expected, target.Snapshot()); // old credentials not yet deleted

        var report = await RecoverAsync(target);

        Assert.Equal(RestoreRecoveryOutcome.Completed, report.Outcome);
        Assert.Equal(expected, target.Snapshot());
        Assert.False(Directory.Exists(target.JournalDirectory));
    }

    [Fact]
    public async Task OldCredentialDeletionFailure_AfterCommit_IsCompletedWithCleanupPending_RetriedAtNextStart()
    {
        using var target = await BackupScenarios.TargetAsync();
        var (plan, service) = await InspectSourceAsync(target);
        target.Raw.DeleteFault = _ => new CredentialStoreException(CredentialStoreOperation.Delete, 5);

        var result = await service.ApplyAsync(plan);
        plan.Dispose();

        Assert.Equal(RestoreApplyOutcome.Completed, result.Outcome);
        Assert.True(result.OldCredentialCleanupPending);
        Assert.True(Directory.Exists(target.JournalDirectory));

        target.Raw.DeleteFault = null;
        Assert.Equal(RestoreRecoveryOutcome.Completed, (await RecoverAsync(target)).Outcome);
        Assert.False(Directory.Exists(target.JournalDirectory));
        Assert.Equal(6, target.Raw.References.Count);
    }

    // ---------------------------------------------------------------- journal hardening (V4, cp 16, C-9)

    [Fact]
    public async Task CorruptJournal_NothingDestructive_KeepsIt_AndBlocksApplyAndExport()
    {
        using var target = await BackupScenarios.TargetAsync();
        Directory.CreateDirectory(target.JournalDirectory);
        await File.WriteAllTextAsync(Path.Combine(target.JournalDirectory, "journal.json"), "{ broken");
        var before = target.Snapshot();

        var report = await RecoverAsync(target);

        Assert.Equal(RestoreRecoveryOutcome.Stuck, report.Outcome);
        Assert.Equal(target.JournalDirectory, report.JournalDirectory);
        Assert.Equal(before, target.Snapshot());
        Assert.True(File.Exists(Path.Combine(target.JournalDirectory, "journal.json")));
        Assert.Equal(BackupError.RestorePending, (await target.CreateService().ExportAsync(target.OutPath(), Pass, Pass)).Error);
    }

    [Fact]
    public async Task PendingJournal_BlocksApply_NothingTouched()
    {
        using var target = await BackupScenarios.TargetAsync();
        var (plan, service) = await InspectSourceAsync(target);
        Directory.CreateDirectory(target.JournalDirectory);
        var before = target.Snapshot();

        var result = await service.ApplyAsync(plan);
        plan.Dispose();

        Assert.Equal(RestoreApplyOutcome.RestorePending, result.Outcome);
        Assert.Equal(target.JournalDirectory, result.JournalDirectory);
        Assert.Equal(before, target.Snapshot());
        Assert.False(target.Gate.IsLocked);
    }

    public static TheoryData<string> JournalTampering() =>
    [
        "unknown-participant", "path-field", "duplicate-participant", "missing-participant", "hash-mismatch",
        "unknown-state", "future-version", "reference-in-both-lists",
    ];

    [Theory]
    [MemberData(nameof(JournalTampering))]
    public async Task TamperedJournal_IsCorrupt_NothingDestructive(string tampering)
    {
        using var target = await BackupScenarios.TargetAsync();
        await CrashAtAsync(target, "write:servers.direct");
        var manifestPath = Path.Combine(target.JournalDirectory, "journal.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var files = manifest["files"]!.AsArray();
        switch (tampering)
        {
            case "unknown-participant": files[0]!["participant"] = "servers.elsewhere"; break;
            case "path-field": files[0]!["path"] = @"C:\Windows\System32\drivers\etc\hosts"; break;
            case "duplicate-participant": files[1]!["participant"] = (string)files[0]!["participant"]!; break;
            case "missing-participant": files.RemoveAt(0); break;
            case "hash-mismatch":
                var existing = files.First(f => (bool)f!["existed"]!)!;
                existing["sha256"] = new string('0', 64);
                break;
            case "unknown-state": manifest["state"] = "Done"; break;
            case "future-version": manifest["journalVersion"] = 2; break;
            default: manifest["oldCredentials"]!.AsArray().Add(manifest["newCredentials"]![0]!.DeepClone()); break;
        }

        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var midState = target.Snapshot();

        var report = await RecoverAsync(target);

        Assert.Equal(RestoreRecoveryOutcome.Stuck, report.Outcome);
        Assert.Equal(midState, target.Snapshot());
        Assert.True(File.Exists(manifestPath));
    }

    // cp 16: a hash-valid journal that lists a LIVE credential in newCredentials must never delete it.
    [Fact]
    public async Task JournalListingALiveCredentialAsNew_RollbackDeletesNothing_KeepsJournal()
    {
        using var target = await BackupScenarios.TargetAsync();
        var live = ServerCredentialReferences.Target(BackupHarness.Direct(10, "other.example.com"))!.Value;
        await CrashAtAsync(target, "write:servers.direct");

        // MOVED from oldCredentials (where a genuine journal lists it) to newCredentials: a hash-valid journal whose
        // only defect is the lie, so the referential guard is the one thing standing between it and a deletion.
        await MoveReferenceAsync(target, "oldCredentials", "newCredentials", live);

        var report = await RecoverAsync(target);

        Assert.Equal(RestoreRecoveryOutcome.Stuck, report.Outcome);
        Assert.True(target.Raw.Contains(live));
        Assert.True(Directory.Exists(target.JournalDirectory));
    }

    // cp 16 (roll-forward side): a committed journal that lists a credential the committed configuration uses.
    [Fact]
    public async Task CommittedJournalListingAUsedCredentialAsOld_DeletesNothing_KeepsJournal()
    {
        using var target = await BackupScenarios.TargetAsync();
        await CrashAtAsync(target, "cleanup");
        target.Reopen();
        var used = ServerCredentialReferences.Target((await target.Servers.GetAllAsync()).Single(s => s.Id == BackupScenarios.Web))!.Value;
        await MoveReferenceAsync(target, "newCredentials", "oldCredentials", used);
        var credentialsBefore = target.Raw.Dump();

        var report = await RecoverAsync(target);

        Assert.Equal(RestoreRecoveryOutcome.Stuck, report.Outcome);
        Assert.True(target.Raw.Contains(used));
        Assert.Equal(credentialsBefore, target.Raw.Dump());
        Assert.True(Directory.Exists(target.JournalDirectory));
    }

    [Fact]
    public async Task OrphanJournalWithoutManifest_IsRemoved_NothingElseTouched()
    {
        using var target = await BackupScenarios.TargetAsync();
        Directory.CreateDirectory(Path.Combine(target.JournalDirectory, "pre"));
        var before = target.Snapshot();

        var report = await RecoverAsync(target);

        Assert.Equal(RestoreRecoveryOutcome.NothingToRecover, report.Outcome);
        Assert.False(Directory.Exists(target.JournalDirectory));
        Assert.Equal(before, target.Snapshot());
    }

    // ---------------------------------------------------------------- gate drain (V5)

    [Fact]
    public async Task InFlightWrite_DrainTimeout_IsBusy_NothingTouched_MonitoringUntouched()
    {
        using var target = await BackupScenarios.TargetAsync();
        var service = target.CreateService(drainTimeout: TimeSpan.FromMilliseconds(200));
        using var plan = (await service.InspectAsync(await BackupScenarios.WriteSourceBackupAsync(target), Pass)).Plan!;
        var inFlight = await Task.Run(target.Gate.EnterWrite); // a concurrent writer that does not finish in time
        var before = target.Snapshot();

        var result = await service.ApplyAsync(plan);
        inFlight.Dispose();

        Assert.Equal(RestoreApplyOutcome.Busy, result.Outcome);
        Assert.Equal(before, target.Snapshot());
        Assert.Equal(0, target.Monitoring.Stops);
        Assert.False(target.Gate.IsLocked);
        Assert.True(await target.Servers.HideAsync(BackupScenarios.TargetOther));
    }

    [Fact]
    public async Task InFlightWrite_DrainsBeforeTheJournal_ThenTheRestoreProceeds()
    {
        using var target = await BackupScenarios.TargetAsync();
        var inFlight = await Task.Run(target.Gate.EnterWrite);
        var leaseReleased = false;
        var journalStartedBeforeRelease = false;
        var (plan, service) = await InspectSourceAsync(target, faults: step =>
        {
            if (step == "journal" && !leaseReleased)
            {
                journalStartedBeforeRelease = true;
            }
        });

        var apply = service.ApplyAsync(plan);
        await Task.Delay(100);
        Assert.False(apply.IsCompleted);
        Assert.False(Directory.Exists(target.JournalDirectory));
        leaseReleased = true;
        inFlight.Dispose();

        Assert.Equal(RestoreApplyOutcome.Completed, (await apply).Outcome);
        Assert.False(journalStartedBeforeRelease);
        plan.Dispose();
    }

    [Fact]
    public async Task AfterARollback_OrdinaryWritesAndMonitoringResume()
    {
        using var target = await BackupScenarios.TargetAsync();
        var (plan, service) = await InspectSourceAsync(target, faults: step =>
        {
            if (step == "verify")
            {
                throw new IOException("x");
            }
        });

        Assert.Equal(RestoreApplyOutcome.RolledBack, (await service.ApplyAsync(plan)).Outcome);
        plan.Dispose();

        Assert.False(target.Gate.IsLocked);
        Assert.Equal(1, target.Monitoring.Resumes);
        Assert.True(await target.Servers.HideAsync(BackupScenarios.TargetOther));
    }

    [Fact]
    public async Task APlan_CanBeAppliedOnlyOnce_AndOnlyByItsService()
    {
        using var target = await BackupScenarios.TargetAsync();
        var (plan, service) = await InspectSourceAsync(target);

        await Assert.ThrowsAsync<ArgumentException>(() => target.CreateService().ApplyAsync(plan));
        Assert.Equal(RestoreApplyOutcome.Completed, (await service.ApplyAsync(plan)).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(plan));
        plan.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ApplyAsync(plan));
    }

    // ---------------------------------------------------------------- legacy namespace (§5.4)

    [Fact]
    public async Task Legacy_RestoreWritesNeutralOnly_AndDeletesTheOldLegacyReferenceAfterCommit()
    {
        using var native = new Security.CredentialNamespaceMigrationTests.DictionaryCredentialManagerNative();
        using var windows = new WindowsCredentialStore(native);
        using var target = new BackupHarness(windows);
        var old = BackupHarness.Direct(10, "other.example.com");
        await target.SeedAsync([old], withSecrets: false);
        var oldReference = ServerCredentialReferences.Target(old)!.Value;
        native.Seed(CredentialTargetName.CreateLegacy(oldReference), "legacy-secret");
        var service = target.CreateService();
        using var plan = (await service.InspectAsync(await BackupScenarios.WriteSourceBackupAsync(target), Pass)).Plan!;

        Assert.Equal(RestoreApplyOutcome.Completed, (await service.ApplyAsync(plan)).Outcome);

        Assert.False(native.Contains(CredentialTargetName.CreateLegacy(oldReference)));
        target.Reopen();
        foreach (var reference in (await target.Servers.GetAllAsync()).SelectMany(ServerCredentialReferences.All))
        {
            Assert.True(native.Contains(CredentialTargetName.Create(reference)));
            Assert.False(native.Contains(CredentialTargetName.CreateLegacy(reference)));
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(RestorePlan Plan, ConfigurationBackupService Service)> InspectSourceAsync(
        BackupHarness target,
        Action<string>? faults = null)
    {
        var path = await BackupScenarios.WriteSourceBackupAsync(target);
        var service = target.CreateService(faults: faults);
        var result = await service.InspectAsync(path, Pass);
        Assert.True(result.Succeeded, $"inspect failed: {result.Error}");
        return (result.Plan!, service);
    }

    private static async Task CrashAtAsync(BackupHarness target, string step)
    {
        var (plan, service) = await InspectSourceAsync(target, faults: at =>
        {
            if (at == step)
            {
                throw new RestoreAbandonedException();
            }
        });
        await Assert.ThrowsAsync<RestoreAbandonedException>(() => service.ApplyAsync(plan));
        plan.Dispose();
    }

    private static async Task MoveReferenceAsync(BackupHarness target, string from, string to, CredentialReference reference)
    {
        var manifestPath = Path.Combine(target.JournalDirectory, "journal.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var source = manifest[from]!.AsArray();
        var existing = source.Single(node => (string)node!["referenceId"]! == reference.ReferenceId.ToString());
        source.Remove(existing);
        manifest[to]!.AsArray().Add(new JsonObject
        {
            ["serverId"] = reference.ServerId.ToString(),
            ["kind"] = BackupCredentialKindNames.ToName(reference.Kind),
            ["referenceId"] = reference.ReferenceId.ToString(),
        });
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
    }

    /// <summary>The next start: new stores over the same files, then startup recovery.</summary>
    internal static async Task<RestoreRecoveryReport> RecoverAsync(BackupHarness target)
    {
        target.Reopen();
        var report = await RestoreJournalRecovery.RecoverAsync(
            target.ServerOptions,
            target.TrustOptions,
            target.RoutedTrustOptions,
            target.NotificationPath,
            target.BackgroundPath,
            target.RawStore,
            target.Log.For<ConfigurationRestoreApplyTests>());
        target.Reopen();
        return report;
    }

    private static void AssertUntouched(BackupHarness target, string before)
    {
        Assert.Equal(before, target.Snapshot());
        Assert.False(Directory.Exists(target.JournalDirectory));
        Assert.False(target.Gate.IsLocked);
    }

    // The fresh reference ids are the only expected difference between the source and a re-export.
    private static string WithoutReferences(JsonObject payload)
    {
        var copy = payload.DeepClone().AsObject();
        copy.Remove("createdAt");
        foreach (var server in copy["servers"]!.AsArray())
        {
            server!["credentialReferenceId"] = null;
            if (server["route"]?["jump"] is JsonObject jump)
            {
                jump["credentialReferenceId"] = null;
            }
        }

        foreach (var credential in copy["credentials"]!.AsArray())
        {
            credential!["referenceId"] = null;
        }

        var servers = copy["servers"]!.AsArray().Select(node => node!.ToJsonString()).Order().ToList();
        var credentials = copy["credentials"]!.AsArray().Select(node => node!.ToJsonString()).Order().ToList();
        copy.Remove("servers");
        copy.Remove("credentials");
        return copy.ToJsonString() + string.Join("|", servers) + string.Join("|", credentials);
    }
}
