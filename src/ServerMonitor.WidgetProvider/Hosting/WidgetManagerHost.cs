using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;

namespace ServerMonitor.WidgetProvider.Hosting;

/// <summary>
/// The real <see cref="IWidgetHost"/>, wrapping the Windows App SDK <see cref="WidgetManager"/>. This is
/// the only place the provider touches the WinRT widget API; the coordinator and everything below stay on
/// the framework-neutral <see cref="WidgetActivation"/> so they remain unit-testable (§33).
/// </summary>
internal sealed class WidgetManagerHost : IWidgetHost
{
    public IReadOnlyList<WidgetActivation> GetActiveWidgets()
    {
        var infos = WidgetManager.GetDefault().GetWidgetInfos();
        if (infos is null)
        {
            return Array.Empty<WidgetActivation>();
        }

        var result = new List<WidgetActivation>(infos.Length);
        foreach (var info in infos)
        {
            var context = info.WidgetContext;
            result.Add(new WidgetActivation(
                context.Id,
                context.DefinitionId,
                MapSize(context.Size),
                info.CustomState));
        }

        return result;
    }

    public void Update(string widgetId, string templateJson, string dataJson)
    {
        var options = new WidgetUpdateRequestOptions(widgetId)
        {
            Template = templateJson,
            Data = dataJson
        };

        WidgetManager.GetDefault().UpdateWidget(options);
    }

    private static WidgetSizeHint MapSize(WidgetSize size) => size switch
    {
        WidgetSize.Small => WidgetSizeHint.Small,
        WidgetSize.Medium => WidgetSizeHint.Medium,
        WidgetSize.Large => WidgetSizeHint.Large,
        _ => WidgetSizeHint.Unknown
    };
}
