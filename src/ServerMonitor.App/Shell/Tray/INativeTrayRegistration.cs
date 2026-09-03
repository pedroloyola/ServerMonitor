namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// What a shell operation did, keeping the native outcomes SEPARATE.
/// </summary>
/// <remarks>
/// The boundary used to collapse an add into <c>Add() &amp;&amp; SetVersion()</c>, and that single boolean
/// destroyed the distinction the lifecycle depends on: <c>NIM_ADD FALSE</c> means the shell never took
/// the icon, while <c>NIM_ADD TRUE</c> with <c>NIM_SETVERSION FALSE</c> means the icon may well exist and
/// must be removed. Both arrived as <c>false</c>, so the machine could not tell "there was never anything
/// to delete" from "the delete failed" — which is what made a failed initial registration end the process
/// instead of degrading.
/// </remarks>
internal enum ShellOutcome
{
    /// <summary>The operation was not performed at all.</summary>
    NotPerformed = 0,

    /// <summary>Every native call in the operation reported success.</summary>
    Succeeded = 1,

    /// <summary>
    /// The operation failed and left NOTHING behind: for an add, <c>NIM_ADD</c> itself returned false, so
    /// the shell is not holding an icon of ours as a result of it.
    /// </summary>
    FailedWithoutEffect = 2,

    /// <summary>
    /// The operation failed AFTER a native call that may have created an effect: <c>NIM_ADD</c> succeeded
    /// and a later call did not. The icon may exist and removing it is mandatory.
    /// </summary>
    FailedWithPossibleEffect = 3
}

/// <summary>The shell operations the tray boundary is allowed to perform. Closed by design.</summary>
internal enum NativeTrayOperation
{
    /// <summary>No shell call at all. Used by effects that are not shell I/O.</summary>
    None = 0,

    /// <summary><c>NIM_ADD</c> followed by <c>NIM_SETVERSION</c>.</summary>
    Add = 1,

    /// <summary><c>NIM_DELETE</c>.</summary>
    Delete = 2
}

/// <summary>
/// The ability to touch the Windows notification area. <b>This is a capability, not a service.</b>
/// <para>
/// It is deliberately never registered in the container and is held by exactly one type —
/// <c>TrayStateMachine.EffectExecutor</c> — because the CV-20 defect was our own machinery being usable
/// as a vehicle for an effect of unproven origin. The allowlist of members that may name this type is
/// enumerated by identity in the architecture test, not described by exclusion.
/// </para>
/// <para>
/// Every method returns the <b>observed</b> BOOL from <c>Shell_NotifyIconW</c>. Nothing here infers
/// success: that inference is the original S2-T defect.
/// </para>
/// </summary>
internal interface INativeTrayRegistration
{
    /// <summary>
    /// <c>NIM_ADD</c>. Returns the real BOOL. A <c>false</c> means the shell did not take the icon.
    /// </summary>
    bool Add();

    /// <summary>
    /// <c>NIM_SETVERSION</c> with <c>NOTIFYICON_VERSION_4</c>. Documented to return <c>false</c> when the
    /// requested version is unsupported, which is a contract failure for us: the v4 anchor coordinates
    /// are what position the flyout, so we never silently degrade to v3.
    /// </summary>
    bool SetVersion();

    /// <summary>
    /// <c>NIM_DELETE</c>. Returns the real BOOL. <c>false</c> is benign when no icon exists and is a
    /// failure only when we know an effect of ours may still be held by the shell.
    /// </summary>
    bool Delete();
}
