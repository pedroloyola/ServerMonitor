using System.Text.RegularExpressions;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// The composition guard for the returned blocker (M13 S2 §1).
/// <para>
/// The watchdog behaved correctly and was still defeated by WHERE it was registered: a container-created
/// singleton inside the very <c>IHost</c> the exit stops and disposes, so <c>host.Dispose()</c> ended it
/// and a failed <c>Application.Exit()</c> left no escalation. Behaviour tests cannot see that — the
/// defect lives in the wiring — so the wiring is asserted here. Restoring the old registration makes
/// these fail, which is the point: the previous suite passed with it.
/// </para>
/// </summary>
public sealed class WatchdogOwnershipBoundaryTests
{
    private static string AppComposition() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "ServerMonitor.App", "App.xaml.cs"));

    private static string ProgramSource() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "ServerMonitor.App", "Program.cs"));

    /// <summary>
    /// The host may KNOW about the watchdog, but it must not CREATE it: a container disposes what it
    /// creates, and this must outlive the container by construction.
    /// </summary>
    [Fact]
    public void The_host_container_never_creates_the_watchdog()
    {
        var composition = StripComments(AppComposition());

        Assert.DoesNotContain("AddSingleton<ITerminationWatchdog, TerminationWatchdog>", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("new TerminationWatchdog(", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("new DedicatedThreadWatchdogScheduler(", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProcessTerminator(", composition, StringComparison.Ordinal);
    }

    /// <summary>It is registered as the PROCESS-owned instance, which is what makes it survive the host.</summary>
    [Fact]
    public void The_watchdog_and_the_terminator_are_registered_as_process_owned_instances()
    {
        var composition = StripComments(AppComposition());

        Assert.Contains("AddSingleton(Program.TerminationWatchdog)", composition, StringComparison.Ordinal);
        Assert.Contains("AddSingleton(Program.ProcessTerminator)", composition, StringComparison.Ordinal);
    }

    /// <summary>And the process really does own them, created before any host exists.</summary>
    [Fact]
    public void Program_owns_the_watchdog_and_the_terminator()
    {
        var program = StripComments(ProgramSource());

        Assert.Matches(new Regex(@"static\s+ITerminationWatchdog\s+TerminationWatchdog"), program);
        Assert.Matches(new Regex(@"static\s+IProcessTerminator\s+ProcessTerminator"), program);

        Assert.NotNull(Program.TerminationWatchdog);
        Assert.NotNull(Program.ProcessTerminator);
        Assert.IsNotType<IDisposable>(Program.TerminationWatchdog, exactMatch: false);
    }

    /// <summary>
    /// Nothing in the app may try to end the watchdog. There is no such API any more, so this is a guard
    /// against re-adding one: the only state that makes it inert is the death of the process.
    /// </summary>
    [Fact]
    public void Nothing_in_the_app_tries_to_disarm_or_dispose_the_watchdog()
    {
        var offenders = Directory
            .EnumerateFiles(
                Path.Combine(FindRepositoryRoot(), "src", "ServerMonitor.App"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var source = StripComments(File.ReadAllText(path));
                return source.Contains("Watchdog.Disarm", StringComparison.Ordinal)
                    || source.Contains("watchdog.Disarm", StringComparison.Ordinal)
                    || source.Contains("Watchdog.Dispose", StringComparison.Ordinal)
                    || source.Contains("watchdog.Dispose", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
        Assert.DoesNotContain(
            typeof(ITerminationWatchdog).GetMethods(),
            method => method.Name is "Disarm" or "Dispose" or "Cancel");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate ServerMonitor.slnx from test output.");
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", string.Empty, RegexOptions.None);
}
