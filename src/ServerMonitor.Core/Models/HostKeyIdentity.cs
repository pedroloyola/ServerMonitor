using System.Security.Cryptography;

namespace ServerMonitor.Core.Models;

public sealed record HostKeyIdentity
{
    public required string Algorithm { get; init; }

    public required string Sha256Fingerprint { get; init; }

    public static HostKeyIdentity Create(string algorithm, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        var payload = fingerprint.Trim();
        if (payload.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            payload = payload[7..];
        }

        payload = payload.TrimEnd('=');
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The SHA-256 fingerprint is not valid Base64.", nameof(fingerprint), exception);
        }

        if (decoded.Length != 32)
        {
            throw new ArgumentException("The SHA-256 fingerprint must contain 32 bytes.", nameof(fingerprint));
        }

        CryptographicOperations.ZeroMemory(decoded);
        return new HostKeyIdentity
        {
            Algorithm = algorithm.Trim(),
            Sha256Fingerprint = $"SHA256:{payload}"
        };
    }

    public bool Matches(HostKeyIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var left = DecodeFingerprint(Sha256Fingerprint);
        var right = DecodeFingerprint(other.Sha256Fingerprint);
        try
        {
            return string.Equals(GetKeyFamily(Algorithm), GetKeyFamily(other.Algorithm), StringComparison.Ordinal)
                && CryptographicOperations.FixedTimeEquals(left, right);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(left);
            CryptographicOperations.ZeroMemory(right);
        }
    }

    private static byte[] DecodeFingerprint(string fingerprint)
    {
        var payload = fingerprint[7..];
        return Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '='));
    }

    private static string GetKeyFamily(string algorithm)
    {
        if (algorithm.StartsWith("rsa-sha2-", StringComparison.Ordinal)
            || algorithm.StartsWith("ssh-rsa", StringComparison.Ordinal))
        {
            return "rsa";
        }

        return algorithm;
    }
}
