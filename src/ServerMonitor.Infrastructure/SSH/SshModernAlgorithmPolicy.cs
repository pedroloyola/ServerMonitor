using Renci.SshNet;

namespace ServerMonitor.Infrastructure.SSH;

internal static class SshModernAlgorithmPolicy
{
    public static IReadOnlySet<string> KeyExchangeAlgorithms { get; } = CreateSet(
        "mlkem768x25519-sha256",
        "sntrup761x25519-sha512",
        "sntrup761x25519-sha512@openssh.com",
        "curve25519-sha256",
        "curve25519-sha256@libssh.org",
        "ecdh-sha2-nistp256",
        "ecdh-sha2-nistp384",
        "ecdh-sha2-nistp521",
        "diffie-hellman-group-exchange-sha256",
        "diffie-hellman-group16-sha512",
        "diffie-hellman-group18-sha512",
        "diffie-hellman-group14-sha256");

    public static IReadOnlySet<string> EncryptionAlgorithms { get; } = CreateSet(
        "chacha20-poly1305@openssh.com",
        "aes128-gcm@openssh.com",
        "aes256-gcm@openssh.com",
        "aes128-ctr",
        "aes192-ctr",
        "aes256-ctr");

    public static IReadOnlySet<string> HmacAlgorithms { get; } = CreateSet(
        "hmac-sha2-256-etm@openssh.com",
        "hmac-sha2-512-etm@openssh.com",
        "hmac-sha2-256",
        "hmac-sha2-512");

    public static IReadOnlySet<string> HostKeyAlgorithms { get; } = CreateSet(
        "ssh-ed25519-cert-v01@openssh.com",
        "ecdsa-sha2-nistp256-cert-v01@openssh.com",
        "ecdsa-sha2-nistp384-cert-v01@openssh.com",
        "ecdsa-sha2-nistp521-cert-v01@openssh.com",
        "rsa-sha2-512-cert-v01@openssh.com",
        "rsa-sha2-256-cert-v01@openssh.com",
        "ssh-ed25519",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "rsa-sha2-512",
        "rsa-sha2-256");

    public static bool IsPrivateKeySignatureAllowed(string algorithm) =>
        !string.Equals(algorithm, "ssh-rsa", StringComparison.Ordinal);

    public static void Apply(ConnectionInfo connectionInfo)
    {
        ArgumentNullException.ThrowIfNull(connectionInfo);

        Retain(connectionInfo.KeyExchangeAlgorithms, KeyExchangeAlgorithms);
        Retain(connectionInfo.Encryptions, EncryptionAlgorithms);
        Retain(connectionInfo.HmacAlgorithms, HmacAlgorithms);
        Retain(connectionInfo.HostKeyAlgorithms, HostKeyAlgorithms);
    }

    private static void Retain<TValue>(
        IOrderedDictionary<string, TValue> algorithms,
        IReadOnlySet<string> allowed)
    {
        foreach (var name in algorithms.Keys.Where(name => !allowed.Contains(name)).ToArray())
        {
            algorithms.Remove(name);
        }
    }

    private static IReadOnlySet<string> CreateSet(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);
}
