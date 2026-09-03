using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// The Prism HIGH, made decidable. <c>RequestedTheme</c> is per-root, and the app now has two roots: the
/// Dashboard and the tray flyout. A service that remembered only the most recent one would let the
/// Dashboard silently stop following the preference the moment the flyout was created.
/// </summary>
public sealed class ThemeRootSetTests
{
    [Fact]
    public void A_second_root_is_added_and_does_not_replace_the_first()
    {
        // THE regression this type exists for. With a single field, Count would be 1 here and the first
        // root would never be written to again.
        var set = new ThemeRootSet();
        var dashboard = new object();
        var flyout = new object();

        set.Add(dashboard);
        set.Add(flyout);

        Assert.Equal(2, set.Count);
        Assert.Contains(dashboard, set.Snapshot());
        Assert.Contains(flyout, set.Snapshot());
    }

    [Fact]
    public void Attaching_the_same_root_twice_registers_it_once()
    {
        var set = new ThemeRootSet();
        var root = new object();

        Assert.True(set.Add(root));
        Assert.False(set.Add(root));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Detaching_removes_exactly_that_root_and_leaves_the_others()
    {
        var set = new ThemeRootSet();
        var dashboard = new object();
        var flyout = new object();
        set.Add(dashboard);
        set.Add(flyout);

        Assert.True(set.Remove(flyout));

        Assert.Equal([dashboard], set.Snapshot());
    }

    [Fact]
    public void Detaching_something_that_was_never_attached_reports_it_rather_than_pretending()
    {
        var set = new ThemeRootSet();

        Assert.False(set.Remove(new object()));
    }

    [Fact]
    public void Applying_visits_every_root_and_not_just_the_most_recent_one()
    {
        // The direct killer for "apply to the last root only", which is the shape the single-field
        // service had and the shape a well-meaning simplification would restore.
        var set = new ThemeRootSet();
        var dashboard = new object();
        var flyout = new object();
        set.Add(dashboard);
        set.Add(flyout);

        var visited = new List<object>();
        set.ForEach(visited.Add);

        Assert.Equal(2, visited.Count);
        Assert.Contains(dashboard, visited);
        Assert.Contains(flyout, visited);
    }

    [Fact]
    public void Applying_over_an_empty_set_visits_nothing_and_does_not_throw()
    {
        var set = new ThemeRootSet();
        var visited = 0;

        set.ForEach(_ => visited++);

        Assert.Equal(0, visited);
    }

    [Fact]
    public void The_snapshot_is_a_copy_so_iterating_it_cannot_be_disturbed_by_a_new_window()
    {
        var set = new ThemeRootSet();
        set.Add(new object());

        var snapshot = set.Snapshot();
        set.Add(new object());

        Assert.Single(snapshot);
        Assert.Equal(2, set.Count);
    }
}

/// <summary>The preference-to-theme mapping, which is a pure function and therefore assertable.</summary>
public sealed class ThemeResolutionTests
{
    [Fact]
    public void Each_preference_maps_to_the_theme_the_product_means()
    {
        Assert.Equal(ElementTheme.Light, ThemeService.ResolveElementTheme(AppThemePreference.Light));
        Assert.Equal(ElementTheme.Dark, ThemeService.ResolveElementTheme(AppThemePreference.Dark));

        // "System" is Default, not a guess at what the system currently is: Default is what defers to it.
        Assert.Equal(ElementTheme.Default, ThemeService.ResolveElementTheme(AppThemePreference.System));
    }

    [Fact]
    public void Every_preference_is_mapped()
    {
        // The switch has no default arm and CS8509 is an error here, so this cannot silently regress —
        // but a preference added and mapped to the wrong value would still compile, so the values above
        // are pinned individually rather than only counted.
        Assert.All(Enum.GetValues<AppThemePreference>(),
            preference => ThemeService.ResolveElementTheme(preference));
    }
}
