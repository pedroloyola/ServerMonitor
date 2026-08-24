using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Infrastructure.Security;

public sealed class WindowsCredentialStore : IServerCredentialStore
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

    public Task WriteAsync(
        CredentialReference reference,
        SecretValue secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(secret);
        EnsureSupportedPlatform();

        var targetName = CredentialTargetName.Create(reference);
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

        return Task.CompletedTask;
    }

    public Task<SecretValue?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupportedPlatform();

        var targetName = CredentialTargetName.Create(reference);
        if (!_native.Read(targetName, out var credentialPointer, out var errorCode))
        {
            if (errorCode == ErrorNotFound)
            {
                return Task.FromResult<SecretValue?>(null);
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
                    return Task.FromResult<SecretValue?>(new SecretValue(characters));
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

    public Task<bool> DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupportedPlatform();

        var targetName = CredentialTargetName.Create(reference);
        if (_native.Delete(targetName, out var errorCode))
        {
            return Task.FromResult(true);
        }

        if (errorCode == ErrorNotFound)
        {
            return Task.FromResult(false);
        }

        throw new CredentialStoreException(CredentialStoreOperation.Delete, errorCode);
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
