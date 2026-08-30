namespace ServerMonitor.WidgetContract;

/// <summary>
/// The single, canonical on-disk location of the widget snapshot and its temp files, shared by the
/// writer (app) and the reader (out-of-process provider) so they cannot drift. LOCAL-FIRST: under
/// <c>%LOCALAPPDATA%\ServerMonitor</c> (folder name kept for compatibility, ADR-018 §7).
/// </summary>
public static class WidgetStateLocation
{
    /// <summary>Internal folder under LocalApplicationData. Not renamed to ServerAlyzer in M13 (§7).</summary>
    public const string FolderName = "ServerMonitor";

    /// <summary>The committed snapshot file name.</summary>
    public const string FileName = "widget-state.json";

    /// <summary>Prefix/extension of the writer's atomic temp files. The reader's orphan sweep matches
    /// ONLY this pattern so it can never touch unrelated files.</summary>
    public const string TempPrefix = "widget-state.";

    public const string TempExtension = ".tmp";

    /// <summary>
    /// Hard upper bound on the snapshot file size, enforced BEFORE reading/deserializing (untrusted-on-
    /// read, L-018). A 100-server snapshot is well under ~30 KB; 256 KB leaves generous margin while
    /// making an altered/oversized file fail neutral instead of pulling arbitrary bytes into memory.
    /// </summary>
    public const long MaxFileBytes = 256L * 1024L;

    /// <summary>Absolute path of the snapshot for the current user.</summary>
    public static string ForCurrentUser() => Combine(FileName);

    /// <summary>Absolute directory that holds the snapshot and its temp files.</summary>
    public static string DirectoryForCurrentUser() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>A fresh, unique temp file NAME (no directory) following the shared pattern.</summary>
    public static string NewTempName() => $"{TempPrefix}{Guid.NewGuid():N}{TempExtension}";

    /// <summary>A fresh, unique temp path in <paramref name="directory"/> (same volume as the target).</summary>
    public static string NewTempPath(string directory) =>
        System.IO.Path.Combine(directory, NewTempName());

    /// <summary>True if <paramref name="fileName"/> is one of OUR atomic temp files (name only, no path).</summary>
    public static bool IsOwnTempFileName(string? fileName) =>
        !string.IsNullOrEmpty(fileName) &&
        fileName.StartsWith(TempPrefix, StringComparison.Ordinal) &&
        fileName.EndsWith(TempExtension, StringComparison.Ordinal) &&
        // Guard against the committed file itself and any path separators sneaking in.
        !string.Equals(fileName, FileName, StringComparison.OrdinalIgnoreCase) &&
        fileName.IndexOfAny(new[] { '/', '\\' }) < 0;

    private static string Combine(string leaf) => System.IO.Path.Combine(DirectoryForCurrentUser(), leaf);
}
