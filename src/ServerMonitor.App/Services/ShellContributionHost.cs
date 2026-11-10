using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using ServerMonitor.Features;

namespace ServerMonitor.App.Services;

/// <summary>A contribution whose view was created: what the shell shows for it.</summary>
/// <param name="Id">The contribution id, also its route.</param>
/// <param name="Title">The title, resolved from the contribution's resource key.</param>
/// <param name="View">The view its factory created (a UI element in production).</param>
public sealed record RealizedShellContribution(string Id, string Title, object View);

/// <summary>
/// The user interface feature modules add to the shell (<see cref="ShellContribution"/>), turned into views.
/// <para>
/// Each kind is arranged once with <see cref="ShellContributionOrdering.Arrange"/> (the first of two equal ids wins,
/// an id that is one of the shell's own built-in ids is left out, then by order and id) and its views are created once,
/// on the UI thread, the first time the shell asks: the navigation when the sidebar binds, the Settings sections when
/// the Settings page is built. A factory that throws, or returns something the shell cannot show, removes that one
/// contribution and is logged; it never reaches the caller. With no contributions both sequences are exactly the
/// built-in ones, and nothing about the shell changes.
/// </para>
/// <para>
/// Only composed modules contribute (<see cref="FeatureComposition"/> applies the same gate as for their services),
/// so this type never decides who sees what: it only displays what composition kept.
/// </para>
/// </summary>
public sealed class ShellContributionHost
{
    /// <summary>The built-in navigation destinations, in display order (the sidebar's items).</summary>
    public static IReadOnlyList<string> BuiltInNavigationIds { get; } = ["overview", "servers", "history", "settings"];

    /// <summary>The built-in sections of the Settings page, in display order; contributed sections follow them.</summary>
    public static IReadOnlyList<string> BuiltInSettingsSectionIds { get; } = ["appearance", "monitoring", "window"];

    /// <summary>The built-in destination that always stays last; contributed entries go before it.</summary>
    private const string LastNavigationId = "settings";

    private readonly ShellContributions _contributions;
    private readonly ILogger _logger;
    private readonly Func<string, string> _title;
    private readonly Func<object, bool> _canShow;
    private IReadOnlyList<RealizedShellContribution>? _navigationEntries;
    private IReadOnlyList<RealizedShellContribution>? _settingsSections;

    public ShellContributionHost(
        ShellContributions contributions,
        ILocalizationService localization,
        ILogger<ShellContributionHost> logger)
        : this(contributions, logger, localization.GetString, view => view is UIElement)
    {
    }

    /// <summary>TEST SEAM: the real arranging and realizing over a title lookup and a view check without a XAML runtime.</summary>
    internal ShellContributionHost(
        ShellContributions contributions,
        ILogger logger,
        Func<string, string> title,
        Func<object, bool> canShow)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(canShow);
        _contributions = contributions;
        _logger = logger;
        _title = title;
        _canShow = canShow;
    }

    /// <summary>The contributed navigation entries, realized on first use (UI thread).</summary>
    public IReadOnlyList<RealizedShellContribution> NavigationEntries =>
        _navigationEntries ??= Realize(_contributions.NavigationEntries, BuiltInNavigationIds, "navigation entry");

    /// <summary>The contributed Settings sections, realized on first use (UI thread).</summary>
    public IReadOnlyList<RealizedShellContribution> SettingsSections =>
        _settingsSections ??= Realize(_contributions.SettingsSections, BuiltInSettingsSectionIds, "Settings section");

    /// <summary>Every navigation id in display order: the built-in ones, the contributed ones before the last built-in.</summary>
    public IReadOnlyList<string> NavigationIds =>
    [
        .. BuiltInNavigationIds.Where(id => id != LastNavigationId),
        .. NavigationEntries.Select(entry => entry.Id),
        LastNavigationId,
    ];

    /// <summary>Every Settings section id in display order: the built-in ones, then the contributed ones.</summary>
    public IReadOnlyList<string> SettingsSectionIds =>
        [.. BuiltInSettingsSectionIds, .. SettingsSections.Select(section => section.Id)];

    /// <summary>The view of the contributed navigation entry <paramref name="id"/>; false for any other id.</summary>
    public bool TryGetNavigationView(string id, out object view)
    {
        var entry = NavigationEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        view = entry?.View!;
        return entry is not null;
    }

    private IReadOnlyList<RealizedShellContribution> Realize(
        IReadOnlyList<ShellContribution> inCompositionOrder,
        IReadOnlyList<string> builtInIds,
        string kind)
    {
        var arranged = ShellContributionOrdering.Arrange(inCompositionOrder, builtInIds, out var rejected);
        foreach (var rejection in rejected)
        {
            _logger.LogWarning(
                "A contributed {Kind} was left out: {Reason}. Id: {Id}.", kind, rejection.Reason, rejection.Contribution.Id);
        }

        var realized = new List<RealizedShellContribution>(arranged.Count);
        foreach (var contribution in arranged)
        {
            try
            {
                var view = contribution.CreateView();
                if (view is null || !_canShow(view))
                {
                    _logger.LogError(
                        "A contributed {Kind} was removed: its factory returned no displayable view. Id: {Id}.",
                        kind, contribution.Id);
                    continue;
                }

                var title = _title(contribution.TitleResourceKey);
                realized.Add(new RealizedShellContribution(
                    contribution.Id, string.IsNullOrWhiteSpace(title) ? contribution.Id : title, view));
            }
            catch (Exception exception)
            {
                // Never fatal: one contribution that cannot be built is left out, the shell stays as it is.
                _logger.LogError(
                    "A contributed {Kind} was removed: its factory failed. Id: {Id}. Exception type: {ExceptionType}.",
                    kind, contribution.Id, exception.GetType().Name);
            }
        }

        return realized;
    }
}
