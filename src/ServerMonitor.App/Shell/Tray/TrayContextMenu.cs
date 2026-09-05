using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// The tray context menu, as a NATIVE shell menu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this replaces the XAML flyout.</b> Microsoft documents that a notification-area menu requires
/// the owning window to be the foreground window before <c>TrackPopupMenu</c>, and that otherwise
/// <i>"the menu will not disappear when the user clicks outside of the menu"</i>. That is precisely what
/// M13-QA-11 measured: with the Dashboard not in the foreground, the menu opened and then could not be
/// dismissed, and our window never won the foreground at all. Four rounds were spent hunting a dismissal
/// signal — <c>MenuFlyout.Closed</c>, <c>Window.Deactivated</c>, a foreground-change hook — and every one
/// was absent or a broad proxy. <b>There was no signal to find:</b> the behaviour was the documented
/// consequence of never being foreground.
/// </para>
/// <para>
/// <c>TPM_RETURNCMD</c> makes the call MODAL and returns the chosen command, so the whole family goes
/// away by construction: there is no close event to subscribe to, no slot to release, and no liveness to
/// prove — <b>the function returns when the menu closes</b>. <c>XamlRoot</c> leaves the picture with it,
/// and that was the cause of the lost first click, because it needed a loaded XAML tree, which needed
/// <c>Activate</c>, which never won the foreground.
/// </para>
/// <para>
/// It also fixes the truncation. A native menu belongs to the shell and is not drawn beneath the Windows
/// overflow panel; the XAML flyout was not topmost, and the human's log shows the overflow island holding
/// the foreground every single time the menu opened.
/// </para>
/// <para>
/// <b>On focus.</b> Taking the foreground here is the documented mechanism for an action the user asked
/// for by right-clicking, and the previous foreground is restored in a <c>finally</c>. It is not focus
/// theft: measurement showed the old path never took the foreground at all, which is the defect.
/// </para>
/// <para>
/// The owner is <see cref="TrayHostWindow"/> — top-level, unowned, never shown — and deliberately not the
/// Dashboard: the tray affordance may not depend on the main window, which is the S2-T invariant.
/// </para>
/// <para>
/// <b>Theme.</b> A native menu follows the app's light/dark preference through two undocumented
/// <c>uxtheme</c> ordinals, applied BEST EFFORT: see <see cref="ApplyPreferredTheme"/>. The alternative
/// considered and rejected was a XAML window styled to look like a menu, which is the same class of
/// problem this file was written to bury.
/// </para>
/// </remarks>
internal sealed class TrayContextMenu(
    ILocalizationService localization, IThemeService themeService, ILogger logger)
{
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint WM_NULL = 0x0000;

    private readonly ILocalizationService _localization =
        localization ?? throw new ArgumentNullException(nameof(localization));

    private readonly IThemeService _themeService =
        themeService ?? throw new ArgumentNullException(nameof(themeService));

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Shows the menu and returns the chosen command, or <c>null</c> when it was dismissed.
    /// </summary>
    /// <remarks>
    /// Blocks until the menu closes. That is the point: the caller learns the outcome by return, so
    /// nothing can be left open and nothing has to be notified.
    /// </remarks>
    internal TrayCommand? Show(nint ownerWindow, Point anchor)
    {
        if (ownerWindow == nint.Zero)
        {
            _logger.LogError("The tray context menu has no owner window; it cannot be shown.");
            return null;
        }

        var menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            _logger.LogError("The tray context menu could not be created.");
            return null;
        }

        // The foreground we must give back, whatever happens below.
        var previousForeground = GetForegroundWindow();

        try
        {
            if (!Build(menu))
            {
                return null;
            }

            // DOCUMENTED PRECONDITION, not an optimisation: without it the menu does not dismiss when the
            // user clicks away. Whether the call succeeds is logged rather than assumed, because the same
            // foreground rules defeated the XAML path's Activate().
            var tookForeground = SetForegroundWindow(ownerWindow);
            if (!tookForeground)
            {
                _logger.LogWarning(
                    "The tray context menu could not take the foreground; dismissal may behave oddly.");
            }

            var selection = TrackPopupMenuEx(
                menu,
                TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                anchor.X,
                anchor.Y,
                ownerWindow,
                nint.Zero);

            // Documented companion to the call above: without forcing a task switch, a second showing of
            // the menu appears and immediately disappears.
            _ = PostMessageW(ownerWindow, WM_NULL, nint.Zero, nint.Zero);

            return FromCommandId(selection);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The tray context menu failed.");
            return null;
        }
        finally
        {
            _ = DestroyMenu(menu);

            // Give the foreground back, so the window the user was in keeps its keyboard focus.
            if (previousForeground != nint.Zero && previousForeground != ownerWindow)
            {
                _ = SetForegroundWindow(previousForeground);
            }
        }
    }

    /// <summary>
    /// Builds the menu from the SAME order and resource keys the product fixed, so the native menu cannot
    /// drift from the one the tests pin.
    /// </summary>
    internal bool Build(nint menu)
    {
        ApplyPreferredTheme(_themeService.Current);

        foreach (var command in TrayFlyoutMenu.Order)
        {
            if (command == TrayCommand.Exit && !AppendMenuW(menu, MF_SEPARATOR, nint.Zero, null))
            {
                _logger.LogError("The tray context menu separator could not be added.");
                return false;
            }

            var text = _localization.GetString(TrayFlyoutMenu.ResourceKeyFor(command));

            if (!AppendMenuW(menu, MF_STRING, ToCommandId(command), text))
            {
                _logger.LogError("A tray context menu item could not be added.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The undocumented uxtheme mode this preference maps to. Pure, and separated from the interop for
    /// exactly that reason: the mapping is the part that can be wrong in a way a test can catch.
    /// </summary>
    internal static PreferredAppMode PreferredAppModeFor(AppThemePreference preference) => preference switch
    {
        // "System" must not FORCE anything: AllowDark lets the menu follow the OS setting, which is what
        // the user asked for by choosing to follow the system.
        AppThemePreference.System => PreferredAppMode.AllowDark,
        AppThemePreference.Dark => PreferredAppMode.ForceDark,
        AppThemePreference.Light => PreferredAppMode.ForceLight
        // No `_ =>` arm: CS8509 is an error here, so a new preference cannot be added without deciding
        // what a native menu does about it.
    };

    /// <summary>
    /// Asks Windows to draw menus in the app's theme. BEST EFFORT, BY DESIGN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no public API for this. The two entry points are exported from <c>uxtheme.dll</c> BY
    /// ORDINAL ONLY (135 and 136), they are undocumented, and they are absent on older builds. So every
    /// step is conditional and every failure is silent.
    /// </para>
    /// <para>
    /// That asymmetry is the whole policy, and it is deliberate: <b>a menu in the wrong colour is a
    /// blemish; a menu that does not open is the defect M13-QA-11 exists to remove.</b> Nothing in here
    /// may throw, and nothing in here may decide whether the menu is shown.
    /// </para>
    /// </remarks>
    private void ApplyPreferredTheme(AppThemePreference preference)
    {
        // Recorded BEFORE the interop, because the interop is the part that is allowed to be absent. The
        // field witnesses the DECISION -- that the current preference was read and mapped at build time --
        // which is the half a test can hold. Whether uxtheme honoured it cannot be observed from managed
        // code at all, and is left to the eye.
        LastAppliedMode = PreferredAppModeFor(preference);

        try
        {
            var uxtheme = ThemeInterop.Value;
            if (uxtheme is null)
            {
                return;
            }

            uxtheme.SetPreferredAppMode(LastAppliedMode.Value);
            uxtheme.FlushMenuThemes();
        }
        catch (Exception exception)
        {
            // Debug, not warning: on a build without these ordinals this is the expected outcome, and a
            // warning every time the user opens the menu would be noise reporting normal operation.
            _logger.LogDebug(exception, "The tray context menu could not apply the app theme.");
        }
    }

    /// <summary>The mode the last build mapped to, or <c>null</c> if no menu has been built yet.</summary>
    internal PreferredAppMode? LastAppliedMode { get; private set; }

    /// <summary>Resolved once per process: the ordinals either exist on this build or they never will.</summary>
    private static readonly Lazy<UxThemeEntryPoints?> ThemeInterop = new(ResolveThemeInterop);

    private static UxThemeEntryPoints? ResolveThemeInterop()
    {
        try
        {
            var module = LoadLibraryW("uxtheme.dll");
            if (module == nint.Zero)
            {
                return null;
            }

            // MAKEINTRESOURCE: the ordinal travels as the pointer itself. These exports have no names.
            var setMode = GetProcAddress(module, 135);
            var flush = GetProcAddress(module, 136);

            if (setMode == nint.Zero || flush == nint.Zero)
            {
                return null;
            }

            return new UxThemeEntryPoints(
                Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(setMode),
                Marshal.GetDelegateForFunctionPointer<FlushMenuThemesDelegate>(flush));
        }
        catch (Exception)
        {
            // Unavailable is a normal answer here, and there is no logger on a static path.
            return null;
        }
    }

    private sealed record UxThemeEntryPoints(
        SetPreferredAppModeDelegate SetPreferredAppMode, FlushMenuThemesDelegate FlushMenuThemes);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate PreferredAppMode SetPreferredAppModeDelegate(PreferredAppMode mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void FlushMenuThemesDelegate();

    /// <summary>The undocumented uxtheme mode. Values are fixed by Windows, not by us.</summary>
    internal enum PreferredAppMode
    {
        Default = 0,
        AllowDark = 1,
        ForceDark = 2,
        ForceLight = 3
    }

    // Zero is what TrackPopupMenuEx returns for "nothing was chosen", so the ids start at one.
    internal static nint ToCommandId(TrayCommand command) => (nint)((int)command + 1);

    internal static TrayCommand? FromCommandId(int selection) =>
        selection <= 0 || selection > TrayFlyoutMenu.Order.Length
            ? null
            : (TrayCommand)(selection - 1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu, uint flags, nint id, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(
        nint menu, uint flags, int x, int y, nint owner, nint parameters);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryW(string name);

    // The ordinal overload: these uxtheme exports are nameless, so there is no string to pass.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetProcAddress(nint module, nint ordinal);
}
