using System.Diagnostics;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.Infrastructure.Tests.SshConfig;

/// <summary>
/// M14.5 A1: default key discovery from METADATA only. The seam tests drive the production
/// <see cref="LocalSshKeyDiscovery"/> over an in-memory metadata table; the real-disk tests run it over the
/// real <see cref="LocalSshKeyFileSystem"/> and prove it never opens a key (a key held with
/// <see cref="FileShare.None"/> is still found) and never writes anything.
/// </summary>
public sealed class LocalSshKeyDiscoveryTests : IDisposable
{
    private const string Profile = @"C:\Users\qa";
    private const string Ssh = @"C:\Users\qa\.ssh";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sm-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // A junction is removed as a link (non-recursive) before its target, never followed.
                foreach (var junction in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                             .Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                             .ToList())
                {
                    Directory.Delete(junction);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ seam

    [Fact]
    public async Task All_three_defaults_are_listed_in_preference_order_and_only_the_first_is_recommended()
    {
        var fs = new MetadataTable().Dir(Ssh).Key("id_rsa").Key("id_ecdsa").Key("id_ed25519");

        var keys = await Discover(fs);

        Assert.Equal(
            [
                new LocalSshKey(Path.Combine(Ssh, "id_ed25519"), "id_ed25519", LocalSshKeyKind.Ed25519, true),
                new LocalSshKey(Path.Combine(Ssh, "id_ecdsa"), "id_ecdsa", LocalSshKeyKind.Ecdsa, false),
                new LocalSshKey(Path.Combine(Ssh, "id_rsa"), "id_rsa", LocalSshKeyKind.Rsa, false)
            ],
            keys);
    }

    [Fact]
    public async Task Without_ed25519_the_ecdsa_key_is_recommended()
    {
        var keys = await Discover(new MetadataTable().Dir(Ssh).Key("id_rsa").Key("id_ecdsa"));

        Assert.Equal([LocalSshKeyKind.Ecdsa, LocalSshKeyKind.Rsa], keys.Select(k => k.Kind));
        Assert.Equal([true, false], keys.Select(k => k.IsRecommended));
    }

    [Fact]
    public async Task A_lone_rsa_key_is_recommended()
    {
        var key = Assert.Single(await Discover(new MetadataTable().Dir(Ssh).Key("id_rsa")));

        Assert.Equal(LocalSshKeyKind.Rsa, key.Kind);
        Assert.True(key.IsRecommended);
    }

    [Fact]
    public async Task Only_the_three_exact_default_paths_are_ever_looked_at()
    {
        // Every excluded name exists and would qualify: none is looked up, so none can be listed.
        var fs = new MetadataTable().Dir(Ssh)
            .Key("id_dsa").Key("id_ed25519_sk").Key("id_ecdsa_sk").Key("id_ed25519.pub").Key("my_key").Key("id_rsa.old");

        var keys = await Discover(fs);

        Assert.Empty(keys);
        Assert.Equal(
            [Ssh, Path.Combine(Ssh, "id_ed25519"), Path.Combine(Ssh, "id_ecdsa"), Path.Combine(Ssh, "id_rsa")],
            fs.Described);
    }

    [Fact]
    public async Task A_reparse_point_key_is_never_offered()
    {
        var fs = new MetadataTable().Dir(Ssh)
            .Set(Path.Combine(Ssh, "id_ed25519"), new(LocalSshKeyPathKind.RegularFile, IsReparsePoint: true, 400))
            .Key("id_rsa");

        var key = Assert.Single(await Discover(fs));

        Assert.Equal("id_rsa", key.FileName);
        Assert.True(key.IsRecommended);
    }

    [Fact]
    public async Task A_directory_or_device_with_a_key_name_is_never_offered()
    {
        var fs = new MetadataTable().Dir(Ssh)
            .Set(Path.Combine(Ssh, "id_ed25519"), new(LocalSshKeyPathKind.Directory, false, 0))
            .Set(Path.Combine(Ssh, "id_ecdsa"), new(LocalSshKeyPathKind.Other, false, 400));

        Assert.Empty(await Discover(fs));
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(1L, true)]
    [InlineData(LocalSshKeyDiscovery.MaxKeyFileBytes, true)]
    [InlineData(LocalSshKeyDiscovery.MaxKeyFileBytes + 1, false)]
    [InlineData(long.MaxValue, false)]
    public async Task Only_a_non_empty_file_up_to_64_KiB_is_offered(long length, bool offered)
    {
        var fs = new MetadataTable().Dir(Ssh).Key("id_ed25519", length);

        Assert.Equal(offered, (await Discover(fs)).Count == 1);
    }

    [Fact]
    public async Task A_missing_ssh_directory_yields_nothing()
    {
        var fs = new MetadataTable().Key("id_ed25519"); // .ssh itself is missing

        Assert.Empty(await Discover(fs));
    }

    [Fact]
    public async Task A_linked_ssh_directory_yields_nothing()
    {
        var fs = new MetadataTable()
            .Set(Ssh, new(LocalSshKeyPathKind.Directory, IsReparsePoint: true, 0))
            .Key("id_ed25519");

        Assert.Empty(await Discover(fs));
    }

    [Fact]
    public async Task An_ssh_path_that_is_a_file_yields_nothing()
    {
        var fs = new MetadataTable().Set(Ssh, new(LocalSshKeyPathKind.RegularFile, false, 10)).Key("id_ed25519");

        Assert.Empty(await Discover(fs));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task An_exception_on_one_key_skips_it_and_never_throws(Type exceptionType)
    {
        var fs = new MetadataTable().Dir(Ssh).Key("id_rsa");
        fs.Throw(Path.Combine(Ssh, "id_ed25519"), (Exception)Activator.CreateInstance(exceptionType)!);

        var key = Assert.Single(await Discover(fs));

        Assert.Equal("id_rsa", key.FileName);
        Assert.True(key.IsRecommended);
    }

    [Fact]
    public async Task An_exception_on_the_ssh_directory_yields_nothing()
    {
        var fs = new MetadataTable().Key("id_ed25519");
        fs.Throw(Ssh, new UnauthorizedAccessException());

        Assert.Empty(await Discover(fs));
    }

    [Fact]
    public async Task A_blank_profile_yields_nothing_and_looks_at_nothing()
    {
        var fs = new MetadataTable();

        Assert.Empty(await new LocalSshKeyDiscovery(" ", fs.Describe).DiscoverAsync());
        Assert.Empty(fs.Described);
    }

    [Fact]
    public async Task An_already_cancelled_token_is_honoured()
    {
        var fs = new MetadataTable().Dir(Ssh).Key("id_ed25519");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new LocalSshKeyDiscovery(Profile, fs.Describe).DiscoverAsync(cancel.Token));
        Assert.Empty(fs.Described);
    }

    [Fact]
    public async Task Cancelling_mid_discovery_stops_before_the_next_key()
    {
        using var cancel = new CancellationTokenSource();
        var fs = new MetadataTable().Dir(Ssh).Key("id_ed25519").Key("id_ecdsa").Key("id_rsa");
        fs.OnDescribe = path =>
        {
            if (path.EndsWith("id_ed25519", StringComparison.Ordinal))
            {
                cancel.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new LocalSshKeyDiscovery(Profile, fs.Describe).DiscoverAsync(cancel.Token));
        Assert.DoesNotContain(Path.Combine(Ssh, "id_ecdsa"), fs.Described);
        Assert.DoesNotContain(Path.Combine(Ssh, "id_rsa"), fs.Described);
    }

    [Fact]
    public async Task Discovery_runs_off_the_calling_thread()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fs = new MetadataTable().Dir(Ssh).Key("id_ed25519");
        fs.OnDescribe = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        // The call returns while the metadata lookup is still blocked: it was not run on this thread.
        var task = new LocalSshKeyDiscovery(Profile, fs.Describe).DiscoverAsync();
        Assert.False(task.IsCompleted);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        release.Set();

        Assert.Single(await task);
    }

    // ------------------------------------------------------------- real disk

    [Fact]
    public async Task Real_disk_finds_a_key_that_cannot_be_opened_so_discovery_never_opens_it()
    {
        var ssh = CreateSsh();
        WriteDummy(ssh, "id_ed25519");
        WriteDummy(ssh, "id_rsa");

        // Held exclusively: any open (read, or even attributes-by-handle) by discovery would fail and drop it.
        using var lockEd = new FileStream(Path.Combine(ssh, "id_ed25519"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var lockRsa = new FileStream(Path.Combine(ssh, "id_rsa"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var keys = await new LocalSshKeyDiscovery(_root).DiscoverAsync();

        Assert.Equal(["id_ed25519", "id_rsa"], keys.Select(k => k.FileName));
        Assert.Equal(Path.Combine(ssh, "id_ed25519"), keys[0].Path);
        Assert.True(keys[0].IsRecommended);
    }

    [Fact]
    public async Task Real_disk_excluded_names_empty_oversize_and_directories_are_not_offered()
    {
        var ssh = CreateSsh();
        foreach (var name in new[] { "id_dsa", "id_ed25519_sk", "id_ed25519.pub", "custom_key" })
        {
            WriteDummy(ssh, name);
        }

        File.WriteAllBytes(Path.Combine(ssh, "id_ed25519"), new byte[LocalSshKeyDiscovery.MaxKeyFileBytes + 1]);
        File.WriteAllBytes(Path.Combine(ssh, "id_ecdsa"), []);
        Directory.CreateDirectory(Path.Combine(ssh, "id_rsa"));

        Assert.Empty(await new LocalSshKeyDiscovery(_root).DiscoverAsync());
    }

    [Fact]
    public async Task Real_disk_never_writes_or_changes_anything()
    {
        var ssh = CreateSsh();
        WriteDummy(ssh, "id_ed25519");
        WriteDummy(ssh, "id_ecdsa");
        var before = Snapshot(ssh);

        await new LocalSshKeyDiscovery(_root).DiscoverAsync();

        Assert.Equal(before, Snapshot(ssh));
    }

    [Fact]
    public async Task Real_disk_missing_profile_yields_nothing()
    {
        Assert.Empty(await new LocalSshKeyDiscovery(Path.Combine(_root, "nobody")).DiscoverAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, "nobody"))); // nothing created
    }

    [Fact]
    public async Task Real_disk_ssh_directory_that_is_a_junction_yields_nothing()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        WriteDummy(elsewhere, "id_ed25519");
        var profile = Path.Combine(_root, "linked");
        Directory.CreateDirectory(profile);
        var junction = Path.Combine(profile, ".ssh");
        CreateJunction(junction, elsewhere);
        Assert.True(new DirectoryInfo(junction).Attributes.HasFlag(FileAttributes.ReparsePoint));

        Assert.Empty(await new LocalSshKeyDiscovery(profile).DiscoverAsync());
    }

    [Fact]
    public void Real_metadata_reader_reports_a_junction_as_a_reparse_point_without_following_it()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(_root, "junction");
        CreateJunction(junction, target);

        var metadata = LocalSshKeyFileSystem.Describe(junction);

        Assert.Equal(LocalSshKeyPathKind.Directory, metadata.Kind);
        Assert.True(metadata.IsReparsePoint);
        Assert.Equal(LocalSshKeyFileMetadata.Missing, LocalSshKeyFileSystem.Describe(Path.Combine(_root, "absent")));
    }

    // ---------------------------------------------------------------- helpers

    private static Task<IReadOnlyList<LocalSshKey>> Discover(MetadataTable fs) =>
        new LocalSshKeyDiscovery(Profile, fs.Describe).DiscoverAsync();

    private string CreateSsh()
    {
        var ssh = Path.Combine(_root, ".ssh");
        Directory.CreateDirectory(ssh);
        return ssh;
    }

    private static void WriteDummy(string directory, string name) =>
        File.WriteAllText(Path.Combine(directory, name), "QA DUMMY - NOT A PRIVATE KEY\n");

    private static List<string> Snapshot(string directory) =>
        new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Select(entry => $"{entry.FullName}|{entry.Attributes}|{entry.LastWriteTimeUtc.Ticks}|{(entry as FileInfo)?.Length}")
            .Order(StringComparer.Ordinal)
            .ToList();

    private static void CreateJunction(string link, string target)
    {
        // A junction needs no privilege (unlike a symbolic link).
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit(10_000);
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>In-memory metadata: a path absent from the table is missing. Records every path looked at.</summary>
    private sealed class MetadataTable
    {
        private readonly Dictionary<string, LocalSshKeyFileMetadata> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _throws = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Described { get; } = [];

        public Action<string>? OnDescribe { get; set; }

        public MetadataTable Dir(string path) => Set(path, new(LocalSshKeyPathKind.Directory, false, 0));

        public MetadataTable Key(string name, long length = 411) =>
            Set(Path.Combine(Ssh, name), new(LocalSshKeyPathKind.RegularFile, false, length));

        public MetadataTable Set(string path, LocalSshKeyFileMetadata metadata)
        {
            _entries[path] = metadata;
            return this;
        }

        public void Throw(string path, Exception exception) => _throws[path] = exception;

        public LocalSshKeyFileMetadata Describe(string fullPath)
        {
            lock (Described)
            {
                Described.Add(fullPath);
            }

            OnDescribe?.Invoke(fullPath);
            if (_throws.TryGetValue(fullPath, out var exception))
            {
                throw exception;
            }

            return _entries.TryGetValue(fullPath, out var metadata) ? metadata : LocalSshKeyFileMetadata.Missing;
        }
    }
}
