using ServerMonitor.Core.SshConfig;
using Fs = ServerMonitor.Core.Tests.SshConfig.InMemorySshConfigFileSystem;

namespace ServerMonitor.Core.Tests.SshConfig;

/// <summary>M14.4a safe Include: OpenSSH readconf.c semantics over an in-memory file system.</summary>
public sealed class SshConfigIncludeTests
{
    private static SshConfigImportResult Import(Fs fs) => SshConfigResolver.Import(fs, Fs.Profile);

    private static SshConfigHostEntry Host(Fs fs, string alias) =>
        Assert.Single(Import(fs).Hosts, host => host.Alias == alias);

    private static Fs Config(string text) => new Fs().File("config", text);

    // ---- splicing, order, paths

    [Fact]
    public void Include_IsSplicedInPlace_WithFirstWinsAcrossFiles()
    {
        var fs = Config("Host a\n  Include inc/a.conf\n  User from-root\n  HostName root.lan\n")
            .File("inc/a.conf", "User from-include\nPort 2201\n");

        var host = Host(fs, "a");

        Assert.Equal("from-include", host.User);
        Assert.Equal(2201, host.Port);
        Assert.Equal("root.lan", host.HostName);
        Assert.True(host.IsImportable);
    }

    [Fact]
    public void ValueBeforeTheInclude_WinsOverTheIncludedValue()
    {
        var host = Host(Config("Host a\n  User first\n  Include x\n").File("x", "User second\n"), "a");

        Assert.Equal("first", host.User);
    }

    [Fact]
    public void RelativePath_IsRelativeToSshDirectory_AndTildeToTheProfile()
    {
        var fs = Config("Include conf.d/web ~/.ssh/extra\nHost a\n")
            .File("conf.d/web", "User w\n")
            .File("extra", "Port 2022\n");

        var host = Host(fs, "a");

        Assert.Equal("w", host.User);
        Assert.Equal(2022, host.Port);
        Assert.Equal(
            [Fs.SshDirectory + @"\config", Fs.SshDirectory + @"\conf.d\web", Fs.SshDirectory + @"\extra"],
            fs.Reads);
    }

    [Fact]
    public void MultipleArguments_AreProcessedLeftToRight()
    {
        var fs = Config("Host a\n  Include b.conf a.conf\n")
            .File("a.conf", "User from-a\n")
            .File("b.conf", "User from-b\n");

        Assert.Equal("from-b", Host(fs, "a").User);
        Assert.Equal([Fs.SshDirectory + @"\b.conf", Fs.SshDirectory + @"\a.conf"], fs.Reads.Skip(1));
    }

    [Fact]
    public void Glob_ExpandsInOrdinalOrder()
    {
        var fs = Config("Host a\n  Include conf.d/*\n")
            .File("conf.d/a", "User lower-a\n")
            .File("conf.d/B", "User upper-b\n")
            .File("conf.d/_c", "User underscore\n");

        Assert.Equal("upper-b", Host(fs, "a").User);
        Assert.Equal(
            [@"conf.d\B", @"conf.d\_c", @"conf.d\a"],
            fs.Reads.Skip(1).Select(path => path[(Fs.SshDirectory.Length + 1)..]));
    }

    [Fact]
    public void QuestionMarkGlob_MatchesExactlyOneCharacter()
    {
        var fs = Config("Host a\n  Include conf.d/h?\n")
            .File("conf.d/h1", "User one\n")
            .File("conf.d/h2", "Port 2\n")
            .File("conf.d/h10", "HostName ten\n");

        var host = Host(fs, "a");

        Assert.Equal("one", host.User);
        Assert.Equal(2, host.Port);
        Assert.Equal("a", host.HostName);
    }

    [Fact]
    public void Glob_DoesNotMatchDotfiles_UnlessThePatternStartsWithADot()
    {
        var fs = Config("Host a\n  Include conf.d/*\nHost b\n  Include conf.d/.h*\n")
            .File("conf.d/.hidden", "User hidden\n")
            .File("conf.d/visible", "Port 2200\n");

        Assert.Null(Host(fs, "a").User);
        Assert.Equal(2200, Host(fs, "a").Port);
        Assert.Equal("hidden", Host(fs, "b").User);
    }

