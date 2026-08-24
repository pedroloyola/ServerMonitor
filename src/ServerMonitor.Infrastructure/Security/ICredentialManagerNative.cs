namespace ServerMonitor.Infrastructure.Security;

internal interface ICredentialManagerNative
{
    bool Write(ref NativeCredential credential, out int errorCode);

    bool Read(string targetName, out nint credential, out int errorCode);

    bool Delete(string targetName, out int errorCode);

    void Free(nint credential);
}
