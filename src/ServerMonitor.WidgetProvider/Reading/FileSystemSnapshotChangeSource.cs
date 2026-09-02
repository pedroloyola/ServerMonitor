using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Diagnostics;

namespace ServerMonitor.WidgetProvider.Reading;

/// <summary>
/// Watches the snapshot's <b>directory</b> — never the file path — for commits of
/// <c>widget-state.json</c>.
/// <para>
/// <b>Why the directory.</b> The app never writes the destination in place. It writes a uniquely-named
/// temp in the SAME folder and commits with <c>File.Replace</c> (ReplaceFile), or <c>File.Move</c> on the
/// very first write (see <c>AtomicWidgetStateWriter</c>). Both are rename/replace operations: the
/// destination's identity is SWAPPED, not modified. A path-scoped watcher is bound to a directory entry
/// that is being replaced underneath it, and there is no guarantee it receives a <c>Changed</c> event at
/// all. Watching the directory and accepting <b>Created, Changed, Deleted and Renamed</b> — matching the
/// new name OR the old name — covers every shape those two primitives produce.
/// </para>
/// <para>
/// <b>Events are a hint, never a source of truth.</b> One commit legitimately produces several (temp
/// created, temp renamed onto the destination, destination renamed to <c>.bak</c>, backup deleted), and
/// events can be LOST when the OS internal buffer overflows — which surfaces as
/// <see cref="FileSystemWatcher.Error"/> and drops everything in between. So this source never says WHAT
/// changed: every signal means only "re-read". <see cref="WidgetSnapshotChangeWatcher"/> coalesces the
/// duplicates and carries a periodic backstop re-read for what is lost.
/// </para>
/// <para>
/// <b>Fault handling without re-entrancy.</b> A watcher that raised <see cref="FileSystemWatcher.Error"/>
/// is no longer delivering reliably, so the source marks itself faulted (<see cref="IsWatching"/> goes
/// false) and signals a re-read. It deliberately does NOT dispose the watcher from inside the watcher's
/// own callback; the caller's backstop re-establishes it from a timer thread instead.
/// </para>
/// Nothing here can throw at the caller: a missing directory or an OS refusal simply leaves the source
/// inert, because a diagnostic-grade watcher must never be able to fault the COM server (ADR-018 §16).
/// </summary>
public sealed class FileSystemSnapshotChangeSource : ISnapshotChangeSource
{
    /// <summary>Generous, so an unrelated burst in the folder cannot overflow and drop our commit.</summary>
    private const int WatcherBufferBytes = 64 * 1024;

    private readonly string _directory;
    private readonly string _fileName;
    private readonly IWidgetProviderLog _log;
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>
    /// Set from the watcher's own error callback, which must never take <see cref="_gate"/>: doing so
    /// could deadlock against a <see cref="Stop"/> that holds the gate while disposing the watcher.
    /// </summary>
    private volatile bool _faulted;

    public FileSystemSnapshotChangeSource(string? snapshotPath = null, IWidgetProviderLog? log = null)
    {
        var path = snapshotPath ?? WidgetStateLocation.ForCurrentUser();
        _directory = Path.GetDirectoryName(path) ?? WidgetStateLocation.DirectoryForCurrentUser();
        _fileName = Path.GetFileName(path);
        _log = log ?? NullWidgetProviderLog.Instance;
    }

    public event Action? Changed;

    public bool IsWatching
    {
        get { lock (_gate) { return _watcher is not null && !_faulted; } }
    }

    /// <summary>
    /// Establishes a watch, replacing a faulted one. Never throws; on failure the source stays inert and
    /// <see cref="IsWatching"/> reports false so the caller's backstop retries later.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_watcher is not null)
            {
                if (!_faulted)
                {
                    return; // already healthy
                }

                // Replace the faulted watcher. Safe under the gate: no watcher callback takes it.
                DisposeWatcher(_watcher);
                _watcher = null;
                _faulted = false;
            }

