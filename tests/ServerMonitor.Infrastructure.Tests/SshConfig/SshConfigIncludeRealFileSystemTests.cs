using System.Diagnostics;
using ServerMonitor.Core.SshConfig;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.Infrastructure.Tests.SshConfig;

/// <summary>
/// M14.4a safe Include against the real Windows file system in a temp profile: real symlinks
/// (file and directory), real junctions (<c>mklink /J</c>), a real directory match, read-only proof.
/// </summary>
public sealed class SshConfigIncludeRealFileSystemTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "sm-sshinc-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = [];

    public SshConfigIncludeRealFileSystemTests() => Directory.CreateDirectory(Path.Combine(_profile, ".ssh"));

    private string Ssh(string relative) => Path.Combine(_profile, ".ssh", relative);

    private string InProfile(string relative) => Path.Combine(_profile, relative);

    public void Dispose()
    {
        // Remove links first so a recursive delete can never walk into a link target.
        foreach (var link in Enumerable.Reverse(_links))
        {
            try
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
                else
                {
                    File.Delete(link);
                }
            }
            catch (IOException)
            {
            }
        }

        try
        {
            Directory.Delete(_profile, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private sealed class SpyOpener
    {
        public List<(string Path, FileMode Mode, FileAccess Access, FileShare Share)> Calls { get; } = [];

        public Stream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            lock (Calls)
            {
                Calls.Add((path, mode, access, share));
            }

            return new FileStream(path, mode, access, share);
        }
    }

    private async Task<(SshConfigImportResult Result, SpyOpener Spy)> LoadAsync()
    {
        var spy = new SpyOpener();
        var result = await new SshConfigFileImportSource(_profile, spy.Open).LoadAsync();
        return (result, spy);
    }

    private void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        Assert.True(process.WaitForExit(15_000), "mklink /J did not finish");
        Assert.Equal(0, process.ExitCode);
        _links.Add(link);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private void FileSymlink(string link, string target)
    {
        File.CreateSymbolicLink(link, target);
        _links.Add(link);
        Assert.True(new FileInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private void DirectorySymlink(string link, string target)
    {
        Directory.CreateSymbolicLink(link, target);
        _links.Add(link);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private Dictionary<string, (byte[] Bytes, DateTime WriteTime)> Snapshot() =>
        Directory.EnumerateFileSystemEntries(_profile, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            })
            .ToDictionary(
                path => path,
                path => File.Exists(path)
                    ? (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path))
                    : (Array.Empty<byte>(), Directory.GetLastWriteTimeUtc(path)));

    [Fact]
    public async Task RealIncludes_AreFollowedReadOnly_OnlyInsideSsh_AndNothingChanges()
    {
        Write(Ssh("config"), "Include conf.d/*.conf\nHost a\n  Include ../outside.conf\n");
        Write(Ssh(@"conf.d\10.conf"), "Host a\n  User real\n");
        Write(Ssh(@"conf.d\20.conf"), "Host a\n  Port 2201\n");
        Write(InProfile("outside.conf"), "ProxyJump none\n");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        foreach (var file in Directory.EnumerateFiles(_profile, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, stamp);
        }

        var before = Snapshot();
        SshConfigImportResult result;
        SpyOpener spy;
        using (new FileStream(Ssh("config"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            (result, spy) = await LoadAsync();
        }

        var host = Assert.Single(result.Hosts);
        Assert.Equal("real", host.User);
        Assert.Equal(2201, host.Port);
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);

        Assert.Equal(3, spy.Calls.Count);
        Assert.All(spy.Calls, call =>
        {
            Assert.StartsWith(Ssh(string.Empty), call.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(FileMode.Open, call.Mode);
            Assert.Equal(FileAccess.Read, call.Access);
            Assert.Equal(FileShare.ReadWrite, call.Share);
        });

        var after = Snapshot();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.All(before, entry =>
        {
            Assert.Equal(entry.Value.Bytes, after[entry.Key].Bytes);
            Assert.Equal(entry.Value.WriteTime, after[entry.Key].WriteTime);
        });
    }

    [Fact]
    public async Task RealFileSymlink_CannotBeVerified_AndNeitherLinkNorTargetIsOpened()
    {
        Write(InProfile("target.conf"), "ProxyJump none\n");
        Write(Ssh("config"), "Host a\n  Include link.conf\n");
        FileSymlink(Ssh("link.conf"), InProfile("target.conf"));

        var (result, spy) = await LoadAsync();

        var host = Assert.Single(result.Hosts);
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.Contains(host.Findings, f => f.Reason == SshConfigFindingReason.IncludeReparsePoint);
        Assert.Equal([Ssh("config")], spy.Calls.Select(call => call.Path));
    }

    [Fact]
    public async Task RealFileSymlinkMatchedByAGlob_IsSkippedAndBlocks()
    {
        Write(InProfile("target.conf"), "ProxyJump none\n");
        Write(Ssh(@"conf.d\a.conf"), "User a\n");
        Write(Ssh("config"), "Host a\n  Include conf.d/*\n");
        FileSymlink(Ssh(@"conf.d\b.conf"), InProfile("target.conf"));

        var (result, spy) = await LoadAsync();

        var host = Assert.Single(result.Hosts);
        Assert.Equal("a", host.User);
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.Equal([Ssh("config"), Ssh(@"conf.d\a.conf")], spy.Calls.Select(call => call.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealLinkedDirectory_CannotBeVerified_AndIsNeverEntered(bool junction)
    {
        Write(InProfile(@"realdir\a.conf"), "ProxyJump none\n");
        Write(Ssh("config"), "Host a\n  Include conf.d/* conf.d/a.conf\n");
        if (junction)
        {
            Junction(Ssh("conf.d"), InProfile("realdir"));
        }
        else
        {
            DirectorySymlink(Ssh("conf.d"), InProfile("realdir"));
        }

        var (result, spy) = await LoadAsync();

        var host = Assert.Single(result.Hosts);
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.Contains(host.Findings, f => f.Reason == SshConfigFindingReason.IncludeReparsePoint);
        Assert.Equal([Ssh("config")], spy.Calls.Select(call => call.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealLinkedSshDirectory_FailsAsConfigIsLink_AndNothingIsOpened(bool junction)
    {
        Directory.Delete(Ssh(string.Empty));
        Write(InProfile(@"realssh\config"), "Host a\n  User u\n");
        if (junction)
        {
            Junction(Path.Combine(_profile, ".ssh"), InProfile("realssh"));
        }
        else
        {
            DirectorySymlink(Path.Combine(_profile, ".ssh"), InProfile("realssh"));
        }

        var (result, spy) = await LoadAsync();

        Assert.Equal(SshConfigImportStatus.Error, result.Status);
        Assert.Equal(SshConfigImportErrorCode.ConfigIsLink, result.ErrorCode);
        Assert.Empty(result.Hosts);
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task RealSymlinkedConfigFile_FailsAsConfigIsLink_AndNothingIsOpened()
    {
        Write(InProfile("real-config"), "Host a\n  User u\n");
        FileSymlink(Ssh("config"), InProfile("real-config"));

        var (result, spy) = await LoadAsync();

        Assert.Equal(SshConfigImportErrorCode.ConfigIsLink, result.ErrorCode);
        Assert.Empty(result.Hosts);
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task RealDirectoryMatch_IsNotARegularFile()
    {
        Directory.CreateDirectory(Ssh(@"conf.d\sub"));
        Write(Ssh("config"), "Host a\n  Include conf.d/*\n");

        var (result, spy) = await LoadAsync();

        var host = Assert.Single(result.Hosts);
        Assert.Contains(host.Findings, f => f.Reason == SshConfigFindingReason.IncludeNotRegularFile);
        Assert.False(host.IsImportable);
        Assert.Equal([Ssh("config")], spy.Calls.Select(call => call.Path));
    }

    [Fact]
    public async Task RealCycle_FailsWithTheFileNamed()
    {
        Write(Ssh("config"), "Include a\nHost h\n");
        Write(Ssh("a"), "Include b\n");
        Write(Ssh("b"), "Include a\n");

        var (result, _) = await LoadAsync();

        Assert.Equal(SshConfigImportErrorCode.IncludeCycle, result.ErrorCode);
        Assert.Equal(Ssh("a"), result.ErrorDetail);
    }
}
