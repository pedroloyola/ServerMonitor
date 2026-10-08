using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Controls;

/// <summary>
/// UI.8: the Compact window's body (summary, rows or state block, footer). A pure VIEW: it renders
/// <see cref="CompactPresentationViewModel"/> - itself a view over the one dashboard's cards - and forwards the user's
/// intents to <see cref="WindowModeViewModel"/>, which owns every exit (RC-3). No engine, store, clock or timer here.
/// </summary>
public sealed partial class CompactShell : UserControl
{
    public CompactShell()
    {
        InitializeComponent();
    }

    public CompactPresentationViewModel? Presentation { get; private set; }

    public WindowModeViewModel? WindowMode { get; private set; }

    /// <summary>Connects the shell to its two view models (once, by the window that hosts it).</summary>
    public void Initialize(CompactPresentationViewModel presentation, WindowModeViewModel windowMode)
    {
        Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        WindowMode = windowMode ?? throw new ArgumentNullException(nameof(windowMode));
        Bindings.Update();
    }

    /// <summary>The list host (the window focuses its first visible row when Compact opens with servers).</summary>
    internal ItemsRepeater Repeater => CompactRepeater;

    /// <summary>
    /// Beacon c2 B-6: the first row VISIBLE in the list's viewport among the realized rows (the scroll position is kept, so
    /// row 1 may not be realized at all), chosen by <see cref="Views.CompactEntryFocus.FirstVisibleRow"/>; null until one
    /// is laid out in view.
    /// </summary>
    internal Control? FirstVisibleRow()
    {
        var count = CompactRepeater.ItemsSourceView?.Count ?? 0;
        var realized = new List<(int Index, double Top, double Height)>();
        for (var index = 0; index < count; index++)
        {
            if (CompactRepeater.TryGetElement(index) is Control { IsLoaded: true, Visibility: Visibility.Visible } row && row.ActualHeight > 0)
            {
                var top = row.TransformToVisual(ListScroller).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                realized.Add((index, top, row.ActualHeight));
            }
        }

        return Views.CompactEntryFocus.FirstVisibleRow(realized, ListScroller.ViewportHeight) is { } visible
            ? CompactRepeater.TryGetElement(visible) as Control
            : null;
    }

    /// <summary>The state block's real action for this presentation state (whether or not it is laid out yet).</summary>
    internal Control? StateActionFor(CompactPresentationViewModel presentation) =>
        presentation.ShowsAddAction ? AddServerButton
        : presentation.ShowsHiddenAction ? ManageHiddenButton
        : null;

    // Prism c1 P-4: the name's tooltip only when the name is actually cut (the row's accessible name always carries it).
    private void OnNameTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        for (DependencyObject? node = sender; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ServerTableRowButton row)
            {
                ToolTipService.SetToolTip(row, sender.IsTextTrimmed ? sender.Text : null);
                return;
            }
        }
    }

    // D-UI8-7: a row opens its server's Detail through the window-mode exit (Standard first, then the guarded command). The
    // id is the row's compiled CommandParameter binding - never its DataContext, which the repeater leaves unset (c2 B-1).
    private void OnRowClick(object sender, RoutedEventArgs e) =>
        CompactRowActivation.Open((sender as ButtonBase)?.CommandParameter, WindowMode?.OpenServerDetailCommand);
}
