using System.Security.Cryptography;
using System.Text;

namespace ServerMonitor.Infrastructure.Security;

internal sealed class SensitiveByteBuffer : IDisposable
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly byte[] _bytes;
    private bool _disposed;

    private SensitiveByteBuffer(byte[] bytes)
    {
        _bytes = bytes;
    }

    public int Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes.Length;
        }
    }

    public static SensitiveByteBuffer FromUtf8(ReadOnlySpan<char> value)
    {
        var bytes = new byte[StrictUtf8.GetByteCount(value)];
        try
        {
            StrictUtf8.GetBytes(value, bytes);
            return new SensitiveByteBuffer(bytes);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public byte[] DangerousGetArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _bytes;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_bytes);
        _disposed = true;
    }
}
