using System.Xml.Linq;
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
        var option = Option("Host via-proxyjump\n  ProxyJump bastion,outer\n", "via-proxyjump");

        Assert.Equal(
            "via-proxyjump — blocked: Requires more than one jump host — not supported. Notes: Unsupported: ProxyJump",
            option.AccessibleName);
    }

    [Theory]
    [InlineData("ProxyJump a,b", "Requires more than one jump host — not supported")]
    [InlineData("ProxyJump ssh://bastion", "The ProxyJump value cannot be read exactly — not imported")]
    [InlineData("ProxyJump target", "The ProxyJump loops back to this host — not imported")]
    [InlineData("ProxyJump bastion\nHost bastion\n  HostName %h.corp", "The jump host's HostName cannot be resolved exactly — not imported")]
    public void EveryJumpBlocker_HasItsOwnReason(string lines, string reason)
    {
        var option = Option($"Host target\n  {lines}\n", "target");

        Assert.Equal(reason, option.RequirementText);
    }

    [Fact]
    public void SingleHopJumpHost_IsImportable_AndPreviewsItsRoute()
    {
        var option = Option(
            "Host inner\n  HostName 10.1.0.9\n  ProxyJump bastion\nHost bastion\n  IdentityFile ~/.ssh/a\n  IdentityFile ~/.ssh/b\n",
            "inner");

        Assert.True(option.IsImportable);
        Assert.False(option.HasRequirement);
        Assert.EndsWith("Via: bastion", option.Preview);
        Assert.Equal(
            "inner — importable. Notes: Jump host bastion: Ambiguous: IdentityFile (choose manually)",
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

    // M14.4c QA LOW-1: a host with nothing to preview (HostName %h is not resolved) never shows a
    // blank preview line with an empty UIA name; the line is omitted and the row is still named.
    [Fact]
    public void HostWithNothingToPreview_OmitsThePreviewLine_AndIsStillNamed()
    {
        var option = Option("Host tok-bastion\n  HostName %h\n", "tok-bastion");

        Assert.True(option.IsImportable);
        Assert.False(option.HasPreview);
        Assert.Equal("tok-bastion — importable. Notes: Unsupported: HostName", option.AccessibleName);
    }

    [Fact]
    public void HostWithValues_ShowsThePreviewLine()
    {
        var option = Option("Host web\n  HostName 10.0.0.5\n", "web");

        Assert.True(option.HasPreview);
        Assert.False(string.IsNullOrWhiteSpace(option.Preview));
    }

    [Fact]
    public void ThePreviewLine_IsCollapsedByHasPreview_InTheHostList()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        // UI.7B: the host list lives in the "Importar de SSH" layer panel (x:Bind to the same option view model).
        var xaml = XDocument.Load(Path.Combine(
            FindRepositoryRoot(), "src", "ServerMonitor.App", "Views", "SshConfigImportPanel.xaml"));

        var preview = Assert.Single(
            xaml.Descendants(presentation + "TextBlock"),
            block => (string?)block.Attribute("Text") == "{x:Bind Preview}");

        Assert.Equal("{x:Bind HasPreview}", (string?)preview.Attribute("Visibility"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate ServerMonitor.slnx from test output.");
    }
}
