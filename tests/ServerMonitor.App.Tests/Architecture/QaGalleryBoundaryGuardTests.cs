using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.2 S2 gallery boundary. Lives in Architecture/ (not Qa/) so it also compiles - and the assembly check runs -
/// in the Release test run (release-build.yml).
/// <list type="bullet">
/// <item>T-15: the gallery's XAML pages are removed from Release as well as its C# (a Qa page left in Release
/// would generate InitializeComponent/XamlTypeInfo for code-behind that no longer exists), and a Release
/// assembly carries no type from any ServerMonitor.App.Qa* namespace.</item>
/// <item>T-16: in App.xaml.cs the gallery branch returns BEFORE the host is built, and OnLaunched returns to the
/// gallery BEFORE StartupRestoreRecovery and OrphanTemporaryCleaner touch any user data.</item>
/// </list>
/// </summary>
public sealed class QaGalleryBoundaryGuardTests
{
    [Fact]
    public void GalleryIsExcludedFromRelease()
    {
        var document = XDocument.Load(AppSourceTree.Full("ServerMonitor.App.csproj"));

        foreach (var (item, pattern) in new[] { ("Compile", @"Qa\**\*.cs"), ("Page", @"Qa\**\*.xaml") })
        {
            var removal = Assert.Single(document.Descendants(item), e => (string?)e.Attribute("Remove") == pattern);
            var condition = removal.Ancestors("ItemGroup").Select(e => (string?)e.Attribute("Condition")).Single();
            Assert.Equal("'$(Configuration)' != 'Debug'", condition);
        }
    }

    [Fact]
    public void ReleaseAssemblyContainsNoQaTypes()
    {
        var qaTypes = typeof(App).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("ServerMonitor.App.Qa", StringComparison.Ordinal) == true)
            .ToList();
#if DEBUG
        Assert.Contains(qaTypes, type => type.Namespace == "ServerMonitor.App.Qa.Gallery");
#else
        Assert.Empty(qaTypes);
#endif
    }

    [Fact]
    public void GalleryShortCircuitPrecedesHostConstruction()
    {
        var code = AppSourceTree.CodeWithoutComments("App.xaml.cs");

        var constructor = Body(code, "public App()");
        var router = IndexOf(constructor, "_activationRouter = new ActivationRouter(");
        var gallery = IndexOf(constructor, "QaGalleryComposition.IsRequested()");
        var host = IndexOf(constructor, ".CreateDefaultBuilder(");
        var branchEnd = constructor.IndexOf("#endif", gallery, StringComparison.Ordinal);
        Assert.True(branchEnd > gallery && branchEnd < host, "The gallery branch must be a closed #if DEBUG block before the host is built.");
        var galleryBranch = constructor[gallery..branchEnd];
        Assert.True(router < gallery, "The activation router must be built before the gallery branch (the branch returns early).");
        Assert.True(gallery < host, "The gallery branch must come before the host is built.");
        Assert.Contains("return;", galleryBranch, StringComparison.Ordinal);
        Assert.Contains("InitializeComponent();", galleryBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("ServicesHost", galleryBranch, StringComparison.Ordinal);

        var launched = Body(code, "protected override async void OnLaunched(");
        var launch = IndexOf(launched, "QaGalleryComposition.Launch()");
        var recovery = IndexOf(launched, "StartupRestoreRecovery.RunAsync(");
        var cleaner = IndexOf(launched, "OrphanTemporaryCleaner");
        Assert.True(launch < recovery && launch < cleaner,
            "OnLaunched must hand over to the gallery before StartupRestoreRecovery and OrphanTemporaryCleaner.");
        Assert.Contains("return;", launched[launch..recovery], StringComparison.Ordinal);
    }

    private static int IndexOf(string code, string marker)
    {
        var index = code.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"App.xaml.cs no longer contains '{marker}'");
        return index;
    }

    /// <summary>The brace-balanced body that follows <paramref name="signature"/>.</summary>
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf('{', IndexOf(code, signature));
        var depth = 0;
        for (var index = start; index < code.Length; index++)
        {
            depth += code[index] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return code[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Unbalanced body after '{signature}'.");
    }
}
