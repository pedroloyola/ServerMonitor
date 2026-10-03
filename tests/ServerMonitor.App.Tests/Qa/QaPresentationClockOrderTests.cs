using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.5 fix round 4 (runtime smoke finding): the composition root now registers the system PresentationClock
/// explicitly (the notice owners require one), but it runs AFTER the overview harness has registered its fixed clock.
/// A plain AddSingleton there made the system clock win ("Há 1 dia" instead of "Há 8 segundos"); TryAdd keeps the
/// harness clock, in the production order, and still supplies the system clock when no harness did.
/// </summary>
public sealed class QaPresentationClockOrderTests
{
    [Fact]
    public void InTheProductionOrder_TheHarnessClockWins_OverTheExplicitSystemClock()
    {
        var services = new ServiceCollection();
        QaOverviewComposition.Apply(services, "mixed", backupDoublesRequested: true); // earlier in App.ConfigureServices
        services.TryAddSingleton(PresentationClock.System);                         // the root's explicit system clock
        using var provider = services.BuildServiceProvider();

        Assert.Equal(QaOverviewCatalog.Now, provider.GetRequiredService<PresentationClock>().UtcNow);
    }

    [Fact]
    public void WithoutAHarness_TheRootSuppliesTheSystemClock()
    {
        var services = new ServiceCollection();
        services.TryAddSingleton(PresentationClock.System);
        using var provider = services.BuildServiceProvider();

        Assert.Same(PresentationClock.System, provider.GetRequiredService<PresentationClock>());
    }

    [Fact]
    public void TheRoot_TryAddsTheSystemClock_AfterTheOverviewHarness()
    {
        var code = AppSourceTree.CodeWithoutComments("App.xaml.cs");
        var harness = code.IndexOf("QaOverviewComposition.Apply(services)", StringComparison.Ordinal);
        var clock = code.IndexOf("services.TryAddSingleton(PresentationClock.System)", StringComparison.Ordinal);

        Assert.True(harness >= 0 && clock >= 0, "both registrations must exist");
        Assert.True(clock > harness);
        Assert.DoesNotContain("services.AddSingleton(PresentationClock.System)", code, StringComparison.Ordinal);
    }
}
