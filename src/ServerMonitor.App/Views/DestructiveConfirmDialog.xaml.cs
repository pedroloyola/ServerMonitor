using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.5 fix round 2 (Prism C1 M-3): one destructive confirmation for Remover servidor, Limpar histórico and Repor
/// histórico (Figma section 11). The texts come from the caller (already localized); the result is Primary only when the
/// user chose the destructive button - Esc, Cancelar and Enter (the safe default) all answer no.
/// </summary>
public sealed partial class DestructiveConfirmDialog : ContentDialog
{
    public DestructiveConfirmDialog(DestructiveConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        Body = confirmation.Body;
        Affected = confirmation.Affected;
        AffectedIconData = confirmation.AffectedIconData;
        Note = confirmation.Note;
        InitializeComponent();
        Title = confirmation.Title;
        PrimaryButtonText = confirmation.PrimaryButtonText;
        CloseButtonText = confirmation.CloseButtonText;
    }

    public string Body { get; }

    public string Affected { get; }

    public string AffectedIconData { get; }

    public string Note { get; }

    /// <summary>
    /// The smoke layer covers the whole window (Figma backdrop #000@.36 / @.20): the dialog is sized to its XamlRoot and
    /// follows it while open - the same rule the previous Remover dialog and the backup dialogs use. Call after XamlRoot.
    /// </summary>
    public void FillWindow()
    {
        if (XamlRoot is not { } root)
        {
            return;
        }

        void UpdateBounds()
        {
            Width = root.Size.Width;
            Height = root.Size.Height;
        }

        void OnRootChanged(Microsoft.UI.Xaml.XamlRoot sender, Microsoft.UI.Xaml.XamlRootChangedEventArgs args) => UpdateBounds();
        UpdateBounds();
        root.Changed += OnRootChanged;
        Closed += (_, _) => root.Changed -= OnRootChanged;
    }
}
