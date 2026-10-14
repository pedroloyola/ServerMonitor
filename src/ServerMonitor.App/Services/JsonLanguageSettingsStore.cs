using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Services;

/// <summary>
/// REL.0D: the language provenance file, in the same style as <see cref="JsonNotificationSettingsService"/> (one small
/// non-sensitive document, size cap, atomic replace) plus a schema version. Reading is strict and never throws: a
/// missing file is <see cref="LanguageRecordState.Absent"/>; anything present but not exactly understood (malformed,
/// oversized, another schema version, an override that is not a supported language) is
/// <see cref="LanguageRecordState.Unreadable"/>, which the policy treats as "no proof" - the persisted language is then
/// the user's choice and is never touched.
/// <para>
/// PR #55 fix round 1: schema 2 carries the <c>writer</c> (Cortex N-1). A well-formed record of another app identity reads
/// as <see cref="LanguageRecordState.OtherWriter"/>. Parsing is genuinely strict (Relay N-1): exact camelCase names,
/// numbers only as JSON numbers, no unknown properties. Schema 1 existed only in unreleased builds of this PR.
/// </para>
/// <para>
/// Not part of a backup and not behind the restore write gate: like the override it describes, it is a device-local UI
/// preference (a restore never reads or replaces it). v1.1.x does not know the file and ignores it.
/// </para>
/// </summary>
public sealed class JsonLanguageSettingsStore : ILanguageSettingsStore
{
    internal const int MaxFileBytes = 4 * 1024;

    internal const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly LanguageSettingsStorageOptions _storageOptions;
    private readonly ILogger<JsonLanguageSettingsStore> _logger;
    private readonly object _sync = new();

    public JsonLanguageSettingsStore(
        LanguageSettingsStorageOptions storageOptions,
        ILogger<JsonLanguageSettingsStore> logger)
    {
        _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public LanguageRecordRead Read()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_storageOptions.FilePath))
                {
                    return LanguageRecordRead.Absent;
                }

                if (new FileInfo(_storageOptions.FilePath).Length > MaxFileBytes)
                {
                    _logger.LogWarning("Language settings exceed {MaxBytes} bytes; the language choice is kept as it is.", MaxFileBytes);
                    return LanguageRecordRead.Unreadable;
                }

                return Parse(File.ReadAllBytes(_storageOptions.FilePath), _storageOptions.Writer);
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                _logger.LogWarning(
                    "Language settings could not be read ({Type}); the language choice is kept as it is.",
                    exception.GetType().Name);
                return LanguageRecordRead.Unreadable;
            }
        }
    }

    public bool TryWrite(LanguageProvenance record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            try
            {
                WriteAtomically(Render(record, _storageOptions.Writer));
                return true;
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                _logger.LogWarning(
                    "Language settings could not be saved ({Type}); the language is left unchanged.",
                    exception.GetType().Name);
                return false;
            }
        }
    }

    /// <summary>
    /// Relay N-2: the exception scope of <see cref="Read"/> and <see cref="TryWrite"/> is every failure the .NET path and
    /// file APIs document for a bad, inaccessible or unsupported path or a failing disk (I/O incl. directory/path-too-long,
    /// access denied, unsupported path format, invalid path, code-access security). Those are environment failures: the
    /// contract is "never throws, nothing changes". Anything else (null reference, out of memory, ...) is a programming
    /// error and is deliberately left to surface instead of being turned into a silent "could not save".
    /// </summary>
    internal static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException;

    /// <summary>The file bytes a save by <paramref name="writer"/> writes for <paramref name="record"/>.</summary>
    internal static byte[] Render(LanguageProvenance record, string writer) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new LanguageSettingsDocument
            {
                SchemaVersion = SchemaVersion,
                Writer = writer,
                FollowSystem = record.FollowSystem,
                AutoOverride = record.FollowSystem ? record.AutoOverride ?? string.Empty : null
            },
            SerializerOptions);

    /// <summary>
    /// The record <paramref name="bytes"/> hold for the app identity <paramref name="writer"/> (null = no file). Strict:
    /// anything unexpected is Unreadable; a valid record of another identity is OtherWriter (never proof).
    /// </summary>
    internal static LanguageRecordRead Parse(byte[]? bytes, string writer)
    {
        if (bytes is null)
        {
            return LanguageRecordRead.Absent;
        }

        if (bytes.Length > MaxFileBytes)
        {
            return LanguageRecordRead.Unreadable;
        }

        LanguageSettingsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<LanguageSettingsDocument>(bytes, SerializerOptions);
        }
        catch (JsonException)
        {
            return LanguageRecordRead.Unreadable;
        }

        if (document is not { SchemaVersion: SchemaVersion, FollowSystem: { } followSystem, Writer: { Length: > 0 } recordWriter })
        {
            return LanguageRecordRead.Unreadable;
        }

        LanguageRecordRead record;
        if (!followSystem)
        {
            record = string.IsNullOrEmpty(document.AutoOverride)
                ? LanguageRecordRead.Of(LanguageProvenance.Explicit)
                : LanguageRecordRead.Unreadable;
        }
        else
        {
            record = document.AutoOverride is { } written && (written.Length == 0 || SupportedCultures.IsSupported(written))
                ? LanguageRecordRead.Of(LanguageProvenance.System(written))
                : LanguageRecordRead.Unreadable;
        }

        // Only a well-formed record says anything about its writer; another identity's proves nothing about this one.
        return record.State == LanguageRecordState.Valid && !string.Equals(recordWriter, writer, StringComparison.Ordinal)
            ? LanguageRecordRead.OtherWriter
            : record;
    }

    private void WriteAtomically(byte[] content)
    {
        var directory = Path.GetDirectoryName(_storageOptions.FilePath)
            ?? throw new IOException("The language settings path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryFile = _storageOptions.FilePath + ".tmp";

        try
        {
            using (var stream = new FileStream(
                temporaryFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryFile, _storageOptions.FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private sealed record LanguageSettingsDocument
    {
        public int? SchemaVersion { get; init; }

        public string? Writer { get; init; }

        public bool? FollowSystem { get; init; }

        public string? AutoOverride { get; init; }
    }
}
