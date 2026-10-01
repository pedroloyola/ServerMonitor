using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// The Debug-only <c>--qa-*</c> harnesses (src/ServerMonitor.App/Qa/**) must never ship. Three independent
/// locks keep them out of Release, and this guard pins all three: the app csproj compile-removes Qa/** outside
/// Debug, every <c>Qa.</c> reference in App.xaml.cs sits inside <c>#if DEBUG</c>, and the built assembly
/// carries QA types only in a Debug build. Lives in Architecture/ (not Qa/) so it also compiles in Release.
/// </summary>
public sealed partial class QaReleaseExclusionGuardTests
{
    private const string QaNamespace = "ServerMonitor.App.Qa";

    [Theory]
    [InlineData("src/ServerMonitor.App/ServerMonitor.App.csproj")]
    [InlineData("tests/ServerMonitor.App.Tests/ServerMonitor.App.Tests.csproj")]
    public void ProjectCompileRemovesQaSourcesOutsideDebug(string project)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppSourceTree.AppRoot, "..", ".."));
        var document = XDocument.Load(Path.Combine(repositoryRoot, project));

        var removal = Assert.Single(document.Descendants("Compile"), e => (string?)e.Attribute("Remove") == @"Qa\**\*.cs");
        var condition = removal.Ancestors("ItemGroup").Select(e => (string?)e.Attribute("Condition")).Single();
        Assert.Equal("'$(Configuration)' != 'Debug'", condition);
    }

    [Fact]
    public void EveryQaReferenceInTheCompositionRootIsInsideIfDebug()
    {
        var lines = File.ReadAllLines(AppSourceTree.Full("App.xaml.cs"));
        var depth = 0;
        var debugDepths = new Stack<bool>();
        var outside = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#if", StringComparison.Ordinal))
            {
                debugDepths.Push(line == "#if DEBUG");
                depth++;
                continue;
            }

            if (line.StartsWith("#else", StringComparison.Ordinal) || line.StartsWith("#elif", StringComparison.Ordinal))
            {
                debugDepths.Pop();
                debugDepths.Push(false); // the #else of #if DEBUG is the Release branch
                continue;
            }

            if (line.StartsWith("#endif", StringComparison.Ordinal))
            {
                debugDepths.Pop();
                depth--;
                continue;
            }

            if (!line.StartsWith("//", StringComparison.Ordinal) && QaReference().IsMatch(line) && !debugDepths.Contains(true))
            {
                outside.Add($"App.xaml.cs:{i + 1}: {line}");
            }
        }

        Assert.Equal(0, depth);
        Assert.True(outside.Count == 0,
            "Debug-only QA wiring referenced outside #if DEBUG (it would break or ship in Release):" +
            Environment.NewLine + string.Join(Environment.NewLine, outside));
    }

    [Fact]
    public void BuiltAssemblyCarriesQaTypesOnlyInDebug()
    {
        // Prefix, not equality: Qa.Gallery (UI.2) is a Qa namespace too (Vigil info i).
        var qaTypes = typeof(App).Assembly.GetTypes().Where(t => t.Namespace?.StartsWith(QaNamespace, StringComparison.Ordinal) == true).ToList();
#if DEBUG
        Assert.NotEmpty(qaTypes);
#else
        Assert.Empty(qaTypes);
#endif
    }

    [GeneratedRegex(@"\bQa\.[A-Z]")]
    private static partial Regex QaReference();
}
