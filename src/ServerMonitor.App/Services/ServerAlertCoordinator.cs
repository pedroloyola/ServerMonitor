using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Alerts;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Services;

/// <summary>
/// Converts M6 monitoring-state transitions into privacy-preserving local notifications.
/// The state event handler performs only a synchronous snapshot/policy decision and queues
/// an immutable intent; platform notification work runs outside the producer thread.
/// </summary>
public sealed class ServerAlertCoordinator : IServerAlertCoordinator, IAsyncDisposable
{
    internal static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

    private readonly IServerMonitoringStateStore _stateStore;
    private readonly IServerService _serverService;
    private readonly INotificationSettingsService _settings;
    private readonly IUserNotificationService _notificationService;
    private readonly ILocalizationService _localization;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ServerAlertCoordinator> _logger;
    private readonly TimeSpan _cooldown;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ServerMonitoringState> _baselines = [];
    private readonly Dictionary<(Guid ServerId, ServerAlertCategory Category), DateTimeOffset> _lastAlerts = [];

    private Channel<CoordinatorWork>? _intents;
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private int _settingsGeneration;
    private bool _started;
    private bool _accepting;
    private bool _disposed;

    public ServerAlertCoordinator(
        IServerMonitoringStateStore stateStore,
        IServerService serverService,
        INotificationSettingsService settings,
        IUserNotificationService notificationService,
        ILocalizationService localization,
        ILogger<ServerAlertCoordinator> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? cooldown = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _serverService = serverService ?? throw new ArgumentNullException(nameof(serverService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cooldown = cooldown ?? DefaultCooldown;
        if (_cooldown <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return Task.CompletedTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _baselines.Clear();
            foreach (var state in _stateStore.GetAll())
            {
                _baselines[state.ServerId] = state;
            }

            _intents = Channel.CreateUnbounded<CoordinatorWork>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _lifetime = new CancellationTokenSource();
            _worker = ProcessIntentsAsync(_intents.Reader, _lifetime.Token);
            _stateStore.StateChanged += OnStateChanged;
            _settings.NotificationsEnabledChanged += OnNotificationsEnabledChanged;
            _started = true;
            _accepting = true;
        }

        _logger.LogInformation("Server alert coordinator started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Channel<CoordinatorWork>? intents;
        Task? worker;
        CancellationTokenSource? lifetime;

        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            if (_accepting)
            {
                _accepting = false;
                _stateStore.StateChanged -= OnStateChanged;
                _settings.NotificationsEnabledChanged -= OnNotificationsEnabledChanged;
            }
            intents = _intents;
            worker = _worker;
            lifetime = _lifetime;
            _intents = null;
            _worker = null;
            _lifetime = null;
            intents?.Writer.TryComplete();
            lifetime?.Cancel();
        }

        try
        {
            if (worker is not null)
            {
                await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's shutdown bound elapsed; the lifetime token above has already
            // fenced callbacks and the worker is unwinding.
        }
        finally
        {
            lifetime?.Cancel();
            lifetime?.Dispose();
        }

        _logger.LogInformation("Server alert coordinator stopped.");
    }

    public void BeginShutdown()
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            if (!_started || !_accepting)
            {
                return;
            }

            _accepting = false;
            _settingsGeneration++;
            _stateStore.StateChanged -= OnStateChanged;
            _settings.NotificationsEnabledChanged -= OnNotificationsEnabledChanged;
            _intents?.Writer.TryComplete();
            lifetime = _lifetime;
        }

        // Fence the platform boundary before cancelling/draining the alert worker. If a
        // delivery raced past policy evaluation, the platform rejects it synchronously.
        _notificationService.BeginShutdown();
        lifetime?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Deterministic test seam that completes after all earlier queued intents.</summary>
    internal Task FlushAsync()
    {
        lock (_gate)
        {
            if (!_started || !_accepting || _intents is null)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_intents.Writer.TryWrite(new FlushWork(completion)))
            {
                completion.TrySetResult();
            }

            return completion.Task;
        }
    }

    private void OnStateChanged(object? sender, Guid serverId)
    {
        if (!_stateStore.TryGet(serverId, out var snapshot))
        {
            lock (_gate)
            {
                _baselines.Remove(serverId);
                foreach (var key in _lastAlerts.Keys
                             .Where(candidate => candidate.ServerId == serverId)
                             .ToArray())
                {
                    _lastAlerts.Remove(key);
                }
            }

            return;
        }

        AlertIntent? intent = null;
        lock (_gate)
        {
            if (!_started || !_accepting || _intents is null)
            {
                return;
            }

            if (!_baselines.TryGetValue(serverId, out var previous))
            {
                _baselines[serverId] = snapshot;
                return;
            }

            _baselines[serverId] = snapshot;
            var decision = ServerAlertPolicy.Evaluate(previous.Health, snapshot.Health);
            if (decision is null || !_settings.NotificationsEnabled)
            {
                return;
            }

            intent = new AlertIntent(serverId, decision, _settingsGeneration);
            _intents.Writer.TryWrite(intent);
        }
    }

    private void OnNotificationsEnabledChanged(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            // Every preference change fences intents evaluated under the earlier setting.
            // Baselines remain current because monitoring observations continue normally.
            _settingsGeneration++;
        }
    }

