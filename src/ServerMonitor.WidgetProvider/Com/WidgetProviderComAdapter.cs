using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using ServerMonitor.WidgetProvider.Activation;
using ServerMonitor.WidgetProvider.Diagnostics;
using ServerMonitor.WidgetProvider.Hosting;

namespace ServerMonitor.WidgetProvider.Com;

/// <summary>
/// The thin COM boundary: implements the Windows <see cref="IWidgetProvider"/> and forwards every
/// callback to the framework-neutral <see cref="WidgetProviderCoordinator"/>. Its only jobs are mapping
/// the WinRT <see cref="WidgetContext"/> to <see cref="WidgetActivation"/> and being an absolute firewall
/// against exceptions: no .NET exception may ever cross back into the Widgets host (§16). Deep-link
/// handling (<see cref="OnActionInvoked"/>) belongs to a later slice and is intentionally a safe no-op.
/// </summary>
[ComVisible(true)]
[Guid(ClsidString)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class WidgetProviderComAdapter : IWidgetProvider
{
    /// <summary>Dedicated widget-provider CLSID — distinct from the M12 notification-activation CLSID.</summary>
    public const string ClsidString = "78CFFBEF-7A95-4400-BB8B-A2376C6642C3";

    public static readonly Guid Clsid = new(ClsidString);

    private readonly WidgetProviderCoordinator _coordinator;
    private readonly ComServerProcess _process;
    private readonly WidgetActionHandler _actionHandler;
    private readonly IWidgetProviderLog _log;

    public WidgetProviderComAdapter(
        WidgetProviderCoordinator coordinator,
        ComServerProcess process,
        IWidgetProviderLog? log = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _log = log ?? NullWidgetProviderLog.Instance;
        _actionHandler = new WidgetActionHandler(new ProtocolAppLauncher(), _log);

        // This live COM object keeps the server process alive; released in the finalizer when the host
        // drops its last reference (the correct COM lifetime barrier, not the widget registry).
        _process.AddRef();
    }

    ~WidgetProviderComAdapter()
    {
        try
        {
            _process.Release();
        }
        catch
        {
            // Never let a finalizer throw.
        }
    }

    public void CreateWidget(WidgetContext widgetContext) =>
        Guard(nameof(CreateWidget), () => _coordinator.OnWidgetActivated(Map(widgetContext)));

    public void Activate(WidgetContext widgetContext) =>
        Guard(nameof(Activate), () => _coordinator.OnWidgetActivated(Map(widgetContext)));

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs) =>
        Guard(nameof(OnWidgetContextChanged), () =>
            _coordinator.OnWidgetContextChanged(Map(contextChangedArgs.WidgetContext)));

    public void DeleteWidget(string widgetId, string customState) =>
        Guard(nameof(DeleteWidget), () => _coordinator.OnWidgetDeleted(widgetId));

    public void Deactivate(string widgetId) =>
        // The widget still exists; it is just not currently being viewed. Nothing to push until the next
        // Activate/context change — keeping the registry as-is is correct.
        Guard(nameof(Deactivate), () => { });

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs) =>
        // A click on the card/row: map the allowlisted verb + opaque id to a serveralyzer:// launch (§14).
        // Fully contained — a click can never fault the provider.
        Guard(nameof(OnActionInvoked), () =>
            _actionHandler.Handle(actionInvokedArgs.Verb, actionInvokedArgs.Data));

    private static WidgetActivation Map(WidgetContext context) =>
        new(context.Id, context.DefinitionId, MapSize(context.Size), CustomState: null);

    private static WidgetSizeHint MapSize(WidgetSize size) => size switch
    {
        WidgetSize.Small => WidgetSizeHint.Small,
        WidgetSize.Medium => WidgetSizeHint.Medium,
        WidgetSize.Large => WidgetSizeHint.Large,
        _ => WidgetSizeHint.Unknown
    };

    private void Guard(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            // Neutral-on-exception at the COM boundary: log coarsely, swallow, keep the host stable (§16).
            _log.Warn($"Widget provider callback {operation} failed. Error: {exception.GetType().Name}.");
        }
    }
}
