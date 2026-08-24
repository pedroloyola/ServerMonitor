using System.Runtime.InteropServices;

namespace ServerMonitor.Infrastructure.Security;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCredential
{
    public uint Flags;
    public uint Type;
    public nint TargetName;
    public nint Comment;
    public long LastWritten;
    public uint CredentialBlobSize;
    public nint CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public nint Attributes;
    public nint TargetAlias;
    public nint UserName;
}
