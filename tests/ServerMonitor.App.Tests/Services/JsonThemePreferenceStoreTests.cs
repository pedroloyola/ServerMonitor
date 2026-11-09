using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// THEME-1: <c>theme-settings.json</c> on a per-test temp folder (never the real <c>%LOCALAPPDATA%\ServerMonitor</c>).
/// Strict reading where anything not exactly understood is System (fail safe, never a throw), and an atomic write that
/// reports failure instead of throwing.
/// </summary>
public sealed class JsonThemePreferenceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sm-theme-settings-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "theme-settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void NoFile_ReadsAsSystem() =>
        Assert.Equal(AppThemePreference.System, Store().Read());

    [Theory]
    [InlineData(AppThemePreference.System)]
    [InlineData(AppThemePreference.Light)]
    [InlineData(AppThemePreference.Dark)]
    public void EachValue_RoundTrips_AndCreatesTheFolder_WithNoTemporaryLeftBehind(AppThemePreference preference)
    {
        Assert.True(Store().TryWrite(preference));

        Assert.Equal(preference, Store().Read());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void TheFile_IsLocaleFree_TheEnumNameAndTheSchemaVersionOnly()
    {
        Store().TryWrite(AppThemePreference.Dark);

        using var document = JsonDocument.Parse(File.ReadAllBytes(FilePath));
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal(["schemaVersion", "theme"], names);
        Assert.Equal(JsonThemePreferenceStore.SchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Dark", document.RootElement.GetProperty("theme").GetString());
    }

    [Fact]
    public void ALaterWrite_ReplacesTheEarlierOne()
    {
        Store().TryWrite(AppThemePreference.Dark);
        Store().TryWrite(AppThemePreference.Light);

        Assert.Equal(AppThemePreference.Light, Store().Read());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"theme":"Dark"}""")]
    [InlineData("""{"schemaVersion":1,"theme":null}""")]
    [InlineData("""{"schemaVersion":1,"theme":"Blue"}""")]
    [InlineData("""{"schemaVersion":1,"theme":"dark"}""")]
    [InlineData("""{"schemaVersion":1,"theme":"DARK"}""")]
    [InlineData("""{"schemaVersion":1,"theme":" Dark"}""")]
    [InlineData("""{"schemaVersion":1,"theme":"Escuro"}""")]
    [InlineData("""{"schemaVersion":1,"theme":"2"}""")]
    [InlineData("""{"schemaVersion":1,"theme":2}""")]
    [InlineData("""{"schemaVersion":1,"theme":"Dark","extra":true}""")]
    [InlineData("""{"SchemaVersion":1,"Theme":"Dark"}""")]
    [InlineData("""{"schemaVersion":"1","theme":"Dark"}""")]
    [InlineData("""{"schemaVersion":2,"theme":"Dark"}""")]
    [InlineData("""{"schemaVersion":0,"theme":"Dark"}""")]
    public void AnythingNotExactlyUnderstood_ReadsAsSystem_WithoutThrowing(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, content, new UTF8Encoding(false));

        Assert.Equal(AppThemePreference.System, Store().Read());
        Assert.Equal(AppThemePreference.System, JsonThemePreferenceStore.Parse(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void TheValidShape_IsWhatTheRejectionsAreMeasuredAgainst()
    {
        // Anti-vacuity for the rejection theory: the same document, exactly right, does read as Dark.
        Assert.Equal(
            AppThemePreference.Dark,
            JsonThemePreferenceStore.Parse(Encoding.UTF8.GetBytes("""{"schemaVersion":1,"theme":"Dark"}""")));
    }

    [Fact]
    public void AnOversizedFile_ReadsAsSystem_EvenWhenItsPrefixIsValid()
    {
        Directory.CreateDirectory(_directory);
        var padding = new string(' ', JsonThemePreferenceStore.MaxFileBytes);
        File.WriteAllText(FilePath, """{"schemaVersion":1,"theme":"Dark"}""" + padding, new UTF8Encoding(false));

        Assert.Equal(AppThemePreference.System, Store().Read());
    }

    [Fact]
    public void AnUnreadablePath_ReadsAsSystem_AndAWriteReportsFailure_NeverThrows()
    {
        var store = new JsonThemePreferenceStore(
            new ThemeSettingsStorageOptions { FilePath = Path.Combine(_directory, "bad\0name.json") },
            NullLogger<JsonThemePreferenceStore>.Instance);

        Assert.Equal(AppThemePreference.System, store.Read());
        Assert.False(store.TryWrite(AppThemePreference.Dark));
    }

    [Fact]
    public void AWriteThatCannotReplaceTheFile_ReportsFailure_AndKeepsTheOldRecord()
    {
        Store().TryWrite(AppThemePreference.Light);
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(Store().TryWrite(AppThemePreference.Dark));
        }

        Assert.Equal(AppThemePreference.Light, Store().Read());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    // ---- Atomic replace (Atlas P2-B) -------------------------------------------------------------------------------

    [Fact]
    public void ASaveInterruptedBeforeTheReplace_KeepsTheOldRecordWhole_AndLeavesNoPartialFile()
    {
        Store().TryWrite(AppThemePreference.Light);
        var before = File.ReadAllBytes(FilePath);
        string? temporary = null;
        var store = Store(beforeReplace: path =>
        {
            temporary = path;
            // The new record is complete in the temporary file, and the old one is still in place.
            Assert.Equal(JsonThemePreferenceStore.Render(AppThemePreference.Dark), File.ReadAllBytes(path));
            Assert.Equal(before, File.ReadAllBytes(FilePath));
            throw new IOException("simulated interruption before the replace");
        });

        Assert.False(store.TryWrite(AppThemePreference.Dark));

        Assert.NotNull(temporary); // the save really reached the replace point
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Equal(AppThemePreference.Light, Store().Read());
        Assert.False(File.Exists(temporary));
        Assert.Equal(["theme-settings.json"], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public void AReadDuringASave_WaitsForIt_AndSeesTheNewRecord()
    {
        // Read and TryWrite share one lock. The writer is held at the replace point by a gate: a Read started
        // meanwhile must not complete until the save does, and then sees the new record. The short wait below can
        // only make a broken lock harder to catch, never fail a correct one.
        // r3 (Atlas): the gate only opens when the test opens it. If the writer's wait ever times out (a runner
        // stalled for minutes) that is recorded and reported as such, never mistaken for a lock failure.
        Store().TryWrite(AppThemePreference.Light);
        var gateTimeout = TimeSpan.FromMinutes(5);
        using var atReplace = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var gateExpired = false;
        var store = Store(beforeReplace: _ =>
        {
            atReplace.Set();
            gateExpired = !release.Wait(gateTimeout);
        });

        Task<bool> write;
        Task<AppThemePreference> read;
        bool completedWhileHeld;
        try
        {
            write = Task.Run(() => store.TryWrite(AppThemePreference.Dark));
            Assert.True(atReplace.Wait(gateTimeout), "The writer never reached the replace point.");
            read = Task.Run(store.Read);
            completedWhileHeld = read.Wait(TimeSpan.FromMilliseconds(200));
        }
        finally
        {
            release.Set(); // whatever happened above, the writer is never left blocked
        }

        Assert.True(write.Wait(gateTimeout), "The save did not finish after the gate was released.");
        Assert.True(read.Wait(gateTimeout), "The read did not finish after the save.");
        Assert.False(gateExpired, "The gate expired before the test released it (runner stall); the result says nothing about the lock.");
        Assert.True(write.Result, "The save reported failure.");
        Assert.False(completedWhileHeld, "A Read completed while a save held the lock.");
        Assert.Equal(AppThemePreference.Dark, read.Result);
    }

    [Fact]
    public void AnUndefinedValue_IsNeverWritten()
    {
        Assert.False(Store().TryWrite((AppThemePreference)7));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void TheInMemoryStore_FollowsTheSameContract()
    {
        var store = new InMemoryThemePreferenceStore();
        Assert.Equal(AppThemePreference.System, store.Read());
        Assert.True(store.TryWrite(AppThemePreference.Dark));
        Assert.Equal(AppThemePreference.Dark, store.Read());
        Assert.False(store.TryWrite((AppThemePreference)7));
        Assert.Equal(AppThemePreference.Dark, store.Read());
        Assert.Equal(AppThemePreference.System, new InMemoryThemePreferenceStore((AppThemePreference)7).Read());
    }

    private JsonThemePreferenceStore Store(Action<string>? beforeReplace = null) =>
        new(new ThemeSettingsStorageOptions { FilePath = FilePath }, NullLogger<JsonThemePreferenceStore>.Instance, beforeReplace);
}
