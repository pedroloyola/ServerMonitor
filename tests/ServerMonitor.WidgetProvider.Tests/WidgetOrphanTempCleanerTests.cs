using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Tests;

public sealed class WidgetOrphanTempCleanerTests : IDisposable
{
    private readonly string _dir;

    public WidgetOrphanTempCleanerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sm-widgetcleaner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void Removes_only_our_temp_pattern()
    {
        var t1 = Touch(WidgetStateLocation.NewTempName());
        var t2 = Touch(WidgetStateLocation.NewTempName());
        var snapshot = Touch(WidgetStateLocation.FileName);
        var unrelated = Touch("history.db");
        var otherTmp = Touch("something-else.tmp");

        var removed = new WidgetOrphanTempCleaner(_dir).Sweep();

        Assert.Equal(2, removed);
        Assert.False(File.Exists(t1));
        Assert.False(File.Exists(t2));
        Assert.True(File.Exists(snapshot));   // never the committed file
        Assert.True(File.Exists(unrelated));  // never unrelated files
        Assert.True(File.Exists(otherTmp));   // never a .tmp that is not ours
    }

    [Fact]
    public void Does_not_recurse_into_subdirectories()
    {
        var sub = Path.Combine(_dir, "nested");
        Directory.CreateDirectory(sub);
        var nestedTemp = Path.Combine(sub, WidgetStateLocation.NewTempName());
        File.WriteAllText(nestedTemp, "x");

        var removed = new WidgetOrphanTempCleaner(_dir).Sweep();

        Assert.Equal(0, removed);
        Assert.True(File.Exists(nestedTemp));
    }

    [Fact]
    public void Missing_directory_returns_zero_and_does_not_throw()
    {
        var gone = Path.Combine(_dir, "does-not-exist");
        Assert.Equal(0, new WidgetOrphanTempCleaner(gone).Sweep());
    }

    [Fact]
    public void Sweep_is_bounded_and_eventually_clears_a_large_directory()
    {
        // More matching temps than the per-sweep examination bound: one sweep must stay bounded, and
        // repeated sweeps must eventually clear them (Atlas/Vigil S2 L-2 hardening).
        const int total = 600;
        for (var i = 0; i < total; i++)
        {
            Touch(WidgetStateLocation.NewTempName());
        }

        var cleaner = new WidgetOrphanTempCleaner(_dir);
        var first = cleaner.Sweep();
        Assert.InRange(first, 1, 512); // bounded per sweep

        // Drain the rest.
        for (var i = 0; i < 5 && Directory.GetFiles(_dir, $"{WidgetStateLocation.TempPrefix}*{WidgetStateLocation.TempExtension}").Length > 0; i++)
        {
            cleaner.Sweep();
        }

        Assert.Empty(Directory.GetFiles(_dir, $"{WidgetStateLocation.TempPrefix}*{WidgetStateLocation.TempExtension}"));
    }
}
