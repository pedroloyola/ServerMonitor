using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// THEME-1: the saved choice is restored before the first root attaches, only an explicit Settings choice writes it, a
/// failed write is not fatal, and nothing on the High Contrast path writes it.
/// </summary>
public sealed class ThemePersistenceTests
{
    // ---- Restore: before the first Attach -------------------------------------------------------------------------

    [Theory]
    [InlineData(AppThemePreference.System, ElementTheme.Default)]
    [InlineData(AppThemePreference.Light, ElementTheme.Light)]
    [InlineData(AppThemePreference.Dark, ElementTheme.Dark)]
    public void TheSavedChoice_IsCurrent_FromConstruction_SoTheFirstRootAttachesInIt(
        AppThemePreference saved, ElementTheme expected)
    {
        var store = new FakeThemePreferenceStore(saved);

        var service = new ThemeService(NullLogger<ThemeService>.Instance, store);

        // No Apply, no root yet: what Attach would give the FIRST root is already the saved choice.
        Assert.Equal(saved, service.Current);
        Assert.Equal(expected, service.ThemeForNewRoot);
        Assert.Equal(0, service.AttachedRootCount);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public void ASecondRoot_GetsTheSameRestoredValue_AndTheStoreIsReadOnce()
    {
        var store = new FakeThemePreferenceStore(AppThemePreference.Dark);
        var service = new ThemeService(NullLogger<ThemeService>.Instance, store);

        var first = service.ThemeForNewRoot;
        var second = service.ThemeForNewRoot;

        Assert.Equal(ElementTheme.Dark, first);
        Assert.Equal(first, second);
        Assert.Equal(1, store.Reads);
    }

    // ---- Restore: what a root REALLY receives at Attach (Atlas P2-A) ----------------------------------------------

    [Theory]
    [InlineData(AppThemePreference.System, ElementTheme.Default)]
    [InlineData(AppThemePreference.Light, ElementTheme.Light)]
    [InlineData(AppThemePreference.Dark, ElementTheme.Dark)]
    public void TheFirstRoot_ReceivesTheSavedTheme_AtAttach_BeforeAnyApply(AppThemePreference saved, ElementTheme expected)
    {
        var assigned = new List<(object Root, ElementTheme Theme)>();
        var service = new ThemeService(
            NullLogger<ThemeService>.Instance, new FakeThemePreferenceStore(saved), (root, theme) => assigned.Add((root, theme)));
        var dashboard = new object();

        service.AttachRoot(dashboard);

        // Through the same setter the production line sits behind: the theme the root is given, not a property that
        // could disagree with it. Exactly one assignment, for this root, before anything else ran.
        Assert.Equal([(dashboard, expected)], assigned);
    }

    [Fact]
    public void ASecondRoot_ReceivesTheSameSavedTheme_AndALaterApplyReachesBothRoots()
    {
        var assigned = new List<(object Root, ElementTheme Theme)>();
        var service = new ThemeService(
            NullLogger<ThemeService>.Instance,
            new FakeThemePreferenceStore(AppThemePreference.Dark),
            (root, theme) => assigned.Add((root, theme)));
        var dashboard = new object();
        var flyout = new object();

        service.AttachRoot(dashboard);
        service.AttachRoot(flyout);
        service.Apply(AppThemePreference.Light);

        Assert.Equal(
            [(dashboard, ElementTheme.Dark), (flyout, ElementTheme.Dark), (dashboard, ElementTheme.Light), (flyout, ElementTheme.Light)],
            assigned);
    }

    [Fact]
    public void TheProductionConstructor_UsesTheXamlSetter()
    {
        // The public constructor (the one DI uses) must route through XamlRootThemeSetter, whose body is the single
        // RequestedTheme assignment; the internal constructor exists only for the recording setter above.
        var constructor = Assert.Single(typeof(ThemeService).GetConstructors());
        Assert.Equal(
            [typeof(Microsoft.Extensions.Logging.ILogger<ThemeService>), typeof(IThemePreferenceStore)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        var service = new ThemeService(NullLogger<ThemeService>.Instance, new FakeThemePreferenceStore());
        var field = typeof(ThemeService).GetField("_setRootTheme", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.Same(ThemeService.XamlRootThemeSetter, field!.GetValue(service));
    }

    [Fact]
    public void AStoreThatThrows_OrReturnsAnUndefinedValue_RestoresSystem_AndNeverCrashesStartup()
    {
        var throwing = new FakeThemePreferenceStore { ThrowOnRead = new InvalidOperationException("broken store") };
        Assert.Equal(AppThemePreference.System, new ThemeService(NullLogger<ThemeService>.Instance, throwing).Current);

        var undefined = new FakeThemePreferenceStore((AppThemePreference)42);
        Assert.Equal(AppThemePreference.System, new ThemeService(NullLogger<ThemeService>.Instance, undefined).Current);
    }

    [Fact]
    public void TheComposedService_RestoresTheFileWrittenByAnEarlierSession()
    {
        using var composition = new IsolatedAppComposition();
        Directory.CreateDirectory(composition.DataDirectory);
        File.WriteAllBytes(
            Path.Combine(composition.DataDirectory, "theme-settings.json"),
            JsonThemePreferenceStore.Render(AppThemePreference.Light));
        using var provider = composition.BuildProvider();

        var service = Assert.IsType<ThemeService>(provider.GetRequiredService<IThemeService>());

        Assert.Equal(AppThemePreference.Light, service.Current);
        Assert.Equal(ElementTheme.Light, service.ThemeForNewRoot);
        // One singleton: the Dashboard and the tray menu read the same restored value.
        Assert.Same(service, provider.GetRequiredService<IThemeService>());
    }

    [Fact]
    public void TheProductionComposition_RegistersTheJsonStore_NextToTheLanguageFile()
    {
        var descriptors = IsolatedAppComposition.ProductionDescriptors();

        var store = Assert.Single(descriptors, d => d.ServiceType == typeof(IThemePreferenceStore));
        Assert.Equal(typeof(JsonThemePreferenceStore), store.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, store.Lifetime);
        var theme = Assert.IsType<ThemeSettingsStorageOptions>(
            Assert.Single(descriptors, d => d.ServiceType == typeof(ThemeSettingsStorageOptions)).ImplementationInstance).FilePath;
        var language = Assert.IsType<LanguageSettingsStorageOptions>(
            Assert.Single(descriptors, d => d.ServiceType == typeof(LanguageSettingsStorageOptions)).ImplementationInstance).FilePath;
        Assert.Equal("theme-settings.json", Path.GetFileName(theme));
        Assert.Equal(Path.GetDirectoryName(language), Path.GetDirectoryName(theme));
    }

    [Fact]
    public void TheIsolatedComposition_PutsTheFileUnderTheTestRoot()
    {
        using var composition = new IsolatedAppComposition();
        using var provider = composition.BuildProvider();

        Assert.Equal(
            Path.Combine(composition.DataDirectory, "theme-settings.json"),
            provider.GetRequiredService<ThemeSettingsStorageOptions>().FilePath);
        Assert.IsType<JsonThemePreferenceStore>(provider.GetRequiredService<IThemePreferenceStore>());
    }

    // ---- High Contrast / non-user paths never write ---------------------------------------------------------------

    [Fact]
    public void TheThemeService_NeverWrites_NotOnRestore_NotOnApply()
    {
        // HC is applied by Windows over RequestedTheme; the app's reaction (MainWindow.OnActualThemeChanged ->
        // SaThemeRefresh remounts) never goes through the store. The service itself only ever reads.
        var store = new FakeThemePreferenceStore(AppThemePreference.Dark);
        var service = new ThemeService(NullLogger<ThemeService>.Instance, store);

        service.Apply(AppThemePreference.Light);
        service.Apply(AppThemePreference.System);

        Assert.Empty(store.Writes);
        Assert.Equal(AppThemePreference.Dark, store.Saved);
    }

    [Fact]
    public void OnlyTheThemeServiceAndSettings_DependOnTheStore()
    {
        // Who CAN write: every App type that takes the store. A new consumer (an HC handler, a tray item) fails here
        // and has to justify itself.
        var consumers = typeof(ThemeService).Assembly.GetTypes()
            .Where(type => type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IThemePreferenceStore))))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([nameof(SettingsViewModel), nameof(ThemeService)], consumers);
    }
}
