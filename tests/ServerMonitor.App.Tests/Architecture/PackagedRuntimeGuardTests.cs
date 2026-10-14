using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// REL.0C source-level guard: the production MSIX carries its own .NET runtime. Windows does not ship .NET 10
/// and the Store has no .NET framework package, so a framework-dependent package (1.1.2.2) starts only where
/// somebody installed .NET 10 machine-wide. The Windows App Runtime, by contrast, stays a framework package
/// (ADR-017). This is the cheap per-PR signal; the binding proof is tools/release/Assert-PackageRuntime.ps1
/// on the built .msix (release-build.yml and the CI package-privacy job).
/// </summary>
public sealed class PackagedRuntimeGuardTests
{
    private const string PackagedCondition = "'$(Packaged)' == 'true'";

    private static XDocument AppProject() => XDocument.Load(AppSourceTree.Full("ServerMonitor.App.csproj"));

    private static XDocument ProviderProject() => XDocument.Load(Path.Combine(
        AppSourceTree.RepositoryRoot, "src", "ServerMonitor.WidgetProvider", "ServerMonitor.WidgetProvider.csproj"));

    private static XElement PackagedPropertyGroup(XDocument project) => Assert.Single(
        project.Root!.Elements("PropertyGroup"), group => (string?)group.Attribute("Condition") == PackagedCondition);

    [Fact]
    public void Packaged_app_is_self_contained_for_dotnet_and_framework_dependent_for_the_windows_app_runtime()
    {
        var group = PackagedPropertyGroup(AppProject());

        Assert.Equal("true", Assert.Single(group.Elements("SelfContained")).Value);
        Assert.Equal("false", Assert.Single(group.Elements("WindowsAppSDKSelfContained")).Value);
    }

    [Fact]
    public void Packaged_app_targets_one_runtime_identifier()
    {
        // A self-contained build needs a RID; the app pins win-x64 unconditionally (ADR-017 support matrix).
        var rid = Assert.Single(AppProject().Root!.Elements("PropertyGroup").Elements("RuntimeIdentifier"));
        Assert.Equal("win-x64", rid.Value);
        Assert.Null(rid.Parent!.Attribute("Condition"));
    }

    [Fact]
    public void Widget_provider_is_published_self_contained_with_the_apps_runtime_identifier_and_dotnet_host()
    {
        var stage = Assert.Single(AppProject().Root!.Elements("Target"),
            target => (string?)target.Attribute("Name") == "StageWidgetProviderForPackage");
        var command = (string?)Assert.Single(stage.Elements("Exec")).Attribute("Command");

        Assert.NotNull(command);
        Assert.StartsWith("\"$(WidgetProviderDotnet)\" publish ", command, StringComparison.Ordinal);
        Assert.Contains(" -p:SelfContained=true ", command, StringComparison.Ordinal);
        Assert.Contains(" -p:RuntimeIdentifier=$(RuntimeIdentifier) ", command, StringComparison.Ordinal);
        Assert.Contains(" -p:WindowsAppSDKSelfContained=false ", command, StringComparison.Ordinal);
        Assert.DoesNotContain("SelfContained=false -", command.Replace("WindowsAppSDKSelfContained=false", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        var host = stage.Elements("PropertyGroup").Elements("WidgetProviderDotnet").ToList();
        Assert.Equal("$(DOTNET_HOST_PATH)", host[0].Value);
        Assert.Null(host[0].Attribute("Condition"));
    }

    [Fact]
    public void Console_subsystem_createdump_is_dropped_from_both_packaged_runtime_payloads()
    {
        AssertDropsCreateDump(AppProject(), PackagedCondition);
        AssertDropsCreateDump(ProviderProject(), "'$(WindowsPackageType)' == 'MSIX' and '$(SelfContained)' == 'true'");
    }

    private static void AssertDropsCreateDump(XDocument project, string condition)
    {
        var target = Assert.Single(project.Root!.Elements("Target"),
            t => (string?)t.Attribute("Name") == "ExcludeCreateDumpFromPackage");

        Assert.Equal("ResolveRuntimePackAssets", (string?)target.Attribute("AfterTargets"));
        Assert.Equal(condition, (string?)target.Attribute("Condition"));
        foreach (var item in new[] { "RuntimePackAsset", "ReferenceCopyLocalPaths" })
        {
            var removal = Assert.Single(target.Elements("ItemGroup").Elements(item));
            Assert.Equal($"@({item})", (string?)removal.Attribute("Remove"));
            Assert.Equal("'%(Filename)%(Extension)' == 'createdump.exe'", (string?)removal.Attribute("Condition"));
        }
    }
}
