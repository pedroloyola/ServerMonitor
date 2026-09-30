using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.Services;

public sealed record BackgroundSettingsStorageOptions
{
    public required string FilePath { get; init; }

    public static BackgroundSettingsStorageOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new BackgroundSettingsStorageOptions
        {
            FilePath = Path.Combine(localApplicationData, "ServerMonitor", "background-settings.json")
        };
    }
}

/// <summary>
/// Small independent store for the background lifecycle preference and the one-shot notice flag. It
/// mirrors <see cref="JsonNotificationSettingsService"/> exactly: two non-sensitive booleans, an atomic
/// replace, and in-memory state committed only after the file is safely on disk. Missing, malformed and
/// oversized files fall back to the product defaults — background monitoring ON, notice not yet shown.
/// </summary>
public sealed class JsonBackgroundMonitoringSettingsService : IBackgroundMonitoringSettingsService
{
    internal const int MaxFileBytes = 4 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly BackgroundSettingsStorageOptions _storageOptions;
    private readonly ILogger<JsonBackgroundMonitoringSettingsService> _logger;
    private readonly IConfigurationWriteGate _writeGate;
    private readonly object _sync = new();
    private bool _backgroundMonitoringEnabled;
    private bool _backgroundNoticeShown;

    public JsonBackgroundMonitoringSettingsService(
        BackgroundSettingsStorageOptions storageOptions,
        ILogger<JsonBackgroundMonitoringSettingsService> logger,
        IConfigurationWriteGate writeGate)
    {
        _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _writeGate = writeGate ?? throw new ArgumentNullException(nameof(writeGate));

        var document = LoadOrDefault();
        _backgroundMonitoringEnabled = document.BackgroundMonitoringEnabled;
        _backgroundNoticeShown = document.BackgroundNoticeShown;
    }

    public event EventHandler? BackgroundMonitoringEnabledChanged;

    public bool BackgroundMonitoringEnabled
    {
        get { lock (_sync) { return _backgroundMonitoringEnabled; } }
    }

    public bool BackgroundNoticeShown
    {
        get { lock (_sync) { return _backgroundNoticeShown; } }
    }

    public void SetBackgroundMonitoringEnabled(bool enabled)
    {
        EventHandler? changed;
        lock (_sync)
        {
            if (_backgroundMonitoringEnabled == enabled)
            {
                return;
            }

            // M14.6 V5: refused while a restore holds the configuration gate or after it committed.
            using var lease = _writeGate.EnterWrite();
            Save(enabled, _backgroundNoticeShown);
            _backgroundMonitoringEnabled = enabled;
            changed = BackgroundMonitoringEnabledChanged;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Claims the single notice. The flag is persisted BEFORE the caller attempts delivery, so a toast
    /// that Windows never displays still spends it — the requirement is "attempted once", not
    /// "delivered once", precisely so an unavailable notification channel cannot turn into a nag.
    /// A persistence failure still claims it in memory for this session rather than retrying in a loop.
    /// </summary>
    public bool TryClaimBackgroundNotice()
    {
        lock (_sync)
        {
            if (_backgroundNoticeShown)
            {
                return false;
            }

            try
            {
                // While a restore holds the gate the claim stays in memory only: no file write (M14.6 §5.5).
                using var lease = _writeGate.EnterWrite();
                Save(_backgroundMonitoringEnabled, backgroundNoticeShown: true);
            }
            catch (ConfigurationLockedException)
            {
                _logger.LogInformation("The background notice was claimed in memory only: a restore holds the configuration.");
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "The background notice flag could not be persisted ({Type}); it stays claimed for this session.",
                    exception.GetType().Name);
            }

            _backgroundNoticeShown = true;
            return true;
        }
    }

    /// <summary>The file bytes a save writes (restore renders with it).</summary>
    internal static byte[] Render(bool backgroundMonitoringEnabled, bool backgroundNoticeShown) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new BackgroundSettingsDocument
            {
                BackgroundMonitoringEnabled = backgroundMonitoringEnabled,
                BackgroundNoticeShown = backgroundNoticeShown
            },
            SerializerOptions);

    /// <summary>The monitoring preference the service would load from <paramref name="bytes"/> (null = no file).</summary>
    internal static bool ParseBackgroundMonitoringEnabled(byte[]? bytes)
    {
        if (bytes is null || bytes.Length > MaxFileBytes)
        {
            return BackgroundSettingsDocument.Default.BackgroundMonitoringEnabled;
        }

        try
        {
            return (JsonSerializer.Deserialize<BackgroundSettingsDocument>(bytes, SerializerOptions)
                ?? BackgroundSettingsDocument.Default).BackgroundMonitoringEnabled;
        }
        catch (JsonException)
        {
            return BackgroundSettingsDocument.Default.BackgroundMonitoringEnabled;
        }
    }

    /// <summary>
    /// Restore-only replace of the file (<paramref name="content"/> null deletes it) under this service's lock;
    /// the in-memory values are reloaded from the file without raising the change event (the app relaunches).
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

            var document = LoadOrDefault();
            _backgroundMonitoringEnabled = document.BackgroundMonitoringEnabled;
            _backgroundNoticeShown = document.BackgroundNoticeShown;
        }
    }

    private BackgroundSettingsDocument LoadOrDefault()
    {
        try
        {
            if (!File.Exists(_storageOptions.FilePath))
            {
                return BackgroundSettingsDocument.Default;
            }

            var info = new FileInfo(_storageOptions.FilePath);
            if (info.Length > MaxFileBytes)
            {
                _logger.LogWarning(
                    "Background settings exceed {MaxBytes} bytes; using defaults.",
                    MaxFileBytes);
                return BackgroundSettingsDocument.Default;
            }

            using var stream = new FileStream(
                _storageOptions.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            return JsonSerializer.Deserialize<BackgroundSettingsDocument>(stream, SerializerOptions)
                ?? BackgroundSettingsDocument.Default;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Background settings could not be read ({Type}); using defaults.",
                exception.GetType().Name);
            return BackgroundSettingsDocument.Default;
        }
    }

    private void Save(bool backgroundMonitoringEnabled, bool backgroundNoticeShown) =>
        WriteAtomically(Render(backgroundMonitoringEnabled, backgroundNoticeShown));

    private void WriteAtomically(byte[] content)
    {
        var directory = Path.GetDirectoryName(_storageOptions.FilePath)
            ?? throw new InvalidOperationException("The background settings path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryFile = _storageOptions.FilePath + ".tmp";

        try
        {
            using (var stream = new FileStream(
                temporaryFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
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

    private sealed record BackgroundSettingsDocument
    {
        public static readonly BackgroundSettingsDocument Default = new();

        public bool BackgroundMonitoringEnabled { get; init; } = true;

        public bool BackgroundNoticeShown { get; init; }
    }
}
