using System.Text;
using System.Text.Json;
using ServerMonitor.Infrastructure.Backup;

namespace ServerMonitor.Infrastructure.Tests.Backup;

public sealed class ZeroingBufferWriterTests
{
    [Fact]
    public void Grow_CopiesWrittenBytes_AndZeroesEveryOldBuffer()
    {
        var buffers = new List<byte[]>();
        using var writer = new ZeroingBufferWriter(1024, 8, length => Track(buffers, length));

        for (var i = 0; i < 100; i++)
        {
            writer.GetSpan(1)[0] = (byte)(i + 1);
            writer.Advance(1);
        }

        Assert.True(buffers.Count > 2);
        Assert.All(buffers[..^1], old => Assert.All(old, b => Assert.Equal(0, b)));
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (byte)i).ToArray(), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Dispose_ZeroesTheCurrentBuffer_AndBlocksFurtherUse()
    {
        var buffers = new List<byte[]>();
        var writer = new ZeroingBufferWriter(1024, 64, length => Track(buffers, length));
        "secret-bytes"u8.CopyTo(writer.GetSpan(12));
        writer.Advance(12);

        writer.Dispose();

        Assert.All(Assert.Single(buffers), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan());
        Assert.Throws<ObjectDisposedException>(() => writer.WrittenCount);
        writer.Dispose();
    }

    [Fact]
    public void CommittedBytes_AreCappedAtMaximum()
    {
        using var writer = new ZeroingBufferWriter(100, 16);

        writer.GetSpan(100);
        writer.Advance(100);
        writer.GetSpan(1);

        Assert.Equal(100, writer.WrittenCount);
        Assert.Throws<BackupPayloadTooLargeException>(() => writer.Advance(1));
        Assert.Equal(100, writer.WrittenCount);
    }

    [Fact]
    public void GetMemory_HintBeyondTheAllocationLimit_Throws_WithoutAllocating()
    {
        var buffers = new List<byte[]>();
        using var writer = new ZeroingBufferWriter(100, 16, length => Track(buffers, length));

        var limit = (int)((ZeroingBufferWriter.AllocationLimitFactor * 100) + ZeroingBufferWriter.AllocationHeadroom);
        writer.GetMemory(limit);
        Assert.Throws<BackupPayloadTooLargeException>(() => writer.GetMemory(limit + 1));
        Assert.Equal(2, buffers.Count);
    }

    // Utf8JsonWriter asks for a worst-case hint (~3x the string length); a hint-based cap would refuse
    // this payload even though its bytes fit.
    [Fact]
    public void Utf8JsonWriter_PessimisticHintNearTheCap_StillWritesAPayloadThatFits()
    {
        var value = new string('v', 900);
        using var buffer = new ZeroingBufferWriter(1024, 16);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("k", value);
            json.WriteEndObject();
        }

        Assert.Equal("{\"k\":\"" + value + "\"}", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void Advance_PastAvailableOrNegative_Throws()
    {
        using var writer = new ZeroingBufferWriter(100, 16);

        Assert.Throws<InvalidOperationException>(() => writer.Advance(17));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1));
        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void DefaultMaximum_IsTheFormatMaximumPlaintext()
    {
        using var writer = new ZeroingBufferWriter();

        writer.GetSpan(BackupFileCodec.MaxPlaintextLength);
        writer.Advance(BackupFileCodec.MaxPlaintextLength);
        writer.GetSpan(1);

        Assert.Throws<BackupPayloadTooLargeException>(() => writer.Advance(1));
    }

    [Fact]
    public void Utf8JsonWriter_WritesThroughIt()
    {
        using var buffer = new ZeroingBufferWriter(64 * 1024, 4);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteString("name", new string('n', 300));
            json.WriteEndObject();
        }

        Assert.Equal(
            "{\"schemaVersion\":1,\"name\":\"" + new string('n', 300) + "\"}",
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static byte[] Track(List<byte[]> buffers, int length)
    {
        var buffer = new byte[length];
        buffers.Add(buffer);
        return buffer;
    }
}
