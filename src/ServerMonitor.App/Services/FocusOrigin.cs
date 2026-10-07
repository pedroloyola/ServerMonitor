using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Services;

/// <summary>UI.7 B-5: the name of the control that has keyboard focus now (the editor's opener), for its return slot.</summary>
internal static class FocusOrigin
{
    /// <summary>
    /// A capture over the window: the element's page-level name; for a templated row control with no page-level name
    /// (Cortex 7A m-3: "Adicionar" on a network suggestion) a token for its row; null without a window, without focus or
    /// for any other unnamed element.
    /// </summary>
    public static Func<string?> CaptureName(IWindowContext windowContext) => () =>
        FocusManager.GetFocusedElement(windowContext.XamlRoot) switch
        {
            FrameworkElement { Name.Length: > 0 } element => element.Name,
            FrameworkElement { DataContext: DiscoveredServerViewModel suggestion } => EditorOpenerToken.ForSuggestion(suggestion.Endpoint),
            _ => null
        };
}

/// <summary>
/// UI.7C (Cortex 7A m-3): the return-focus token of a control that lives in a row template (no page-level name): the
/// "Adicionar" button of a network suggestion, identified by the suggestion's endpoint. In memory only (the editor's
/// return slot); never logged.
/// </summary>
internal static class EditorOpenerToken
{
    private const string SuggestionPrefix = "suggestion:";

    public static string ForSuggestion(string endpoint) => SuggestionPrefix + endpoint;

    public static bool TryGetSuggestion(string? token, out string endpoint)
    {
        endpoint = string.Empty;
        if (token is null || !token.StartsWith(SuggestionPrefix, StringComparison.Ordinal) || token.Length == SuggestionPrefix.Length)
        {
            return false;
        }

        endpoint = token[SuggestionPrefix.Length..];
        return true;
    }
}
