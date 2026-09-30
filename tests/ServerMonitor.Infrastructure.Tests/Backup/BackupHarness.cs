using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;
using ServerMonitor.Infrastructure.SSH;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.Infrastructure.Tests.Backup;

/// <summary>
/// One "machine": the REAL stores over real files in a temp data directory, the REAL gate, a fake raw
/// credential store (in-memory, with faults), a file-backed settings participant with the App's semantics,
/// a capturing logger and a fake monitoring engine. Everything the backup engine touches is real except the
/// Credential Manager and the App's two settings services.
/// </summary>
internal sealed class BackupHarness : IDisposable
{
    public const string Passphrase = "correct horse battery staple";
    public const string SecretMarker = "SECRET-MARKER-";

    private int _nextId;

    public BackupHarness(IServerCredentialStore? rawStore = null)
    {
        RawStore = rawStore ?? Raw;
        Root = Path.Combine(Path.GetTempPath(), "sm-backup-" + Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(Root, "data");
        OutDirectory = Path.Combine(Root, "out");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(OutDirectory);
        ServerOptions = new ServerStorageOptions { FilePath = Path.Combine(DataDirectory, "servers.json") };
        TrustOptions = new HostKeyTrustStorageOptions { FilePath = Path.Combine(DataDirectory, "known-hosts.json") };
        RoutedTrustOptions = RoutedHostKeyTrustStorageOptions.From(TrustOptions);
        Reopen();
    }

    public string Root { get; }

    public string DataDirectory { get; }

    public string OutDirectory { get; }

    public ServerStorageOptions ServerOptions { get; }

    public HostKeyTrustStorageOptions TrustOptions { get; }

    public RoutedHostKeyTrustStorageOptions RoutedTrustOptions { get; }

    public ConfigurationWriteGate Gate { get; private set; } = null!;

    public JsonServerRepository Repository { get; private set; } = null!;

    public ServerService Servers { get; private set; } = null!;

    public JsonHostKeyTrustStore DirectTrust { get; private set; } = null!;

    public JsonRoutedHostKeyTrustStore RoutedTrust { get; private set; } = null!;

    public FakeCredentialStore Raw { get; } = new();

    /// <summary>The raw store the engine and the gated decorator use (default: <see cref="Raw"/>).</summary>
    public IServerCredentialStore RawStore { get; }

    public GatedCredentialStore Credentials { get; private set; } = null!;

    public ServerProfileService Profiles { get; private set; } = null!;

    public FileSettingsParticipant Settings { get; private set; } = null!;

    public FakeMonitoring Monitoring { get; } = new();

    public CapturingLogger Log { get; } = new();

    public string JournalDirectory => Path.Combine(DataDirectory, "restore-journal");

    public string NotificationPath => Path.Combine(DataDirectory, "notification-settings.json");

    public string BackgroundPath => Path.Combine(DataDirectory, "background-settings.json");

    public IEnumerable<string> ParticipantPaths =>
    [
        TrustOptions.FilePath, RoutedTrustOptions.FilePath, ServerOptions.RoutedFilePath, ServerOptions.FilePath,
        NotificationPath, BackgroundPath,
    ];

    /// <summary>A fresh "process": new stores, gate and caches over the same files and credential store.</summary>
    public void Reopen()
    {
        Gate = new ConfigurationWriteGate();
        Repository = new JsonServerRepository(ServerOptions, NullLogger<JsonServerRepository>.Instance, Gate);
        Servers = new ServerService(Repository, new ServerValidator(), Gate);
        DirectTrust = new JsonHostKeyTrustStore(TrustOptions, NullLogger<JsonHostKeyTrustStore>.Instance, Gate);
        RoutedTrust = new JsonRoutedHostKeyTrustStore(RoutedTrustOptions, NullLogger<JsonRoutedHostKeyTrustStore>.Instance, Gate);
        Credentials = new GatedCredentialStore(RawStore, Gate);
        Profiles = new ServerProfileService(Servers, Credentials, Gate);
        Settings = new FileSettingsParticipant(NotificationPath, BackgroundPath, Gate);
    }

    public ConfigurationBackupService CreateService(
        Action<string>? faults = null,
        LocalKeyFileAccess? keyFiles = null,
        TimeSpan? drainTimeout = null,
        RestoreRecoveryStatus? recovery = null,
        BackupFileCodec? codec = null) =>
        new(
            Servers,
            new ServerValidator(),
            Repository,
            DirectTrust,
            RoutedTrust,
            new UngatedCredentialStore(RawStore),
            Settings,
            Gate,
            Monitoring,
            ServerOptions,
            TrustOptions,
            RoutedTrustOptions,
            new BackupServiceOptions { AppVersion = "1.2.0" },
            recovery ?? new RestoreRecoveryStatus(),
            Log.For<ConfigurationBackupService>())
        {
            Codec = codec ?? FastCodec,
            FaultInjector = faults,
            NewId = NextId,
            KeyFileAccess = keyFiles ?? NoKeyFilesAccess,
            GateDrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(10),
        };

    /// <summary>Production reader bounds, lowest accepted writer cost (0.6M iterations) to keep tests fast.</summary>
    public static BackupFileCodec FastCodec { get; } = new(new BackupFileCodecSeams { WriterIterations = BackupFileCodec.MinimumIterations });

    /// <summary>Every local key path "exists"; nothing touches the real disk.</summary>
    public static LocalKeyFileAccess NoKeyFilesAccess { get; } = new(
        _ => DriveType.Fixed,
        _ => FileAttributes.Directory,
        _ => throw new InvalidOperationException("never opened"),
        _ => new LocalSshKeyFileMetadata(LocalSshKeyPathKind.RegularFile, false, 100));

    // Deterministic, distinct fresh ids (and never equal to any seeded id: those use another prefix).
    private Guid NextId() => new($"f0000000-0000-0000-0000-{Interlocked.Increment(ref _nextId):D12}");

    // ---------------------------------------------------------------- seeding

    public static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");

    public static Server Direct(int id, string host) => new()
    {
        Id = Id(id),
        Name = "direct-" + id,
        Host = host,
        Port = 22,
        Username = "ops",
        AuthenticationMethod = AuthenticationMethod.Password,
        CredentialReferenceId = Id(1000 + id),
        RefreshIntervalSeconds = 30,
        CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
    };

    public static Server KeyServer(int id, string host, string keyPath) => Direct(id, host) with
    {
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = keyPath,
    };

    public static Server Routed(int id, string host, string jumpHost) => Direct(id, host) with
    {
        Name = "routed-" + id,
        Route = new ServerRoute
        {
            Jump = new JumpHop
            {
                Host = jumpHost,
                Port = 22,
                Username = "jump",
                AuthenticationMethod = AuthenticationMethod.SshKey,
                PrivateKeyPath = @"C:\keys\jump_" + id,
                CredentialReferenceId = Id(2000 + id),
            }
        }
    };

    public async Task SeedAsync(IReadOnlyList<Server> servers, bool withSecrets = true)
    {
        await Repository.SaveAllAsync(servers);
        if (!withSecrets)
        {
            return;
        }

        foreach (var reference in servers.SelectMany(ServerCredentialReferences.All))
        {
            Raw.Seed(reference, SecretMarker + reference.Kind + "-" + reference.ServerId.ToString("N")[^4..]);
        }
    }

    public static TrustedHostKey DirectTrustEntry(string host, int port = 22, byte seed = 1) => new()
    {
        Endpoint = SshEndpoint.Create(host, port),
        Identity = HostKeyIdentity.Create("ssh-ed25519", "SHA256:" + Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray()).TrimEnd('=')),
        ConfirmedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
    };

