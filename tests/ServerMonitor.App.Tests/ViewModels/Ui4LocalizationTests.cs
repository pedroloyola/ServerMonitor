using System.Text.RegularExpressions;
using System.Xml.Linq;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.4 phase 1: every new string exists in pt-PT/pt-BR/en-US, placeholders agree, pt-PT uses "tu", and the user-facing
/// "Workloads" is gone (renamed "Serviços e containers" / "Services and containers"; keys, classes and namespaces keep
/// their names).
/// </summary>
public sealed partial class Ui4LocalizationTests
{
    private static readonly string[] NewKeys =
    [
        "ServerMetricUnavailable", "ServerMetricUnavailableAccessible", "ServerDetailOpenFor", "ServerRowAutomationFormat",
        "OverviewPageTitle", "ServersPageTitle", "OverviewHealthOfTotalFormat", "OverviewHealthAutomationFormat",
        "OverviewPriorityAutomationFormat", "OverviewNoProblemsTitle", "OverviewNoProblemsMessage",
        "OverviewUpdatedJustNow", "OverviewUpdatedSecondsFormat", "OverviewUpdatedMinutesFormat",
        "OverviewUpdatedHoursFormat", "OverviewUpdatedDaysFormat", "ServerSearchNoResultsTitleFormat",
        "ServersBreadcrumb.ParentText", "ServersBreadcrumb.CurrentText", "ServersPageTitleText.Text", "ServersAddButton.Content",
        "ServersSearch.PlaceholderText", "ServersSearch.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "ServersNoResultsState.Message", "ServersClearSearchButton.Content", "ServersEmptyState.Title",
        "ServersEmptyState.Message", "ServersEmptyAddButton.Content", "ServersHiddenNote.Text", "ServerDetailOpenButton.Content"
    ];

    /// <summary>Every key the UI.4 view models build at runtime from enum values or plural forms.</summary>
    private static IEnumerable<string> DynamicKeys()
    {
        foreach (var health in Enum.GetValues<ServerHealth>())
        {
            yield return $"ServerStatus{health}";
        }

        foreach (var health in OverviewPresentation.ChipOrder)
        {
            foreach (var count in new[] { 1, 2 })
            {
                yield return WorkloadPresentation.PluralKey($"OverviewHealthChip{health}", count);
                yield return WorkloadPresentation.PluralKey($"OverviewHealthAutomation{health}", count);
            }
        }

        foreach (var metric in Enum.GetValues<PriorityMetric>())
        {
            yield return $"OverviewPriorityTitle{metric}";
        }

        foreach (var count in new[] { 1, 2 })
        {
            yield return WorkloadPresentation.PluralKey("ServersSummaryTotal", count);
            yield return WorkloadPresentation.PluralKey("ServersSummaryHealthy", count);
        }
    }

    [Fact]
    public void NewAndDynamicKeys_ExistAndAreNonEmpty_InEveryCulture()
    {
        foreach (var culture in ResWLocalizationService.Cultures)
        {
            var resources = ResWLocalizationService.Load(culture);
            foreach (var key in NewKeys.Concat(DynamicKeys()).Distinct())
            {
                Assert.True(resources.TryGetValue(key, out var value), $"{culture} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture} has an empty {key}.");
            }
        }
    }

    [Fact]
    public void Placeholders_AgreeAcrossCultures()
    {
        var reference = ResWLocalizationService.Load("pt-PT");
        foreach (var key in NewKeys.Concat(DynamicKeys()).Distinct())
        {
            var expected = Placeholders(reference[key]);
            foreach (var culture in ResWLocalizationService.Cultures)
            {
                Assert.Equal(expected, Placeholders(ResWLocalizationService.Load(culture)[key]));
            }
        }
    }

    [Fact]
    public void PtPt_NewCopy_UsesTu()
    {
        var resources = ResWLocalizationService.Load("pt-PT");
        foreach (var key in NewKeys.Concat(DynamicKeys()).Distinct())
        {
            Assert.DoesNotMatch(YouImperative(), resources[key]);
        }

        Assert.Contains("Experimenta", resources["ServersNoResultsState.Message"], StringComparison.Ordinal);
        Assert.Contains("Adiciona", resources["ServersEmptyState.Message"], StringComparison.Ordinal);
    }

    [Fact]
    public void Workloads_IsRenamed_InEveryUserFacingString()
    {
        foreach (var culture in ResWLocalizationService.Cultures)
        {
            var resources = ResWLocalizationService.Load(culture);
            var english = culture == "en-US";
            Assert.Equal(english ? "Services and containers" : "Serviços e containers", resources["ServerCardWorkloadsMenuItem.Text"]);
            Assert.Equal(english ? "Services and containers" : "Serviços e containers", resources["WorkloadsSubtitle.Text"]);
            Assert.DoesNotContain(resources, entry => entry.Value.Contains("workload", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Workloads_IsNotHardCodedInXaml()
    {
        foreach (var file in Directory.EnumerateFiles(AppSourceTree.AppRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(file);
            foreach (var attribute in document.Descendants().Attributes()
                         .Where(a => a.Name.LocalName is "Text" or "Content" or "Header" or "PlaceholderText" or "Title"))
            {
                Assert.False(
                    attribute.Value.Contains("workload", StringComparison.OrdinalIgnoreCase) && !attribute.Value.StartsWith('{'),
                    $"{Path.GetFileName(file)}: {attribute.Name.LocalName}=\"{attribute.Value}\"");
            }
        }
    }

    private static string[] Placeholders(string value) =>
        PlaceholderPattern().Matches(value).Select(match => match.Groups[1].Value).Distinct().Order().ToArray();

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\b(Verifique|Experimente|Tente|Volte|Remova|Adicione|Restaure)\b")]
    private static partial Regex YouImperative();
}