            try
            {
                if (!Directory.Exists(_directory))
                {
                    // The app has not written a snapshot yet. Inert, not an error: the backstop retries.
                    return;
                }

                var watcher = new FileSystemWatcher(_directory)
                {
                    // "*", not the file name: the commit is a rename, so the event that matters may carry
                    // the temp file as OldName. Filtering at the watcher would discard exactly that event.
                    Filter = "*",
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size
                        | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    InternalBufferSize = WatcherBufferBytes
                };

                watcher.Created += OnFileEvent;
                watcher.Changed += OnFileEvent;
                watcher.Deleted += OnFileEvent;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception exception)
            {
                // Never propagate: the widget still works, it just falls back to the backstop cadence.
                _log.Warn($"Snapshot watcher could not start. Error: {exception.GetType().Name}.");
                _watcher = null;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            DisposeWatcher(_watcher);
            _watcher = null;
            _faulted = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeWatcher(_watcher);
            _watcher = null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs args)
    {
        if (IsSnapshot(args.Name))
        {
            Raise();
        }
    }

    /// <summary>
    /// A commit renames temp to <c>widget-state.json</c>, so the snapshot is the NEW name.
    /// <c>File.Replace</c> also renames <c>widget-state.json</c> to <c>widget-state.json.bak</c>, where it
    /// is the OLD name. Both mean "re-read".
    /// </summary>
    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        if (IsSnapshot(args.Name) || IsSnapshot(args.OldName))
        {
            Raise();
        }
    }

    /// <summary>
    /// Test seams: drive the REAL event handlers with the argument shapes Windows produces, so the name
    /// filter can be proved exhaustively — including that unrelated files in the same directory raise
    /// nothing — without waiting on the OS to NOT do something, which is unprovable in bounded time.
    /// The handlers themselves are exercised end to end against the real filesystem by the positive tests.
    /// </summary>
    internal void SimulateFileEventForTesting(WatcherChangeTypes change, string name) =>
        OnFileEvent(this, new FileSystemEventArgs(change, _directory, name));

    /// <inheritdoc cref="SimulateFileEventForTesting"/>
    internal void SimulateRenameForTesting(string newName, string oldName) =>
        OnRenamed(this, new RenamedEventArgs(WatcherChangeTypes.Renamed, _directory, newName, oldName));

    /// <summary>
    /// Test seam: drives the REAL <see cref="OnError"/> handler, so the fault path (mark faulted, signal a
    /// re-read, let the caller's backstop re-establish the watch) can be proved deterministically. The OS
    /// only raises that event on an internal-buffer overflow, which cannot be provoked on demand without a
    /// timing-dependent flood.
    /// </summary>
    internal void SimulateWatcherErrorForTesting(Exception error) =>
        OnError(this, new ErrorEventArgs(error));

    /// <summary>
    /// The OS buffer overflowed (or the watch broke): events were LOST and we cannot know whether the
    /// snapshot was among them, so signal unconditionally. Mark faulted rather than disposing here — this
    /// runs on the watcher's own callback thread.
    /// </summary>
    private void OnError(object sender, ErrorEventArgs args)
    {
        _faulted = true;
        _log.Warn($"Snapshot watcher faulted; re-reading and rearming. Error: {args.GetException().GetType().Name}.");
        Raise();
    }

    /// <summary>
    /// <see cref="FileSystemEventArgs.Name"/> is relative to the watched directory; for a non-recursive
    /// watch that is the bare file name.
    /// </summary>
    private bool IsSnapshot(string? name) =>
        string.Equals(name, _fileName, StringComparison.OrdinalIgnoreCase);

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception exception)
        {
            // This runs on a threadpool callback owned by FileSystemWatcher; an escaping exception would
            // tear the process down (§16).
            _log.Warn($"Snapshot change handler failed. Error: {exception.GetType().Name}.");
        }
    }

    private void DisposeWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnFileEvent;
            watcher.Changed -= OnFileEvent;
            watcher.Deleted -= OnFileEvent;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
        catch (Exception exception)
        {
            _log.Warn($"Snapshot watcher teardown failed. Error: {exception.GetType().Name}.");
        }
    }
}
