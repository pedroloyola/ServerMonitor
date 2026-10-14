using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// REL.0C (human decision 2026-10-13): en-US is the package default/base language.
/// <list type="bullet">
/// <item>The App project's <c>DefaultLanguage</c> is en-US and both manifests list en-US first.</item>
/// <item>The app Description is <c>ms-resource:AppDescription</c>, present and different in the three languages,
/// so no market sees a hard-coded Portuguese description.</item>
/// <item>The three Resources.resw files keep identical key sets.</item>
/// <item>Startup goes through <c>SupportedCultures.StartupOverride</c>, which never overwrites a persisted choice.</item>
/// </list>
/// </summary>
public sealed partial class Rel0cLanguageGuardTests
{
    private static readonly string[] Manifests = ["Package.appxmanifest", "Package.Dev.appxmanifest"];
    private static readonly string[] Languages = ["en-US", "pt-PT", "pt-BR"];

    public static TheoryData<string> ManifestNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Manifests)
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void The_package_default_language_is_american_english()
    {
        var project = XDocument.Load(AppSourceTree.Full("ServerMonitor.App.csproj"));
        var defaults = project.Descendants().Where(e => e.Name.LocalName == "DefaultLanguage").Select(e => e.Value.Trim()).ToList();

        Assert.Equal(["en-US"], defaults);
    }

    [Theory]
    [MemberData(nameof(ManifestNames))]
    public void The_manifest_lists_the_three_languages_with_american_english_first(string manifestName)
    {
        var manifest = XDocument.Load(AppSourceTree.Full(manifestName));
        var resources = manifest.Descendants().Single(e => e.Name.LocalName == "Resources")
            .Elements().Select(e => (string?)e.Attribute("Language")).ToList();

        Assert.Equal(Languages, resources);
    }

    [Theory]
    [MemberData(nameof(ManifestNames))]
    public void The_app_description_is_a_resource_localized_in_every_language(string manifestName)
    {
        var manifest = XDocument.Load(AppSourceTree.Full(manifestName));
        var visual = manifest.Descendants().Single(e => e.Name.LocalName == "VisualElements");

        Assert.Equal("ms-resource:AppDescription", (string?)visual.Attribute("Description"));
        var descriptions = Languages.Select(language => ResourceValue(language, "AppDescription")).ToList();
        Assert.All(descriptions, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Equal(3, descriptions.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(ManifestNames))]
    public void No_hard_coded_portuguese_is_left_in_the_app_visual_elements(string manifestName)
    {
        var visual = XDocument.Load(AppSourceTree.Full(manifestName)).Descendants()
            .Single(e => e.Name.LocalName == "VisualElements");
        var values = visual.DescendantsAndSelf().SelectMany(e => e.Attributes()).Select(a => a.Value).ToList();

        foreach (var marker in new[] { "ã", "ç", "á", "é", "ú", "servidores", "Painel", "monitoriza" })
        {
            Assert.DoesNotContain(values, v => v.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_three_resource_files_have_identical_keys()
    {
        var keys = Languages.ToDictionary(language => language, language => ResourceKeys(language).Order(StringComparer.Ordinal).ToList());

        Assert.Equal(keys["en-US"], keys["pt-PT"]);
        Assert.Equal(keys["en-US"], keys["pt-BR"]);
        Assert.Equal(keys["en-US"].Count, keys["en-US"].Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Startup_resolves_the_language_through_the_shared_table_that_keeps_a_persisted_choice()
    {
        var code = AppSourceTree.CodeWithoutComments("Services/LocalizationService.cs");

        Assert.Contains("SupportedCultures.StartupOverride(CurrentLanguageOverride, systemCulture)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportedCultures.IsSupported", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_localization_service_and_the_debug_qa_flag_write_the_language_override()
    {
        var writers = AppSourceTree.Files(".cs")
            .Where(path => OverrideWrite().IsMatch(AppSourceTree.CodeWithoutComments(path)))
            .ToList();

        Assert.Equal(["Qa/QaUiLanguageComposition.cs", "Services/LocalizationService.cs"], writers);
    }

    private static XDocument Resources(string language) =>
        XDocument.Load(AppSourceTree.Full($"Resources/{language}/Resources.resw"));

    private static IEnumerable<string> ResourceKeys(string language) =>
        Resources(language).Root!.Elements("data").Select(e => (string)e.Attribute("name")!);

    private static string? ResourceValue(string language, string key) =>
        (string?)Resources(language).Root!.Elements("data").SingleOrDefault(e => (string?)e.Attribute("name") == key)?.Element("value");

    [GeneratedRegex(@"PrimaryLanguageOverride\s*=(?!=)")]
    private static partial Regex OverrideWrite();
}
