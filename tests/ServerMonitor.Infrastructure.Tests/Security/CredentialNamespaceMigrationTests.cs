using System.Runtime.InteropServices;
using System.Text;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.Infrastructure.Tests.Security;

// Verifies the M12/ADR-017 backward-compatible migration from the legacy personal
// credential namespace (pedroloyola.ServerMonitor:v1:ssh) to the neutral namespace
// (ServerMonitor:v1:ssh). Core invariant: a working credential is NEVER lost.
public sealed class CredentialNamespaceMigrationTests : IDisposable
{
    private const int ErrorNotFound = 1168;
    private const int ErrorAccessDenied = 5;

    private readonly DictionaryCredentialManagerNative _native = new();

    public void Dispose() => _native.Dispose();

    private WindowsCredentialStore Store => new(_native);

    private static CredentialReference Reference { get; } = new(
        Guid.Parse("40be3d25-ef62-4c5e-8d4d-d1164cc722f1"),
        ServerCredentialKind.Password,
        Guid.Parse("443065c4-2549-450f-85dc-5ec020cf575a"));

    private static string NeutralTarget => CredentialTargetName.Create(Reference);

    private static string LegacyTarget => CredentialTargetName.CreateLegacy(Reference);

    // 1. New-only: read returns the neutral secret; legacy is never touched.
    [Fact]
    public async Task Read_NewOnly_ReturnsNeutralWithoutMigration()
    {
        _native.Seed(NeutralTarget, "neutral-secret");

        using var result = await Store.ReadAsync(Reference);

        Assert.NotNull(result);
        Assert.Equal("neutral-secret", result.RevealAsString());
        Assert.False(_native.Contains(LegacyTarget));
        Assert.Equal(0, _native.WriteCount); // no migration write
    }

    // 2. Legacy-only: read returns the secret, writes the neutral target, removes legacy.
    [Fact]
    public async Task Read_LegacyOnly_MigratesForwardAndRemovesLegacy()
    {
        _native.Seed(LegacyTarget, "legacy-secret-密碼");

        using var result = await Store.ReadAsync(Reference);

        Assert.NotNull(result);
        Assert.Equal("legacy-secret-密碼", result.RevealAsString());
        Assert.True(_native.Contains(NeutralTarget));
        Assert.Equal("legacy-secret-密碼", _native.Reveal(NeutralTarget));
        Assert.False(_native.Contains(LegacyTarget)); // legacy removed after verify
    }

    // 3. Both exist: neutral wins; legacy is left untouched (cleaned up on next Delete).
    [Fact]
    public async Task Read_BothExist_ReturnsNeutralAndLeavesLegacyUntouched()
    {
        _native.Seed(NeutralTarget, "neutral-secret");
        _native.Seed(LegacyTarget, "stale-legacy");

        using var result = await Store.ReadAsync(Reference);

        Assert.Equal("neutral-secret", result!.RevealAsString());
        Assert.True(_native.Contains(LegacyTarget));
    }

    // 4. Legacy read hard-fails while neutral is absent: the store error surfaces.
    [Fact]
    public async Task Read_NeutralAbsentAndLegacyHardFails_Throws()
    {
        _native.FailReadWith(LegacyTarget, ErrorAccessDenied);

        var exception = await Assert.ThrowsAsync<CredentialStoreException>(() =>
            Store.ReadAsync(Reference));

        Assert.Equal(CredentialStoreOperation.Read, exception.Operation);
        Assert.Equal(ErrorAccessDenied, exception.NativeErrorCode);
    }

    // 5. Neutral write fails during migration: legacy stays, secret still returned.
    [Fact]
    public async Task Read_MigrationWriteFails_KeepsLegacyAndReturnsSecret()
    {
        _native.Seed(LegacyTarget, "legacy-secret");
        _native.FailWriteWith(NeutralTarget, ErrorAccessDenied);

        using var result = await Store.ReadAsync(Reference);

        Assert.Equal("legacy-secret", result!.RevealAsString());
        Assert.True(_native.Contains(LegacyTarget)); // never lost
        Assert.False(_native.Contains(NeutralTarget));
    }

