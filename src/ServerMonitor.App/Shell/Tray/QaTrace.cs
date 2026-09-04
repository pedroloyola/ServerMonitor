namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// TEMPORARY instrumentation for M13-QA-11. NOT PART OF THE PRODUCT — delete with the defect.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddDebug()</c> goes to an attached debugger and leaves nothing to read afterwards, so it cannot
/// serve as evidence. This appends to a file, keyed by a request id, so each right-click can be followed
/// end to end: gate → show → <c>ShowAt</c> → <c>Opened</c>/<c>Closed</c> → hide → gate release.
/// </para>
/// <para>
/// It exists because three links in the causal chain were INFERRED rather than measured: that the first
/// <c>ShowAt</c> throws, that <c>OnClosed</c> never runs, and that the third request is refused by the
/// gate. Absence of a popup proves none of them.
/// </para>
/// </remarks>
internal static class QaTrace
{
    private static readonly object Sync = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServerMonitor",
        "qa11-trace.log");

    private static int _nextRequestId;

    internal static int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    internal static void Write(int requestId, string stage, string detail = "")
    {
        try
        {
            var line = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} req={1,-3} tid={2,-4} {3,-26} {4}",
                DateTime.Now,
                requestId,
                Environment.CurrentManagedThreadId,
                stage,
                detail);

            lock (Sync)
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                System.IO.File.AppendAllText(Path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Instrumentation must never change the behaviour it is measuring, including by throwing.
        }
    }

    /// <summary>Records an exception with its HRESULT, which is the part that identifies a XAML failure.</summary>
    internal static void WriteException(int requestId, string stage, Exception exception) =>
        Write(
            requestId,
            stage,
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "EXCEPTION {0} hr=0x{1:X8} msg={2}",
                exception.GetType().FullName,
                exception.HResult,
                exception.Message));
}
