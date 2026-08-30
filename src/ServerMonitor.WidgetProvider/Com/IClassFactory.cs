using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ServerMonitor.WidgetProvider.Com;

/// <summary>
/// The classic COM <c>IClassFactory</c>, declared with source-generated COM interop
/// (<see cref="GeneratedComInterfaceAttribute"/>) so no built-in COM marshalling is required. The
/// Widgets host calls <see cref="CreateInstance"/> through the CLSID registered with
/// <c>CoRegisterClassObject</c> to obtain the provider.
/// </summary>
[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, in Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}
