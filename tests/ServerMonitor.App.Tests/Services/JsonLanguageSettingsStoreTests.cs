using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0D: <c>language-settings.json</c> on a per-test temp folder (never the real <c>%LOCALAPPDATA%\ServerMonitor</c>).
/// Strict reading - anything not exactly understood is Unreadable, which the policy treats as "no proof" - and an atomic
/// write that reports failure instead of throwing. PR #55 fix round 1: schema 2 with the writer identity (Cortex N-1) and
/// a genuinely strict parser (Relay N-1).
/// </summary>
public sealed class JsonLanguageSettingsStoreTests : IDisposable
{
    private const string Me = "package:Test.Store_abc";
    private const string Someone = "unpackaged";

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
    public void TheFile_CarriesTheSchemaVersion_TheWriter_AndNoLanguageOtherThanTheAppsOwnOverride()
    {
        Store().TryWrite(LanguageProvenance.System("en-US"));

        using var document = JsonDocument.Parse(File.ReadAllBytes(FilePath));
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal(["schemaVersion", "writer", "followSystem", "autoOverride"], names);
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Me, document.RootElement.GetProperty("writer").GetString());
    }

    /// <summary>Cortex N-1: a valid record of another identity sharing the folder proves nothing about this one.</summary>
    [Theory]
    [InlineData(true, "en-US")]
    [InlineData(true, "")]
    [InlineData(false, null)]
    public void AValidRecordOfAnotherWriter_ReadsAsOtherWriter(bool followSystem, string? autoOverride)
    {
        var record = followSystem ? LanguageProvenance.System(autoOverride!) : LanguageProvenance.Explicit;
        Assert.True(Store(Someone).TryWrite(record));

        Assert.Equal(LanguageRecordRead.OtherWriter, Store(Me).Read());
        Assert.Equal(LanguageRecordRead.Of(record), Store(Someone).Read());
    }

    /// <summary>Only a well-formed record says who wrote it: a broken one of another writer is still Unreadable.</summary>
    [Fact]
    public void AMalformedRecordOfAnotherWriter_IsUnreadable()
    {
        WriteRaw("""{"schemaVersion":2,"writer":"unpackaged","followSystem":true,"autoOverride":"fr-FR"}""");

        Assert.Equal(LanguageRecordRead.Unreadable, Store(Me).Read());
    }

    [Theory]
    [InlineData("Store.Family_8wekyb3d8bbwe", "package:Store.Family_8wekyb3d8bbwe")]
    [InlineData("", "unpackaged")]
    [InlineData(null, "unpackaged")]
    public void TheWriter_IsThePackageFamily_OrUnpackaged(string? family, string expected)
    {
        Assert.Equal(expected, LanguageSettingsWriter.Resolve(() => family));
        Assert.Equal("unpackaged", LanguageSettingsWriter.Resolve(() => throw new InvalidOperationException("not packaged")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"writer":"package:Test.Store_abc","followSystem":true,"autoOverride":"en-US"}""")] // no schema version
    [InlineData("""{"schemaVersion":3,"writer":"package:Test.Store_abc","followSystem":true,"autoOverride":"en-US"}""")] // a newer schema
    [InlineData("""{"schemaVersion":1,"followSystem":true,"autoOverride":"en-US"}""")] // schema 1 (unreleased builds, no writer)
    [InlineData("""{"schemaVersion":2,"followSystem":true,"autoOverride":"en-US"}""")] // no writer
    [InlineData("""{"schemaVersion":2,"writer":"","followSystem":true,"autoOverride":"en-US"}""")] // empty writer
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","autoOverride":"en-US"}""")] // no followSystem
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":true}""")] // follows, but which override?
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":true,"autoOverride":"fr-FR"}""")] // not a supported language
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":false,"autoOverride":"en-US"}""")] // explicit cannot carry one
    // Relay N-1: genuinely strict - exact names, numbers as numbers, no unknown members.
    [InlineData("""{"SchemaVersion":2,"writer":"package:Test.Store_abc","followSystem":true,"autoOverride":"en-US"}""")]
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","FollowSystem":true,"autoOverride":"en-US"}""")]
    [InlineData("""{"schemaVersion":"2","writer":"package:Test.Store_abc","followSystem":true,"autoOverride":"en-US"}""")]
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":"true","autoOverride":"en-US"}""")]
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":true,"autoOverride":"en-US","extra":1}""")]
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
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":true,"autoOverride":"en-US"}""", true, "en-US")]
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":true,"autoOverride":""}""", true, "")]
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":false}""", false, null)]
    [InlineData("""{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":false,"autoOverride":null}""", false, null)]
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
        File.WriteAllText(FilePath, """{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":false}""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(LanguageRecordRead.Unreadable, Store().Read());
    }

    [Fact]
    public void AnOversizedFile_IsUnreadable()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{"schemaVersion":2,"writer":"package:Test.Store_abc","followSystem":false}""" + new string(' ', JsonLanguageSettingsStore.MaxFileBytes));

        Assert.Equal(LanguageRecordRead.Unreadable, Store().Read());
    }

    [Fact]
    public void Parse_IsStrict_AndTheRenderRoundTrips()
    {
        Assert.Equal(LanguageRecordRead.Absent, JsonLanguageSettingsStore.Parse(null, Me));
        foreach (var record in new[] { LanguageProvenance.Explicit, LanguageProvenance.System(""), LanguageProvenance.System("pt-PT") })
        {
            Assert.Equal(LanguageRecordRead.Of(record), JsonLanguageSettingsStore.Parse(JsonLanguageSettingsStore.Render(record, Me), Me));
            Assert.Equal(LanguageRecordRead.OtherWriter, JsonLanguageSettingsStore.Parse(JsonLanguageSettingsStore.Render(record, Someone), Me));
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

    /// <summary>Relay N-2: the exception scope - environment failures only; programming errors still surface.</summary>
    [Fact]
    public void TheFailureScope_IsTheFileSystemOnes()
    {
        Assert.All(
            new Exception[] { new IOException(), new UnauthorizedAccessException(), new NotSupportedException(), new ArgumentException(),
                new System.Security.SecurityException(), new PathTooLongException(), new DirectoryNotFoundException() },
            exception => Assert.True(JsonLanguageSettingsStore.IsFileSystemFailure(exception), exception.GetType().Name));
        Assert.False(JsonLanguageSettingsStore.IsFileSystemFailure(new NullReferenceException()));
        Assert.False(JsonLanguageSettingsStore.IsFileSystemFailure(new InvalidOperationException()));
    }

    /// <summary>A path the OS cannot use (an embedded NUL) is a false / not-valid read, never an exception.</summary>
    [Fact]
    public void AnUnusablePath_WritesFalse_AndNeverReadsValid_WithoutThrowing()
    {
        var store = new JsonLanguageSettingsStore(
            new LanguageSettingsStorageOptions { FilePath = Path.Combine(_directory, "bad\0name.json"), Writer = Me },
            NullLogger<JsonLanguageSettingsStore>.Instance);

        Assert.False(store.TryWrite(LanguageProvenance.Explicit));
        Assert.NotEqual(LanguageRecordState.Valid, store.Read().State);
    }

    private JsonLanguageSettingsStore Store(string writer = Me) =>
        new(new LanguageSettingsStorageOptions { FilePath = FilePath, Writer = writer }, NullLogger<JsonLanguageSettingsStore>.Instance);
}
