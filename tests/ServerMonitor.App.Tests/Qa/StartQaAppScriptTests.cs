using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.3 rule (after the gate 1A incident): nothing launches ServerMonitor.App.exe except tools/qa/Start-QaApp.ps1, and that
/// script refuses BEFORE Start-Process unless the arguments select an isolated mode. Proven WITHOUT launching the app:
/// the lists are compared with <see cref="QaStartupIsolation"/>, the other scripts are scanned, and the script's decision
/// runs in a pwsh process against a SENTINEL executable path that does not exist - so even a broken refusal could not
/// start anything (it would only reach "executable not found").
/// </summary>
public sealed partial class StartQaAppScriptTests
{
    private static readonly string Tools = Path.GetFullPath(Path.Combine(AppSourceTree.AppRoot, "..", "..", "tools"));

    private static readonly string Script = Path.Combine(Tools, "qa", "Start-QaApp.ps1");

    private const string Tfm = "net10.0-windows10.0.19041.0";

    /// <summary>A fake worktree under this assembly's QA root: only a ServerMonitor.slnx marker, nothing runnable.</summary>
    private static readonly string FakeWorktree = CreateWorktree("worktree");

    /// <summary>Never created: the Debug executable path of the fake worktree.</summary>
    private static readonly string SentinelExe = ExeIn(FakeWorktree, "Debug");

