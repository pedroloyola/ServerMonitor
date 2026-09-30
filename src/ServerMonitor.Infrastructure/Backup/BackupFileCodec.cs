using System.Buffers.Binary;
using System.Security.Cryptography;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.Infrastructure.Backup;

/// <summary>
/// Encodes and decodes the <c>.serveralyzer-backup</c> file format v1 (M14.6 spec §2): a 66-byte
/// header that is entirely AES-GCM associated data, then the ciphertext, then a 16-byte tag. The key is
/// PBKDF2-HMAC-SHA256 over the NFC/strict-UTF-8 passphrase and a per-file random salt. BCL primitives
/// only (Vigil C-13); the only custom code is the framing.
/// <para>
/// Decoding is fail-closed in the canonical order: size → magic → formatVersion → kdfId/aeadId/flags →
/// iteration bounds → ciphertextLength → passphrase policy → KDF → tag. Nothing before the KDF spends
/// CPU on attacker-chosen parameters. Salt and nonce are generated inside every <see cref="Encrypt"/>
/// call (Vigil C-3), so a retry never reuses them. The derived key and any failed plaintext are zeroed
/// in <c>finally</c> on every path, including a tag mismatch (Vigil C-2).
/// </para>
/// <para>
/// This type does not log and does not throw content-bearing messages; failures are
/// <see cref="BackupError"/> codes. File-system exceptions from <see cref="DecryptFile"/> (missing
/// file, access denied) propagate for the caller to map.
/// </para>
/// </summary>
internal sealed class BackupFileCodec
{
    internal const int MagicLength = 8;
    internal const int HeaderLength = 66;
    internal const int TagLength = 16;
    internal const int SaltLength = 32;
    internal const int NonceLength = 12;
    internal const int KeyLength = 32;

    internal const int FormatVersionOffset = 8;
    internal const int KdfIdOffset = 10;
    internal const int AeadIdOffset = 11;
    internal const int IterationsOffset = 12;
    internal const int FlagsOffset = 16;
    internal const int SaltOffset = 18;
    internal const int NonceOffset = 50;
    internal const int CiphertextLengthOffset = 62;

    internal const ushort CurrentFormatVersion = 1;
    internal const byte KdfPbkdf2HmacSha256 = 1;
    internal const byte AeadAes256Gcm = 1;

    internal const int WriterIterations = 1_000_000;
    internal const int MinimumIterations = 600_000;
    internal const int MaximumIterations = 5_000_000;

    internal const int MaxPlaintextLength = 4 * 1024 * 1024;
    internal const int MinPlaintextLength = 2;
    internal const int MaxFileLength = MaxPlaintextLength + HeaderLength + TagLength;
    internal const int MinFileLength = MinPlaintextLength + HeaderLength + TagLength;

    // "SALZBAK\0"
    internal static ReadOnlySpan<byte> Magic => [0x53, 0x41, 0x4C, 0x5A, 0x42, 0x41, 0x4B, 0x00];

    private readonly int _writerIterations;
    private readonly BackupRandomFill _fillRandom;
    private readonly Action<int>? _onKeyDerivation;
    private readonly Func<int, byte[]> _allocateSensitive;
    private readonly Func<string, Stream> _openRead;

    public BackupFileCodec()
        : this(new BackupFileCodecSeams())
    {
    }

