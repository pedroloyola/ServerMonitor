using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// UI.5 §4 against the REAL <see cref="NavigationService"/> over a recorded host and page factory (the UI.4 MUST-1 seam):
/// the two Settings sub-pages, the in-page General ↔ Data navigation (Cortex 2), the About landing (H-UI5-4) and the
/// Background focus request made while Settings is ALREADY shown (Cortex #6, the latent defect fixed in UI.5).
/// </summary>
public sealed class NavigationServiceUi5Tests
{
    [Theory]
    [InlineData(SettingsSection.General, typeof(SettingsPage))]
    [InlineData(SettingsSection.Data, typeof(SettingsDataPage))]
    [InlineData(SettingsSection.About, typeof(SettingsDataPage))]
    public void EachSection_LandsOnItsSubPage(SettingsSection section, Type expected)
    {
        var world = new World();

        world.Navigation.GoToSettings(section);

        Assert.Equal(expected, world.Shown.PageType);
    }

    [Fact]
    public void GoToSettings_WithoutASection_IsGeneral()
    {
        var world = new World();

        world.Navigation.GoToSettings();

        Assert.Equal(typeof(SettingsPage), world.Shown.PageType);
    }

    /// <summary>Moving between the sub-pages reuses the singletons (a restore in progress lives in their shared VMs).</summary>
    [Fact]
    public void MovingBetweenTheSubPages_ReusesBothSingletons()
    {
        var world = new World();

        world.Navigation.GoToSettings(SettingsSection.General);
        var general = world.Shown;
        world.Navigation.GoToSettings(SettingsSection.Data);
        var data = world.Shown;
        world.Navigation.GoToSettings(SettingsSection.General);
        Assert.Same(general, world.Shown);
        world.Navigation.GoToSettings(SettingsSection.Data);
        Assert.Same(data, world.Shown);
    }

    /// <summary>Cortex #6: targeting the sub-page already shown swaps nothing (no Loaded) — the page is told instead.</summary>
    [Fact]
    public void TargetingTheShownSubPage_NotifiesIt_InsteadOfDoingNothing()
    {
        var world = new World();
        world.Navigation.GoToSettings(SettingsSection.Data);
        var data = world.Shown;
        data.SimulateLoaded();
        var awayCount = world.NavigatedAway;

        world.Navigation.GoToSettings(SettingsSection.About);

        Assert.Same(data, world.Shown);
        Assert.Equal(1, data.NotifiedAgain);
        Assert.Equal(awayCount, world.NavigatedAway); // nothing was swapped
        Assert.True(data.AboutConsumed);
    }

    /// <summary>
    /// The exact production sequence (ApplicationWindowController.OpenBackgroundSettings): GoToSettings, THEN the request,
    /// with Settings already open. Before UI.5 the request waited for the next visit; now the shown page consumes it.
    /// </summary>
    [Fact]
    public void ABackgroundRequest_WhileGeneralIsShown_IsConsumedAtOnce()
    {
        var world = new World();
        world.Navigation.GoToSettings();
        var general = world.Shown;
        general.SimulateLoaded();
        Assert.False(general.BackgroundConsumed);

        world.Navigation.GoToSettings();
        world.Navigation.RequestBackgroundSettingsFocus();

        Assert.True(general.BackgroundConsumed);
        Assert.False(world.Navigation.ConsumeBackgroundSettingsFocus()); // consumed once, by the page
    }

    /// <summary>
    /// Cortex B1 M-1: the PRODUCTION order on the cold path (ApplicationWindowController.OpenBackgroundSettings with the
    /// window in the tray): GoToSettings swaps the content, THEN the request arrives - before the page's Loaded. The page
    /// must not be told yet (it would consume the request on an element that cannot scroll); its Loaded consumes it.
    /// </summary>
    [Fact]
    public void ColdPath_GoToThenRequest_BeforeLoaded_IsConsumedByLoaded()
    {
        var world = new World();

        world.Navigation.GoToSettings();
        var general = world.Shown;
        world.Navigation.RequestBackgroundSettingsFocus();

        Assert.Equal(0, general.NotifiedAgain);
        Assert.False(general.BackgroundConsumed);
        general.SimulateLoaded();
        Assert.True(general.BackgroundConsumed);
        Assert.False(world.Navigation.ConsumeBackgroundSettingsFocus()); // consumed exactly once, by Loaded
    }

