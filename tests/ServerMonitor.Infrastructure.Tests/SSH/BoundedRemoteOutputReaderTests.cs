using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

public sealed class BoundedRemoteOutputReaderTests
{
    [Fact]
    public async Task Reads_output_at_the_limit()
    {
        await using var stream = new MemoryStream(new byte[4096]);

        var result = await BoundedRemoteOutputReader.ReadAsync(stream, 4096, CancellationToken.None);

        Assert.Equal(4096, result.Length);
    }

    [Fact]
    public async Task Rejects_output_over_the_limit()
    {
        await using var stream = new MemoryStream(new byte[4097]);

        await Assert.ThrowsAsync<RemoteOutputLimitException>(() =>
            BoundedRemoteOutputReader.ReadAsync(stream, 4096, CancellationToken.None));
    }

    [Fact]
    public async Task Observes_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new CancelAwareStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedRemoteOutputReader.ReadAsync(stream, 4096, cancellation.Token));
    }

    private sealed class CancelAwareStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromCanceled<int>(cancellationToken);

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
