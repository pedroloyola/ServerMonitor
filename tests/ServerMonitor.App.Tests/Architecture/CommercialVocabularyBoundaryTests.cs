using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ServerMonitor.Collectors;
using ServerMonitor.Core.Models;
using ServerMonitor.Features;
using ServerMonitor.Infrastructure;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// C1/C14/C15 — the commercial dimension must not become a second axis in the layers below the shell,
/// and the leaf must stay a leaf.
/// <para>
/// The primary assertions are STRUCTURAL (assembly and project graph), because that is what a compiler
/// can be held to. The textual scan afterwards is defence in depth only: it catches a stray
/// <c>isPro</c>-shaped flag added inside a layer without adding a reference, and it is deliberately never
/// the sole authority for anything.
/// </para>
/// </summary>
public sealed class CommercialVocabularyBoundaryTests
{
    private const string FeaturesAssemblyName = "ServerMonitor.Features";

    /// <summary>
    /// The one project allowed to speak about composition and entitlement, named explicitly. Without this
    /// exception the vocabulary rule would fail against the very assembly that defines the vocabulary.
    /// </summary>
    private const string VocabularyException = "src/ServerMonitor.Features";

    private static readonly Assembly[] LayersThatMustStayEditionBlind =
    [
        typeof(Server).Assembly,                    // ServerMonitor.Core
        typeof(InfrastructureAssembly).Assembly,    // ServerMonitor.Infrastructure
        typeof(CollectorsAssembly).Assembly,        // ServerMonitor.Collectors
        typeof(WidgetSchema).Assembly               // ServerMonitor.WidgetContract
    ];

    // -------------------------------------------------------------- structural: no reference at all

    [Fact]
    public void The_domain_and_infrastructure_layers_do_not_reference_the_features_assembly()
    {
        var offenders = LayersThatMustStayEditionBlind
            .Where(assembly => assembly.GetReferencedAssemblies()
                .Any(reference => string.Equals(
                    reference.Name, FeaturesAssemblyName, StringComparison.OrdinalIgnoreCase)))
            .Select(assembly => assembly.GetName().Name!)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_shell_project_references_the_features_project()
    {
        // Covers what reflection cannot reach from this test assembly, WidgetProvider included.
        var root = FindRepositoryRoot();
        var referencing = new List<string>();

        foreach (var project in Directory.EnumerateFiles(
            Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var references = XDocument.Load(project)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty);

            if (references.Any(include => include.Contains(FeaturesAssemblyName, StringComparison.Ordinal)))
            {
                referencing.Add(Path.GetFileNameWithoutExtension(project));
            }
        }

        Assert.Equal(["ServerMonitor.App"], referencing);
    }

    // -------------------------------------------------------------------------- C1: the leaf is a leaf

    [Fact]
    public void The_features_project_is_a_leaf_with_exactly_one_package_reference()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root, "src", "ServerMonitor.Features", "ServerMonitor.Features.csproj"));

        Assert.Empty(document.Descendants("ProjectReference"));
        Assert.Equal(
            ["Microsoft.Extensions.DependencyInjection.Abstractions"],
            document.Descendants("PackageReference")
                .Select(element => element.Attribute("Include")?.Value));
    }

    [Fact]
    public void Core_still_has_no_package_references_at_all()
    {
        // The whole reason the composition contracts are NOT in Core.
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root, "src", "ServerMonitor.Core", "ServerMonitor.Core.csproj"));

        Assert.Empty(document.Descendants("PackageReference"));
        Assert.Empty(document.Descendants("ProjectReference"));
    }

    // ------------------------------------------------------- defence in depth: no edition-shaped flag

    [Fact]
    public void No_layer_below_the_shell_declares_an_edition_shaped_flag()
    {
        string[] forbidden =
        [
            "IsPro", "isPro", "IsCommunity", "IsProEdition", "ProEdition",
            "IEntitlementProvider", "IsEntitled", "IFeatureCatalog", "IFeatureModule"
        ];

        var root = FindRepositoryRoot();
        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.Combine("bin"), StringComparison.OrdinalIgnoreCase)
                && !path.Contains(Path.Combine("obj"), StringComparison.OrdinalIgnoreCase))
            .Select(path => (Relative: Path.GetRelativePath(root, path).Replace('\\', '/'), Path: path))
            // The shell composes, so it may name these. The leaf DEFINES them, and is the single
            // explicit exception without which this rule would fail against itself.
            .Where(file => !file.Relative.StartsWith("src/ServerMonitor.App/", StringComparison.Ordinal)
                && !file.Relative.StartsWith(VocabularyException, StringComparison.Ordinal))
            .Select(file => (file.Relative, Source: StripComments(File.ReadAllText(file.Path))))
            .SelectMany(file => forbidden
                .Where(token => Regex.IsMatch(file.Source, $@"\b{Regex.Escape(token)}\b"))
                .Select(token => $"{file.Relative}: {token}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    // --------------------------------------------------------------- C15: persisted schemas untouched

    [Fact]
    public void Persisted_schema_versions_are_unchanged_by_M14()
    {
        // ADR-021 E8: a shared schema version is never bumped to prepare for a commercial capability.
        // Pinning the literals here makes an accidental bump a named failure rather than a field report.
        Assert.Equal(1, WidgetSchema.CurrentVersion);

        var root = FindRepositoryRoot();
        var store = File.ReadAllText(Path.Combine(
            root, "src", "ServerMonitor.Infrastructure", "Persistence", "SqliteServerHistoryStore.cs"));

        Assert.Contains("private const int CurrentSchemaVersion = 2;", store, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------- helpers

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

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//.*?$|/\*.*?\*/", string.Empty,
            RegexOptions.Multiline | RegexOptions.Singleline);
}
