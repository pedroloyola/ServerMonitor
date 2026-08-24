using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using Windows.System;

namespace ServerMonitor.App.Controls;

public sealed partial class ServerEditorModal : UserControl
{
    private readonly TaskCompletionSource<ServerEditorResult?> _tcs = new();

    public ServerEditorViewModel ViewModel { get; }
    public ServerEditorResult? Result { get; private set; }

    public ServerEditorModal(
        ServerEditorViewModel viewModel,
        ILocalizationService localizationService,
        bool isEdit)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ModalTitleText.Text = localizationService.GetString(isEdit ? "EditServerDialog/Title" : "AddServerDialog/Title");
        PrimaryActionButton.Content = localizationService.GetString(isEdit ? "EditServerDialog/PrimaryButtonText" : "AddServerDialog/PrimaryButtonText");
        CancelActionButton.Content = localizationService.GetString(isEdit ? "EditServerDialog/CloseButtonText" : "AddServerDialog/CloseButtonText");

        Loaded += OnModalLoaded;
        KeyDown += OnModalKeyDown;
    }

    public static async Task<ServerEditorResult?> ShowAsync(
        IWindowContext windowContext,
        ServerEditorViewModel viewModel,
        ILocalizationService localizationService,
        bool isEdit)
    {
        var modalHost = windowContext.ModalHost;
        if (modalHost is null)
        {
            return null;
        }

        var modal = new ServerEditorModal(viewModel, localizationService, isEdit)
        {
            RequestedTheme = windowContext.ActualTheme
        };

        modalHost.Children.Add(modal);
        modalHost.IsHitTestVisible = true;

        try
        {
            return await modal._tcs.Task;
        }
        finally
        {
            modalHost.Children.Remove(modal);
            modalHost.IsHitTestVisible = modalHost.Children.Count > 0;
        }
    }

    private void OnModalLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnModalLoaded;
        ServerForm.FocusFirstField();
    }

    private void OnModalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseWithResult(null);
        }
        else if (e.Key == VirtualKey.Enter && !ViewModel.IsTestingConnection)
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox tb || !tb.AcceptsReturn)
            {
                e.Handled = true;
                Submit();
            }
        }
    }

    private void OnSmokeLayerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        CloseWithResult(null);
    }

    private void OnCancelButtonClick(object sender, RoutedEventArgs e)
    {
        CloseWithResult(null);
    }

    private void OnPrimaryButtonClick(object sender, RoutedEventArgs e)
    {
        Submit();
    }

    private void Submit()
    {
        if (ViewModel.IsTestingConnection)
        {
            return;
        }

        ServerForm.CaptureSecret();
        if (!ViewModel.TryCreateResult(out var result))
        {
            ServerForm.FocusFirstField();
            return;
        }

        Result = result;
        CloseWithResult(result);
    }

    private void CloseWithResult(ServerEditorResult? result)
    {
        _tcs.TrySetResult(result);
    }
}
