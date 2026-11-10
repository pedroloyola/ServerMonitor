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

/// <summary>What one module adds to the shell. Both lists may be empty; neither is ever null.</summary>
public sealed class ShellContributions
{
    /// <summary>Nothing: the default for every module that adds no user interface.</summary>
    public static readonly ShellContributions Empty = new([], []);

    public ShellContributions(
        IReadOnlyList<ShellContribution> settingsSections,
        IReadOnlyList<ShellContribution> navigationEntries)
    {
        ArgumentNullException.ThrowIfNull(settingsSections);
        ArgumentNullException.ThrowIfNull(navigationEntries);
        if (settingsSections.Contains(null!) || navigationEntries.Contains(null!))
        {
            throw new ArgumentException("A contribution list may not contain null.");
        }

        SettingsSections = [.. settingsSections];
        NavigationEntries = [.. navigationEntries];
    }

    /// <summary>Sections shown on the Settings page, after the built-in ones.</summary>
    public IReadOnlyList<ShellContribution> SettingsSections { get; }

    /// <summary>Entries shown in the navigation, after the built-in destinations and before Settings.</summary>
    public IReadOnlyList<ShellContribution> NavigationEntries { get; }
}

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
        ArgumentNullException.ThrowIfNull(inCompositionOrder);
        ArgumentNullException.ThrowIfNull(reservedIds);

        var reserved = new HashSet<string>(reservedIds, StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<ShellContribution>(inCompositionOrder.Count);
        var left = new List<ShellContributionRejection>();
        foreach (var contribution in inCompositionOrder)
        {
            ArgumentNullException.ThrowIfNull(contribution);
            if (reserved.Contains(contribution.Id))
            {
                left.Add(new ShellContributionRejection(contribution, "its id is a built-in id of the shell"));
            }
            else if (!taken.Add(contribution.Id))
            {
                left.Add(new ShellContributionRejection(contribution, "an earlier contribution already has its id"));
            }
            else
            {
                kept.Add(contribution);
            }
        }

        rejected = left;
        return [.. kept.OrderBy(c => c.Order).ThenBy(c => c.Id, StringComparer.Ordinal)];
    }
}
