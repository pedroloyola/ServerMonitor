namespace ServerMonitor.Features;

/// <summary>
/// One piece of user interface that a feature module adds to the shell: a Settings section or a navigation entry.
/// <para>
/// It is a plain description. The shell decides where it goes, orders it, titles it from
/// <see cref="TitleResourceKey"/> and creates its view by calling <see cref="CreateView"/> on the UI thread. The shell
/// routes to it by <see cref="Id"/> alone: there is no page type to name and nothing is resolved by reflection, so a
/// module that is not part of this repository can contribute a view the shell has never heard of.
/// </para>
/// <para>
/// This assembly has no UI dependency, so the view is an <see cref="object"/>. The shell accepts only what it can
/// display; anything else, and any exception thrown by <see cref="CreateView"/>, removes the contribution instead of
/// failing the application.
/// </para>
/// <para>
/// <b>Lifetime of the view.</b> The shell creates the view once and keeps it for the life of the process: it is shown
/// again on every visit and is never disposed. The view must therefore tolerate being loaded and unloaded many times
/// (every navigation away and back, and a theme change, which re-mounts the page it is on), release anything it holds
/// on <c>Unloaded</c> and take it again on <c>Loaded</c>, and never rely on <see cref="IDisposable"/>. It must be a new
/// element with no parent. A navigation entry's view must be a page, so the shell can re-mount it when the theme
/// changes; a Settings section's view may be any element.
/// </para>
/// </summary>
public sealed class ShellContribution
{
    /// <param name="id">
    /// Stable identifier, in the same shape as a <see cref="FeatureId"/> (for example <c>reports.weekly</c>). It is
    /// how the shell routes to the contribution and how duplicates are recognised.
    /// </param>
    /// <param name="order">Position among contributions of the same kind: lower first, ties broken by <paramref name="id"/>.</param>
    /// <param name="titleResourceKey">The resource key of the title the shell shows for it.</param>
    /// <param name="createView">Creates the view. Called by the shell on the UI thread, at most once per contribution.</param>
    /// <param name="iconPathData">
    /// Optional icon for a navigation entry, as path markup data (the same mini-language as the shell's own icons).
    /// Null or blank means the shell's generic icon. Settings sections show no icon.
    /// </param>
    public ShellContribution(string id, int order, string titleResourceKey, Func<object> createView, string? iconPathData = null)
    {
        if (!FeatureId.IsWellFormed(id))
        {
            throw new ArgumentException(
                $"'{id}' is not a well-formed contribution id. It follows the feature id shape " +
                "(lowercase ASCII letters, digits, '-' and '.' between non-empty segments).",
                nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(titleResourceKey);
        ArgumentNullException.ThrowIfNull(createView);
        Id = id;
        Order = order;
        TitleResourceKey = titleResourceKey;
        CreateView = createView;
        IconPathData = string.IsNullOrWhiteSpace(iconPathData) ? null : iconPathData;
    }

    public string Id { get; }

    public int Order { get; }

    public string TitleResourceKey { get; }

    public Func<object> CreateView { get; }

    /// <summary>The navigation entry's icon as path markup data, or null for the shell's generic icon.</summary>
    public string? IconPathData { get; }

    public override string ToString() => $"{Id} (order {Order})";
}

/// <summary>
/// What is added to the shell. Every list may be empty; none is ever null.
/// <para>
/// A feature module fills the first two only: <see cref="FeatureComposition"/> collects a composed module's Settings
/// sections and navigation entries and nothing else. <see cref="UnlistedPages"/> and <see cref="Links"/> are filled by
/// the composition root alone, from what the composition supplies.
/// </para>
/// </summary>
public sealed class ShellContributions
{
    /// <summary>Nothing: the default for every module that adds no user interface.</summary>
    public static readonly ShellContributions Empty = new([], []);

    public ShellContributions(
        IReadOnlyList<ShellContribution> settingsSections,
        IReadOnlyList<ShellContribution> navigationEntries)
        : this(settingsSections, navigationEntries, [], [])
    {
    }

    public ShellContributions(
        IReadOnlyList<ShellContribution> settingsSections,
        IReadOnlyList<ShellContribution> navigationEntries,
        IReadOnlyList<ShellContribution> unlistedPages,
        IReadOnlyList<ShellLink> links)
    {
        ArgumentNullException.ThrowIfNull(settingsSections);
        ArgumentNullException.ThrowIfNull(navigationEntries);
        ArgumentNullException.ThrowIfNull(unlistedPages);
        ArgumentNullException.ThrowIfNull(links);
        if (settingsSections.Contains(null!) || navigationEntries.Contains(null!) || unlistedPages.Contains(null!) || links.Contains(null!))
        {
            throw new ArgumentException("A contribution list may not contain null.");
        }

        SettingsSections = [.. settingsSections];
        NavigationEntries = [.. navigationEntries];
        UnlistedPages = [.. unlistedPages];
        Links = [.. links];
    }

    /// <summary>Sections shown on the Settings page, after the built-in ones.</summary>
    public IReadOnlyList<ShellContribution> SettingsSections { get; }

    /// <summary>Entries shown in the navigation, after the built-in destinations and before Settings.</summary>
    public IReadOnlyList<ShellContribution> NavigationEntries { get; }

    /// <summary>
    /// Pages reached by id only, never listed in the navigation: their view must be a page, like a navigation entry's.
    /// The shell draws a way back to Settings above them; moving between sub-pages stays inside the page itself.
    /// </summary>
    public IReadOnlyList<ShellContribution> UnlistedPages { get; }

    /// <summary>Links the shell draws itself (a Settings card, or one entry at the foot of the navigation) to a page.</summary>
    public IReadOnlyList<ShellLink> Links { get; }
}

/// <summary>Where the shell draws a <see cref="ShellLink"/>.</summary>
public enum ShellLinkPlacement
{
    /// <summary>A card in the row of link cards at the end of the Settings page.</summary>
    SettingsCard,

    /// <summary>One entry at the foot of the navigation, just above Settings. At most one is shown.</summary>
    SidebarFooter,
}

/// <summary>
/// A link the shell draws with its own control (it carries no view): opening it goes to the page
/// <see cref="TargetId"/>, a navigation entry or an unlisted page, through the shell's navigation like any other.
/// <para>
/// <see cref="Label"/> and <see cref="Hint"/> are read by the shell on the UI thread whenever it shows the link again
/// (when it is built, after each navigation, when the window becomes active, after a theme change); a provider that
/// throws or returns nothing falls back to the title resource. A link whose target is not a page the shell shows is
/// left out.
/// </para>
/// </summary>
public sealed class ShellLink
{
    /// <param name="id">Stable identifier, in the same shape as a <see cref="FeatureId"/>.</param>
    /// <param name="order">Position among links: lower first, ties broken by <paramref name="id"/>.</param>
    /// <param name="titleResourceKey">The resource key of the label shown when <paramref name="label"/> gives none.</param>
    /// <param name="targetId">The id of the page it opens.</param>
    /// <param name="placement">Where the shell draws it.</param>
    /// <param name="label">Optional: the current label. Read on the UI thread; must be quick and must not block.</param>
    /// <param name="hint">Optional: the current secondary text (help text for the Narrator, the tooltip).</param>
    /// <param name="iconPathData">Optional icon as path markup data; null or blank means the shell's generic icon.</param>
    public ShellLink(
        string id,
        int order,
        string titleResourceKey,
        string targetId,
        ShellLinkPlacement placement,
        Func<string>? label = null,
        Func<string>? hint = null,
        string? iconPathData = null)
    {
        if (!FeatureId.IsWellFormed(id))
        {
            throw new ArgumentException($"'{id}' is not a well-formed link id.", nameof(id));
        }

        if (!FeatureId.IsWellFormed(targetId))
        {
            throw new ArgumentException($"'{targetId}' is not a well-formed target id.", nameof(targetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(titleResourceKey);
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown link placement.");
        }

        Id = id;
        Order = order;
        TitleResourceKey = titleResourceKey;
        TargetId = targetId;
        Placement = placement;
        Label = label;
        Hint = hint;
        IconPathData = string.IsNullOrWhiteSpace(iconPathData) ? null : iconPathData;
    }

    public string Id { get; }

    public int Order { get; }

    public string TitleResourceKey { get; }

    public string TargetId { get; }

    public ShellLinkPlacement Placement { get; }

    public Func<string>? Label { get; }

    public Func<string>? Hint { get; }

    public string? IconPathData { get; }

    public override string ToString() => $"{Id} -> {TargetId} ({Placement}, order {Order})";
}

/// <summary>A link the shell left out, and why.</summary>
public readonly record struct ShellLinkRejection(ShellLink Link, string Reason);

/// <summary>A contribution the shell left out, and why. Surfaced so the caller can log it rather than lose it.</summary>
/// <param name="Contribution">The contribution that was left out.</param>
/// <param name="Reason">Why, in words fit for a log line.</param>
public readonly record struct ShellContributionRejection(ShellContribution Contribution, string Reason);

/// <summary>
/// The one ordering rule for contributions of one kind, so every shell applies it the same way.
/// </summary>
public static class ShellContributionOrdering
{
    /// <summary>
    /// Arranges <paramref name="inCompositionOrder"/> (contributions of ONE kind, in the order their modules were
    /// composed) for display.
    /// <list type="number">
    /// <item><description>
    /// <b>Duplicates: the first one wins.</b> Walking in composition order, a contribution whose id is one of
    /// <paramref name="reservedIds"/> (the shell's own built-in ids) or was already taken by an earlier contribution is
    /// left out and reported in <paramref name="rejected"/>. Composition order is a static list, so the winner never
    /// depends on timing.
    /// </description></item>
    /// <item><description>
    /// <b>Order:</b> the rest by <see cref="ShellContribution.Order"/>, then by <see cref="ShellContribution.Id"/>
    /// (ordinal). Ids are unique after step 1, so the result is a total order: the same contributions always give
    /// the same sequence, whatever order the modules listed them in.
    /// </description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<ShellContribution> Arrange(
        IReadOnlyList<ShellContribution> inCompositionOrder,
        IEnumerable<string> reservedIds,
        out IReadOnlyList<ShellContributionRejection> rejected)
    {
        var arranged = ArrangeBy(inCompositionOrder, reservedIds, c => c.Id, c => c.Order, out var left);
        rejected = [.. left.Select(l => new ShellContributionRejection(l.Item, l.Reason))];
        return arranged;
    }

    /// <summary>The same rule for links (<see cref="ShellLink"/>): first id wins, reserved ids left out, then Order and Id.</summary>
    public static IReadOnlyList<ShellLink> Arrange(
        IReadOnlyList<ShellLink> inCompositionOrder,
        IEnumerable<string> reservedIds,
        out IReadOnlyList<ShellLinkRejection> rejected)
    {
        var arranged = ArrangeBy(inCompositionOrder, reservedIds, l => l.Id, l => l.Order, out var left);
        rejected = [.. left.Select(l => new ShellLinkRejection(l.Item, l.Reason))];
        return arranged;
    }

    private static IReadOnlyList<T> ArrangeBy<T>(
        IReadOnlyList<T> inCompositionOrder,
        IEnumerable<string> reservedIds,
        Func<T, string> idOf,
        Func<T, int> orderOf,
        out List<(T Item, string Reason)> rejected)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(inCompositionOrder);
        ArgumentNullException.ThrowIfNull(reservedIds);

        var reserved = new HashSet<string>(reservedIds, StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<T>(inCompositionOrder.Count);
        rejected = [];
        foreach (var item in inCompositionOrder)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (reserved.Contains(idOf(item)))
            {
                rejected.Add((item, "its id is a built-in id of the shell"));
            }
            else if (!taken.Add(idOf(item)))
            {
                rejected.Add((item, "an earlier contribution already has its id"));
            }
            else
            {
                kept.Add(item);
            }
        }

        return [.. kept.OrderBy(orderOf).ThenBy(idOf, StringComparer.Ordinal)];
    }
}
