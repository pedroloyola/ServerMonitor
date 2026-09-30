using ServerMonitor.Core.SshConfig;
using Fs = ServerMonitor.Core.Tests.SshConfig.InMemorySshConfigFileSystem;

namespace ServerMonitor.Core.Tests.SshConfig;

/// <summary>
/// M14.4c: a single-hop ProxyJump from ~/.ssh/config is imported as ssh would use it (the jump host is
/// resolved against the same config, explicit user@/:port win); every other shape is blocked with its
/// own reason and is never imported as a direct connection.
/// </summary>
public sealed class SshConfigProxyJumpTests
{
    private const string Profile = @"C:\Users\tester";

    private static SshConfigHostEntry Host(string text, string alias) =>
        Assert.Single(SshConfigResolver.Import(text, Profile).Hosts, host => host.Alias == alias);

    private static SshConfigJumpHost Jump(string text, string alias)
    {
        var host = Host(text, alias);
        Assert.Equal(SshConfigHostBlocker.None, host.Blocker);
        return Assert.IsType<SshConfigJumpHost>(host.Jump);
    }

    private static void AssertBlocked(SshConfigHostBlocker expected, string text, string alias = "target")
    {
        var host = Host(text, alias);

        Assert.Equal(expected, host.Blocker);
        Assert.False(host.IsImportable);
        Assert.Null(host.Jump);
    }

    // ---- supported: one verified hop

    [Fact]
    public void SimpleJumpAlias_WithoutItsOwnBlock_IsTheLiteralHost()
    {
        var jump = Jump("Host target\n  HostName 10.0.0.5\n  ProxyJump bastion\n", "target");

        Assert.Equal("bastion", jump.Name);
        Assert.Equal("bastion", jump.HostName);
        Assert.Null(jump.User);
        Assert.Null(jump.Port);
        Assert.Null(jump.IdentityFile);
    }

    [Fact]
    public void JumpAlias_TakesItsOwnHostNameUserPortAndIdentityFile()
    {
        var host = Host(
            """
            Host target
                HostName 10.0.0.5
                User app
                Port 2201
                ProxyJump bastion
            Host bastion
                HostName 203.0.113.7
                User jumper
                Port 2222
                IdentityFile ~/.ssh/jump_key
            """,
            "target");

        Assert.True(host.IsImportable);
        Assert.Equal("10.0.0.5", host.HostName);
        Assert.Equal("app", host.User);
        Assert.Equal(2201, host.Port);
        var jump = Assert.IsType<SshConfigJumpHost>(host.Jump);
        Assert.Equal("bastion", jump.Name);
        Assert.Equal("203.0.113.7", jump.HostName);
        Assert.Equal("jumper", jump.User);
        Assert.Equal(2222, jump.Port);
        Assert.Equal(@"C:\Users\tester\.ssh\jump_key", jump.IdentityFile);
    }

    [Fact]
    public void ExplicitUserAndPortInTheValue_WinOverTheJumpsConfig()
    {
        var jump = Jump(
            "Host target\n  ProxyJump ops@bastion:2200\nHost bastion\n  HostName 203.0.113.7\n  User jumper\n  Port 2222\n",
            "target");

        Assert.Equal("203.0.113.7", jump.HostName);
        Assert.Equal("ops", jump.User);
        Assert.Equal(2200, jump.Port);
    }

    [Fact]
    public void JumpConfigValues_FillOnlyWhatTheValueLeavesOut()
    {
        var jump = Jump(
            "Host target\n  ProxyJump bastion:2200\nHost bastion\n  User jumper\n  Port 2222\n",
            "target");

        Assert.Equal("jumper", jump.User);
        Assert.Equal(2200, jump.Port);
    }

    [Fact]
    public void JumpResolution_IsFirstValueWinsAcrossHostBlocks_IncludingWildcards()
    {
        var jump = Jump(
            """
            Host target
                ProxyJump bastion
            Host bast*
                User from-wildcard
                Port 2000
            Host bastion
                HostName 203.0.113.7
                User from-concrete
                Port 3000
            Host *
                User from-star
            """,
            "target");

        Assert.Equal("203.0.113.7", jump.HostName);
        Assert.Equal("from-wildcard", jump.User);
        Assert.Equal(2000, jump.Port);
    }

