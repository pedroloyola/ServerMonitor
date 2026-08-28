using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Infrastructure.Security;

public sealed class WindowsCredentialStore : IServerCredentialStore, IDisposable
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorInvalidData = 13;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobSize = 5 * 512;

    private readonly ICredentialManagerNative _native;
    private readonly bool _requireWindows;

    // Serializes every credential operation so read-time migration is linearizable: a Read that
    // migrates a legacy credential cannot interleave with a concurrent Write/Delete of the same
    // reference (which would otherwise let stale data clobber a newer write or resurrect a deleted
    // credential). The app is single-instance (ADR-017 §6), so one in-process gate fully serializes
    // access to the per-user Credential Manager. Structural fix, no timing (QUALITY_BAR §6).
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsCredentialStore()
        : this(new CredentialManagerNative(), requireWindows: true)
    {
    }

    internal WindowsCredentialStore(ICredentialManagerNative native)
        : this(native, requireWindows: false)
    {
    }

    private WindowsCredentialStore(ICredentialManagerNative native, bool requireWindows)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _requireWindows = requireWindows;
    }

    public async Task WriteAsync(
        CredentialReference reference,
        SecretValue secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(secret);
        EnsureSupportedPlatform();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // New writes always land in the neutral namespace (ADR-017).
            WriteTarget(CredentialTargetName.Create(reference), secret);

            // Best-effort cleanup of any legacy credential for this reference so an update
            // never leaves a stale personal-namespace secret behind. Non-destructive: the
            // neutral target written above is authoritative regardless of the outcome here.
            TryDeleteTargetSilently(CredentialTargetName.CreateLegacy(reference));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SecretValue?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupportedPlatform();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 1. Prefer the neutral namespace. When present it is authoritative; return it.
            var neutral = ReadTarget(CredentialTargetName.Create(reference));
            if (neutral is not null)
            {
                return neutral;
            }

            // 2. Fall back to the legacy namespace (pre-M12 credentials).
            var legacy = ReadTarget(CredentialTargetName.CreateLegacy(reference));
            if (legacy is null)
            {
                return null;
            }

            // 3. Migrate forward: write+verify the neutral target before removing legacy.
            //    Any failure keeps legacy intact and still returns the working secret, so
            //    authentication continues and the user is never re-prompted (ADR-017 §5).
            //    Holding the gate across the whole migration makes it linearizable against
            //    concurrent Write/Delete of the same reference.
            TryMigrateToNeutral(reference, legacy);
            return legacy;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupportedPlatform();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Remove the neutral target (primary; hard errors surface) and best-effort the
            // legacy one so a deleted server/credential never leaves an orphan behind (§13).
            var deletedNeutral = DeleteTarget(CredentialTargetName.Create(reference));
            var deletedLegacy = TryDeleteTargetSilently(CredentialTargetName.CreateLegacy(reference));

            return deletedNeutral || deletedLegacy;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private void WriteTarget(string targetName, SecretValue secret)
    {
        using var encodedSecret = SensitiveByteBuffer.FromUtf8(secret.Reveal());
        if (encodedSecret.Length == 0)
        {
            throw new ArgumentException("The credential secret cannot be empty.", nameof(secret));
        }

        if (encodedSecret.Length > MaximumCredentialBlobSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                $"The credential secret cannot exceed {MaximumCredentialBlobSize} UTF-8 bytes.");
        }

        var targetPointer = nint.Zero;
        var blobPointer = nint.Zero;
        try
        {
            targetPointer = Marshal.StringToCoTaskMemUni(targetName);
            blobPointer = Marshal.AllocCoTaskMem(encodedSecret.Length);
            Marshal.Copy(encodedSecret.DangerousGetArray(), 0, blobPointer, encodedSecret.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)encodedSecret.Length),
                CredentialBlob = blobPointer,
                Persist = CredentialPersistLocalMachine
            };

            if (!_native.Write(ref credential, out var errorCode))
            {
                throw new CredentialStoreException(CredentialStoreOperation.Write, errorCode);
            }
        }
        finally
        {
            ZeroAndFree(blobPointer, encodedSecret.Length);
            Marshal.FreeCoTaskMem(targetPointer);
        }
    }

    private SecretValue? ReadTarget(string targetName)
    {
        if (!_native.Read(targetName, out var credentialPointer, out var errorCode))
        {
            if (errorCode == ErrorNotFound)
            {
                return null;
            }

            throw new CredentialStoreException(CredentialStoreOperation.Read, errorCode);
        }

        var nativeCredential = default(NativeCredential);
        try
        {
            nativeCredential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (nativeCredential.Type != CredentialTypeGeneric
                || nativeCredential.CredentialBlobSize == 0
                || nativeCredential.CredentialBlobSize > MaximumCredentialBlobSize
                || nativeCredential.CredentialBlob == nint.Zero)
            {
                throw new CredentialStoreException(CredentialStoreOperation.Read, ErrorInvalidData);
            }

            var byteCount = checked((int)nativeCredential.CredentialBlobSize);
            var encodedSecret = new byte[byteCount];
            try
            {
                Marshal.Copy(nativeCredential.CredentialBlob, encodedSecret, 0, byteCount);
                var characterCount = StrictUtf8.GetCharCount(encodedSecret);
                var characters = new char[characterCount];
                try
                {
                    StrictUtf8.GetChars(encodedSecret, characters);
                    return new SecretValue(characters);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encodedSecret);
            }
        }
        catch (DecoderFallbackException)
        {
            throw new CredentialStoreException(CredentialStoreOperation.Read, ErrorInvalidData);
        }
        finally
        {
            if (nativeCredential.CredentialBlob != nint.Zero
                && nativeCredential.CredentialBlobSize is > 0 and <= MaximumCredentialBlobSize)
            {
                ZeroMemory(
                    nativeCredential.CredentialBlob,
                    checked((int)nativeCredential.CredentialBlobSize));
            }

            _native.Free(credentialPointer);
        }
    }

    private bool DeleteTarget(string targetName)
    {
        if (_native.Delete(targetName, out var errorCode))
        {
            return true;
        }

        if (errorCode == ErrorNotFound)
        {
            return false;
        }

        throw new CredentialStoreException(CredentialStoreOperation.Delete, errorCode);
    }

    private bool TryDeleteTargetSilently(string targetName)
    {
        try
        {
            return DeleteTarget(targetName);
        }
        catch (CredentialStoreException)
        {
            // Best-effort cleanup only; the authoritative target is unaffected.
            return false;
        }
    }

    // Writes the secret to the neutral target and verifies it read-backs identically
    // before removing the legacy credential. Never throws: a failed migration leaves the
    // legacy credential in place so the caller can still authenticate with it.
    private void TryMigrateToNeutral(CredentialReference reference, SecretValue legacySecret)
    {
        try
        {
            var neutralTarget = CredentialTargetName.Create(reference);
            WriteTarget(neutralTarget, legacySecret);

            using var verification = ReadTarget(neutralTarget);
            if (verification is null || !verification.Reveal().SequenceEqual(legacySecret.Reveal()))
            {
                // Neutral target not confirmed — keep the legacy credential authoritative.
                return;
            }

            TryDeleteTargetSilently(CredentialTargetName.CreateLegacy(reference));
        }
        catch (CredentialStoreException)
        {
            // Migration is best-effort; legacy credential remains usable.
        }
        catch (ArgumentException)
        {
            // Defensive: legacy blob failed neutral-write validation. Keep legacy.
        }
    }

    private void EnsureSupportedPlatform()
    {
        if (_requireWindows && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is available only on Windows.");
        }
    }

    private static void ZeroAndFree(nint pointer, int length)
    {
        if (pointer == nint.Zero)
        {
            return;
        }

        if (length > 0)
        {
            ZeroMemory(pointer, length);
        }

        Marshal.FreeCoTaskMem(pointer);
    }

    private static void ZeroMemory(nint pointer, int length)
    {
        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }
}
