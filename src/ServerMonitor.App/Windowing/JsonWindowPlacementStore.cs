using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Windowing;

/// <summary>
/// Persists the window-placement preference to a small non-sensitive JSON file, mirroring the
/// atomic write-through pattern used for notification settings. The persisted rectangle is
/// untrusted input on read: modes outside the enum, invalid DPI factors and absurd/negative/zero
/// bounds are sanitized to safe defaults, and a missing/malformed/oversized file yields
/// <see cref="WindowPlacementSettings.Default"/> so the app always opens somewhere usable.
/// </summary>
public sealed class JsonWindowPlacementStore : IWindowPlacementStore
{
    internal const int MaxFileBytes = 4 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly WindowPlacementStorageOptions _storageOptions;
    private readonly ILogger<JsonWindowPlacementStore> _logger;
    private readonly object _sync = new();

    public JsonWindowPlacementStore(
        WindowPlacementStorageOptions storageOptions,
        ILogger<JsonWindowPlacementStore> logger)
    {
        _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WindowPlacementSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_storageOptions.FilePath))
                {
                    return WindowPlacementSettings.Default;
                }

                var info = new FileInfo(_storageOptions.FilePath);
                if (info.Length > MaxFileBytes)
                {
                    _logger.LogWarning(
                        "Window placement file exceeds {MaxBytes} bytes; using defaults.",
                        MaxFileBytes);
                    return WindowPlacementSettings.Default;
                }

                using var stream = new FileStream(
                    _storageOptions.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan);
                var document = JsonSerializer.Deserialize<PlacementDocument>(stream, SerializerOptions);
                return document is null ? WindowPlacementSettings.Default : Sanitize(document);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    "Window placement could not be read ({Type}); using defaults.",
                    exception.GetType().Name);
                return WindowPlacementSettings.Default;
            }
        }
    }

    public void Save(WindowPlacementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            try
            {
                var directory = Path.GetDirectoryName(_storageOptions.FilePath)
                    ?? throw new InvalidOperationException("The window placement path has no directory.");
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
                        JsonSerializer.Serialize(stream, PlacementDocument.From(settings), SerializerOptions);
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Persisting placement is best-effort; a write failure must never break the app.
                _logger.LogWarning(
                    "Window placement could not be saved ({Type}); the preference will not persist.",
                    exception.GetType().Name);
            }
        }
    }

    private static WindowPlacementSettings Sanitize(PlacementDocument document)
    {
        var mode = document.Mode is WindowMode.Standard or WindowMode.Compact
            ? document.Mode.Value
            : WindowMode.Standard;

        return new WindowPlacementSettings
        {
            Mode = mode,
            StandardBounds = SanitizeBounds(document.StandardBounds),
            StandardDpiScalePercent = SanitizeDpi(document.StandardDpiScalePercent),
            CompactBounds = SanitizeBounds(document.CompactBounds),
            CompactDpiScalePercent = SanitizeDpi(document.CompactDpiScalePercent),
            CompactAlwaysOnTop = document.CompactAlwaysOnTop ?? false
        };
    }

    private static WindowBounds? SanitizeBounds(BoundsDocument? bounds)
    {
        if (bounds?.X is not { } x
            || bounds.Y is not { } y
            || bounds.Width is not { } width
            || bounds.Height is not { } height)
        {
            return null;
        }

        var candidate = new WindowBounds(x, y, width, height);
        return WindowPlacementResolver.IsSane(candidate) ? candidate : null;
    }

    private static int SanitizeDpi(int? dpiScalePercent) =>
        dpiScalePercent is { } value && WindowPlacementResolver.IsValidDpi(value)
            ? value
            : WindowPlacementSettings.DefaultDpiScalePercent;

    private sealed record PlacementDocument
    {
        public WindowMode? Mode { get; init; }

        public BoundsDocument? StandardBounds { get; init; }

        public int? StandardDpiScalePercent { get; init; }

        public BoundsDocument? CompactBounds { get; init; }

        public int? CompactDpiScalePercent { get; init; }

        public bool? CompactAlwaysOnTop { get; init; }

        public static PlacementDocument From(WindowPlacementSettings settings) => new()
        {
            Mode = settings.Mode,
            StandardBounds = BoundsDocument.From(settings.StandardBounds),
            StandardDpiScalePercent = settings.StandardDpiScalePercent,
            CompactBounds = BoundsDocument.From(settings.CompactBounds),
            CompactDpiScalePercent = settings.CompactDpiScalePercent,
            CompactAlwaysOnTop = settings.CompactAlwaysOnTop
        };
    }

    private sealed record BoundsDocument
    {
        public int? X { get; init; }

        public int? Y { get; init; }

        public int? Width { get; init; }

        public int? Height { get; init; }

        public static BoundsDocument? From(WindowBounds? bounds) => bounds is { } value
            ? new BoundsDocument { X = value.X, Y = value.Y, Width = value.Width, Height = value.Height }
            : null;
    }
}
