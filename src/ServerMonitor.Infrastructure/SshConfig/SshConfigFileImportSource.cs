using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.Infrastructure.SshConfig;

/// <summary>
/// Reads <c>~/.ssh/config</c> and the files it <c>Include</c>s strictly read-only
/// (<see cref="FileAccess.Read"/>, sharing read/write so an editor holding a file is not
/// disturbed), each bounded to <see cref="MaxBytes"/>, as strict UTF-8, and hands them to the pure
/// Core expander/resolver. Includes are only followed inside <c>~/.ssh</c> and never through a
/// symlink or junction. It never creates or writes anything and never opens an IdentityFile.
/// </summary>
public sealed class SshConfigFileImportSource : ISshConfigImportSource
{
    public const int MaxBytes = SshConfigIncludeExpander.MaxFileBytes;

    private readonly string _userProfile;
    private readonly SshConfigFileSystem _fileSystem;

    public SshConfigFileImportSource()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <summary>Reads <c>&lt;userProfile&gt;\.ssh\config</c>; the Debug QA harness points this at a fixture profile.</summary>
    public SshConfigFileImportSource(string userProfile)
        : this(userProfile, OpenFile)
    {
    }

    internal SshConfigFileImportSource(
        string userProfile,
        SshConfigFileSystem.FileOpener openFile,
        SshConfigFileSystem.OpenedFileCheck? verifyOpened = null)
    {
        _userProfile = userProfile;
        _fileSystem = new SshConfigFileSystem(openFile, verifyOpened);
    }

    public string ConfigPath => Path.Combine(_userProfile, ".ssh", "config");

    // The whole load (open, bounded read, decode, include expansion, resolve) runs on the thread
    // pool, never on the caller's UI thread.
    public Task<SshConfigImportResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => SshConfigResolver.Import(_fileSystem, _userProfile, cancellationToken), cancellationToken);

    private static Stream OpenFile(string path, FileMode mode, FileAccess access, FileShare share) =>
        new FileStream(path, mode, access, share);
}