    [Fact]
    public void JumpNameWithNoMatchingBlock_IsALiteralHost_AndWildcardBlocksStillApply()
    {
        var jump = Jump(
            "Host target\n  ProxyJump inexistente.example\nHost *.example\n  User wild\n  Port 2022\n",
            "target");

        Assert.Equal("inexistente.example", jump.Name);
        Assert.Equal("inexistente.example", jump.HostName);
        Assert.Equal("wild", jump.User);
        Assert.Equal(2022, jump.Port);
    }

    [Fact]
    public void FirstApplicableProxyJump_WinsAcrossHostBlocks()
    {
        var jump = Jump(
            "Host target\n  ProxyJump first\nHost targ*\n  ProxyJump second\n",
            "target");

        Assert.Equal("first", jump.Name);
    }

    [Fact]
    public void JumpDefinedThroughAnInclude_IsResolvedWithTheSameResolver()
    {
        var fs = new Fs()
            .File("config", "Include jumps.conf\nHost target\n  HostName 10.0.0.5\n  ProxyJump bastion\n")
            .File("jumps.conf", "Host bastion\n  HostName 203.0.113.7\n  User inc\n");

        var host = Assert.Single(SshConfigResolver.Import(fs, Fs.Profile).Hosts, h => h.Alias == "target");

        Assert.True(host.IsImportable);
        Assert.Equal("203.0.113.7", host.Jump?.HostName);
        Assert.Equal("inc", host.Jump?.User);
    }

    [Theory]
    [InlineData("[2001:db8::7]", "2001:db8::7", null, null)]
    [InlineData("[2001:db8::7]:2222", "2001:db8::7", null, 2222)]
    [InlineData("ops@[2001:db8::7]:2222", "2001:db8::7", "ops", 2222)]
    [InlineData("192.0.2.10:2200", "192.0.2.10", null, 2200)]
    [InlineData("first.last@bastion", "bastion", "first.last", null)]
    [InlineData("user@corp@bastion", "bastion", "user@corp", null)]
    [InlineData("bastion:022", "bastion", null, 22)]
    [InlineData("bastion:65535", "bastion", null, 65535)]
    [InlineData("bastion:1", "bastion", null, 1)]
    public void Grammar_AcceptsOnlyTheVerifiedSingleHopForms(string value, string host, string? user, int? port)
    {
        var jump = Jump($"Host target\n  HostName 10.0.0.5\n  ProxyJump {value}\n", "target");

        Assert.Equal(host, jump.Name);
        Assert.Equal(host, jump.HostName);
        Assert.Equal(user, jump.User);
        Assert.Equal(port, jump.Port);
    }

    [Fact]
    public void ProxyJumpNone_StaysDirect()
    {
        var host = Host("Host target\n  ProxyJump none\nHost *\n  ProxyJump bastion\n", "target");

        Assert.True(host.IsImportable);
        Assert.Null(host.Jump);
    }

    [Fact]
    public void SupportedJump_IsNotReportedAsAnUnsupportedKeyword()
    {
        var host = Host("Host target\n  ProxyJump bastion\n", "target");

        Assert.DoesNotContain(host.Findings, finding => finding.Keyword == "ProxyJump");
    }

    [Fact]
    public void JumpFindings_AreKeptApartFromTheTargets()
    {
        var host = Host(
            "Host target\n  ProxyJump bastion\nHost bastion\n  IdentityFile ~/.ssh/a\n  IdentityFile ~/.ssh/b\n",
            "target");

        Assert.Empty(host.Findings);
        Assert.Contains(host.Jump!.Findings, f => f.Keyword == "IdentityFile" && f.Kind == SshConfigFindingKind.Ambiguous);
        Assert.Null(host.Jump.IdentityFile);
    }

    // ---- blocked: multi-hop / chained

    [Theory]
    [InlineData("j1,j2")]
    [InlineData("j1,")]
    [InlineData(",j1")]
    [InlineData("ops@j1:22,ops@j2:22")]
    public void CommaList_IsMultiHop(string value) =>
        AssertBlocked(SshConfigHostBlocker.JumpMultiHop, $"Host target\n  ProxyJump {value}\n");

    [Fact]
    public void JumpWithItsOwnProxyJump_IsChainedMultiHop() =>
        AssertBlocked(
            SshConfigHostBlocker.JumpMultiHop,
            "Host target\n  ProxyJump bastion\nHost bastion\n  ProxyJump outer\n");

