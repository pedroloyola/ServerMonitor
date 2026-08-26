using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ServerMonitor.App.Windowing;

/// <summary>
/// The real <see cref="IWindowPlacementAdapter"/> over AppWindow / OverlappedPresenter / DisplayArea.
/// This is the only place that touches native window geometry and per-monitor DPI; everything above
/// it works with the WinUI-free windowing model so it can be faked in tests. Always-on-top uses the
/// presenter's supported <c>IsAlwaysOnTop</c> flag — no timers, no z-order polling.
/// </summary>
public sealed class AppWindowPlacementAdapter(ILogger<AppWindowPlacementAdapter> logger) : IWindowPlacementAdapter
{
    private const int MdtEffectiveDpi = 0;
    private const uint DefaultDpi = 96;

    private Window? _window;
    private AppWindow? _appWindow;

    public bool IsAttached => _appWindow is not null;

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window is not null && !ReferenceEquals(_window, window))
        {
            throw new InvalidOperationException("The window placement adapter is already attached.");
        }

        _window = window;
        _appWindow = window.AppWindow;
    }

    public WindowPlacement? GetPlacement()
    {
        if (_appWindow is null)
        {
            return null;
        }

        // A minimized window reports meaningless position/size; skip so we never persist garbage.
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            return null;
        }

        var position = _appWindow.Position;
        var size = _appWindow.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        var bounds = new WindowBounds(position.X, position.Y, size.Width, size.Height);
        return new WindowPlacement(bounds, GetWindowDpiScalePercent());
    }

    public IReadOnlyList<DisplayWorkArea> GetDisplays()
    {
        var displays = new List<(DisplayWorkArea Area, bool IsPrimary)>();
        DisplayId? primaryId = null;
        try
        {
            primaryId = DisplayArea.Primary?.DisplayId;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not resolve the primary display.");
        }

        // Iterate by index rather than foreach: enumerating the WinRT IReadOnlyList<DisplayArea>
        // returned by FindAll() goes through the CsWinRT generic IEnumerable projection, which throws
        // "ClassFactory cannot supply requested class" in an unpackaged/self-contained app. The
        // indexer projects each element individually and is safe.
        var allDisplays = DisplayArea.FindAll();
        for (var index = 0; index < allDisplays.Count; index++)
        {
            var displayArea = allDisplays[index];
            var workArea = displayArea.WorkArea;
            if (workArea.Width <= 0 || workArea.Height <= 0)
            {
                continue;
            }

            var isPrimary = primaryId is { } id && displayArea.DisplayId.Value == id.Value;
            displays.Add((
                new DisplayWorkArea(
                    workArea.X,
                    workArea.Y,
                    workArea.Width,
                    workArea.Height,
                    GetMonitorDpiScalePercent(displayArea.DisplayId)),
                isPrimary));
        }

        if (displays.Count == 0)
        {
            return [];
        }

        // The resolver treats the first display as primary for centering fallbacks.
        return displays
            .OrderByDescending(entry => entry.IsPrimary)
            .Select(entry => entry.Area)
            .ToList();
    }

    public void ApplyBounds(WindowBounds bounds)
    {
        if (_appWindow is null)
        {
            return;
        }

        _appWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    public void ConfigurePresenter(WindowMode mode, WindowSizeConstraints constraints)
    {
        if (_appWindow?.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        var isStandard = mode == WindowMode.Standard;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
        presenter.IsMinimizable = true;
        // Compact is a fixed-footprint utility widget: non-resizable and non-maximizable keeps its
        // layout predictable and removes the maximize caption button for a widget-like chrome.
        presenter.IsResizable = isStandard;
        presenter.IsMaximizable = isStandard;
    }

    public void SetAlwaysOnTop(bool enabled)
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = enabled;
        }
    }

    /// <summary>
    /// Width, in physical pixels, that the system reserves on the right of the title bar for the
    /// native caption buttons. Meaningful only while the content is extended into the title bar;
    /// it reflects the current DPI and the presenter's button set (e.g. maximize hidden), so a
    /// custom title bar must derive its reserved space from this rather than a fixed constant.
    /// Returns 0 when unavailable.
    /// </summary>
    public int GetCaptionRightInset() => _appWindow?.TitleBar.RightInset ?? 0;

    private int GetWindowDpiScalePercent()
    {
        if (_window is null)
        {
            return WindowPlacementSettings.DefaultDpiScalePercent;
        }

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var dpi = GetDpiForWindow(hwnd);
            return ToScalePercent(dpi != 0 ? dpi : DefaultDpi);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not read the window DPI; assuming 100%.");
            return WindowPlacementSettings.DefaultDpiScalePercent;
        }
    }

    private int GetMonitorDpiScalePercent(DisplayId displayId)
    {
        try
        {
            var monitor = Win32Interop.GetMonitorFromDisplayId(displayId);
            if (monitor != nint.Zero
                && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0
                && dpiX != 0)
            {
                return ToScalePercent(dpiX);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not read a monitor DPI; assuming 100%.");
        }

        return WindowPlacementSettings.DefaultDpiScalePercent;
    }

    private static int ToScalePercent(uint dpi) => (int)Math.Round(dpi / (double)DefaultDpi * 100);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
