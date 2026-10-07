using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>The list host (the window focuses its first row when Compact opens with servers).</summary>
    internal ItemsRepeater Repeater => CompactRepeater;

    /// <summary>The first focusable thing of the state block, if it has a real action.</summary>
    internal Control? StateAction =>
        AddServerButton.Visibility == Visibility.Visible ? AddServerButton
        : ManageHiddenButton.Visibility == Visibility.Visible ? ManageHiddenButton
        : null;

    // D-UI8-7: a row opens its server's Detail through the window-mode exit (Standard first, then the guarded command).
    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CompactServerRowViewModel row })
        {
            WindowMode?.OpenServerDetailCommand.Execute(row.ServerId);
        }
    }
}
