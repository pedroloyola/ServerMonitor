using Renci.SshNet;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Infrastructure.SSH;

internal sealed class SshNetSession(
    ConnectionInfo connectionInfo,
    Renci.SshNet.AuthenticationMethod authentication,
    IDisposable? authenticationResource) : ISshSession
{
    private readonly SshClient _client = new(connectionInfo);
    private bool _disposed;

    public Task<SshSessionResult> ConnectAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken) =>
        RunAsync(detectOperatingSystem: false, hostKeyVerifier, cancellationToken);

    public Task<SshSessionResult> DetectOperatingSystemAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken) =>
        RunAsync(detectOperatingSystem: true, hostKeyVerifier, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _client.Dispose();
        authentication.Dispose();
        authenticationResource?.Dispose();
        _disposed = true;
    }

    private async Task<SshSessionResult> RunAsync(
        bool detectOperatingSystem,
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);
        ObjectDisposedException.ThrowIf(_disposed, this);

        HostKeyIdentity? presentedHostKey = null;
        var hostKeyWasRejected = false;

        void OnHostKeyReceived(object? sender, Renci.SshNet.Common.HostKeyEventArgs args)
        {
            args.CanTrust = false;
            try
            {
                presentedHostKey = HostKeyIdentity.Create(
                    args.HostKeyName,
                    $"SHA256:{args.FingerPrintSHA256}");
                args.CanTrust = hostKeyVerifier(presentedHostKey);
                hostKeyWasRejected = !args.CanTrust;
            }
            catch
            {
                args.CanTrust = false;
                hostKeyWasRejected = true;
            }
        }

        _client.HostKeyReceived += OnHostKeyReceived;
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var detectedOperatingSystem = ServerOperatingSystem.Unknown;
            if (detectOperatingSystem)
            {
                using var command = _client.CreateCommand("uname -s");
                command.CommandTimeout = connectionInfo.Timeout;
                await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                detectedOperatingSystem = SshOperatingSystemParser.ParseUname(command.Result);
            }

            return new SshSessionResult
            {
                ErrorCode = SshConnectionErrorCode.None,
                PresentedHostKey = presentedHostKey,
                DetectedOperatingSystem = detectedOperatingSystem
            };
        }
        catch (Exception exception)
        {
            return new SshSessionResult
            {
                ErrorCode = hostKeyWasRejected
                    ? SshConnectionErrorCode.HostKeyMismatch
                    : SshExceptionMapper.Map(exception),
                PresentedHostKey = presentedHostKey,
                ExceptionType = exception.GetType().Name
            };
        }
        finally
        {
            _client.HostKeyReceived -= OnHostKeyReceived;
            if (_client.IsConnected)
            {
                try
                {
                    _client.Disconnect();
                }
                catch
                {
                    // The result of the completed operation must not be replaced by
                    // a best-effort disconnect failure.
                }
            }
        }
    }
}
