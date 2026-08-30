using Microsoft.Extensions.Time.Testing;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Tests;

public sealed class WidgetSnapshotReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir;
    private readonly string _path;
    private readonly FakeTimeProvider _clock = new(Now);

    public WidgetSnapshotReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sm-widgetreader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, WidgetStateLocation.FileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private WidgetSnapshotReader NewReader(long? maxBytes = null) =>
        new(_path, maxBytes, _clock);

    private static WidgetStateSnapshot ValidSnapshot(int servers = 1) => new()
    {
        SchemaVersion = WidgetSchema.CurrentVersion,
        GeneratedAtUtc = Now,
        OverallHealth = WidgetHealth.Healthy,
        Servers = Enumerable.Range(0, servers).Select(_ => new WidgetServerState
        {
            Id = Guid.NewGuid(),
            DisplayName = "Home",
            Health = WidgetHealth.Healthy,
            CpuUsagePercent = 10,
            MemoryUsagePercent = 20,
            DiskUsagePercent = 30,
            LastUpdatedUtc = Now
        }).ToArray()
    };

    private void WriteValid(int servers = 1) =>
        File.WriteAllBytes(_path, WidgetStateSerializer.SerializeToUtf8Bytes(ValidSnapshot(servers)));

    private void WriteRaw(string json) => File.WriteAllText(_path, json);

    [Fact]
    public void Missing_file_is_unavailable()
    {
        var result = NewReader().Read();
        Assert.Equal(WidgetReadStatus.Unavailable, result.Status);
        Assert.Equal(WidgetReadUnavailableReason.Missing, result.Reason);
    }

    [Fact]
    public void Valid_file_is_available()
    {
        WriteValid(3);
        var result = NewReader().Read();
        Assert.True(result.IsAvailable);
        Assert.Equal(3, result.Snapshot!.Servers.Count);
    }

    [Fact]
    public void Oversized_file_is_unavailable_without_full_read()
    {
        WriteValid();
        var tiny = new FileInfo(_path).Length - 1; // cap below the real size
        var result = NewReader(maxBytes: tiny).Read();
        Assert.Equal(WidgetReadUnavailableReason.Oversized, result.Reason);
    }

    [Fact]
    public void Corrupt_json_is_unavailable()
    {
        WriteRaw("{ not valid json ");
        Assert.Equal(WidgetReadUnavailableReason.Corrupt, NewReader().Read().Reason);
    }

    [Fact]
    public void Unsupported_schema_is_unavailable()
    {
        WriteRaw("{\"schemaVersion\":2,\"generatedAtUtc\":\"2026-08-30T12:00:00+00:00\"," +
                 "\"overallHealth\":\"Healthy\",\"servers\":[]}");
        Assert.Equal(WidgetReadUnavailableReason.Invalid, NewReader().Read().Reason);
    }

    [Fact]
    public void Out_of_range_metric_is_unavailable()
    {
        WriteRaw("{\"schemaVersion\":1,\"generatedAtUtc\":\"2026-08-30T12:00:00+00:00\"," +
                 "\"overallHealth\":\"Healthy\",\"servers\":[{\"id\":\"11111111-1111-1111-1111-111111111111\"," +
                 "\"displayName\":\"x\",\"health\":\"Healthy\",\"cpuUsagePercent\":150}]}");
        Assert.Equal(WidgetReadUnavailableReason.Invalid, NewReader().Read().Reason);
    }

    [Fact]
    public void Bad_timestamp_is_unavailable()
    {
        WriteRaw("{\"schemaVersion\":1,\"generatedAtUtc\":\"1999-01-01T00:00:00+00:00\"," +
                 "\"overallHealth\":\"Healthy\",\"servers\":[]}");
        Assert.Equal(WidgetReadUnavailableReason.Invalid, NewReader().Read().Reason);
    }

    [Fact]
    public void Too_many_servers_is_unavailable()
    {
        WriteValid(servers: WidgetSchema.MaxServers + 1);
        Assert.Equal(WidgetReadUnavailableReason.Invalid, NewReader().Read().Reason);
    }

    [Fact]
    public void IO_error_is_unavailable_not_thrown()
    {
        // Point the reader at the directory itself → opening as a file fails; must be neutral, not throw.
        var reader = new WidgetSnapshotReader(_dir, null, _clock);
        var result = reader.Read();
        Assert.Equal(WidgetReadStatus.Unavailable, result.Status);
    }

    [Fact]
    public void Reader_never_blocks_a_concurrent_replace()
    {
        WriteValid();
        // Open the file with the same share mode the reader uses, then prove a rename-replace still works.
        using (var held = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var temp = Path.Combine(_dir, "widget-state.replacement.tmp");
            File.WriteAllBytes(temp, WidgetStateSerializer.SerializeToUtf8Bytes(ValidSnapshot(2)));
            // ReplaceFile semantics tolerate an open share-delete reader (the primitive the writer uses).
            File.Replace(temp, _path, destinationBackupFileName: null); // must not throw despite the open handle
        }

        Assert.True(NewReader().Read().IsAvailable);
    }
}
