using System.Diagnostics;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.SshConfig;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.Infrastructure.Tests.SshConfig;

/// <summary>
/// Runs tools/qa/ssh-config-fixtures.ps1 into a temp root and loads every generated profile through
/// the real read-only import source, so the QA fixtures provably show what they claim.
/// </summary>
public sealed class SshConfigQaFixturesScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sm-sshqa-" + Guid.NewGuid().ToString("N"));
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "sm-sshqa-bin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _bin })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A stand-in for the Debug executable (never launched), with a chosen build time.</summary>
    private string FakeAppExe(DateTime writeTimeUtc)
    {
        Directory.CreateDirectory(_bin);
        var exe = Path.Combine(_bin, "ServerMonitor.App.exe");
        File.WriteAllText(exe, "not a real executable");
        File.SetLastWriteTimeUtc(exe, writeTimeUtc);
        return exe;
    }

    private static string ScriptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "tools", "qa", "ssh-config-fixtures.ps1");
    }

    private static (int ExitCode, string Output) RunScript(string root, string? appExe = null)
    {
        var shell = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"))
            ? "pwsh.exe"
            : "powershell.exe";
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(), "-Root", root })
        {
            start.ArgumentList.Add(argument);
        }

        if (appExe is not null)
        {
            start.ArgumentList.Add("-AppExe");
            start.ArgumentList.Add(appExe);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "fixture script timed out");
        return (process.ExitCode, output.Result + error.Result);
    }

    private sealed class SpyOpener
    {
        public List<string> Paths { get; } = [];

        public Stream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            lock (Paths)
            {
                Paths.Add(path);
            }

            return new FileStream(path, mode, access, share);
        }
    }

    private async Task<(SshConfigImportResult Result, SpyOpener Spy)> LoadAsync(string scenario)
    {
        var spy = new SpyOpener();
        var result = await new SshConfigFileImportSource(Path.Combine(_root, scenario), spy.Open).LoadAsync();
        return (result, spy);
    }

    [Fact]
    public async Task FixtureScript_GeneratesTheSixProfiles_AsDocumented()
    {
        var exe = FakeAppExe(DateTime.UtcNow);
        var (exitCode, output) = RunScript(_root, exe);
        Assert.True(exitCode == 0, output);
        foreach (var scenario in new[] { "empty", "error", "big", "blocked", "normal", "proxyjump", "keys", "keys-rsa", "keys-edge" })
        {
            // The exact executable, never `dotnet run` (which may launch a different, stale binary), and always
            // ISOLATED (M14.5 D-2): its own data directory, in-memory secrets, no monitoring.
            var isolated = $"& \"{exe}\" --qa-proxyjump --qa-proxyjump-dir=\"{Path.Combine(_root, scenario)}-data\" --qa-ssh-config \"{Path.Combine(_root, scenario)}\"";
            Assert.Matches(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(isolated) + @"\r?$", System.Text.RegularExpressions.RegexOptions.Multiline), output);
            foreach (var language in new[] { "en-US", "pt-BR" })
            {
                Assert.Contains($"{isolated} --qa-ui-language {language}", output);
            }
        }

        Assert.DoesNotContain("dotnet run", output);

        // No launch line runs the real composition: every one that names --qa-ssh-config is isolated.
        var launchLines = output.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(line => line.Contains("--qa-ssh-config", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(9 * 3, launchLines.Count);
        Assert.All(launchLines, line =>
        {
            Assert.Contains("--qa-proxyjump --qa-proxyjump-dir=\"", line);
            Assert.Contains("-data\" --qa-ssh-config", line);
        });

        // (a) empty
        Assert.Equal(SshConfigImportStatus.NotFound, (await LoadAsync("empty")).Result.Status);

        // (b) error
        var error = (await LoadAsync("error")).Result;
        Assert.Equal(SshConfigImportStatus.Error, error.Status);
        Assert.Equal(SshConfigImportErrorCode.InvalidSyntax, error.ErrorCode);

        // (c) big: capped, with every kind of row
        var big = (await LoadAsync("big")).Result;
        Assert.Equal(SshConfigImportStatus.Loaded, big.Status);
        Assert.Equal(SshConfigResolver.MaxHosts, big.Hosts.Count);
        Assert.Contains(SshConfigFileWarning.HostsTruncated, big.FileWarnings);
        Assert.Contains(big.Hosts, host => host.IsImportable && host.IdentityFile is not null);
        Assert.Contains(big.Hosts, host => host.IsImportable && host.Jump?.HostName == "qa-bastion");
        Assert.Contains(big.Hosts, host => host.Findings.Any(f => f.Kind == SshConfigFindingKind.Ambiguous));
        Assert.Contains(big.Hosts, host => host.Findings.Any(f => f.Kind == SshConfigFindingKind.Unsupported && f.Keyword == "HostKeyAlias"));
        Assert.All(big.Hosts, host => Assert.Contains(host.Findings, f => f.Kind == SshConfigFindingKind.Ignored));

        // (d) blocked: one reason per host, contrast importable, outside file never opened
        var (blocked, blockedSpy) = await LoadAsync("blocked");
        SshConfigHostBlocker BlockerOf(string alias) => Assert.Single(blocked.Hosts, host => host.Alias == alias).Blocker;
        Assert.Equal(SshConfigHostBlocker.None, BlockerOf("ok-contrast"));
        Assert.Equal(SshConfigHostBlocker.JumpMultiHop, BlockerOf("via-multihop"));
        Assert.Equal(SshConfigHostBlocker.ProxyCommand, BlockerOf("via-proxycommand"));
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, BlockerOf("via-include"));
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByMatch, BlockerOf("via-match"));
        Assert.Equal(SshConfigHostBlocker.CanonicalizationMayChangeRoute, BlockerOf("via-canonicalize"));
        Assert.Contains(new SshConfigDiagnostic(SshConfigDiagnosticKind.IncludeMatchedNoFiles, "conf.d/*.missing"), blocked.Diagnostics);
        Assert.DoesNotContain(blockedSpy.Paths, path => path.EndsWith("outside.conf", StringComparison.OrdinalIgnoreCase));

        // (e) normal: 5 importable hosts, key paths that do not exist and are never opened
        var (normal, normalSpy) = await LoadAsync("normal");
        Assert.Equal(["lab-a", "lab-b", "web", "db", "nas"], normal.Hosts.Select(host => host.Alias));
        Assert.All(normal.Hosts, host =>
        {
            Assert.True(host.IsImportable);
            Assert.NotNull(host.HostName);
            Assert.NotNull(host.User);
            Assert.NotNull(host.Port);
            Assert.NotNull(host.IdentityFile);
            Assert.StartsWith(Path.Combine(_root, "normal", ".ssh"), host.IdentityFile);
            Assert.False(File.Exists(host.IdentityFile));
        });
        Assert.All(normalSpy.Paths, path => Assert.DoesNotContain("qa_missing", path));

        // (f) proxyjump: via-* importable with the jump resolved; every blocked shape has its own reason
        var (proxyJump, proxyJumpSpy) = await LoadAsync("proxyjump");
        SshConfigHostEntry HostOf(string alias) => Assert.Single(proxyJump.Hosts, host => host.Alias == alias);
        SshConfigJumpHost JumpOf(string alias)
        {
            var host = HostOf(alias);
            Assert.True(host.IsImportable, $"{alias}: {host.Blocker}");
            return Assert.IsType<SshConfigJumpHost>(host.Jump);
        }

        var viaAlias = JumpOf("via-alias");
        Assert.Equal(("10.50.0.1", "jumpuser", (int?)2222), (viaAlias.HostName, viaAlias.User, viaAlias.Port));
        Assert.Equal(Path.Combine(_root, "proxyjump", ".ssh", "qa_missing_bastion"), viaAlias.IdentityFile);
        Assert.False(File.Exists(viaAlias.IdentityFile));
        var viaExplicit = JumpOf("via-explicit");
        Assert.Equal(("10.50.0.1", "ops", (int?)2200), (viaExplicit.HostName, viaExplicit.User, viaExplicit.Port));
        Assert.Equal("jump.qa.internal", JumpOf("via-literal").HostName);
        var viaIpv6 = JumpOf("via-ipv6");
        Assert.Equal(("fd00::10", "admin", (int?)2022), (viaIpv6.HostName, viaIpv6.User, viaIpv6.Port));
        var viaInclude = JumpOf("via-include-jump");
        Assert.Equal(("10.50.0.9", "incuser", (int?)2209), (viaInclude.HostName, viaInclude.User, viaInclude.Port));
        Assert.Equal("10.50.0.1", JumpOf("chained-bastion").HostName);
        Assert.True(HostOf("bastion").IsImportable);
        Assert.Null(HostOf("bastion").Jump);
        Assert.Equal(SshConfigHostBlocker.JumpMultiHop, HostOf("blocked-multihop").Blocker);
        Assert.Equal(SshConfigHostBlocker.JumpMultiHop, HostOf("blocked-chained").Blocker);
        Assert.Equal(SshConfigHostBlocker.ProxyCommand, HostOf("blocked-proxycommand-jump").Blocker);
        Assert.Equal(SshConfigHostBlocker.ProxyCommand, HostOf("pc-bastion").Blocker);
        Assert.Equal(SshConfigHostBlocker.JumpUnparsable, HostOf("blocked-uri").Blocker);
        Assert.Equal(SshConfigHostBlocker.JumpUnparsable, HostOf("blocked-token").Blocker);
        Assert.Equal(SshConfigHostBlocker.JumpHostNameUnresolved, HostOf("blocked-hostname-token").Blocker);
        Assert.Equal(SshConfigHostBlocker.JumpCycle, HostOf("cycle-self").Blocker);
        Assert.Equal(SshConfigHostBlocker.JumpCycle, HostOf("cycle-a").Blocker);
        Assert.Equal(SshConfigHostBlocker.JumpCycle, HostOf("cycle-b").Blocker);
        Assert.All(proxyJump.Hosts.Where(host => !host.IsImportable), host => Assert.Null(host.Jump));
        Assert.All(proxyJumpSpy.Paths, path => Assert.DoesNotContain("qa_missing", path));
    }

    [Fact]
    public async Task FixtureScript_KeyScenarios_DriveTheRealKeyDiscovery_WithDummyBytesOnly()
    {
        var (exitCode, output) = RunScript(_root, FakeAppExe(DateTime.UtcNow.AddMinutes(5)));
        Assert.True(exitCode == 0, output);

        // keys: the three defaults, ed25519 recommended; every excluded name exists but is never listed.
        var keysSsh = Path.Combine(_root, "keys", ".ssh");
        var keys = await new LocalSshKeyDiscovery(Path.Combine(_root, "keys")).DiscoverAsync();
        Assert.Equal(["id_ed25519", "id_ecdsa", "id_rsa"], keys.Select(key => key.FileName));
        Assert.Equal([true, false, false], keys.Select(key => key.IsRecommended));
        Assert.All(keys, key => Assert.Equal(keysSsh, Path.GetDirectoryName(key.Path)));
        foreach (var excluded in new[] { "id_dsa", "id_ed25519_sk", "id_ecdsa_sk", "id_ed25519.pub", "my_server_key" })
        {
            Assert.True(File.Exists(Path.Combine(keysSsh, excluded)), excluded);
        }

        // Every "key" file is the dummy marker, never key material.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, "keys"), "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(path) != "config"))
        {
            var text = File.ReadAllText(file);
            Assert.StartsWith("QA FIXTURE - NOT A PRIVATE KEY", text);
            Assert.DoesNotContain("PRIVATE KEY-----", text);
        }

        // keys: "keyed" imports its own IdentityFile (a discovered default); "plain" has none.
        var (keysConfig, _) = await LoadAsync("keys");
        var keyed = Assert.Single(keysConfig.Hosts, host => host.Alias == "keyed");
        Assert.Equal(Path.Combine(keysSsh, "id_rsa"), keyed.IdentityFile);
        Assert.Null(Assert.Single(keysConfig.Hosts, host => host.Alias == "plain").IdentityFile);

        // keys-rsa: one key, recommended.
        var rsa = Assert.Single(await new LocalSshKeyDiscovery(Path.Combine(_root, "keys-rsa")).DiscoverAsync());
        Assert.Equal(("id_rsa", LocalSshKeyKind.Rsa, true), (rsa.FileName, rsa.Kind, rsa.IsRecommended));

        // keys-edge: oversize, empty and a directory - nothing offered.
        var edge = Path.Combine(_root, "keys-edge", ".ssh");
        Assert.Equal(LocalSshKeyDiscovery.MaxKeyFileBytes + 1, new FileInfo(Path.Combine(edge, "id_ed25519")).Length);
        Assert.Equal(0, new FileInfo(Path.Combine(edge, "id_ecdsa")).Length);
        Assert.True(Directory.Exists(Path.Combine(edge, "id_rsa")));
        Assert.Empty(await new LocalSshKeyDiscovery(Path.Combine(_root, "keys-edge")).DiscoverAsync());

        // The other scenarios have no key files at all (today's "no key found" behaviour).
        Assert.Empty(await new LocalSshKeyDiscovery(Path.Combine(_root, "normal")).DiscoverAsync());
    }

    [Fact]
    public void FixtureScript_MissingExecutable_WarnsAndPrintsNoLaunchCommand()
    {
        var (exitCode, output) = RunScript(_root, Path.Combine(_bin, "missing", "ServerMonitor.App.exe"));

        Assert.True(exitCode == 0, output);
        Assert.Contains("no launch command printed", output);
        Assert.DoesNotContain("--qa-ssh-config", output);
        Assert.DoesNotContain("--qa-ui-language", output);
        Assert.True(File.Exists(Path.Combine(_root, "normal", ".ssh", "config")), "fixtures are still written");
    }

    [Fact]
    public void FixtureScript_ExecutableOlderThanHead_WarnsAndPrintsNoLaunchCommand()
    {
        var (exitCode, output) = RunScript(_root, FakeAppExe(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.True(exitCode == 0, output);
        Assert.Contains("is older than HEAD", output);
        Assert.DoesNotContain("--qa-ssh-config", output);
    }

    [Fact]
    public void FixtureScript_RefusesANonEmptyRoot_AndWritesNothing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "keep.txt"), "x");

        var (exitCode, _) = RunScript(_root);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName));
    }
}
