namespace ServerMonitor.Core.SshConfig;

/// <summary>
/// Pure, lexical path rules for following <c>Include</c> (Windows semantics, no file-system
/// access). A path is accepted only as a plain, fully-qualified local path (<c>X:\...</c>): UNC,
/// <c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>, drive-relative, device names, alternate data streams
/// (<c>:</c>) and segments with trailing dots or spaces are all rejected, and <c>..</c> is resolved
/// lexically before any containment check.
/// </summary>
public static class SshConfigPathPolicy
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>The normalized <c>X:\a\b</c> form, or <see langword="null"/> when the path is not a plain local path.</summary>
    public static string? NormalizeFullPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 2048)
        {
            return null;
        }

        foreach (var c in path)
        {
            if (c < ' ' || c is '<' or '>' or '|' or '"' or '*' or '?')
            {
                return null;
            }
        }

        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith('\\')
            || normalized.Length < 3
            || !char.IsAsciiLetter(normalized[0])
            || normalized[1] != ':'
            || normalized[2] != '\\'
            || normalized.IndexOf(':', 2) >= 0)
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var segment in normalized[3..].Split('\\'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            if (segment.EndsWith('.') || segment.EndsWith(' ') || IsDeviceName(segment))
            {
                return null;
            }

            segments.Add(segment);
        }

        return char.ToUpperInvariant(normalized[0]) + ":\\" + string.Join('\\', segments);
    }

    /// <summary>True when <paramref name="candidate"/> is strictly inside <paramref name="directory"/> (both normalized).</summary>
    public static bool IsStrictlyInside(string candidate, string directory) =>
        candidate.Length > directory.Length + 1
        && candidate.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
        && candidate[directory.Length] == '\\';

    public static bool IsSameOrInside(string candidate, string directory) =>
        string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase) || IsStrictlyInside(candidate, directory);

    public static bool HasGlob(string text) => text.IndexOfAny(['*', '?']) >= 0;

    /// <summary>Glob syntax beyond <c>*</c> and <c>?</c> (<c>[...]</c>, <c>{...}</c>, <c>**</c>) is not supported.</summary>
    public static bool HasUnsupportedGlob(string text) =>
        text.IndexOfAny(['[', ']', '{', '}']) >= 0 || text.Contains("**", StringComparison.Ordinal);

    /// <summary>
    /// glob(3) matching of one path segment: <c>*</c> any run, <c>?</c> one character, and a
    /// leading <c>.</c> is only matched by a literal leading <c>.</c>. Case-insensitive, as the
    /// Windows file system is.
    /// </summary>
    public static bool GlobMatch(string pattern, string name)
    {
        if (name.StartsWith('.') && !pattern.StartsWith('.'))
        {
            return false;
        }

        return SshConfigResolver.WildcardMatch(pattern, name);
    }

    private static bool IsDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
        return DeviceNames.Contains(stem);
    }
}
