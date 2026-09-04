using System.Drawing;
using ServerMonitor.App.Shell.Tray;

namespace ServerMonitor.App.Tests.Shell;

/// <summary>
/// M13-QA-11: the flyout's LIFECYCLE POLICY, behind an injectable surface.
/// </summary>
/// <remarks>
/// The presentation itself needs a desktop — <c>XamlRoot</c>, <c>MenuFlyout.ShowAt</c> and
/// <c>SetWinEventHook</c> do not exist headlessly, and the four-cell matrix is what proves those. The
/// POLICY does not need one, and the defect was in the policy: a slot released only by a signal that, in
/// three of four measured states, never arrived.
/// <para>
/// Every test here fails on the branches that shipped in the first fix: the watch refusal was not checked,
/// the failed hide had no terminal, and <c>Dispose</c> released nothing.
/// </para>
/// </remarks>
public sealed class FlyoutLifecycleTests
{
    private sealed class FakeSurface : IFlyoutSurface
    {
        public bool Presentable { get; set; } = true;

        public bool WatchInstallable { get; set; } = true;

        public bool HideSucceeds { get; set; } = true;

        public bool PresentThrows { get; set; }

        public bool MenuShown { get; private set; }

        public int WatchInstalls { get; private set; }

        public int WatchRemovals { get; private set; }

        public int WindowHides { get; private set; }

        public List<string> Calls { get; } = new();

        public bool IsPresentable => Presentable;

        public void MoveTo(Point anchor) => Calls.Add("MoveTo");

        public void Activate() => Calls.Add("Activate");

        public bool TryInstallDismissalWatch()
        {
            Calls.Add("TryInstallDismissalWatch");

            if (!WatchInstallable)
            {
                return false;
            }

            WatchInstalls++;
            return true;
        }

        public void RemoveDismissalWatch()
        {
            Calls.Add("RemoveDismissalWatch");
            WatchRemovals++;
        }

        /// <summary>When set, the foreground moves while the menu is being shown — the blind window.</summary>
        public nint? MoveForegroundOnPresent { get; set; }

        public void PresentMenu()
        {
            Calls.Add("PresentMenu");

            if (MoveForegroundOnPresent is { } moved)
            {
                Foreground = moved;
            }


            if (PresentThrows)
            {
                throw new InvalidOperationException("the menu could not be shown");
            }

            MenuShown = true;
        }

        public bool TryHideMenu()
        {
            Calls.Add("TryHideMenu");

            if (!HideSucceeds)
            {
                return false;
            }

            MenuShown = false;
            return true;
        }

        public void HideWindow()
        {
            Calls.Add("HideWindow");
            WindowHides++;
        }

        /// <summary>What the foreground reports. A test moves it to simulate a change.</summary>
        public nint Foreground { get; set; } = 100;

        public nint CaptureForeground()
        {
            Calls.Add("CaptureForeground");
            return Foreground;
        }
    }

    private static (FlyoutLifecycle Subject, FakeSurface Surface, Func<int> Releases) Create()
    {
        var surface = new FakeSurface();
        var releases = 0;
        var subject = new FlyoutLifecycle(surface, () => releases++);
        return (subject, surface, () => releases);
    }

    // ---------------------------------------------------------------- readiness

    [Fact]
    public void A_presentable_surface_shows_the_menu_immediately()
    {
        var (subject, surface, releases) = Create();

        subject.Show(new Point(10, 10));

        Assert.True(surface.MenuShown);
        Assert.True(subject.IsPresented);
        Assert.Equal(0, releases());
    }

    /// <summary>
    /// The click is KEPT and shown when the tree can host it — not repeated, and not dropped.
    /// </summary>
    [Fact]
    public void An_unready_surface_waits_and_then_presents_once()
    {
        var (subject, surface, releases) = Create();
        surface.Presentable = false;

        subject.Show(new Point(10, 10));

        Assert.False(surface.MenuShown);
        Assert.True(subject.IsAwaitingReadiness);

        surface.Presentable = true;
        subject.OnSurfaceReady();

        Assert.True(surface.MenuShown);
        Assert.Equal(1, surface.WatchInstalls);
        Assert.Equal(0, releases());
    }

    /// <summary>A second readiness signal must not present a second time.</summary>
    [Fact]
    public void Readiness_arriving_twice_presents_once()
    {
        var (subject, surface, _) = Create();
        surface.Presentable = false;
        subject.Show(new Point(10, 10));

        surface.Presentable = true;
        subject.OnSurfaceReady();
        subject.OnSurfaceReady();

        Assert.Equal(1, surface.Calls.Count(c => c == "PresentMenu"));
    }

    [Fact]
    public void A_surface_that_never_becomes_usable_releases_the_slot()
    {
        var (subject, surface, releases) = Create();
        surface.Presentable = false;
        subject.Show(new Point(10, 10));

        subject.OnSurfaceReady();   // still not presentable

        Assert.False(surface.MenuShown);
        Assert.Equal(1, releases());
    }

    // ---------------------------------------------------------------- the watch

    /// <summary>
    /// <c>SetWinEventHook</c> returning zero is a documented failure, and it is the dismissal signal in
    /// exactly the states where the menu's own close was measured absent.
    /// </summary>
    [Fact]
    public void A_refused_dismissal_watch_leaves_no_menu_and_releases_the_slot()
    {
        var (subject, surface, releases) = Create();
        surface.WatchInstallable = false;

        subject.Show(new Point(10, 10));

        // The menu must not be LEFT: nothing could ever dismiss it, so it is torn down again and the
        // slot is freed rather than held for the rest of the session.
        Assert.False(surface.MenuShown);
        Assert.Equal(1, releases());
    }

