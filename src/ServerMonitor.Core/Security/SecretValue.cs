using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ServerMonitor.Core.Security;

public sealed class SecretValue : IDisposable
{
    private char[]? _characters;

    public SecretValue(ReadOnlySpan<char> value)
    {
        _characters = value.ToArray();
    }

    public int Length => _characters?.Length ?? 0;

    public ReadOnlySpan<char> Reveal() => _characters
        ?? throw new ObjectDisposedException(nameof(SecretValue));

    public string RevealAsString() => new(Reveal());

    public void Dispose()
    {
        var characters = Interlocked.Exchange(ref _characters, null);
        if (characters is not null)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }

    public override string ToString() => "[REDACTED]";
}
