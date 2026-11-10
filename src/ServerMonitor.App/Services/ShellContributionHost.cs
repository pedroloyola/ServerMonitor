using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.Features;

namespace ServerMonitor.App.Services;

/// <summary>A contribution whose view was created: what the shell shows for it.</summary>
/// <param name="Id">The contribution id, also its route.</param>
/// <param name="Title">The title, resolved from the contribution's resource key.</param>
/// <param name="View">The view its factory created (a page for a navigation entry, an element for a Settings section).</param>
/// <param name="IconPathData">The navigation entry's icon as path markup data, or null for the shell's generic icon.</param>
public sealed record RealizedShellContribution(string Id, string Title, object View, string? IconPathData = null);

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
/// What the shell can show: a navigation entry's view must be a <see cref="Page"/>, because the shell re-mounts the page
/// in its frame when the theme changes; a Settings section's view may be any <see cref="FrameworkElement"/>, because it
/// lives inside the Settings page. Either must be new, with no parent: an element can be in one place only. A view is
/// placed through <see cref="TryAttach"/>, so a view that still fails when it is added is left out too.
/// </para>
/// <para>
/// Views are created once and kept for the life of the process, shown again on every visit and never disposed (see
/// <see cref="ShellContribution"/> for what that asks of a view).
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

    /// <summary>The neutral label shown, and logged, when a contribution's title resource is missing.</summary>
    internal const string UntitledResourceKey = "ShellContributionUntitled";

    /// <summary>The neutral label when even <see cref="UntitledResourceKey"/> cannot be read.</summary>
    internal const string UntitledFallback = "Untitled";

    private readonly ShellContributions _contributions;
    private readonly ILogger _logger;
    private readonly Func<string, string> _title;
    private readonly Func<object, bool> _canShowNavigationView;
    private readonly Func<object, bool> _canShowSectionView;
    private IReadOnlyList<RealizedShellContribution>? _navigationEntries;
    private IReadOnlyList<RealizedShellContribution>? _settingsSections;
    private bool _realizing;

    public ShellContributionHost(
        ShellContributions contributions,
        ILocalizationService localization,
        ILogger<ShellContributionHost> logger)
        : this(contributions, logger, localization.GetString, IsDisplayableNavigationView, IsDisplayableSectionView)
    {
    }

    /// <summary>TEST SEAM: the real arranging and realizing, with one view check for both kinds.</summary>
    internal ShellContributionHost(
        ShellContributions contributions,
        ILogger logger,
        Func<string, string> title,
        Func<object, bool> canShow)
        : this(contributions, logger, title, canShow, canShow)
    {
    }

    /// <summary>TEST SEAM: the real arranging and realizing over a title lookup and per-kind view checks.</summary>
    internal ShellContributionHost(
        ShellContributions contributions,
        ILogger logger,
        Func<string, string> title,
        Func<object, bool> canShowNavigationView,
        Func<object, bool> canShowSectionView)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(canShowNavigationView);
        ArgumentNullException.ThrowIfNull(canShowSectionView);
        _contributions = contributions;
        _logger = logger;
        _title = title;
        _canShowNavigationView = canShowNavigationView;
        _canShowSectionView = canShowSectionView;
    }

    /// <summary>The type a navigation entry's view must be: what the shell re-mounts in its frame on a theme change.</summary>
    internal static Type NavigationViewType { get; } = typeof(Page);

    /// <summary>The type a Settings section's view must be.</summary>
    internal static Type SectionViewType { get; } = typeof(FrameworkElement);

    /// <summary>A navigation entry's view: a page that is not placed anywhere yet.</summary>
    internal static bool IsDisplayableNavigationView(object view) =>
        NavigationViewType.IsInstanceOfType(view) && ((FrameworkElement)view).Parent is null;

    /// <summary>A Settings section's view: an element that is not placed anywhere yet.</summary>
    internal static bool IsDisplayableSectionView(object view) =>
        SectionViewType.IsInstanceOfType(view) && ((FrameworkElement)view).Parent is null;

    /// <summary>The contributed navigation entries, realized on first use (UI thread).</summary>
    public IReadOnlyList<RealizedShellContribution> NavigationEntries =>
        _navigationEntries ?? RealizeOnce(
            ref _navigationEntries, _contributions.NavigationEntries, BuiltInNavigationIds, "navigation entry", _canShowNavigationView);

    /// <summary>The contributed Settings sections, realized on first use (UI thread).</summary>
    public IReadOnlyList<RealizedShellContribution> SettingsSections =>
        _settingsSections ?? RealizeOnce(
            ref _settingsSections, _contributions.SettingsSections, BuiltInSettingsSectionIds, "Settings section", _canShowSectionView);

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

    /// <summary>
    /// Places one realized view in the shell by running <paramref name="attach"/> (for example, adding it to a panel).
    /// If placing it throws, the contribution is left out and logged (exception type only) and false is returned; the
    /// caller carries on with the next one.
    /// </summary>
    public bool TryAttach(RealizedShellContribution contribution, Action<object> attach)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(attach);
        try
        {
            attach(contribution.View);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "A contribution could not be placed in the shell and was left out. Id: {Id}. Exception type: {ExceptionType}.",
                contribution.Id, exception.GetType().Name);
            return false;
        }
    }

    /// <summary>The view of the contributed navigation entry <paramref name="id"/>; false for any other id.</summary>
    public bool TryGetNavigationView(string id, out object view)
    {
        var entry = NavigationEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        view = entry?.View!;
        return entry is not null;
    }

    private IReadOnlyList<RealizedShellContribution> RealizeOnce(
        ref IReadOnlyList<RealizedShellContribution>? cache,
        IReadOnlyList<ShellContribution> inCompositionOrder,
        IReadOnlyList<string> builtInIds,
        string kind,
        Func<object, bool> canShow)
    {
        if (_realizing)
        {
            // A factory that asks for the shell's contributions while they are being created would otherwise recurse
            // until the stack overflows, which nothing can catch. It sees none; nothing is cached, so the real read that
            // follows still realizes everything.
            _logger.LogWarning("A contribution factory read the shell's contributions while they were being created; it saw none.");
            return [];
        }

        _realizing = true;
        try
        {
            return cache = Realize(inCompositionOrder, builtInIds, kind, canShow);
        }
        finally
        {
            _realizing = false;
        }
    }

    private IReadOnlyList<RealizedShellContribution> Realize(
        IReadOnlyList<ShellContribution> inCompositionOrder,
        IReadOnlyList<string> builtInIds,
        string kind,
        Func<object, bool> canShow)
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
                if (view is null || !canShow(view))
                {
                    _logger.LogError(
                        "A contributed {Kind} was removed: its factory returned no view the shell can show (a new one, with no parent, of the required type). Id: {Id}.",
                        kind, contribution.Id);
                    continue;
                }

                realized.Add(new RealizedShellContribution(
                    contribution.Id, TitleOf(contribution, kind), view, contribution.IconPathData));
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

    /// <summary>
    /// The contribution's title. A missing resource (blank, the key itself, or a lookup that throws) is logged with the
    /// id and the key and shows a neutral label, never the raw id, so the Narrator does not read an identifier.
    /// </summary>
    private string TitleOf(ShellContribution contribution, string kind)
    {
        if (Lookup(contribution.TitleResourceKey) is { } title)
        {
            return title;
        }

        _logger.LogWarning(
            "A contributed {Kind} has no title resource; a neutral label is shown. Id: {Id}. Key: {Key}.",
            kind, contribution.Id, contribution.TitleResourceKey);
        return Lookup(UntitledResourceKey) ?? UntitledFallback;
    }

    private string? Lookup(string key)
    {
        try
        {
            var value = _title(key);
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
