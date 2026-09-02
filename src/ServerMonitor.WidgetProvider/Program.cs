using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ServerMonitor.WidgetProvider.Com;
using ServerMonitor.WidgetProvider.Diagnostics;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider;

/// <summary>
/// Out-of-process COM entry point for ServerAlyzer.WidgetProvider.exe. It sweeps orphan temp files,
/// rehydrates existing widgets via GetWidgetInfos (bounded, before admitting COM callbacks so a stale
/// snapshot cannot race a Create/Delete), registers the provider's class factory, then serves until the
/// last COM object is released. Process lifetime uses the official COM protocol via
/// <see cref="ComServerProcess"/> (CoAddRefServerProcess/CoReleaseServerProcess): when the count reaches
/// zero, COM suspends new activations atomically and we revoke and exit; Windows relaunches the provider
/// on the next activation (ADR-018 §11–§15). The provider never opens SSH, the engine, credentials, or
/// history.
/// <para>
/// NOTE: this all builds against the real Windows App SDK widget API, but the runtime COM activation /
/// widget-board behavior can only be validated with a packaged install on a Widgets board (build
/// 22621.1413+) — a dev-mode/admin step (honest NOT_RUN on Windows Home). A local smoke launch confirms
/// the process starts, registers, and self-exits after the idle grace.
/// </para>
/// </summary>
internal static partial class Program
{
    private const uint ClsctxLocalServer = 0x4;
    private const uint RegclsMultipleuse = 0x1;

    /// <summary>Bounds a provider that was launched but never received a Create (§15).</summary>
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(30);

    /// <summary>Bounds startup rehydration so a hanging GetWidgetInfos cannot wedge the process (§M-1).</summary>
    private static readonly TimeSpan RehydrateTimeout = TimeSpan.FromSeconds(5);

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(
        in Guid rclsid, IntPtr pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint dwRegister);

    /// <summary>Returned when a fatal exception carries no usable failure HRESULT of its own.</summary>
    private const int EFail = unchecked((int)0x80004005);

    [MTAThread]
    private static int Main()
    {
        // Invisible sinks only (Trace + ETW). Nothing this process logs may reach a console: it is a
        // GUI-subsystem COM server and must stay windowless on the user's desktop (M13-QA-7).
        return RunGuarded(() => EtwWidgetProviderLog.Instance, Serve);
    }

    /// <summary>
    /// Last-resort barrier around the whole entry point (M13-QA-7). A GUI-subsystem process has no
    /// console, so an exception that escapes Main does not print a stack trace — it ends the process
    /// through Windows Error Reporting, and the WER dialog would be the ONE remaining way this provider
    /// can put pixels on the user's desktop during a board activation. Catching here converts that into a
    /// silent failure HRESULT plus an invisible log line. It changes no COM lifetime semantics: the whole
    /// serve loop, including its finally (Shutdown / CoRevokeClassObject / Marshal.Release), runs to
    /// completion inside <paramref name="body"/> before this catch is ever reached, and a registration
    /// failure still returns its own HRESULT through the normal path rather than being swallowed here.
    /// </summary>
    internal static int RunGuarded(Func<IWidgetProviderLog> logFactory, Func<IWidgetProviderLog, int> body)
    {
        // Resolved inside the guard: even building the log must not be able to reach WER.
        IWidgetProviderLog? log = null;
        try
        {
            log = logFactory();
            return body(log);
        }
        catch (Exception exception)
        {
            try
            {
                // Operation + exception type only, never the payload (ADR-018 §31).
                (log ?? NullWidgetProviderLog.Instance).Warn(
                    $"Widget provider terminated on an unhandled {exception.GetType().Name}.");
            }
            catch
            {
                // Diagnostics must never be the reason the process still dies by WER (§16).
            }

            return FailureHResult(exception);
        }
    }

    /// <summary>Maps a fatal exception onto an HRESULT the COM SCM sees as a failure, never a success.</summary>
    internal static int FailureHResult(Exception exception)
    {
        var hr = Marshal.GetHRForException(exception);
        return hr < 0 ? hr : EFail;
    }

    private static int Serve(IWidgetProviderLog log)
    {
        // Best-effort startup hygiene (Vigil L2): remove temp files a crashed writer may have left.
        new WidgetOrphanTempCleaner(log: log).Sweep();

        var host = new WidgetManagerHost();
        var coordinator = new WidgetProviderCoordinator(host, log: log);
        var process = new ComServerProcess();

        // Bootstrap reference: hold the process alive across startup so the serve loop's idle exit can be
        // performed by RELEASING this reference (which flows through CoReleaseServerProcess and, at zero,
        // CoSuspendClassObjects) instead of breaking out of the loop and racing an in-flight activation.
        process.AddRef();

        // Rehydrate BEFORE registering the class factory (no callback race, H-2), but bounded so a hanging
        // host call cannot wedge startup (M-1). Tombstones + the gate keep it safe even if it overruns.
        RunBounded(coordinator.RehydrateFromHost, RehydrateTimeout, log);

        var factory = new WidgetProviderClassFactory(
            () => new WidgetProviderComAdapter(coordinator, process, log),
            process,
            log);

        var factoryPtr = ComWrappers.GetOrCreateComInterfaceForObject(factory, CreateComInterfaceFlags.None);
        uint cookie = 0;
        try
        {
            var clsid = WidgetProviderComAdapter.Clsid;
            var hr = CoRegisterClassObject(clsid, factoryPtr, ClsctxLocalServer, RegclsMultipleuse, out cookie);
            if (hr < 0)
            {
                log.Warn($"CoRegisterClassObject failed (0x{hr:X8}).");
                return hr;
            }

            log.Info("Widget provider registered; serving host callbacks.");

            // Serve until the process reference count reaches zero, which — per the official protocol —
            // atomically suspends new activations (CoSuspendClassObjects) before we revoke. The idle exit
            // goes through the SAME barrier rather than around it: the bootstrap reference taken before
            // registration is released at the first idle checkpoint, so a launched-but-never-activated
            // provider drops to zero and suspends atomically, while a provider that HAS a live object stays
            // above zero (H-1). At each checkpoint we also reclaim COM adapters the host has already
            // released, so their process reference is dropped at a bounded point rather than an arbitrary
            // future GC (bounds the H-2 finalizer window).
            var bootstrapReleased = false;
            while (!process.WaitForExit(IdleGrace))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();

                if (!bootstrapReleased)
                {
                    bootstrapReleased = true;
                    process.Release(); // hand lifetime to live objects/locks; drops to zero if there are none
                }
            }

            return 0;
        }
        finally
        {
            // On EVERY exit path (normal, registration failure, or exception) invalidate and drain late
            // work BEFORE revoking, so a rehydration that overran its bound can neither add nor repaint
            // widgets as the class object is revoked (M-1).
            coordinator.Shutdown();

            if (cookie != 0)
            {
                CoRevokeClassObject(cookie);
            }

            Marshal.Release(factoryPtr);
        }
    }

    private static void RunBounded(Action action, TimeSpan timeout, IWidgetProviderLog log)
    {
        try
        {
            if (!Task.Run(action).Wait(timeout))
            {
                log.Warn("Startup rehydration did not complete in time; proceeding.");
            }
        }
        catch (Exception exception)
        {
            log.Warn($"Startup rehydration failed. Error: {exception.GetType().Name}.");
        }
    }
}
