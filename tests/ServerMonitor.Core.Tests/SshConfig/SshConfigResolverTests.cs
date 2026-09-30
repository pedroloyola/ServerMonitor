using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.Core.Tests.SshConfig;

public sealed class SshConfigResolverTests
{
    private const string Profile = @"C:\Users\tester";

    private static SshConfigImportResult Import(string text) => SshConfigResolver.Import(text, Profile);

    private static SshConfigHostEntry Host(string text, string alias) =>
        Assert.Single(Import(text).Hosts, host => host.Alias == alias);

    private static bool Has(SshConfigHostEntry host, string keyword, SshConfigFindingKind kind) =>
        host.Findings.Any(f => f.Keyword == keyword && f.Kind == kind);

    [Fact]
    public void EmptyConfig_LoadsWithNoHostsAndNoWarnings()
    {
        var result = Import(string.Empty);

        Assert.Equal(SshConfigImportStatus.Loaded, result.Status);
        Assert.Empty(result.Hosts);
        Assert.Empty(result.FileWarnings);
    }

    [Fact]
    public void Alias_ResolvesHostNameUserPortAndIdentityFile()
    {
        var host = Host(
            """
            Host web
                HostName 10.0.0.5
                User deploy
                Port 2222
                IdentityFile ~/.ssh/id_ed25519
            """,
            "web");

        Assert.Equal("10.0.0.5", host.HostName);
        Assert.Equal("deploy", host.User);
        Assert.Equal(2222, host.Port);
        Assert.Equal(@"C:\Users\tester\.ssh\id_ed25519", host.IdentityFile);
        Assert.True(host.IsImportable);
        Assert.Empty(host.Findings);
    }

    [Fact]
    public void AliasWithoutHostName_UsesAliasAsHost()
    {
        var host = Host("Host nas.local\n  User admin\n", "nas.local");

        Assert.Equal("nas.local", host.HostName);
        Assert.Equal("admin", host.User);
        Assert.Null(host.Port);
        Assert.Null(host.IdentityFile);
    }

    [Fact]
    public void Syntax_KeywordEqualsValueQuotesCommentsAndCaseInsensitiveKeywords()
    {
        var host = Host(
            """
            # leading comment

            HOST box   # trailing comment
              hostname=box.example.com
              USER = "ops"
              port	=22 # comment
              IdentityFile "C:\Program Files\keys\my key"
            """,
            "box");

        Assert.Equal("box.example.com", host.HostName);
        Assert.Equal("ops", host.User);
        Assert.Equal(22, host.Port);
        Assert.Equal(@"C:\Program Files\keys\my key", host.IdentityFile);
        Assert.Empty(host.Findings);
    }

    [Fact]
    public void Resolve_FirstObtainedValueWins_WildcardBlockBeforeConcreteBlockWins()
    {
        var host = Host(
            """
            Host *
                User everyone
                Port 2200
            Host app
                HostName app.internal
                User app-user
                Port 22
            """,
            "app");

        Assert.Equal("everyone", host.User);
        Assert.Equal(2200, host.Port);
        Assert.Equal("app.internal", host.HostName);
    }

    [Fact]
    public void Resolve_FirstObtainedValueWins_WildcardBlockAfterConcreteBlockOnlyFillsGaps()
    {
        var host = Host(
            """
            Host app
                User app-user
            Host app
                User second-user
            Host *
                User everyone
                Port 2200
            """,
            "app");

        Assert.Equal("app-user", host.User);
        Assert.Equal(2200, host.Port);
    }

    [Fact]
    public void LinesBeforeFirstHost_ApplyToEveryHostWithFirstWins()
    {
        var host = Host("User global\nHost a\n  User local\n  HostName a.lan\n", "a");

        Assert.Equal("global", host.User);
        Assert.Equal("a.lan", host.HostName);
    }

    [Fact]
    public void NegatedPattern_ExcludesTheBlock()
    {
        var text =
            """
            Host * !bastion
                User normal
            Host bastion web
                HostName h
            """;

        Assert.Null(Host(text, "bastion").User);
        Assert.Equal("normal", Host(text, "web").User);
    }

