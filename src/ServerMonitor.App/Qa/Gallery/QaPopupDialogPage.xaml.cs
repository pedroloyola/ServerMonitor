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

    // R2 (Cortex C2-1): "input" = Kind left UNSET and a focused TextBox in the content, so Enter must reach the
    // DefaultButton (Primary, from SaDialogStyle) - not merely activate a focused button.
    private bool _input;

    public QaPopupDialogPage()
    {
        InitializeComponent();
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = ShowAsync());
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _input = e.Parameter is "input";
        _kind = e.Parameter switch
        {
            "confirm" => SaDialogKind.Confirm,
            "input" => SaDialogKind.Unspecified,
            _ => SaDialogKind.Destructive
        };
        Description.Text = _kind switch
        {
            SaDialogKind.Destructive => "Opened automatically: SaDialogStyle + SaDialog.Kind=Destructive (destructive primary; Enter and first focus = Cancelar).",
            SaDialogKind.Confirm => "Opened automatically: SaDialogStyle + SaDialog.Kind=Confirm (Enter confirms; first focus = primary, Sa look, no accent).",
            _ => "Opened automatically: SaDialogStyle, SaDialog.Kind NOT set, a focused text box in the content: Enter must confirm (DefaultButton=Primary from the style)."
        };
    }

    private void OnOpenDialog(object sender, RoutedEventArgs e) => _ = ShowAsync();

    private async Task ShowAsync()
    {
        Application.Current.Resources.TryGetValue("SaDialogStyle", out var dialogStyle);
        var destructive = _kind == SaDialogKind.Destructive;
        var nameBox = new TextBox { Header = "Nome", Text = "QA · Healthy · Linux" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nameBox, "Nome do servidor");
        object content = _input
            ? nameBox
            : destructive
                ? "O QA · Healthy · Linux deixa de ser monitorizado. O histórico local é apagado."
                : "As alterações ao QA · Healthy · Linux são aplicadas na próxima recolha.";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = dialogStyle as Style,
            Title = destructive ? "Remover servidor?" : _input ? "Mudar o nome?" : "Guardar alterações?",
            Content = content,
            PrimaryButtonText = destructive ? "Remover servidor" : "Guardar",
            CloseButtonText = "Cancelar",
            RequestedTheme = ActualTheme
        };
        if (_kind != SaDialogKind.Unspecified)
        {
            SaDialog.SetKind(dialog, _kind);
        }

        DefaultButtonText.Text = "Dialog DefaultButton: " + dialog.DefaultButton;
        dialog.Opened += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_input)
            {
                nameBox.Focus(FocusState.Keyboard);
            }

            InitialFocusText.Text = "Dialog initial focus: " + FocusManager.GetFocusedElement(XamlRoot) switch
            {
                Button button => button.Content,
                TextBox => "text box",
                _ => "-"
            };
        });
        var result = await dialog.ShowAsync();
        ResultText.Text = "Dialog result: " + result;
    }
}