    // ---- block context (activep / NEVERMATCH / restore)

    [Fact]
    public void IncludeInNonMatchingHost_NothingInTheIncludedFileApplies()
    {
        var fs = Config("Host other\n  Include x\nHost a\n")
            .File("x", "User leaked\nHost a\n  Port 2222\nHost b\n  HostName b.lan\n");

        var result = Import(fs);
        var a = Assert.Single(result.Hosts, host => host.Alias == "a");

        Assert.Null(a.User);
        Assert.Null(a.Port);
        Assert.DoesNotContain(result.Hosts, host => host.Alias == "b");
    }

    [Fact]
    public void IncludeInMatchingHost_HostBlocksInsideTheIncludedFileApply()
    {
        var fs = Config("Host *\n  Include x\n")
            .File("x", "Host a\n  Port 2222\nHost b\n  HostName b.lan\n");

        Assert.Equal(2222, Host(fs, "a").Port);
        Assert.Equal("b.lan", Host(fs, "b").HostName);
    }

    [Fact]
    public void EnclosingBlockResumesAfterTheInclude()
    {
        var fs = Config("Host a\n  Include x\n  User after\n")
            .File("x", "Host other\n  Port 1\n");

        var host = Host(fs, "a");

        Assert.Equal("after", host.User);
        Assert.Null(host.Port);
    }

    [Fact]
    public void IncludedFileLeadingLines_BelongToTheEnclosingBlock()
    {
        var fs = Config("Host a\n  Include x\nHost b\n")
            .File("x", "User lead\nHost zzz\n  Port 9\n");

        Assert.Equal("lead", Host(fs, "a").User);
        Assert.Null(Host(fs, "b").User);
    }

    // ---- proxy safety through includes

    [Fact]
    public void ProxyJumpInsideAnIncludedFile_ResolvesExactlyAsIfInline()
    {
        var host = Host(Config("Host a\n  Include x\n").File("x", "ProxyJump bastion\n"), "a");

        Assert.True(host.IsImportable);
        Assert.Equal("bastion", host.Jump?.HostName);
    }

    [Fact]
    public void MultiHopProxyJumpInsideAnIncludedFile_BlocksExactlyAsIfInline()
    {
        var host = Host(Config("Host a\n  Include x\n").File("x", "ProxyJump j1,j2\n"), "a");

        Assert.False(host.IsImportable);
        Assert.Equal(SshConfigHostBlocker.JumpMultiHop, host.Blocker);
    }

    [Fact]
    public void ProxyJumpNoneDecidedBeforeAnInclude_StaysImportable()
    {
        var fs = Config("Host a\n  ProxyJump none\n  Include x ../outside\n").File("x", "ProxyJump bastion\n");

        Assert.True(Host(fs, "a").IsImportable);
    }

