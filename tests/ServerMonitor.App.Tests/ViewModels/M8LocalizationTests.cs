using System.Xml.Linq;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class M8LocalizationTests
{
    private static readonly string[] Cultures = ["pt-BR", "pt-PT", "en-US"];

    private static readonly string[] RequiredKeys =
    [
        "SettingsNotificationsTitle.Text",
        "SettingsNotificationsDescription.Text",
        "SettingsNotificationsToggle.Header",
        "SettingsNotificationsToggle.OnContent",
        "SettingsNotificationsToggle.OffContent",
        "SettingsNotificationsToggle.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "SettingsNotificationsToggle.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.HelpText",
        "SettingsNotificationsSaveError.Title",
        "SettingsNotificationsSaveError.Message",
        "TrayToolTip",
        "TrayOpenMenuItem",
        "TrayRefreshAllMenuItem",
        "TraySettingsMenuItem",
        "TrayExitMenuItem",
        "NotificationServerFallbackName",
        "NotificationWarningTitle",
        "NotificationWarningBodyFormat",
        "NotificationCriticalTitle",
        "NotificationCriticalBodyFormat",
        "NotificationOfflineTitle",
        "NotificationOfflineBodyFormat",
        "NotificationRecoveryTitle",
        "NotificationRecoveryOnlineBodyFormat",
        "NotificationHealthyTitle",
        "NotificationHealthyBodyFormat",
        "NotificationPlatformUnavailableTitle",
        "NotificationPlatformUnavailableMessage"
    ];

    [Fact]
    public void M8Resources_ArePresentAndNonEmptyInEverySupportedCulture()
    {
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var key in RequiredKeys)
            {
                Assert.True(resources.TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture} has an empty {key}.");
            }
        }
    }

    [Fact]
    public void NotificationBodies_KeepSingleServerNamePlaceholder()
    {
        var bodyKeys = RequiredKeys.Where(key => key.EndsWith("BodyFormat", StringComparison.Ordinal));
        foreach (var culture in Cultures)
        {
            var resources = LoadResources(culture);
            foreach (var key in bodyKeys)
            {
                Assert.Equal(1, Count(resources[key], "{0}"));
                Assert.DoesNotContain("{1}", resources[key], StringComparison.Ordinal);
            }
        }
    }

    private static int Count(string value, string needle) =>
        (value.Length - value.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;

    private static IReadOnlyDictionary<string, string> LoadResources(string culture)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ServerMonitor.App",
            "Resources",
            culture,
            "Resources.resw");
        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ServerMonitor.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
