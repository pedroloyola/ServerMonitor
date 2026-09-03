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

    // ------------------------------------------------------------------ the degraded session is WIRED

    /// <summary>
    /// The blocking defect, as a test: a launch whose registration never succeeded must degrade at
    /// STARTUP, not at the user's first close.
    /// </summary>
    /// <remarks>
    /// Before the fix, <see cref="TrayAffordanceLifecycle"/> was constructed lazily by the
    /// <c>WindowCloseCoordinator</c> factory, so nothing evaluated it until someone clicked X. A
    /// <c>--background</c> launch with a failed registration therefore published Unavailable to nobody
    /// and went on monitoring, invisible, with no way out — A12, the thing this slice exists to remove.
    /// </remarks>
    [Fact]
    public void A_launch_with_no_affordance_degrades_at_startup_and_not_at_the_first_close()
    {
        var harness = new StartupHarness(TrayAffordanceState.Unavailable);

        App.EvaluateStartupAffordance(harness.Services);

        Assert.True(harness.Notice.Raised);
        Assert.Equal(1, harness.Window.BackgroundSettingsOpened);
    }

    /// <summary>
    /// The other half: the subscription is LIVE from startup, so an affordance lost before the user has
    /// closed anything degrades then, instead of the icon vanishing silently and the next close quitting
    /// with no explanation.
    /// </summary>
    [Fact]
    public void An_affordance_lost_before_any_user_close_degrades_immediately()
    {
        var harness = new StartupHarness(TrayAffordanceState.Available);

        App.EvaluateStartupAffordance(harness.Services);
        Assert.False(harness.Notice.Raised);

        harness.Source.Publish(TrayAffordanceState.Lost);

        Assert.True(harness.Notice.Raised);
        Assert.Equal(1, harness.Window.BackgroundSettingsOpened);
    }

    [Fact]
    public void A_healthy_launch_degrades_nothing()
    {
        var harness = new StartupHarness(TrayAffordanceState.Available);

        App.EvaluateStartupAffordance(harness.Services);

        Assert.False(harness.Notice.Raised);
        Assert.Equal(0, harness.Window.BackgroundSettingsOpened);
    }

    [Fact]
    public void A_recovering_affordance_at_startup_holds_without_degrading()
    {
        // CV-2b: an unauthenticated TaskbarCreated broadcast must not be able to cost the user the
        // session. Evaluating at startup must not turn the bounded recovery window into a degradation.
        var harness = new StartupHarness(TrayAffordanceState.Recovering);

        App.EvaluateStartupAffordance(harness.Services);

        Assert.False(harness.Notice.Raised);
        Assert.Equal(0, harness.Window.BackgroundSettingsOpened);
    }

    /// <summary>
    /// The one link the behaviour tests cannot reach: that <c>OnLaunched</c> actually calls the seam.
    /// </summary>
    /// <remarks>
    /// A source assertion, declared as one — and comments are stripped first, which is not a detail. My
    /// first version asserted the call text was present, and a mutation that COMMENTED THE CALL OUT
    /// stayed green: the text was still there, inside the comment. I had claimed a positive assertion
    /// could not be satisfied by prose. It can. Stripping comments is what makes the claim true.
    /// </remarks>
    [Fact]
    public void OnLaunched_evaluates_the_affordance_after_the_host_starts_and_before_activation_routing()
    {
        var source = StripComments(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ServerMonitor.App", "App.xaml.cs")));

        var startAsync = source.IndexOf("await ServicesHost.StartAsync();", StringComparison.Ordinal);
        var evaluate = source.IndexOf(
            "EvaluateStartupAffordance(ServicesHost.Services);", StringComparison.Ordinal);
        var markReady = source.IndexOf("_activationRouter.MarkReady();", StringComparison.Ordinal);

        Assert.True(startAsync >= 0, "the host start could not be found");
        Assert.True(evaluate >= 0, "OnLaunched does not evaluate the affordance at startup");
        Assert.True(markReady >= 0, "the activation hand-off could not be found");
        Assert.InRange(evaluate, startAsync, markReady);
    }

    private static string StripComments(string source) =>
        System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*[\s\S]*?\*/|//[^\r\n]*", string.Empty);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    /// <summary>
    /// The REAL composition, with only what a test cannot have replaced: the affordance source (so the
    /// state under test can be chosen), the window and the notice (so degradation is observable), and the
    /// lifecycle controller. <see cref="TrayAffordanceLifecycle"/> itself is the production registration.
    /// </summary>
    private sealed class StartupHarness
    {
        public FakeAffordanceSource Source { get; }

        public FakeDegradationNotice Notice { get; } = new();

        public FakeWindowController Window { get; } = new();

        public ServiceProvider Services { get; }

        public StartupHarness(TrayAffordanceState initial)
        {
            Source = new FakeAffordanceSource(initial);

            var services = RealComposition();
            services.AddLogging();
            services.AddSingleton<IAppLifecycleController>(FakeLifecycle.Instance);
            services.AddSingleton<ITrayAffordanceSource>(Source);
            services.AddSingleton<IBackgroundDegradationNotice>(Notice);
            services.AddSingleton<IApplicationWindowController>(Window);

            Services = services.BuildServiceProvider();
        }
    }

    private sealed class FakeAffordanceSource(TrayAffordanceState initial) : ITrayAffordanceSource
    {
        public event EventHandler? StateChanged;

        public TrayAffordanceState State { get; private set; } = initial;

        public void Publish(TrayAffordanceState state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeDegradationNotice : IBackgroundDegradationNotice
    {
        public event EventHandler? Changed;

        public bool IsDegraded => Raised;

        public bool Raised { get; private set; }

        public void Raise()
        {
            Raised = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeWindowController : IApplicationWindowController
    {
        public int BackgroundSettingsOpened { get; private set; }

        public bool IsAttached => true;

        public bool IsMaterialized => true;

        public void OpenBackgroundSettings() => BackgroundSettingsOpened++;

        public void Attach(Window window)
        {
        }

        public void AttachWindowFactory(Func<Window> factory)
        {
        }

        public void HideForMinimize()
        {
        }

        public void HideToBackground()
        {
        }

        public void RestoreAndActivate()
        {
        }

        public void OpenSettings()
        {
        }

        public void ToggleCompactMode()
        {
        }

        public void RequestClose()
        {
        }

        public void BeginShutdown()
        {
        }
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
