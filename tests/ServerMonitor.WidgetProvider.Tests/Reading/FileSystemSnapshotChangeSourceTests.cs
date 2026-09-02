using System.Collections.Concurrent;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Reading;
using Xunit.Abstractions;

namespace ServerMonitor.WidgetProvider.Tests.Reading;

/// <summary>
/// REAL filesystem tests for the change source. These deliberately exercise the writer's own commit
/// primitives — a uniquely-named temp in the same directory plus <c>File.Replace</c>, or <c>File.Move</c>
/// on the first write — because the whole point of the defect was that a plausible-looking watcher can be
/// green in theory and silent in practice. Waits are event-driven with a generous timeout; there are no
/// fixed sleeps and no assumptions about machine speed.
/// </summary>
public sealed class FileSystemSnapshotChangeSourceTests : IDisposable
{
    /// <summary>Generous: the OS delivers watcher events asynchronously and we never poll for them.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(20);

    private readonly ITestOutputHelper _output;
    private readonly string _dir;
    private readonly string _path;

    public FileSystemSnapshotChangeSourceTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "sm-widgetwatch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, WidgetStateLocation.FileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>The writer's first-write primitive: temp in the same folder, then a plain rename.</summary>
    private void FirstWrite(string content)
    {
        var temp = WidgetStateLocation.NewTempPath(_dir);
        File.WriteAllText(temp, content);
        File.Move(temp, _path);
    }

    /// <summary>The writer's steady-state commit: temp in the same folder, then ReplaceFile with a backup.</summary>
    private void AtomicReplace(string content)
    {
        var temp = WidgetStateLocation.NewTempPath(_dir);
        File.WriteAllText(temp, content);
        File.Replace(temp, _path, _path + ".bak");
        File.Delete(_path + ".bak");
    }

    [Fact]
    public void A_source_that_was_never_started_is_not_watching()
    {
        using var source = new FileSystemSnapshotChangeSource(_path);

        Assert.False(source.IsWatching);
    }

    [Fact]
    public void Starting_on_a_missing_directory_stays_inert_instead_of_throwing()
    {
        var missing = Path.Combine(_dir, "not-created-yet", WidgetStateLocation.FileName);
        using var source = new FileSystemSnapshotChangeSource(missing);

        source.Start();

        // Inert, not an error: the pump's backstop retries once the app has written its first snapshot.
        Assert.False(source.IsWatching);
    }

    [Fact]
    public void Start_and_stop_are_idempotent()
    {
        using var source = new FileSystemSnapshotChangeSource(_path);

        source.Start();
        source.Start();
        Assert.True(source.IsWatching);

        source.Stop();
        source.Stop();
        Assert.False(source.IsWatching);
    }

    [Fact]
    public void The_first_write_ever_move_onto_a_missing_destination_is_detected()
    {
        using var signalled = new ManualResetEventSlim(false);
        using var source = new FileSystemSnapshotChangeSource(_path);
        source.Changed += () => signalled.Set();
        source.Start();

        FirstWrite("{\"first\":true}");

        Assert.True(signalled.Wait(SignalTimeout), "the first write was never detected");
    }

    /// <summary>
    /// THE case the defect turned on: the destination is never written in place, it is replaced by rename.
    /// A watcher bound to the file path can miss this entirely; watching the directory must not.
    /// </summary>
    [Fact]
    public void A_real_atomic_replace_of_an_existing_snapshot_is_detected()
    {
        FirstWrite("{\"generation\":1}");

        using var signalled = new ManualResetEventSlim(false);
        using var source = new FileSystemSnapshotChangeSource(_path);
        source.Changed += () => signalled.Set();
        source.Start();

        AtomicReplace("{\"generation\":2}");

        Assert.True(signalled.Wait(SignalTimeout), "the atomic replace produced no signal");
    }

    [Fact]
    public void Several_consecutive_atomic_replaces_are_each_detected()
    {
        FirstWrite("{\"generation\":0}");

        var signals = 0;
        using var advanced = new ManualResetEventSlim(false);
        using var source = new FileSystemSnapshotChangeSource(_path);
        source.Changed += () =>
        {
            if (Interlocked.Increment(ref signals) >= 3)
            {
                advanced.Set();
            }
        };
        source.Start();

        for (var generation = 1; generation <= 5; generation++)
        {
            AtomicReplace($"{{\"generation\":{generation}}}");
        }

        // At least three signals across five commits — the source over-reports by design; coalescing is
        // the pump's job, never the source's.
        Assert.True(advanced.Wait(SignalTimeout), $"only {signals} signals arrived for 5 commits");
    }

