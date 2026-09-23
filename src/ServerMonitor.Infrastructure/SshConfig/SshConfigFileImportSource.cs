using System.Text;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.Infrastructure.SshConfig;

/// <summary>
/// Reads <c>~/.ssh/config</c> strictly read-only (<see cref="FileAccess.Read"/>, sharing
/// read/write so an editor holding the file is not disturbed), bounded to
/// <see cref="MaxBytes"/>, as strict UTF-8, and hands the text to the pure Core resolver.
/// It never creates, writes or opens any other file — in particular never an IdentityFile.
/// </summary>
public sealed class SshConfigFileImportSource : ISshConfigImportSource
{
    public const int MaxBytes = 256 * 1024;

    internal delegate Stream FileOpener(string path, FileMode mode, FileAccess access, FileShare share);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string _userProfile;
    private readonly FileOpener _openFile;

    public SshConfigFileImportSource()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OpenFile)
    {
    }

    internal SshConfigFileImportSource(string userProfile, FileOpener openFile)
    {
        _userProfile = userProfile;
        _openFile = openFile;
    }

    public string ConfigPath => Path.Combine(_userProfile, ".ssh", "config");

    // The whole load (open, bounded read, decode, resolve) runs on the thread pool, never on the
    // caller's UI thread.
    public Task<SshConfigImportResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadCoreAsync(cancellationToken), cancellationToken);

    private async Task<SshConfigImportResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_userProfile))
        {
            return SshConfigImportResult.NotFound;
        }

        byte[] bytes;
        try
        {
            await using var stream = _openFile(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.CanSeek && stream.Length > MaxBytes)
            {
                return SshConfigImportResult.Failed(SshConfigImportErrorCode.TooLarge);
            }

            // Read at most one byte past the cap so a non-seekable stream is bounded too.
            var buffer = new byte[MaxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length
                && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            if (total > MaxBytes)
            {
                return SshConfigImportResult.Failed(SshConfigImportErrorCode.TooLarge);
            }

            bytes = buffer[..total];
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return SshConfigImportResult.NotFound;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return SshConfigImportResult.Failed(SshConfigImportErrorCode.Unreadable);
        }

        string text;
        try
        {
            var span = bytes.AsSpan();
            if (span.StartsWith(Encoding.UTF8.Preamble))
            {
                span = span[Encoding.UTF8.Preamble.Length..];
            }

            text = StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return SshConfigImportResult.Failed(SshConfigImportErrorCode.InvalidEncoding);
        }

        return SshConfigResolver.Import(text, _userProfile);
    }

    private static Stream OpenFile(string path, FileMode mode, FileAccess access, FileShare share) =>
        new FileStream(path, new FileStreamOptions
        {
            Mode = mode,
            Access = access,
            Share = share,
            Options = FileOptions.Asynchronous
        });
}
