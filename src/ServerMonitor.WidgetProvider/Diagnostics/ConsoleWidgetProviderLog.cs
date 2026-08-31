using System.Diagnostics;

namespace ServerMonitor.WidgetProvider.Diagnostics;

/// <summary>
/// Coarse diagnostic log for the running provider, writing to <see cref="Trace"/> (visible via a debug
/// listener) and standard error. It only ever emits operation names and exception type names — never the
/// snapshot payload, server names, or file contents (§31).
/// </summary>
public sealed class ConsoleWidgetProviderLog : IWidgetProviderLog
{
    public static readonly ConsoleWidgetProviderLog Instance = new();

    private ConsoleWidgetProviderLog()
    {
    }

    public void Warn(string message) => Write("WARN", message);

    public void Info(string message) => Write("INFO", message);

    private static void Write(string level, string message)
    {
        var line = $"[ServerAlyzer.WidgetProvider] {level}: {message}";
        Trace.WriteLine(line);
        try
        {
            Console.Error.WriteLine(line);
        }
        catch
        {
            // No console attached (normal for a COM-activated server) — Trace is enough.
        }
    }
}
