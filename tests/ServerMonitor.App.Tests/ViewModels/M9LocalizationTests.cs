using System.Xml.Linq;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class M9LocalizationTests
{
    private static readonly string[] Cultures = ["pt-BR", "pt-PT", "en-US"];

    private static readonly string[] RequiredKeys =
    [
        "TrayCompactModeMenuItem",
        "DashboardCompactModeButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "DashboardCompactModeButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip",
        // UI.8 (RC-9 / Prism R-9): the switch has a visible label now (its tooltip is gone); the legacy card's "Offline"
        // (CompactMetricsUnavailable) is gone with the card; the rest is the Compact Mode copy.
        "CompactAlwaysOnTopToggle.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "CompactAlwaysOnTopLabel.Text",
        "CompactExpandButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "CompactExpandButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip",
        "CompactExpandButtonText.Text",
        "CompactEmptyTitle.Text",
        "CompactEmptyBody.Text",
        "CompactEmptyAction.Content",
        "CompactConfigUnavailableTitle.Text",
        "CompactMetricCpuLabel.Text",
        "CompactMetricMemoryLabel.Text",
        "CompactMetricDiskLabel.Text",
        "CompactRowAccessibleFormat",
        "CompactMetricAttentionAccessible",
        "CompactMetricCriticalAccessible",
        "SettingsCompactTitle.Text",
        "SettingsCompactDescription.Text",
        "SettingsEnterCompactButtonText.Text",
        "SettingsEnterCompactButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "SettingsAlwaysOnTopToggle.Header",
        "SettingsAlwaysOnTopToggle.OnContent",
        "SettingsAlwaysOnTopToggle.OffContent",
        "SettingsAlwaysOnTopToggle.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
    ];

    [Fact]
    public void M9Resources_ArePresentAndNonEmptyInEverySupportedCulture()
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
