using System.Text.Json;
using Microsoft.Extensions.Logging;

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
    private readonly object _sync = new();
    private bool _notificationsEnabled;

    public JsonNotificationSettingsService(
        NotificationSettingsStorageOptions storageOptions,
        ILogger<JsonNotificationSettingsService> logger)
    {
        _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            Save(enabled);
            _notificationsEnabled = enabled;
            changed = NotificationsEnabledChanged;
        }

        changed?.Invoke(this, EventArgs.Empty);
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

    private void Save(bool enabled)
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
                JsonSerializer.Serialize(
                    stream,
                    new NotificationSettingsDocument { NotificationsEnabled = enabled },
                    SerializerOptions);
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
