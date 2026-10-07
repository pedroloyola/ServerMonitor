namespace ServerMonitor.App.Services;

/// <summary>
/// UI.7 H-UI7-3: "Descartar alterações?" (Figma 112:9018, SaDialog Destructive). True only when the user chose to discard;
/// Esc, Enter and "Continuar a editar" (the safe default) all answer false. Never saves.
/// </summary>
public interface IServerEditorDiscardPrompt
{
    Task<bool> ConfirmDiscardAsync(ServerEditorDiscardContext context);
}

/// <param name="OpenedName">The name the editor was opened with (empty for a fresh add).</param>
/// <param name="CurrentName">The name in the form now.</param>
public sealed record ServerEditorDiscardContext(ServerEditorMode Mode, string OpenedName, string CurrentName);
