using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Services;

/// <summary>
/// THEME-1: the saved theme choice, in the style of <see cref="JsonLanguageSettingsStore"/> - one small non-sensitive
/// document, a size cap, atomic replace and a schema version. The value is the enum NAME (<c>System</c>, <c>Light</c>,
/// <c>Dark</c>): locale-free, never localized text and never a number.
/// <para>
/// Reading is strict and never throws: exact camelCase names, numbers only as JSON numbers, no unknown properties, the
/// exact schema version and an exact (ordinal) value. Anything else - missing, malformed, oversized, unknown value,
/// unreadable path - reads as <see cref="AppThemePreference.System"/>, the fail-safe that follows Windows.
/// </para>
/// <para>
/// Not part of a backup and not behind the restore write gate: like <c>language-settings.json</c> it is a device-local
/// UI preference (a restore never reads or replaces it). Builds before THEME-1 do not know the file and ignore it.
/// </para>
/// </summary>
public sealed class JsonThemePreferenceStore : IThemePreferenceStore
{
    internal const int MaxFileBytes = 4 * 1024;

    internal const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly ThemeSettingsStorageOptions _storageOptions;
    private readonly ILogger<JsonThemePreferenceStore> _logger;
    private readonly object _sync = new();
    private readonly Action<string>? _beforeReplace;

    public JsonThemePreferenceStore(
        ThemeSettingsStorageOptions storageOptions,
        ILogger<JsonThemePreferenceStore> logger)
        : this(storageOptions, logger, beforeReplace: null)
    {
    }

    /// <summary>
    /// THEME-1 r2 (Atlas P2-B) test seam: <paramref name="beforeReplace"/> runs inside the lock, with the complete
    /// temporary file on disk and the old record not yet replaced - the exact point an interrupted save stops at.
    /// </summary>
    internal JsonThemePreferenceStore(
        ThemeSettingsStorageOptions storageOptions,
        ILogger<JsonThemePreferenceStore> logger,
        Action<string>? beforeReplace)
    {
        _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _beforeReplace = beforeReplace;
    }

    public AppThemePreference Read()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_storageOptions.FilePath))
                {
                    return AppThemePreference.System;
                }

                if (new FileInfo(_storageOptions.FilePath).Length > MaxFileBytes)
                {
                    _logger.LogWarning("Theme settings exceed {MaxBytes} bytes; the theme follows Windows.", MaxFileBytes);
                    return AppThemePreference.System;
                }

                if (TryParse(File.ReadAllBytes(_storageOptions.FilePath)) is { } preference)
                {
                    return preference;
                }

                _logger.LogWarning("Theme settings are not understood; the theme follows Windows.");
                return AppThemePreference.System;
            }
            catch (Exception exception) when (JsonLanguageSettingsStore.IsFileSystemFailure(exception))
            {
                _logger.LogWarning(
                    "Theme settings could not be read ({Type}); the theme follows Windows.",
                    exception.GetType().Name);
                return AppThemePreference.System;
            }
        }
    }

    public bool TryWrite(AppThemePreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            _logger.LogWarning("Theme value {Value} is not a known preference; nothing is saved.", (int)preference);
            return false;
        }

        lock (_sync)
        {
            try
            {
                WriteAtomically(Render(preference));
                return true;
            }
            catch (Exception exception) when (JsonLanguageSettingsStore.IsFileSystemFailure(exception))
            {
                _logger.LogWarning(
                    "Theme settings could not be saved ({Type}); the theme applies to this session only.",
                    exception.GetType().Name);
                return false;
            }
        }
    }

    /// <summary>The file bytes a save of <paramref name="preference"/> writes.</summary>
    internal static byte[] Render(AppThemePreference preference) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new ThemeSettingsDocument { SchemaVersion = SchemaVersion, Theme = Name(preference) },
            SerializerOptions);

    /// <summary>The preference <paramref name="bytes"/> hold (null = no file), or System when anything is not exactly understood.</summary>
    internal static AppThemePreference Parse(byte[]? bytes) => TryParse(bytes) ?? AppThemePreference.System;

    private static AppThemePreference? TryParse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length > MaxFileBytes)
        {
            return null;
        }

        ThemeSettingsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ThemeSettingsDocument>(bytes, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is not { SchemaVersion: SchemaVersion, Theme: { } theme })
        {
            return null;
        }

        // Exact and ordinal - never Enum.TryParse, which would also accept "1", "dark" or " Dark".
        return theme switch
        {
            nameof(AppThemePreference.System) => AppThemePreference.System,
            nameof(AppThemePreference.Light) => AppThemePreference.Light,
            nameof(AppThemePreference.Dark) => AppThemePreference.Dark,
            _ => null
        };
    }

    private static string Name(AppThemePreference preference) => preference switch
    {
        AppThemePreference.System => nameof(AppThemePreference.System),
        AppThemePreference.Light => nameof(AppThemePreference.Light),
        AppThemePreference.Dark => nameof(AppThemePreference.Dark),
        _ => throw new ArgumentOutOfRangeException(nameof(preference))
    };

    private void WriteAtomically(byte[] content)
    {
        var directory = Path.GetDirectoryName(_storageOptions.FilePath)
            ?? throw new IOException("The theme settings path has no directory.");
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

            _beforeReplace?.Invoke(temporaryFile);
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

    private sealed record ThemeSettingsDocument
    {
        public int? SchemaVersion { get; init; }

        public string? Theme { get; init; }
    }
}