    [Fact]
    public void JumpInheritingAProxyJumpFromAWildcard_IsChainedMultiHop() =>
        AssertBlocked(
            SshConfigHostBlocker.JumpMultiHop,
            "Host target\n  ProxyJump bastion\nHost *\n  ProxyJump outer\n");

    [Fact]
    public void JumpWithAMalformedOwnProxyJump_IsStillChained() =>
        AssertBlocked(
            SshConfigHostBlocker.JumpMultiHop,
            "Host target\n  ProxyJump bastion\nHost bastion\n  ProxyJump a b\n");

    [Fact]
    public void JumpWithProxyJumpNone_IsDirect()
    {
        var jump = Jump("Host target\n  ProxyJump bastion\nHost bastion\n  ProxyJump none\nHost *\n  ProxyJump outer\n", "target");

        Assert.Equal("bastion", jump.HostName);
    }

    // ---- blocked: ProxyCommand

    [Fact]
    public void ProxyCommandOnTheTarget_IsProxyCommand() =>
        AssertBlocked(SshConfigHostBlocker.ProxyCommand, "Host target\n  ProxyCommand nc bastion 22\n  ProxyJump bastion\n");

    [Fact]
    public void ProxyCommandOnTheJump_IsProxyCommand() =>
        AssertBlocked(
            SshConfigHostBlocker.ProxyCommand,
            "Host target\n  ProxyJump bastion\nHost bastion\n  ProxyCommand ssh -W %h:%p outer\n");

    // ---- blocked: unparsable

    [Theory]
    [InlineData("ssh://bastion")]
    [InlineData("ssh://ops@bastion:2222")]
    [InlineData("%h")]
    [InlineData("%r@bastion")]
    [InlineData("bastion-%p")]
    [InlineData("bastion:0")]
    [InlineData("bastion:65536")]
    [InlineData("bastion:99999999999")]
    [InlineData("bastion:")]
    [InlineData("bastion:-1")]
    [InlineData("bastion:22x")]
    [InlineData("@bastion")]
    [InlineData("ops@")]
    [InlineData("2001:db8::7")]
    [InlineData("[2001:db8::7")]
    [InlineData("[2001:db8::7]x")]
    [InlineData("[bastion]")]
    [InlineData("[192.0.2.10]:22")]
    [InlineData("[fe80::1%eth0]")]
    [InlineData("bastion/22")]
    [InlineData("-oProxyCommand=x")]
    [InlineData("-bastion")]
    [InlineData(".bastion")]
    [InlineData("-l@bastion")]
    [InlineData("ops@bastion%")]
    [InlineData("ops;rm@bastion")]
    [InlineData("bas$tion")]
    [InlineData("NONE")]
    [InlineData("None")]
    [InlineData("\"bastion\"")]
    [InlineData("'bastion'")]
    [InlineData("bas\\ tion")]
    [InlineData("bastion # comment")]
    [InlineData("bastion extra")]
    public void UnverifiedValue_IsUnparsable(string value) =>
        AssertBlocked(SshConfigHostBlocker.JumpUnparsable, $"Host target\n  HostName 10.0.0.5\n  ProxyJump {value}\n");

    [Theory]
    [InlineData("  ProxyJump\n")]
    [InlineData("  ProxyJump=\n")]
    [InlineData("  ProxyJump \"\"\n")]
    public void EmptyValue_IsUnparsable(string line) =>
        AssertBlocked(SshConfigHostBlocker.JumpUnparsable, $"Host target\n{line}");

    // ---- blocked: cycles

    [Theory]
    [InlineData("target")]
    [InlineData("TARGET")]
    [InlineData("ops@target:2222")]
    public void JumpToTheTargetItself_IsACycle(string value) =>
        AssertBlocked(SshConfigHostBlocker.JumpCycle, $"Host target\n  HostName 10.0.0.5\n  ProxyJump {value}\n");

    [Fact]
    public void JumpToTheTargetItself_IsReportedAsACycle_EvenWhenAMatchWouldAlsoBlockTheJump() =>
        AssertBlocked(
            SshConfigHostBlocker.JumpCycle,
            "Host target\n  ProxyJump target\nMatch host other\n  HostName 198.51.100.1\n");

    [Fact]
    public void JumpBackToTheTarget_IsACycle() =>
        AssertBlocked(
            SshConfigHostBlocker.JumpCycle,
            "Host a\n  ProxyJump b\nHost b\n  ProxyJump a\n",
            "a");

