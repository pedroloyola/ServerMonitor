using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Probes;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// M16.1: every probe the scheduler leaves out is logged once at startup, by position and reason, and never with its
/// command.
/// </summary>
public sealed class RemoteProbeCompositionTests
{
    private const string Decoy = "cat /srv/decoy-7f3a-secret-path";

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception) + " " + state + " " + exception));
    }

    private sealed class Probe(string id, string command) : IRemoteProbe
    {
        public string Id { get; } = id;

        public ServerOperatingSystem OperatingSystem => ServerOperatingSystem.Linux;

        public string Command { get; } = command;

        public int OutputLimitBytes => 1024;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public TimeSpan MinimumInterval => TimeSpan.Zero;

        public void Accept(RemoteProbeOutcome outcome)
        {
        }
    }

    [Fact]
    public void Each_rejected_probe_is_logged_once_as_a_warning_without_its_command()
    {
        var logger = new RecordingLogger();
        var probes = new IRemoteProbe[]
        {
            new Probe("good", "uptime"),
            new Probe("carriage", Decoy + "\r\nuptime"),                          // CR: rejected
            new Probe("Bad Id", Decoy),                                             // id: rejected
            new Probe("long", Decoy + new string('x', RemoteProbeLimits.MaxCommandLength)) // too long: rejected
        };

        var scheduler = RemoteProbeComposition.CreateScheduler(probes, new HistoryGeneration(), logger);

        Assert.Equal(1, scheduler.Count);
        Assert.Equal(3, scheduler.Rejected.Count);
        var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, warning => Assert.StartsWith("A remote probe was left out: probe #", warning.Message, StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Message.Contains("probe #1:", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Message.Contains("probe #2:", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Message.Contains("probe #3:", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("decoy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("/srv/", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_is_logged_when_every_probe_is_accepted()
    {
        var logger = new RecordingLogger();

        RemoteProbeComposition.CreateScheduler([new Probe("good", "uptime")], new HistoryGeneration(), logger);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void The_app_builds_its_scheduler_through_the_logging_composition()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var app = File.ReadAllText(Path.Combine(directory.FullName, "src", "ServerMonitor.App", "App.xaml.cs"));

        Assert.Contains("RemoteProbeComposition.CreateScheduler(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new RemoteProbeScheduler(", app, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<RemoteProbeWatch>();", app, StringComparison.Ordinal);
        Assert.Contains("sp.GetRequiredService<RemoteProbeWatch>()));", app, StringComparison.Ordinal);
    }

    private sealed class Read(string id, string command) : IRemoteRead
    {
        public string Id { get; } = id;

        public ServerOperatingSystem OperatingSystem => ServerOperatingSystem.Linux;

        public string Command { get; } = command;

        public RemoteReadArgumentKind ArgumentKind => RemoteReadArgumentKind.SystemdUnit;

        public int OutputLimitBytes => 1024;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
    }

    [Fact]
    public void Each_rejected_read_is_logged_once_as_a_warning_without_its_script()
    {
        var logger = new RecordingLogger();
        var reads = new IRemoteRead[]
        {
            new Read("good", "journalctl -u \"$1\""),
            new Read("no-arg", Decoy),                                  // no "$1": rejected
            new Read("two", Decoy + " \"$1\" \"$2\""),                  // $2: rejected
            new Read("probe.id", "journalctl -u \"$1\""),               // a probe's id: rejected
        };

        var registry = RemoteProbeComposition.CreateReadRegistry(reads, ["probe.id"], logger);

        Assert.Equal(1, registry.Count);
        var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, warning => Assert.StartsWith("A remote read was left out: read #", warning.Message, StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Message.Contains("read #1:", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Message.Contains("read #2:", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Message.Contains("read #3: a probe already has its id", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("decoy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("journalctl", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_is_logged_when_no_read_is_registered()
    {
        var logger = new RecordingLogger();

        var registry = RemoteProbeComposition.CreateReadRegistry([], [], logger);

        Assert.Equal(0, registry.Count);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void The_app_builds_its_read_runner_through_the_logging_composition_over_the_probe_ids()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var app = File.ReadAllText(Path.Combine(directory.FullName, "src", "ServerMonitor.App", "App.xaml.cs"));
        var composition = File.ReadAllText(Path.Combine(directory.FullName, "src", "ServerMonitor.App", "Services", "RemoteProbeComposition.cs"));

        Assert.Contains("services.AddSingleton<IRemoteReadRunner>(sp => RemoteProbeComposition.CreateReadRunner(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new RemoteReadRunner(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new RemoteReadRegistry(", app, StringComparison.Ordinal);
        Assert.Contains("CreateReadRegistry(reads, scheduler.ProbeIds, logger)", composition, StringComparison.Ordinal);
    }

    private sealed class PageProbe : IRemoteProbe
    {
        public string Id => "page";

        public ServerOperatingSystem OperatingSystem => ServerOperatingSystem.Linux;

        public string Command => "uptime";

        public int OutputLimitBytes => 1024;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public TimeSpan MinimumInterval => TimeSpan.Zero;

        public bool RunsOnlyWhileWatched => true;

        public void Accept(RemoteProbeOutcome outcome)
        {
        }
    }

    [Fact]
    public void The_watch_is_handed_to_the_scheduler()
    {
        var watch = new RemoteProbeWatch();
        var scheduler = RemoteProbeComposition.CreateScheduler([new PageProbe()], new HistoryGeneration(), new RecordingLogger(), watch);
        var server = Guid.NewGuid();

        using (watch.Watch(server, "page"))
        {
            var reservation = scheduler.TryReserve(server, CancellationToken.None)!;
            Assert.NotNull(reservation.Begin(ServerOperatingSystem.Linux));
            reservation.Complete();
        }

        var later = scheduler.TryReserve(server, CancellationToken.None)!;
        Assert.Null(later.Begin(ServerOperatingSystem.Linux));
        later.Complete();
    }
}
