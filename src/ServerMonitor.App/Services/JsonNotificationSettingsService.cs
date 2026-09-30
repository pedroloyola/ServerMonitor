using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.Services;

/// <summary>
/// Small independent store for the global health-notification preference. It persists only one
/// non-sensitive boolean and commits in-memory state only after the atomic file replacement.
/// Missing, malformed and oversized files use the product default: notifications enabled.
/// </summary>
public sealed class JsonNotificationSettingsService : INotificationSettingsService
{
    internal const int MaxFileBytes = 4 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly NotificationSettingsStorageOptions _storageOptions;
    private readonly ILogger<JsonNotificationSettingsService> _logger;
    private readonly IConfigurationWriteGate _writeGate;
    private readonly object _sync = new();
    private bool _notificationsEnabled;

    public JsonNotificationSettingsService(
        NotificationSettingsStorageOptions storageOptions,
        ILogger<JsonNotificationSettingsService> logger,
        IConfigurationWriteGate writeGate)
    {
        _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _writeGate = writeGate ?? throw new ArgumentNullException(nameof(writeGate));
        _notificationsEnabled = LoadOrDefault();
    }

    public event EventHandler? NotificationsEnabledChanged;

    public bool NotificationsEnabled
    {
        get
        {
            lock (_sync)
            {
                return _notificationsEnabled;
            }
        }
    }

    public void SetNotificationsEnabled(bool enabled)
    {
        EventHandler? changed;
        lock (_sync)
        {
            if (_notificationsEnabled == enabled)
            {
                return;
            }

            // M14.6 V5: refused while a restore holds the configuration gate or after it committed.
            using var lease = _writeGate.EnterWrite();
            Save(enabled);
            _notificationsEnabled = enabled;
            changed = NotificationsEnabledChanged;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The file bytes a save writes for <paramref name="enabled"/> (restore renders with it).</summary>
    internal static byte[] Render(bool enabled) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new NotificationSettingsDocument { NotificationsEnabled = enabled },
            SerializerOptions);

    /// <summary>The value the service would load from <paramref name="bytes"/> (null = no file).</summary>
    internal static bool Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length > MaxFileBytes)
        {
            return true;
        }

        try
        {
            return JsonSerializer.Deserialize<NotificationSettingsDocument>(bytes, SerializerOptions)?.NotificationsEnabled ?? true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// Restore-only replace of the file (<paramref name="content"/> null deletes it) under this service's lock;
    /// the in-memory value is reloaded from the file without raising the change event (the app relaunches).
    /// </summary>
    internal void ReplaceForRestore(RestoreWriteToken token, byte[]? content)
    {
        _writeGate.EnsureHeldBy(token);
        lock (_sync)
        {
            if (content is null)
            {
                if (File.Exists(_storageOptions.FilePath))
                {
                    File.Delete(_storageOptions.FilePath);
                }
            }
            else
            {
                WriteAtomically(content);
            }

            _notificationsEnabled = LoadOrDefault();
        }
    }

    private bool LoadOrDefault()
    {
        try
        {
            if (!File.Exists(_storageOptions.FilePath))
            {
                return true;
            }

            var info = new FileInfo(_storageOptions.FilePath);
            if (info.Length > MaxFileBytes)
            {
                _logger.LogWarning(
                    "Notification settings exceed {MaxBytes} bytes; using defaults.",
                    MaxFileBytes);
                return true;
            }

            using var stream = new FileStream(
                _storageOptions.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            var document = JsonSerializer.Deserialize<NotificationSettingsDocument>(
                stream,
                SerializerOptions);
            return document?.NotificationsEnabled ?? true;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Notification settings could not be read ({Type}); using defaults.",
                exception.GetType().Name);
            return true;
        }
    }

    private void Save(bool enabled) => WriteAtomically(Render(enabled));

    private void WriteAtomically(byte[] content)
    {
        var directory = Path.GetDirectoryName(_storageOptions.FilePath)
            ?? throw new InvalidOperationException("The notification settings path has no directory.");
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

    private sealed record NotificationSettingsDocument
    {
        public bool? NotificationsEnabled { get; init; }
    }
}