    /// <summary>
    /// The name filter, driven through the REAL handlers with the argument shapes Windows produces. This
    /// is deterministic where a "write noise and hope nothing arrives within N seconds" test is not: a
    /// silence budget cannot tell "correctly ignored" apart from "not delivered yet".
    /// </summary>
    [Theory]
    [InlineData(WatcherChangeTypes.Created, "unrelated.txt", false)]
    [InlineData(WatcherChangeTypes.Changed, "servers.json", false)]
    [InlineData(WatcherChangeTypes.Deleted, "widget-state.a1b2.tmp", false)]
    [InlineData(WatcherChangeTypes.Changed, "widget-state.json.bak", false)]
    [InlineData(WatcherChangeTypes.Created, "widget-state.json", true)]
    [InlineData(WatcherChangeTypes.Changed, "widget-state.json", true)]
    [InlineData(WatcherChangeTypes.Deleted, "widget-state.json", true)]
    [InlineData(WatcherChangeTypes.Changed, "WIDGET-STATE.JSON", true)]
    public void Only_the_snapshot_file_name_signals(WatcherChangeTypes change, string name, bool expected)
    {
        var signals = 0;
        using var source = new FileSystemSnapshotChangeSource(_path);
        source.Changed += () => Interlocked.Increment(ref signals);

        source.SimulateFileEventForTesting(change, name);

        Assert.Equal(expected ? 1 : 0, signals);
    }

    /// <summary>
    /// A commit renames the temp ONTO the destination and the destination to the backup, so the snapshot
    /// appears as the new name in one event and as the OLD name in the other. Both must signal; a rename
    /// touching neither must not.
    /// </summary>
    [Theory]
    [InlineData("widget-state.json", "widget-state.a1b2.tmp", true)]
    [InlineData("widget-state.json.bak", "widget-state.json", true)]
    [InlineData("servers.json", "servers.old.json", false)]
    public void A_rename_signals_when_either_side_is_the_snapshot(string name, string oldName, bool expected)
    {
        var signals = 0;
        using var source = new FileSystemSnapshotChangeSource(_path);
        source.Changed += () => Interlocked.Increment(ref signals);

        source.SimulateRenameForTesting(name, oldName);

        Assert.Equal(expected ? 1 : 0, signals);
    }

    /// <summary>
    /// A stopped source signals nothing, proved by ORDER rather than by a silence budget: every signal
    /// re-reads the file and records what it saw, and generation 2 — committed while stopped — must never
    /// appear, even though the test goes on to observe generation 3 through the restarted watch. A late
    /// event from the stopped period would have been recorded before that one.
    /// </summary>
    [Fact]
    public void A_stopped_source_signals_nothing_and_a_restart_resumes()
    {
        FirstWrite("{\"generation\":1}");

        var seen = new ConcurrentBag<string>();
        using var sawThird = new ManualResetEventSlim(false);
        using var source = new FileSystemSnapshotChangeSource(_path);
        source.Changed += () =>
        {
            var content = ReadSnapshot();
            seen.Add(content);
            if (content.Contains("\"generation\":3", StringComparison.Ordinal))
            {
                sawThird.Set();
            }
        };

        source.Start();
        source.Stop();
        AtomicReplace("{\"generation\":2}"); // committed while stopped: must stay invisible

        source.Start();
        AtomicReplace("{\"generation\":3}");

        Assert.True(sawThird.Wait(SignalTimeout), "the restarted source never signalled");
        Assert.DoesNotContain(seen, content => content.Contains("\"generation\":2", StringComparison.Ordinal));
    }