    [Fact]
    public void QuestionMarkWildcard_MatchesOneCharacter()
    {
        var text = "Host web?\n  User w\nHost web1 web10\n";

        Assert.Equal("w", Host(text, "web1").User);
        Assert.Null(Host(text, "web10").User);
    }

    [Fact]
    public void WildcardOrNegatedPatterns_AreNeverOfferedAsAliases()
    {
        var result = Import("Host *\n  User u\nHost web-*\nHost !x db\nHost db prod?\n");

        Assert.Equal(["db"], result.Hosts.Select(h => h.Alias));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("22 23")]
    public void InvalidPort_IsClassifiedInvalidAndNotImported(string port)
    {
        var host = Host($"Host a\n  Port {port}\n", "a");

        Assert.Null(host.Port);
        Assert.True(Has(host, "Port", SshConfigFindingKind.Invalid));
    }

    [Fact]
    public void PercentTokens_AreUnsupportedAndNeverExpanded()
    {
        var host = Host("Host a\n  HostName %h.example.com\n  IdentityFile ~/.ssh/%r_key\n", "a");

        Assert.Null(host.HostName);
        Assert.Null(host.IdentityFile);
        Assert.True(Has(host, "HostName", SshConfigFindingKind.Unsupported));
        Assert.True(Has(host, "IdentityFile", SshConfigFindingKind.Unsupported));
    }

    [Theory]
    [InlineData("${HOME}/.ssh/key")]
    [InlineData("~other/.ssh/key")]
    [InlineData("keys/id_rsa")]
    public void IdentityFileThatCannotBeResolvedExactly_IsUnsupported(string value)
    {
        var host = Host($"Host a\n  IdentityFile {value}\n", "a");

        Assert.Null(host.IdentityFile);
        Assert.True(Has(host, "IdentityFile", SshConfigFindingKind.Unsupported));
    }

    [Fact]
    public void MultipleIdentityFiles_AreAmbiguousAndNotAutoPicked()
    {
        var host = Host("Host *\n  IdentityFile ~/.ssh/a\nHost a\n  IdentityFile ~/.ssh/b\n", "a");

        Assert.Null(host.IdentityFile);
        Assert.Equal(["~/.ssh/a", "~/.ssh/b"], host.IdentityFileValues);
        Assert.True(Has(host, "IdentityFile", SshConfigFindingKind.Ambiguous));
        Assert.True(host.IsImportable);
    }

    [Fact]
    public void Include_ProducesFileWarningAndIsNotFollowed()
    {
        var result = Import("Include ~/.ssh/config.d/*\nHost a\n  User u\n");

        Assert.Contains(SshConfigFileWarning.IncludeNotFollowed, result.FileWarnings);
        var host = Assert.Single(result.Hosts);
        Assert.True(Has(host, "Include", SshConfigFindingKind.Unsupported));
        Assert.Equal("u", host.User);
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
    }

    [Fact]
    public void Match_ProducesFileWarningMarksHostsAndIsNotEvaluated()
    {
        var result = Import("Host a\n  HostName a.lan\nMatch host a\n  User from-match\nHost b\n");

        Assert.Contains(SshConfigFileWarning.MatchNotEvaluated, result.FileWarnings);
        Assert.All(result.Hosts, host => Assert.True(Has(host, "Match", SshConfigFindingKind.Unsupported)));
        var a = Assert.Single(result.Hosts, h => h.Alias == "a");
        Assert.Null(a.User);
        Assert.Equal(["a", "b"], result.Hosts.Select(h => h.Alias));
    }

    [Fact]
    public void Resolve_ProxyCommand_IsUnsupportedAndNotImportable()
    {
        var host = Host("Host inner\n  HostName 10.1.0.9\n  ProxyCommand ssh -W %h:%p bastion\n", "inner");

        Assert.False(host.IsImportable);
        Assert.Equal(SshConfigHostBlocker.ProxyCommand, host.Blocker);
        Assert.True(Has(host, "ProxyCommand", SshConfigFindingKind.Unsupported));
        Assert.Null(host.Jump);
    }

