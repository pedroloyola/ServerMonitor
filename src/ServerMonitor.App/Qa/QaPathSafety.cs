namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (UI.2 R1: Cortex R-9, Vigil low). A string prefix check after <see cref="Path.GetFullPath(string)"/> cannot see
/// a junction or symbolic link: a reparse point inside the QA folder could redirect a delete or a report write into the
/// user's real data. Every QA write/delete therefore refuses a path whose existing file or any existing ancestor is a
/// reparse point. Excluded from Release.
/// </summary>
internal static class QaPathSafety
{
    /// <summary>True when the path itself or any existing ancestor directory is a reparse point (junction/symlink).</summary>
    public static bool CrossesReparsePoint(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }
}
