using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.Shell.Tray;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Shell;

/// <summary>
/// The native tray menu, tested against the REAL <c>HMENU</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>CreatePopupMenu</c> and <c>AppendMenuW</c> need no message loop, no desktop interaction and no
/// window, so the menu the user actually gets is built here and read back with the same API a screen
/// reader would use. That matters: the alternative is asserting the order against a second copy of the
/// list, which passes whether or not <c>Build</c> ever puts it on a menu.
/// </para>
/// <para>
/// What cannot be tested here is what M13-QA-11 measured on a real desktop — that the menu takes the
/// foreground, stays up, and dismisses on a click outside. Those are properties of the window system,
/// not of this class, and pretending otherwise with a double would be a test that passes for the wrong
/// reason.
/// </para>
/// </remarks>
public sealed class TrayContextMenuTests
{
    private const uint MF_BYPOSITION = 0x0400;
    private const uint MF_SEPARATOR = 0x0800;

    private static TrayContextMenu Create(AppThemePreference preference = AppThemePreference.System) =>
        new(new FakeLocalizationService(), new StubThemeService(preference), NullLogger.Instance);

    // ----------------------------------------------------------------- the built menu

    [Fact]
    public void The_built_menu_carries_the_five_commands_in_the_agreed_order()
    {
        var menu = CreatePopupMenu();

        try
        {
            Assert.True(Create().Build(menu));

            // The fake resolves an unknown key to the key itself, so this asserts the RESW KEYS reach the
            // menu -- a missing translation would show the key, not silently show nothing.
            Assert.Equal(
                [
                    "TrayOpenMenuItem",
                    "TrayCompactModeMenuItem",
                    "TrayRefreshAllMenuItem",
                    "TraySettingsMenuItem",
                    "TrayExitMenuItem"
                ],
                Labels(menu));
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [Fact]
    public void A_separator_sits_immediately_before_Exit_and_nowhere_else()
    {
        var menu = CreatePopupMenu();

        try
        {
            Assert.True(Create().Build(menu));

            var separators = Enumerable.Range(0, GetMenuItemCount(menu))
                .Where(position => IsSeparator(menu, position))
                .ToArray();

            // Exactly one, and it is the entry before the last -- Exit is destructive and is kept away
            // from the item above it by a gap, not merely by being last.
            Assert.Equal([GetMenuItemCount(menu) - 2], separators);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [Fact]
    public void Every_item_carries_the_id_that_maps_back_to_its_own_command()
    {
        var menu = CreatePopupMenu();

        try
        {
            Assert.True(Create().Build(menu));

            var roundTripped = Enumerable.Range(0, GetMenuItemCount(menu))
                .Where(position => !IsSeparator(menu, position))
                .Select(position => TrayContextMenu.FromCommandId((int)GetMenuItemID(menu, position)))
                .ToArray();

            // The whole path, end to end: order -> id on the menu -> command handed to the adapter. An
            // off-by-one anywhere in it lands the user on a neighbouring item, and Exit has a neighbour.
            Assert.Equal(TrayFlyoutMenu.Order.Cast<TrayCommand?>(), roundTripped);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    // ----------------------------------------------------------------- the selection contract

    [Fact]
    public void Nothing_chosen_is_not_a_command()
    {
        // TrackPopupMenuEx returns 0 when the menu was dismissed. Reading that as a command would run the
        // FIRST item every time the user clicked away -- and the first item opens a window.
        Assert.Null(TrayContextMenu.FromCommandId(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public void A_selection_outside_the_menu_is_not_a_command(int selection) =>
        Assert.Null(TrayContextMenu.FromCommandId(selection));

    [Fact]
    public void Every_command_survives_the_trip_through_a_menu_id()
    {
        Assert.All(
            Enum.GetValues<TrayCommand>(),
            command => Assert.Equal(
                command, TrayContextMenu.FromCommandId((int)TrayContextMenu.ToCommandId(command))));
    }

    // ----------------------------------------------------------------- the theme mapping

    // Two facts rather than a Theory: the mode is an internal nested type, and a public test signature
    // cannot name it.
    [Fact]
    public void Dark_forces_dark() =>
        Assert.Equal(
            TrayContextMenu.PreferredAppMode.ForceDark,
            TrayContextMenu.PreferredAppModeFor(AppThemePreference.Dark));

    [Fact]
    public void Light_forces_light() =>
        Assert.Equal(
            TrayContextMenu.PreferredAppMode.ForceLight,
            TrayContextMenu.PreferredAppModeFor(AppThemePreference.Light));

    [Fact]
    public void Following_the_system_allows_dark_rather_than_forcing_anything()
    {
        // The distinction is the point. ForceLight would also "work" in a light session and would be
        // wrong the moment the user's Windows theme went dark, which is the case the setting exists for.
        var mode = TrayContextMenu.PreferredAppModeFor(AppThemePreference.System);

        Assert.Equal(TrayContextMenu.PreferredAppMode.AllowDark, mode);
        Assert.NotEqual(TrayContextMenu.PreferredAppMode.ForceLight, mode);
        Assert.NotEqual(TrayContextMenu.PreferredAppMode.ForceDark, mode);
    }

    [Fact]
    public void Building_the_menu_applies_the_theme_in_force_at_that_moment()
    {
        // Without this, removing the theme call from Build changes nothing any test can see: the mapping
        // stays correct and stays unused. What is witnessed here is the DECISION -- the current
        // preference was read and mapped while the menu was being built. Whether Windows honoured it
        // cannot be observed from managed code, and this test does not pretend to.
        var menu = CreatePopupMenu();
        var contextMenu = Create(AppThemePreference.Dark);

        try
        {
            Assert.Null(contextMenu.LastAppliedMode);

            Assert.True(contextMenu.Build(menu));

            Assert.Equal(TrayContextMenu.PreferredAppMode.ForceDark, contextMenu.LastAppliedMode);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [Fact]
    public void Every_preference_has_a_mode_and_no_two_share_one()
    {
        var preferences = Enum.GetValues<AppThemePreference>();
        var modes = preferences.Select(TrayContextMenu.PreferredAppModeFor).ToArray();

        Assert.Equal(preferences.Length, modes.Distinct().Count());
    }

    // ----------------------------------------------------------------- helpers

    private static string[] Labels(nint menu) =>
        Enumerable.Range(0, GetMenuItemCount(menu))
            .Where(position => !IsSeparator(menu, position))
            .Select(position =>
            {
                var text = new StringBuilder(256);
                GetMenuStringW(menu, (uint)position, text, text.Capacity, MF_BYPOSITION);
                return text.ToString();
            })
            .ToArray();

    private static bool IsSeparator(nint menu, int position) =>
        (GetMenuState(menu, (uint)position, MF_BYPOSITION) & MF_SEPARATOR) != 0;

    private sealed class StubThemeService(AppThemePreference current) : IThemeService
    {
        public AppThemePreference Current { get; } = current;

        public void Attach(FrameworkElement rootElement) => throw new NotSupportedException();

        public void Detach(FrameworkElement rootElement) => throw new NotSupportedException();

        public void Apply(AppThemePreference preference) => throw new NotSupportedException();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(nint menu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(nint menu, int position);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(nint menu, uint item, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(nint menu, uint item, StringBuilder text, int count, uint flags);
}
