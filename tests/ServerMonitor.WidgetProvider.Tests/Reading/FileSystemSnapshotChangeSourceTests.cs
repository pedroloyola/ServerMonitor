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

    /// <summary>Budget for "must NOT fire" checks. Cannot flake: a wrong name simply never signals.</summary>
    private static readonly TimeSpan SilenceBudget = TimeSpan.FromSeconds(2);

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

    [Fact]
    public void Unrelated_files_in_the_same_directory_never_signal()
    {
        using var signalled = new ManualResetEventSlim(false);
        using var source = new FileSystemSnapshotChangeSource(_path);
        source.Changed += () => signalled.Set();
        source.Start();

        File.WriteAllText(Path.Combine(_dir, "unrelated.txt"), "noise");
        File.WriteAllText(Path.Combine(_dir, "servers.json"), "noise");
        File.Delete(Path.Combine(_dir, "unrelated.txt"));

        Assert.False(signalled.Wait(SilenceBudget), "an unrelated file raised a snapshot signal");
    }

    [Fact]
    public void A_stopped_source_stops_signalling()
    {
        FirstWrite("{\"generation\":1}");

        using var signalled = new ManualResetEventSlim(false);
        using var source = new FileSystemSnapshotChangeSource(_path);
        source.Changed += () => signalled.Set();
        source.Start();
        source.Stop();

        AtomicReplace("{\"generation\":2}");

        Assert.False(signalled.Wait(SilenceBudget), "a stopped source still signalled");
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
