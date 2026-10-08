using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.Windowing;

/// <summary>
/// Cortex 8B gate N-2: <see cref="WindowModeViewModel"/> takes the window controller and the dashboard as OPTIONAL ctor
/// dependencies (so the existing single-argument tests stay valid); a registration that dropped them would turn the Compact
/// exits (row → Detail, Adicionar, Gerir ocultos) into silent no-ops. Proven over the REAL composition root (every per-user
/// path re-pointed at temp first): the container hands it BOTH, and they are the very singletons the rest of the app uses.
/// </summary>
public sealed class WindowModeViewModelCompositionTests
{
    [Fact]
    public async Task TheRealRegistration_GivesWindowModeViewModelBothOptionalDependencies()
    {
        using var composition = new TestSupport.IsolatedAppComposition();
        await using var provider = composition.BuildProvider(); // async: a hosted service is IAsyncDisposable only

        var windowMode = provider.GetRequiredService<WindowModeViewModel>();

        Assert.Same(provider.GetRequiredService<IApplicationWindowController>(), Field(windowMode, "_windowController"));
        Assert.Same(provider.GetRequiredService<DashboardViewModel>(), Field(windowMode, "_dashboard"));
    }

    private static object? Field(WindowModeViewModel viewModel, string name) =>
        typeof(WindowModeViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel);
}
