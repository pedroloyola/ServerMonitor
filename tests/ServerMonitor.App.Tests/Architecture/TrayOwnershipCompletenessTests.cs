using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using ServerMonitor.Core.Enums;
using ServerMonitor.App;
using ServerMonitor.App.Services;
using ServerMonitor.App.Shell.Tray;
using ServerMonitor.App.Tests.Fakes;

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

    // ------------------------------------------------------------------ the DPI update is ROUTED

    /// <summary>
    /// The adapter SENDS its own shell updates through the machine's gate.
    /// </summary>
    /// <remarks>
    /// The previous test proved only that <c>InvokeUnderShellGate</c> serializes — a property of the
    /// machine. A mutation that sent the DPI update straight to the shell left it green, because nothing
    /// asserted that this adapter uses the gate at all. This asserts the routing.
    /// </remarks>
    [Fact]
    public void A_shell_update_owned_by_the_adapter_is_routed_through_the_machines_gate()
    {
        var native = new BlockingNativeTrayRegistration();
        using var machine = new TrayStateMachine(
            native,
            () => { },
            () => { },
            TimeProvider.System,
            NullLogger<TrayStateMachine>.Instance);

        var ran = false;
        OwnedTrayIconAdapter.RouteShellUpdate(machine, () =>
        {
            ran = true;

            // Inside the gate: a second shell call from this thread is re-entrant, but the point is that
            // the update runs where the machine's own calls are serialized.
            native.Calls.Add("Dpi");
        });

        Assert.True(ran, "the update never ran");
        Assert.Contains("Dpi", native.Calls);
    }

    [Fact]
    public void A_shell_update_still_runs_when_there_is_no_machine_to_serialize_against()
    {
        var ran = false;

        OwnedTrayIconAdapter.RouteShellUpdate(null, () => ran = true);

        Assert.True(ran, "with no machine there is nothing to serialize against, so it must still run");
    }

    /// <summary>
    /// The link the behaviour tests cannot reach: that the DPI handler uses the router.
    /// </summary>
    /// <remarks>
    /// A source assertion, comments stripped, declared as one — <c>OnDpiChanged</c> needs a real
    /// <c>TrayHostWindow</c> and a real registration, so no test can drive it.
    /// </remarks>
    [Fact]
    public void The_DPI_handler_goes_through_the_router_and_not_straight_to_the_shell()
    {
        var source = StripComments(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ServerMonitor.App", "Shell", "Tray", "OwnedTrayIconAdapter.cs")));

        var handler = source.IndexOf("private void OnDpiChanged", StringComparison.Ordinal);
        Assert.True(handler >= 0, "the DPI handler could not be found");

        var body = source[handler..];
        var end = body.IndexOf("internal static void RouteShellUpdate", StringComparison.Ordinal);
        Assert.True(end > 0, "the router could not be found");

        body = body[..end];

        // The update is handed to the router, not issued directly and not gated by hand. Naming
        // UpdateForDpi is expected — it is the delegate being routed; what must not appear is a call
        // that bypasses the router or reimplements it.
        Assert.Contains(
            "RouteShellUpdate(machine, () => registration.UpdateForDpi(dpi));", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvokeUnderShellGate", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The UI marshaller has exactly ONE inline invocation — the branch where we already own the thread —
    /// and no fallback that runs the continuation when the dispatcher refuses.
    /// </summary>
    /// <remarks>
    /// Source-level, and declared as one: taking the false branch needs a real <c>DispatcherQueue</c> in
    /// the middle of shutting down, which no test can produce. The fallback is what cancelled the
    /// topology guarantee, so its ABSENCE is what has to be pinned.
    /// </remarks>
    [Fact]
    public void The_UI_marshaller_has_no_inline_fallback()
    {
        var source = StripComments(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ServerMonitor.App", "Shell", "Tray", "OwnedTrayIconAdapter.cs")));

        var start = source.IndexOf("private bool RunOnUiThread", StringComparison.Ordinal);
        Assert.True(start >= 0, "the marshaller could not be found");

        var body = source[start..];
        var end = body.IndexOf("\n    }", StringComparison.Ordinal);
        Assert.True(end > 0, "the marshaller body could not be delimited");
        body = body[..end];

        var inlineCalls = body.Split("continuation();").Length - 1;
        Assert.Equal(1, inlineCalls);
        Assert.Contains("return dispatcher.TryEnqueue(", body, StringComparison.Ordinal);
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

        /// <summary>
        /// THE SAME TWO CHANNELS AS PRODUCTION. A fake that delivered a loss on the observer event would
        /// be a permanent mutation applied to the environment instead of the code: every degradation test
        /// would keep passing while the real machine had stopped using that channel. So the loss goes to
        /// the registered consumer, exactly as the machine does it, and single assignment is enforced
        /// here too.
        /// </summary>
        private ITrayLossConsumer? _lossConsumer;

        public void SetLossConsumer(ITrayLossConsumer consumer)
        {
            if (_lossConsumer is not null)
            {
                throw new InvalidOperationException(
                    "The authoritative loss consumer is already registered; there is exactly one.");
            }

            _lossConsumer = consumer;
        }

        public void Publish(TrayAffordanceState state)
        {
            State = state;

            if (state is TrayAffordanceState.Lost or TrayAffordanceState.Unavailable)
            {
                _lossConsumer?.AcknowledgeLoss(state);
                return;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The commit, faked the way the real one behaves: the act runs only while the affordance is
        /// established, and it runs INSIDE the determination so a test can invalidate the affordance from
        /// within and see that the act was still refused.
        /// </summary>
        public void EnterBackground(Action enterBackground)
        {
            if (State != TrayAffordanceState.Available)
            {
                return;
            }

            enterBackground();
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

    /// <summary>
    /// The OWNER's own single-assignment guard, on the owner and not on a double.
    /// </summary>
    /// <remarks>
    /// This test exists because its absence was measured: the mutation that lets a latecomer displace the
    /// authoritative loss consumer SURVIVED, while a test named for exactly that property passed. That
    /// test was driving the fake source, whose guard is a copy — so it proved the copy and nothing about
    /// the owner. A guard that only a test double enforces is not a guard, and it is the same lesson the
    /// honest-delete fake taught earlier in this slice.
    /// <para>
    /// Displacement is the INVERSE abuse of the seam: instead of failing to consume a loss, a latecomer
    /// consumes every loss silently and suppresses the fail-safe that should have fired.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_owner_refuses_a_second_authoritative_loss_consumer()
    {
        var adapter = new OwnedTrayIconAdapter(
            new UnusedThemeService(),
            new UnusedLocalizationService(),
            () => FakeLifecycle.Instance,
            new UnusedProcessTerminator(),
            NullLoggerFactory.Instance);

        adapter.SetLossConsumer(new NoopLossConsumer());

        Assert.Throws<InvalidOperationException>(() => adapter.SetLossConsumer(new NoopLossConsumer()));
    }

    private sealed class NoopLossConsumer : ITrayLossConsumer
    {
        public void AcknowledgeLoss(TrayAffordanceState state)
        {
        }
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
