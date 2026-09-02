using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace ServerMonitor.WidgetProvider.Diagnostics;

/// <summary>
/// The provider's production diagnostic log. It writes to two deliberately <b>invisible</b> sinks:
/// <see cref="Trace"/> (OutputDebugString — readable with an attached debugger or DebugView) and an ETW
/// <see cref="EventSource"/> named <c>ServerAlyzer-WidgetProvider</c> (readable with logman/PerfView/WPR,
/// and free while no session is enabled). Neither creates a window, a console, or a file.
/// <para>
/// It MUST NEVER write to <see cref="Console"/> (M13-QA-7). Its predecessor wrote to
/// <c>Console.Error</c> under the comment "no console attached (normal for a COM-activated server)".
/// That assumption is false: a COM ExeServer is started with CreateProcess from a parent that has no
/// console, so Windows allocates one for a console-subsystem image — measured directly, the CUI provider
/// launched from a console-less parent gets a real <c>conhost</c> attached. The window is what the user
/// saw; the console write only decided what was printed inside it. The subsystem is fixed in the csproj
/// (WinExe); dropping the console sink means a future subsystem regression would at least have nothing
/// to print.
/// </para>
/// Messages stay coarse: operation names and exception TYPE names only — never the snapshot payload,
/// server names, or file paths (ADR-018 §31).
/// </summary>
public sealed class EtwWidgetProviderLog : IWidgetProviderLog
{
    public static readonly EtwWidgetProviderLog Instance = new();

    private EtwWidgetProviderLog()
    {
    }

    public void Warn(string message)
    {
        Trace.WriteLine($"[ServerAlyzer.WidgetProvider] WARN: {message}");
        Emit(warning: true, message);
    }

    public void Info(string message)
    {
        Trace.WriteLine($"[ServerAlyzer.WidgetProvider] INFO: {message}");
        Emit(warning: false, message);
    }

    /// <summary>
    /// Self-isolating: an unavailable or misconfigured EventSource must never be able to change the COM
    /// server's behavior (§16). Trace already carries the same line, so swallowing here loses nothing.
    /// </summary>
    private static void Emit(bool warning, string message)
    {
        try
        {
            if (warning)
            {
                WidgetProviderEventSource.Log.Warn(message);
            }
            else
            {
                WidgetProviderEventSource.Log.Info(message);
            }
        }
        catch
        {
            // Diagnostics are never worth a behavior change.
        }
    }
}

/// <summary>
/// The provider's ETW channel. Collect it in the field with
/// <c>logman start sa -p ServerAlyzer-WidgetProvider -ets</c> (or PerfView/WPR); nothing is written, and
/// nothing costs, while no session is enabled. It carries exactly the same coarse strings
/// <see cref="EtwWidgetProviderLog"/> produces — never payload, server names, or paths (§31).
/// </summary>
[EventSource(Name = "ServerAlyzer-WidgetProvider")]
internal sealed class WidgetProviderEventSource : EventSource
{
    internal static readonly WidgetProviderEventSource Log = new();

    private WidgetProviderEventSource()
    {
    }

    /// <remarks>
    /// Public because EventSource only discovers event methods among the type's PUBLIC instance methods;
    /// the enclosing type stays internal, so this is not provider surface area.
    /// </remarks>
    [Event(EventIds.Info, Level = EventLevel.Informational, Message = "{0}")]
    public void Info(string message)
    {
        if (IsEnabled())
        {
            WriteEvent(EventIds.Info, message);
        }
    }

    /// <inheritdoc cref="Info" />
    [Event(EventIds.Warn, Level = EventLevel.Warning, Message = "{0}")]
    public void Warn(string message)
    {
        if (IsEnabled())
        {
            WriteEvent(EventIds.Warn, message);
        }
    }

    /// <summary>Stable wire ids: never renumber, collected traces are read by id.</summary>
    private static class EventIds
    {
        internal const int Info = 1;
        internal const int Warn = 2;
    }
}
