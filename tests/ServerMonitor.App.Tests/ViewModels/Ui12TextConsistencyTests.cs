using System.Xml.Linq;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.12 TEXT-01 (Prism UI12A): (1) the notifications toggle's accessible name starts with its visible label, so voice
/// control ("click Alertas de estado") reaches it (WCAG 2.5.3 as reference); (2) the Windows notifications use the app's
/// own Offline wording ("Sem ligação" / "Sem conexão" / "No connection") - the app and widget no longer say "offline".
/// </summary>
public sealed class Ui12TextConsistencyTests
{
    private const string ToggleName = "SettingsNotificationsToggle.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    [Theory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void TheToggleNameStartsWithItsVisibleLabel(string language)
    {
        var strings = Strings(language);
        Assert.StartsWith(strings["SettingsNotificationsTitle.Text"], strings[ToggleName], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pt-PT", "Ligação restabelecida")]
    [InlineData("pt-BR", "Conexão restabelecida")]
    [InlineData("en-US", "Connection restored")]
    public void NotificationsUseTheAppsOfflineWording(string language, string recovered)
    {
        var strings = Strings(language);
        Assert.Equal(strings["ServerStatusOffline"], strings["NotificationOfflineTitle"]);
        Assert.Equal(recovered, strings["NotificationRecoveryTitle"]);
        foreach (var key in new[] { "NotificationOfflineTitle", "NotificationRecoveryTitle", "NotificationRecoveryOnlineBodyFormat" })
        {
            Assert.DoesNotContain("online", strings[key], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("offline", strings[key], StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("{0}", strings["NotificationRecoveryOnlineBodyFormat"], StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Strings(string language) =>
        XDocument.Load(Path.Combine(AppSourceTree.AppRoot, "Resources", language, "Resources.resw"))
            .Descendants("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);
}
