namespace ServerMonitor.Infrastructure.SSH;

internal static class BoundedRemoteOutputReader
{
    internal static async Task<byte[]> ReadAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var buffer = new MemoryStream(Math.Min(maximumBytes, 4096));
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new RemoteOutputLimitException();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class RemoteOutputLimitException : Exception;
