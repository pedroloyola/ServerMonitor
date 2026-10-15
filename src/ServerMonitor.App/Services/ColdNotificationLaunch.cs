using ServerMonitor.ActivationContract;

namespace ServerMonitor.App.Services;

/// <summary>
/// REL.0D D-3: the start of a process that COM launched for a notification click.
/// <para>
/// Both package manifests declare the toast activator as a COM ExeServer whose <c>Arguments</c> are
/// <see cref="ActivationToken"/>, so a click on a toast while the app is closed starts
/// <c>ServerMonitor.App.exe ----AppNotificationActivated:</c>. In that process the Windows App SDK answers
/// <c>AppInstance.GetActivatedEventArgs()</c> by waiting for the click's COM <c>Activate</c> call on an event that
/// only <c>AppNotificationManager.Register()</c> creates (WinAppSDK <c>AppNotificationManager::Deserialize</c>). Asked
/// BEFORE <c>Register()</c>, the handle is null and WIL fail-fasts the process (<c>wil::handle_wait</c>,
/// 0xc0000409 in Microsoft.WindowsAppRuntime.dll): the cold-click crash. A native fail-fast cannot be caught.
/// </para>
/// <para>
/// The fix is Microsoft's documented order (AppNotifications spec: connect <c>NotificationInvoked</c>, call
/// <c>Register()</c>, and only then <c>GetActivatedEventArgs()</c>), and <see cref="ReadActivation"/> is the one
/// place that enforces it: for a notification launch the arguments are read only after a successful
/// registration, and never otherwise.
/// </para>
/// </summary>
public static class ColdNotificationLaunch
{
    /// <summary>
    /// The ExeServer <c>Arguments</c> of the toast activator in BOTH <c>Package.appxmanifest</c> and
    /// <c>Package.Dev.appxmanifest</c> (tested), and the marker the Windows App SDK looks for in the command line.
    /// </summary>
    public const string ActivationToken = "----AppNotificationActivated:";

    /// <summary>
    /// True when this process was started for a notification click. Deliberately a superset of the SDK's own
    /// (case-sensitive, argv-based) test: a raw, case-insensitive search of the whole command line. A false
    /// positive only registers notifications early, which every launch could do safely; a false negative would
    /// bring the fail-fast back.
    /// </summary>
    public static bool IsNotificationLaunch(string? commandLine) =>
        commandLine is not null && commandLine.Contains(ActivationToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads this launch's activation arguments in the documented order, without ever letting the read crash the
    /// start:
    /// <list type="bullet">
    /// <item>a notification launch first registers (<paramref name="tryRegisterNotifications"/>); if that is not
    /// possible (unsupported, or it failed) the arguments are NOT read, because the read would fail-fast, and the
    /// launch continues as a plain one (<c>null</c>);</item>
    /// <item>a thrown read (for example the SDK's <c>ERROR_TIMEOUT</c> when the click's <c>Activate</c> never comes)
    /// is a plain launch too, never a failed start.</item>
    /// </list>
    /// </summary>
    public static TArgs? ReadActivation<TArgs>(
        string? commandLine,
        Func<bool> tryRegisterNotifications,
        Func<TArgs?> readActivatedEventArgs)
        where TArgs : class
    {
        ArgumentNullException.ThrowIfNull(tryRegisterNotifications);
        ArgumentNullException.ThrowIfNull(readActivatedEventArgs);

        if (IsNotificationLaunch(commandLine) && !tryRegisterNotifications())
        {
            return null;
        }

        try
        {
            return readActivatedEventArgs();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What a cold notification click asks the starting app to show, through the same single activation hand-off
    /// the widget uses. The closed notification contract decides (fail closed): a health or fail-safe notice opens
    /// the Dashboard — the target a warm click restores — and everything else is a plain launch (<c>null</c>). The
    /// first-close notice's Settings › Background target has no activation intent; a cold click on it opens the
    /// app normally (the notice only exists while the app runs, so this needs the app to have exited since).
    /// </summary>
    public static ActivationIntent? IntentFor(IReadOnlyDictionary<string, string>? arguments) =>
        NotificationActivationContract.ResolveAction(arguments) == NotificationAction.OpenDashboard
            ? ActivationIntent.Dashboard
            : null;
}
