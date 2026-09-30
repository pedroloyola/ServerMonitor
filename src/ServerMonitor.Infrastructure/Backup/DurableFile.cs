namespace ServerMonitor.Infrastructure.Backup;

/// <summary>
/// Durable atomic replace used by restore and its journal: write <c>&lt;path&gt;.tmp</c> with
/// <see cref="FileOptions.WriteThrough"/>, <c>Flush(true)</c>, then <see cref="File.Move(string, string, bool)"/>
/// over the destination (BK-ROB-1: NTFS rename durability is mitigated, not provable). The temp name is the
/// same fixed <c>.tmp</c> suffix the stores already use.
/// </summary>
internal static class DurableFile
{
    public const string TemporarySuffix = ".tmp";

    /// <summary>Replaces <paramref name="path"/> with <paramref name="content"/>, or deletes it when
    /// <paramref name="content"/> is <see langword="null"/>.</summary>
    public static void Replace(string path, byte[]? content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (content is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The destination path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryFile = path + TemporarySuffix;
        try
        {
            using (var stream = new FileStream(
                temporaryFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryFile, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    /// <summary>The file's bytes, or <see langword="null"/> when it does not exist.</summary>
    public static byte[]? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
}