    // 6. Neutral write reports success but does not persist (verification read-back fails):
    //    migration aborts, legacy is retained.
    [Fact]
    public async Task Read_MigrationVerificationFails_KeepsLegacyAndReturnsSecret()
    {
        _native.Seed(LegacyTarget, "legacy-secret");
        _native.SuppressWritePersistence(NeutralTarget); // Write returns true, stores nothing

        using var result = await Store.ReadAsync(Reference);

        Assert.Equal("legacy-secret", result!.RevealAsString());
        Assert.True(_native.Contains(LegacyTarget)); // legacy not deleted
        Assert.False(_native.Contains(NeutralTarget));
    }

    // 7. Legacy delete fails after a verified migration: neutral becomes authoritative,
    //    legacy lingers, secret still returned. No credential lost.
    [Fact]
    public async Task Read_LegacyDeleteFails_NeutralAuthoritativeSecretReturned()
    {
        _native.Seed(LegacyTarget, "legacy-secret");
        _native.FailDeleteWith(LegacyTarget, ErrorAccessDenied);

        using var result = await Store.ReadAsync(Reference);

        Assert.Equal("legacy-secret", result!.RevealAsString());
        Assert.True(_native.Contains(NeutralTarget));
        Assert.Equal("legacy-secret", _native.Reveal(NeutralTarget));
        Assert.True(_native.Contains(LegacyTarget)); // delete failed, tolerated
    }

    // 8. Server deletion: both neutral and legacy targets are removed.
    [Fact]
    public async Task Delete_RemovesBothNeutralAndLegacy()
    {
        _native.Seed(NeutralTarget, "neutral-secret");
        _native.Seed(LegacyTarget, "legacy-secret");

        var deleted = await Store.DeleteAsync(Reference);

        Assert.True(deleted);
        Assert.False(_native.Contains(NeutralTarget));
        Assert.False(_native.Contains(LegacyTarget));
    }

    // 8b. Deleting a legacy-only credential still reports success and removes it.
    [Fact]
    public async Task Delete_LegacyOnly_RemovesLegacyAndReportsDeleted()
    {
        _native.Seed(LegacyTarget, "legacy-secret");

        var deleted = await Store.DeleteAsync(Reference);

        Assert.True(deleted);
        Assert.False(_native.Contains(LegacyTarget));
    }

    // 9. Password update: write lands in the neutral target and clears any legacy secret.
    [Fact]
    public async Task Write_UpdatesNeutralAndClearsLegacy()
    {
        _native.Seed(LegacyTarget, "old-legacy-secret");
        using var secret = new SecretValue("new-secret");

        await Store.WriteAsync(Reference, secret);

        Assert.True(_native.Contains(NeutralTarget));
        Assert.Equal("new-secret", _native.Reveal(NeutralTarget));
        Assert.False(_native.Contains(LegacyTarget)); // legacy cleared on write
    }

    // A neutral delete hard-failure surfaces even though legacy cleanup is best-effort.
    [Fact]
    public async Task Delete_NeutralHardFailure_Throws()
    {
        _native.Seed(NeutralTarget, "neutral-secret");
        _native.FailDeleteWith(NeutralTarget, ErrorAccessDenied);

        var exception = await Assert.ThrowsAsync<CredentialStoreException>(() =>
            Store.DeleteAsync(Reference));

        Assert.Equal(CredentialStoreOperation.Delete, exception.Operation);
        Assert.Equal(ErrorAccessDenied, exception.NativeErrorCode);
    }

