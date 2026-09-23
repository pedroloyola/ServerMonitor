using System.Diagnostics;
using System.Runtime.InteropServices;
using ServerMonitor.Core.SshConfig;
using ServerMonitor.Infrastructure.SshConfig;
using Xunit.Abstractions;

namespace ServerMonitor.Infrastructure.Tests.SshConfig;

/// <summary>
/// Vigil L1/L2 on the real file system: the post-open identity check (final path of the handle, and
/// the hard-link count) with real files, real 8.3 / case-variant paths and real hard links.
/// </summary>
public sealed class SshConfigOpenedFileIdentityTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "sm-sshid-" + Guid.NewGuid().ToString("N"));

    private string Ssh(string relative) => Path.Combine(_profile, ".ssh", relative);

    private string InProfile(string relative) => Path.Combine(_profile, relative);

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

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void HardLink(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        Assert.True(process.WaitForExit(15_000), "mklink /H did not finish");
        Assert.Equal(0, process.ExitCode);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);

    private static string ShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        Assert.InRange(length, 1u, (uint)buffer.Length - 1);
        return new string(buffer, 0, (int)length);
    }

    // ---- the verifier itself

    [Fact]
    public void Verifier_SameFile_IsVerified()
    {
        Write(InProfile("a.conf"), "x");
        using var stream = new FileStream(InProfile("a.conf"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal(SshConfigOpenedFileIdentity.Verified, SshConfigOpenedFileVerifier.Verify(stream, InProfile("a.conf")));
    }

    [Fact]
    public void Verifier_HandleToADifferentFileThanTheCheckedPath_IsAMismatch()
    {
        // Stands in for "a link was swapped in between the check and the open": the handle is a
        // real, plain file, but not the one whose path was checked.
        Write(InProfile("checked.conf"), "x");
        Write(InProfile("opened.conf"), "y");
        using var stream = new FileStream(InProfile("opened.conf"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal(
            SshConfigOpenedFileIdentity.FinalPathMismatch,
            SshConfigOpenedFileVerifier.Verify(stream, InProfile("checked.conf")));
    }

    [Fact]
    public void Verifier_ANonFileStream_IsUnverifiable()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        Assert.Equal(SshConfigOpenedFileIdentity.Unverifiable, SshConfigOpenedFileVerifier.Verify(stream, InProfile("a.conf")));
    }

    [Fact]
    public void Verifier_RealHardLink_IsHardLinked()
    {
        Write(InProfile("target.conf"), "x");
        HardLink(InProfile("link.conf"), InProfile("target.conf"));
        using var stream = new FileStream(InProfile("link.conf"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal(SshConfigOpenedFileIdentity.HardLinked, SshConfigOpenedFileVerifier.Verify(stream, InProfile("link.conf")));
    }

    // ---- end to end through the import source

    [Fact]
    public async Task CaseVariantProfilePath_ComparesCorrectly_AndImports()
    {
        Write(Ssh("config"), "Include conf.d/*\nHost a\n");
        Write(Ssh(@"conf.d\one"), "Host a\n  User one\n");
        var variant = _profile.ToUpperInvariant();
        Assert.NotEqual(_profile, variant);

        var result = await new SshConfigFileImportSource(variant).LoadAsync();

        Assert.Equal(SshConfigImportStatus.Loaded, result.Status);
        Assert.Equal("one", Assert.Single(result.Hosts).User);
    }

    [Fact]
    public async Task ShortNameProfilePath_ComparesCorrectly_AndImports()
    {
        Write(Ssh("config"), "Include conf.d/*\nHost a\n");
        Write(Ssh(@"conf.d\one"), "Host a\n  User one\n");
        var shortProfile = ShortPath(_profile);
        output.WriteLine(string.Equals(shortProfile, _profile, StringComparison.OrdinalIgnoreCase)
            ? $"8.3 names are not generated on this volume; path unchanged: {shortProfile}"
            : $"8.3 path used: {shortProfile}");

        var result = await new SshConfigFileImportSource(shortProfile).LoadAsync();

        Assert.Equal(SshConfigImportStatus.Loaded, result.Status);
        Assert.Equal("one", Assert.Single(result.Hosts).User);
    }

    [Fact]
    public async Task RealHardLinkedInclude_IsNotFollowed()
    {
        Write(InProfile("outside.conf"), "ProxyJump none\nUser from-outside\n");
        Write(Ssh("config"), "Host a\n  Include x\n");
        HardLink(Ssh("x"), InProfile("outside.conf"));

        var result = await new SshConfigFileImportSource(_profile).LoadAsync();

        var host = Assert.Single(result.Hosts);
        Assert.Null(host.User);
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.Contains(host.Findings, f => f.Reason == SshConfigFindingReason.IncludeHardLinked);
    }

    [Fact]
    public async Task RealHardLinkedRootConfig_FailsAsConfigIsLink()
    {
        Write(InProfile("dotfiles-config"), "Host a\n  User u\n");
        Directory.CreateDirectory(Ssh(string.Empty));
        HardLink(Ssh("config"), InProfile("dotfiles-config"));

        var result = await new SshConfigFileImportSource(_profile).LoadAsync();

        Assert.Equal(SshConfigImportErrorCode.ConfigIsLink, result.ErrorCode);
        Assert.Empty(result.Hosts);
    }
}