    public static TrustedRoutedHostKey RoutedTrustEntry(string via, string target, byte seed = 2) => new()
    {
        Route = SshRoute.Create(SshEndpoint.Create(via, 22), SshEndpoint.Create(target, 22)),
        Identity = HostKeyIdentity.Create("ssh-ed25519", "SHA256:" + Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray()).TrimEnd('=')),
        ConfirmedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
    };

    public void SeedTrust(IEnumerable<TrustedHostKey> direct, IEnumerable<TrustedRoutedHostKey> routed)
    {
        File.WriteAllBytes(TrustOptions.FilePath, JsonHostKeyTrustStore.Render(direct));
        File.WriteAllBytes(RoutedTrustOptions.FilePath, JsonRoutedHostKeyTrustStore.Render(routed));
        Reopen();
    }

    public void SeedSettings(bool notifications, bool background, bool noticeShown)
    {
        File.WriteAllText(NotificationPath, $"{{\"notificationsEnabled\":{Json(notifications)}}}");
        File.WriteAllText(BackgroundPath, $"{{\"backgroundMonitoringEnabled\":{Json(background)},\"backgroundNoticeShown\":{Json(noticeShown)}}}");
    }

    private static string Json(bool value) => value ? "true" : "false";

    public string OutPath(string name = "backup.serveralyzer-backup") => Path.Combine(OutDirectory, name);

    // ---------------------------------------------------------------- state assertions

    /// <summary>SHA-256 of every participant file (or "absent") plus the whole credential store.</summary>
    public string Snapshot()
    {
        var builder = new StringBuilder();
        foreach (var path in ParticipantPaths)
        {
            builder.Append(Path.GetFileName(path)).Append('=')
                .Append(File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent")
                .Append('\n');
        }

        builder.Append(Raw.Dump());
        return builder.ToString();
    }

    /// <summary>No secret marker and no passphrase in logs, in the temp tree (data, journal, output) — as
    /// UTF-8 and UTF-16LE (spec §9 "No secret in logs / temp").</summary>
    public void AssertNoSecretsLeaked()
    {
        var needles = new[] { SecretMarker, Passphrase };
        foreach (var entry in Log.Entries)
        {
            foreach (var needle in needles)
            {
                Assert.DoesNotContain(needle, entry, StringComparison.Ordinal);
            }
        }

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var needle in needles)
            {
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(needle)), $"UTF-8 marker in {Path.GetFileName(file)}");
                Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(needle)), $"UTF-16 marker in {Path.GetFileName(file)}");
            }
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakeCredentialStore : IServerCredentialStore
{
    private readonly ConcurrentDictionary<CredentialReference, string> _secrets = new();

    public Func<CredentialReference, int, Exception?>? WriteFault { get; set; }

    public Func<CredentialReference, Exception?>? ReadFault { get; set; }

    public Func<CredentialReference, Exception?>? DeleteFault { get; set; }

    public int Writes { get; private set; }

    public void Seed(CredentialReference reference, string secret) => _secrets[reference] = secret;

    public bool Contains(CredentialReference reference) => _secrets.ContainsKey(reference);

    public string? Get(CredentialReference reference) => _secrets.GetValueOrDefault(reference);

    public IReadOnlyCollection<CredentialReference> References => _secrets.Keys.ToList();

    public string Dump() => string.Join(
        "\n",
        _secrets.OrderBy(pair => pair.Key.ReferenceId).Select(pair =>
            $"{pair.Key.ServerId:N}/{pair.Key.Kind}/{pair.Key.ReferenceId:N}={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value)))}"));

    public Task WriteAsync(CredentialReference reference, SecretValue secret, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WriteFault?.Invoke(reference, Writes) is { } fault)
        {
            throw fault;
        }

        Writes++;
        _secrets[reference] = new string(secret.Reveal());
        return Task.CompletedTask;
    }

    public Task<SecretValue?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        if (ReadFault?.Invoke(reference) is { } fault)
        {
            throw fault;
        }

        return Task.FromResult(_secrets.TryGetValue(reference, out var value) ? new SecretValue(value) : null);
    }

    public Task<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        if (DeleteFault?.Invoke(reference) is { } fault)
        {
            throw fault;
        }

        return Task.FromResult(_secrets.TryRemove(reference, out _));
    }
}

