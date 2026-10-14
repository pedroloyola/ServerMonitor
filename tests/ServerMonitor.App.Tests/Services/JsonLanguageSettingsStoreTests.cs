using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0D: <c>language-settings.json</c> on a per-test temp folder (never the real <c>%LOCALAPPDATA%\ServerMonitor</c>).
/// Strict reading - anything not exactly understood is Unreadable, which the policy treats as "no proof" - and an atomic
/// write that reports failure instead of throwing.
/// </summary>
public sealed class JsonLanguageSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sm-language-settings-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "language-settings.json");

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
    public void NoFile_ReadsAsAbsent() =>
        Assert.Equal(LanguageRecordRead.Absent, Store().Read());

    [Theory]
    [InlineData(true, "en-US")]
    [InlineData(true, "pt-PT")]
    [InlineData(true, "")]
    [InlineData(false, null)]
    public void AWrittenRecord_ReadsBackExactly_AndCreatesTheFolder(bool followSystem, string? autoOverride)
    {
        var record = followSystem ? LanguageProvenance.System(autoOverride!) : LanguageProvenance.Explicit;

        Assert.True(Store().TryWrite(record));

        Assert.Equal(LanguageRecordRead.Of(record), Store().Read());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void TheFile_CarriesTheSchemaVersion_AndNoLanguageOtherThanTheAppsOwnOverride()
    {
        Store().TryWrite(LanguageProvenance.System("en-US"));

        using var document = JsonDocument.Parse(File.ReadAllBytes(FilePath));
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal(["schemaVersion", "followSystem", "autoOverride"], names);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"followSystem":true,"autoOverride":"en-US"}""")] // no schema version
    [InlineData("""{"schemaVersion":2,"followSystem":true,"autoOverride":"en-US"}""")] // a newer schema
    [InlineData("""{"schemaVersion":1,"autoOverride":"en-US"}""")] // no followSystem
    [InlineData("""{"schemaVersion":1,"followSystem":true}""")] // follows, but which override?
    [InlineData("""{"schemaVersion":1,"followSystem":true,"autoOverride":"fr-FR"}""")] // not a supported language
    [InlineData("""{"schemaVersion":1,"followSystem":false,"autoOverride":"en-US"}""")] // explicit cannot carry one
    public void AnythingNotExactlyUnderstood_IsUnreadable(string content)
    {
        WriteRaw(content);

        Assert.Equal(LanguageRecordRead.Unreadable, Store().Read());
    }

    /// <summary>
    /// The positive control for the theory above (K10): the same hand-written shape with the CURRENT schema reads as
    /// valid, so each Unreadable case fails for its own reason - not because of how the test wrote the file.
    /// </summary>
    [Theory]
    [InlineData("""{"schemaVersion":1,"followSystem":true,"autoOverride":"en-US"}""", true, "en-US")]
    [InlineData("""{"schemaVersion":1,"followSystem":true,"autoOverride":""}""", true, "")]
    [InlineData("""{"schemaVersion":1,"followSystem":false}""", false, null)]
    [InlineData("""{"schemaVersion":1,"followSystem":false,"autoOverride":null}""", false, null)]
    public void AHandWrittenRecordOfTheCurrentSchema_IsRead(string content, bool followSystem, string? autoOverride)
    {
        WriteRaw(content);

        var expected = followSystem ? LanguageProvenance.System(autoOverride!) : LanguageProvenance.Explicit;
        Assert.Equal(LanguageRecordRead.Of(expected), Store().Read());
    }

    /// <summary>The app never writes a BOM; a file saved with one (e.g. by an editor) proves nothing and is Unreadable.</summary>
    [Fact]
    public void AFileWithAByteOrderMark_IsUnreadable()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{"schemaVersion":1,"followSystem":false}""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(LanguageRecordRead.Unreadable, Store().Read());
    }

    [Fact]
    public void AnOversizedFile_IsUnreadable()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{"schemaVersion":1,"followSystem":false}""" + new string(' ', JsonLanguageSettingsStore.MaxFileBytes));

        Assert.Equal(LanguageRecordRead.Unreadable, Store().Read());
    }

    [Fact]
    public void Parse_IsStrict_AndTheRenderRoundTrips()
    {
        Assert.Equal(LanguageRecordRead.Absent, JsonLanguageSettingsStore.Parse(null));
        foreach (var record in new[] { LanguageProvenance.Explicit, LanguageProvenance.System(""), LanguageProvenance.System("pt-PT") })
        {
            Assert.Equal(LanguageRecordRead.Of(record), JsonLanguageSettingsStore.Parse(JsonLanguageSettingsStore.Render(record)));
        }
    }

    /// <summary>A6 at the file level: a write that cannot complete reports false (the service then leaves the language alone).</summary>
    [Fact]
    public void AWriteThatCannotComplete_ReportsFalse_AndLeavesTheOldRecord()
    {
        Assert.True(Store().TryWrite(LanguageProvenance.System("en-US")));
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(Store().TryWrite(LanguageProvenance.Explicit));
        }

        Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.System("en-US")), Store().Read());
    }

    [Fact]
    public void ALockedFile_ReadsAsUnreadable_NeverAsAbsent()
    {
        Assert.True(Store().TryWrite(LanguageProvenance.Explicit));
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(LanguageRecordRead.Unreadable, Store().Read());
        }
    }

    /// <summary>Exactly these bytes (UTF-8, no byte order mark), as the app itself writes them.</summary>
    private void WriteRaw(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(FilePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
    }

    private JsonLanguageSettingsStore Store() =>
        new(new LanguageSettingsStorageOptions { FilePath = FilePath }, NullLogger<JsonLanguageSettingsStore>.Instance);
}