    [Fact]
    public void ProxyJumpInheritedFromWildcard_ThatAlsoMatchesTheJump_IsACycle()
    {
        // *.corp also applies to jump.corp itself: ssh would jump through jump.corp to reach jump.corp.
        var host = Host("Host *.corp\n  ProxyJump jump.corp\nHost db.corp\n  User dba\n", "db.corp");

        Assert.False(host.IsImportable);
        Assert.Equal(SshConfigHostBlocker.JumpCycle, host.Blocker);
    }

    [Fact]
    public void ProxyJumpInheritedFromWildcard_IsTheHostsSingleHopJump()
    {
        var host = Host("Host jump.corp\n  ProxyJump none\nHost *.corp\n  ProxyJump jump.corp\nHost db.corp\n  User dba\n", "db.corp");

        Assert.True(host.IsImportable);
        Assert.Equal("db.corp", host.HostName);
        Assert.Equal("jump.corp", host.Jump?.Name);
        Assert.Equal("jump.corp", host.Jump?.HostName);
    }

    [Fact]
    public void ProxyJumpNoneObtainedFirst_KeepsHostImportable()
    {
        var host = Host("Host bastion\n  ProxyJump none\nHost *\n  ProxyJump bastion\n", "bastion");

        Assert.True(host.IsImportable);
        Assert.Equal(SshConfigHostBlocker.None, host.Blocker);
    }

    [Fact]
    public void HostAffectingKeywords_AreUnsupportedAndUnrelatedOnesIgnored()
    {
        var host = Host(
            "Host a\n  CanonicalizeHostname yes\n  HostKeyAlias other\n  ServerAliveInterval 30\n  ForwardAgent yes\n",
            "a");

        Assert.True(Has(host, "CanonicalizeHostname", SshConfigFindingKind.Unsupported));
        Assert.True(Has(host, "HostKeyAlias", SshConfigFindingKind.Unsupported));
        Assert.True(Has(host, "ServerAliveInterval", SshConfigFindingKind.Ignored));
        Assert.True(Has(host, "ForwardAgent", SshConfigFindingKind.Ignored));
        Assert.True(host.IsImportable);
    }

    [Fact]
    public void UnterminatedQuote_IsInvalidNotACrash()
    {
        var host = Host("Host a\n  User \"broken\n  HostName a.lan\n", "a");

        Assert.Null(host.User);
        Assert.True(Has(host, "User", SshConfigFindingKind.Invalid));
        Assert.Equal("a.lan", host.HostName);
    }

    // ---- Vigil M14.4a H1/M1: any applicable proxy line, valid or not, decides the slot; only an exact none is "no proxy".

    [Theory]
    [InlineData("ProxyJump bastion extra")]
    [InlineData("ProxyJump \"bastion")]
    [InlineData("ProxyJump=")]
    [InlineData("ProxyJump")]
    [InlineData("ProxyJump none extra")]
    [InlineData("ProxyJump \"none")]
    public void Resolve_MalformedProxyJump_IsNotImportable(string line)
    {
        var host = Host($"Host a\n  HostName a.lan\n  {line}\n", "a");

        Assert.False(host.IsImportable);
        Assert.Equal(SshConfigHostBlocker.JumpUnparsable, host.Blocker);
        Assert.Null(host.Jump);
    }

    [Theory]
    [InlineData("ProxyCommand sh -c 'nc %h %p")]
    [InlineData("ProxyCommand #x")]
    [InlineData("ProxyCommand none extra")]
    [InlineData("ProxyCommand")]
    [InlineData("ProxyCommand=")]
    [InlineData("ProxyCommand \"none\"")]
    public void Resolve_ProxyCommandIsReadRaw_AnythingButExactNoneIsNotImportable(string line)
    {
        var host = Host($"Host a\n  HostName a.lan\n  {line}\n", "a");

        Assert.False(host.IsImportable);
        Assert.Equal(SshConfigHostBlocker.ProxyCommand, host.Blocker);
    }

    [Theory]
    [InlineData("ProxyCommand none")]
    [InlineData("ProxyCommand=none")]
    [InlineData("ProxyJump none")]
    [InlineData("ProxyJump=none")]
    public void Resolve_ExactNone_DecidesNoProxy(string line)
    {
        var host = Host($"Host a\n  {line}\nHost *\n  ProxyJump bastion\n", "a");

        Assert.True(host.IsImportable);
    }

