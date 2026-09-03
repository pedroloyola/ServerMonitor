using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using ServerMonitor.Core.Enums;
using ServerMonitor.App;
using ServerMonitor.App.Services;
using ServerMonitor.App.Shell.Tray;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// The swap is COMPLETE: one owner of the icon, and no path that can bring the old one back.
/// <para>
/// This is the M13-QA-9 lesson applied to itself. That defect was correct code with no caller; the
/// inverse — a new owner registered beside the old one, or an old one still reachable behind a fallback —
/// is the same class of defect, and "I removed it" is not evidence. These assertions are the evidence,
/// and they run against the container the application really builds.
/// </para>
/// </summary>
public sealed class TrayOwnershipCompletenessTests
{
    private static readonly Assembly AppAssembly = typeof(TrayStateMachine).Assembly;

    /// <summary>Builds the real composition, exactly as <c>App</c> does, without starting a host.</summary>
    private static ServiceCollection RealComposition()
    {
        var services = new ServiceCollection();
        App.ConfigureApplicationServices(services);
        return services;
    }

    // ------------------------------------------------------------------ one owner

    [Fact]
    public void The_assembly_declares_exactly_one_tray_icon_owner()
    {
        var owners = Implementations(typeof(ITrayIconAdapter));

        Assert.Equal([typeof(OwnedTrayIconAdapter)], owners);
    }

    [Fact]
    public void The_assembly_declares_exactly_one_affordance_source()
    {
        // Two types implement the interface, and the second one is not a competitor: TrayStateMachine IS
        // the state, and OwnedTrayIconAdapter forwards to it so that something exists to hand the
        // container before Start() has run. Naming both by identity is the point — the placeholder that
        // used to answer this question is gone, and a THIRD implementation appearing would mean a second
        // answer to whether the user has a way out.
        var sources = Implementations(typeof(ITrayAffordanceSource));

        Assert.Equal([typeof(OwnedTrayIconAdapter), typeof(TrayStateMachine)], sources);
    }

    [Fact]
    public void No_type_from_the_replaced_tray_implementation_survives()
    {
        var leftovers = AppAssembly
            .GetTypes()
            .Where(t => t.Name.Contains("WinUIExTray", StringComparison.Ordinal)
                        || t.Name.Contains("PendingTrayAffordance", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .ToArray();

        Assert.Empty(leftovers);
    }

    // ------------------------------------------------------------------ the real container

    [Fact]
    public void The_container_resolves_both_tray_roles_to_the_same_single_instance()
    {
        // Two registrations of the same type would be two Shell_NotifyIcon owners with one icon id. The
        // factories must both forward to the one concrete singleton, and this proves they do — by
        // resolving, not by reading the registration shape.
        var services = RealComposition();
        services.AddSingleton<IAppLifecycleController>(FakeLifecycle.Instance);

        // Logging comes from ConfigureLogging, which is a different builder stage; the composition root
        // does not register it and is not supposed to.
        services.AddLogging();

        using var provider = services.BuildServiceProvider();

        var byRole = provider.GetRequiredService<ITrayIconAdapter>();
        var byAffordance = provider.GetRequiredService<ITrayAffordanceSource>();
        var concrete = provider.GetRequiredService<OwnedTrayIconAdapter>();

        Assert.Same(concrete, byRole);
        Assert.Same(concrete, byAffordance);
    }

    [Fact]
    public void Nothing_registers_a_second_implementation_for_either_tray_role()
    {
        var services = RealComposition();

        Assert.Single(services, d => d.ServiceType == typeof(ITrayIconAdapter));
        Assert.Single(services, d => d.ServiceType == typeof(ITrayAffordanceSource));
        Assert.Single(services, d => d.ServiceType == typeof(OwnedTrayIconAdapter));
    }

    /// <summary>
    /// CV-20, T14c, over the <see cref="IServiceCollection"/> the composition root REALLY produces.
    /// </summary>
    /// <remarks>
    /// This replaces the source-text approximation the first delivery shipped with. Text would pass over
    /// a registration written any other way — a factory, a generic helper, an extension method — and the
    /// condition was explicit that it had to be the descriptors.
    /// </remarks>
    [Fact]
    public void T14c_the_capability_is_never_registered_in_the_container()
    {
        var services = RealComposition();

        var offenders = services
            .Where(d => d.ServiceType == typeof(INativeTrayRegistration)
                        || d.ImplementationType == typeof(INativeTrayRegistration))
            .Select(d => d.ServiceType.FullName!)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void T14c_is_not_vacuous_because_the_composition_really_ran()
    {
        // An empty collection would satisfy the assertion above for the wrong reason. This pins that the
        // seam actually produced the application's registrations.
        var services = RealComposition();

        Assert.True(services.Count > 50, $"the composition produced only {services.Count} descriptors");
        Assert.Contains(services, d => d.ServiceType == typeof(ITrayAffordanceSource));
    }

    [Fact]
    public void The_owner_reports_no_affordance_until_a_registration_has_actually_succeeded()
    {
        // Fail-closed at the only moment it is free to be wrong: before Start() there is no machine and
        // therefore no proof of anything. Reporting Available here would let the window hide on a process
        // that never registered an icon — the A12 zombie this whole contract exists to prevent.
        var adapter = new OwnedTrayIconAdapter(
            new UnusedThemeService(),
            new UnusedLocalizationService(),
            () => FakeLifecycle.Instance,
            new UnusedProcessTerminator(),
            NullLoggerFactory.Instance);

        Assert.Equal(TrayAffordanceState.Unavailable, adapter.State);
    }

    // ------------------------------------------------------------------

    private sealed class UnusedThemeService : IThemeService
    {
        public AppThemePreference Current => AppThemePreference.System;

        public void Attach(FrameworkElement rootElement) => throw new NotSupportedException();

        public void Detach(FrameworkElement rootElement) => throw new NotSupportedException();

        public void Apply(AppThemePreference preference) => throw new NotSupportedException();
    }

    private sealed class UnusedLocalizationService : ILocalizationService
    {
        public string? CurrentLanguageOverride => null;

        public string GetString(string resourceKey) => throw new NotSupportedException();

        public void InitializeFromSystem() => throw new NotSupportedException();

        public void SetLanguage(string? languageTag) => throw new NotSupportedException();
    }

    private sealed class UnusedProcessTerminator : IProcessTerminator
    {
        public void Terminate(int exitCode) => throw new NotSupportedException();
    }

    private static Type[] Implementations(Type contract) =>
        [.. AppAssembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && contract.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)];

    /// <summary>
    /// The composition registers <see cref="IAppLifecycleController"/> through a factory that reaches
    /// process-level statics. Overriding it keeps this test about tray ownership rather than about the
    /// lifecycle it is not testing.
    /// </summary>
    private sealed class FakeLifecycle : IAppLifecycleController
    {
        internal static readonly FakeLifecycle Instance = new();

        public AppLifecycleState State => AppLifecycleState.Foreground;

        public bool StartedInBackground => false;

        public bool IsExiting => false;

        public void EnterForeground()
        {
        }

        public void EnterBackground()
        {
        }

        public void RequestExit(ExitReason reason)
        {
        }
    }
}
