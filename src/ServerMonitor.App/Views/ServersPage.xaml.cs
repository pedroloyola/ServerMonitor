using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>UI.4 §3: the Servidores directory. Fresh per navigation; the view model is disposed on Unloaded.</summary>
public sealed partial class ServersPage : Page, IDisposable
{
    public ServersPage(ServersViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        // Released on Unloaded AND when navigation replaces the page (a page replaced before Loaded never unloads).
        Unloaded += (_, _) => Dispose();
        Loaded += OnLoaded;
        // AdaptiveTrigger state changes do not raise the VisualStateGroup events, so the reflow is observed where it
        // happens: the width states swap the repeater's template.
        ServersRepeater.RegisterPropertyChangedCallback(ItemsRepeater.ItemTemplateProperty, OnRowTemplateChanged);
        ServersRepeater.GotFocus += OnRowGotFocus;
    }

    private Guid? _focusedRowId;
    private bool _reflowPending;

    // By the time the template changes the old rows are already cleared, so the focused server is kept here (by the
    // repeater's index: x:Bind rows do not carry their item as DataContext).
    private void OnRowGotFocus(object sender, RoutedEventArgs e)
    {
        var index = e.OriginalSource is UIElement row ? ServersRepeater.GetElementIndex(row) : -1;
        _focusedRowId = index >= 0 && index < ViewModel.Rows.Count ? ViewModel.Rows[index].ServerId : null;
    }

    // Beacon r1 SHOULD-1: back from the interim page, focus returns to the row that opened it; otherwise to the search.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var index = ViewModel.TakeReturnFocusIndex();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!RepeaterFocus.FocusIndex(ServersRepeater, index))
            {
                SearchBox.Focus(FocusState.Programmatic);
            }
        });
    }

    // Beacon r1 SHOULD-5: Wide/Mid/Stacked swap the row template, which recreates the rows and would drop focus to the
    // top of the window; the focused row keeps focus across the reflow.
    // Only while a row holds focus (the old, cleared row keeps it until the new rows exist).
    private void OnRowTemplateChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_reflowPending || _focusedRowId is not { } focusedId || XamlRoot is null
            || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is not Controls.ServerTableRowButton)
        {
            return;
        }

        var index = -1;
        for (var i = 0; i < ViewModel.Rows.Count; i++)
        {
            if (ViewModel.Rows[i].ServerId == focusedId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        // The new template's rows exist only after the repeater's next layout pass; focusing earlier hits the old,
        // pinned row (QA r2: focus ended on another server with no position).
        void OnLayoutUpdated(object? s, object args)
        {
            ServersRepeater.LayoutUpdated -= OnLayoutUpdated;
            _reflowPending = false;
            RepeaterFocus.FocusIndexLater(ServersRepeater, index);
        }

        _reflowPending = true;
        ServersRepeater.LayoutUpdated += OnLayoutUpdated;
    }

    public ServersViewModel ViewModel { get; }

    /// <summary>Idempotent: the view model unsubscribes once.</summary>
    public void Dispose() => ViewModel.Dispose();

    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => ViewModel.BackToOverviewCommand.Execute(null);

    // "Limpar pesquisa" disappears once it worked; focus returns to the search box (UI.3 pattern).
    private void OnClearSearchClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, SearchBox);
}
