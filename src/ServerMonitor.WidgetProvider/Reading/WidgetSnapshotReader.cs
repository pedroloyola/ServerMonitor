using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Diagnostics;

namespace ServerMonitor.WidgetProvider.Reading;

/// <summary>
/// Reads and validates <c>widget-state.json</c> as an UNTRUSTED file (L-018). The persisted snapshot is
/// the sole data source (ADR-018); the provider never opens SSH, credentials, or history. Every failure
/// path returns a neutral <see cref="WidgetReadResult"/> — the reader never throws — so a missing,
/// oversized, corrupt, tampered, or locked file makes the widget show "unavailable" rather than crash
/// the Widgets host (§16).
/// <para>
/// Order of defense (§7/§9): existence → <b>size cap enforced before reading a single byte</b> (Vigil
/// L1) → bounded read with a share mode that never blocks the writer's atomic replace → structural
/// deserialize (null on malformed) → full bounds/enum/timestamp validation.
/// </para>
/// </summary>
public sealed class WidgetSnapshotReader
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly TimeProvider _timeProvider;
    private readonly IWidgetProviderLog _log;

    public WidgetSnapshotReader(
        string? path = null,
        long? maxBytes = null,
        TimeProvider? timeProvider = null,
        IWidgetProviderLog? log = null)
    {
        _path = path ?? WidgetStateLocation.ForCurrentUser();
        _maxBytes = maxBytes ?? WidgetStateLocation.MaxFileBytes;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log ?? NullWidgetProviderLog.Instance;
    }

    public WidgetReadResult Read()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                return WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing);
            }

            // Cap BEFORE opening/reading: never pull an arbitrarily large file into memory just to find
            // out it is invalid.
            if (info.Length > _maxBytes)
            {
                _log.Warn("Widget snapshot exceeds the size cap; treating as unavailable.");
                return WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Oversized);
            }

            byte[] bytes;
            // ReadWrite + Delete share so the app's File.Move atomic replace is never blocked by our read
            // (Atlas Slice-1 note); we get a consistent view of whichever file version we opened.
            using (var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                var length = stream.Length;
                if (length > _maxBytes)
                {
                    return WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Oversized);
                }

                bytes = new byte[length];
                stream.ReadExactly(bytes, 0, bytes.Length);
            }

            var snapshot = WidgetStateSerializer.TryDeserialize(bytes);
            if (snapshot is null)
            {
                return WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Corrupt);
            }

            var validation = WidgetStateValidator.Validate(snapshot, _timeProvider.GetUtcNow());
            if (!validation.IsValid)
            {
                _log.Warn($"Widget snapshot failed validation ({validation.Failure}); treating as unavailable.");
                return WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Invalid);
            }

            return WidgetReadResult.Available(snapshot);
        }
        catch (Exception exception)
        {
            // Any IO/parse failure is contained here — never across the COM boundary (§16/§31).
            _log.Warn($"Widget snapshot read failed. Error: {exception.GetType().Name}.");
            return WidgetReadResult.Unavailable(WidgetReadUnavailableReason.IoError);
        }
    }
}
