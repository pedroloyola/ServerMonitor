using System.Text.RegularExpressions;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>UI.3 phase 1: every new string exists in pt-PT/pt-BR/en-US, formats agree, dynamic keys resolve.</summary>
public sealed partial class Ui3LocalizationTests
{
    private static readonly string[] NewKeys =
    [
        "HistoryCurrentLabel", "HistoryPercentUnit", "HistoryPeakFormat", "HistoryPeriodFooterFormat",
        "HistoryPeriodSameDayFormat", "HistoryPeriodSameMonthFormat", "HistoryPeriodSameYearFormat",
        "HistoryPeriodCrossYearFormat", "HistoryAxisTimeFormat", "HistoryAxisDayFormat", "HistoryRetentionNotice",
        "HistorySelectorLoading", "HistoryServerStatusConnected", "HistoryServerStatusOffline",
        "HistoryEmptyNeverTitle", "HistoryEmptyNeverMessage", "HistoryEmptyNeverAction",
        "HistoryEmptyPeriodTitle", "HistoryEmptyPeriodMessage", "HistoryEmptyPeriodAction",
        "WorkloadContextLoading", "WorkloadContextLastQueryFailed", "WorkloadFilterAll", "WorkloadFilterProblems",
        "WorkloadFilterAllFormat", "WorkloadFilterProblemsFormat", "WorkloadProblemBadgeOne", "WorkloadProblemBadgeOther",
        "WorkloadManagerDocker", "WorkloadServiceStatusActive", "WorkloadServiceStatusInactive",
        "WorkloadServiceStartupAutomatic", "WorkloadServiceStartupStaticDisplay", "WorkloadServiceStartupManual",
        "WorkloadServiceStartupBlocked", "WorkloadContainerHealthNone", "WorkloadValueUnknown",
        "WorkloadValueUnknownAccessible", "WorkloadServiceDisplayAccessibleFormat", "WorkloadNoResultsTitleFormat",
        "WorkloadNoProblemsTitle", "WorkloadNoResultsMessage", "WorkloadClearSearch", "WorkloadSectionNoResults",
        "WorkloadNothingTitle", "WorkloadNothingMessage", "WorkloadUnavailableTitle", "WorkloadUnavailableMessage",
        "WorkloadRetry", "WorkloadSearchAllPlaceholder", "WorkloadReadOnlyNotice",
    ];

    /// <summary>Every key a UI.3 VM builds at runtime from enum values or plural forms.</summary>
    private static IEnumerable<string> DynamicKeys()
    {
        foreach (var state in Enum.GetValues<ContainerState>())
        {
            foreach (var health in Enum.GetValues<ContainerHealth>())
            {
                if (WorkloadPresentation.ContainerHealthDisplayKey(state, health) is { } key)
                {
                    yield return key;
                }
            }

            yield return $"WorkloadContainerState{state}";
            foreach (var form in new[] { 1, 2 })
            {
                yield return WorkloadPresentation.PluralKey(WorkloadPresentation.ContainerLifecycleKey(state), form);
            }
        }

        foreach (var state in Enum.GetValues<ServiceState>())
        {
            yield return WorkloadPresentation.ServiceStateDisplayKey(state);
            foreach (var form in new[] { 1, 2 })
            {
                yield return WorkloadPresentation.PluralKey(WorkloadPresentation.ServiceLifecycleKey(state), form);
            }
        }

        foreach (var startup in Enum.GetValues<ServiceStartupState>())
        {
            if (WorkloadPresentation.ServiceStartupDisplayKey(startup) is { } key)
            {
                yield return key;
            }
        }

        foreach (var manager in Enum.GetValues<ServiceManager>())
        {
            yield return $"WorkloadManager{manager}";
        }

        foreach (var range in Enum.GetValues<HistoryTimeRange>())
        {
            yield return $"HistoryRangeName{range}";
        }

        yield return WorkloadPresentation.PluralKey("WorkloadProblemBadge", 1);
        yield return WorkloadPresentation.PluralKey("WorkloadProblemBadge", 5);
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
    public void DateFormats_AreValidCompositeFormats_InEveryCulture()
    {
        var start = new DateTime(2026, 9, 28, 15, 0, 0);
        var end = new DateTime(2026, 9, 29, 15, 0, 0);
        foreach (var culture in ResWLocalizationService.Cultures)
        {
            var resources = ResWLocalizationService.Load(culture);
            var format = HistoryPresentation.FormatCulture(culture);
            foreach (var key in new[]
                     {
                         "HistoryPeriodSameDayFormat", "HistoryPeriodSameMonthFormat",
                         "HistoryPeriodSameYearFormat", "HistoryPeriodCrossYearFormat"
                     })
            {
                var text = string.Format(format, resources[key], start, end);
                Assert.Contains("2026", text, StringComparison.Ordinal);
            }

            Assert.Equal("15:00", start.ToString(resources["HistoryAxisTimeFormat"], format));
        }
    }

    [Fact]
    public void PtPt_NewCopy_UsesTu()
    {
        // D-UI3-7 / D6: no "você" imperatives in the new pt-PT copy.
        var resources = ResWLocalizationService.Load("pt-PT");
        foreach (var key in NewKeys)
        {
            Assert.DoesNotMatch(YouImperative(), resources[key]);
        }

        Assert.Contains("Experimenta", resources["HistoryEmptyPeriodMessage"], StringComparison.Ordinal);
        Assert.Contains("Verifica", resources["WorkloadUnavailableMessage"], StringComparison.Ordinal);
    }

    private static string[] Placeholders(string value) =>
        PlaceholderPattern().Matches(value).Select(match => match.Groups[1].Value).Distinct().Order().ToArray();

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\b(Verifique|Experimente|Tente|Volte|Remova)\b")]
    private static partial Regex YouImperative();
}
