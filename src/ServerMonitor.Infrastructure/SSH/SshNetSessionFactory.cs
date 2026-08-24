using Renci.SshNet;
using Renci.SshNet.Security;
using ServerMonitor.Core.Models;
using System.Security.Cryptography;

namespace ServerMonitor.Infrastructure.SSH;

internal sealed class SshNetSessionFactory : ISshSessionFactory
{
    private const long MaximumPrivateKeySize = 1024 * 1024;

    public ISshSession CreateHostKeyProbe(Server server, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Create(server, timeout, new NoneAuthenticationMethod(server.Username));
    }

    public ISshSession CreatePasswordSession(Server server, string password, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(password);
        return Create(server, timeout, new PasswordAuthenticationMethod(server.Username, password));
    }

    public ISshSession CreatePrivateKeySession(
        Server server,
        string privateKeyPath,
        string? passphrase,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);

        ModernPrivateKeySource keySource;
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

            keySource = new ModernPrivateKeySource(new PrivateKeyFile(stream, passphrase));
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

        var authentication = new PrivateKeyAuthenticationMethod(server.Username, keySource);
        return Create(server, timeout, authentication, keySource);
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

    private static ISshSession Create(
        Server server,
        TimeSpan timeout,
        Renci.SshNet.AuthenticationMethod authentication,
        IDisposable? authenticationResource = null)
    {
        try
        {
            var connectionInfo = new ConnectionInfo(
                server.Host,
                server.Port,
                server.Username,
                authentication)
            {
                Timeout = timeout,
                ChannelCloseTimeout = TimeSpan.FromSeconds(1)
            };

            SshModernAlgorithmPolicy.Apply(connectionInfo);
            return new SshNetSession(connectionInfo, authentication, authenticationResource);
        }
        catch
        {
            authentication.Dispose();
            authenticationResource?.Dispose();
            throw;
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
