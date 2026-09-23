using System.Text;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.Core.Tests.SshConfig;

/// <summary>
/// In-memory <see cref="ISshConfigFileSystem"/> with a spy on every call. Paths are Windows-style
/// and case-insensitive; parent directories are created implicitly.
/// </summary>
internal sealed class InMemorySshConfigFileSystem : ISshConfigFileSystem
{
    public const string Profile = @"C:\Users\tester";
    public const string SshDirectory = @"C:\Users\tester\.ssh";

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public InMemorySshConfigFileSystem()
    {
        AddDirectory(SshDirectory);
    }

    public List<string> Reads { get; } = [];

    public List<string> Enumerations { get; } = [];

    /// <summary>Adds a file; a relative path is relative to <see cref="SshDirectory"/>.</summary>
    public InMemorySshConfigFileSystem File(string path, string text) => File(path, Encoding.UTF8.GetBytes(text));

    public InMemorySshConfigFileSystem File(string path, byte[] bytes, SshConfigReadStatus status = SshConfigReadStatus.Ok)
    {
        var full = Full(path);
        EnsureParents(full);
        _entries[full] = new Entry(SshConfigPathKind.RegularFile, false, bytes, status);
        return this;
    }

    public InMemorySshConfigFileSystem AddDirectory(string path)
    {
        var full = Full(path);
        EnsureParents(full);
        _entries.TryAdd(full, new Entry(SshConfigPathKind.Directory, false, [], SshConfigReadStatus.Ok));
        return this;
    }

    public InMemorySshConfigFileSystem Device(string path)
    {
        var full = Full(path);
        EnsureParents(full);
        _entries[full] = new Entry(SshConfigPathKind.Other, false, [], SshConfigReadStatus.Unreadable);
        return this;
    }

    public InMemorySshConfigFileSystem ReparsePoint(string path)
    {
        var full = Full(path);
        _entries[full] = _entries[full] with { IsReparsePoint = true };
        return this;
    }

    public SshConfigPathInfo GetInfo(string fullPath) =>
        _entries.TryGetValue(fullPath, out var entry)
            ? new SshConfigPathInfo(entry.Kind, entry.IsReparsePoint)
            : new SshConfigPathInfo(SshConfigPathKind.Missing, false);

    public IReadOnlyList<string>? EnumerateNames(string fullDirectoryPath)
    {
        Enumerations.Add(fullDirectoryPath);
        var prefix = fullDirectoryPath.TrimEnd('\\') + "\\";
        return _entries.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.IndexOf('\\', prefix.Length) < 0)
            .Select(key => key[prefix.Length..])
            .ToList();
    }

    /// <summary>Called after a read is recorded (e.g. to cancel mid-import).</summary>
    public Action<string>? OnRead { get; set; }

    public SshConfigFileRead ReadFile(string fullPath, int maxBytes)
    {
        Reads.Add(fullPath);
        OnRead?.Invoke(fullPath);
        if (!_entries.TryGetValue(fullPath, out var entry) || entry.Kind == SshConfigPathKind.Directory)
        {
            return SshConfigFileRead.Failed(SshConfigReadStatus.NotFound);
        }

        if (entry.Status != SshConfigReadStatus.Ok)
        {
            return SshConfigFileRead.Failed(entry.Status);
        }

        return entry.Bytes.Length > maxBytes
            ? SshConfigFileRead.Failed(SshConfigReadStatus.TooLarge)
            : new SshConfigFileRead(SshConfigReadStatus.Ok, entry.Bytes);
    }

    public static string Full(string path) =>
        path.Length > 1 && path[1] == ':' ? path : SshDirectory + "\\" + path.Replace('/', '\\');

    private void EnsureParents(string full)
    {
        var index = full.LastIndexOf('\\');
        while (index > 2)
        {
            var parent = full[..index];
            _entries.TryAdd(parent, new Entry(SshConfigPathKind.Directory, false, [], SshConfigReadStatus.Ok));
            index = parent.LastIndexOf('\\');
        }
    }

    private sealed record Entry(SshConfigPathKind Kind, bool IsReparsePoint, byte[] Bytes, SshConfigReadStatus Status);
}
