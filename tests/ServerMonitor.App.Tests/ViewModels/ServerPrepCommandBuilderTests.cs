using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.5 B4 — the helper's commands are pasted into PowerShell by the user, and the user/host/port come from
/// the form (the host possibly from an mDNS announcement). A value is substituted only when it matches the
/// strict grammar; anything else must come out as the placeholder, with no trace of the hostile text.
/// </summary>
public sealed class ServerPrepCommandBuilderTests
{
    private const string UserPlaceholder = "<user>";
    private const string HostPlaceholder = "<server>";
    private const string JumpPlaceholder = "<jump-host>";
    private const string RemoteScript = "\"umask 077; mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys\"";

    private static ServerPrepCommands Build(
        string? user = "deploy",
        string? host = "web-01.example.com",
        string? port = "22",
        string? key = null) =>
        ServerPrepCommandBuilder.Build(user, host, port, key, UserPlaceholder, HostPlaceholder);

    [Fact]
    public void SafeValues_AreSubstitutedIntoTheExactCommand()
    {
        var commands = Build();

        Assert.Equal("ssh-keygen -t ed25519", commands.GenerateKey);
        Assert.Equal(
            $"type $env:USERPROFILE\\.ssh\\id_ed25519.pub | ssh deploy@web-01.example.com {RemoteScript}",
            commands.CopyPublicKey);
        Assert.False(commands.UsesPlaceholders);
    }

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("server", "server")]
    [InlineData("[fe80::1]", "fe80::1")]
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("[fe80::42%7]", "fe80::42%7")]
    [InlineData("2001:db8::10", "2001:db8::10")]
    public void SafeHostForms_AreAccepted(string host, string expected)
    {
        var commands = Build(host: host);

        Assert.Contains($" ssh deploy@{expected} ", commands.CopyPublicKey);
        Assert.False(commands.UsesPlaceholders);
    }

    [Theory]
    [InlineData("2222", " ssh -p 2222 deploy@")]
    [InlineData("22", " ssh deploy@")]
    [InlineData("", " ssh deploy@")]
    [InlineData("0", " ssh deploy@")]
    [InlineData("65536", " ssh deploy@")]
    [InlineData("22; rm -rf ~", " ssh deploy@")]
    [InlineData("２２２２", " ssh deploy@")]
    [InlineData("-22", " ssh deploy@")]
    public void Port_IsIncludedOnlyWhenNumericInRangeAndNotTheDefault(string port, string expected)
    {
        var commands = Build(port: port);

        Assert.Contains(expected, commands.CopyPublicKey);
        Assert.DoesNotContain("rm", commands.CopyPublicKey);
    }

    public static TheoryData<string> HostileValues => new()
    {
        "",
        " ",
        "a b",
        "a;b",
        "a;rm -rf ~",
        "a\"b",
        "a'b",
        "a`b",
        "$(whoami)",
        "a$(id)",
        "${HOME}",
        "$env:USERNAME",
        "a|b",
        "a&b",
        "a&&b",
        "a>b",
        "a<b",
        "a\nb",
        "a\r\nb",
        "a\tb",
        "a\\b",
        "a/b",
        "a,b",
        "a(b)",
        "a{b}",
        "a#b",
        "a!b",
        "a*b",
        "a?b",
        "a=b",
        "a@b",
        "-oProxyCommand=calc",
        "--help",
        "ｅｘａｍｐｌｅ",
        "exämple",
        "пример",
        "a​b",
        "a‮b",
        "a b",
        new string('a', 300),
    };

    [Theory]
    [MemberData(nameof(HostileValues))]
    public void HostileUser_BecomesThePlaceholderAndLeavesNoTrace(string hostile)
    {
        var commands = Build(user: hostile);

        Assert.Equal(
            $"type $env:USERPROFILE\\.ssh\\id_ed25519.pub | ssh {UserPlaceholder}@web-01.example.com {RemoteScript}",
            commands.CopyPublicKey);
        Assert.True(commands.UsesPlaceholders);
    }

    [Theory]
    [MemberData(nameof(HostileValues))]
    public void HostileHost_BecomesThePlaceholderAndLeavesNoTrace(string hostile)
    {
        var commands = Build(host: hostile);

        Assert.Equal(
            $"type $env:USERPROFILE\\.ssh\\id_ed25519.pub | ssh deploy@{HostPlaceholder} {RemoteScript}",
            commands.CopyPublicKey);
        Assert.True(commands.UsesPlaceholders);
    }

    // Kept out of the theory data: a NUL or escape character in a test-case name upsets some result loggers.
    [Fact]
    public void ControlCharacters_AreRefusedInUserAndHost()
    {
        foreach (var control in new[] { '\0', '\u0001', '\u001B', '\u007F', '\u0085' })
        {
            var hostile = $"a{control}b";

            Assert.Contains($" ssh {UserPlaceholder}@", Build(user: hostile).CopyPublicKey);
            Assert.Contains($"@{HostPlaceholder} ", Build(host: hostile).CopyPublicKey);
            Assert.DoesNotContain(control, Build(user: hostile, host: hostile).CopyPublicKey);
        }
    }

    [Theory]
    [InlineData("-leading")]
    [InlineData(".leading")]
    [InlineData("under_score")]
    [InlineData("[fe80::1")]
    [InlineData("fe80::1]")]
    [InlineData("[]")]
    [InlineData("[not-ipv6]")]
    [InlineData("[fe80::1%eth0;id]")]
    [InlineData("[fe80::1%]")]
    [InlineData("fe80::zz")]
    [InlineData("1:2")]
    [InlineData("host:22")]
    public void HostOutsideTheGrammar_IsRefused(string host)
    {
        var commands = Build(host: host);

        Assert.Contains($"deploy@{HostPlaceholder} ", commands.CopyPublicKey);
        Assert.True(commands.UsesPlaceholders);
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("Deploy_01")]
    [InlineData("svc.monitor")]
    [InlineData("a-b")]
    [InlineData("_svc")]
    public void SafeUserForms_AreAccepted(string user)
    {
        Assert.Contains($" ssh {user}@web-01.example.com ", Build(user: user).CopyPublicKey);
    }

    [Fact]
    public void NullValues_BecomePlaceholders()
    {
        var commands = Build(user: null, host: null, port: null);

        Assert.Equal(
            $"type $env:USERPROFILE\\.ssh\\id_ed25519.pub | ssh {UserPlaceholder}@{HostPlaceholder} {RemoteScript}",
            commands.CopyPublicKey);
        Assert.True(commands.UsesPlaceholders);
    }

    [Theory]
    [InlineData("id_ed25519", "id_ed25519.pub")]
    [InlineData("id_ecdsa", "id_ecdsa.pub")]
    [InlineData("id_rsa", "id_rsa.pub")]
    [InlineData(null, "id_ed25519.pub")]
    [InlineData("", "id_ed25519.pub")]
    [InlineData("ID_RSA", "id_ed25519.pub")]
    [InlineData("id_dsa", "id_ed25519.pub")]
    [InlineData("work_key", "id_ed25519.pub")]
    [InlineData("id_rsa.pub; calc", "id_ed25519.pub")]
    [InlineData("..\\..\\secret", "id_ed25519.pub")]
    [InlineData("id_rsa\" | calc \"", "id_ed25519.pub")]
    public void PublicKeyFile_IsNamedOnlyForAnExactDefaultKey(string? selected, string expected)
    {
        var commands = Build(key: selected);

        Assert.StartsWith($"type $env:USERPROFILE\\.ssh\\{expected} | ssh ", commands.CopyPublicKey);
        Assert.DoesNotContain("calc", commands.CopyPublicKey);
        Assert.DoesNotContain("secret", commands.CopyPublicKey);
    }

    [Fact]
    public void JumpHost_IsPassedWithDashJWhenEveryPartIsSafe()
    {
        var commands = ServerPrepCommandBuilder.Build(
            "deploy", "10.0.0.5", "2222", null, UserPlaceholder, HostPlaceholder,
            useJumpHost: true, jumpUsername: "jumper", jumpHost: "bastion.example.test", jumpPort: "2200",
            jumpPlaceholder: JumpPlaceholder);

        Assert.Equal(
            $"type $env:USERPROFILE\\.ssh\\id_ed25519.pub | ssh -J jumper@bastion.example.test:2200 -p 2222 deploy@10.0.0.5 {RemoteScript}",
            commands.CopyPublicKey);
        Assert.False(commands.UsesPlaceholders);
    }

    [Theory]
    [InlineData("jumper;id", "bastion", "22")]
    [InlineData("jumper", "bastion$(id)", "22")]
    [InlineData("jumper", "bastion", "22 -oProxyCommand=calc")]
    [InlineData("-jumper", "bastion", "22")]
    [InlineData("jumper", "[fe80::1]", "22")]
    [InlineData("", "", "")]
    public void UnsafeJumpHost_BecomesThePlaceholder(string jumpUser, string jumpHost, string jumpPort)
    {
        var commands = ServerPrepCommandBuilder.Build(
            "deploy", "10.0.0.5", "22", null, UserPlaceholder, HostPlaceholder,
            useJumpHost: true, jumpUsername: jumpUser, jumpHost: jumpHost, jumpPort: jumpPort,
            jumpPlaceholder: JumpPlaceholder);

        Assert.Equal(
            $"type $env:USERPROFILE\\.ssh\\id_ed25519.pub | ssh -J {JumpPlaceholder} deploy@10.0.0.5 {RemoteScript}",
            commands.CopyPublicKey);
        Assert.True(commands.UsesPlaceholders);
    }

    [Fact]
    public void NoCommandEverAsksForElevation()
    {
        var commands = Build();

        foreach (var command in new[] { commands.GenerateKey, commands.CopyPublicKey })
        {
            Assert.DoesNotContain("sudo", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("root", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("chmod", command, StringComparison.OrdinalIgnoreCase);
        }
    }
}