    [Fact]
    public void IncludeUnderMatch_ProxyInsideIsUnverifiable()
    {
        var host = Host(Config("Match all\n  Include x\nHost a\n").File("x", "ProxyJump bastion\n"), "a");

        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByMatch, host.Blocker);
    }

    // ---- missing / empty

    [Fact]
    public void MissingFileOrEmptyGlob_ContributesNothing_WithADeterministicDiagnostic()
    {
        var fs = Config("Include missing.conf conf.d/*.none\nHost a\n  User u\n").AddDirectory("conf.d");

        var result = Import(fs);
        var host = Assert.Single(result.Hosts);

        Assert.True(host.IsImportable);
        Assert.Equal("u", host.User);
        Assert.DoesNotContain(SshConfigFileWarning.IncludeNotFollowed, result.FileWarnings);
        Assert.Equal(
            [
                new SshConfigDiagnostic(SshConfigDiagnosticKind.IncludeMatchedNoFiles, "missing.conf"),
                new SshConfigDiagnostic(SshConfigDiagnosticKind.IncludeMatchedNoFiles, "conf.d/*.none")
            ],
            result.Diagnostics);
    }

    // ---- fatal: cycle, depth, syntax, encoding, unreadable

    [Fact]
    public void Cycle_FailsTheWholeImport_NamingTheFile()
    {
        var result = Import(Config("Include a\nHost h\n").File("a", "Include b\n").File("b", "Include a\n"));

        Assert.Equal(SshConfigImportStatus.Error, result.Status);
        Assert.Equal(SshConfigImportErrorCode.IncludeCycle, result.ErrorCode);
        Assert.Equal(Fs.SshDirectory + @"\a", result.ErrorDetail);
        Assert.Empty(result.Hosts);
    }

    [Fact]
    public void SelfInclude_IsACycle()
    {
        var result = Import(Config("Include config\nHost h\n"));

        Assert.Equal(SshConfigImportErrorCode.IncludeCycle, result.ErrorCode);
        Assert.Equal(Fs.SshDirectory + @"\config", result.ErrorDetail);
    }

    [Fact]
    public void Depth17_FailsTheWholeImport_Depth16Loads()
    {
        static Fs Chain(int length)
        {
            var fs = Config("Include f1\nHost h\n");
            for (var i = 1; i <= length; i++)
            {
                fs.File($"f{i}", i < length ? $"Include f{i + 1}\n" : "User deepest\n");
            }

            return fs;
        }

        Assert.Equal("deepest", Host(Chain(16), "h").User);

        var result = Import(Chain(17));
        Assert.Equal(SshConfigImportErrorCode.IncludeTooDeep, result.ErrorCode);
        Assert.Equal(Fs.SshDirectory + @"\f17", result.ErrorDetail);
        Assert.Empty(result.Hosts);
    }

    [Theory]
    [InlineData("\"Host\" other\n")]
    [InlineData("Host\n")]
    [InlineData("Proxy\"Jump\" x\n")]
    public void FatalSyntaxInAnIncludedFile_FailsTheWholeImport(string included)
    {
        var result = Import(Config("Host a\n  Include x\n").File("x", included));

        Assert.Equal(SshConfigImportErrorCode.InvalidSyntax, result.ErrorCode);
        Assert.Equal(Fs.SshDirectory + @"\x", result.ErrorDetail);
        Assert.Empty(result.Hosts);
    }

    [Fact]
    public void FatalSyntaxInAnIncludedFileFromANonMatchingBlock_StillFails()
    {
        var result = Import(Config("Host nobody\n  Include x\nHost a\n").File("x", "Host\n"));

        Assert.Equal(SshConfigImportErrorCode.InvalidSyntax, result.ErrorCode);
    }

    [Fact]
    public void InvalidUtf8OrUnreadableIncludedFile_FailsTheWholeImport()
    {
        Assert.Equal(
            SshConfigImportErrorCode.InvalidEncoding,
            Import(Config("Include x\nHost a\n").File("x", [0xC3, 0x28])).ErrorCode);
        Assert.Equal(
            SshConfigImportErrorCode.Unreadable,
            Import(Config("Include x\nHost a\n").File("x", [], SshConfigReadStatus.Unreadable)).ErrorCode);
    }

    // ---- containment

    [Theory]
    [InlineData("../outside")]
    [InlineData("conf.d/../../outside")]
    [InlineData(@"C:\Users\tester\outside")]
    [InlineData("~/outside")]
    [InlineData(@"D:\.ssh\x")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"\\?\C:\Users\tester\.ssh\x")]
    [InlineData(@"\\.\C:\Users\tester\.ssh\x")]
    [InlineData("/etc/ssh/x")]
    [InlineData("x:stream")]
    [InlineData("x.conf:stream")]
    [InlineData("NUL")]
    [InlineData("conf.d/con.txt")]
    [InlineData("C:x")]
    public void EscapingPath_CannotBeVerified_AndNothingOutsideIsOpened(string argument)
    {
        var fs = Config($"Host a\n  Include {argument}\n")
            .File(@"C:\Users\tester\outside", "ProxyJump none\n")
            .File(@"D:\.ssh\x", "ProxyJump none\n")
            .File("x", "ProxyJump none\n")
            .File("x.conf", "ProxyJump none\n");

        var result = Import(fs);
        var host = Assert.Single(result.Hosts);

        Assert.False(host.IsImportable);
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.Contains(host.Findings, f => f.Keyword == "Include" && f.Reason == SshConfigFindingReason.IncludeOutsideSshDirectory);
        Assert.Equal([Fs.SshDirectory + @"\config"], fs.Reads);
        Assert.Contains(SshConfigFileWarning.IncludeNotFollowed, result.FileWarnings);
    }

    [Fact]
    public void DotDotThatStaysInsideSshDirectory_IsFollowed()
    {
        var fs = Config("Host a\n  Include conf.d/../x\n").File("x", "User inside\n").AddDirectory("conf.d");

        Assert.Equal("inside", Host(fs, "a").User);
    }

    // ---- reparse points (attribute seam; real ones are covered in Infrastructure)

    [Theory]
    [InlineData("x", new[] { "x" })]
    [InlineData("conf.d", new[] { @"conf.d\y" })]
    public void ReparsePointOnTheFileOrAnyDirectoryDownFromSsh_CannotBeVerified(string reparse, string[] neverOpened)
    {
        var fs = Config("Host a\n  Include x conf.d/y\n")
            .File("x", "User x\n")
            .File("conf.d/y", "Port 22\n")
            .ReparsePoint(reparse);

        var host = Host(fs, "a");

        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.Contains(host.Findings, f => f.Reason == SshConfigFindingReason.IncludeReparsePoint);
        Assert.All(neverOpened, path => Assert.DoesNotContain(Fs.SshDirectory + @"\" + path, fs.Reads));
    }

    [Theory]
    [InlineData("config")]
    [InlineData(Fs.SshDirectory)]
    public void RootConfigOrSshDirectoryIsALink_FailsTheWholeImport_AndNothingIsOpened(string reparse)
    {
        var fs = Config("Host a\n  User u\n").ReparsePoint(reparse);

        var result = Import(fs);

        Assert.Equal(SshConfigImportStatus.Error, result.Status);
        Assert.Equal(SshConfigImportErrorCode.ConfigIsLink, result.ErrorCode);
        Assert.Empty(result.Hosts);
        Assert.Empty(fs.Reads);
    }

    [Fact]
    public void ReparsePointMatchedByAGlob_IsNotOpened()
    {
        var fs = Config("Host a\n  Include conf.d/*\n")
            .File("conf.d/a", "User a\n")
            .File("conf.d/b", "Port 22\n")
            .ReparsePoint("conf.d/b");

        var host = Host(fs, "a");

        Assert.Equal("a", host.User);
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.DoesNotContain(Fs.SshDirectory + @"\conf.d\b", fs.Reads);
    }

    [Fact]
    public void GlobInReparseDirectory_IsNeverEnumerated()
    {
        var fs = Config("Host a\n  Include conf.d/*\n").File("conf.d/a", "User a\n").ReparsePoint("conf.d");

        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, Host(fs, "a").Blocker);
        Assert.Empty(fs.Enumerations);
    }

    // ---- non-regular and unsupported patterns

    [Theory]
    [InlineData("conf.d")]
    [InlineData("conf.d/")]
    [InlineData("conf.d/*")]
    [InlineData("dev")]
    public void DirectoryOrDeviceMatch_CannotBeVerified(string argument)
    {
        var fs = Config($"Host a\n  Include {argument}\n").AddDirectory("conf.d/sub").Device("dev");

        var host = Host(fs, "a");

        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.Contains(host.Findings, f => f.Reason == SshConfigFindingReason.IncludeNotRegularFile);
    }

    [Theory]
    [InlineData("conf.d/[ab]")]
    [InlineData("conf.d/**")]
    [InlineData("conf.d/{a,b}")]
    [InlineData("*/x")]
    [InlineData("conf.*/x")]
    public void UnsupportedGlob_CannotBeVerified(string argument)
    {
        var fs = Config($"Host a\n  Include {argument}\n").File("conf.d/a", "User a\n");

        var host = Host(fs, "a");

        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.Contains(host.Findings, f => f.Reason == SshConfigFindingReason.IncludeUnsupportedPattern);
        Assert.Empty(fs.Enumerations);
    }

    [Theory]
    [InlineData("%d/.ssh/x")]
    [InlineData("${HOME}/.ssh/x")]
    [InlineData("~other/.ssh/x")]
    public void UnsupportedExpansion_CannotBeVerified(string argument)
    {
        var host = Host(Config($"Host a\n  Include {argument}\n"), "a");

        Assert.Contains(host.Findings, f => f.Reason == SshConfigFindingReason.IncludeUnsupportedExpansion);
        Assert.False(host.IsImportable);
    }

    // ---- limits

    [Fact]
    public void SixtyFiveFiles_FailTheWholeImport()
    {
        var fs = Config("Include conf.d/*\nHost a\n");
        for (var i = 0; i < 64; i++)
        {
            fs.File($"conf.d/f{i:D2}", "User u\n");
        }

        var result = Import(fs);

        Assert.Equal(SshConfigImportErrorCode.TooManyFiles, result.ErrorCode);
        Assert.Empty(result.Hosts);
        Assert.Equal(64, fs.Reads.Count);
    }

    [Fact]
    public void SixtyFourFiles_Load()
    {
        var fs = Config("Include conf.d/*\nHost a\n");
        for (var i = 0; i < 63; i++)
        {
            fs.File($"conf.d/f{i:D2}", "User u\n");
        }

        Assert.Equal("u", Host(fs, "a").User);
    }

    [Fact]
    public void MoreThanOneMebibyteInTotal_FailsTheWholeImport()
    {
        var fs = Config("Include conf.d/*\nHost a\n");
        var chunk = new string('#', 250 * 1024) + "\n";
        for (var i = 0; i < 5; i++)
        {
            fs.File($"conf.d/f{i}", chunk);
        }

        var result = Import(fs);

        Assert.Equal(SshConfigImportErrorCode.TooLarge, result.ErrorCode);
        Assert.Empty(result.Hosts);
    }

    [Fact]
    public void OneIncludedFileOverTheFileCap_FailsTheWholeImport()
    {
        var fs = Config("Include big\nHost a\n").File("big", new string('#', SshConfigIncludeExpander.MaxFileBytes + 1));

        var result = Import(fs);

        Assert.Equal(SshConfigImportErrorCode.TooLarge, result.ErrorCode);
        Assert.Equal(Fs.SshDirectory + @"\big", result.ErrorDetail);
    }

    [Fact]
    public void MoreThan256GlobMatches_FailTheWholeImport_BeforeReadingAny()
    {
        var fs = Config("Include conf.d/*\nHost a\n");
        for (var i = 0; i < 257; i++)
        {
            fs.File($"conf.d/f{i:D3}", string.Empty);
        }

        var result = Import(fs);

        Assert.Equal(SshConfigImportErrorCode.TooManyFiles, result.ErrorCode);
        Assert.Equal([Fs.SshDirectory + @"\config"], fs.Reads);
    }

    // ---- read scope

    [Fact]
    public void OnlyFilesUnderSshDirectoryAreEverOpened()
    {
        var fs = Config("Include conf.d/* ../x /etc/y ~/z\nHost a\n  IdentityFile ~/.ssh/id_a\n")
            .File("conf.d/one", "User one\n")
            .File("id_a", "PRIVATE KEY")
            .File(@"C:\Users\tester\x", "User outside\n");

        Import(fs);

        Assert.All(fs.Reads, path => Assert.StartsWith(Fs.SshDirectory + @"\", path, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Fs.SshDirectory + @"\id_a", fs.Reads);
    }
}
