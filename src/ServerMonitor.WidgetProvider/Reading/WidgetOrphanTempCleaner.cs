using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Diagnostics;

namespace ServerMonitor.WidgetProvider.Reading;

/// <summary>
/// Best-effort, bounded sweep of orphan temp files left by a crashed writer between its temp-write and
/// atomic rename (Vigil L2). It deletes ONLY files matching the writer's own temp pattern
/// (<see cref="WidgetStateLocation.IsOwnTempFileName"/>) in the snapshot directory — never arbitrary
/// files, never recursing into subdirectories, and never following the directory if it is a reparse
/// point (symlink/junction). Any failure is swallowed: cleanup must never destabilize the provider (§8).
/// </summary>
public sealed class WidgetOrphanTempCleaner
{
    /// <summary>Upper bound on files EXAMINED per sweep, so a directory stuffed with matching (even
    /// undeletable) temp names cannot make the sweep — or its logging — do unbounded work (Vigil S2 L-1).</summary>
    private const int MaxFilesPerSweep = 512;

    private readonly string _directory;
    private readonly IWidgetProviderLog _log;

    public WidgetOrphanTempCleaner(string? directory = null, IWidgetProviderLog? log = null)
    {
        _directory = directory ?? WidgetStateLocation.DirectoryForCurrentUser();
        _log = log ?? NullWidgetProviderLog.Instance;
    }

    /// <summary>Returns the number of temp files removed (0 on any error). Never throws.</summary>
    public int Sweep()
    {
        try
        {
            var directoryInfo = new DirectoryInfo(_directory);
            if (!directoryInfo.Exists)
            {
                return 0;
            }

            // Do not traverse a reparse point — refuse to operate through a symlink/junction we did not
            // create, to avoid being redirected to an unexpected location.
            if (directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                _log.Warn("Widget snapshot directory is a reparse point; skipping temp cleanup.");
                return 0;
            }

            var removed = 0;
            var examined = 0;
            // Top level only (no recursion). EnumerateFiles yields names in this directory only.
            foreach (var file in directoryInfo.EnumerateFiles($"{WidgetStateLocation.TempPrefix}*{WidgetStateLocation.TempExtension}"))
            {
                // Bound total ITERATIONS (not just successful deletions) so undeletable matches cannot
                // make the sweep or its logging run unbounded.
                if (examined++ >= MaxFilesPerSweep)
                {
                    break;
                }

                if (!WidgetStateLocation.IsOwnTempFileName(file.Name))
                {
                    continue;
                }

                // Skip anything that is itself a reparse point (a temp name pointing elsewhere).
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                try
                {
                    file.Delete();
                    removed++;
                }
                catch (Exception exception)
                {
                    // A temp still held by an in-flight write, or a permission issue — leave it, it is inert.
                    _log.Info($"Skipped an orphan temp file. Error: {exception.GetType().Name}.");
                }
            }

            return removed;
        }
        catch (Exception exception)
        {
            _log.Warn($"Widget temp cleanup failed. Error: {exception.GetType().Name}.");
            return 0;
        }
    }
}
