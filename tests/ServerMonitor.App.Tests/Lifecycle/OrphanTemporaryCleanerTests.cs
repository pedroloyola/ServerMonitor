using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// Vigil C10. A watchdog termination does not run <c>finally</c> blocks, so the host-key store's
/// <c>known-hosts.json.tmp</c> is the one file it can orphan. Startup removes exactly that path and
/// nothing else: no directory sweep, no wildcard, and no file this app did not write.
/// </summary>
public sealed class OrphanTemporaryCleanerTests : IDisposable
{
    private readonly string _directory;
    private readonly string _knownHostsPath;
    private readonly OrphanTemporaryCleaner _cleaner =
        new(NullLogger<OrphanTemporaryCleaner>.Instance);

    public OrphanTemporaryCleanerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sm-orphan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _knownHostsPath = Path.Combine(_directory, "known-hosts.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void The_orphaned_temporary_is_removed()
    {
        var temporary = _knownHostsPath + ".tmp";
        File.WriteAllText(temporary, "{}");

        _cleaner.CleanKnownHostTemporary(_knownHostsPath);

        Assert.False(File.Exists(temporary));
    }

    /// <summary>
    /// The committed file is the product's trust store. Deleting it would silently drop every accepted
    /// host key, so the cleanup must not touch it under any circumstances.
    /// </summary>
    [Fact]
    public void The_committed_known_hosts_file_is_never_touched()
    {
        File.WriteAllText(_knownHostsPath, "[{\"host\":\"example\"}]");
        File.WriteAllText(_knownHostsPath + ".tmp", "{}");

        _cleaner.CleanKnownHostTemporary(_knownHostsPath);

        Assert.True(File.Exists(_knownHostsPath));
        Assert.Equal("[{\"host\":\"example\"}]", File.ReadAllText(_knownHostsPath));
    }

    /// <summary>
    /// Everything else in the folder survives — including files that merely LOOK like the target. The
    /// cleanup is one absolute path, not a pattern.
    /// </summary>
    [Theory]
    [InlineData("known-hosts.json.tmp.tmp")]
    [InlineData("known-hosts.json.bak")]
    [InlineData("known-hosts.tmp")]
    [InlineData("other.json.tmp")]
    [InlineData("servers.json")]
    [InlineData("widget-state.json")]
    [InlineData("widget-state.abc123.tmp")]
    [InlineData("history.db")]
    public void No_other_file_is_ever_removed(string fileName)
    {
        var bystander = Path.Combine(_directory, fileName);
        File.WriteAllText(bystander, "keep me");
        File.WriteAllText(_knownHostsPath + ".tmp", "{}");

        _cleaner.CleanKnownHostTemporary(_knownHostsPath);

        Assert.True(File.Exists(bystander), $"{fileName} must not be touched");
        Assert.False(File.Exists(_knownHostsPath + ".tmp"));
    }

    [Fact]
    public void A_missing_temporary_is_not_an_error()
    {
        var thrown = Record.Exception(() => _cleaner.CleanKnownHostTemporary(_knownHostsPath));

        Assert.Null(thrown);
    }

    [Fact]
    public void An_unusable_path_is_ignored()
    {
        Assert.Null(Record.Exception(() => _cleaner.CleanKnownHostTemporary(string.Empty)));
        Assert.Null(Record.Exception(() => _cleaner.CleanKnownHostTemporary("   ")));
    }

    /// <summary>
    /// A temporary that is still locked (a concurrent write) must not fail startup — a leftover is
    /// harmless, and the next successful write replaces it.
    /// </summary>
    [Fact]
    public void A_locked_temporary_does_not_fail_startup()
    {
        var temporary = _knownHostsPath + ".tmp";
        using var handle = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);

        var thrown = Record.Exception(() => _cleaner.CleanKnownHostTemporary(_knownHostsPath));

        Assert.Null(thrown);
    }
}
