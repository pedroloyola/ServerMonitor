using ServerMonitor.Core.Backup;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>The file-system operations the key-path policy may use; tests substitute them.</summary>
internal sealed class LocalKeyFileAccess(
    Func<string, DriveType> driveType,
    Func<string, FileAttributes> getAttributes,
    Func<string, Stream> open,
    LocalSshKeyFileSystem.MetadataReader describe)
{
    public static LocalKeyFileAccess Real { get; } = new(
        root => new DriveInfo(root).DriveType,
        File.GetAttributes,
        path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan),
        LocalSshKeyFileSystem.Describe);

    public Func<string, DriveType> DriveType { get; } = driveType;

    /// <summary>Metadata only (GetFileAttributes); never opens the file.</summary>
    public Func<string, FileAttributes> GetAttributes { get; } = getAttributes;

    public Func<string, Stream> Open { get; } = open;

    public LocalSshKeyFileSystem.MetadataReader Describe { get; } = describe;
}

/// <summary>
/// The ONE rule deciding whether a private-key path may be touched at all (M14.6 V1/C-7, shared by the
/// connect path and the restore probe so they cannot drift). A path qualifies only when it is its own
/// <see cref="Path.GetFullPath(string)"/> result, does not start with <c>\\</c> or <c>//</c> (UNC,
/// <c>\\?\</c>, <c>\\.\</c>, devices), has the shape <c>X:\</c>, and sits on a Fixed or Removable drive.
/// Nothing that fails this rule is ever opened or probed, so no SMB/NTLM traffic can be triggered by a
/// configured or restored key path. Reparse points are checked from the root DOWN to the leaf, each
/// component before descending into it (Vigil N3/V12).
/// </summary>
internal static class LocalKeyPathPolicy
{
    public static bool IsLocalCandidate(string fullPath, Func<string, DriveType> driveType)
    {
        ArgumentNullException.ThrowIfNull(driveType);
        if (string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (fullPath.Length < 3 || !char.IsAsciiLetter(fullPath[0]) || fullPath[1] != ':' || fullPath[2] != '\\')
        {
            return false;
        }

        try
        {
            if (!string.Equals(fullPath, Path.GetFullPath(fullPath), StringComparison.Ordinal))
            {
                return false;
            }

            return driveType(fullPath[..3]) is System.IO.DriveType.Fixed or System.IO.DriveType.Removable;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Connect path: throws <see cref="UnauthorizedAccessException"/> for anything but a regular local
    /// file reached without a reparse point. Runs before any open.</summary>
    public static void EnsureLocalRegularFile(string fullPath, LocalKeyFileAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!IsLocalCandidate(fullPath, access.DriveType))
        {
            throw new UnauthorizedAccessException("Only regular local private-key files on a fixed or removable drive are supported.");
        }

        foreach (var component in RootToLeaf(fullPath))
        {
            if (access.GetAttributes(component).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("Reparse points are not supported for private keys.");
            }
        }
    }

    /// <summary>
    /// Restore summary probe: metadata only, never opens, never follows links. Returns
    /// <see langword="null"/> when the path is a regular local file that the connect path would accept.
    /// </summary>
    public static KeyPathStatus? Probe(string path, LocalKeyFileAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!IsLocalCandidate(path, access.DriveType))
        {
            return KeyPathStatus.NotChecked;
        }

        var components = RootToLeaf(path).ToList();
        foreach (var directory in components.Take(components.Count - 1))
        {
            try
            {
                var attributes = access.GetAttributes(directory);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory))
                {
                    return KeyPathStatus.Unsupported;
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return KeyPathStatus.Missing;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return KeyPathStatus.Unsupported;
            }
        }

        var leaf = access.Describe(path);
        return leaf.Kind switch
        {
            LocalSshKeyPathKind.Missing => KeyPathStatus.Missing,
            LocalSshKeyPathKind.RegularFile when !leaf.IsReparsePoint => null,
            _ => KeyPathStatus.Unsupported
        };
    }

    // "C:\", "C:\a", "C:\a\b", ..., fullPath.
    private static IEnumerable<string> RootToLeaf(string fullPath)
    {
        var root = fullPath[..3];
        yield return root;
        var index = 3;
        while (index < fullPath.Length)
        {
            var next = fullPath.IndexOf('\\', index);
            if (next < 0)
            {
                yield return fullPath;
                yield break;
            }

            if (next > index)
            {
                yield return fullPath[..next];
            }

            index = next + 1;
        }
    }
}
