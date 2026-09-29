using Renci.SshNet;
using Renci.SshNet.Security;
using ServerMonitor.Core.Models;
using System.Security.Cryptography;

namespace ServerMonitor.Infrastructure.SSH;

internal sealed class SshNetSessionFactory : ISshSessionFactory, ISshDialSessionFactory
{
    private const long MaximumPrivateKeySize = 1024 * 1024;

    public ISshSession CreateHostKeyProbe(Server server, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Create(Dial(server), SshLogin.None, timeout, null);
    }

    public ISshSession CreatePasswordSession(Server server, string password, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(password);
        return Create(Dial(server), new SshLogin(SshLoginKind.Password, Password: password), timeout, null);
    }

    public ISshSession CreatePrivateKeySession(
        Server server,
        string privateKeyPath,
        string? passphrase,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);
        return Create(
            Dial(server),
            new SshLogin(SshLoginKind.PrivateKey, PrivateKeyPath: privateKeyPath, Passphrase: passphrase),
            timeout,
            null);
    }

    public ISshSession CreateJumpHostKeyProbe(SshDialTarget jump, TimeSpan timeout) =>
        Create(jump, SshLogin.None, timeout, null);

    public ISshSession Create(SshDialTarget dial, SshLogin login, TimeSpan timeout, ISshConnectGate? gate)
    {
        ArgumentNullException.ThrowIfNull(login);
        var (authentication, resource) = CreateAuthentication(dial.Username, login);
        try
        {
            var connectionInfo = CreateConnectionInfo(dial, authentication, timeout);
            return new SshNetSession(connectionInfo, authentication, resource, gate);
        }
        catch
        {
            authentication.Dispose();
            resource?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the SSH.NET authentication for one login. A private key is loaded here, under the local-file
    /// rules; any load failure is a <see cref="SshPrivateKeyLoadException"/> (or the I/O exception itself).
    /// </summary>
    internal static (Renci.SshNet.AuthenticationMethod Authentication, IDisposable? Resource) CreateAuthentication(
        string username,
        SshLogin login)
    {
        switch (login.Kind)
        {
            case SshLoginKind.None:
                return (new NoneAuthenticationMethod(username), null);
            case SshLoginKind.Password:
                ArgumentNullException.ThrowIfNull(login.Password);
                return (new PasswordAuthenticationMethod(username, login.Password), null);
            case SshLoginKind.PrivateKey:
                ArgumentException.ThrowIfNullOrWhiteSpace(login.PrivateKeyPath);
                var keySource = LoadPrivateKey(login.PrivateKeyPath, login.Passphrase);
                return (new PrivateKeyAuthenticationMethod(username, keySource), keySource);
            default:
                throw new ArgumentOutOfRangeException(nameof(login));
        }
    }

    internal static ConnectionInfo CreateConnectionInfo(
        SshDialTarget dial,
        Renci.SshNet.AuthenticationMethod authentication,
        TimeSpan timeout)
    {
        var connectionInfo = new ConnectionInfo(dial.Host, dial.Port, dial.Username, authentication)
        {
            Timeout = timeout,
            ChannelCloseTimeout = TimeSpan.FromSeconds(1)
        };

        SshModernAlgorithmPolicy.Apply(connectionInfo);
        return connectionInfo;
    }

    private static SshDialTarget Dial(Server server) => new(server.Host, server.Port, server.Username);

    private static ModernPrivateKeySource LoadPrivateKey(string privateKeyPath, string? passphrase)
    {
        try
        {
            var fullPath = Path.GetFullPath(privateKeyPath);
            EnsureLocalRegularFile(fullPath);

            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumPrivateKeySize)
            {
                throw new InvalidDataException("The private-key file size is invalid.");
            }

            return new ModernPrivateKeySource(new PrivateKeyFile(stream, passphrase));
        }
        catch (Exception exception) when (exception is Renci.SshNet.Common.SshException or
                                          CryptographicException or
                                          FormatException or
                                          InvalidDataException or
                                          ArgumentException or
                                          InvalidOperationException or
                                          NotSupportedException)
        {
            throw new SshPrivateKeyLoadException(exception);
        }
    }

    private static void EnsureLocalRegularFile(string fullPath)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Only regular local private-key files are supported.");
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new UnauthorizedAccessException("The private-key path has no local drive root.");
        }

        var driveType = new DriveInfo(root).DriveType;
        if (driveType is not (DriveType.Fixed or DriveType.Removable))
        {
            throw new UnauthorizedAccessException("Network and virtual drives are not supported for private keys.");
        }

        for (var current = fullPath; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("Reparse points are not supported for private keys.");
            }

            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }

    private sealed class ModernPrivateKeySource : IPrivateKeySource, IDisposable
    {
        private readonly PrivateKeyFile _inner;

        public ModernPrivateKeySource(PrivateKeyFile inner)
        {
            _inner = inner;
            HostKeyAlgorithms = inner.HostKeyAlgorithms
                .Where(algorithm => SshModernAlgorithmPolicy.IsPrivateKeySignatureAllowed(algorithm.Name))
                .ToArray();

            if (HostKeyAlgorithms.Count == 0)
            {
                _inner.Dispose();
                throw new NotSupportedException("The private key does not support a modern signature algorithm.");
            }
        }

        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }

        public void Dispose() => _inner.Dispose();
    }
}
