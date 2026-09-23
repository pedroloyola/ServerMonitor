using System.Diagnostics;
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

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
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

    private static (int ExitCode, string Output) RunScript(string root)
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
    public async Task FixtureScript_GeneratesTheFiveProfiles_AsDocumented()
    {
        var (exitCode, output) = RunScript(_root);
        Assert.True(exitCode == 0, output);
        foreach (var scenario in new[] { "empty", "error", "big", "blocked", "normal" })
        {
            Assert.Contains($"--qa-ssh-config \"{Path.Combine(_root, scenario)}\"", output);
        }

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
        Assert.Contains(big.Hosts, host => host.Blocker == SshConfigHostBlocker.ProxyJump);
        Assert.Contains(big.Hosts, host => host.Findings.Any(f => f.Kind == SshConfigFindingKind.Ambiguous));
        Assert.Contains(big.Hosts, host => host.Findings.Any(f => f.Kind == SshConfigFindingKind.Unsupported && f.Keyword == "HostKeyAlias"));
        Assert.All(big.Hosts, host => Assert.Contains(host.Findings, f => f.Kind == SshConfigFindingKind.Ignored));

        // (d) blocked: one reason per host, contrast importable, outside file never opened
        var (blocked, blockedSpy) = await LoadAsync("blocked");
        SshConfigHostBlocker BlockerOf(string alias) => Assert.Single(blocked.Hosts, host => host.Alias == alias).Blocker;
        Assert.Equal(SshConfigHostBlocker.None, BlockerOf("ok-contrast"));
        Assert.Equal(SshConfigHostBlocker.ProxyJump, BlockerOf("via-proxyjump"));
        Assert.Equal(SshConfigHostBlocker.ProxyCommand, BlockerOf("via-proxycommand"));
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByInclude, BlockerOf("via-include"));
        Assert.Equal(SshConfigHostBlocker.ProxyMaySetByMatch, BlockerOf("via-match"));
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
