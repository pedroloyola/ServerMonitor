namespace ServerMonitor.WidgetProvider.Hosting;

/// <summary>The widget sizes the Windows Widgets board can request.</summary>
public enum WidgetSizeHint
{
    Unknown = 0,
    Small,
    Medium,
    Large
}

/// <summary>
/// A framework-neutral view of a widget instance, mapped from the real <c>WidgetContext</c>/<c>WidgetInfo</c>
/// by the thin COM adapter. Keeping the coordinator on this type (not on the WinRT types) lets the whole
/// provider lifecycle be unit-tested without the Windows Widgets host (§33).
/// </summary>
public readonly record struct WidgetActivation(
    string WidgetId,
    string DefinitionId,
    WidgetSizeHint Size,
    string? CustomState);

/// <summary>
/// Abstraction over the Windows <c>WidgetManager</c> so the coordinator can be tested with a fake host.
/// The COM adapter implements this over <c>WidgetManager.GetDefault()</c>: <see cref="GetActiveWidgets"/>
/// wraps <c>GetWidgetInfos()</c> and <see cref="Update"/> wraps <c>UpdateWidget(WidgetUpdateRequestOptions)</c>.
/// </summary>
public interface IWidgetHost
{
    /// <summary>The widgets Windows currently believes this provider owns (used to rehydrate on startup).</summary>
    IReadOnlyList<WidgetActivation> GetActiveWidgets();

    /// <summary>Pushes a new Adaptive Card template + data to one widget.</summary>
    void Update(string widgetId, string templateJson, string dataJson);
}
