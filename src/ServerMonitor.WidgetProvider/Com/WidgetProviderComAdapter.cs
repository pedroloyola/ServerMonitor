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
        // The widget still exists; it is just not currently being viewed, so the registry is deliberately
        // left as-is. What DOES change is that it stops counting as on screen: when the last visible widget
        // deactivates the coordinator disarms the repaint pump, so the provider goes completely idle with
        // the board closed (M13 QA-9). This used to be a no-op, which is why nothing ever turned the pump
        // off — and, with no pump at all, why an open board never repainted.
        Guard(nameof(Deactivate), () => _coordinator.OnWidgetDeactivated(widgetId));

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs) =>
        // A click on the card/row: map the allowlisted verb + opaque id to a serveralyzer:// launch (§14).
        // Fully contained — a click can never fault the provider.
        Guard(nameof(OnActionInvoked), () =>
        {
            SpikeRecordActionInvoked(actionInvokedArgs.Verb);
            _actionHandler.Handle(actionInvokedArgs.Verb, actionInvokedArgs.Data);
        });

    /// <summary>
    /// M13-QA-10 SPIKE — DO NOT MERGE. Board measurement point 4 ("does the provider's OnActionInvoked
    /// fire?") cannot be answered from outside the process: the production log writes only to ETW and
    /// OutputDebugString, which are invisible without a session or a debugger attached. So this spike
    /// build appends one line per invocation next to the snapshot, and the human reads that file.
    /// <para>
    /// It records only a timestamp and WHICH allowlisted verb matched — never the raw verb, never the
    /// action data, never a server id — so it adds no payload to disk. Failure is swallowed: a broken
    /// probe must not change what the spike measures.
    /// </para>
    /// </summary>
    private static void SpikeRecordActionInvoked(string? verb)
    {
        try
        {
            var matched = verb switch
            {
                ServerMonitor.ActivationContract.ActivationVerbs.OpenDashboard => "openDashboard",
                ServerMonitor.ActivationContract.ActivationVerbs.OpenServer => "openServer",
                _ => "<unrecognized>"
            };

            File.AppendAllText(
                Path.Combine(
                    ServerMonitor.WidgetContract.WidgetStateLocation.DirectoryForCurrentUser(),
                    "qa10-spike-actions.log"),
                $"[{DateTimeOffset.Now:HH:mm:ss.fff}] OnActionInvoked verb={matched}{Environment.NewLine}");
        }
        catch
        {
            // Spike instrumentation only.
        }
    }

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
