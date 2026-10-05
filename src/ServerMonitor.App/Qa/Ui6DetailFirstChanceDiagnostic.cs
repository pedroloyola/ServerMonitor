using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace ServerMonitor.App.Qa;

// TEMPORARY UI.6 c1 §15 probe. Remove after the root cause is measured.
internal static class Ui6DetailFirstChanceDiagnostic
{
    private static int _captured;
    private static string? _output;

    public static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--qa-overview") || !args.Contains("--qa-overview-scenario=detail") ||
            !args.Contains("--qa-start=detail:1")) return;
        _output = Environment.GetEnvironmentVariable("UI6_DETAIL_DIAGNOSTIC_PATH");
        if (string.IsNullOrWhiteSpace(_output)) return;
        File.WriteAllText(_output, JsonSerializer.Serialize(new { Probe = "UI6-C1-FirstChance", Pid = Environment.ProcessId }) + Environment.NewLine);
        AppDomain.CurrentDomain.FirstChanceException += Capture;
    }

    private static void Capture(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (args.Exception is not InvalidCastException && args.Exception.HResult != unchecked((int)0x80004002)) return;
        if (Interlocked.Increment(ref _captured) > 8) return;
        // No exception messages, argument values, object state, server identities or credentials.
        var frames = new StackTrace(args.Exception, false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Select(method => method?.DeclaringType?.FullName + "." + method?.Name).ToArray();
        File.AppendAllText(_output!, JsonSerializer.Serialize(new
        {
            Type = args.Exception.GetType().FullName,
            HResult = args.Exception.HResult.ToString("X8"),
            Frames = frames,
            CaptureFrames = new StackTrace(false).GetFrames()
                .Select(frame => frame.GetMethod())
                .Select(method => method?.DeclaringType?.FullName + "." + method?.Name).ToArray()
        }) + Environment.NewLine);
    }
}