    /// <summary>Test-only seams (salt/nonce/iteration injection, KDF spy, buffer capture, stream
    /// substitution). Internal and never registered in DI; the reader's bounds are constants and are not
    /// affected by any seam.</summary>
    internal BackupFileCodec(BackupFileCodecSeams seams)
    {
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seams.WriterIterations);
        _writerIterations = seams.WriterIterations;
        _fillRandom = seams.FillRandom ?? RandomNumberGenerator.Fill;
        _onKeyDerivation = seams.OnKeyDerivation;
        _allocateSensitive = seams.AllocateSensitive ?? AllocatePinned;
        _openRead = seams.OpenRead ?? OpenFileForRead;
    }

    public BackupEncodeResult Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<char> passphrase)
    {
        if (!IsPlatformSupported())
        {
            return BackupEncodeResult.Failed(BackupError.Unsupported);
        }

        if (plaintext.Length > MaxPlaintextLength)
        {
            return BackupEncodeResult.Failed(BackupError.TooLarge);
        }

        if (plaintext.Length < MinPlaintextLength)
        {
            throw new ArgumentException("The backup payload is shorter than the format minimum.", nameof(plaintext));
        }

        var problem = BackupPassphrasePolicy.ValidateForExport(passphrase, passphrase);
        if (problem != BackupPassphraseProblem.None)
        {
            return BackupEncodeResult.PassphraseRejected(problem);
        }

        var file = new byte[HeaderLength + plaintext.Length + TagLength];
        var header = file.AsSpan(0, HeaderLength);
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[FormatVersionOffset..], CurrentFormatVersion);
        header[KdfIdOffset] = KdfPbkdf2HmacSha256;
        header[AeadIdOffset] = AeadAes256Gcm;
        BinaryPrimitives.WriteUInt32LittleEndian(header[IterationsOffset..], (uint)_writerIterations);
        BinaryPrimitives.WriteUInt16LittleEndian(header[FlagsOffset..], 0);
        _fillRandom(header.Slice(SaltOffset, SaltLength));
        _fillRandom(header.Slice(NonceOffset, NonceLength));
        BinaryPrimitives.WriteUInt32LittleEndian(header[CiphertextLengthOffset..], (uint)plaintext.Length);

        var key = _allocateSensitive(KeyLength);
        byte[]? passphraseBytes = null;
        try
        {
            passphraseBytes = BackupPassphrasePolicy.ToNormalizedUtf8(passphrase);
            DeriveKey(passphraseBytes, header.Slice(SaltOffset, SaltLength), _writerIterations, key);
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(
                header.Slice(NonceOffset, NonceLength),
                plaintext,
                file.AsSpan(HeaderLength, plaintext.Length),
                file.AsSpan(HeaderLength + plaintext.Length, TagLength),
                header);
            return BackupEncodeResult.Succeeded(file);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (passphraseBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passphraseBytes);
            }
        }
    }

    /// <summary>
    /// Reads and decrypts a backup file. The size comes from <see cref="FileInfo.Length"/> and is bounded
    /// before the file is opened; the read is capped at the stated length plus one byte and must end
    /// exactly there, so a file that grew or shrank after the size check is rejected (Vigil C-1, V8).
    /// </summary>
    public BackupDecodeResult DecryptFile(string path, ReadOnlySpan<char> passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var length = new FileInfo(path).Length;
        if (CheckSize(length) is { } sizeError)
        {
            return BackupDecodeResult.Failed(sizeError);
        }

        var file = new byte[length];
        using (var stream = _openRead(path))
        {
            if (stream.ReadAtLeast(file, file.Length, throwOnEndOfStream: false) != file.Length)
            {
                return BackupDecodeResult.Failed(BackupError.Damaged);
            }

            Span<byte> overflow = stackalloc byte[1];
            if (stream.Read(overflow) != 0)
            {
                return BackupDecodeResult.Failed(BackupError.Damaged);
            }
        }

        return Decrypt(file, passphrase);
    }

    public BackupDecodeResult Decrypt(ReadOnlySpan<byte> file, ReadOnlySpan<char> passphrase)
    {
        if (!IsPlatformSupported())
        {
            return BackupDecodeResult.Failed(BackupError.Unsupported);
        }

        if (CheckStructure(file, out var iterations, out var ciphertextLength) is { } structuralError)
        {
            return BackupDecodeResult.Failed(structuralError);
        }

        var problem = BackupPassphrasePolicy.ValidateForRestore(passphrase);
        if (problem != BackupPassphraseProblem.None)
        {
            return BackupDecodeResult.PassphraseRejected(problem);
        }

        var header = file[..HeaderLength];
        var key = _allocateSensitive(KeyLength);
        var plaintext = _allocateSensitive(ciphertextLength);
        byte[]? passphraseBytes = null;
        var succeeded = false;
        try
        {
            passphraseBytes = BackupPassphrasePolicy.ToNormalizedUtf8(passphrase);
            DeriveKey(passphraseBytes, header.Slice(SaltOffset, SaltLength), iterations, key);
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(
                header.Slice(NonceOffset, NonceLength),
                file.Slice(HeaderLength, ciphertextLength),
                file.Slice(HeaderLength + ciphertextLength, TagLength),
                plaintext,
                header);
            succeeded = true;
            return BackupDecodeResult.Succeeded(new BackupPlaintext(plaintext));
        }
        catch (AuthenticationTagMismatchException)
        {
            return BackupDecodeResult.Failed(BackupError.WrongPassphraseOrDamaged);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (passphraseBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passphraseBytes);
            }

            if (!succeeded)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    // C-2: AES-GCM must be available and must support the format's 16-byte tag.
    private static bool IsPlatformSupported()
    {
        if (!AesGcm.IsSupported)
        {
            return false;
        }

        var sizes = AesGcm.TagByteSizes;
        return TagLength >= sizes.MinSize
            && TagLength <= sizes.MaxSize
            && (sizes.SkipSize == 0 ? TagLength == sizes.MinSize : (TagLength - sizes.MinSize) % sizes.SkipSize == 0);
    }

    private static BackupError? CheckSize(long length)
    {
        if (length < MinFileLength)
        {
            return BackupError.Damaged;
        }

        return length > MaxFileLength ? BackupError.TooLarge : null;
    }

    // Every check the spec orders before the KDF. Returns null when the structure is acceptable.
    private static BackupError? CheckStructure(ReadOnlySpan<byte> file, out int iterations, out int ciphertextLength)
    {
        iterations = 0;
        ciphertextLength = 0;

        if (CheckSize(file.Length) is { } sizeError)
        {
            return sizeError;
        }

        if (!file[..MagicLength].SequenceEqual(Magic))
        {
            return BackupError.NotABackup;
        }

        var formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(file[FormatVersionOffset..]);
        if (formatVersion == 0)
        {
            return BackupError.Damaged;
        }

        if (formatVersion != CurrentFormatVersion)
        {
            return BackupError.IncompatibleVersion;
        }

        if (file[KdfIdOffset] != KdfPbkdf2HmacSha256
            || file[AeadIdOffset] != AeadAes256Gcm
            || BinaryPrimitives.ReadUInt16LittleEndian(file[FlagsOffset..]) != 0)
        {
            return BackupError.IncompatibleVersion;
        }

        var storedIterations = BinaryPrimitives.ReadUInt32LittleEndian(file[IterationsOffset..]);
        if (storedIterations < MinimumIterations || storedIterations > MaximumIterations)
        {
            return BackupError.Damaged;
        }

        var storedCiphertextLength = BinaryPrimitives.ReadUInt32LittleEndian(file[CiphertextLengthOffset..]);
        if (storedCiphertextLength != (uint)(file.Length - HeaderLength - TagLength)
            || storedCiphertextLength > MaxPlaintextLength)
        {
            return BackupError.Damaged;
        }

        iterations = (int)storedIterations;
        ciphertextLength = (int)storedCiphertextLength;
        return null;
    }

    private void DeriveKey(ReadOnlySpan<byte> passphrase, ReadOnlySpan<byte> salt, int iterations, Span<byte> key)
    {
        _onKeyDerivation?.Invoke(iterations);
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, iterations, HashAlgorithmName.SHA256);
    }

    private static byte[] AllocatePinned(int length) => GC.AllocateArray<byte>(length, pinned: true);

    private static Stream OpenFileForRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1,
        FileOptions.SequentialScan);
}

