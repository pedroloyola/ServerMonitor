using System.Text;
using Renci.SshNet;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.Docker;
using ServerMonitor.Infrastructure.Collectors.Launchd;
using ServerMonitor.Infrastructure.Collectors.Linux;
using ServerMonitor.Infrastructure.Collectors.MacOS;
using ServerMonitor.Infrastructure.Collectors.Systemd;
using ServerMonitor.Infrastructure.Collectors.Workloads;

namespace ServerMonitor.Infrastructure.SSH;

internal sealed class SshNetSession(
    ConnectionInfo connectionInfo,
    Renci.SshNet.AuthenticationMethod authentication,
    IDisposable? authenticationResource) : ISshSession
{
    private const int DefaultOutputLimit = 256 * 1024;
    private const int SmallOutputLimit = 16 * 1024;
    private const int ErrorOutputLimit = 16 * 1024;

    // Workload inventories (docker ps, systemctl, launchctl print) can be larger than a metric source.
    private const int WorkloadListOutputLimit = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SshClient _client = new(connectionInfo);
    private bool _disposed;

    public Task<SshSessionResult> ConnectAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken) =>
        RunAsync(SessionOperation.Connect, hostKeyVerifier, TimeSpan.Zero, default, cancellationToken);

    public Task<SshSessionResult> DetectOperatingSystemAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken) =>
        RunAsync(SessionOperation.DetectOperatingSystem, hostKeyVerifier, TimeSpan.Zero, default, cancellationToken);

    public Task<SshSessionResult> CollectLinuxMetricsAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        TimeSpan cpuSampleInterval,
        CancellationToken cancellationToken) =>
        RunAsync(SessionOperation.CollectLinuxMetrics, hostKeyVerifier, cpuSampleInterval, default, cancellationToken);

    public Task<SshSessionResult> CollectMacOsMetricsAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken) =>
        RunAsync(SessionOperation.CollectMacOsMetrics, hostKeyVerifier, TimeSpan.Zero, default, cancellationToken);

    public Task<SshSessionResult> CollectWorkloadsAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        WorkloadCollectionPlan plan,
        CancellationToken cancellationToken) =>
        RunAsync(SessionOperation.CollectWorkloads, hostKeyVerifier, TimeSpan.Zero, plan, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _client.Dispose();
        authentication.Dispose();
        authenticationResource?.Dispose();
        _disposed = true;
    }

    private async Task<SshSessionResult> RunAsync(
        SessionOperation operation,
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        TimeSpan cpuSampleInterval,
        WorkloadCollectionPlan workloadPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);
        ObjectDisposedException.ThrowIf(_disposed, this);

        HostKeyIdentity? presentedHostKey = null;
        var hostKeyWasRejected = false;

        void OnHostKeyReceived(object? sender, Renci.SshNet.Common.HostKeyEventArgs args)
        {
            args.CanTrust = false;
            try
            {
                presentedHostKey = HostKeyIdentity.Create(
                    args.HostKeyName,
                    $"SHA256:{args.FingerPrintSHA256}");
                args.CanTrust = hostKeyVerifier(presentedHostKey);
                hostKeyWasRejected = !args.CanTrust;
            }
            catch
            {
                args.CanTrust = false;
                hostKeyWasRejected = true;
            }
        }

        _client.HostKeyReceived += OnHostKeyReceived;
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var detectedOperatingSystem = ServerOperatingSystem.Unknown;
            LinuxMetricsRawData? linuxMetrics = null;
            MacOsMetricsRawData? macOsMetrics = null;
            WorkloadRawData? workloads = null;
            if (operation == SessionOperation.DetectOperatingSystem)
            {
                var uname = await TryExecuteCommandAsync(
                        "uname -s",
                        SmallOutputLimit,
                        cancellationToken)
                    .ConfigureAwait(false);
                detectedOperatingSystem = SshOperatingSystemParser.ParseUname(uname);
            }
            else if (operation == SessionOperation.CollectLinuxMetrics)
            {
                linuxMetrics = await CollectLinuxDataAsync(cpuSampleInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (operation == SessionOperation.CollectMacOsMetrics)
            {
                macOsMetrics = await CollectMacOsDataAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (operation == SessionOperation.CollectWorkloads)
            {
                (workloads, detectedOperatingSystem) = await CollectWorkloadsDataAsync(
                        workloadPlan,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new SshSessionResult
            {
                ErrorCode = SshConnectionErrorCode.None,
                PresentedHostKey = presentedHostKey,
                DetectedOperatingSystem = detectedOperatingSystem,
                LinuxMetrics = linuxMetrics,
                MacOsMetrics = macOsMetrics,
                Workloads = workloads
            };
        }
        catch (Exception exception)
        {
            return new SshSessionResult
            {
                ErrorCode = hostKeyWasRejected
                    ? SshConnectionErrorCode.HostKeyMismatch
                    : SshExceptionMapper.Map(exception),
                PresentedHostKey = presentedHostKey,
                ExceptionType = exception.GetType().Name
            };
        }
        finally
        {
            _client.HostKeyReceived -= OnHostKeyReceived;
            if (_client.IsConnected)
            {
                try
                {
                    _client.Disconnect();
                }
                catch
                {
                    // A best-effort disconnect must not replace the operation result.
                }
            }
        }
    }

    private async Task<LinuxMetricsRawData> CollectLinuxDataAsync(
        TimeSpan cpuSampleInterval,
        CancellationToken cancellationToken)
    {
        var firstCpuStat = await TryExecuteCommandAsync(
                LinuxMetricsCommandCatalog.CpuStat,
                DefaultOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);

        await Task.Delay(cpuSampleInterval, cancellationToken).ConfigureAwait(false);

        var secondCpuStat = await TryExecuteCommandAsync(
                LinuxMetricsCommandCatalog.CpuStat,
                DefaultOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var memInfo = await TryExecuteCommandAsync(
                LinuxMetricsCommandCatalog.MemInfo,
                DefaultOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var rootFileSystem = await TryExecuteCommandAsync(
                LinuxMetricsCommandCatalog.RootFileSystem,
                DefaultOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var uptime = await TryExecuteCommandAsync(
                LinuxMetricsCommandCatalog.Uptime,
                SmallOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var hostname = await TryExecuteCommandAsync(
                LinuxMetricsCommandCatalog.Hostname,
                SmallOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var osRelease = await TryExecuteCommandAsync(
                LinuxMetricsCommandCatalog.OsRelease,
                DefaultOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);

        return new LinuxMetricsRawData
        {
            FirstCpuStat = firstCpuStat,
            SecondCpuStat = secondCpuStat,
            MemInfo = memInfo,
            RootFileSystem = rootFileSystem,
            Uptime = uptime,
            Hostname = hostname,
            OsRelease = osRelease
        };
    }

    private async Task<MacOsMetricsRawData> CollectMacOsDataAsync(CancellationToken cancellationToken)
    {
        // top -l 2 self-samples over ~1s; no external delay is required. All
        // commands run in this single authenticated session.
        var cpuTop = await TryExecuteCommandAsync(
                MacOsMetricsCommandCatalog.CpuTop,
                DefaultOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var vmStat = await TryExecuteCommandAsync(
                MacOsMetricsCommandCatalog.VmStat,
                DefaultOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var physicalMemory = await TryExecuteCommandAsync(
                MacOsMetricsCommandCatalog.PhysicalMemory,
                SmallOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var rootFileSystem = await TryExecuteCommandAsync(
                MacOsMetricsCommandCatalog.RootFileSystem,
                DefaultOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var bootTime = await TryExecuteCommandAsync(
                MacOsMetricsCommandCatalog.BootTime,
                SmallOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var hostname = await TryExecuteCommandAsync(
                MacOsMetricsCommandCatalog.Hostname,
                SmallOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var swVers = await TryExecuteCommandAsync(
                MacOsMetricsCommandCatalog.SwVers,
                SmallOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);

        return new MacOsMetricsRawData
        {
            CpuTop = cpuTop,
            VmStat = vmStat,
            PhysicalMemory = physicalMemory,
            RootFileSystem = rootFileSystem,
            BootTime = bootTime,
            Hostname = hostname,
            SwVers = swVers
        };
    }

    private async Task<(WorkloadRawData Data, ServerOperatingSystem EffectiveOperatingSystem)>
        CollectWorkloadsDataAsync(
            WorkloadCollectionPlan plan,
            CancellationToken cancellationToken)
    {
        // Resolve the effective OS inside this trusted, already-authenticated session (no extra SSH
        // session). A server configured as Auto/Unknown is detected with the same read-only `uname -s`
        // used by M3 host detection; a concrete configured OS is trusted as-is. The service commands are
        // then selected from the effective OS. Mapping the OS to a ServiceManager stays in the Core policy.
        var effectiveOperatingSystem = plan.OperatingSystem;
        if (effectiveOperatingSystem is ServerOperatingSystem.Auto or ServerOperatingSystem.Unknown)
        {
            var uname = await TryExecuteCommandAsync("uname -s", SmallOutputLimit, cancellationToken)
                .ConfigureAwait(false);
            effectiveOperatingSystem = SshOperatingSystemParser.ParseUname(uname);
        }

        var includeSystemd = effectiveOperatingSystem == ServerOperatingSystem.Linux;
        var includeLaunchd = effectiveOperatingSystem == ServerOperatingSystem.MacOS;

        RemoteCommandOutcome? dockerVersion = null;
        RemoteCommandOutcome? dockerPs = null;
        RemoteCommandOutcome? systemdListUnits = null;
        RemoteCommandOutcome? systemdUnitFiles = null;
        RemoteCommandOutcome? launchdPrintSystem = null;

        if (plan.IncludeDocker)
        {
            dockerVersion = await ExecuteDetailedAsync(
                    DockerCommandCatalog.Version,
                    SmallOutputLimit,
                    cancellationToken)
                .ConfigureAwait(false);

            // Only list containers when the daemon answered the version probe; otherwise the availability
            // is already decided from the probe's exit/stderr and ps would just fail the same way.
            if (dockerVersion.ExitStatus == 0)
            {
                dockerPs = await ExecuteDetailedAsync(
                        DockerCommandCatalog.ContainerList,
                        WorkloadListOutputLimit,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (includeSystemd)
        {
            systemdListUnits = await ExecuteDetailedAsync(
                    SystemdCommandCatalog.ListUnits,
                    WorkloadListOutputLimit,
                    cancellationToken)
                .ConfigureAwait(false);

            // Enablement is a second command; skip it if the manager did not answer list-units.
            if (systemdListUnits.ExitStatus == 0)
            {
                systemdUnitFiles = await ExecuteDetailedAsync(
                        SystemdCommandCatalog.ListUnitFiles,
                        WorkloadListOutputLimit,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (includeLaunchd)
        {
            launchdPrintSystem = await ExecuteDetailedAsync(
                    LaunchdCommandCatalog.PrintSystem,
                    WorkloadListOutputLimit,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var data = new WorkloadRawData
        {
            DockerVersion = dockerVersion,
            DockerPs = dockerPs,
            SystemdListUnits = systemdListUnits,
            SystemdListUnitFiles = systemdUnitFiles,
            LaunchdPrintSystem = launchdPrintSystem
        };

        return (data, effectiveOperatingSystem);
    }

    /// <summary>
    /// Runs one catalog command and captures its exit status plus bounded, strictly-decoded stdout and
    /// stderr. Unlike <see cref="TryExecuteCommandAsync"/> (stdout-only), this keeps the exit/stderr that
    /// the pure availability classifier needs. Oversized stdout is reported via
    /// <see cref="RemoteCommandOutcome.OutputExceededLimit"/> without exposing the raw output.
    /// </summary>
    private async Task<RemoteCommandOutcome> ExecuteDetailedAsync(
        string commandText,
        int outputLimit,
        CancellationToken cancellationToken)
    {
        using var command = _client.CreateCommand(commandText);
        command.CommandTimeout = connectionInfo.Timeout;
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var executeTask = command.ExecuteAsync(commandCancellation.Token);
        var outputTask = BoundedRemoteOutputReader.ReadAsync(
            command.OutputStream,
            outputLimit,
            commandCancellation.Token);
        var errorTask = BoundedRemoteOutputReader.ReadAsync(
            command.ExtendedOutputStream,
            ErrorOutputLimit,
            commandCancellation.Token);

        try
        {
            await Task.WhenAll(executeTask, outputTask, errorTask).ConfigureAwait(false);
            return new RemoteCommandOutcome
            {
                WasExecuted = true,
                ExitStatus = command.ExitStatus,
                StandardOutput = Decode(await outputTask.ConfigureAwait(false)),
                StandardError = Decode(await errorTask.ConfigureAwait(false)),
                OutputExceededLimit = false
            };
        }
        catch (RemoteOutputLimitException)
        {
            commandCancellation.Cancel();
            try
            {
                await Task.WhenAll(executeTask, outputTask, errorTask).ConfigureAwait(false);
            }
            catch
            {
                // Draining after an oversized-output cancellation must not replace the outcome.
            }

            return new RemoteCommandOutcome
            {
                WasExecuted = true,
                ExitStatus = command.ExitStatus,
                StandardOutput = null,
                StandardError = null,
                OutputExceededLimit = true
            };
        }
    }

    private static string? Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private async Task<string?> TryExecuteCommandAsync(
        string commandText,
        int outputLimit,
        CancellationToken cancellationToken)
    {
        using var command = _client.CreateCommand(commandText);
        command.CommandTimeout = connectionInfo.Timeout;
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var executeTask = command.ExecuteAsync(commandCancellation.Token);
        var outputTask = BoundedRemoteOutputReader.ReadAsync(
            command.OutputStream,
            outputLimit,
            commandCancellation.Token);
        var errorTask = BoundedRemoteOutputReader.ReadAsync(
            command.ExtendedOutputStream,
            ErrorOutputLimit,
            commandCancellation.Token);

        try
        {
            await Task.WhenAll(executeTask, outputTask, errorTask).ConfigureAwait(false);
            if (command.ExitStatus != 0)
            {
                return null;
            }

            return StrictUtf8.GetString(await outputTask.ConfigureAwait(false));
        }
        catch (RemoteOutputLimitException)
        {
            commandCancellation.Cancel();
            try
            {
                await Task.WhenAll(executeTask, outputTask, errorTask).ConfigureAwait(false);
            }
            catch
            {
                // Oversized output is an unavailable individual source. Do not
                // expose the remote output or replace it with a fabricated zero.
            }

            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private enum SessionOperation
    {
        Connect,
        DetectOperatingSystem,
        CollectLinuxMetrics,
        CollectMacOsMetrics,
        CollectWorkloads
    }
}
