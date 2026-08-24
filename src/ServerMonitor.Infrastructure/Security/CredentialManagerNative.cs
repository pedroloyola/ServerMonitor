using System.Runtime.InteropServices;

namespace ServerMonitor.Infrastructure.Security;

internal sealed class CredentialManagerNative : ICredentialManagerNative
{
    private const string Advapi32 = "advapi32.dll";
    private const uint CredentialTypeGeneric = 1;

    public bool Write(ref NativeCredential credential, out int errorCode)
    {
        var succeeded = CredWriteW(ref credential, 0) != 0;
        errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public bool Read(string targetName, out nint credential, out int errorCode)
    {
        var succeeded = CredReadW(targetName, CredentialTypeGeneric, 0, out credential) != 0;
        errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public bool Delete(string targetName, out int errorCode)
    {
        var succeeded = CredDeleteW(targetName, CredentialTypeGeneric, 0) != 0;
        errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public void Free(nint credential) => CredFree(credential);

    [DllImport(Advapi32, EntryPoint = "CredWriteW", ExactSpelling = true, SetLastError = true)]
    private static extern int CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport(
        Advapi32,
        EntryPoint = "CredReadW",
        ExactSpelling = true,
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern int CredReadW(
        string targetName,
        uint type,
        uint flags,
        out nint credential);

    [DllImport(
        Advapi32,
        EntryPoint = "CredDeleteW",
        ExactSpelling = true,
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern int CredDeleteW(string targetName, uint type, uint flags);

    [DllImport(Advapi32, EntryPoint = "CredFree", ExactSpelling = true)]
    private static extern void CredFree(nint buffer);
}
