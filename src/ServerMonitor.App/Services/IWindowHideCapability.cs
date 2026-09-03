namespace ServerMonitor.App.Services;

/// <summary>
/// The right to hide the window into the background — a CAPABILITY, held by name and by type, and not a
/// member of the general window contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the door, and it is the seventh ring.</b> Six corrections removed six ways of OBTAINING the
/// permission — a fabricable token, an implementable channel, a readable property, a returned bool, an
/// arbitrary delegate, and finally the caller's own code running inside the authorisation — and every one
/// of them left <c>HideToBackground()</c> on <see cref="IApplicationWindowController"/>, which is
/// registered globally. So the act itself stayed reachable by anyone who held the window contract, and
/// knowing the affordance had been valid was enough to use it afterwards. Closing the door only for the
/// window-close coordinator closed it for one caller and left it open for everybody else: measured
/// holders were MainWindow, TrayService, WindowsAppNotificationService and two resolutions in the
/// composition root.
/// </para>
/// <para>
/// The cure is the one CV-20 already proved for <c>INativeTrayRegistration</c>: take the capability out of
/// the general contract AND out of the container, give it a small number of named holders, and let an
/// architecture test enumerate them. There are exactly two, and they are the two the design always
/// intended — the guarded operation, and the authorised exit path.
/// </para>
/// <para>
/// <b>Declared residual.</b> Everything here lives in one assembly, so this is not unforgeable the way a
/// private nested type is: assembly code could construct <c>WindowHideCapability</c> for itself. What is
/// enforced is what the closure criterion asks for — the capability is off the general contract, absent
/// from the container, and restricted BY TYPE to an enumerated pair, with a mutation that injects it into
/// a third consumer failing the test.
/// </para>
/// </remarks>
public interface IWindowHideCapability
{
    /// <summary>
    /// Hides the Dashboard for the BACKGROUND state (M13 S2). Tolerates there being no window at all,
    /// which is the headless case.
    /// </summary>
    void HideToBackground();

    /// <summary>
    /// The minimize hide. Same mechanics, same guard, same capability — see
    /// <see cref="TrayGuardedOperation.HideForMinimize"/> for why keeping it on the general contract left
    /// the door open under a second name.
    /// </summary>
    void HideForMinimize();
}
