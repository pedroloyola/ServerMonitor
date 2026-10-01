using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>
/// QA-ONLY (UI.2 S6, R1 Cortex R-2 / Beacon F-1): shows a ContentDialog with SaDialogStyle on load so it can be captured,
/// and reports which button took the first focus and what the dialog returned, so a scripted Enter can be verified.
/// </summary>
public sealed partial class QaPopupDialogPage : Page
{
    private SaDialogKind _kind = SaDialogKind.Destructive;

    public QaPopupDialogPage()
    {
        InitializeComponent();
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = ShowAsync());
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _kind = e.Parameter is "confirm" ? SaDialogKind.Confirm : SaDialogKind.Destructive;
        Description.Text = _kind == SaDialogKind.Destructive
            ? "Opened automatically: SaDialogStyle + SaDialog.Kind=Destructive (destructive primary; Enter and first focus = Cancelar)."
            : "Opened automatically: SaDialogStyle + SaDialog.Kind=Confirm (Enter confirms; first focus = primary, Sa look, no accent).";
    }

    private void OnOpenDialog(object sender, RoutedEventArgs e) => _ = ShowAsync();

    private async Task ShowAsync()
    {
        Application.Current.Resources.TryGetValue("SaDialogStyle", out var dialogStyle);
        var destructive = _kind == SaDialogKind.Destructive;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = dialogStyle as Style,
            Title = destructive ? "Remover servidor?" : "Guardar alterações?",
            Content = destructive
                ? "O QA · Healthy · Linux deixa de ser monitorizado. O histórico local é apagado."
                : "As alterações ao QA · Healthy · Linux são aplicadas na próxima recolha.",
            PrimaryButtonText = destructive ? "Remover servidor" : "Guardar",
            CloseButtonText = "Cancelar",
            RequestedTheme = ActualTheme
        };
        SaDialog.SetKind(dialog, _kind);
        dialog.Opened += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            InitialFocusText.Text = "Dialog initial focus: " + ((FocusManager.GetFocusedElement(XamlRoot) as Button)?.Content ?? "-"));
        var result = await dialog.ShowAsync();
        ResultText.Text = "Dialog result: " + result;
    }
}
