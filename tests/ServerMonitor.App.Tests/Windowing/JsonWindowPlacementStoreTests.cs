using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

public sealed class JsonWindowPlacementStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;

    public JsonWindowPlacementStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sm-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "window-placement.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private JsonWindowPlacementStore CreateStore() =>
        new(new WindowPlacementStorageOptions { FilePath = _filePath }, NullLogger<JsonWindowPlacementStore>.Instance);

    [Fact]
    public void MissingFile_ReturnsDefault()
    {
        var settings = CreateStore().Load();

        Assert.Equal(WindowMode.Standard, settings.Mode);
        Assert.Null(settings.StandardBounds);
        Assert.Null(settings.CompactBounds);
        Assert.False(settings.CompactAlwaysOnTop);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var store = CreateStore();
        var original = new WindowPlacementSettings
        {
            Mode = WindowMode.Compact,
            StandardBounds = new WindowBounds(10, 20, 780, 760),
            StandardDpiScalePercent = 150,
            CompactBounds = new WindowBounds(-30, 40, 348, 420),
            CompactDpiScalePercent = 100,
            CompactAlwaysOnTop = true
        };

        store.Save(original);
        var loaded = CreateStore().Load();

        Assert.Equal(original, loaded);
    }

    [Fact]
    public void MalformedJson_ReturnsDefault()
    {
        File.WriteAllText(_filePath, "{ this is not valid json");

        var settings = CreateStore().Load();

        Assert.Equal(WindowPlacementSettings.Default, settings);
    }

    [Fact]
    public void OversizedFile_ReturnsDefault()
    {
        File.WriteAllText(_filePath, new string(' ', JsonWindowPlacementStore.MaxFileBytes + 1));

        var settings = CreateStore().Load();

        Assert.Equal(WindowPlacementSettings.Default, settings);
    }

    [Fact]
    public void InvalidModeValue_SanitizesToStandard()
    {
        File.WriteAllText(_filePath, """{ "mode": 99 }""");

        var settings = CreateStore().Load();

        Assert.Equal(WindowMode.Standard, settings.Mode);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(5000)]
    [InlineData(0)]
    public void InvalidDpi_SanitizesToDefault(int dpi)
    {
        File.WriteAllText(_filePath, $$"""{ "mode": 0, "standardDpiScalePercent": {{dpi}} }""");

        var settings = CreateStore().Load();

        Assert.Equal(WindowPlacementSettings.DefaultDpiScalePercent, settings.StandardDpiScalePercent);
    }

    [Fact]
    public void AbsurdBounds_SanitizeToNull()
    {
        File.WriteAllText(
            _filePath,
            """{ "mode": 1, "compactBounds": { "x": 0, "y": 0, "width": 999999, "height": 5 } }""");

        var settings = CreateStore().Load();

        Assert.Null(settings.CompactBounds);
    }

    [Fact]
    public void PartialBounds_SanitizeToNull()
    {
        File.WriteAllText(
            _filePath,
            """{ "mode": 0, "standardBounds": { "x": 10, "y": 20 } }""");

        var settings = CreateStore().Load();

        Assert.Null(settings.StandardBounds);
    }

    [Fact]
    public void SaveToUnwritablePath_DoesNotThrow()
    {
        // Point the file at the directory itself; File.Move onto a directory fails, and the store
        // must swallow it so a placement write can never crash the app.
        var store = new JsonWindowPlacementStore(
            new WindowPlacementStorageOptions { FilePath = _directory },
            NullLogger<JsonWindowPlacementStore>.Instance);

        var exception = Record.Exception(() => store.Save(WindowPlacementSettings.Default));

        Assert.Null(exception);
    }
}