    /// <summary>
    /// The lost-event path: <see cref="FileSystemWatcher.Error"/> (an internal-buffer overflow) means
    /// events were dropped, so the source must report itself no longer watching AND signal one
    /// unconditional re-read — that pair is what lets the pump's backstop re-establish the watch.
    /// </summary>
    [Fact]
    public void A_watcher_error_marks_the_source_faulted_and_signals_a_reread()
    {
        var signals = 0;
        using var source = new FileSystemSnapshotChangeSource(_path);
        source.Changed += () => Interlocked.Increment(ref signals);
        source.Start();
        Assert.True(source.IsWatching);

        source.SimulateWatcherErrorForTesting(new InternalBufferOverflowException("buffer overflow"));

        Assert.Equal(1, signals);
        Assert.False(source.IsWatching);
    }

    /// <summary>A faulted watch is replaced by the next Start, and really delivers again afterwards.</summary>
    [Fact]
    public void A_faulted_watch_is_reestablished_by_a_later_start()
    {
        FirstWrite("{\"generation\":1}");

        using var signalled = new ManualResetEventSlim(false);
        using var source = new FileSystemSnapshotChangeSource(_path);
        source.Start();
        source.SimulateWatcherErrorForTesting(new InternalBufferOverflowException("buffer overflow"));
        Assert.False(source.IsWatching);

        source.Changed += () => signalled.Set();
        source.Start(); // what the pump's backstop does on its next tick
        Assert.True(source.IsWatching);

        AtomicReplace("{\"generation\":2}");

        Assert.True(signalled.Wait(SignalTimeout), "the re-established watch delivered nothing");
    }

    /// <summary>
    /// Reads the destination the way the provider does. The commit is a rename, so the file can be
    /// momentarily unavailable; that is a retry, not a failure.
    /// </summary>
    private string ReadSnapshot()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
                // The destination is being replaced right now; try again.
            }
        }

        return string.Empty;
    }

    [Fact]
    public void A_handler_that_throws_cannot_escape_the_watcher_callback()
    {
        FirstWrite("{\"generation\":1}");

        using var recovered = new ManualResetEventSlim(false);
        using var source = new FileSystemSnapshotChangeSource(_path);
        var first = true;
        source.Changed += () =>
        {
            if (first)
            {
                first = false;
                throw new InvalidOperationException("handler exploded");
            }

            recovered.Set();
        };
        source.Start();

        AtomicReplace("{\"generation\":2}");
        AtomicReplace("{\"generation\":3}");

        // The process is still standing and the source keeps delivering — an escaping exception on a
        // FileSystemWatcher threadpool callback would tear the COM server down (§16).
        Assert.True(recovered.Wait(SignalTimeout), "the source stopped working after a handler threw");
    }

    /// <summary>
    /// EVIDENCE, not just behavior: records the raw event shapes Windows actually produces for the
    /// writer's commit, so the "watch the directory, accept Created/Changed/Deleted/Renamed on either
    /// name" decision rests on observation rather than theory. Fails if the destination file name never
    /// appears in any event, which is exactly the assumption the source depends on.
    /// </summary>
    [Fact]
    public void Raw_event_shapes_of_a_real_atomic_replace()
    {
        FirstWrite("{\"generation\":1}");

        var events = new List<string>();
        using var sawDestination = new ManualResetEventSlim(false);
        using var watcher = new FileSystemWatcher(_dir)
        {
            Filter = "*",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024
        };

        void Record(string line)
        {
            lock (events)
            {
                events.Add(line);
            }

            if (line.Contains(WidgetStateLocation.FileName, StringComparison.OrdinalIgnoreCase))
            {
                sawDestination.Set();
            }
        }

        watcher.Created += (_, e) => Record($"Created  Name={e.Name}");
        watcher.Changed += (_, e) => Record($"Changed  Name={e.Name}");
        watcher.Deleted += (_, e) => Record($"Deleted  Name={e.Name}");
        watcher.Renamed += (_, e) => Record($"Renamed  OldName={e.OldName} -> Name={e.Name}");
        watcher.EnableRaisingEvents = true;

        AtomicReplace("{\"generation\":2}");

        Assert.True(
            sawDestination.Wait(SignalTimeout),
            "no event named widget-state.json for a real temp+File.Replace commit");

        lock (events)
        {
            _output.WriteLine($"--- raw FileSystemWatcher events for one temp + File.Replace commit ({events.Count}) ---");
            foreach (var line in events)
            {
                _output.WriteLine(line);
            }
        }
    }
}
