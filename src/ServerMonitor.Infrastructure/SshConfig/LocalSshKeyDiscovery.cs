using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Infrastructure.SshConfig;

/// <summary>
/// M14.5 — finds the OpenSSH default private keys directly inside <c>%USERPROFILE%\.ssh</c> from file-system
/// METADATA only: the three exact names <c>id_ed25519</c>, <c>id_ecdsa</c>, <c>id_rsa</c> are looked up by path
/// and described through <see cref="LocalSshKeyFileSystem"/>, which has no way to open a file. Nothing is
/// enumerated, read, copied, parsed, created or modified. <c>id_dsa</c> (insecure), <c>*_sk</c> (FIDO,
/// unsupported) and every other name are never candidates; the user picks those with "Browse…".
/// A linked <c>.ssh</c> (symlink/junction) yields no keys, as the SSH config import fails closed on it.
/// </summary>
public sealed class LocalSshKeyDiscovery : ILocalSshKeyDiscovery
{
    /// <summary>A default private key is a few KiB; anything larger is not offered.</summary>
    public const long MaxKeyFileBytes = 64 * 1024;

    // Preference order: the first one found is the recommended key.
    private static readonly (string FileName, LocalSshKeyKind Kind)[] Candidates =
    [
        ("id_ed25519", LocalSshKeyKind.Ed25519),
        ("id_ecdsa", LocalSshKeyKind.Ecdsa),
        ("id_rsa", LocalSshKeyKind.Rsa)
    ];

    private readonly string _userProfile;
    private readonly LocalSshKeyFileSystem.MetadataReader _describe;

    public LocalSshKeyDiscovery()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <summary>Looks in <c>&lt;userProfile&gt;\.ssh</c>; the Debug QA harness points this at a fixture profile.</summary>
    public LocalSshKeyDiscovery(string userProfile)
        : this(userProfile, LocalSshKeyFileSystem.Describe)
    {
    }

    internal LocalSshKeyDiscovery(string userProfile, LocalSshKeyFileSystem.MetadataReader describe)
    {
        _userProfile = userProfile ?? string.Empty;
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
    }

    public string SshDirectory => Path.Combine(_userProfile, ".ssh");

    // Metadata lookups can block (network profile, cloud placeholders): always off the caller's UI thread.
    public Task<IReadOnlyList<LocalSshKey>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Discover(cancellationToken), cancellationToken);

    private IReadOnlyList<LocalSshKey> Discover(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_userProfile))
        {
            return [];
        }

        var directory = SafeDescribe(SshDirectory);
        if (directory.Kind != LocalSshKeyPathKind.Directory || directory.IsReparsePoint)
        {
            return [];
        }

        var found = new List<LocalSshKey>(Candidates.Length);
        foreach (var (fileName, kind) in Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = Path.Combine(SshDirectory, fileName);
            if (IsOfferable(SafeDescribe(path)))
            {
                found.Add(new LocalSshKey(path, fileName, kind, IsRecommended: found.Count == 0));
            }
        }

        return found;
    }

    private static bool IsOfferable(LocalSshKeyFileMetadata metadata) =>
        metadata.Kind == LocalSshKeyPathKind.RegularFile
        && !metadata.IsReparsePoint
        && metadata.Length is > 0 and <= MaxKeyFileBytes;

    private LocalSshKeyFileMetadata SafeDescribe(string path)
    {
        try
        {
            return _describe(path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never throws for I/O: an unexpected failure is "not inspectable", so never offered.
            return LocalSshKeyFileMetadata.Uninspectable;
        }
    }
}

internal enum LocalSshKeyPathKind
{
    Missing,
    RegularFile,
    Directory,

    /// <summary>A device, or present but not inspectable.</summary>
    Other
}

/// <param name="IsReparsePoint">Symbolic link, junction or any other reparse point (the link itself, never followed).</param>
/// <param name="Length">Size in bytes from the directory entry; 0 when not a regular file.</param>
internal readonly record struct LocalSshKeyFileMetadata(LocalSshKeyPathKind Kind, bool IsReparsePoint, long Length)
{
    public static LocalSshKeyFileMetadata Missing { get; } = new(LocalSshKeyPathKind.Missing, false, 0);

    public static LocalSshKeyFileMetadata Uninspectable { get; } = new(LocalSshKeyPathKind.Other, false, 0);
}

/// <summary>
/// The only file-system access key discovery has: path METADATA (GetFileAttributesEx / FindFirstFile semantics),
/// describing a link itself rather than its target. Deliberately exposes no open, read or write.
/// </summary>
internal static class LocalSshKeyFileSystem
{
    internal delegate LocalSshKeyFileMetadata MetadataReader(string fullPath);

    public static LocalSshKeyFileMetadata Describe(string fullPath)
    {
        try
        {
            // FileSystemInfo.Refresh reads attributes and size from the directory entry; the file is not opened.
            var info = new FileInfo(fullPath);
            info.Refresh();
            var attributes = info.Attributes;
            if ((int)attributes == -1)
            {
                return LocalSshKeyFileMetadata.Missing;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                return new LocalSshKeyFileMetadata(
                    LocalSshKeyPathKind.Directory,
                    attributes.HasFlag(FileAttributes.ReparsePoint),
                    0);
            }

            if (attributes.HasFlag(FileAttributes.Device))
            {
                return new LocalSshKeyFileMetadata(LocalSshKeyPathKind.Other, attributes.HasFlag(FileAttributes.ReparsePoint), 0);
            }

            return new LocalSshKeyFileMetadata(
                LocalSshKeyPathKind.RegularFile,
                attributes.HasFlag(FileAttributes.ReparsePoint),
                info.Length);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return LocalSshKeyFileMetadata.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Present but not inspectable: never treated as a regular file.
            return LocalSshKeyFileMetadata.Uninspectable;
        }
    }
}
