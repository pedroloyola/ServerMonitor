using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.5 fix round 2 (Beacon C1 M3): a live theme switch (Escuro → Claro with the page open) left the glass cards of the
/// open page with the previous theme's acrylic until the page was recreated. Measured at runtime: the page itself never
/// receives ActualThemeChanged (only the window root, whose RequestedTheme the ThemeService sets, does), and re-applying
/// the style or the brush on the live elements still rendered a different blend than a page entering the tree. So the
/// page listens to its window root and, after a theme change, REMOUNTS its content (out of the tree and back in, the
/// same objects: bindings and view model untouched) - exactly what re-entering the page does - then restores the page
/// scroller's vertical offset (Cortex C2 R-1) and gives focus back to the element that had it. Attached once per page; idempotent.
/// </summary>
public static class SaThemeRefresh
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SaThemeRefresh), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty SubscriptionProperty = DependencyProperty.RegisterAttached(
        "Subscription", typeof(object), typeof(SaThemeRefresh), new PropertyMetadata(null));

    public static bool GetIsEnabled(Page element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Page element, bool value) => element.SetValue(IsEnabledProperty, value);

    // Set on a Page: the refresh remounts the page's own Content.
    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Page page)
        {
            return;
        }

        page.Loaded -= OnLoaded;
        page.Unloaded -= OnUnloaded;
        if (e.NewValue is true)
        {
            page.Loaded += OnLoaded;
            page.Unloaded += OnUnloaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var page = (Page)sender;
        (page.GetValue(SubscriptionProperty) as RootSubscription)?.Dispose();
        if (page.XamlRoot?.Content is FrameworkElement root)
        {
            page.SetValue(SubscriptionProperty, new RootSubscription(page, root));
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var page = (Page)sender;
        (page.GetValue(SubscriptionProperty) as RootSubscription)?.Dispose();
        page.ClearValue(SubscriptionProperty);
    }

    /// <summary>Takes the page's content out of the tree and puts the same object back, keeping the focused element.</summary>
    public static bool Remount(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Content is not UIElement content || page.XamlRoot is null)
        {
            return false;
        }

        var focused = FocusManager.GetFocusedElement(page.XamlRoot) as Control;
        // Cortex C2 R-1: the page's scroller is part of the remounted content - its offset is kept explicitly.
        var scroller = FirstScrollViewer(content);
        var offset = scroller?.VerticalOffset ?? 0;
        page.Content = null;
        page.Content = content;
        page.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            scroller?.ChangeView(null, offset, null, disableAnimation: true);
            focused?.Focus(FocusState.Programmatic);
        });

        return true;
    }

    private static ScrollViewer? FirstScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (FirstScrollViewer(child) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private sealed class RootSubscription : IDisposable
    {
        private readonly Page _page;
        private readonly FrameworkElement _root;
        private ElementTheme _theme;

        public RootSubscription(Page page, FrameworkElement root)
        {
            _page = page;
            _root = root;
            _theme = root.ActualTheme;
            root.ActualThemeChanged += OnRootThemeChanged;
        }

        public void Dispose() => _root.ActualThemeChanged -= OnRootThemeChanged;

        private void OnRootThemeChanged(FrameworkElement sender, object args)
        {
            if (sender.ActualTheme == _theme)
            {
                return;
            }

            _theme = sender.ActualTheme;
            _page.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Remount(_page));
        }
    }
}