    /// <summary>
    /// The watch is installed AFTER the menu is shown, and the order is measured rather than preferred.
    /// </summary>
    /// <remarks>
    /// Installing it first looked tidier — nothing is shown that cannot be dismissed — and on a real
    /// desktop it made the flyout dismiss ITSELF, because showing the popup changes the foreground and the
    /// watch caught its own churn. The assertion was reversed to match what the platform does, not to make
    /// a test pass: the refusal path above still tears the menu down.
    /// </remarks>
    [Fact]
    public void The_watch_is_installed_after_the_menu_is_shown()
    {
        var (subject, surface, _) = Create();

        subject.Show(new Point(10, 10));

        Assert.True(
            surface.Calls.IndexOf("PresentMenu") < surface.Calls.IndexOf("TryInstallDismissalWatch"),
            $"[{string.Join(", ", surface.Calls)}]");
    }

    // ---------------------------------------------------------------- the blind window

    /// <summary>
    /// A foreground change that lands BETWEEN showing the menu and installing the watch is still a
    /// dismissal, even though no callback will ever report it.
    /// </summary>
    /// <remarks>
    /// The watch reports FUTURE transitions only. Whatever happens in the interval produces no event at
    /// all, so without this the slot would stay held until some later, unrelated change — the same
    /// liveness shape as the original defect, one layer along. Compared against THIS menu's own baseline,
    /// not against "is the foreground foreign": in the states the defect lives in, the foreground is
    /// already foreign when the menu legitimately opens.
    /// </remarks>
    [Fact]
    public void A_foreground_change_during_the_blind_window_dismisses_the_menu()
    {
        var (subject, surface, releases) = Create();
        surface.MoveForegroundOnPresent = 999;

        subject.Show(new Point(10, 10));

        Assert.False(surface.MenuShown);
        subject.OnMenuClosed();
        Assert.Equal(1, releases());
    }

    /// <summary>And an unchanged foreground leaves the menu alone — including when it was never ours.</summary>
    [Fact]
    public void A_menu_opened_under_a_foreign_foreground_stays_open_while_nothing_changes()
    {
        var (subject, surface, releases) = Create();
        surface.Foreground = 777;   // someone else already had it, as in cells B, C and D

        subject.Show(new Point(10, 10));

        Assert.True(surface.MenuShown);
        Assert.True(subject.IsPresented);
        Assert.Equal(0, releases());
    }

    // ---------------------------------------------------------------- dismissal

    [Fact]
    public void A_foreign_foreground_hides_the_menu()
    {
        var (subject, surface, releases) = Create();
        subject.Show(new Point(10, 10));

        subject.OnForeignForeground();

        Assert.False(surface.MenuShown);

        // The real menu answers with Closed, which is what frees the slot.
        subject.OnMenuClosed();
        Assert.Equal(1, releases());
    }

    /// <summary>A hide that did not happen produces no close, so the slot must be freed here.</summary>
    [Fact]
    public void A_failed_hide_releases_the_slot_anyway()
    {
        var (subject, surface, releases) = Create();
        subject.Show(new Point(10, 10));
        surface.HideSucceeds = false;

        subject.OnForeignForeground();

        Assert.Equal(1, releases());
        Assert.True(surface.WatchRemovals > 0);
    }

    [Fact]
    public void A_foreign_foreground_before_anything_was_presented_does_nothing()
    {
        var (subject, surface, releases) = Create();

        subject.OnForeignForeground();

        Assert.Empty(surface.Calls);
        Assert.Equal(0, releases());
    }

    // ---------------------------------------------------------------- disposal

    [Fact]
    public void Dispose_before_readiness_releases_the_slot()
    {
        var (subject, surface, releases) = Create();
        surface.Presentable = false;
        subject.Show(new Point(10, 10));

        subject.Dispose();

        Assert.Equal(1, releases());
    }

    [Fact]
    public void Dispose_with_the_menu_open_releases_the_slot_and_removes_the_watch()
    {
        var (subject, surface, releases) = Create();
        subject.Show(new Point(10, 10));

        subject.Dispose();

        Assert.Equal(1, releases());
        Assert.True(surface.WatchRemovals > 0);
        Assert.True(surface.WindowHides > 0);
    }

    // ---------------------------------------------------------------- exactly once

    /// <summary>
    /// Every terminal frees the slot EXACTLY once. Releasing twice would hand a second caller a slot the
    /// first still believes it holds.
    /// </summary>
    [Fact]
    public void Every_terminal_releases_exactly_once()
    {
        var (subject, _, releases) = Create();
        subject.Show(new Point(10, 10));

        subject.OnMenuClosed();
        subject.OnMenuClosed();
        subject.OnForeignForeground();
        subject.Dispose();
        subject.Dispose();

        Assert.Equal(1, releases());
    }

    [Fact]
    public void A_failed_presentation_releases_the_slot_and_rethrows()
    {
        var (subject, surface, releases) = Create();
        surface.PresentThrows = true;

        Assert.Throws<InvalidOperationException>(() => subject.Show(new Point(10, 10)));

        Assert.Equal(1, releases());
        Assert.True(surface.WatchRemovals > 0);
    }

    [Fact]
    public void A_request_after_a_completed_one_releases_again()
    {
        var (subject, _, releases) = Create();

        subject.Show(new Point(10, 10));
        subject.OnMenuClosed();
        Assert.Equal(1, releases());

        subject.Show(new Point(20, 20));
        subject.OnMenuClosed();
        Assert.Equal(2, releases());
    }
}