/// <summary>The App's two settings files with the App's semantics (defaults true / notice false; restore keeps
/// the target's notice flag), writing only through the gate's restore token.</summary>
internal sealed class FileSettingsParticipant(string notificationPath, string backgroundPath, IConfigurationWriteGate gate)
    : IPortableSettingsParticipant
{
    public string NotificationSettingsFilePath => notificationPath;

    public string BackgroundSettingsFilePath => backgroundPath;

    public PortableSettings ReadCurrent() =>
        ReadNotificationAndBackground(DurableRead(notificationPath), DurableRead(backgroundPath));

    public byte[] RenderNotificationSettings(PortableSettings settings) =>
        Encoding.UTF8.GetBytes($"{{\"notificationsEnabled\":{(settings.NotificationsEnabled ? "true" : "false")}}}");

    public byte[] RenderBackgroundSettings(PortableSettings settings)
    {
        var noticeShown = Read(DurableRead(backgroundPath), "backgroundNoticeShown", false);
        return Encoding.UTF8.GetBytes(
            $"{{\"backgroundMonitoringEnabled\":{(settings.BackgroundMonitoringEnabled ? "true" : "false")},\"backgroundNoticeShown\":{(noticeShown ? "true" : "false")}}}");
    }

    public PortableSettings ReadNotificationAndBackground(byte[]? notificationBytes, byte[]? backgroundBytes) =>
        new(Read(notificationBytes, "notificationsEnabled", true), Read(backgroundBytes, "backgroundMonitoringEnabled", true));

    public void ReplaceNotificationSettings(RestoreWriteToken token, byte[]? content) => Replace(token, notificationPath, content);

    public void ReplaceBackgroundSettings(RestoreWriteToken token, byte[]? content) => Replace(token, backgroundPath, content);

    public Action<string>? BeforeReplace { get; set; }

    private void Replace(RestoreWriteToken token, string path, byte[]? content)
    {
        gate.EnsureHeldBy(token);
        BeforeReplace?.Invoke(path);
        if (content is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllBytes(path, content);
        }
    }

    private static byte[]? DurableRead(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static bool Read(byte[]? bytes, string name, bool fallback)
    {
        if (bytes is null)
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}

internal sealed class FakeMonitoring : IRestoreMonitoringControl
{
    public int Stops { get; private set; }

    public int Resumes { get; private set; }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stops++;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        Resumes++;
        return Task.CompletedTask;
    }
}

/// <summary>Captures every log line (formatted message plus each structured value).</summary>
internal sealed class CapturingLogger
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries.ToArray();

    public ILogger<T> For<T>() => new Typed<T>(this);

    private sealed class Typed<T>(CapturingLogger owner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = new StringBuilder(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    line.Append(" | ").Append(key).Append('=').Append(value);
                }
            }

            if (exception is not null)
            {
                line.Append(" | exception=").Append(exception);
            }

            owner._entries.Enqueue(line.ToString());
        }
    }
}
