using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Services;

/// <summary>Controls the one authoritative main window without ever creating another one.</summary>
public interface IApplicationWindowController
{
    bool IsAttached { get; }

    void Attach(Window window);

    void HideForMinimize();

    void RestoreAndActivate();

    void OpenSettings();

    /// <summary>Restores/activates the window and toggles between Standard and Compact presentation.</summary>
    void ToggleCompactMode();

    void RequestClose();

    void BeginShutdown();
}
