using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// TEMPORARY instrumentation for the M13-QA-11 human session. NOT PART OF THE PRODUCT — it comes out with
/// the defect, exactly as <c>QaTrace</c> did.
/// </summary>
/// <remarks>
/// <para>
/// It answers one question that automation cannot: <b>does the menu dismiss itself during a REAL
/// right-click, and if so, who took the foreground?</b> Under a synthetic <c>PostMessage</c> the desktop
/// was seen taking the foreground about two seconds after the menu opened, which counts as a dismissal —
/// but a posted message may leave focus in a state a real click would not, so the observation is
/// inconclusive by construction.
/// </para>
/// <para>
/// Each dismissal records WHO took the foreground (handle, window class, process id and name) and HOW LONG
/// after the menu opened. If the elapsed time is short and the owner is the shell or the desktop rather
/// than something the user clicked, the menu is closing on its own and that is a UX failure.
/// </para>
/// </remarks>
internal static class QaDismissTrace
{
    private static readonly object Sync = new();

    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServerMonitor",
        "qa11-dismissals.log");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder name, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>
    /// Records every foreground observation with the elapsed time taken from the EVENT's own timestamp,
    /// not from the moment it was delivered.
    /// </summary>
    internal static void Observed(nint hwnd, uint sinceOpenedMs)
    {
        var className = new StringBuilder(256);
        _ = GetClassNameW(hwnd, className, className.Capacity);
        _ = GetWindowThreadProcessId(hwnd, out var processId);

        var processName = "?";
        try
        {
            processName = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception)
        {
            // A window whose process has gone is still worth recording by id.
        }

        Write(string.Format(
            CultureInfo.InvariantCulture,
            "FOREGROUND at {0,7} ms  hwnd=0x{1:X} class={2} pid={3} ({4})",
            sinceOpenedMs,
            hwnd,
            className,
            processId,
            processName));
    }

    /// <summary>
    /// Records the menu opening WITH the foreground baseline. Without it a later
    /// <c>FOREGROUND … class=Progman</c> line cannot be classified: taking the foreground away from the
    /// overflow panel is a real dismissal, taking it from nothing is an artefact of the harness.
    /// </summary>
    internal static void Opened(nint anchorWindow, nint foregroundAtOpen)
    {
        Write(string.Format(
            CultureInfo.InvariantCulture,
            "MENU OPENED            anchor=0x{0:X}  baseline foreground: {1}",
            anchorWindow,
            Describe(foregroundAtOpen)));
    }

    private static string Describe(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return "none (hwnd=0)";
        }

        var className = new StringBuilder(256);
        _ = GetClassNameW(hwnd, className, className.Capacity);
        _ = GetWindowThreadProcessId(hwnd, out var processId);

        var processName = "?";
        try
        {
            processName = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception)
        {
            // A window whose process has gone is still worth recording by id.
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "hwnd=0x{0:X} class={1} pid={2} ({3})", hwnd, className, processId, processName);
    }

    internal static void Note(string stage, string detail)
    {
        Write($"{stage,-22} {detail}");
    }

    /// <summary>Records a dismissal: who took the foreground, and how long the menu had been open.</summary>
    internal static void Dismissal(nint hwnd, TimeSpan sinceOpened)
    {
        var className = new StringBuilder(256);
        _ = GetClassNameW(hwnd, className, className.Capacity);
        _ = GetWindowThreadProcessId(hwnd, out var processId);

        var processName = "?";
        try
        {
            processName = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception)
        {
            // A window whose process has gone is still worth recording by id.
        }

        Write(string.Format(
            CultureInfo.InvariantCulture,
            "DISMISSED after {0,7:F0} ms by hwnd=0x{1:X} class={2} pid={3} ({4})",
            sinceOpened.TotalMilliseconds,
            hwnd,
            className,
            processId,
            processName));
    }

    private static void Write(string line)
    {
        try
        {
            var stamped = string.Format(
                CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff}  {1}", DateTime.Now, line);

            lock (Sync)
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                System.IO.File.AppendAllText(Path, stamped + Environment.NewLine);
            }
        }
        catch
        {
            // Instrumentation must never change what it measures, including by throwing.
        }
    }
}