    private static string CreateWorktree(string name)
    {
        var root = Path.Combine(QaTestRoots.Root, $"start-qaapp-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ServerMonitor.slnx"), "<Solution />");
        return root;
    }

    private static string ExeIn(string worktree, string configuration) =>
        Path.Combine(worktree, "src", "ServerMonitor.App", "bin", "x64", configuration, Tfm, "win-x64", "ServerMonitor.App.exe");

    // ---- M-1A-R1 (Vigil): the executable is judged on its canonical, OS-resolved path ----

    public static TheoryData<string, string> RefusedExecutables => new()
    {
        // E3: a Debug\..\ traversal into Release.
        { ExeIn(FakeWorktree, "Debug").Replace(@"\Debug\", @"\Debug\..\Release\", StringComparison.Ordinal), "'.' or '..' segment" },
        // E4: a Debug\..\ traversal into the installed app.
        { Path.Combine(FakeWorktree, "src", "ServerMonitor.App", "bin", "x64", "Debug") + @"\..\..\..\..\Program Files\WindowsApps\ServerAlyzer\ServerMonitor.App.exe", "'.' or '..' segment" },
        { Path.Combine(FakeWorktree, "src", "ServerMonitor.App", "bin", "x64", "Debug", ".", Tfm, "win-x64", "ServerMonitor.App.exe"), "'.' or '..' segment" },
        { @"C:\Program Files\WindowsApps\ServerAlyzer_1.1.1.0_x64__abc\ServerMonitor.App.exe", "installation folder" },
        { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "X", "src", "ServerMonitor.App", "bin", "x64", "Debug", Tfm, "win-x64", "ServerMonitor.App.exe"), "installation folder" },
        { ExeIn(FakeWorktree, "Release"), "not <worktree>" },
        { Path.Combine(QaTestRoots.Root, "no-slnx", "src", "ServerMonitor.App", "bin", "x64", "Debug", Tfm, "win-x64", "ServerMonitor.App.exe"), "not inside a ServerMonitor worktree" },
        { @"src\ServerMonitor.App\bin\x64\Debug\" + Tfm + @"\win-x64\ServerMonitor.App.exe", "not an absolute path" }
    };

    [Theory]
    [MemberData(nameof(RefusedExecutables))]
    public void AnExecutableOutsideADebugWorktreeBuild_IsRefused_OnItsCanonicalPath(string exe, string reason)
    {
        var result = Assert.Single(RunScript([["--qa-health"]], exe, validateOnly: true));
        Assert.StartsWith("REFUSED", result);
        Assert.Contains(reason, result);
    }

    /// <summary>A junction makes a lexically-Debug path land in Release: the OS-resolved path decides.</summary>
    [Fact]
    public void AJunctionFromDebugToRelease_IsRefused()
    {
        var worktree = CreateWorktree("junction");
        var bin = Path.Combine(worktree, "src", "ServerMonitor.App", "bin", "x64");
        Directory.CreateDirectory(Path.Combine(bin, "Release", Tfm, "win-x64"));
        var junction = Path.Combine(bin, "Debug");
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", junction, Path.Combine(bin, "Release")])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        mklink.WaitForExit(30_000);
        try
        {
            Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0, "the junction was not created");
            Assert.True(ValidateAllowsLexically(ExeIn(worktree, "Debug")), "precondition: the path is Debug lexically");

            var result = Assert.Single(RunScript([["--qa-health"]], ExeIn(worktree, "Debug"), validateOnly: true));
            Assert.StartsWith("REFUSED", result);
            Assert.Contains(@"\Release\", result);
        }
        finally
        {
            Directory.Delete(junction); // removes the link only, never the target's content
        }
    }

    // ---- M-1A-R1 addendum: the binary itself (metadata only, never loaded or run) ----

    [Fact]
    public void TheBinary_MustBeADebugBuildCarryingTheIsolation()
    {
        var worktree = CreateWorktree("binary");
        var exe = ExeIn(worktree, "Debug");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, []); // 0 bytes: not runnable, and -ValidateOnly never starts anything anyway
        var dll = Path.ChangeExtension(exe, ".dll");

        Assert.Contains("no ServerMonitor.App.dll", Assert.Single(RunScript([["--qa-health"]], exe, validateOnly: true)));

        // A Debug assembly without the gate 1A isolation (stands in for a pre-1A or non-App build).
        File.Copy(typeof(ServerMonitor.Core.Interfaces.IServerService).Assembly.Location, dll, overwrite: true);
        Assert.Contains("lacks ServerMonitor.App.Qa.QaStartupIsolation", Assert.Single(RunScript([["--qa-health"]], exe, validateOnly: true)));

        // A Release assembly (the runtime's own CoreLib is built Release).
        var release = typeof(object).Assembly;
        Assert.Equal("Release", release.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration);
        File.Copy(release.Location, dll, overwrite: true);
        Assert.Contains("is a 'Release' build, not Debug", Assert.Single(RunScript([["--qa-health"]], exe, validateOnly: true)));

        // The real Debug ServerMonitor.App.dll: accepted.
        File.Copy(typeof(App).Assembly.Location, dll, overwrite: true);
        Assert.Equal("ALLOWED", Assert.Single(RunScript([["--qa-health"]], exe, validateOnly: true)));
    }

    private static bool ValidateAllowsLexically(string exe) =>
        exe.Contains(@"\bin\x64\Debug\", StringComparison.Ordinal) && !exe.Contains(@"\..\", StringComparison.Ordinal);

    [Fact]
    public void TheScriptsLists_AreExactlyTheCompositionRootsHarnessAndModifierFlags()
    {
        var source = File.ReadAllText(Script);
        Assert.Equal(QaStartupIsolation.HarnessFlags, ListIn(source, "HarnessFlags"));
        Assert.Equal(QaStartupIsolation.ModifierFlags, ListIn(source, "ModifierFlags"));
        Assert.Equal([QaGalleryPolicy.ComponentsFlag, QaGalleryPolicy.TokensFlag], ListIn(source, "GalleryFlags"));
    }

    [Fact]
    public void NoOtherToolsScript_StartsAProcess()
    {
        var offenders = Directory.EnumerateFiles(Tools, "*.ps1", SearchOption.AllDirectories)
            .Where(file => !string.Equals(file, Script, StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, index, line)))
            .Where(entry => ProcessStart().IsMatch(entry.line))
            .Select(entry => $"{Path.GetRelativePath(Tools, entry.file)}:{entry.index + 1}: {entry.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "launch the app through tools/qa/Start-QaApp.ps1 only:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static readonly string[][] RefusedLaunches =
    [
        [],                                                          // production composition
        ["--background"],
        ["--qa-ui-language", "pt-PT"],                               // modifier alone
        ["--qa-backup", "ok", "--qa-ssh-config", @"C:\fixture"],
        ["--qa-health=1"],                                           // Vigil probe forms
        ["--qa-proxyjump=on", "--qa-backup", "stuck"],
        ["--QA-HEALTH"],
        ["--qa-health", "--qa-made-up"],
        ["--qa-proxyjump", "--qa-proxyjump-dir="],
        [@"C:\Users\x\AppData\Local\Temp\ServerMonitor-QA\relay-pj"] // the incident's argument
    ];

    private static readonly string[][] AllowedLaunches =
    [
        ["--qa-health"],
        ["--qa-health", "--qa-ui-language", "pt-PT"],
        ["--qa-compact:12"],
        ["--qa-proxyjump", @"--qa-proxyjump-dir=C:\Temp\ServerMonitor-QA\pj", "--qa-ssh-config", @"C:\fixture dir"],
        ["--qa-components", "--qa-gallery-page", "forms", "--qa-gallery-theme", "dark"]
    ];

    [Fact]
    public void ARefusedLaunch_StopsBeforeTheExecutableIsEvenResolved_AndAnAllowedOneOnlyReachesThatCheck()
    {
        var cases = RefusedLaunches.Select(arguments => (Arguments: arguments, Allowed: false))
            .Concat(AllowedLaunches.Select(arguments => (Arguments: arguments, Allowed: true)))
            .ToList();

        var results = RunScript(cases.Select(entry => entry.Arguments).ToList(), SentinelExe, validateOnly: false);

        Assert.False(File.Exists(SentinelExe));
        for (var i = 0; i < cases.Count; i++)
        {
            var label = $"[{string.Join(' ', cases[i].Arguments)}] -> {results[i]}";
            if (cases[i].Allowed)
            {
                Assert.True(results[i].Contains("executable not found", StringComparison.Ordinal), label);
            }
            else
            {
                Assert.True(results[i].StartsWith("REFUSED", StringComparison.Ordinal), label);
                Assert.Contains("QA launch refused", results[i]);
            }
        }
    }

    [Fact]
    public void AReleaseExecutable_IsRefused_EvenWithAHarness()
    {
        var release = SentinelExe.Replace(@"\Debug\", @"\Release\", StringComparison.Ordinal);
        var result = Assert.Single(RunScript([["--qa-health"]], release, validateOnly: true));
        Assert.StartsWith("REFUSED", result);
        Assert.Contains("a Release build ignores --qa flags", result);
    }

    /// <summary>
    /// Same decision as the app's own refusal for every launch that carries a --qa argument: the script allows exactly what
    /// QaStartupIsolation.LaunchRefusal allows (and, unlike the app, never a launch without --qa).
    /// </summary>
    [Fact]
    public void TheScriptsDecision_MatchesTheAppsRefusal()
    {
        var corpus = new List<string[]>();
        string[] tokens = ["--qa-health", "--qa-compact:3", "--qa-proxyjump", "--qa-ui-language", "--qa-backup=ok", "--qa-ssh-config=C:\\f",
            "--qa-proxyjump-dir=C:\\p", "--qa-health=1", "--QA-HISTORY", "--qa", "--qa-components", "--qa-gallery-page=forms", "pt-PT"];
        foreach (var first in tokens)
        {
            foreach (var second in tokens)
            {
                corpus.Add([first, second]);
            }
        }

        var results = RunScript(corpus, SentinelExe, validateOnly: true);
        for (var i = 0; i < corpus.Count; i++)
        {
            string[] appArgs = [SentinelExe, .. corpus[i]];
            var appAllows = QaStartupIsolation.LaunchRefusal(appArgs) is null && corpus[i].Any(QaStartupIsolation.IsQaLike);
            Assert.True(appAllows == (results[i] == "ALLOWED"), $"[{string.Join(' ', corpus[i])}] app={appAllows} script={results[i]}");
        }
    }

    /// <summary>One pwsh process for all cases; each case prints ALLOWED / REFUSED:&lt;msg&gt; / ERROR:&lt;msg&gt;.</summary>
    private static List<string> RunScript(IReadOnlyList<string[]> cases, string exe, bool validateOnly)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var body = new StringBuilder("$ErrorActionPreference = 'Stop'\n");
        foreach (var arguments in cases)
        {
            body.Append("try { $r = & ").Append(Quote(Script)).Append(" -Exe ").Append(Quote(exe))
                .Append(" -Arguments @(").Append(string.Join(", ", arguments.Select(Quote))).Append(')')
                .Append(validateOnly ? " -ValidateOnly" : string.Empty)
                .Append("; if ($r -eq 'ALLOWED') { 'ALLOWED' } else { 'STARTED' } }")
                .Append(" catch { $m = $_.Exception.Message -replace '\\s+', ' '; if ($m -like 'QA launch refused*') { 'REFUSED:' + $m } else { 'ERROR:' + $m } }\n");
        }

        // A long corpus does not fit a command line: the cases go into a temporary script under this assembly's QA root.
        Directory.CreateDirectory(QaTestRoots.Root);
        var driver = Path.Combine(QaTestRoots.Root, $"start-qaapp-cases-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(driver, body.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", driver })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var errorTask = process.StandardError.ReadToEndAsync(); // read both streams at once: no pipe deadlock
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            process.Kill();
            Assert.Fail("pwsh did not finish");
        }

        var error = errorTask.Result;
        File.Delete(driver);
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.True(lines.Count == cases.Count, $"expected {cases.Count} results, got {lines.Count}: {output} {error}");
        Assert.DoesNotContain("STARTED", lines);
        return lines;
    }

    private static string[] ListIn(string source, string name)
    {
        var match = Regex.Match(source, @"\$" + name + @"\s*=\s*@\((?<items>[^)]*)\)");
        Assert.True(match.Success, $"${name} not found in Start-QaApp.ps1");
        return Regex.Matches(match.Groups["items"].Value, "'([^']*)'").Select(item => item.Groups[1].Value).ToArray();
    }

    [GeneratedRegex(@"Start-Process|Diagnostics\.Process\]::Start|\[Process\]::Start|ProcessStartInfo|Invoke-Item|\bStart\s+-FilePath", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessStart();
}
