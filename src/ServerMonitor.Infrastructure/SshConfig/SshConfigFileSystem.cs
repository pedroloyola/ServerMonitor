using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.Infrastructure.SshConfig;

/// <summary>
/// The real, strictly read-only file system behind the SSH config import. Attributes are read from
/// the path itself (a symlink or junction reports <see cref="FileAttributes.ReparsePoint"/> and is not
/// followed), directories are listed one level only, and every open goes through the
/// <see cref="FileOpener"/> seam with <c>FileMode.Open</c> + <c>FileAccess.Read</c> +
/// <c>FileShare.ReadWrite</c>. Nothing is ever created or written. Every opened handle is checked with
/// <see cref="SshConfigOpenedFileVerifier"/> (final path + single link) before a byte is read.
/// </summary>
internal sealed class SshConfigFileSystem(
    SshConfigFileSystem.FileOpener openFile,
    SshConfigFileSystem.OpenedFileCheck? verifyOpened = null) : ISshConfigFileSystem
{
    internal delegate Stream FileOpener(string path, FileMode mode, FileAccess access, FileShare share);

    internal delegate SshConfigOpenedFileIdentity OpenedFileCheck(Stream stream, string expectedPath);

    private readonly OpenedFileCheck _verifyOpened = verifyOpened ?? SshConfigOpenedFileVerifier.Verify;

    public SshConfigPathInfo GetInfo(string fullPath)
    {
        FileAttributes attributes;
        try
        {
            // GetFileAttributesEx semantics: describes the link itself, never its target.
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SshConfigPathInfo(SshConfigPathKind.Missing, IsReparsePoint: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Present but not inspectable: never treated as a regular file.
            return new SshConfigPathInfo(SshConfigPathKind.Other, IsReparsePoint: false);
        }

        var kind = attributes.HasFlag(FileAttributes.Directory) ? SshConfigPathKind.Directory
            : attributes.HasFlag(FileAttributes.Device) ? SshConfigPathKind.Other
            : SshConfigPathKind.RegularFile;
        return new SshConfigPathInfo(kind, attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    public IReadOnlyList<string>? EnumerateNames(string fullDirectoryPath)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                MatchType = MatchType.Simple
            };
            return new DirectoryInfo(fullDirectoryPath)
                .EnumerateFileSystemInfos("*", options)
                .Select(entry => entry.Name)
                .Take(SshConfigIncludeExpander.MaxDirectoryEntries + 1)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public SshConfigFileRead ReadFile(string fullPath, int maxBytes)
    {
        try
        {
            using var stream = openFile(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // The checks before the open were on the path; this proves the handle is that plain file.
            switch (_verifyOpened(stream, fullPath))
            {
                case SshConfigOpenedFileIdentity.FinalPathMismatch:
                    return SshConfigFileRead.Failed(SshConfigReadStatus.FinalPathMismatch);
                case SshConfigOpenedFileIdentity.HardLinked:
                    return SshConfigFileRead.Failed(SshConfigReadStatus.HardLinked);
                case SshConfigOpenedFileIdentity.Unverifiable:
                    return SshConfigFileRead.Failed(SshConfigReadStatus.IdentityUnverifiable);
            }

            if (stream.CanSeek && stream.Length > maxBytes)
            {
                return SshConfigFileRead.Failed(SshConfigReadStatus.TooLarge);
            }

            // Read at most one byte past the cap so a non-seekable stream is bounded too.
            var buffer = new byte[maxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            return total > maxBytes
                ? SshConfigFileRead.Failed(SshConfigReadStatus.TooLarge)
                : new SshConfigFileRead(SshConfigReadStatus.Ok, buffer[..total]);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return SshConfigFileRead.Failed(SshConfigReadStatus.NotFound);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return SshConfigFileRead.Failed(SshConfigReadStatus.Unreadable);
        }
    }
}