    [Fact]
    public void JumpToItself_IsACycle() =>
        AssertBlocked(
            SshConfigHostBlocker.JumpCycle,
            "Host target\n  ProxyJump b\nHost b\n  ProxyJump b\n");

    [Fact]
    public void WildcardProxyJumpThatAlsoAppliesToTheJump_IsACycle() =>
        AssertBlocked(
            SshConfigHostBlocker.JumpCycle,
            "Host bastion\n  HostName 203.0.113.7\nHost *\n  ProxyJump bastion\nHost target\n");

    [Fact]
    public void JumpResolvingToTheTargetsOwnEndpoint_IsACycle() =>
        AssertBlocked(
            SshConfigHostBlocker.JumpCycle,
            "Host target\n  HostName 10.0.0.5\n  ProxyJump bastion\nHost bastion\n  HostName 10.0.0.5\n");

    [Fact]
    public void JumpOnTheSameAddressButAnotherPort_IsNotACycle()
    {
        var jump = Jump(
            "Host target\n  HostName 10.0.0.5\n  ProxyJump bastion\nHost bastion\n  HostName 10.0.0.5\n  Port 2222\n",
            "target");

        Assert.Equal(2222, jump.Port);
    }

    // ---- blocked: the jump's own resolution is unsafe or inexact

    [Theory]
    [InlineData("Match all\n  ProxyJump outer\n")]
    [InlineData("Match host bastion\n  ProxyCommand nc x 22\n")]
    [InlineData("Match host bastion\n  HostName 198.51.100.1\n")]
    [InlineData("Match all\n  Include other\n")]
    public void MatchThatMaySetTheJumpsProxyOrHostName_Blocks(string match) =>
        AssertBlocked(
            SshConfigHostBlocker.ProxyMaySetByMatch,
            $"Host target\n  HostName 10.0.0.5\n  ProxyJump bastion\n{match}");

    [Fact]
    public void MatchAfterTheJumpsHostNameAndProxyAreDecided_DoesNotBlock()
    {
        var jump = Jump(
            "Host target\n  ProxyJump bastion\nHost bastion\n  HostName 203.0.113.7\n  ProxyJump none\nMatch all\n  HostName x\n  ProxyJump y\n",
            "target");

        Assert.Equal("203.0.113.7", jump.HostName);
        Assert.Contains(jump.Findings, f => f.Keyword == "Match");
    }

    [Fact]
    public void MatchSettingOnlyTheJumpsUser_IsNotABlocker()
    {
        var jump = Jump("Host target\n  ProxyJump bastion\nMatch all\n  User m\n", "target");

        Assert.Null(jump.User);
    }

    [Fact]
    public void UnverifiableIncludeBeforeTheJumpsHostName_Blocks() =>
        // Proxy and canonicalisation are already decided: only the jump's pending HostName is at stake.
        AssertBlocked(
            SshConfigHostBlocker.ProxyMaySetByInclude,
            "Host target\n  CanonicalizeHostname no\n  ProxyJump bastion\nHost bastion\n  ProxyJump none\n  CanonicalizeHostname no\n  Include conf.d/*\n  HostName 203.0.113.7\n");

    [Fact]
    public void UnverifiableIncludeAfterTheJumpsAddressAndProxyAreDecided_DoesNotBlock()
    {
        var jump = Jump(
            "Host target\n  ProxyJump bastion\nHost bastion\n  HostName 203.0.113.7\n  CanonicalizeHostname no\n  ProxyJump none\n  Include conf.d/*\n",
            "target");

        Assert.Equal("203.0.113.7", jump.HostName);
    }

    [Fact]
    public void UnverifiableIncludeBeforeTheJumpsCanonicalizeHostnameIsDecided_Blocks() =>
        AssertBlocked(
            SshConfigHostBlocker.ProxyMaySetByInclude,
            "Host target\n  ProxyJump bastion\nHost bastion\n  HostName 203.0.113.7\n  ProxyJump none\n  Include conf.d/*\n");

    // ---- Vigil M14.4c M-3: CanonicalizeHostname makes ssh re-read the config for the jump.

