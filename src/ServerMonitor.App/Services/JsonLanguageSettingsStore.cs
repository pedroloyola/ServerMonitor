using System.Text.Json;
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
/// Not part of a backup and not behind the restore write gate: like the override it describes, it is a device-local UI
/// preference (a restore never reads or replaces it). v1.1.x does not know the file and ignores it.
/// </para>
/// </summary>
public sealed class JsonLanguageSettingsStore : ILanguageSettingsStore
{
    internal const int MaxFileBytes = 4 * 1024;

    internal const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
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

                return Parse(File.ReadAllBytes(_storageOptions.FilePath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
                WriteAtomically(Render(record));
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    "Language settings could not be saved ({Type}); the language is left unchanged.",
                    exception.GetType().Name);
                return false;
            }
        }
    }

    /// <summary>The file bytes a save writes for <paramref name="record"/>.</summary>
    internal static byte[] Render(LanguageProvenance record) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new LanguageSettingsDocument
            {
                SchemaVersion = SchemaVersion,
                FollowSystem = record.FollowSystem,
                AutoOverride = record.FollowSystem ? record.AutoOverride ?? string.Empty : null
            },
            SerializerOptions);

    /// <summary>The record <paramref name="bytes"/> hold (null = no file). Strict: anything unexpected is Unreadable.</summary>
    internal static LanguageRecordRead Parse(byte[]? bytes)
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

        if (document is not { SchemaVersion: SchemaVersion, FollowSystem: { } followSystem })
        {
            return LanguageRecordRead.Unreadable;
        }

        if (!followSystem)
        {
            return string.IsNullOrEmpty(document.AutoOverride)
                ? LanguageRecordRead.Of(LanguageProvenance.Explicit)
                : LanguageRecordRead.Unreadable;
        }

        return document.AutoOverride is { } written && (written.Length == 0 || SupportedCultures.IsSupported(written))
            ? LanguageRecordRead.Of(LanguageProvenance.System(written))
            : LanguageRecordRead.Unreadable;
    }

    private void WriteAtomically(byte[] content)
    {
        var directory = Path.GetDirectoryName(_storageOptions.FilePath)
            ?? throw new InvalidOperationException("The language settings path has no directory.");
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

        public bool? FollowSystem { get; init; }

        public string? AutoOverride { get; init; }
    }
}
