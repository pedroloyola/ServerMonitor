using Microsoft.Windows.AppNotifications;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// The Windows notification platform, made decidable. Only the platform is faked: the service and the
/// presenter under test are the production ones, because M13-QA-12 is precisely a defect in what the
/// production service concluded from this boundary (BOSS.md §10).
/// </summary>
internal sealed class FakeNotificationPlatform : IWindowsAppNotificationPlatform
{
    private EventHandler<NotificationActivationEventArgs>? _invoked;

    public event EventHandler<NotificationActivationEventArgs>? Invoked
    {
        add { _invoked += value; }
        remove { _invoked -= value; }
    }

    public bool Supported { get; init; } = true;

    public AppNotificationSetting Setting { get; init; } = AppNotificationSetting.Enabled;

    /// <summary>When set, <c>Register</c> throws — the M13-QA-12 case, as the real platform can.</summary>
    public bool FailRegistration { get; init; }

    /// <summary>When set, <c>Show</c> throws AFTER a successful registration.</summary>
    public bool FailShow { get; init; }

    public int RegisterCount { get; private set; }

    public int ShowCount { get; private set; }

    /// <summary>True while a handler is attached. A failed registration must not leave one behind.</summary>
    public bool HasHandler => _invoked is not null;

    public bool IsSupported() => Supported;

    public void Register(string displayName, Uri iconUri)
    {
        RegisterCount++;
        if (FailRegistration)
        {
            throw new InvalidOperationException("the notification registration was refused");
        }
    }

    public void Unregister()
    {
    }

    public void Show(
        string title,
        string body,
        IReadOnlyDictionary<string, string> arguments,
        bool expiresOnReboot,
        TimeSpan? expiresAfter)
    {
        ShowCount++;
        if (FailShow)
        {
            throw new InvalidOperationException("the notification could not be displayed");
        }
    }
}