    // Concurrent Read (migrating) + Write + Delete on the same reference must be serialized so a
    // read-time migration can never clobber a newer write, resurrect a deleted credential, or
    // corrupt state (H-1, Atlas reliability review). The fake records the MAXIMUM number of native
    // operations observed in-flight simultaneously; the store's gate must keep that at exactly 1.
    // With the gate this is deterministic (native calls are strictly serialized); a regression that
    // dropped the gate would let the 30 concurrent tasks overlap (>1) and/or corrupt the
    // non-thread-safe fake dictionary (throw). No delays in the store under test.
    [Fact]
    public async Task ConcurrentReadWriteDelete_AreSerialized_NeverOverlap()
    {
        _native.Seed(LegacyTarget, "legacy-secret");
        var store = Store; // one instance → one gate

        var tasks = new List<Task>();
        for (var i = 0; i < 30; i++)
        {
            var k = i;
            tasks.Add(Task.Run(async () =>
            {
                switch (k % 3)
                {
                    case 0:
                        using (var secret = new SecretValue($"secret-{k}"))
                        {
                            await store.WriteAsync(Reference, secret);
                        }
                        break;
                    case 1:
                        using (await store.ReadAsync(Reference))
                        {
                        }
                        break;
                    default:
                        await store.DeleteAsync(Reference);
                        break;
                }
            }));
        }

        await Task.WhenAll(tasks); // must not throw (fake dictionary never mutated concurrently)

        // The gate serialized every native credential operation.
        Assert.Equal(1, _native.MaxObservedConcurrency);

        // Final state is consistent: either absent (a delete won last) or a value actually written.
        using var final = await store.ReadAsync(Reference);
        Assert.True(final is null || final.RevealAsString().Length > 0);
    }

    // Deterministic proof that a read-time migration is atomic against a concurrent write: while the
    // migrating Read holds the gate (blocked inside its neutral write), a concurrent Write cannot run
    // any native operation and cannot complete; only after the migration is released does the write
    // proceed — and it wins (no stale clobber). Uses event signals, not timing-based pacing.
    [Fact]
    public async Task Migration_HoldsGateAgainstConcurrentWrite_NoInterleaveAndWriteWins()
    {
        _native.Seed(LegacyTarget, "legacy-secret");
        var store = Store;

        using var migrationWriteEntered = new ManualResetEventSlim(false);
        using var releaseMigration = new ManualResetEventSlim(false);
        var blockFirstWrite = 1;

        _native.BeforeWrite = _ =>
        {
            // Block only the migration write (the first write, to neutral), keeping the gate held.
            if (Interlocked.Exchange(ref blockFirstWrite, 0) == 1)
            {
                migrationWriteEntered.Set();
                releaseMigration.Wait(5000);
            }
        };

        var reader = Task.Run(async () =>
        {
            using var _ = await store.ReadAsync(Reference); // migrates → first write blocks
        });
        Assert.True(migrationWriteEntered.Wait(5000)); // migration now holds the gate

        // Call WriteAsync directly on this thread: it runs synchronously up to `await _gate.WaitAsync`.
        // Because the migrating read holds the gate, the returned task is provably incomplete right
        // here — no Task.Run/scheduler ambiguity. Without the gate WriteAsync would run the (synchronous)
        // native write inline and the task would already be completed.
        using var secret = new SecretValue("new-secret");
        var writer = store.WriteAsync(Reference, secret);
        Assert.False(writer.IsCompleted);

        releaseMigration.Set();
        await Task.WhenAll(reader, writer);

        // Ordering: the migration's neutral write is logged strictly before the writer's neutral
        // write — the two transactions never interleaved.
        List<string> log;
        lock (_native.OpLog)
        {
            log = new List<string>(_native.OpLog);
        }
        var firstNeutralWrite = log.IndexOf("Write:" + NeutralTarget);
        var lastNeutralWrite = log.LastIndexOf("Write:" + NeutralTarget);
        Assert.True(firstNeutralWrite >= 0 && lastNeutralWrite > firstNeutralWrite);

        // The concurrent write wins: neutral holds the new secret, never the stale legacy value.
        Assert.Equal("new-secret", _native.Reveal(NeutralTarget));
    }

    // Fake Credential Manager keyed by target name, with per-target failure injection.
    private sealed class DictionaryCredentialManagerNative : ICredentialManagerNative, IDisposable
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _failRead = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _failWrite = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _failDelete = new(StringComparer.Ordinal);
        private readonly HashSet<string> _suppressWrite = new(StringComparer.Ordinal);
        private readonly Dictionary<nint, (nint Credential, nint Blob)> _allocations = new();

        public int WriteCount { get; private set; }

        // Serialization instrumentation: the maximum number of native operations ever observed
        // in-flight at the same time. Under the store's gate this must stay at 1.
        private int _active;

