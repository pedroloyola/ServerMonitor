using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>UI.4 §3: the Servidores directory. Fresh per navigation; the view model is disposed on Unloaded.</summary>
public sealed partial class ServersPage : Page, IDisposable
{
    private readonly ServerEditorReturnFocus? _editorReturnFocus;

    public ServersPage(ServersViewModel viewModel, ServerEditorReturnFocus? editorReturnFocus = null)
    {
        InitializeComponent();
        _editorReturnFocus = editorReturnFocus;
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

    // Beacon r1 SHOULD-1: back from the interim page, focus returns to the row that opened it; otherwise to the search -
    // or, with no server at all, to "Adicionar servidor" (Prism r2 C-R2-1: nothing to search for).
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var index = ViewModel.TakeReturnFocusIndex();
        // UI.7 B-5: back from the editor without saving, focus returns to the control that opened it.
        var editorOpener = _editorReturnFocus?.Take(NavigationDestination.Servers);
        if (ShellPageFocus.GetKeepSidebar(this))
        {
            AnnounceReturnNoticeAfterFocus();
            return;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!EditorReturnFocus.TryFocusNow(this, editorOpener) && !RepeaterFocus.FocusIndex(ServersRepeater, index))
            {
                ShellPageFocus.FocusHeading(this);
            }

            AnnounceReturnNoticeAfterFocus();
        });
    }

    /// <summary>UI.12 B12B-02-R: fixed, never localized - the "most recent" source of the Hidden / Removed notice.</summary>
    internal const string ReturnNoticeActivityId = "ServersNoticeShown";

    /// <summary>UI.12 B12B-02-R (Cortex: WinUI InfoBar's choice for success): do not cut off the heading being read.</summary>
    internal const AutomationNotificationProcessing ReturnNoticeProcessing = AutomationNotificationProcessing.CurrentThenMostRecent;

    private bool _returnNoticeAnnounced;

    // UI.12 B12B-02-R (real Narrator: only the page heading was heard after Ocultar): the notice arrives with the page entry,
    // and a polite live region raised in the same turn as the focus change is dropped. So the toast does not announce
    // itself (AnnouncesItself="False"); the page raises its text once as a notification in the turn AFTER its focus
    // decision. Only when the Detail's own Ocultar / Remover succeeded (the notice exists only then).
    private void AnnounceReturnNoticeAfterFocus()
    {
        if (!ViewModel.IsNoticeOpen)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var content = AutomationProperties.GetName(ReturnNotice);
            if (!ShouldAnnounceReturnNotice(_returnNoticeAnnounced, ViewModel.IsNoticeOpen, content)
                || (FrameworkElementAutomationPeer.FromElement(ReturnNotice) ?? FrameworkElementAutomationPeer.CreatePeerForElement(ReturnNotice)) is not { } peer)
            {
                return;
            }

            _returnNoticeAnnounced = true;
            peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, ReturnNoticeProcessing, content, ReturnNoticeActivityId);
        });
    }

    /// <summary>Pure rule (unit-tested): once per page, only while the notice is open and has something to say.</summary>
    internal static bool ShouldAnnounceReturnNotice(bool announced, bool noticeOpen, string? content) =>
        !announced && noticeOpen && !string.IsNullOrWhiteSpace(content);

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


    // "Limpar pesquisa" disappears once it worked; focus returns to the search box (UI.3 pattern).
    private void OnClearSearchClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, SearchBox);

    private void OnReturnNoticeCloseRequested(object? sender, EventArgs e) => ViewModel.DismissNoticeCommand.Execute(null);
}
