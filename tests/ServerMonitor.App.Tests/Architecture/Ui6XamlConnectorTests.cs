using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

public sealed class Ui6XamlConnectorTests
{
    public static IEnumerable<object[]> Pages => new[] { "Views/DashboardPage", "Views/ServersPage", "Views/ServerDetailPage", "Views/HistoryPage", "Views/WorkloadsPage", "Views/SettingsPage", "Views/SettingsDataPage", "Controls/OnboardingView" }.Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(Pages))]
    public void HeadingChild_StartsOnItsOwnLine_ForXamlConnectionInsertion(string page)
    {
        var doc = XDocument.Load(AppSourceTree.Full(page + ".xaml"), LoadOptions.SetLineInfo);
        foreach (var host in doc.Descendants().Where(e => e.Name.LocalName == "SaHeadingHost"))
            Assert.All(host.Elements(), child => Assert.NotEqual(((IXmlLineInfo)host).LineNumber, ((IXmlLineInfo)child).LineNumber));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void GeneratedConnectionTargets_MatchTheirConnectorCasts(string page)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var generated = Path.Combine(AppSourceTree.AppRoot, "obj", "x64", configuration, "net10.0-windows10.0.19041.0", "win-x64", page);
        var doc = XDocument.Load(generated + ".xaml");
        var code = File.ReadAllText(generated + ".g.cs");
        var casts = Regex.Matches(code, @"case (\d+):[^\{]*\{\s*(?:(?!break;).)*?CastExtensions.As<global::([\w.]+)>\(target\)", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Split('.').Last());
        Assert.NotEmpty(casts);
        foreach (var element in doc.Descendants())
        {
            var id = (string?)element.Attribute(AppSourceTree.Xaml + "ConnectionId");
            if (id is not null && casts.TryGetValue(id, out var expected))
                Assert.True(expected == element.Name.LocalName, $"{page}: connection {id} targets {element.Name.LocalName}, connector casts {expected}.");
        }
    }

    [Fact]
    public void TemporaryFirstChanceProbe_IsRemoved()
    {
        Assert.False(File.Exists(AppSourceTree.Full("Qa/Ui6DetailFirstChanceDiagnostic.cs")));
        Assert.DoesNotContain("Ui6DetailFirstChanceDiagnostic", File.ReadAllText(AppSourceTree.Full("App.xaml.cs")));
    }
}
