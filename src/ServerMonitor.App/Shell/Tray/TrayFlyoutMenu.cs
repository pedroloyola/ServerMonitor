namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// The menu, as data. Separated from any window so the order — which is product-fixed and
/// non-negotiable — is assertable without a desktop.
/// </summary>
/// <remarks>
/// It used to live beside the XAML flyout that rendered it. That window is gone (M13-QA-11 replaced it
/// with a native shell menu), and this outlived it deliberately: the order and the resource keys are the
/// contract, and the thing that draws them is an implementation detail that has now changed once.
/// </remarks>
internal static class TrayFlyoutMenu
{
    /// <summary>
    /// Abrir o ServerAlyzer · Modo compacto · Atualizar todos · Definições · Sair do ServerAlyzer.
    /// </summary>
    internal static readonly TrayCommand[] Order =
    [
        TrayCommand.Open,
        TrayCommand.ToggleCompact,
        TrayCommand.RefreshAll,
        TrayCommand.Settings,
        TrayCommand.Exit
    ];

    internal static string ResourceKeyFor(TrayCommand command) => command switch
    {
        TrayCommand.Open => "TrayOpenMenuItem",
        TrayCommand.ToggleCompact => "TrayCompactModeMenuItem",
        TrayCommand.RefreshAll => "TrayRefreshAllMenuItem",
        TrayCommand.Settings => "TraySettingsMenuItem",
        TrayCommand.Exit => "TrayExitMenuItem"
        // No `_ =>` arm: CS8509 is an error in this project, so a new command cannot be added without
        // deciding what it is called.
    };
}