internal delegate void BackupRandomFill(Span<byte> destination);

/// <summary>Test seams for <see cref="BackupFileCodec"/>. Defaults are the production behaviour.</summary>
internal sealed class BackupFileCodecSeams
{
    public int WriterIterations { get; init; } = BackupFileCodec.WriterIterations;

    public BackupRandomFill? FillRandom { get; init; }

    /// <summary>Invoked with the iteration count immediately before every key derivation.</summary>
    public Action<int>? OnKeyDerivation { get; init; }

    /// <summary>Allocates the key and plaintext buffers (default: pinned arrays).</summary>
    public Func<int, byte[]>? AllocateSensitive { get; init; }

    public Func<string, Stream>? OpenRead { get; init; }
}

internal readonly record struct BackupEncodeResult(
    byte[]? File,
    BackupError? Error,
    BackupPassphraseProblem PassphraseProblem)
{
    public bool IsSuccess => File is not null;

    public static BackupEncodeResult Succeeded(byte[] file) => new(file, null, BackupPassphraseProblem.None);

    public static BackupEncodeResult Failed(BackupError error) => new(null, error, BackupPassphraseProblem.None);

    public static BackupEncodeResult PassphraseRejected(BackupPassphraseProblem problem) => new(null, null, problem);
}

/// <summary>Outcome of decoding a backup. Owns the plaintext on success; disposing zeroes it.</summary>
internal sealed class BackupDecodeResult : IDisposable
{
    private BackupDecodeResult(BackupPlaintext? plaintext, BackupError? error, BackupPassphraseProblem problem)
    {
        Plaintext = plaintext;
        Error = error;
        PassphraseProblem = problem;
    }

    public BackupPlaintext? Plaintext { get; }

    public BackupError? Error { get; }

    public BackupPassphraseProblem PassphraseProblem { get; }

    public bool IsSuccess => Plaintext is not null;

    public static BackupDecodeResult Succeeded(BackupPlaintext plaintext) =>
        new(plaintext, null, BackupPassphraseProblem.None);

    public static BackupDecodeResult Failed(BackupError error) =>
        new(null, error, BackupPassphraseProblem.None);

    public static BackupDecodeResult PassphraseRejected(BackupPassphraseProblem problem) =>
        new(null, null, problem);

    public void Dispose() => Plaintext?.Dispose();
}

/// <summary>Decrypted payload in a pinned buffer; <see cref="Dispose"/> zeroes it.</summary>
internal sealed class BackupPlaintext : IDisposable
{
    private byte[]? _bytes;

    internal BackupPlaintext(byte[] bytes)
    {
        _bytes = bytes;
    }

    public ReadOnlySpan<byte> Span => _bytes ?? throw new ObjectDisposedException(nameof(BackupPlaintext));

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public override string ToString() => "[REDACTED]";
}
