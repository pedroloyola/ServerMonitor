using Microsoft.Extensions.Logging;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>
/// Writes the widget snapshot atomically (§12): serialize → write a uniquely-named temp file in the
/// SAME directory → flush to disk → atomic replace of the destination. Because the destination is only
/// ever touched by the final atomic move, a reader never observes a half-written file, and a failed new
/// write leaves the previous last-known-good intact (§13). Single-writer by construction — its only
/// caller (<c>WidgetSnapshotRecorder</c>) serializes writes — so no cross-process/thread file lock is
/// needed. Best-effort: on failure it removes the temp file and rethrows for the caller to isolate (§16).
/// </summary>
public sealed class AtomicWidgetStateWriter : IWidgetStateWriter
{
    private const int WriteBufferSize = 4096;

    private readonly WidgetStateOptions _options;
    private readonly ILogger<AtomicWidgetStateWriter> _logger;

    public AtomicWidgetStateWriter(WidgetStateOptions options, ILogger<AtomicWidgetStateWriter> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WriteAsync(WidgetStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var path = _options.SnapshotPath;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Widget snapshot path has no directory.");
        Directory.CreateDirectory(directory);

        // Serialize first: if serialization ever throws, no file was touched and the last-good remains.
        var bytes = WidgetStateSerializer.SerializeToUtf8Bytes(snapshot);

        // Unique temp name in the SAME directory (same volume) so the replace is a metadata-only atomic
        // rename, never a cross-volume copy. A crashed prior run may leave a *.tmp behind; it is inert
        // (the provider reads only widget-state.json) and is overwritten/cleaned by later writes.
        var tempPath = Path.Combine(directory, $"widget-state.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                WriteBufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Honor cancellation right up to the commit point: if asked to stop, abort before replacing
            // so the existing last-known-good is preserved untouched.
            cancellationToken.ThrowIfCancellationRequested();

            // Atomic replace on the same NTFS volume (MoveFileEx with MOVEFILE_REPLACE_EXISTING). Also
            // handles the first write, where the destination does not yet exist, as a plain rename.
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    private void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception exception)
        {
            // Never surface temp-cleanup problems: the real write already failed and is being rethrown,
            // and a stray temp file is inert. Log coarsely without the payload (§31).
            _logger.LogDebug(
                "Failed to remove widget snapshot temp file. Error: {Type}.",
                exception.GetType().Name);
        }
    }
}