    private async Task ProcessIntentsAsync(ChannelReader<CoordinatorWork> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var intent in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (intent is AlertIntent alert)
                {
                    await ProcessIntentAsync(alert, cancellationToken).ConfigureAwait(false);
                }
                else if (intent is FlushWork flush)
                {
                    flush.Completion.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal lifecycle cancellation.
        }
        finally
        {
            while (reader.TryRead(out var remaining))
            {
                if (remaining is FlushWork flush)
                {
                    flush.Completion.TrySetCanceled(cancellationToken);
                }
            }
        }
    }

    private async Task ProcessIntentAsync(AlertIntent intent, CancellationToken cancellationToken)
    {
        try
        {
            if (!CanDeliver(intent))
            {
                return;
            }

            // Resolve against configured servers at delivery time. This deliberately includes
            // hidden servers and excludes discovery-only records, which never enter IServerService.
            var servers = await _serverService.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var server = servers.FirstOrDefault(candidate => candidate.Id == intent.ServerId);
            if (server is null)
            {
                return;
            }

            if (!CanDeliver(intent))
            {
                return;
            }

            var alertKey = (intent.ServerId, intent.Decision.Category);
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                if (!IsPriorityEscalation(intent.Decision) &&
                    _lastAlerts.TryGetValue(alertKey, out var lastAlert) &&
                    now - lastAlert < _cooldown)
                {
                    _logger.LogDebug(
                        "Suppressed {Category} notification for server {ServerId} by cooldown.",
                        intent.Decision.Category,
                        intent.ServerId);
                    return;
                }
            }

            var notification = CreateNotification(server.Name, intent);
            await _notificationService.ShowAsync(notification, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                // Reserve cooldown only after the notification boundary completed. An intent
                // fenced by a settings change, a removed server, cancellation or a failed
                // delivery must not suppress the next real transition.
                _lastAlerts[alertKey] = _timeProvider.GetUtcNow();
            }
            _logger.LogDebug(
                "Sent {Category} notification for server {ServerId}.",
                intent.Decision.Category,
                intent.ServerId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal lifecycle cancellation.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to show {Category} notification for server {ServerId}.",
                intent.Decision.Category,
                intent.ServerId);
        }
    }

    private bool CanDeliver(AlertIntent intent)
    {
        lock (_gate)
        {
            return _started &&
                _accepting &&
                _settings.NotificationsEnabled &&
                intent.SettingsGeneration == _settingsGeneration;
        }
    }

    private static bool IsPriorityEscalation(ServerAlertDecision decision) =>
        decision.CurrentHealth == ServerHealth.Offline ||
        decision.CurrentHealth == ServerHealth.Critical &&
        decision.PreviousHealth is ServerHealth.Healthy or ServerHealth.Warning;

    private UserNotification CreateNotification(string serverName, AlertIntent intent)
    {
        var safeName = NotificationPresentationSanitizer.SanitizeServerName(
            serverName,
            _localization.GetString("NotificationServerFallbackName"));
        var (titleKey, bodyKey) = intent.Decision.Category switch
        {
            ServerAlertCategory.Warning => ("NotificationWarningTitle", "NotificationWarningBodyFormat"),
            ServerAlertCategory.Critical => ("NotificationCriticalTitle", "NotificationCriticalBodyFormat"),
            ServerAlertCategory.Offline => ("NotificationOfflineTitle", "NotificationOfflineBodyFormat"),
            ServerAlertCategory.Recovery when intent.Decision.PreviousHealth == ServerHealth.Offline =>
                ("NotificationRecoveryTitle", "NotificationRecoveryOnlineBodyFormat"),
            ServerAlertCategory.Recovery => ("NotificationHealthyTitle", "NotificationHealthyBodyFormat"),
            _ => throw new ArgumentOutOfRangeException()
        };

        return new UserNotification(
            intent.ServerId,
            intent.Decision.Category,
            _localization.GetString(titleKey),
            string.Format(_localization.GetString(bodyKey), safeName));
    }

    private abstract record CoordinatorWork;

    private sealed record AlertIntent(
        Guid ServerId,
        ServerAlertDecision Decision,
        int SettingsGeneration) : CoordinatorWork;

    private sealed record FlushWork(TaskCompletionSource Completion) : CoordinatorWork;
}