    [Fact]
    public void Resolve_MalformedProxyJumpInWildcardBlock_BlocksEveryMatchingHost()
    {
        var result = Import("Host *\n  ProxyJump a b\nHost x\nHost y\n");

        Assert.All(result.Hosts, host => Assert.Equal(SshConfigHostBlocker.JumpUnparsable, host.Blocker));
    }

    // ---- Vigil M14.4a H2: an unevaluated Match that may set a proxy, met before the proxy is decided.

    [Theory]
    [InlineData("Match all\n  ProxyJump jump\nHost a\n  HostName a.lan\n")]
    [InlineData("Host a\n  HostName a.lan\nMatch all\n  ProxyJump jump\n")]
    [InlineData("Match host a\n  ProxyCommand nc jump 22\nHost a\n")]
    [InlineData("Match all\n  ProxyJump a b\nHost a\n")]
    [InlineData("Match all\n  Include other\nHost a\n")]
    public void Resolve_MatchThatMaySetProxyBeforeDecision_IsNotImportable(string text)
    {
        var host = Host(text, "a");

        Assert.False(host.IsImportable);
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByMatch, host.Blocker);
    }

    [Theory]
    [InlineData("Host a\n  ProxyJump none\nMatch all\n  ProxyJump jump\n")]
    [InlineData("Match all\n  ProxyJump none\nHost a\n")]
    [InlineData("Match all\n  User m\nHost a\n")]
    public void Resolve_MatchAfterDecisionOrWithoutProxy_StaysImportable(string text)
    {
        var host = Host(text, "a");

        Assert.True(host.IsImportable);
        Assert.True(Has(host, "Match", SshConfigFindingKind.Unsupported));
    }

    // ---- Vigil M14.4a H3: an applicable, unfollowed Include before the proxy is decided.

    [Theory]
    [InlineData("Include conf.d/*\nHost a\n")]
    [InlineData("Host a\n  HostName a.lan\n  Include conf.d/a\n")]
    [InlineData("Host *\n  Include conf.d/*\nHost a\n")]
    public void Resolve_ApplicableIncludeBeforeProxyDecision_IsNotImportable(string text)
    {
        var host = Host(text, "a");

        Assert.False(host.IsImportable);
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, host.Blocker);
    }

    [Theory]
    [InlineData("Host a\n  ProxyJump none\n  Include conf.d/*\n")]
    [InlineData("ProxyJump none\nInclude conf.d/*\nHost a\n")]
    [InlineData("Host b\n  Include conf.d/b\nHost a\n")]
    public void Resolve_IncludeAfterNoneOrNotApplicable_StaysImportable(string text)
    {
        var result = Import(text);
        var host = Assert.Single(result.Hosts, h => h.Alias == "a");

        Assert.True(host.IsImportable);
        Assert.Contains(SshConfigFileWarning.IncludeNotFollowed, result.FileWarnings);
    }

    // ---- Vigil M14.4a L1: a malformed Host/Match line makes ssh reject the file.

    [Theory]
    [InlineData("Host\n  User u\n")]
    [InlineData("Host \"broken\n")]
    [InlineData("Host a\nMatch\n")]
    [InlineData("Host a\nMatch \"all\n")]
    public void MalformedHostOrMatchLine_IsAClassifiedFileError(string text)
    {
        var result = Import(text);

        Assert.Equal(SshConfigImportStatus.Error, result.Status);
        Assert.Equal(SshConfigImportErrorCode.InvalidSyntax, result.ErrorCode);
        Assert.Empty(result.Hosts);
    }

    // ---- Vigil M14.4a N1: quoted keywords are real keywords to ssh; fail the whole file closed.

    [Theory]
    [InlineData("Host a\n  \"ProxyJump\" bastion\n")]
    [InlineData("Host a\n  \"Include\" x\n")]
    [InlineData("Host a\n\"Host\" other\n")]
    [InlineData("Host a\n\"Match\" all\n")]
    [InlineData("Host a\n  'ProxyCommand' nc jump 22\n")]
    [InlineData("Host a\n  \"User\" root\n")]
    public void QuotedKeyword_FailsTheWholeFileClosed(string text)
    {
        var result = Import(text);

        Assert.Equal(SshConfigImportStatus.Error, result.Status);
        Assert.Equal(SshConfigImportErrorCode.InvalidSyntax, result.ErrorCode);
        Assert.Empty(result.Hosts);
    }

    [Theory]
    [InlineData("Host a\n  Proxy\"Jump\" bastion\n")]
    [InlineData("Host a\n  Proxy'Command' nc jump 22\n")]
    [InlineData("Host a\n  Inc\"lude\" x\n")]
    [InlineData("Host a\nHo\"st\" other\n")]
    [InlineData("Host a\n  User\"\" root\n")]
    public void KeywordWithAQuoteInAnyPosition_FailsTheWholeFileClosed(string text)
    {
        var result = Import(text);

        Assert.Equal(SshConfigImportStatus.Error, result.Status);
        Assert.Equal(SshConfigImportErrorCode.InvalidSyntax, result.ErrorCode);
        Assert.Empty(result.Hosts);
    }

    [Fact]
    public void QuotedArguments_AreStillAccepted()
    {
        var host = Host("Host a\n  User \"u\"\n  ProxyJump \"none\"\nHost *\n  ProxyJump bastion\n", "a");

        Assert.Equal("u", host.User);
        Assert.True(host.IsImportable);
    }

    [Fact]
    public void QuotedHostKeyword_StructuralCase_RejectsTheFileAndImportsNoHost()
    {
        // To ssh, '"Host" other' starts a new block, so "web" would get ProxyJump bastion.
        var result = Import(
            """
            Host web
            "Host" other
              ProxyJump none
            Host *
              ProxyJump bastion
            """);

        Assert.Equal(SshConfigImportErrorCode.InvalidSyntax, result.ErrorCode);
        Assert.DoesNotContain(result.Hosts, host => host.IsImportable);
    }

    // ---- Vigil M14.4a M2: bounded output.

    [Fact]
    public void ManyAliases_AreCappedWithATruncationWarning_AndResolveQuickly()
    {
        var text = new System.Text.StringBuilder("Host *\n  ServerAliveInterval 30\n");
        for (var i = 0; i < 33_000; i++)
        {
            text.Append("Host h").Append(i).Append("\n  HostName 10.0.0.1\n");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = Import(text.ToString());
        stopwatch.Stop();

        Assert.Equal(SshConfigResolver.MaxHosts, result.Hosts.Count);
        Assert.Contains(SshConfigFileWarning.HostsTruncated, result.FileWarnings);
        Assert.Equal("h0", result.Hosts[0].Alias);
        Assert.Equal("h499", result.Hosts[^1].Alias);
        Assert.All(result.Hosts, host => Assert.Equal("10.0.0.1", host.HostName));
        // Generous bound: the indexed plan is linear; the old per-alias rescan was quadratic.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void FindingsPerHost_AreDedupedAndCapped()
    {
        var text = new System.Text.StringBuilder("Host a\n");
        for (var i = 0; i < 100; i++)
        {
            text.Append("  Unrelated").Append(i).Append(" yes\n  Unrelated").Append(i).Append(" no\n");
        }

        var host = Host(text.ToString(), "a");

        Assert.Equal(SshConfigResolver.MaxFindingsPerHost, host.Findings.Count);
        Assert.True(host.FindingsTruncated);
        Assert.Equal(host.Findings.Count, host.Findings.Select(f => (f.Keyword, f.Kind)).Distinct().Count());
    }

    [Fact]
    public void CanonicalizeAndHostKeyAlias_StayVisibleWarningsOnAnImportableHost()
    {
        var host = Host("Host a\n  CanonicalizeHostname always\n  HostKeyAlias other\n", "a");

        Assert.True(host.IsImportable);
        Assert.True(Has(host, "CanonicalizeHostname", SshConfigFindingKind.Unsupported));
        Assert.True(Has(host, "HostKeyAlias", SshConfigFindingKind.Unsupported));
    }
}