        public int MaxObservedConcurrency { get; private set; }

        private void Enter()
        {
            var n = Interlocked.Increment(ref _active);
            if (n > MaxObservedConcurrency)
            {
                MaxObservedConcurrency = n;
            }

            // Widen the in-flight window so a missing gate reliably surfaces as overlap.
            Thread.SpinWait(2000);
        }

        private void Exit() => Interlocked.Decrement(ref _active);

        // Deterministic interleaving hooks: BeforeWrite lets a test hold a specific write inside the
        // native boundary (keeping the store's gate held); OpLog records the exact order of native
        // operations so a test can assert one transaction fully precedes another (no interleave).
        public Action<string>? BeforeWrite { get; set; }

        public List<string> OpLog { get; } = new();

        private void Log(string op, string target)
        {
            lock (OpLog)
            {
                OpLog.Add($"{op}:{target}");
            }
        }

        public void Seed(string target, string secret) => _store[target] = Utf8.GetBytes(secret);

        public bool Contains(string target) => _store.ContainsKey(target);

        public string Reveal(string target) => Utf8.GetString(_store[target]);

        public void FailReadWith(string target, int errorCode) => _failRead[target] = errorCode;

        public void FailWriteWith(string target, int errorCode) => _failWrite[target] = errorCode;

        public void FailDeleteWith(string target, int errorCode) => _failDelete[target] = errorCode;

        public void SuppressWritePersistence(string target) => _suppressWrite.Add(target);

        public bool Write(ref NativeCredential credential, out int errorCode)
        {
            Enter();
            try
            {
                WriteCount++;
                var target = Marshal.PtrToStringUni(credential.TargetName)!;
                Log("Write", target);
                BeforeWrite?.Invoke(target);
                if (_failWrite.TryGetValue(target, out var code))
                {
                    errorCode = code;
                    return false;
                }

                errorCode = 0;
                if (_suppressWrite.Contains(target))
                {
                    return true; // reports success but persists nothing (verification will fail)
                }

                var blob = new byte[checked((int)credential.CredentialBlobSize)];
                Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                _store[target] = blob;
                return true;
            }
            finally
            {
                Exit();
            }
        }

        public bool Read(string targetName, out nint credential, out int errorCode)
        {
            Enter();
            try
            {
                Log("Read", targetName);
                if (_failRead.TryGetValue(targetName, out var code))
                {
                    credential = nint.Zero;
                    errorCode = code;
                    return false;
                }

                if (!_store.TryGetValue(targetName, out var blob))
                {
                    credential = nint.Zero;
                    errorCode = ErrorNotFound;
                    return false;
                }

                errorCode = 0;
                var blobPtr = Marshal.AllocCoTaskMem(blob.Length);
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                var native = new NativeCredential
                {
                    Type = 1,
                    CredentialBlobSize = checked((uint)blob.Length),
                    CredentialBlob = blobPtr
                };
                var credPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeCredential>());
                Marshal.StructureToPtr(native, credPtr, fDeleteOld: false);
                _allocations[credPtr] = (credPtr, blobPtr);
                credential = credPtr;
                return true;
            }
            finally
            {
                Exit();
            }
        }

        public bool Delete(string targetName, out int errorCode)
        {
            Enter();
            try
            {
                Log("Delete", targetName);
                if (_failDelete.TryGetValue(targetName, out var code))
                {
                    errorCode = code;
                    return false;
                }

                if (_store.Remove(targetName))
                {
                    errorCode = 0;
                    return true;
                }

                errorCode = ErrorNotFound;
                return false;
            }
            finally
            {
                Exit();
            }
        }

        public void Free(nint credential)
        {
            if (_allocations.Remove(credential, out var allocation))
            {
                Marshal.FreeCoTaskMem(allocation.Blob);
                Marshal.FreeCoTaskMem(allocation.Credential);
            }
        }

        public void Dispose()
        {
            foreach (var allocation in _allocations.Values)
            {
                Marshal.FreeCoTaskMem(allocation.Blob);
                Marshal.FreeCoTaskMem(allocation.Credential);
            }

            _allocations.Clear();
        }
    }
}
