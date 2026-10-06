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

    [Fact]
    public void EveryGeneratedConnector_MatchesEveryTargetCast_IncludingBindings()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        // Canonical solution x64/RID/TFM. Review this parser when upgrading WindowsAppSDK:
        // unknown cast syntax, missing XAML/id or disagreeing casts must fail closed.
        var generated = Path.Combine(AppSourceTree.AppRoot, "obj", "x64", configuration, "net10.0-windows10.0.19041.0", "win-x64");
        var files = Directory.GetFiles(generated, "*.g.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var checkedCasts = 0;
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            var all = TargetCasts(code).ToArray();
            if (all.Length == 0) continue;
            var xaml = file[..^5] + ".xaml";
            Assert.True(File.Exists(xaml), $"Missing generated XAML for {file}");
            var doc = XDocument.Load(xaml);
            var elements = doc.Descendants().Where(e => e.Attribute(AppSourceTree.Xaml + "ConnectionId") is not null)
                .ToDictionary(e => (string)e.Attribute(AppSourceTree.Xaml + "ConnectionId")!);
            var checkedInFile = 0;
            var typesById = new Dictionary<string, string>();
            foreach (Match block in Regex.Matches(code, @"case (?<id>\d+):(?<body>.*?)(?=\bcase\s|\bdefault\s*:|\z)", RegexOptions.Singleline))
            {
                foreach (var type in TargetCasts(block.Groups["body"].Value))
                {
                    var id = block.Groups["id"].Value;
                    Assert.True(elements.ContainsKey(id), $"{file}: cast connection {id} has no XAML target");
                    if (typesById.ContainsKey(id)) Assert.Equal(typesById[id], type);
                    typesById[id] = type;
                    Assert.True(elements[id].Name.LocalName == type,
                        $"{file}: connection {id} targets {elements[id].Name.LocalName}, connector casts {type}");
                    checkedInFile++;
                }
            }
            Assert.Equal(all.Length, checkedInFile);
            checkedCasts += checkedInFile;
        }
        Assert.True(checkedCasts > 0);
    }

    private static IEnumerable<string> TargetCasts(string code)
    {
        // Match all target casts first, then reject unrecognised syntax rather than ignoring it.
        Assert.DoesNotMatch(@"\btarget\s+as\b", code);
        foreach (Match cast in Regex.Matches(code, @"(?:[\w.:]+(?:<[^;\n]+?>)?\s*\(\s*target\s*\)|\([^();\n]+\)\s*target)"))
        {
            var match = Regex.Match(cast.Value, @"^(?:global::)?WinRT\.CastExtensions\.As<global::(?<type>[\w.]+)>\(target\)$|^\(global::(?<type>[\w.]+)\)target$");
            Assert.True(match.Success, $"Unrecognised generated target cast: {cast.Value}");
            yield return match.Groups["type"].Value.Split('.').Last();
        }
    }

    [Fact]
    public void TemporaryFirstChanceProbe_IsRemoved()
    {
        Assert.False(File.Exists(AppSourceTree.Full("Qa/Ui6DetailFirstChanceDiagnostic.cs")));
        Assert.DoesNotContain("Ui6DetailFirstChanceDiagnostic", File.ReadAllText(AppSourceTree.Full("App.xaml.cs")));
    }
}
