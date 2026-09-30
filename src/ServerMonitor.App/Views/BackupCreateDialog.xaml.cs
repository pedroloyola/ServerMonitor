using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class BackupCreateDialog : ContentDialog
{
    public BackupCreateDialog(BackupCreateSession session)
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

    public BackupCreateSession Session { get; }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(BackupCreateSession.CanSubmit))
        {
            IsPrimaryButtonEnabled = Session.CanSubmit;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs args) =>
        Session.SetPassphrase(PassphraseBox.Password, ConfirmationBox.Password);

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await Session.SubmitAsync();
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

    // Esc and Cancel wait for a running export: it cannot be abandoned half-way from here.
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
        ConfirmationBox.Password = string.Empty;
        Session.Clear();
    }
}
