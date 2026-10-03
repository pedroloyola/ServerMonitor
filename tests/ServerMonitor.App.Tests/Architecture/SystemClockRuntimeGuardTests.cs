using System.Text.RegularExpressions;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.Architecture;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SystemClockGuardCollection
{
    public const string Name = "System clock runtime guard (not parallel: one test briefly disarms it)";
}

/// <summary>
/// UI.5 fix round 5 (Boss §16, Atlas C4): the runtime guard is the proof that no App test counts a notice down on the
/// system clock. The system provider is obtained here by REFLECTION on purpose - a route no lexical scan can see.
/// </summary>
[Collection(SystemClockGuardCollection.Name)]
public sealed class SystemClockRuntimeGuardTests
{
    private static TimeProvider SystemProviderByReflection() =>
        (TimeProvider)typeof(TimeProvider).GetProperty("System")!.GetValue(null)!;

    [Fact]
    public void TheGuard_IsArmedForEveryTestRun()
    {
        Assert.True(TransientNoticeTimer.RejectSystemTimeProvider);
        Assert.Contains("[ModuleInitializer]", File.ReadAllText(Path.Combine(
            AppSourceTree.RepositoryRoot, "tests", "ServerMonitor.App.Tests", "Fakes", "SystemClockTestGuard.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Armed_ANoticeOnTheSystemClock_Throws_HoweverTheClockWasObtained()
    {
        var system = SystemProviderByReflection();
        var viaPresentationClock = ((PresentationClock)typeof(PresentationClock).GetProperty("System")!.GetValue(null)!).TimeProvider;

        var direct = Assert.Throws<InvalidOperationException>(() => new TransientNoticeTimer(system).Start(() => { }));
        Assert.Contains("SYSTEM clock", direct.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new TransientNoticeTimer(viaPresentationClock).Start(() => { }));
        // Both refusals were also RECORDED for this test (a swallowing caller cannot hide them); taken here on purpose.
        Assert.Equal(2, SystemClockTestGuard.TakeViolations().Count);
        using var fake = new TransientNoticeTimer(TestClock.Fake().TimeProvider);
        fake.Start(() => { }); // a fake is fine
        Assert.True(fake.IsRunning);
    }

    /// <summary>
    /// Production (never armed): the system clock starts a real countdown exactly as before. The ONLY narrow disarm in the
    /// test project, scoped to this statement and in a non-parallel collection; the timer is cancelled at once (no wait).
    /// </summary>
    [Fact]
    public void Unarmed_AsInProduction_TheSystemClockStartsTheCountdownAsBefore()
    {
        TransientNoticeTimer.RejectSystemTimeProvider = false;
        try
        {
            using var timer = new TransientNoticeTimer(SystemProviderByReflection());
            timer.Start(() => { });
            Assert.True(timer.IsRunning);
            timer.Cancel();
        }
        finally
        {
            TransientNoticeTimer.RejectSystemTimeProvider = true;
        }
    }

    /// <summary>Production never arms it: no assignment anywhere in src, and the declaration has no initializer.</summary>
    [Fact]
    public void ProductionCode_NeverArmsTheGuard()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(AppSourceTree.RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path: path, Text: File.ReadAllText(path))).ToList();

        Assert.DoesNotContain(sources, source => Regex.IsMatch(source.Text, @"(RejectSystemTimeProvider|SystemClockRejected)\s*=[^=>]"));
        var declaration = sources.Single(source => source.Path.EndsWith("TransientNoticeTimer.cs", StringComparison.Ordinal)).Text;
        Assert.Contains("internal static bool RejectSystemTimeProvider { get; set; }\n", declaration.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("internal static Action<string>? SystemClockRejected { get; set; }\n", declaration.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }
}
