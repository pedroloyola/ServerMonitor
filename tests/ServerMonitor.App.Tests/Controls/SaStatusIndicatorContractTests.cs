using System.Reflection;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Tests.Controls;

/// <summary>
/// UI.2 S1. The status primitive declares one visual state per <see cref="SaStatusKind"/> value, in one
/// group, so no status can ever be rendered with a stale dot. Together with
/// <c>TemplatePartsExistInDefaultTemplates</c> (every declared state exists in the default template) this closes
/// enum -> declared state -> template state. Reads attributes only: no XAML runtime is needed.
/// </summary>
public sealed class SaStatusIndicatorContractTests
{
    [Fact]
    public void EveryStatusKindHasADeclaredVisualStateInOneGroup()
    {
        var states = typeof(SaStatusIndicator).GetCustomAttributes<TemplateVisualStateAttribute>()
            .Where(s => s.GroupName == "StatusStates")
            .ToList();

        Assert.Equal(
            Enum.GetNames<SaStatusKind>().OrderBy(n => n, StringComparer.Ordinal),
            states.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// UI.3: the only other group is the label toggle (dot-only presentation). Status colours stay in StatusStates,
    /// so hiding the label can never change which brush a status gets.
    /// </summary>
    [Fact]
    public void LabelVisibilityIsItsOwnTwoStateGroup()
    {
        var groups = typeof(SaStatusIndicator).GetCustomAttributes<TemplateVisualStateAttribute>()
            .GroupBy(s => s.GroupName)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Name).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        Assert.Equal(new[] { "LabelStates", "StatusStates" }, groups.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "LabelHidden", "LabelVisible" }, groups["LabelStates"]);
    }

    [Fact]
    public void StatusVocabularyIsThePrimitivesOwnNotTheDomains()
    {
        Assert.Equal("ServerMonitor.App.Controls.Primitives", typeof(SaStatusKind).Namespace);
        Assert.Equal(SaStatusKind.Unknown, default(SaStatusKind));
    }
}
