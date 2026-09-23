using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// Prism QA A11Y: each host row's UI Automation name is the localized "alias — state" text, never
/// the view-model type name (which is what a ListViewItem falls back to via ToString).
/// </summary>
public sealed class SshConfigHostOptionViewModelTests
{
    private static SshConfigHostOptionViewModel Option(string text, string alias) =>
        new(
            Assert.Single(SshConfigResolver.Import(text, @"C:\Users\tester").Hosts, host => host.Alias == alias),
            new FakeLocalizationService());

    [Fact]
    public void ImportableHost_IsAnnouncedAsImportable()
    {
        var option = Option("Host web\n  HostName 10.0.0.5\n", "web");

        Assert.Equal("web — importable", option.AccessibleName);
    }

    [Fact]
    public void BlockedHost_IsAnnouncedAsBlockedWithTheReason()
    {
        var option = Option("Host via-proxyjump\n  ProxyJump bastion\n", "via-proxyjump");

        Assert.Equal(
            "via-proxyjump — blocked: Requires ProxyJump — not supported yet. Notes: Unsupported: ProxyJump",
            option.AccessibleName);
    }

    [Fact]
    public void AmbiguousHost_IsImportable_AndTheNotesAreAnnouncedOnOneLine()
    {
        var option = Option("Host db\n  IdentityFile ~/.ssh/a\n  IdentityFile ~/.ssh/b\n  ForwardAgent no\n", "db");

        Assert.Equal(
            "db — importable. Notes: Ambiguous: IdentityFile (choose manually); Ignored (not relevant): ForwardAgent",
            option.AccessibleName);
    }

    [Fact]
    public void ToString_IsTheAccessibleName_NeverTheTypeName()
    {
        var option = Option("Host web\n", "web");

        Assert.Equal(option.AccessibleName, option.ToString());
        Assert.DoesNotContain(nameof(SshConfigHostOptionViewModel), option.ToString());
    }
}
