namespace ServerMonitor.Core.SshConfig;

/// <summary>What a path is, read from its own attributes without following links.</summary>
public enum SshConfigPathKind
{
    Missing,
    RegularFile,
    Directory,

    /// <summary>A device or anything else that is neither a regular file nor a directory.</summary>
    Other
}

/// <param name="IsReparsePoint">Symbolic link, junction or any other reparse point.</param>
public readonly record struct SshConfigPathInfo(SshConfigPathKind Kind, bool IsReparsePoint);

public enum SshConfigReadStatus
{
    Ok,
    NotFound,
    TooLarge,
    Unreadable,

    /// <summary>After the open, the handle's final path is not the path that was checked (a link swapped in, a network path).</summary>
    FinalPathMismatch,

    /// <summary>The opened file has more than one hard link.</summary>
    HardLinked,

    /// <summary>The opened file's identity could not be established; never trusted.</summary>
    IdentityUnverifiable
}

public sealed record SshConfigFileRead(SshConfigReadStatus Status, byte[] Bytes)
{
    public static SshConfigFileRead Failed(SshConfigReadStatus status) => new(status, []);
}

/// <summary>
/// The only file-system operations the SSH config import needs, so the <c>Include</c> semantics
/// in Core stay pure and testable in memory. Implementations are strictly read-only: they never
/// create, write or follow anything, and <see cref="ReadFile"/> opens with
/// <c>FileMode.Open</c> + <c>FileAccess.Read</c> + <c>FileShare.ReadWrite</c>.
/// </summary>
public interface ISshConfigFileSystem
{
    /// <summary>Attributes of the path itself (a link is reported as a reparse point, not followed).</summary>
    SshConfigPathInfo GetInfo(string fullPath);

    /// <summary>
    /// Entry names (not paths) directly inside one directory, no recursion; <see langword="null"/>
    /// when the directory cannot be listed.
    /// </summary>
    IReadOnlyList<string>? EnumerateNames(string fullDirectoryPath);

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/>; a longer file is <see cref="SshConfigReadStatus.TooLarge"/>.
    /// Before any byte is returned, the opened handle must be proven to be <paramref name="fullPath"/>
    /// itself (final path) and not hard-linked, otherwise one of the identity statuses is returned.
    /// </summary>
    SshConfigFileRead ReadFile(string fullPath, int maxBytes);
}