    /// <summary>The same rule for an About request aimed at a Data page that is shown but not loaded yet.</summary>
    [Fact]
    public void ColdPath_AboutBeforeLoaded_IsConsumedByLoaded()
    {
        var world = new World();

        world.Navigation.GoToSettings(SettingsSection.Data);
        var data = world.Shown;
        world.Navigation.GoToSettings(SettingsSection.About); // same page, not loaded yet

        Assert.Equal(0, data.NotifiedAgain);
        data.SimulateLoaded();
        Assert.True(data.AboutConsumed);
    }

    [Fact]
    public void ABackgroundRequest_BeforeTheFirstVisit_WaitsForThatVisit()
    {
        var world = new World();

        world.Navigation.RequestBackgroundSettingsFocus();
        world.Navigation.GoToSettings();
        var general = world.Shown;
        Assert.False(general.BackgroundConsumed); // no Loaded in the recorded host yet
        general.SimulateLoaded();

        Assert.True(general.BackgroundConsumed);
    }

    [Fact]
    public void ABackgroundRequest_WhileDataIsShown_StaysPendingForGeneral()
    {
        var world = new World();
        world.Navigation.GoToSettings(SettingsSection.Data);

        world.Navigation.RequestBackgroundSettingsFocus();

        Assert.False(world.Shown.BackgroundConsumed);
        world.Navigation.GoToSettings();
        world.Shown.SimulateLoaded();
        Assert.True(world.Shown.BackgroundConsumed);
    }

    [Fact]
    public void TheAboutRequest_IsConsumedOnce()
    {
        var world = new World();

        world.Navigation.GoToSettings(SettingsSection.About);

        Assert.True(world.Navigation.ConsumeAboutSettingsFocus());
        Assert.False(world.Navigation.ConsumeAboutSettingsFocus());
        world.Navigation.GoToSettings(SettingsSection.Data);
        Assert.False(world.Navigation.ConsumeAboutSettingsFocus());
    }

    [Fact]
    public void SettingsSubPages_AreNotPerVisit()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(SettingsPage)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(SettingsDataPage)));
        Assert.True(typeof(ISettingsNavigationTarget).IsAssignableFrom(typeof(SettingsPage)));
        Assert.True(typeof(ISettingsNavigationTarget).IsAssignableFrom(typeof(SettingsDataPage)));
    }

    private sealed class World
    {
        private readonly Dictionary<Type, SettingsTarget> _singletons = [];

        public World()
        {
            var provider = new ServiceCollection().BuildServiceProvider();
            Navigation = new NavigationService(provider, NullLogger<NavigationService>.Instance, CreatePage);
            Navigation.Initialize(Host);
            Navigation.NavigatedAwayFromOverview += (_, _) => NavigatedAway++;
        }

        public NavigationService Navigation { get; }

        public RecordingHost Host { get; } = new();

        public int NavigatedAway { get; private set; }

        public SettingsTarget Shown => Assert.IsType<SettingsTarget>(Host.Content);

        // Singletons, like the container: the same instance for every request of a type.
        private object CreatePage(Type type)
        {
            if (!_singletons.TryGetValue(type, out var page))
            {
                page = new SettingsTarget(type, this);
                _singletons[type] = page;
            }

            return page;
        }

        /// <summary>Mirrors the two pages' code-behind: General consumes Background, Data consumes About.</summary>
        internal sealed class RecordingHost : INavigationHost
        {
            public object? Content { get; set; }
        }

        internal sealed class SettingsTarget(Type pageType, World world) : ISettingsNavigationTarget
        {
            public Type PageType { get; } = pageType;

            public int NotifiedAgain { get; private set; }

            public bool BackgroundConsumed { get; private set; }

            public bool AboutConsumed { get; private set; }

            /// <summary>Models the page's Loaded (production: IsReadyForSectionRequest => IsLoaded).</summary>
            public bool IsReadyForSectionRequest { get; private set; }

            public void OnNavigatedToAgain()
            {
                NotifiedAgain++;
                Consume();
            }

            /// <summary>The page's Loaded: it becomes ready and consumes what is pending (as the code-behind does).</summary>
            public void SimulateLoaded()
            {
                IsReadyForSectionRequest = true;
                Consume();
            }

            private void Consume()
            {
                if (PageType == typeof(SettingsPage))
                {
                    BackgroundConsumed |= world.Navigation.ConsumeBackgroundSettingsFocus();
                }
                else
                {
                    AboutConsumed |= world.Navigation.ConsumeAboutSettingsFocus();
                }
            }
        }
    }
}
