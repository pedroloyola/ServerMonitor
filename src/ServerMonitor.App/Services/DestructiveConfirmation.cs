using Microsoft.UI.Xaml;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

/// <summary>
/// UI.5 fix round 2 (Prism C1 M-3): the localized content of a destructive confirmation (Figma section 11) - what will
/// happen, the affected data line, the note and the two buttons. Pure (unit-tested); the view is
/// <see cref="Views.DestructiveConfirmDialog"/>.
/// </summary>
public sealed record DestructiveConfirmation(
    string Title,
    string Body,
    string Affected,
    string AffectedIconData,
    string Note,
    string PrimaryButtonText,
    string CloseButtonText)
{
    /// <summary>Figma 112:8559: "Remover este servidor?" · the server's name and endpoint (A-13) · remote untouched.</summary>
    public static DestructiveConfirmation RemoveServer(Server server, ILocalizationService localization, string serverIconData)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(localization);
        return new DestructiveConfirmation(
            localization.GetString("RemoveServerConfirmTitle"),
            Format(localization, "RemoveServerConfirmBodyFormat", server.Name),
            ServerContextPresentation.Join(server.Name, OverviewPresentation.Endpoint(server.Host, server.Port)),
            serverIconData,
            localization.GetString("RemoveServerConfirmNote"),
            localization.GetString("RemoveServerConfirmPrimary"),
            localization.GetString("DestructiveConfirmClose"));
    }

    /// <summary>
    /// UI.7 H-UI7-3, Figma 112:9185: "Descartar alterações?" · the server's name - "before → after" only when the name was
    /// changed (B-15) · "you can keep editing". Kind=Destructive keeps "Continuar a editar" the default and first focus.
    /// </summary>
    public static DestructiveConfirmation DiscardEditorChanges(
        ServerEditorDiscardContext context,
        ILocalizationService localization,
        string serverIconData)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(localization);
        return new DestructiveConfirmation(
            localization.GetString("ServerEditorDiscardTitle"),
            localization.GetString("ServerEditorDiscardBody"),
            DiscardAffected(context, localization),
            serverIconData,
            localization.GetString("ServerEditorDiscardNote"),
            localization.GetString("ServerEditorDiscardPrimary"),
            localization.GetString("ServerEditorDiscardClose"));
    }

    /// <summary>Pure (unit-tested): the name line of the discard dialog.</summary>
    internal static string DiscardAffected(ServerEditorDiscardContext context, ILocalizationService localization)
    {
        var before = context.OpenedName.Trim();
        var after = context.CurrentName.Trim();
        if (before.Length > 0 && after.Length > 0 && !string.Equals(before, after, StringComparison.Ordinal))
        {
            return Format(localization, "ServerEditorDiscardRenamedFormat", before, after);
        }

        var name = after.Length > 0 ? after : before;
        return name.Length > 0 ? name : localization.GetString("ServerEditorDiscardUnnamed");
    }

    /// <summary>Figma 112:8876: "Limpar todo o histórico?" · "Histórico local · Todos os servidores" · settings kept.</summary>
    public static DestructiveConfirmation ClearHistory(ILocalizationService localization, string historyIconData) =>
        History(localization, historyIconData, "HistoryClearConfirm");

    /// <summary>"Repor histórico" (product, no Figma frame): the same pattern - "Histórico local · base indisponível".</summary>
    public static DestructiveConfirmation ResetHistory(ILocalizationService localization, string historyIconData) =>
        History(localization, historyIconData, "HistoryResetConfirm");

    /// <summary>The Sa icon path data for a resource key (falls back to empty: the row still names the data in words).</summary>
    public static string IconData(string resourceKey) =>
        Application.Current?.Resources.TryGetValue(resourceKey, out var data) == true && data is string path ? path : string.Empty;

    private static DestructiveConfirmation History(ILocalizationService localization, string iconData, string prefix)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return new DestructiveConfirmation(
            localization.GetString(prefix + "Title"),
            localization.GetString(prefix + "Message"),
            localization.GetString(prefix + "Affected"),
            iconData,
            localization.GetString(prefix + "Note"),
            localization.GetString(prefix + "Primary"),
            localization.GetString("DestructiveConfirmClose"));
    }

    private static string Format(ILocalizationService localization, string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentUICulture, localization.GetString(key), args);
}
