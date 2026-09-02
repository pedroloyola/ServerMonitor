using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Alerts;

namespace ServerMonitor.App.Tests.Services;

public sealed class WindowsAppNotificationServiceTests : IDisposable
{
    private readonly string _iconPath = Path.Combine(
        Path.GetTempPath(),
        $"server-monitor-notification-{Guid.NewGuid():N}.png");

    public WindowsAppNotificationServiceTests() => File.WriteAllBytes(_iconPath, [0x89, 0x50, 0x4e, 0x47]);

    [Fact]
    public void Platform_DoesNotResolveDefaultManagerBeforeCapabilityGatePasses()
    {
        var managerFactoryCalled = false;
        var platform = new WindowsAppNotificationPlatform(
            () => false,
            () =>
            {
                managerFactoryCalled = true;
                throw new InvalidOperationException("The unavailable Singleton must not be resolved.");
            });

        Assert.False(platform.IsSupported());
        Assert.False(managerFactoryCalled);
    }

    [Fact]
    public async Task Start_RegistersOnceWithHandlerAlreadyAttached()
    {
        var platform = new FakePlatform();
        var service = Create(platform, new FakeWindowController());

        await service.StartAsync(default);
        await service.StartAsync(default);

        Assert.Equal(1, platform.RegisterCount);
        Assert.True(platform.HandlerWasAttachedAtRegister);
        Assert.Equal("ServerAlyzer", platform.DisplayName);
        Assert.Equal(new Uri(_iconPath), platform.IconUri);
    }

    [Fact]
    public async Task UnsupportedPlatform_DoesNotRegisterOrThrow()
    {
        var platform = new FakePlatform { Supported = false };
        var service = Create(platform, new FakeWindowController());

        await service.StartAsync(default);
        await service.ShowAsync(Notification());

        Assert.Equal(0, platform.RegisterCount);
        Assert.Equal(0, platform.ShowCount);
    }

    [Fact]
    public async Task DisabledOsSetting_SuppressesNotification()
    {
        var platform = new FakePlatform { Setting = AppNotificationSetting.DisabledForUser };
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);

        await service.ShowAsync(Notification());

        Assert.Equal(0, platform.ShowCount);
    }

    [Fact]
    public async Task Show_PassesOnlyPreparedTitleAndBody()
    {
        var platform = new FakePlatform();
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);

        await service.ShowAsync(Notification());

        Assert.Equal(1, platform.ShowCount);
        Assert.Equal("Offline", platform.Title);
        Assert.Equal("Server unavailable", platform.Body);
    }

    [Fact]
    public async Task NotificationClick_RestoresSameWindowUntilShutdown()
    {
        var platform = new FakePlatform();
        var window = new FakeWindowController();
        var service = Create(platform, window);
        await service.StartAsync(default);

        platform.RaiseInvoked();
        await service.StopAsync(default);
        platform.RaiseInvoked();

        Assert.Equal(1, window.RestoreCount);
        Assert.Equal(1, platform.UnregisterCount);
    }

    [Fact]
    public async Task RepeatedStop_UnregistersOnceAndSuppressesCallbacks()
    {
        var platform = new FakePlatform();
        var service = Create(platform, new FakeWindowController());
        await service.StartAsync(default);

        await service.StopAsync(default);
        await service.StopAsync(default);
        await service.ShowAsync(Notification());

        Assert.Equal(1, platform.UnregisterCount);
        Assert.Equal(0, platform.ShowCount);
    }

    [Fact]
    public async Task BeginShutdown_SynchronouslySuppressesDeliveryAndActivationBeforeUnregister()
    {
        var platform = new FakePlatform();
        var window = new FakeWindowController();
        var service = Create(platform, window);
        await service.StartAsync(default);

        service.BeginShutdown();
        await service.ShowAsync(Notification());
        platform.RaiseInvoked();

        Assert.Equal(0, platform.ShowCount);
        Assert.Equal(0, window.RestoreCount);
        await service.StopAsync(default);
        Assert.Equal(1, platform.UnregisterCount);
    }

    private WindowsAppNotificationService Create(
        IWindowsAppNotificationPlatform platform,
        IApplicationWindowController window,
        IAppLifecycleController? lifecycle = null) => new(
            platform,
            window,
            lifecycle ?? new FakeAppLifecycleController(),
            NullLogger<WindowsAppNotificationService>.Instance,
            _iconPath);

    private static UserNotification Notification() => new(
        Guid.NewGuid(),
        ServerAlertCategory.Offline,
        "Offline",
        "Server unavailable");

    public void Dispose()
    {
        if (File.Exists(_iconPath))
        {
            File.Delete(_iconPath);
        }
    }

    private sealed class FakePlatform : IWindowsAppNotificationPlatform
    {
        private EventHandler<NotificationActivationEventArgs>? _invoked;

        public event EventHandler<NotificationActivationEventArgs>? Invoked
        {
            add { _invoked += value; }
            remove { _invoked -= value; }
        }

        public bool Supported { get; init; } = true;
        public AppNotificationSetting Setting { get; init; } = AppNotificationSetting.Enabled;
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public int ShowCount { get; private set; }
        public bool HandlerWasAttachedAtRegister { get; private set; }
        public string? DisplayName { get; private set; }
        public Uri? IconUri { get; private set; }
        public string? Title { get; private set; }
        public string? Body { get; private set; }

        public bool IsSupported() => Supported;

        public void Register(string displayName, Uri iconUri)
        {
            RegisterCount++;
            HandlerWasAttachedAtRegister = _invoked is not null;
            DisplayName = displayName;
            IconUri = iconUri;
        }

        public void Unregister() => UnregisterCount++;

        public IReadOnlyDictionary<string, string>? LastArguments { get; private set; }

        public bool LastExpiresOnReboot { get; private set; }

        public void Show(
            string title,
            string body,
            IReadOnlyDictionary<string, string> arguments,
            bool expiresOnReboot)
        {
            ShowCount++;
            Title = title;
            Body = body;
            LastArguments = arguments;
            LastExpiresOnReboot = expiresOnReboot;
        }

        /// <summary>Raises an activation carrying the health contract, which is what these tests exercise.</summary>
        public void RaiseInvoked() =>
            RaiseInvoked(NotificationActivationContract.ForServerHealth());

        public void RaiseInvoked(IReadOnlyDictionary<string, string>? arguments) =>
            _invoked?.Invoke(this, new NotificationActivationEventArgs(arguments));
    }

    private sealed class FakeWindowController : IApplicationWindowController
    {
        public bool IsAttached => true;
        public int RestoreCount { get; private set; }

        public void Attach(Window window) { }

        public bool IsMaterialized => true;

        public void AttachWindowFactory(Func<Window> factory) { }

        public void HideToBackground() => HideToBackgroundCount++;

        public int HideToBackgroundCount { get; private set; }

        public void OpenBackgroundSettings() => OpenBackgroundSettingsCount++;

        public int OpenBackgroundSettingsCount { get; private set; }
        public void HideForMinimize() { }
        public void RestoreAndActivate() => RestoreCount++;
        public void OpenSettings() { }
        public void ToggleCompactMode() { }
        public void RequestClose() { }
        public void BeginShutdown() { }
    }
}
