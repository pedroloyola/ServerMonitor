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

    /// <summary>Suffix of the backup ReplaceFile keeps the old snapshot in. Not a <c>.tmp</c>, so the
    /// provider's orphan sweep never deletes it (it could be the only surviving good copy).</summary>
    internal const string BackupSuffix = ".bak";

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
        // rename, never a cross-volume copy. The name follows the shared WidgetStateLocation pattern so
        // the provider's orphan sweep can recognize and clean a temp left by a crashed prior run; it is
        // otherwise inert (the provider reads only widget-state.json).
        var tempPath = WidgetStateLocation.NewTempPath(directory);
        var backupPath = path + BackupSuffix;

        // Startup/pre-write recovery: a prior crash mid-replace can leave the destination missing but the
        // old-good copy in the backup. Promote it BEFORE risking a new write, so a subsequent write
        // failure can never leave us with no snapshot at all (Atlas/Vigil S2 M-2). Never delete the backup
        // unconditionally here — it may be the only complete copy.
        RecoverLastKnownGood(path, tempPath: string.Empty, backupPath);

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

            // Defense-in-depth (Vigil S2 L-2): never write THROUGH a reparse point we did not create —
            // File.Replace would follow a symlink/junction to its target.
            var destination = new FileInfo(path);
            if (destination.Exists && destination.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Widget snapshot destination is a reparse point.");
            }

            if (destination.Exists)
            {
                // ReplaceFile (File.Replace) succeeds even while the out-of-process provider holds the
                // file open for reading with FileShare.Delete, which File.Move(overwrite) does not. We
                // pass an explicit BACKUP so last-known-good is always recoverable: without it, a
                // ReplaceFile partial failure (ERROR_UNABLE_TO_MOVE_REPLACEMENT) can leave the old
                // destination gone and the new content stranded under the temp name — deleting the temp
                // would then destroy the only complete copy (Atlas/Vigil S2 M-2, §13).
                File.Replace(tempPath, path, backupPath); // failure is handled by the outer catch
                TryDelete(backupPath); // success: the old copy is no longer needed
            }
            else
            {
                // First write: nothing to replace, so a plain rename is the atomic primitive.
                File.Move(tempPath, path);
            }
        }
        catch
        {
            // Guarantee a complete snapshot still exists, then clean working files — but NEVER delete the
            // backup while the destination is missing (it may be the only good copy, §13).
            RecoverLastKnownGood(path, tempPath, backupPath);
            TryDelete(tempPath);
            if (File.Exists(path))
            {
                TryDelete(backupPath);
            }

            throw;
        }
    }

    /// <summary>
    /// After a failed ReplaceFile, guarantee a COMPLETE snapshot still exists at <paramref name="path"/>.
    /// If the destination survived (a failure before any mutation) there is nothing to do. If it was
    /// removed by a partial replace, restore the OLD good copy from the backup, or — failing that —
    /// salvage the freshly-written temp (also a complete file). Best-effort; never throws.
    /// </summary>
    internal static void RecoverLastKnownGood(string path, string tempPath, string backupPath)
    {
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            if (File.Exists(backupPath))
            {
                File.Move(backupPath, path);
            }
            else if (File.Exists(tempPath))
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            // Nothing more we can safely do; the next monitoring cycle rewrites the snapshot.
        }
    }

    private void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception exception)
        {
            // Never surface cleanup problems: a stray temp/backup is inert. Log coarsely (§31).
            _logger.LogDebug(
                "Failed to remove a widget snapshot working file. Error: {Type}.",
                exception.GetType().Name);
        }
    }
}