    [Theory]
    [InlineData("CanonicalizeHostname yes")]
    [InlineData("CanonicalizeHostname always")]
    [InlineData("CanonicalizeHostname")]
    [InlineData("CanonicalizeHostname \"yes")]
    public void JumpThatCanonicalisesItsName_Blocks(string line) =>
        AssertBlocked(
            SshConfigHostBlocker.JumpHostNameUnresolved,
            $"Host target\n  HostName 10.0.0.5\n  ProxyJump bastion\nHost bastion\n  HostName 203.0.113.7\n  {line}\n");

    [Fact]
    public void JumpInheritingCanonicalizeHostnameFromAWildcard_Blocks() =>
        AssertBlocked(
            SshConfigHostBlocker.JumpHostNameUnresolved,
            "Host target\n  CanonicalizeHostname no\n  ProxyJump bastion\nHost *\n  CanonicalizeHostname yes\n");

    [Theory]
    [InlineData("CanonicalizeHostname no")]
    [InlineData("CanonicalizeHostname NO")]
    public void JumpWithCanonicalizeHostnameNo_IsImportable(string line)
    {
        var jump = Jump($"Host target\n  CanonicalizeHostname no\n  ProxyJump bastion\nHost bastion\n  {line}\nHost *\n  CanonicalizeHostname yes\n", "target");

        Assert.Equal("bastion", jump.HostName);
    }

    [Fact]
    public void MatchThatMayTurnOnTheJumpsCanonicalisation_Blocks() =>
        AssertBlocked(
            SshConfigHostBlocker.ProxyMaySetByMatch,
            "Host target\n  HostName 10.0.0.5\n  ProxyJump bastion\nMatch host bastion\n  CanonicalizeHostname yes\n");

    [Fact]
    public void MatchWithCanonicalizeHostnameAfterTheJumpDecidedIt_DoesNotBlock()
    {
        var jump = Jump(
            "Host target\n  CanonicalizeHostname no\n  ProxyJump bastion\nHost bastion\n  CanonicalizeHostname no\nMatch all\n  CanonicalizeHostname yes\n",
            "target");

        Assert.Equal("bastion", jump.HostName);
    }

    [Fact]
    public void MatchWithCanonicalizeHostnameNo_DoesNotBlock()
    {
        var jump = Jump("Host target\n  ProxyJump bastion\nMatch all\n  CanonicalizeHostname no\n", "target");

        Assert.Equal("bastion", jump.HostName);
    }

    [Fact]
    public void TargetThatCanonicalises_IsBlockedWithItsOwnReason_EvenWithACleanJump()
    {
        // M14-SSHCFG-CANON-1: the target blocks too, with its own reason (never JumpHostNameUnresolved).
        var host = Host("Host target\n  CanonicalizeHostname yes\n  ProxyJump bastion\nHost bastion\n  CanonicalizeHostname no\n", "target");

        Assert.False(host.IsImportable);
        Assert.Equal(SshConfigHostBlocker.CanonicalizationMayChangeRoute, host.Blocker);
        Assert.Null(host.Jump);
    }

    [Fact]
    public void JumpThatCanonicalises_UnderAClearTarget_KeepsTheJumpReason() =>
        AssertBlocked(
            SshConfigHostBlocker.JumpHostNameUnresolved,
            "Host target\n  CanonicalizeHostname no\n  ProxyJump bastion\nHost bastion\n  CanonicalizeHostname yes\n");

    // ---- Vigil M14.4c M-2: only ' ' and '\t' separate words to OpenSSH.

    [Theory]
    [InlineData(" ")]
    [InlineData("　")]
    [InlineData("\v")]
    public void ForeignWhitespaceInTheJumpValue_IsUnparsable(string ws)
    {
        AssertBlocked(SshConfigHostBlocker.JumpUnparsable, $"Host target\n  ProxyJump bastion{ws}\n");
        AssertBlocked(SshConfigHostBlocker.JumpUnparsable, $"Host target\n  ProxyJump bas{ws}tion\n");
        AssertBlocked(SshConfigHostBlocker.JumpUnparsable, $"Host target\n  ProxyJump{ws}bastion\n");
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("　")]
    [InlineData("\v")]
    public void ForeignWhitespaceAfterNone_IsNeverDirect(string ws) =>
        AssertBlocked(SshConfigHostBlocker.JumpUnparsable, $"Host target\n  ProxyJump none{ws}\nHost *\n  ProxyJump bastion\n");

