using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

public sealed class AtomicWidgetStateWriterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory;
    private readonly string _path;

    public AtomicWidgetStateWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sm-widget-tests", Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_directory, "widget-state.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private AtomicWidgetStateWriter NewWriter() =>
        new(new WidgetStateOptions { SnapshotPath = _path }, NullLogger<AtomicWidgetStateWriter>.Instance);

    private static WidgetStateSnapshot Snapshot(WidgetHealth overall, string name) => new()
    {
        SchemaVersion = WidgetSchema.CurrentVersion,
        GeneratedAtUtc = Now,
        OverallHealth = overall,
        Servers = new[]
        {
            new WidgetServerState
            {
                Id = Guid.NewGuid(),
                DisplayName = name,
                Health = overall,
                CpuUsagePercent = 10,
                MemoryUsagePercent = 20,
                DiskUsagePercent = 30,
                LastUpdatedUtc = Now
            }
        }
    };

    private IReadOnlyList<string> TempFiles() =>
        Directory.Exists(_directory)
            ? Directory.GetFiles(_directory, "*.tmp")
            : Array.Empty<string>();

    [Fact]
    public async Task Write_creates_a_valid_readable_file()
    {
        await NewWriter().WriteAsync(Snapshot(WidgetHealth.Healthy, "Home"), CancellationToken.None);

        Assert.True(File.Exists(_path));
        var restored = WidgetStateSerializer.TryDeserialize(await File.ReadAllBytesAsync(_path));
        Assert.NotNull(restored);
        Assert.True(WidgetStateValidator.Validate(restored, Now).IsValid);
        Assert.Equal("Home", Assert.Single(restored!.Servers).DisplayName);
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Write_creates_missing_directory()
    {
        Assert.False(Directory.Exists(_directory));
        await NewWriter().WriteAsync(Snapshot(WidgetHealth.Warning, "New"), CancellationToken.None);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public async Task Second_write_atomically_replaces_the_first()
    {
        var writer = NewWriter();
        await writer.WriteAsync(Snapshot(WidgetHealth.Healthy, "First"), CancellationToken.None);
        await writer.WriteAsync(Snapshot(WidgetHealth.Critical, "Second"), CancellationToken.None);

        var restored = WidgetStateSerializer.TryDeserialize(await File.ReadAllBytesAsync(_path));
        Assert.Equal(WidgetHealth.Critical, restored!.OverallHealth);
        Assert.Equal("Second", Assert.Single(restored.Servers).DisplayName);
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Cancelled_write_preserves_last_known_good_and_leaves_no_temp()
    {
        var writer = NewWriter();
        await writer.WriteAsync(Snapshot(WidgetHealth.Healthy, "Good"), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.WriteAsync(Snapshot(WidgetHealth.Offline, "ShouldNotLand"), cts.Token));

        // The previous last-known-good is intact — the failed write never touched the destination.
        var restored = WidgetStateSerializer.TryDeserialize(await File.ReadAllBytesAsync(_path));
        Assert.Equal("Good", Assert.Single(restored!.Servers).DisplayName);
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Failure_after_temp_created_preserves_last_good_and_cleans_temp()
    {
        var writer = NewWriter();
        await writer.WriteAsync(Snapshot(WidgetHealth.Healthy, "Good"), CancellationToken.None);

        // Lock the destination (deny replace) so File.Move fails AFTER the temp file was written — the
        // failure path that a pre-cancelled token never reaches. On Windows this blocks MoveFileEx.
        using (var hold = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // The failure occurs at the COMMIT (File.Move replace) on the locked destination — after the
            // temp was written — so it must be the OS file-replace failure family, not some unrelated
            // pre-temp error. This proves the named post-temp path was exercised.
            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => writer.WriteAsync(Snapshot(WidgetHealth.Offline, "ShouldNotLand"), CancellationToken.None));
            Assert.True(
                exception is IOException or UnauthorizedAccessException,
                $"expected a file-replace failure, got {exception.GetType().Name}");
        }

        // Last-known-good intact and the temp file cleaned up despite the mid-write failure.
        var restored = WidgetStateSerializer.TryDeserialize(await File.ReadAllBytesAsync(_path));
        Assert.Equal("Good", Assert.Single(restored!.Servers).DisplayName);
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Repeated_writes_never_accumulate_temp_files()
    {
        var writer = NewWriter();
        for (var i = 0; i < 20; i++)
        {
            await writer.WriteAsync(Snapshot(WidgetHealth.Healthy, $"n{i}"), CancellationToken.None);
        }

        Assert.True(File.Exists(_path));
        Assert.Empty(TempFiles());
    }
}
