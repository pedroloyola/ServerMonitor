using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ServerMonitor.WidgetProvider.Diagnostics;

namespace ServerMonitor.WidgetProvider.Com;

/// <summary>
/// COM class factory for <see cref="WidgetProviderComAdapter"/>. The Widgets host activates the provider
/// by CLSID; this factory creates the adapter and hands back the requested WinRT interface. Marshalling
/// the WinRT adapter is delegated to CsWinRT; the factory itself is exposed through source-generated COM.
/// Neutral-on-exception: a failure returns an HRESULT, never an escaping .NET exception (§16).
/// <see cref="LockServer"/> ties into the same <see cref="ComServerProcess"/> lifetime protocol as the
/// created objects, so an outstanding lock keeps the process alive (L-1).
/// </summary>
[GeneratedComClass]
internal sealed partial class WidgetProviderClassFactory : IClassFactory
{
    private const int SOk = 0;
    private const int EFail = unchecked((int)0x80004005);
    private const int ClassENoAggregation = unchecked((int)0x80040110);

    private readonly Func<WidgetProviderComAdapter> _create;
    private readonly ComServerProcess _process;
    private readonly IWidgetProviderLog _log;

    public WidgetProviderClassFactory(
        Func<WidgetProviderComAdapter> create,
        ComServerProcess process,
        IWidgetProviderLog log)
    {
        _create = create;
        _process = process;
        _log = log;
    }

    public int CreateInstance(IntPtr pUnkOuter, in Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;

        if (pUnkOuter != IntPtr.Zero)
        {
            return ClassENoAggregation; // no aggregation support
        }

        try
        {
            var adapter = _create();
            var inspectable = WinRT.MarshalInspectable<WidgetProviderComAdapter>.FromManaged(adapter);
            try
            {
                return Marshal.QueryInterface(inspectable, in riid, out ppvObject);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
        catch (Exception exception)
        {
            _log.Warn($"Widget provider activation failed. Error: {exception.GetType().Name}.");
            return EFail;
        }
    }

    public int LockServer(bool fLock)
    {
        // Keep the server process alive while an external lock is held, via the same ref protocol.
        if (fLock)
        {
            _process.AddRef();
        }
        else
        {
            _process.Release();
        }

        return SOk;
    }
}