    [Theory]
    [InlineData(" ")]
    [InlineData("　")]
    [InlineData("\v")]
    public void ForeignWhitespaceInTheJumpsOwnHostName_Blocks(string ws) =>
        AssertBlocked(
            SshConfigHostBlocker.JumpHostNameUnresolved,
            $"Host target\n  ProxyJump bastion\nHost bastion\n  HostName 203.0.113.7{ws}\n");

    [Theory]
    [InlineData(" ")]
    [InlineData("　")]
    [InlineData("\v")]
    public void ForeignWhitespaceInAHostHeader_FailsTheWholeFile(string ws)
    {
        // Scenario A: to ssh "web<NBSP>lab" is ONE pattern, so web falls through to Host * and is routed.
        var result = SshConfigResolver.Import($"Host web{ws}lab\n  ProxyJump none\nHost *\n  ProxyJump bastion\n", Profile);

        Assert.Equal(SshConfigImportStatus.Error, result.Status);
        Assert.Equal(SshConfigImportErrorCode.InvalidSyntax, result.ErrorCode);
        Assert.Empty(result.Hosts);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("　")]
    [InlineData("\v")]
    public void ALineLedByForeignWhitespace_IsNotAComment(string ws)
    {
        var host = Host($"Host target\n{ws}# ProxyJump none\n  HostName 10.0.0.5\n", "target");

        Assert.Contains(host.Findings, f => f.Kind == SshConfigFindingKind.Invalid);
    }

    [Fact]
    public void UnverifiableIncludeAppliedOnlyToTheJump_Blocks() =>
        AssertBlocked(
            SshConfigHostBlocker.ProxyMaySetByInclude,
            "Host target\n  HostName 10.0.0.5\n  ProxyJump bastion\nHost bastion\n  Include ../outside\n");

    [Theory]
    [InlineData("HostName %h.corp")]
    [InlineData("HostName a b")]
    public void JumpHostNameThatCannotBeResolvedExactly_Blocks(string line) =>
        AssertBlocked(
            SshConfigHostBlocker.JumpHostNameUnresolved,
            $"Host target\n  ProxyJump bastion\nHost bastion\n  {line}\n");

    [Fact]
    public void TargetBlockedBeforeItsProxyJump_KeepsThatBlocker_AndNeverCarriesAJump()
    {
        var host = Host("Match all\n  ProxyCommand x\nHost target\n  ProxyJump bastion\n", "target");

        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByMatch, host.Blocker);
        Assert.Null(host.Jump);
        Assert.Contains(host.Findings, f => f.Keyword == "ProxyJump" && f.Kind == SshConfigFindingKind.Unsupported);
    }

    [Fact]
    public void TargetBlockedByItsOwnInclude_NeverCarriesAJump_EvenWhenTheJumpIsClean()
    {
        // The Include applies only to the target, so the jump alone would resolve cleanly.
        var host = Host("Host target\n  Include ../outside\n  ProxyJump bastion\nHost bastion\n  HostName 203.0.113.7\n", "target");

        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
        Assert.False(host.IsImportable);
        Assert.Null(host.Jump);
    }

    [Fact]
    public void BlockedJump_IsListedAsAnUnsupportedProxyJump()
    {
        var host = Host("Host target\n  ProxyJump a,b\n", "target");

        Assert.Contains(host.Findings, f => f.Keyword == "ProxyJump" && f.Kind == SshConfigFindingKind.Unsupported);
    }

    // ---- regression: direct hosts are untouched by jump resolution

    [Fact]
    public void DirectHost_IsUnchanged_WhenOtherHostsUseJumps()
    {
        var result = SshConfigResolver.Import(
            "Host web\n  HostName 10.0.0.5\n  User deploy\nHost inner\n  ProxyJump web\n",
            Profile);

        var web = Assert.Single(result.Hosts, h => h.Alias == "web");
        Assert.True(web.IsImportable);
        Assert.Null(web.Jump);
        Assert.Equal("10.0.0.5", web.HostName);
        var inner = Assert.Single(result.Hosts, h => h.Alias == "inner");
        Assert.Equal("10.0.0.5", inner.Jump?.HostName);
        Assert.Equal("deploy", inner.Jump?.User);
    }

    [Fact]
    public void MatchThatSetsOnlyAHostName_DoesNotBlockADirectHost()
    {
        var host = Host("Host web\n  User u\nMatch all\n  HostName 198.51.100.1\n", "web");

        Assert.True(host.IsImportable);
        Assert.Equal("web", host.HostName);
    }
}
