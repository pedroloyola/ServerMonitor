using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace ServerMonitor.Core.Backup;

public enum BackupPassphraseProblem
{
    None,
    Empty,
    TooShort,
    TooLong,

    /// <summary>The text is not valid UTF-16 (for example a lone surrogate from a paste). Reported as a
    /// policy problem, never as a crash or as a damaged file (Vigil C-4).</summary>
    InvalidCharacters,

    ConfirmationMismatch,
}

/// <summary>
/// Passphrase rules for configuration backups (M14.6 spec §6.7, Vigil C-4). Pure: no I/O, no state.
/// <list type="bullet">
/// <item>The passphrase is normalized to Unicode NFC and then encoded as strict UTF-8, so the same typed
/// text derives the same key across input methods and machines.</item>
/// <item>Length is counted in Unicode scalar values of the NFC form: minimum 12, maximum 256. No
/// composition rules and no trimming (NIST SP 800-63B).</item>
/// <item>Export requires a matching confirmation. Restore enforces validity and the maximum (before any
/// key derivation) but not the minimum, so a future change of the minimum never locks out an existing
/// backup.</item>
/// </list>
/// </summary>
public static class BackupPassphrasePolicy
{
    public const int MinimumLength = 12;
    public const int MaximumLength = 256;

    /// <summary>UTF-16 code units accepted before normalizing, so an absurdly long paste is rejected
    /// without running NFC over it. Generous enough that no passphrase of at most
    /// <see cref="MaximumLength"/> scalar values after NFC is rejected by it.</summary>
    public const int MaximumInputLength = MaximumLength * 4;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static BackupPassphraseProblem ValidateForExport(
        ReadOnlySpan<char> passphrase,
        ReadOnlySpan<char> confirmation)
    {
        var problem = Measure(passphrase, out var length);
        if (problem != BackupPassphraseProblem.None)
        {
            return problem;
        }

        if (length < MinimumLength)
        {
            return BackupPassphraseProblem.TooShort;
        }

        if (Measure(confirmation, out _) != BackupPassphraseProblem.None)
        {
            return BackupPassphraseProblem.ConfirmationMismatch;
        }

        var normalizedPassphrase = NormalizePinned(passphrase);
        var normalizedConfirmation = NormalizePinned(confirmation);
        try
        {
            return normalizedPassphrase.AsSpan().SequenceEqual(normalizedConfirmation)
                ? BackupPassphraseProblem.None
                : BackupPassphraseProblem.ConfirmationMismatch;
        }
        finally
        {
            Zero(normalizedPassphrase);
            Zero(normalizedConfirmation);
        }
    }

    public static BackupPassphraseProblem ValidateForRestore(ReadOnlySpan<char> passphrase) =>
        Measure(passphrase, out _);

    /// <summary>
    /// Returns the NFC-normalized passphrase as strict UTF-8 in a pinned array the caller owns and must
    /// zero with <see cref="CryptographicOperations.ZeroMemory"/>. Throws
    /// <see cref="ArgumentException"/> (without echoing the input) when
    /// <see cref="ValidateForRestore"/> would report a problem; callers validate first.
    /// </summary>
    public static byte[] ToNormalizedUtf8(ReadOnlySpan<char> passphrase)
    {
        if (Measure(passphrase, out _) != BackupPassphraseProblem.None)
        {
            throw new ArgumentException(
                "The passphrase does not satisfy the backup passphrase policy.",
                nameof(passphrase));
        }

        var normalized = NormalizePinned(passphrase);
        try
        {
            var bytes = GC.AllocateArray<byte>(StrictUtf8.GetByteCount(normalized), pinned: true);
            try
            {
                StrictUtf8.GetBytes(normalized, bytes);
                return bytes;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw;
            }
        }
        finally
        {
            Zero(normalized);
        }
    }

    private static BackupPassphraseProblem Measure(ReadOnlySpan<char> passphrase, out int length)
    {
        length = 0;
        if (passphrase.IsEmpty)
        {
            return BackupPassphraseProblem.Empty;
        }

        if (passphrase.Length > MaximumInputLength)
        {
            return BackupPassphraseProblem.TooLong;
        }

        // Validate UTF-16 before normalizing: normalization throws on a lone surrogate.
        var remaining = passphrase;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                return BackupPassphraseProblem.InvalidCharacters;
            }

            remaining = remaining[consumed..];
        }

        var normalized = NormalizePinned(passphrase);
        try
        {
            foreach (var _ in ((ReadOnlySpan<char>)normalized).EnumerateRunes())
            {
                length++;
            }
        }
        finally
        {
            Zero(normalized);
        }

        return length > MaximumLength ? BackupPassphraseProblem.TooLong : BackupPassphraseProblem.None;
    }

    // Normalizes into a pinned array so zeroing it removes the only copy (no intermediate string).
    private static char[] NormalizePinned(ReadOnlySpan<char> value)
    {
        var length = value.GetNormalizedLength(NormalizationForm.FormC);
        var buffer = GC.AllocateArray<char>(length, pinned: true);
        if (!value.TryNormalize(buffer, out var written, NormalizationForm.FormC) || written != length)
        {
            Zero(buffer);
            throw new InvalidOperationException("Unicode normalization produced an unexpected length.");
        }

        return buffer;
    }

    private static void Zero(char[] buffer) =>
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
}
