using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class RestoreOpenDialog : ContentDialog
{
    public RestoreOpenDialog(RestoreOpenSession session)
    {
        Session = session;
        InitializeComponent();
        Title = session.Title;
        PrimaryButtonText = session.PrimaryText;
        CloseButtonText = session.CancelText;
        IsPrimaryButtonEnabled = session.CanSubmit;
        session.PropertyChanged += OnSessionPropertyChanged;
        Closed += OnClosed;
    }

    public RestoreOpenSession Session { get; }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RestoreOpenSession.CanSubmit))
        {
            IsPrimaryButtonEnabled = Session.CanSubmit;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs args) =>
        Session.SetPassphrase(PassphraseBox.Password);

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var close = await Session.SubmitAsync();
            args.Cancel = !close;
            if (!close)
            {
                // A rejected passphrase is not kept: the field is emptied for the next attempt.
                PassphraseBox.Password = string.Empty;
                PassphraseBox.Focus(FocusState.Programmatic);
            }
        }
        catch
        {
            // The owner reports the failure once the dialog is gone; never leave it stuck open.
            args.Cancel = false;
        }
        finally
        {
            deferral.Complete();
        }
    }

    // Esc and Cancel wait for the running check; it is read-only and takes about a second.
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (Session.IsBusy)
        {
            args.Cancel = true;
        }
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        Session.PropertyChanged -= OnSessionPropertyChanged;
        Closed -= OnClosed;
        PassphraseBox.Password = string.Empty;
        Session.Clear();
    }
}
