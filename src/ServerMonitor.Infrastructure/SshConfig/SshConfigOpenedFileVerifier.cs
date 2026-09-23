using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ServerMonitor.Infrastructure.SshConfig;

internal enum SshConfigOpenedFileIdentity
{
    Verified,

    /// <summary>The handle's final path is not the checked path (a link swapped in after the check, or a network path).</summary>
    FinalPathMismatch,

    /// <summary>The file has more than one hard link: the same bytes may also be reachable from outside ~/.ssh.</summary>
    HardLinked,

    /// <summary>Identity could not be established (not a file handle, or the query failed). Never trusted.</summary>
    Unverifiable
}

/// <summary>
/// Post-open identity check for SSH config files, closing the gap between "the path was checked"
/// and "the handle that was opened is that path":
/// <list type="bullet">
/// <item>the handle's final path (<c>GetFinalPathNameByHandleW</c>, normalized, DOS volume name) must equal
/// the checked path with 8.3 components expanded (<c>GetLongPathNameW</c>), ignoring case; a
/// <c>\\?\UNC\</c> final path is refused;</item>
/// <item>the file must have exactly one link (<c>GetFileInformationByHandle</c>), because a hard link is
/// indistinguishable by path or attributes and would let content from outside ~/.ssh be read.</item>
/// </list>
/// Anything that cannot be established is <see cref="SshConfigOpenedFileIdentity.Unverifiable"/>.
/// </summary>
internal static class SshConfigOpenedFileVerifier
{
    private const string Kernel32 = "kernel32.dll";
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    public static SshConfigOpenedFileIdentity Verify(Stream stream, string expectedPath)
    {
        if (stream is not FileStream file)
        {
            return SshConfigOpenedFileIdentity.Unverifiable;
        }

        var handle = file.SafeFileHandle;
        var finalPath = GetFinalPath(handle);
        var expected = GetLongPath(expectedPath);
        if (finalPath is null || expected is null)
        {
            return SshConfigOpenedFileIdentity.Unverifiable;
        }

        if (finalPath.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase)
            || !finalPath.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
            || !string.Equals(finalPath[ExtendedPrefix.Length..], expected, StringComparison.OrdinalIgnoreCase))
        {
            return SshConfigOpenedFileIdentity.FinalPathMismatch;
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            return SshConfigOpenedFileIdentity.Unverifiable;
        }

        return information.NumberOfLinks == 1
            ? SshConfigOpenedFileIdentity.Verified
            : SshConfigOpenedFileIdentity.HardLinked;
    }

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            if (length > 32_768)
            {
                return null;
            }

            buffer = new char[length + 1];
        }
    }

    private static string? GetLongPath(string path)
    {
        var buffer = new char[512];
        while (true)
        {
            var length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            if (length > 32_768)
            {
                return null;
            }

            buffer = new char[length + 1];
        }
    }

    [DllImport(Kernel32, EntryPoint = "GetFinalPathNameByHandleW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint pathLength, uint flags);

    [DllImport(Kernel32, EntryPoint = "GetLongPathNameW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);

    [DllImport(Kernel32, EntryPoint = "GetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    // BY_HANDLE_FILE_INFORMATION; FILETIMEs are two DWORDs each, so uint pairs keep the native layout.
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
