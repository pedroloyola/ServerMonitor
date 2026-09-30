using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ServerMonitor.Core.Backup;
using ServerMonitor.Infrastructure.Backup;

namespace ServerMonitor.Infrastructure.Tests.Backup;

// M14.6 Slice A — the backup file codec against the REAL BCL primitives (no crypto doubles).
// Every rejection that the spec orders before the KDF is asserted together with a KDF spy that must
// NOT have been called (spec §2 canonical order, §9 "Future / incompatible", Vigil C-1/C-2).
public sealed class BackupFileCodecTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private static readonly byte[] KatPlaintext = "{\"schemaVersion\":1}"u8.ToArray();

    // Independent vector: Python hashlib.pbkdf2_hmac + cryptography AESGCM
    // (.boss/tmp/m14.6-A-kat.py), salt = 00..1F, nonce = A0..AB, 600,000 iterations, header as AAD.
    private static readonly byte[] KatFile = Convert.FromHexString(
        "53414C5A42414B0001000101C02709000000000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"
        + "A0A1A2A3A4A5A6A7A8A9AAAB130000008F15C4D886CF158C141CFCCB7EC4AFBAEB8FAB2546CB326D640DFFA2E59C130A"
        + "61A496");

    // Same construction; passphrase is the NFC (precomposed) "café crème brûlée!", plaintext "{}".
    private static readonly byte[] NfcKatFile = Convert.FromHexString(
        "53414C5A42414B0001000101C02709000000000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"
        + "A0A1A2A3A4A5A6A7A8A9AAAB02000000282C6457D820648896E54FB8119FDEA6F922");

    private const string NfcPassphraseComposed = "café crème brûlée!";
    private const string NfcPassphraseDecomposed = "café crème brûlée!";

    private readonly List<int> _kdfCalls = [];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sm-backup-codec-" + Guid.NewGuid().ToString("N"));

    public BackupFileCodecTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------- Known-answer vectors ----------

    [Fact]
    public void Encrypt_WithFixedSaltNonceAndIterations_MatchesIndependentVector()
    {
        var codec = Codec(fixedRandom: true);

        var result = codec.Encrypt(KatPlaintext, Passphrase);

        Assert.True(result.IsSuccess);
        Assert.Equal(Convert.ToHexString(KatFile), Convert.ToHexString(result.File!));
    }

    [Fact]
    public void Decrypt_IndependentVector_ReturnsPlaintext()
    {
        using var result = Codec().Decrypt(KatFile, Passphrase);

        Assert.True(result.IsSuccess);
        Assert.Equal(KatPlaintext, result.Plaintext!.Span.ToArray());
        Assert.Equal(new[] { BackupFileCodec.MinimumIterations }, _kdfCalls);
    }

    [Fact]
    public void Encrypt_DecomposedPassphrase_NormalizesToNfcAndMatchesComposedVector()
    {
        var result = Codec(fixedRandom: true).Encrypt("{}"u8, NfcPassphraseDecomposed);

        Assert.True(result.IsSuccess);
        Assert.Equal(Convert.ToHexString(NfcKatFile), Convert.ToHexString(result.File!));
    }

    [Theory]
    [InlineData(NfcPassphraseComposed)]
    [InlineData(NfcPassphraseDecomposed)]
    public void Decrypt_ComposedOrDecomposedPassphrase_BothOpenTheNfcVector(string passphrase)
    {
        using var result = Codec().Decrypt(NfcKatFile, passphrase);

        Assert.True(result.IsSuccess);
        Assert.Equal("{}"u8.ToArray(), result.Plaintext!.Span.ToArray());
    }

    // ---------- Round trip / production writer ----------

    [Fact]
    public void ProductionCodec_RoundTrips_WithWriterIterationsAndV1Header()
    {
        var codec = new BackupFileCodec();
        var payload = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"servers\":[]}");

        var file = codec.Encrypt(payload, Passphrase).File!;

        Assert.Equal(BackupFileCodec.HeaderLength + payload.Length + BackupFileCodec.TagLength, file.Length);
        Assert.Equal(BackupFileCodec.Magic.ToArray(), file[..8]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8)));
        Assert.Equal(1, file[10]);
        Assert.Equal(1, file[11]);
        Assert.Equal(1_000_000u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(16)));
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(62)));

        using var decoded = codec.Decrypt(file, Passphrase);
        Assert.True(decoded.IsSuccess);
        Assert.Equal(payload, decoded.Plaintext!.Span.ToArray());
    }

    [Fact]
    public void RoundTrip_AtMaximumPlaintext_Succeeds_AndOneByteMoreIsTooLarge()
    {
        var codec = Codec();
        var payload = new byte[BackupFileCodec.MaxPlaintextLength];
        payload.AsSpan().Fill((byte)'a');

        var file = codec.Encrypt(payload, Passphrase).File!;
        Assert.Equal(BackupFileCodec.MaxFileLength, file.Length);
        using (var decoded = codec.Decrypt(file, Passphrase))
        {
            Assert.True(decoded.IsSuccess);
            Assert.True(decoded.Plaintext!.Span.SequenceEqual(payload));
        }

        var tooLarge = codec.Encrypt(new byte[BackupFileCodec.MaxPlaintextLength + 1], Passphrase);
        Assert.Equal(BackupError.TooLarge, tooLarge.Error);
    }

    [Fact]
    public void TwoEncryptsOfTheSamePayload_DifferInSaltNonceAndCiphertext()
    {
        var codec = Codec();

        var first = codec.Encrypt(KatPlaintext, Passphrase).File!;
        var second = codec.Encrypt(KatPlaintext, Passphrase).File!;

        Assert.NotEqual(Salt(first), Salt(second));
        Assert.NotEqual(Nonce(first), Nonce(second));
        Assert.NotEqual(first[BackupFileCodec.HeaderLength..], second[BackupFileCodec.HeaderLength..]);
        using var a = codec.Decrypt(first, Passphrase);
        using var b = codec.Decrypt(second, Passphrase);
        Assert.True(a.IsSuccess && b.IsSuccess);
    }

    // ---------- Wrong passphrase / tampering ----------

    [Fact]
    public void Decrypt_WrongPassphrase_IsWrongPassphraseOrDamaged_AfterExactlyOneKdf()
    {
        using var result = Codec().Decrypt(KatFile, "correct horse battery stapler");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Plaintext);
        Assert.Equal(BackupError.WrongPassphraseOrDamaged, result.Error);
        Assert.Single(_kdfCalls);
    }

    public static TheoryData<int> HeaderOffsets()
    {
        var data = new TheoryData<int>();
        for (var offset = 0; offset < BackupFileCodec.HeaderLength; offset++)
        {
            data.Add(offset);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HeaderOffsets))]
    public void Decrypt_AnyHeaderByteFlipped_IsRejected(int offset)
    {
        var file = (byte[])KatFile.Clone();
        file[offset] ^= 0x01;

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(0, BackupError.NotABackup)]
    [InlineData(7, BackupError.NotABackup)]
    [InlineData(8, BackupError.Damaged)]            // version 1 -> 0
    [InlineData(9, BackupError.IncompatibleVersion)] // version 1 -> 257
    [InlineData(10, BackupError.IncompatibleVersion)]
    [InlineData(11, BackupError.IncompatibleVersion)]
    [InlineData(13, BackupError.Damaged)]            // 600,000 -> 599,744
    [InlineData(16, BackupError.IncompatibleVersion)]
    [InlineData(17, BackupError.IncompatibleVersion)]
    [InlineData(62, BackupError.Damaged)]
    [InlineData(65, BackupError.Damaged)]
    public void Decrypt_StructuralHeaderByteFlipped_IsRejectedWithItsCode_WithoutKdf(int offset, BackupError expected)
    {
        var file = (byte[])KatFile.Clone();
        file[offset] ^= 0x01;

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(expected, result.Error);
        Assert.Empty(_kdfCalls);
    }

    [Theory]
    [InlineData(12)] // iterations stay in range: 600,000 -> 600,001
    [InlineData(18)] // salt
    [InlineData(49)]
    [InlineData(50)] // nonce
    [InlineData(61)]
    public void Decrypt_CryptographicHeaderByteFlipped_FailsAuthentication(int offset)
    {
        var file = (byte[])KatFile.Clone();
        file[offset] ^= 0x01;

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(BackupError.WrongPassphraseOrDamaged, result.Error);
        Assert.Single(_kdfCalls);
    }

    [Theory]
    [InlineData(66)]  // first ciphertext byte
    [InlineData(75)]
    [InlineData(84)]  // last ciphertext byte
    [InlineData(85)]  // first tag byte
    [InlineData(100)] // last tag byte
    public void Decrypt_CiphertextOrTagByteFlipped_IsWrongPassphraseOrDamaged(int offset)
    {
        var file = (byte[])KatFile.Clone();
        Assert.Equal(101, file.Length);
        file[offset] ^= 0x80;

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(BackupError.WrongPassphraseOrDamaged, result.Error);
        Assert.Null(result.Plaintext);
    }

    // AAD proof, writer side: the codec's tag verifies only when the header is supplied as associated
    // data. A header flip alone cannot prove this — every header byte is also bound structurally or via
    // the key/nonce — so the binding is checked directly with an independent AesGcm decrypt.
    [Fact]
    public void Encrypt_BindsTheWholeHeaderAsAssociatedData()
    {
        var file = Codec().Encrypt(KatPlaintext, Passphrase).File!;
        var key = IndependentKey(Passphrase, Salt(file), BackupFileCodec.MinimumIterations);
        var header = file[..BackupFileCodec.HeaderLength];
        var ciphertext = file[BackupFileCodec.HeaderLength..^BackupFileCodec.TagLength];
        var tag = file[^BackupFileCodec.TagLength..];
        var plaintext = new byte[KatPlaintext.Length];

        using var aes = new AesGcm(key, BackupFileCodec.TagLength);
        aes.Decrypt(Nonce(file), ciphertext, tag, plaintext, header);
        Assert.Equal(KatPlaintext, plaintext);

        Assert.Throws<AuthenticationTagMismatchException>(
            () => aes.Decrypt(Nonce(file), ciphertext, tag, plaintext, Array.Empty<byte>()));
    }

    // AAD proof, reader side: a file whose tag was computed WITHOUT the header as associated data (same
    // key, nonce, header bytes) must not open.
    [Fact]
    public void Decrypt_FileAuthenticatedWithoutHeaderAad_IsRejected()
    {
        var file = (byte[])KatFile.Clone();
        var key = IndependentKey(Passphrase, Salt(file), BackupFileCodec.MinimumIterations);
        using (var aes = new AesGcm(key, BackupFileCodec.TagLength))
        {
            aes.Encrypt(
                Nonce(file),
                KatPlaintext,
                file.AsSpan(BackupFileCodec.HeaderLength, KatPlaintext.Length),
                file.AsSpan(BackupFileCodec.HeaderLength + KatPlaintext.Length, BackupFileCodec.TagLength));
        }

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(BackupError.WrongPassphraseOrDamaged, result.Error);
    }

    // ---------- Truncation / trailing bytes / size bounds ----------

    public static TheoryData<int> TruncatedLengths() =>
    [
        0, 1, 7, 8, 10, 12, 16, 18, 50, 62, 65, 66, 67, 81, 82, 83,
        84,   // min file length, but ciphertextLength no longer matches
        85,   // inside ciphertext
        100,  // inside tag (drops last byte)
    ];

    [Theory]
    [MemberData(nameof(TruncatedLengths))]
    public void Decrypt_TruncatedAtAnyRegion_IsDamaged_WithoutKdf(int length)
    {
        using var result = Codec().Decrypt(KatFile.AsSpan(0, length), Passphrase);

        Assert.Equal(BackupError.Damaged, result.Error);
        Assert.Empty(_kdfCalls);
    }

    [Fact]
    public void Decrypt_TrailingByte_IsDamaged_WithoutKdf()
    {
        var file = KatFile.Concat(new byte[] { 0x00 }).ToArray();

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(BackupError.Damaged, result.Error);
        Assert.Empty(_kdfCalls);
    }

    [Fact]
    public void Decrypt_TrailingByteWithAdjustedLengthField_FailsAuthentication()
    {
        var file = KatFile.Concat(new byte[] { 0x00 }).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(BackupFileCodec.CiphertextLengthOffset),
            (uint)(file.Length - BackupFileCodec.HeaderLength - BackupFileCodec.TagLength));

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(BackupError.WrongPassphraseOrDamaged, result.Error);
    }

    [Fact]
    public void Decrypt_LargerThanMaxFile_IsTooLarge_WithoutKdf()
    {
        var file = new byte[BackupFileCodec.MaxFileLength + 1];
        KatFile.AsSpan(0, BackupFileCodec.HeaderLength).CopyTo(file);

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(BackupError.TooLarge, result.Error);
        Assert.Empty(_kdfCalls);
    }

    // ---------- Future / incompatible / weak parameters ----------

    [Theory]
    [InlineData(2, BackupError.IncompatibleVersion)]
    [InlineData(ushort.MaxValue, BackupError.IncompatibleVersion)]
    [InlineData(0, BackupError.Damaged)]
    public void Decrypt_FormatVersion_IsRejectedBeforeKdf(int version, BackupError expected)
    {
        var file = (byte[])KatFile.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(BackupFileCodec.FormatVersionOffset), (ushort)version);

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(expected, result.Error);
        Assert.Empty(_kdfCalls);
    }

    [Theory]
    [InlineData(BackupFileCodec.KdfIdOffset, 2)]
    [InlineData(BackupFileCodec.KdfIdOffset, 0)]
    [InlineData(BackupFileCodec.AeadIdOffset, 2)]
    [InlineData(BackupFileCodec.AeadIdOffset, 0)]
    [InlineData(BackupFileCodec.FlagsOffset, 1)]
    [InlineData(BackupFileCodec.FlagsOffset + 1, 0x80)]
    public void Decrypt_UnknownAlgorithmOrFlags_IsIncompatibleVersion_WithoutKdf(int offset, byte value)
    {
        var file = (byte[])KatFile.Clone();
        file[offset] = value;

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(BackupError.IncompatibleVersion, result.Error);
        Assert.Empty(_kdfCalls);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(599_999u)]
    [InlineData(5_000_001u)]
    [InlineData(uint.MaxValue)]
    public void Decrypt_IterationsOutsideBounds_IsDamaged_WithoutKdf(uint iterations)
    {
        var file = (byte[])KatFile.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(BackupFileCodec.IterationsOffset), iterations);

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(BackupError.Damaged, result.Error);
        Assert.Empty(_kdfCalls);
    }

    [Fact]
    public void Decrypt_IterationsAtUpperBound_ReachesKdf()
    {
        var file = (byte[])KatFile.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(BackupFileCodec.IterationsOffset), 5_000_000u);

        using var result = Codec().Decrypt(file, Passphrase);

        // The KAT was not written with 5M iterations, so the tag cannot verify; the point is that the
        // upper bound itself is inclusive and runs the KDF with exactly that count.
        Assert.Equal(BackupError.WrongPassphraseOrDamaged, result.Error);
        Assert.Equal(new[] { 5_000_000 }, _kdfCalls);
    }

    // A correctly encrypted file with weak parameters is refused on the bound alone — this is the file
    // that would OPEN if the floor were lowered (spec §9.1 #3).
    [Fact]
    public void Decrypt_GenuineFileWrittenWithWeakIterations_IsDamaged_WithoutKdf()
    {
        var weak = new BackupFileCodec(new BackupFileCodecSeams { WriterIterations = 1_000 })
            .Encrypt(KatPlaintext, Passphrase).File!;

        using var result = Codec().Decrypt(weak, Passphrase);

        Assert.Equal(BackupError.Damaged, result.Error);
        Assert.Empty(_kdfCalls);
    }

    [Theory]
    [InlineData("magic+version")]
    [InlineData("version+iterations")]
    [InlineData("kdf+iterations")]
    [InlineData("iterations+length")]
    public void Decrypt_MultipleDefects_ReportTheFirstInCanonicalOrder(string defects)
    {
        var file = (byte[])KatFile.Clone();
        BackupError expected;
        switch (defects)
        {
            case "magic+version":
                file[0] = (byte)'X';
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 2);
                expected = BackupError.NotABackup;
                break;
            case "version+iterations":
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 2);
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 1);
                expected = BackupError.IncompatibleVersion;
                break;
            case "kdf+iterations":
                file[BackupFileCodec.KdfIdOffset] = 2;
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 1);
                expected = BackupError.IncompatibleVersion;
                break;
            default:
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(62), 3);
                expected = BackupError.Damaged;
                break;
        }

        using var result = Codec().Decrypt(file, Passphrase);

        Assert.Equal(expected, result.Error);
        Assert.Empty(_kdfCalls);
    }

    // ---------- Passphrase policy at the codec boundary ----------

    // Built at runtime: attribute strings are stored as UTF-8 in metadata and a lone surrogate would
    // silently become U+FFFD, a valid character.
    public static TheoryData<string, BackupPassphraseProblem> RejectedRestorePassphrases() => new()
    {
        { '\uD800' + " lone high surrogate", BackupPassphraseProblem.InvalidCharacters },
        { "lone low surrogate " + '\uDC00', BackupPassphraseProblem.InvalidCharacters },
        { string.Empty, BackupPassphraseProblem.Empty },
    };

    [Theory]
    [MemberData(nameof(RejectedRestorePassphrases), DisableDiscoveryEnumeration = true)]
    public void Decrypt_PassphraseRejectedByPolicy_IsNotACrash_AndRunsNoKdf(string passphrase, BackupPassphraseProblem expected)
    {
        Assert.Equal(expected == BackupPassphraseProblem.InvalidCharacters, passphrase.Any(char.IsSurrogate));

        using var result = Codec().Decrypt(KatFile, passphrase);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.PassphraseProblem);
        Assert.Empty(_kdfCalls);
    }

    [Fact]
    public void Decrypt_PassphraseOverMaximum_IsTooLong_BeforeKdf()
    {
        using var result = Codec().Decrypt(KatFile, new string('x', BackupPassphrasePolicy.MaximumLength + 1));

        Assert.Equal(BackupPassphraseProblem.TooLong, result.PassphraseProblem);
        Assert.Empty(_kdfCalls);
    }

    [Fact]
    public void Decrypt_StructuralDefectIsReportedBeforePassphraseProblem()
    {
        var file = (byte[])KatFile.Clone();
        file[0] = (byte)'X';

        using var result = Codec().Decrypt(file, '\uD800'.ToString());

        Assert.Equal(BackupError.NotABackup, result.Error);
    }

    public static TheoryData<string, BackupPassphraseProblem> RejectedExportPassphrases() => new()
    {
        { "short", BackupPassphraseProblem.TooShort },
        { '\uDBFF' + " twelve+ chars", BackupPassphraseProblem.InvalidCharacters },
    };

    [Theory]
    [MemberData(nameof(RejectedExportPassphrases), DisableDiscoveryEnumeration = true)]
    public void Encrypt_PassphraseRejectedByPolicy_ProducesNoFile_AndRunsNoKdf(string passphrase, BackupPassphraseProblem expected)
    {
        var result = Codec().Encrypt(KatPlaintext, passphrase);

        Assert.Null(result.File);
        Assert.Equal(expected, result.PassphraseProblem);
        Assert.Empty(_kdfCalls);
    }

    // ---------- Zeroing (Vigil C-2, BK-ROB-2) ----------

    [Fact]
    public void Decrypt_TagMismatch_ZeroesKeyAndPlaintextBuffers()
    {
        var buffers = new List<byte[]>();
        var codec = Codec(allocate: length => Track(buffers, length));

        using var result = codec.Decrypt(KatFile, "correct horse battery stapler");

        Assert.Equal(BackupError.WrongPassphraseOrDamaged, result.Error);
        Assert.Equal(2, buffers.Count);
        Assert.All(buffers, buffer => Assert.All(buffer, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void Decrypt_Success_ZeroesKey_AndPlaintextOnDispose()
    {
        var buffers = new List<byte[]>();
        var codec = Codec(allocate: length => Track(buffers, length));

        var result = codec.Decrypt(KatFile, Passphrase);

        Assert.True(result.IsSuccess);
        var key = Assert.Single(buffers, b => b.Length == BackupFileCodec.KeyLength);
        Assert.All(key, b => Assert.Equal(0, b));
        var plaintext = Assert.Single(buffers, b => b.Length == KatPlaintext.Length);
        Assert.Equal(KatPlaintext, plaintext);

        result.Dispose();

        Assert.All(plaintext, b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => result.Plaintext!.Span.Length);
    }

    [Fact]
    public void Encrypt_ZeroesKey()
    {
        var buffers = new List<byte[]>();
        var codec = Codec(allocate: length => Track(buffers, length));

        Assert.True(codec.Encrypt(KatPlaintext, Passphrase).IsSuccess);

        var key = Assert.Single(buffers);
        Assert.Equal(BackupFileCodec.KeyLength, key.Length);
        Assert.All(key, b => Assert.Equal(0, b));
    }

    // ---------- File read (Vigil C-1, V8) ----------

    [Fact]
    public void DecryptFile_ValidFile_Succeeds()
    {
        var path = WriteFile("valid", KatFile);

        using var result = Codec().DecryptFile(path, Passphrase);

        Assert.True(result.IsSuccess);
        Assert.Equal(KatPlaintext, result.Plaintext!.Span.ToArray());
    }

    [Theory]
    [InlineData(0, BackupError.Damaged)]
    [InlineData(81, BackupError.Damaged)]
    [InlineData(82, BackupError.Damaged)]
    [InlineData(83, BackupError.Damaged)]
    [InlineData(BackupFileCodec.MaxFileLength + 1, BackupError.TooLarge)]
    public void DecryptFile_OutOfBoundsSize_IsRejectedWithoutOpeningTheFile(int length, BackupError expected)
    {
        var path = WriteFile("size-" + length, new byte[length]);
        var opened = 0;
        var codec = Codec(openRead: p =>
        {
            opened++;
            return File.OpenRead(p);
        });

        // An exclusive lock proves no handle is opened: any open would throw a sharing violation.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var result = codec.DecryptFile(path, Passphrase);
            Assert.Equal(expected, result.Error);
        }

        Assert.Equal(0, opened);
        Assert.Empty(_kdfCalls);
    }

    [Fact]
    public void DecryptFile_FileGrewAfterSizeCheck_IsDamaged_AndReadsAtMostOneExtraByte()
    {
        var path = WriteFile("grew", KatFile);
        var grown = new CountingStream(KatFile.Concat(new byte[4096]).ToArray());

        using var result = Codec(openRead: _ => grown).DecryptFile(path, Passphrase);

        Assert.Equal(BackupError.Damaged, result.Error);
        Assert.Equal(KatFile.Length + 1, grown.BytesRead);
        Assert.Empty(_kdfCalls);
    }

    [Fact]
    public void DecryptFile_FileShrankAfterSizeCheck_IsDamaged()
    {
        var path = WriteFile("shrank", KatFile);
        var shrunk = new CountingStream(KatFile[..^1]);

        using var result = Codec(openRead: _ => shrunk).DecryptFile(path, Passphrase);

        Assert.Equal(BackupError.Damaged, result.Error);
        Assert.Empty(_kdfCalls);
    }

    // ---------- helpers ----------

    private BackupFileCodec Codec(
        bool fixedRandom = false,
        Func<int, byte[]>? allocate = null,
        Func<string, Stream>? openRead = null) =>
        new(new BackupFileCodecSeams
        {
            WriterIterations = BackupFileCodec.MinimumIterations,
            FillRandom = fixedRandom ? FixedRandom : null,
            OnKeyDerivation = _kdfCalls.Add,
            AllocateSensitive = allocate,
            OpenRead = openRead,
        });

    // Salt = 00..1F, nonce = A0..AB, matching the independent vector.
    private static void FixedRandom(Span<byte> destination)
    {
        var start = destination.Length == BackupFileCodec.SaltLength ? 0x00 : 0xA0;
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)(start + i);
        }
    }

    private static byte[] Track(List<byte[]> buffers, int length)
    {
        var buffer = new byte[length];
        buffers.Add(buffer);
        return buffer;
    }

    private static byte[] Salt(byte[] file) =>
        file.AsSpan(BackupFileCodec.SaltOffset, BackupFileCodec.SaltLength).ToArray();

    private static byte[] Nonce(byte[] file) =>
        file.AsSpan(BackupFileCodec.NonceOffset, BackupFileCodec.NonceLength).ToArray();

    private static byte[] IndependentKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            BackupFileCodec.KeyLength);

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name + ".serveralyzer-backup");
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class CountingStream(byte[] content) : MemoryStream(content)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        // Span reads on a MemoryStream subclass fall back to Read(byte[], int, int), counted above.
    }
}
