using System.Text;
using ServerMonitor.Core.SshConfig;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.Infrastructure.Tests.SshConfig;

public sealed class SshConfigFileImportSourceTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "sm-sshcfg-" + Guid.NewGuid().ToString("N"));

    public SshConfigFileImportSourceTests() => Directory.CreateDirectory(_profile);

    private string ConfigPath => Path.Combine(_profile, ".ssh", "config");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_profile, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteConfig(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllBytes(ConfigPath, bytes);
    }

    private sealed class SpyOpener
    {
        private readonly Func<string, Stream> _open;

        public SpyOpener(Func<string, Stream> open) => _open = open;

        public List<(string Path, FileMode Mode, FileAccess Access, FileShare Share)> Calls { get; } = [];

        public Stream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            Calls.Add((path, mode, access, share));
            return _open(path);
        }
    }

    [Fact]
    public async Task MissingConfig_IsAnEmptyNonErrorState()
    {
        var result = await new SshConfigFileImportSource(_profile, RealOpen).LoadAsync();

        Assert.Equal(SshConfigImportStatus.NotFound, result.Status);
        Assert.Equal(SshConfigImportErrorCode.None, result.ErrorCode);
        Assert.Empty(result.Hosts);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(ConfigPath)));
    }

    [Fact]
    public async Task EmptyConfig_LoadsWithNoHosts()
    {
        WriteConfig([]);

        var result = await new SshConfigFileImportSource(_profile, RealOpen).LoadAsync();

        Assert.Equal(SshConfigImportStatus.Loaded, result.Status);
        Assert.Empty(result.Hosts);
    }

    [Fact]
    public async Task Utf8WithBom_ExpandsTildeAgainstTheUserProfile()
    {
        WriteConfig([.. Encoding.UTF8.Preamble, .. "Host web\n  User josé\n  IdentityFile ~/.ssh/id\n"u8]);

        var host = Assert.Single((await new SshConfigFileImportSource(_profile, RealOpen).LoadAsync()).Hosts);

        Assert.Equal("web", host.Alias);
        Assert.Equal("josé", host.User);
        Assert.Equal(Path.Combine(_profile, ".ssh", "id"), host.IdentityFile);
    }

    [Fact]
    public async Task OversizeConfig_IsClassifiedTooLarge()
    {
        WriteConfig(Encoding.UTF8.GetBytes(new string('#', SshConfigFileImportSource.MaxBytes + 1)));

        var result = await new SshConfigFileImportSource(_profile, RealOpen).LoadAsync();

        Assert.Equal(SshConfigImportStatus.Error, result.Status);
        Assert.Equal(SshConfigImportErrorCode.TooLarge, result.ErrorCode);
    }

    [Fact]
    public async Task OversizeNonSeekableStream_IsStillBoundedAndClassifiedTooLarge()
    {
        var opener = new SpyOpener(_ => new NonSeekableStream(new byte[SshConfigFileImportSource.MaxBytes + 10]));

        var result = await new SshConfigFileImportSource(_profile, opener.Open, TrustInMemoryStream).LoadAsync();

        Assert.Equal(SshConfigImportErrorCode.TooLarge, result.ErrorCode);
    }

    [Fact]
    public async Task StreamWhoseIdentityCannotBeVerified_IsNeverTrusted()
    {
        // The default verifier needs a real file handle; anything else fails closed before a byte is read.
        var opener = new SpyOpener(_ => new MemoryStream("Host a\n"u8.ToArray(), writable: false));

        var result = await new SshConfigFileImportSource(_profile, opener.Open).LoadAsync();

        Assert.Equal(SshConfigImportErrorCode.Unreadable, result.ErrorCode);
        Assert.Empty(result.Hosts);
    }

    // For tests that exercise bounds or threading with in-memory streams, which have no file handle.
    private static SshConfigOpenedFileIdentity TrustInMemoryStream(Stream stream, string expectedPath) =>
        SshConfigOpenedFileIdentity.Verified;

    [Fact]
    public async Task InvalidUtf8_IsClassifiedInvalidEncoding()
    {
        WriteConfig([0x48, 0x6F, 0x73, 0x74, 0x20, 0xC3, 0x28]);

        var result = await new SshConfigFileImportSource(_profile, RealOpen).LoadAsync();

        Assert.Equal(SshConfigImportErrorCode.InvalidEncoding, result.ErrorCode);
    }

    [Fact]
    public async Task UnreadableConfig_IsClassifiedNotThrown()
    {
        var opener = new SpyOpener(_ => throw new UnauthorizedAccessException());

        var result = await new SshConfigFileImportSource(_profile, opener.Open).LoadAsync();

        Assert.Equal(SshConfigImportStatus.Error, result.Status);
        Assert.Equal(SshConfigImportErrorCode.Unreadable, result.ErrorCode);
    }

    [Fact]
    public async Task ConfigLockedExclusivelyByAnotherProcess_IsClassifiedUnreadable()
    {
        WriteConfig("Host a\n"u8.ToArray());
        using var exclusive = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await new SshConfigFileImportSource(_profile, RealOpen).LoadAsync();

        Assert.Equal(SshConfigImportErrorCode.Unreadable, result.ErrorCode);
    }

    [Fact]
    public async Task Reader_OpensOnlyTheConfigAndOnlyForReadSharingReadWrite()
    {
        var opener = new SpyOpener(_ => new MemoryStream("Host a\n  IdentityFile ~/.ssh/id_a\n"u8.ToArray(), writable: false));

        await new SshConfigFileImportSource(_profile, opener.Open).LoadAsync();

        var call = Assert.Single(opener.Calls);
        Assert.Equal(ConfigPath, call.Path);
        Assert.Equal(FileMode.Open, call.Mode);
        Assert.Equal(FileAccess.Read, call.Access);
        Assert.Equal(FileShare.ReadWrite, call.Share);
    }

    [Fact]
    public async Task Import_LeavesTheRealConfigBytesAndTimestampUnchanged()
    {
        var bytes = "Host a\n  HostName a.lan\n  User u\n  Port 2022\n"u8.ToArray();
        WriteConfig(bytes);
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ConfigPath, stamp);
        using (var holder = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            // An editor holding the file open for writing must not block the read-only import.
            var result = await new SshConfigFileImportSource(_profile, RealOpen).LoadAsync();
            Assert.Equal(2022, Assert.Single(result.Hosts).Port);
        }

        Assert.Equal(bytes, File.ReadAllBytes(ConfigPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(ConfigPath));
        Assert.Equal(["config"], Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Import_NeverOpensTheIdentityFile()
    {
        var keyPath = Path.Combine(_profile, ".ssh", "id_locked");
        WriteConfig(Encoding.UTF8.GetBytes("Host a\n  IdentityFile ~/.ssh/id_locked\n"));
        await File.WriteAllTextAsync(keyPath, "PRIVATE");
        var opener = new SpyOpener(path => path == ConfigPath
            ? File.OpenRead(path)
            : throw new InvalidOperationException("private key opened: " + path));

        // Hold the key exclusively as well: any attempt to open it for real would fail.
        using var exclusiveKey = new FileStream(keyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await new SshConfigFileImportSource(_profile, opener.Open).LoadAsync();

        Assert.Equal(keyPath, Assert.Single(result.Hosts).IdentityFile);
        Assert.Equal([ConfigPath], opener.Calls.Select(call => call.Path));
    }

    [Fact]
    public async Task Load_RunsOffTheCallersSynchronizationContext()
    {
        var callerContext = new SynchronizationContext();
        SynchronizationContext? seenByOpen = callerContext;
        var opener = new SpyOpener(_ =>
        {
            seenByOpen = SynchronizationContext.Current;
            return new MemoryStream("Host a\n"u8.ToArray(), writable: false);
        });
        var source = new SshConfigFileImportSource(_profile, opener.Open, TrustInMemoryStream);

        var previous = SynchronizationContext.Current;
        Task<SshConfigImportResult> load;
        SynchronizationContext.SetSynchronizationContext(callerContext);
        try
        {
            load = source.LoadAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Single((await load).Hosts);
        Assert.Null(seenByOpen);
    }

    [Fact]
    public async Task MalformedHostLine_IsAClassifiedErrorNotAnException()
    {
        WriteConfig("Host \"broken\n  User u\n"u8.ToArray());

        var result = await new SshConfigFileImportSource(_profile, RealOpen).LoadAsync();

        Assert.Equal(SshConfigImportErrorCode.InvalidSyntax, result.ErrorCode);
    }

    private static Stream RealOpen(string path, FileMode mode, FileAccess access, FileShare share) =>
        new FileStream(path, mode, access, share);

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();
    }
}
