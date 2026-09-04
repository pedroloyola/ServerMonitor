using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// Every asset the packaged manifests actually reference exists (M13-QA-12).
/// <para>
/// This is the contract that replaced a stale runtime gate. A missing manifest asset is a PACKAGE defect
/// and belongs here, where it fails on any test run, rather than inside the notification service, where it
/// used to be re-litigated at every startup against a file no manifest ever wanted. The list is read from
/// the manifests themselves rather than typed out, so an asset added or renamed later is covered without
/// anyone remembering to update this.
/// </para>
/// </summary>
public sealed class ManifestAssetIntegrityTests
{
    [Theory]
    [InlineData("Package.appxmanifest")]
    [InlineData("Package.Dev.appxmanifest")]
    public void Every_asset_the_manifest_references_exists(string manifestName)
    {
        var projectDirectory = ProjectDirectory();
        var manifestPath = Path.Combine(projectDirectory, manifestName);
        Assert.True(File.Exists(manifestPath), $"{manifestName} is missing");

        var referenced = XDocument.Load(manifestPath)
            .Descendants()
            .SelectMany(element => element.Attributes().Select(attribute => attribute.Value))
            .Where(value => value.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(referenced);

        foreach (var relative in referenced)
        {
            var assetPath = Path.Combine(projectDirectory, relative.Replace('\\', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(assetPath), $"{manifestName} references a missing asset: {relative}");
        }
    }

    /// <summary>
    /// The obsolete notification icon is NOT one of them. Recorded so that if it ever becomes a manifest
    /// asset again, that happens deliberately and this test says so.
    /// </summary>
    [Fact]
    public void The_obsolete_notification_icon_is_not_a_manifest_asset()
    {
        foreach (var manifestName in new[] { "Package.appxmanifest", "Package.Dev.appxmanifest" })
        {
            var manifest = File.ReadAllText(Path.Combine(ProjectDirectory(), manifestName));

            Assert.DoesNotContain("ServerMonitorNotification.png", manifest, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Walks up from the test output to the application project that owns the manifests.</summary>
    private static string ProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ServerMonitor.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The ServerMonitor.App project directory could not be located.");
    }
}
