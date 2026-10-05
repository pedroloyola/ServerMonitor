using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using Windows.System;
namespace ServerMonitor.App.Tests.Services;
public sealed class Ui6InteractionTests
{
    [Fact]
    public void NativeClickThenExternalNavigation_ReappliesEveryCheckedValue_AndUnsubscribes()
    {
        var router = new FakeNavigationService();
        using var shell = new ShellViewModel(router);
        var values = new Dictionary<ShellDestination, bool>();
        using var sync = new SidebarSelectionSync((key, value) => values[key] = value);
        sync.Bind(shell);
        router.GoToDashboard();
        values[ShellDestination.Servers] = true; // native RadioButton local change before Click
        shell.Navigate(ShellDestination.Servers);
        router.GoToSettings(SettingsSection.Data);
        Assert.All(values, value => Assert.Equal(value.Key == ShellDestination.Settings, value.Value));
        sync.Bind(null);
        router.GoToDashboard();
        Assert.All(values.Values, Assert.False);
    }
    [Fact]
    public void Enter_NavigatesThroughRouter_AndOtherKeysRemainNative()
    {
        var router = new FakeNavigationService();
        using var shell = new ShellViewModel(router);
        using var sync = new SidebarSelectionSync((_, _) => { });
        sync.Bind(shell);
        Assert.True(sync.ActivateKey(VirtualKey.Enter, ShellDestination.History));
        Assert.Equal(NavigationDestination.History, router.CurrentDestination);
        Assert.False(sync.ActivateKey(VirtualKey.Space, ShellDestination.Settings));
        Assert.Equal(NavigationDestination.History, router.CurrentDestination);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Dialog_ReturnsToOriginOrHeading_Once_EvenAfterExceptionalExit(bool attached)
    {
        var order = new List<string>();
        try
        {
            using var focus = new DialogReturnFocus(() => { order.Add("origin"); return attached; }, () => order.Add("heading"));
            order.Add("dialog");
            throw new InvalidOperationException();
        }
        catch (InvalidOperationException) { }
        Assert.Equal(attached ? new[] { "dialog", "origin" } : new[] { "dialog", "origin", "heading" }, order);
    }
    [Fact]
    public void HeadingName_CollapsesNewlinesAndWhitespace() =>
        Assert.Equal("Seus servidores. Em um só lugar.", SaHeadingHost.NormalizeName("Seus servidores.\r\nEm um só lugar."));
}
