using System.Runtime.InteropServices;
using System.Text;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.Infrastructure.Tests.Security;

public sealed class WindowsCredentialStoreTests
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotFound = 1168;

    [Theory]
    [InlineData(ServerCredentialKind.Password, "password")]
    [InlineData(ServerCredentialKind.PrivateKeyPassphrase, "key-passphrase")]
    public void TargetName_UsesOnlyScopedIdentifiers(
        ServerCredentialKind kind,
        string expectedKind)
    {
        var serverId = Guid.Parse("40be3d25-ef62-4c5e-8d4d-d1164cc722f1");
        var referenceId = Guid.Parse("443065c4-2549-450f-85dc-5ec020cf575a");
        var reference = new CredentialReference(serverId, kind, referenceId);

        var target = CredentialTargetName.Create(reference);

        Assert.Equal(
            $"{CredentialTargetName.ProductionPrefix}:{serverId:N}:{expectedKind}:{referenceId:N}",
            target);
        Assert.DoesNotContain("host", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user", target, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidReferences))]
    public void TargetName_RejectsInvalidReference(CredentialReference reference)
    {
        Assert.Throws<ArgumentException>(() => CredentialTargetName.Create(reference));
    }

    [Fact]
    public async Task WriteAsync_WritesGenericLocalMachineCredentialWithUtf8Secret()
    {
        var native = new FakeCredentialManagerNative();
        var store = new WindowsCredentialStore(native);
        var reference = CreateReference();
        using var secret = new SecretValue("pässphrase-密碼");

        await store.WriteAsync(reference, secret);

        Assert.Equal(1u, native.WrittenCredentialType);
        Assert.Equal(2u, native.WrittenPersistence);
        Assert.Equal(CredentialTargetName.Create(reference), native.WrittenTargetName);
        Assert.Equal("pässphrase-密碼", Encoding.UTF8.GetString(native.WrittenBlob!));
    }

    [Fact]
    public async Task WriteAsync_RejectsEmptySecretBeforeNativeCall()
    {
        var native = new FakeCredentialManagerNative();
        var store = new WindowsCredentialStore(native);
        using var secret = new SecretValue(ReadOnlySpan<char>.Empty);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteAsync(CreateReference(), secret));

        Assert.Equal(0, native.WriteCallCount);
    }

    [Fact]
    public async Task WriteAsync_RejectsBlobBeyondCredentialManagerLimit()
    {
        var native = new FakeCredentialManagerNative();
        var store = new WindowsCredentialStore(native);
        using var secret = new SecretValue(new string('a', 2561));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.WriteAsync(CreateReference(), secret));

        Assert.Equal(0, native.WriteCallCount);
    }

    [Fact]
    public async Task WriteAsync_ThrowsTypedRedactedException()
    {
        var native = new FakeCredentialManagerNative
        {
            WriteSucceeds = false,
            WriteErrorCode = ErrorAccessDenied
        };
        var store = new WindowsCredentialStore(native);
        const string secretText = "do-not-log-this-secret";
        using var secret = new SecretValue(secretText);

        var exception = await Assert.ThrowsAsync<CredentialStoreException>(() =>
            store.WriteAsync(CreateReference(), secret));

        Assert.Equal(CredentialStoreOperation.Write, exception.Operation);
        Assert.Equal(ErrorAccessDenied, exception.NativeErrorCode);
        Assert.DoesNotContain(secretText, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullWhenCredentialIsMissing()
    {
        var native = new FakeCredentialManagerNative
        {
            ReadSucceeds = false,
            ReadErrorCode = ErrorNotFound
        };
        var store = new WindowsCredentialStore(native);

        var result = await store.ReadAsync(CreateReference());

        Assert.Null(result);
        Assert.Equal(0, native.FreeCallCount);
    }

    [Fact]
    public async Task ReadAsync_ReturnsSecretAndAlwaysFreesNativeCredential()
    {
        var native = new FakeCredentialManagerNative();
        native.SetReadCredential(Encoding.UTF8.GetBytes("sëcret-金"));
        var store = new WindowsCredentialStore(native);

        using var result = await store.ReadAsync(CreateReference());

        Assert.NotNull(result);
        Assert.Equal("sëcret-金", result.RevealAsString());
        Assert.Equal(1, native.FreeCallCount);
        Assert.True(native.FreedBlobWasZeroed);
    }

    [Fact]
    public async Task ReadAsync_RejectsMalformedUtf8AndFreesNativeCredential()
    {
        var native = new FakeCredentialManagerNative();
        native.SetReadCredential([0xC3, 0x28]);
        var store = new WindowsCredentialStore(native);

        var exception = await Assert.ThrowsAsync<CredentialStoreException>(() =>
            store.ReadAsync(CreateReference()));

        Assert.Equal(CredentialStoreOperation.Read, exception.Operation);
        Assert.Equal(13, exception.NativeErrorCode);
        Assert.Equal(1, native.FreeCallCount);
        Assert.True(native.FreedBlobWasZeroed);
    }

    [Theory]
    [InlineData(2u, 1u)]
    [InlineData(1u, 0u)]
    [InlineData(1u, 2561u)]
    public async Task ReadAsync_RejectsInvalidNativeCredentialAndFreesIt(
        uint type,
        uint blobSize)
    {
        var native = new FakeCredentialManagerNative();
        native.SetReadCredential([1], type, blobSize);
        var store = new WindowsCredentialStore(native);

        var exception = await Assert.ThrowsAsync<CredentialStoreException>(() =>
            store.ReadAsync(CreateReference()));

        Assert.Equal(CredentialStoreOperation.Read, exception.Operation);
        Assert.Equal(13, exception.NativeErrorCode);
        Assert.Equal(1, native.FreeCallCount);
    }

    [Fact]
    public async Task ReadAsync_ThrowsTypedNativeError()
    {
        var native = new FakeCredentialManagerNative
        {
            ReadSucceeds = false,
            ReadErrorCode = ErrorAccessDenied
        };
        var store = new WindowsCredentialStore(native);

        var exception = await Assert.ThrowsAsync<CredentialStoreException>(() =>
            store.ReadAsync(CreateReference()));

        Assert.Equal(CredentialStoreOperation.Read, exception.Operation);
        Assert.Equal(ErrorAccessDenied, exception.NativeErrorCode);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsTrueWhenDeleted()
    {
        var native = new FakeCredentialManagerNative();
        var store = new WindowsCredentialStore(native);

        var deleted = await store.DeleteAsync(CreateReference());

        Assert.True(deleted);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalseWhenCredentialIsMissing()
    {
        var native = new FakeCredentialManagerNative
        {
            DeleteSucceeds = false,
            DeleteErrorCode = ErrorNotFound
        };
        var store = new WindowsCredentialStore(native);

        var deleted = await store.DeleteAsync(CreateReference());

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAsync_ThrowsTypedNativeError()
    {
        var native = new FakeCredentialManagerNative
        {
            DeleteSucceeds = false,
            DeleteErrorCode = ErrorAccessDenied
        };
        var store = new WindowsCredentialStore(native);

        var exception = await Assert.ThrowsAsync<CredentialStoreException>(() =>
            store.DeleteAsync(CreateReference()));

        Assert.Equal(CredentialStoreOperation.Delete, exception.Operation);
        Assert.Equal(ErrorAccessDenied, exception.NativeErrorCode);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("delete")]
    public async Task Operations_HonorPreCanceledTokenWithoutCallingNative(string operation)
    {
        var native = new FakeCredentialManagerNative();
        var store = new WindowsCredentialStore(native);
        using var secret = new SecretValue("secret");
        var token = new CancellationToken(canceled: true);

        Task Operation() => operation switch
        {
            "write" => store.WriteAsync(CreateReference(), secret, token),
            "read" => store.ReadAsync(CreateReference(), token),
            "delete" => store.DeleteAsync(CreateReference(), token),
            _ => throw new InvalidOperationException()
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(Operation);
        Assert.Equal(0, native.TotalCallCount);
    }

    [Fact]
    public void SensitiveByteBuffer_ZeroesBackingArrayOnDispose()
    {
        var buffer = SensitiveByteBuffer.FromUtf8("sensitive".AsSpan());
        var backingArray = buffer.DangerousGetArray();
        Assert.Contains(backingArray, value => value != 0);

        buffer.Dispose();

        Assert.All(backingArray, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => buffer.DangerousGetArray());
    }

    [Fact]
    public async Task RealCredentialManager_RoundTripsInIsolatedTargetAndCleansUp()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialStore();
        var reference = CredentialReference.Create(Guid.NewGuid(), ServerCredentialKind.Password);
        var secretText = $"ServerMonitor integration {Guid.NewGuid():N} 密碼";

        try
        {
            using (var secret = new SecretValue(secretText))
            {
                await store.WriteAsync(reference, secret);
            }

            using var recovered = await store.ReadAsync(reference);
            Assert.NotNull(recovered);
            Assert.Equal(secretText, recovered.RevealAsString());
        }
        finally
        {
            await store.DeleteAsync(reference, CancellationToken.None);
        }

        Assert.Null(await store.ReadAsync(reference));
    }

    public static TheoryData<CredentialReference> InvalidReferences => new()
    {
        new CredentialReference(Guid.Empty, ServerCredentialKind.Password, Guid.NewGuid()),
        new CredentialReference(Guid.NewGuid(), ServerCredentialKind.Password, Guid.Empty),
        new CredentialReference(Guid.NewGuid(), (ServerCredentialKind)999, Guid.NewGuid())
    };

    private static CredentialReference CreateReference() =>
        CredentialReference.Create(Guid.NewGuid(), ServerCredentialKind.Password);

    private sealed class FakeCredentialManagerNative : ICredentialManagerNative
    {
        private nint _readCredential;
        private nint _readBlob;
        private int _readBlobLength;

        public bool WriteSucceeds { get; init; } = true;

        public int WriteErrorCode { get; init; }

        public bool ReadSucceeds { get; init; } = true;

        public int ReadErrorCode { get; init; }

        public bool DeleteSucceeds { get; init; } = true;

        public int DeleteErrorCode { get; init; }

        public int WriteCallCount { get; private set; }

        public int ReadCallCount { get; private set; }

        public int DeleteCallCount { get; private set; }

        public int FreeCallCount { get; private set; }

        public int TotalCallCount => WriteCallCount + ReadCallCount + DeleteCallCount;

        public string? WrittenTargetName { get; private set; }

        public uint WrittenCredentialType { get; private set; }

        public uint WrittenPersistence { get; private set; }

        public byte[]? WrittenBlob { get; private set; }

        public bool FreedBlobWasZeroed { get; private set; }

        public bool Write(ref NativeCredential credential, out int errorCode)
        {
            WriteCallCount++;
            errorCode = WriteErrorCode;
            if (!WriteSucceeds)
            {
                return false;
            }

            WrittenTargetName = Marshal.PtrToStringUni(credential.TargetName);
            WrittenCredentialType = credential.Type;
            WrittenPersistence = credential.Persist;
            WrittenBlob = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(
                credential.CredentialBlob,
                WrittenBlob,
                0,
                WrittenBlob.Length);
            return true;
        }

        public bool Read(string targetName, out nint credential, out int errorCode)
        {
            ReadCallCount++;
            errorCode = ReadErrorCode;
            credential = ReadSucceeds ? _readCredential : nint.Zero;
            return ReadSucceeds;
        }

        public bool Delete(string targetName, out int errorCode)
        {
            DeleteCallCount++;
            errorCode = DeleteErrorCode;
            return DeleteSucceeds;
        }

        public void Free(nint credential)
        {
            FreeCallCount++;
            if (_readBlob != nint.Zero)
            {
                var bytes = new byte[_readBlobLength];
                Marshal.Copy(_readBlob, bytes, 0, bytes.Length);
                FreedBlobWasZeroed = bytes.All(value => value == 0);
                Marshal.FreeCoTaskMem(_readBlob);
                _readBlob = nint.Zero;
            }

            if (_readCredential != nint.Zero)
            {
                Marshal.FreeCoTaskMem(_readCredential);
                _readCredential = nint.Zero;
            }
        }

        public void SetReadCredential(
            byte[] blob,
            uint type = 1,
            uint? reportedBlobSize = null)
        {
            _readBlobLength = blob.Length;
            _readBlob = Marshal.AllocCoTaskMem(blob.Length);
            Marshal.Copy(blob, 0, _readBlob, blob.Length);

            var nativeCredential = new NativeCredential
            {
                Type = type,
                CredentialBlobSize = reportedBlobSize ?? checked((uint)blob.Length),
                CredentialBlob = _readBlob
            };

            _readCredential = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeCredential>());
            Marshal.StructureToPtr(nativeCredential, _readCredential, fDeleteOld: false);
        }
    }
}
