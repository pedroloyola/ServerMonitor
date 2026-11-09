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

    private JsonThemePreferenceStore Store() =>
        new(new ThemeSettingsStorageOptions { FilePath = FilePath }, NullLogger<JsonThemePreferenceStore>.Instance);
}
