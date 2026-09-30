using System.Buffers;
using System.Security.Cryptography;

namespace ServerMonitor.Infrastructure.Backup;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> for backup payloads that contain secrets (M14.6 spec §4 step 4,
/// §6.5). The buffer is a pinned array, so the GC never leaves a relocated copy behind; when it grows,
/// the old buffer is zeroed after the copy; <see cref="Dispose"/> zeroes the final buffer.
/// <para>
/// Size: committed bytes (<see cref="Advance"/>) are capped at the maximum (default: the format's maximum
/// plaintext); a runaway payload fails with <see cref="BackupPayloadTooLargeException"/>. The cap is on
/// committed bytes, not on <c>sizeHint</c>, because <c>Utf8JsonWriter</c> asks for a worst-case hint (up to
/// ~3x a string's length for transcoding, and never less than 4 KiB) and would otherwise refuse, near the
/// cap, data that fits. Allocation is still bounded, at <see cref="AllocationLimitFactor"/> times the maximum
/// plus <see cref="AllocationHeadroom"/>.
/// </para>
/// <para>
/// Scope of the guarantee: this writer's own buffers only. Scratch buffers a caller such as
/// <c>Utf8JsonWriter</c> rents from <c>ArrayPool</c> are outside it (Vigil V7, BK-PLAT-1).
/// </para>
/// </summary>
internal sealed class ZeroingBufferWriter : IBufferWriter<byte>, IDisposable
{
    internal const int DefaultInitialCapacity = 4096;
    internal const long AllocationLimitFactor = 4;

    // Utf8JsonWriter requests at least 4 KiB per growth whatever it is about to write.
    internal const long AllocationHeadroom = 64 * 1024;

    private readonly int _maximumCapacity;
    private readonly Func<int, byte[]> _allocate;
    private byte[] _buffer;
    private int _written;
    private bool _disposed;

    public ZeroingBufferWriter(
        int maximumCapacity = BackupFileCodec.MaxPlaintextLength,
        int initialCapacity = DefaultInitialCapacity)
        : this(maximumCapacity, initialCapacity, allocate: null)
    {
    }

    /// <summary>Test seam: <paramref name="allocate"/> observes every buffer so a test can prove each
    /// one is zeroed.</summary>
    internal ZeroingBufferWriter(int maximumCapacity, int initialCapacity, Func<int, byte[]>? allocate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _maximumCapacity = maximumCapacity;
        _allocate = allocate ?? (static length => GC.AllocateArray<byte>(length, pinned: true));
        _buffer = _allocate(Math.Min(initialCapacity, maximumCapacity));
    }

    public int WrittenCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _written;
        }
    }

    public ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _buffer.AsSpan(0, _written);
        }
    }

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _written)
        {
            throw new InvalidOperationException("Cannot advance past the end of the buffer.");
        }

        if (count > _maximumCapacity - _written)
        {
            throw new BackupPayloadTooLargeException();
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_buffer);
        _buffer = [];
        _written = 0;
        _disposed = true;
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var needed = Math.Max(sizeHint, 1);
        if (_buffer.Length - _written >= needed)
        {
            return;
        }

        var allocationLimit = Math.Min((AllocationLimitFactor * _maximumCapacity) + AllocationHeadroom, Array.MaxLength);
        var required = (long)_written + needed;
        if (required > allocationLimit)
        {
            throw new BackupPayloadTooLargeException();
        }

        var grown = (int)Math.Max(required, Math.Min((long)_buffer.Length * 2, _maximumCapacity));
        var replacement = _allocate(grown);
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        CryptographicOperations.ZeroMemory(_buffer);
        _buffer = replacement;
    }
}

/// <summary>The payload would exceed the backup format's maximum plaintext size.</summary>
internal sealed class BackupPayloadTooLargeException : InvalidOperationException
{
    public BackupPayloadTooLargeException()
        : base("The backup payload exceeds the maximum size.")
    {
    }
}
