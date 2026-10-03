using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.5 §3: the Server Detail page of one server. It COMPOSES the live <see cref="ServerCardViewModel"/> the dashboard
/// keeps up to date (never a copy, never a subclass) and exposes read-only presentation over it; every action passes
/// straight through to that card, so Atualizar, Editar (current modal until UI.7), Ocultar, Remover, Histórico and Serviços
/// e containers behave exactly as everywhere else.
/// <para>
/// UI.4 invariants kept: re-find by id after every list rebuild (an edit makes a new card instance); leave exactly once,
/// deferred, when the server is gone; shared InfoBars with the dashboard as the one source (SHOULD-3); MUST-1 (content
/// before Load) lives in the navigation; per visit, disposed with its page.
/// </para>
/// <para>
/// Update path: the card raises ~31 notifications per engine cycle. This view model maps them per property to dirty
/// groups and flushes ONCE per burst (one queued pass on the UI thread, like the overview), raising each affected
/// property once with cached arguments — never <c>OnPropertyChanged(string.Empty)</c>.
/// </para>
/// </summary>
public sealed class ServerDetailViewModel : ObservableObject, IDisposable
{
    private readonly DashboardViewModel _dashboard;
    private readonly INavigationService _navigation;
    private readonly ILocalizationService _localization;
    private readonly IServerHistoryQueryService? _history;
    private readonly ServerDetailReturnFocus? _returnFocus;
    private readonly ServersReturnNotice? _serversNotice;
    private readonly PresentationClock _clock;
    private readonly MonitoringThresholds _thresholds;
    private readonly Action _flushAction;
    private ServerCardViewModel? _card;
    private Guid _serverId;
    private bool _subscribed;
    private bool _left;
    private bool _disposed;
    private ServersNoticeKind? _exitOperation;
    private bool _exitOperationCompleted;
    private DateTimeOffset _readingNow;
    private Change _dirty;
    private bool _flushScheduled;
    private IReadOnlyList<double> _cpuPulse = [];
    private DateTimeOffset? _pulseAnchor;
    private CancellationTokenSource? _pulseCancellation;

    // Null in unit tests (no WinUI dispatcher): the exit and the flush then run inline.
    private readonly DispatcherQueue? _dispatcherQueue = TryGetDispatcher();

    public ServerDetailViewModel(
        DashboardViewModel dashboard,
        INavigationService navigation,
        ILocalizationService localization,
        IServerHistoryQueryService? history = null,
        ServerDetailReturnFocus? returnFocus = null,
        MonitoringOptions? monitoringOptions = null,
        PresentationClock? clock = null,
        ServersReturnNotice? serversNotice = null)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _history = history;
        _returnFocus = returnFocus;
        _serversNotice = serversNotice;
        _clock = clock ?? PresentationClock.System;
        // The same thresholds instance the engine is composed with (App registers one MonitoringOptions).
        _thresholds = (monitoringOptions ?? MonitoringOptions.Default).Thresholds;
        _flushAction = Flush;
        GoBackCommand = new RelayCommand(() => Leave(serverGone: false));
        RefreshCommand = new RelayCommand(
            () => _card?.RefreshMetricsCommand.Execute(null),
            () => _card?.RefreshMetricsCommand.CanExecute(null) == true);
        EditCommand = new RelayCommand(() => _card?.EditCommand.Execute(null), () => _card is not null);
        HideCommand = new AsyncRelayCommand(() => ExitingOperationAsync(_card?.HideCommand, ServersNoticeKind.Hidden), () => _card is not null);
        RemoveCommand = new AsyncRelayCommand(() => ExitingOperationAsync(_card?.RemoveCommand, ServersNoticeKind.Removed), () => _card is not null);
        ViewHistoryCommand = new RelayCommand(
            () => Explore(ServerDetailReturnTarget.History, _card?.ViewHistoryCommand), () => _card is not null);
        ViewWorkloadsCommand = new RelayCommand(
            () => Explore(ServerDetailReturnTarget.Workloads, _card?.ViewWorkloadsCommand), () => _card is not null);
        _dashboard.PropertyChanged += OnDashboardPropertyChanged;
    }

    // --- Shared InfoBars (SHOULD-3: one source, the dashboard) -------------------------------------------------------

    /// <summary>A failed Ocultar/Remover started here is reported HERE — the same error the Visão geral shows.</summary>
    public bool IsOperationErrorOpen
    {
        get => _dashboard.IsOperationErrorOpen;
        set => _dashboard.IsOperationErrorOpen = value;
    }

    public bool IsConfigurationLockedOpen
    {
        get => _dashboard.IsConfigurationLockedOpen;
        set => _dashboard.IsConfigurationLockedOpen = value;
    }

    public string ConfigurationLockedMessage => _dashboard.ConfigurationLockedMessage;

    /// <summary>The error notice's accessible name (UI.4 NIT-5): its title, so it is never announced unnamed.</summary>
    public string OperationErrorAutomationName => _localization.GetString("ServerOperationError.Title");

    // --- Identity / header ----------------------------------------------------------------------------------------

    public ServerDetailOrigin Origin { get; private set; }

    /// <summary>The hosted live card (null only before <see cref="Load"/> or after the server disappeared).</summary>
    public ServerCardViewModel? Card
    {
        get => _card;
        private set
        {
            if (ReferenceEquals(_card, value))
            {
                return;
            }

            if (_card is not null)
            {
                _card.PropertyChanged -= OnCardPropertyChanged;
            }

            _card = value;
            if (_card is not null && !_disposed)
            {
                _card.PropertyChanged += OnCardPropertyChanged;
            }

            OnPropertyChanged(nameof(Card));
            RequestCpuPulse(force: true);
            _dirty = Change.All;
            Flush();
            NotifyCommands();
        }
    }

    /// <summary>The H1 (UI.4 NIT-1): the server's name.</summary>
    public string Title => _card?.Name ?? string.Empty;

    /// <summary>The breadcrumb's parent: where the page was opened from (A-2: "Servidores" when none is known).</summary>
    public string ParentText => _localization.GetString(
        Origin == ServerDetailOrigin.Overview ? "OverviewPageTitle" : "ServersPageTitle");

    /// <summary>A-13: "host:port", IPv6 bracketed, the current Endpoint format.</summary>
    public string Address => _card is null ? string.Empty : OverviewPresentation.Endpoint(_card.Host, _card.Port);

    /// <summary>The configured system ("Linux" / "macOS"), the same localized value the rows show.</summary>
    public string ConfiguredSystemDisplay => _card?.OperatingSystemDisplayName ?? string.Empty;

    /// <summary>The detected system from the last snapshot ("Ubuntu 24.04 LTS"), or null — omitted, never invented.</summary>
    public string? DetectedSystemDisplay => ServerContextPresentation.OperatingSystemDisplay(_card?.MetricsSnapshot);

    /// <summary>Figma 112:1802 "Ubuntu 24.04 LTS · 192.168.1.10": the system shown in Ligação, then the address as the rows show it.</summary>
    public string HeaderSubtitle => _card is null
        ? string.Empty
        : ServerContextPresentation.Join(SystemDisplay, OverviewPresentation.Address(_card.Host, _card.Port));

    /// <summary>The detected system when the collector reported one, else the configured one (never invented).</summary>
    public string SystemDisplay => DetectedSystemDisplay ?? ConfiguredSystemDisplay;

    /// <summary>Ligação · Utilizador: the configured SSH user.</summary>
    public string Username => _card?.Server.Username ?? string.Empty;

    /// <summary>Ligação · Autenticação: the configured method (Chave SSH / Palavra-passe / Não configurada). Never a secret.</summary>
    public string AuthenticationDisplay => _localization.GetString(
        "ServerDetailAuthentication" + (_card?.Server.AuthenticationMethod ?? AuthenticationMethod.NotConfigured));

    /// <summary>Intervalo: the server's refresh interval ("30 segundos", "5 minutos").</summary>
    public string IntervalDisplay
    {
        get
        {
            var seconds = _card?.Server.RefreshIntervalSeconds ?? 0;
            return seconds >= 60 && seconds % 60 == 0
                ? Format(WorkloadPresentation.PluralKey("ServerDetailDurationMinutes", seconds / 60), seconds / 60)
                : Format(WorkloadPresentation.PluralKey("ServerDetailDurationSeconds", seconds), seconds);
        }
    }

    /// <summary>Tempo de atividade in Figma's long form ("12 dias, 8 horas"), or null when the collector has none.</summary>
    public string? UptimeLongDisplay => _card?.MetricsSnapshot?.Uptime is { } uptime ? LongDuration(uptime) : null;

    /// <summary>The info-strip value: the long uptime, or "—" when the collector reported none (never 0).</summary>
    public string UptimeDisplayOrDash => UptimeLongDisplay ?? Unavailable;

    /// <summary>The info-strip value: "Há 8 segundos", or "—" when there was never a reading.</summary>
    public string LastUpdatedOrDash => LastUpdatedValue ?? Unavailable;

    /// <summary>The status label: "A ligar…" while waiting for the first reading (Figma 112:16093), else the shared copy.</summary>
    public string StatusLabel => IsFirstReading ? _localization.GetString("ServerDetailConnecting") : StatusText;

    /// <summary>The connection-problem notice text: points to Editar, where trust and credentials are reviewed (M14 untouched).</summary>
    public string ConnectionProblemMessage => _localization.GetString("ServerDetailConnectionProblemMessage");

    public ServerHealth Health => _card?.Health ?? ServerHealth.Unknown;

    /// <summary>The shared status copy ("Sem ligação", …) — the same function the rows use. "Offline" is never shown.</summary>
    public string StatusText => ServerStatusPresentation.StatusText(Health, _localization);

    /// <summary>Live accessible summary (recomputed per burst; the FullCard's was computed once and went stale).</summary>
    public string AutomationSummary
    {
        get
        {
            if (_card is null)
            {
                return string.Empty;
            }

            var summary = Format(
                "ServerRowAutomationFormat",
                _card.Name,
                StatusText,
                AccessiblePercent(_card.HasCpuPercent, _card.CpuUsageValue),
                AccessiblePercent(_card.HasMemoryPercent, _card.MemoryUsageValue),
                AccessiblePercent(_card.HasDiskPercent, _card.DiskUsageValue),
                Address,
                ConfiguredSystemDisplay);
            return IsReadingStale ? string.Join(", ", summary, StaleText) : summary;
        }
    }

    // --- Action names carry the server (UI.4 a11y debt) -----------------------------------------------------------

    public string RefreshAutomationName => NameFor("ServerMetricsRefreshFor");

    public string EditAutomationName => NameFor("ServerDetailEditFor");

    public string MoreActionsAutomationName => NameFor("ServerCardMoreOptionsFor");

    public string HideAutomationName => NameFor("ServerDetailHideFor");

    public string RemoveAutomationName => NameFor("ServerDetailRemoveFor");

    public string ViewHistoryAutomationName => NameFor("ServerDetailHistoryFor");

    public string ViewWorkloadsAutomationName => NameFor("ServerDetailWorkloadsFor");

    // --- States ----------------------------------------------------------------------------------------------------

    /// <summary>Figma "Primeira leitura": a metrics-capable server still waiting for its first snapshot.</summary>
    public bool IsFirstReading => _card?.IsMetricsPending == true;

    public bool IsRefreshing => _card?.IsRefreshingMetrics == true;

    /// <summary>Windows / unknown OS: no metrics story — the header only, never a 0% card.</summary>
    public bool IsMetricsUnsupported => _card is not null && !_card.SupportsMetrics;

    /// <summary>A collection failed and there is no snapshot to fall back on.</summary>
    public bool HasCollectionError => _card?.HasMetricsError == true;

    public string? CollectionErrorText => _card?.MetricsErrorDisplay;

    /// <summary>"Sem ligação" (engine health Offline) — a derived notice, not a new state.</summary>
    public bool IsWithoutConnection => Health == ServerHealth.Offline;

    /// <summary>H-UI5-2: a retained reading is shown, marked stale (StaleAgeDisplay semantics), legible without colour.</summary>
    public bool IsReadingStale => _card is not null
        && ServerStatusPresentation.ShowsRetainedReadingAsStale(_card.Health, _card.IsStale, _card.HasMetrics);

    /// <summary>
    /// "Última atualização há N min", else the plain "Leitura desatualizada" text. Boss B2 decision (single source): the age
    /// is the SAME one the "Última atualização" value shows - one timestamp (the engine's last success) and one clock read
    /// per reading (ReadClock) - so the two never disagree at a minute boundary.
    /// </summary>
    public string? StaleText => IsReadingStale ? StaleAge() : null;

    public bool HasReading => _card?.HasMetrics == true;

    public bool HasUptime => _card?.HasUptime == true;

    public string? UptimeDisplay => _card?.UptimeDisplay;

    /// <summary>A-5: "Última atualização" — the label of the label + value pair.</summary>
    public string LastUpdatedLabel => _localization.GetString("ServerDetailLastUpdatedLabel");

    /// <summary>
    /// A-5 / D-UI3-9: the value ("há 8 s", "agora mesmo") from the engine's last success, recomputed only when a new
    /// reading arrives or the page loads — no timer. Null when there was never a reading.
    /// </summary>
    public string? LastUpdatedValue => _card?.LastSuccessAt is { } success ? FormatUpdatedAgo(success) : null;

    /// <summary>The page's single clock read for the reading it shows: taken when a reading arrives or the card changes.</summary>
    private void ReadClock() => _readingNow = _clock.UtcNow;

    private TimeSpan AgeOf(DateTimeOffset lastSuccessUtc) =>
        _readingNow - lastSuccessUtc is var age && age > TimeSpan.Zero ? age : TimeSpan.Zero;

    // The card's StaleAgeDisplay buckets (d / h / min), over the shared age. Under one minute (or with no last success) the
    // generic "Leitura desatualizada" is shown: a rounded-up "1 min" would contradict "Há 8 segundos" beside it.
    private string StaleAge()
    {
        if (_card?.LastSuccessAt is not { } success || AgeOf(success) is var age && age.TotalMinutes < 1)
        {
            return _localization.GetString("ServerDetailStaleReading");
        }

        return age.TotalDays >= 1 ? Format("ServerMetricsStaleDaysFormat", (int)age.TotalDays)
            : age.TotalHours >= 1 ? Format("ServerMetricsStaleHoursFormat", (int)age.TotalHours)
            : Format("ServerMetricsStaleMinutesFormat", (int)age.TotalMinutes);
    }

    // --- Connection (read-only; M14 semantics untouched) ------------------------------------------------------------

    public ServerConnectionState ConnectionState => _card?.ConnectionState ?? ServerConnectionState.NeverConnected;

    /// <summary>The existing localized connection-state name (also used by the editor). Nothing new is classified.</summary>
    public string ConnectionStateDisplay => _card?.ConnectionStateDisplayName ?? string.Empty;

    /// <summary>Authentication / host-key problems recorded by the connection-state store: shown as a notice, fixed in Editar.</summary>
    public bool HasConnectionProblem => ConnectionState is ServerConnectionState.AuthenticationFailed
        or ServerConnectionState.HostKeyUnknown
        or ServerConnectionState.HostKeyMismatch;

    /// <summary>Reached through a jump host (ProxyJump). Presence only — never its credentials, key paths or fingerprints.</summary>
    public bool IsRouted => _card?.Server.Route?.Jump is not null;

    // --- Metrics ---------------------------------------------------------------------------------------------------

    public bool HasCpuPercent => _card?.HasCpuPercent == true;

    /// <summary>"24%", or "—" when unknown — the rows' rule exactly (percent only, never bytes, never 0).</summary>
    public string CpuDisplay => Percent(HasCpuPercent, _card?.CpuUsageValue ?? 0);

    /// <summary>The big number of the card ("24"; the "%" unit is drawn only when known), or "—".</summary>
    public string CpuValueText => Number(HasCpuPercent, _card?.CpuUsageValue ?? 0);

    /// <summary>The whole card as one accessible sentence ("CPU: 24%").</summary>
    public string CpuAccessibleName => Format("ServerDetailMetricAccessibleFormat", _localization.GetString("ServerMetricsCpuLabel.Text"), CpuAccessibleValue);

    public string CpuAccessibleValue => AccessiblePercent(HasCpuPercent, _card?.CpuUsageValue ?? 0);

    public ServerHealth CpuSeverity => Severity(HasCpuPercent, _card?.CpuUsageValue, _thresholds.CpuWarning, _thresholds.CpuCritical);

    /// <summary>H-UI5-3: real CPU samples from local history (oldest → newest, ≤ 30). Empty draws no bars.</summary>
    public IReadOnlyList<double> CpuPulseSamples => _cpuPulse;

    public bool HasCpuPulse => _cpuPulse.Count > 0;

    public bool HasMemoryPercent => _card?.HasMemoryPercent == true;

    public string MemoryDisplay => Percent(HasMemoryPercent, _card?.MemoryUsageValue ?? 0);

    public string MemoryValueText => Number(HasMemoryPercent, _card?.MemoryUsageValue ?? 0);

    public string MemoryAccessibleName => Format("ServerDetailMetricAccessibleFormat", _localization.GetString("ServerMetricsMemoryLabel.Text"), MemoryAccessibleValue);

    /// <summary>Figma "9,9 GB de 16 GB": the snapshot's own byte counts, shown only when both are known (no % derived).</summary>
    public string? MemoryLegend => BytesOf(_card?.MetricsSnapshot?.MemoryUsedBytes, _card?.MetricsSnapshot?.MemoryTotalBytes);

    public string MemoryAccessibleValue => AccessiblePercent(HasMemoryPercent, _card?.MemoryUsageValue ?? 0);

    public ServerHealth MemorySeverity =>
        Severity(HasMemoryPercent, _card?.MemoryUsageValue, _thresholds.MemoryWarning, _thresholds.MemoryCritical);

    /// <summary>A-4: lit memory segments out of 28; null = unknown (empty track + "—").</summary>
    public int? MemoryLitSegments => MetricVisualPresentation.LitSegments(
        HasMemoryPercent ? _card!.MemoryUsageValue : null, MetricVisualPresentation.MemorySegmentCount);

    /// <summary>The meter's lit count: <see cref="MemoryLitSegments"/>, or -1 for unknown (an empty track).</summary>
    public int MemoryLitCount => MemoryLitSegments ?? -1;

    public bool HasDiskPercent => _card?.HasDiskPercent == true;

    public string DiskDisplay => Percent(HasDiskPercent, _card?.DiskUsageValue ?? 0);

    public string DiskValueText => Number(HasDiskPercent, _card?.DiskUsageValue ?? 0);

    public string DiskAccessibleName => Format("ServerDetailMetricAccessibleFormat", _localization.GetString("ServerMetricsDiskLabel.Text"), DiskAccessibleValue);

    public string? DiskLegend => BytesOf(_card?.MetricsSnapshot?.DiskUsedBytes, _card?.MetricsSnapshot?.DiskTotalBytes);

    public string DiskAccessibleValue => AccessiblePercent(HasDiskPercent, _card?.DiskUsageValue ?? 0);

    public ServerHealth DiskSeverity => Severity(HasDiskPercent, _card?.DiskUsageValue, _thresholds.DiskWarning, _thresholds.DiskCritical);

    /// <summary>A-4: lit disk segments out of 14; null = unknown (empty track + "—").</summary>
    public int? DiskLitSegments => MetricVisualPresentation.LitSegments(
        HasDiskPercent ? _card!.DiskUsageValue : null, MetricVisualPresentation.DiskSegmentCount);

    /// <summary>The meter's lit count: <see cref="DiskLitSegments"/>, or -1 for unknown (an empty track).</summary>
    public int DiskLitCount => DiskLitSegments ?? -1;

    // --- Commands (pass-through to the live card) -----------------------------------------------------------------

    public ICommand GoBackCommand { get; }

    public ICommand RefreshCommand { get; }

    /// <summary>Editar: the current editor modal until UI.7.</summary>
    public ICommand EditCommand { get; }

    /// <summary>H-UI5-1: from the "…" menu; no confirmation (as today). Afterwards the page returns to Servidores.</summary>
    public ICommand HideCommand { get; }

    /// <summary>H-UI5-1: from the "…" menu; the existing confirmation dialog. Afterwards the page returns to Servidores.</summary>
    public ICommand RemoveCommand { get; }

    public ICommand ViewHistoryCommand { get; }

    public ICommand ViewWorkloadsCommand { get; }

    // --- Lifecycle -------------------------------------------------------------------------------------------------

    /// <summary>
    /// TEST/MEASUREMENT SEAM: replaces the UI dispatcher as the place a coalesced flush is queued. Null in production (the
    /// dispatcher is used) and in ordinary tests (no dispatcher: the flush runs at once).
    /// </summary>
    internal Func<Action, bool>? Scheduler { get; set; }

    /// <summary>Binds the page to one server. A server that is not in the list (anymore) returns to the origin at once.</summary>
    public void Load(Guid serverId, ServerDetailOrigin origin)
    {
        _serverId = serverId;
        Origin = origin;
        OnPropertyChanged(nameof(Origin));
        OnPropertyChanged(nameof(ParentText));
        if (!_subscribed)
        {
            _dashboard.ServersReloaded += OnServersReloaded;
            _subscribed = true;
        }

        Resolve();
    }

    /// <summary>Where focus returns after Histórico / Serviços e containers (taken once, for this server only).</summary>
    public ServerDetailReturnTarget TakeReturnFocus() => _returnFocus?.Take(_serverId) ?? ServerDetailReturnTarget.None;

    public void Dispose()
    {
        _dashboard.PropertyChanged -= OnDashboardPropertyChanged;
        // A deferred Resolve queued before the page was replaced must not navigate afterwards.
        _disposed = true;
        if (_subscribed)
        {
            _dashboard.ServersReloaded -= OnServersReloaded;
            _subscribed = false;
        }

        if (_card is not null)
        {
            _card.PropertyChanged -= OnCardPropertyChanged;
        }

        _pulseCancellation?.Cancel();
        _pulseCancellation?.Dispose();
        _pulseCancellation = null;
    }

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.IsOperationErrorOpen) or nameof(DashboardViewModel.IsConfigurationLockedOpen))
        {
            OnPropertyChanged(e.PropertyName);

            // An operation that failed here leaves the server in place: a later, unrelated disappearance goes to the origin.
            if (_dashboard.IsOperationErrorOpen || _dashboard.IsConfigurationLockedOpen)
            {
                _exitOperation = null;
                _exitOperationCompleted = false;
            }
        }
    }

    // Cortex r1 NIT-1: raised inside the dashboard's list rebuild; never swap the frame re-entrantly from there.
    private void OnServersReloaded(object? sender, EventArgs e)
    {
        if (_dispatcherQueue is null || !_dispatcherQueue.TryEnqueue(Resolve))
        {
            Resolve();
        }
    }

    private static DispatcherQueue? TryGetDispatcher()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Resolve()
    {
        if (_left || _disposed)
        {
            return;
        }

        var card = _dashboard.VisibleServers.FirstOrDefault(candidate => candidate.Server.Id == _serverId);
        if (card is null)
        {
            // Hidden or removed (here or elsewhere): leave exactly once.
            Leave(serverGone: true);
            return;
        }

        // Cortex B1 N-1: a rebuild AFTER the operation finished that still lists the server means it did not leave because
        // of it (Remover cancelled in its confirmation, or nothing changed): a later, unrelated disappearance is not "ours".
        if (_exitOperationCompleted)
        {
            _exitOperation = null;
            _exitOperationCompleted = false;
        }

        Card = card;
    }

    /// <param name="serverGone">
    /// True when the server left the list. If THIS page's Ocultar/Remover caused it (H-UI5-1) the page returns to
    /// Servidores and hands it a one-shot notice; the breadcrumb (false) always returns to the origin, never with a notice.
    /// </param>
    private void Leave(bool serverGone)
    {
        if (_left)
        {
            return;
        }

        _left = true;
        var ownOperation = serverGone ? _exitOperation : null;
        var name = _card?.Name;
        Dispose();
        if (ownOperation is { } kind && name is not null)
        {
            _serversNotice?.Post(new ServersNotice(kind, name));
        }

        if (ownOperation is not null || Origin == ServerDetailOrigin.Servers)
        {
            _navigation.GoToServers();
        }
        else
        {
            _navigation.GoToDashboard();
        }
    }

    // H-UI5-1: Ocultar / Remover started HERE return to Servidores once the server is gone (its toasts live there).
    private async Task ExitingOperationAsync(ICommand? operation, ServersNoticeKind kind)
    {
        if (operation is null)
        {
            return;
        }

        _exitOperation = kind;
        _exitOperationCompleted = false;
        if (operation is AsyncRelayCommand asyncOperation)
        {
            await asyncOperation.ExecuteAsync().ConfigureAwait(true);
        }
        else
        {
            operation.Execute(null);
        }

        // The rebuild the operation triggers may still be on its way: the NEXT rebuild decides (gone = ours, listed = not).
        if (_exitOperation is not null)
        {
            _exitOperationCompleted = true;
        }
    }

    private void Explore(ServerDetailReturnTarget target, ICommand? open)
    {
        if (_card is null || open is null)
        {
            return;
        }

        _returnFocus?.Remember(_serverId, target);
        open.Execute(null);
    }

    private void NotifyCommands()
    {
        ((RelayCommand)RefreshCommand).NotifyCanExecuteChanged();
        ((RelayCommand)EditCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ViewHistoryCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ViewWorkloadsCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)HideCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)RemoveCommand).NotifyCanExecuteChanged();
    }

    // --- Coalesced update path -------------------------------------------------------------------------------------

    [Flags]
    private enum Change
    {
        None = 0,
        Status = 1,
        Cpu = 2,
        Memory = 4,
        Disk = 8,
        Reading = 16,
        State = 32,
        Connection = 64,
        All = Status | Cpu | Memory | Disk | Reading | State | Connection
    }

    private static Change Map(string? cardProperty) => cardProperty switch
    {
        nameof(ServerCardViewModel.Health) or nameof(ServerCardViewModel.HealthDisplayName)
            => Change.Status | Change.Reading | Change.State,
        nameof(ServerCardViewModel.CpuUsageDisplay) or nameof(ServerCardViewModel.HasCpuPercent)
            or nameof(ServerCardViewModel.CpuUsageValue) or nameof(ServerCardViewModel.HasCpuUsage) => Change.Cpu,
        nameof(ServerCardViewModel.MemoryUsageDisplay) or nameof(ServerCardViewModel.HasMemoryPercent)
            or nameof(ServerCardViewModel.MemoryUsageValue) or nameof(ServerCardViewModel.HasMemoryUsage) => Change.Memory,
        nameof(ServerCardViewModel.DiskUsageDisplay) or nameof(ServerCardViewModel.HasDiskPercent)
            or nameof(ServerCardViewModel.DiskUsageValue) or nameof(ServerCardViewModel.HasDiskUsage) => Change.Disk,
        nameof(ServerCardViewModel.IsStale) or nameof(ServerCardViewModel.StaleAgeDisplay)
            or nameof(ServerCardViewModel.HasStaleIndicator) or nameof(ServerCardViewModel.HasMetrics)
            or nameof(ServerCardViewModel.UptimeDisplay) or nameof(ServerCardViewModel.HasUptime)
            or nameof(ServerCardViewModel.DetectedOperatingSystemDisplay) or nameof(ServerCardViewModel.HasDetectedOperatingSystem)
            or nameof(ServerCardViewModel.LastSuccessAt) or nameof(ServerCardViewModel.MetricsTimestampDisplay) => Change.Reading,
        nameof(ServerCardViewModel.IsRefreshingMetrics) or nameof(ServerCardViewModel.IsMetricsPending)
            or nameof(ServerCardViewModel.HasMetricsError) or nameof(ServerCardViewModel.MetricsErrorDisplay) => Change.State,
        nameof(ServerCardViewModel.ConnectionState) or nameof(ServerCardViewModel.ConnectionStateDisplayName) => Change.Connection,
        _ => Change.None
    };

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _card))
        {
            return;
        }

        var change = Map(e.PropertyName);
        if (change == Change.None)
        {
            return;
        }

        _dirty |= change;
        if (_flushScheduled)
        {
            return;
        }

        var scheduler = Scheduler;
        if (scheduler is not null)
        {
            _flushScheduled = scheduler(_flushAction);
        }
        else if (_dispatcherQueue is not null)
        {
            _flushScheduled = _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FlushFromDispatcher);
        }

        if (!_flushScheduled)
        {
            Flush();
        }
    }

    private void FlushFromDispatcher() => Flush();

    /// <summary>Runs a pending (coalesced) flush now (tests/measurements, like the overview's FlushOverview).</summary>
    internal void FlushPending()
    {
        if (_dirty != Change.None)
        {
            Flush();
        }
    }

    private void Flush()
    {
        _flushScheduled = false;
        var dirty = _dirty;
        _dirty = Change.None;
        if (dirty == Change.None || _disposed)
        {
            return;
        }

        if (dirty == Change.All)
        {
            Raise(Args.Title);
            Raise(Args.Address);
            Raise(Args.ConfiguredSystemDisplay);
            Raise(Args.RefreshAutomationName);
            Raise(Args.EditAutomationName);
            Raise(Args.MoreActionsAutomationName);
            Raise(Args.HideAutomationName);
            Raise(Args.RemoveAutomationName);
            Raise(Args.ViewHistoryAutomationName);
            Raise(Args.ViewWorkloadsAutomationName);
            Raise(Args.IsRouted);
            Raise(Args.IsMetricsUnsupported);
            Raise(Args.Username);
            Raise(Args.AuthenticationDisplay);
            Raise(Args.IntervalDisplay);
        }

        if ((dirty & Change.Status) != 0)
        {
            Raise(Args.Health);
            Raise(Args.StatusText);
            Raise(Args.StatusLabel);
            Raise(Args.IsWithoutConnection);
        }

        if ((dirty & Change.Cpu) != 0)
        {
            Raise(Args.HasCpuPercent);
            Raise(Args.CpuDisplay);
            Raise(Args.CpuValueText);
            Raise(Args.CpuAccessibleName);
            Raise(Args.CpuAccessibleValue);
            Raise(Args.CpuSeverity);
        }

        if ((dirty & Change.Memory) != 0)
        {
            Raise(Args.HasMemoryPercent);
            Raise(Args.MemoryDisplay);
            Raise(Args.MemoryValueText);
            Raise(Args.MemoryAccessibleName);
            Raise(Args.MemoryLegend);
            Raise(Args.MemoryAccessibleValue);
            Raise(Args.MemorySeverity);
            Raise(Args.MemoryLitSegments);
            Raise(Args.MemoryLitCount);
        }

        if ((dirty & Change.Disk) != 0)
        {
            Raise(Args.HasDiskPercent);
            Raise(Args.DiskDisplay);
            Raise(Args.DiskValueText);
            Raise(Args.DiskAccessibleName);
            Raise(Args.DiskLegend);
            Raise(Args.DiskAccessibleValue);
            Raise(Args.DiskSeverity);
            Raise(Args.DiskLitSegments);
            Raise(Args.DiskLitCount);
        }

        if ((dirty & Change.Reading) != 0)
        {
            ReadClock();
            Raise(Args.IsReadingStale);
            Raise(Args.StaleText);
            Raise(Args.HasReading);
            Raise(Args.HasUptime);
            Raise(Args.UptimeDisplay);
            Raise(Args.DetectedSystemDisplay);
            Raise(Args.SystemDisplay);
            Raise(Args.HeaderSubtitle);
            Raise(Args.UptimeLongDisplay);
            Raise(Args.UptimeDisplayOrDash);
            Raise(Args.LastUpdatedValue);
            Raise(Args.LastUpdatedOrDash);
            RequestCpuPulse(force: false);
        }

        if ((dirty & Change.State) != 0)
        {
            Raise(Args.IsFirstReading);
            Raise(Args.StatusLabel);
            Raise(Args.IsRefreshing);
            Raise(Args.HasCollectionError);
            Raise(Args.CollectionErrorText);
            ((RelayCommand)RefreshCommand).NotifyCanExecuteChanged();
        }

        if ((dirty & Change.Connection) != 0)
        {
            Raise(Args.ConnectionState);
            Raise(Args.ConnectionStateDisplay);
            Raise(Args.HasConnectionProblem);
        }

        if ((dirty & (Change.Status | Change.Cpu | Change.Memory | Change.Disk | Change.Reading)) != 0)
        {
            Raise(Args.AutomationSummary);
        }
    }

    private void Raise(PropertyChangedEventArgs args) => OnPropertyChanged(args);

    // --- CPU pulse (H-UI5-3) ---------------------------------------------------------------------------------------

    /// <summary>
    /// H-UI5-3 source: the LOCAL HISTORY the app already records (<see cref="IServerHistoryQueryService"/>, last hour —
    /// at the 30 s sampling policy that range is returned raw, not bucketed). Read on load / card swap, then again only
    /// when the engine's last success has moved at least one history sampling interval past the previous read, so an
    /// open page costs at most one query per persisted sample. No new collection, no new persistence.
    /// Cortex B1 N-3 (accepted): the history writer persists asynchronously, so the read made right after a new reading may
    /// not contain that reading yet - the pulse can lag by one sample until the next read. Still only real samples.
    /// </summary>
    private void RequestCpuPulse(bool force)
    {
        if (_disposed || _card is null || _history is null || !_history.IsAvailable)
        {
            SetCpuPulse([]);
            return;
        }

        var success = _card.LastSuccessAt;
        if (!force)
        {
            if (success is not { } current)
            {
                return;
            }

            if (_pulseAnchor is { } anchor && current - anchor < HistorySamplingPolicy.DefaultMinInterval)
            {
                return;
            }
        }

        _pulseAnchor = success;
        _pulseCancellation?.Cancel();
        _pulseCancellation?.Dispose();
        _pulseCancellation = new CancellationTokenSource();
        _ = LoadCpuPulseAsync(_card.Server.Id, _pulseCancellation.Token);
    }

    private async Task LoadCpuPulseAsync(Guid serverId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _history!.GetHistoryAsync(serverId, HistoryTimeRange.LastHour, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || _disposed || _card?.Server.Id != serverId)
            {
                return;
            }

            SetCpuPulse(MetricVisualPresentation.CpuPulse(result.Cpu));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer read or the page went away.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // History is degradable: no bars rather than invented ones.
            if (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                SetCpuPulse([]);
            }
        }
    }

    private void SetCpuPulse(IReadOnlyList<double> samples)
    {
        if (_cpuPulse.Count == 0 && samples.Count == 0)
        {
            return;
        }

        _cpuPulse = samples;
        Raise(Args.CpuPulseSamples);
        Raise(Args.HasCpuPulse);
    }

    // --- Formatting ------------------------------------------------------------------------------------------------

    private string Unavailable => _localization.GetString("ServerMetricUnavailable");

    private string Percent(bool known, double value) => known
        ? string.Format(CultureInfo.CurrentUICulture, "{0:0}%", value)
        : Unavailable;

    private string Number(bool known, double value) => known
        ? string.Format(CultureInfo.CurrentUICulture, "{0:0}", value)
        : Unavailable;

    private string? BytesOf(long? used, long? total) =>
        used is { } usedBytes && total is { } totalBytes && totalBytes > 0 && usedBytes >= 0
            ? Format("ServerDetailBytesOfFormat", MetricVisualPresentation.FormatBytes(usedBytes), MetricVisualPresentation.FormatBytes(totalBytes))
            : null;

    // "12 dias, 8 horas" / "3 horas, 5 minutos" / "7 minutos": the two most significant units, plural-aware.
    private string LongDuration(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return Join(Unit("Days", (int)uptime.TotalDays), uptime.Hours > 0 ? Unit("Hours", uptime.Hours) : null);
        }

        if (uptime.TotalHours >= 1)
        {
            return Join(Unit("Hours", (int)uptime.TotalHours), uptime.Minutes > 0 ? Unit("Minutes", uptime.Minutes) : null);
        }

        return Unit("Minutes", Math.Max(1, (int)uptime.TotalMinutes));

        string Unit(string unit, int count) => Format(WorkloadPresentation.PluralKey("ServerDetailDuration" + unit, count), count);

        string Join(string first, string? second) =>
            second is null ? first : string.Join(_localization.GetString("ServerDetailDurationSeparator"), first, second);
    }

    private static ServerHealth Severity(bool known, double? value, double warning, double critical) =>
        known ? OverviewPresentation.MetricSeverity(value, warning, critical) : ServerHealth.Healthy;

    private string AccessiblePercent(bool known, double value) => ServerStatusPresentation.AccessiblePercent(known, value, _localization);

    private string NameFor(string key) => _card is null ? string.Empty : Format(key, _card.Name);

    private string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, _localization.GetString(key), args);

    // D-UI3-9 buckets, in Figma's "label + value" long form (A-5, 112:1912 "Há 8 segundos"): <1 s "Agora mesmo", then
    // seconds / minutes / hours / days, plural-aware. Recomputed only on a new reading (no timer).
    private string FormatUpdatedAgo(DateTimeOffset lastSuccessUtc)
    {
        var age = AgeOf(lastSuccessUtc);

        if (age.TotalSeconds < 1)
        {
            return _localization.GetString("ServerDetailUpdatedJustNow");
        }

        var (unit, count) = age.TotalMinutes < 1 ? ("Seconds", (int)age.TotalSeconds)
            : age.TotalHours < 1 ? ("Minutes", (int)age.TotalMinutes)
            : age.TotalDays < 1 ? ("Hours", (int)age.TotalHours)
            : ("Days", (int)age.TotalDays);
        return Format("ServerDetailUpdatedAgoFormat", Format(WorkloadPresentation.PluralKey("ServerDetailDuration" + unit, count), count));
    }

    /// <summary>Cached arguments: the per-burst flush allocates no event arguments.</summary>
    private static class Args
    {
        public static readonly PropertyChangedEventArgs Title = new(nameof(ServerDetailViewModel.Title));
        public static readonly PropertyChangedEventArgs Address = new(nameof(ServerDetailViewModel.Address));
        public static readonly PropertyChangedEventArgs ConfiguredSystemDisplay = new(nameof(ServerDetailViewModel.ConfiguredSystemDisplay));
        public static readonly PropertyChangedEventArgs DetectedSystemDisplay = new(nameof(ServerDetailViewModel.DetectedSystemDisplay));
        public static readonly PropertyChangedEventArgs Health = new(nameof(ServerDetailViewModel.Health));
        public static readonly PropertyChangedEventArgs StatusText = new(nameof(ServerDetailViewModel.StatusText));
        public static readonly PropertyChangedEventArgs AutomationSummary = new(nameof(ServerDetailViewModel.AutomationSummary));
        public static readonly PropertyChangedEventArgs RefreshAutomationName = new(nameof(ServerDetailViewModel.RefreshAutomationName));
        public static readonly PropertyChangedEventArgs EditAutomationName = new(nameof(ServerDetailViewModel.EditAutomationName));
        public static readonly PropertyChangedEventArgs MoreActionsAutomationName = new(nameof(ServerDetailViewModel.MoreActionsAutomationName));
        public static readonly PropertyChangedEventArgs HideAutomationName = new(nameof(ServerDetailViewModel.HideAutomationName));
        public static readonly PropertyChangedEventArgs RemoveAutomationName = new(nameof(ServerDetailViewModel.RemoveAutomationName));
        public static readonly PropertyChangedEventArgs ViewHistoryAutomationName = new(nameof(ServerDetailViewModel.ViewHistoryAutomationName));
        public static readonly PropertyChangedEventArgs ViewWorkloadsAutomationName = new(nameof(ServerDetailViewModel.ViewWorkloadsAutomationName));
        public static readonly PropertyChangedEventArgs IsFirstReading = new(nameof(ServerDetailViewModel.IsFirstReading));
        public static readonly PropertyChangedEventArgs IsRefreshing = new(nameof(ServerDetailViewModel.IsRefreshing));
        public static readonly PropertyChangedEventArgs IsMetricsUnsupported = new(nameof(ServerDetailViewModel.IsMetricsUnsupported));
        public static readonly PropertyChangedEventArgs HasCollectionError = new(nameof(ServerDetailViewModel.HasCollectionError));
        public static readonly PropertyChangedEventArgs CollectionErrorText = new(nameof(ServerDetailViewModel.CollectionErrorText));
        public static readonly PropertyChangedEventArgs IsWithoutConnection = new(nameof(ServerDetailViewModel.IsWithoutConnection));
        public static readonly PropertyChangedEventArgs IsReadingStale = new(nameof(ServerDetailViewModel.IsReadingStale));
        public static readonly PropertyChangedEventArgs StaleText = new(nameof(ServerDetailViewModel.StaleText));
        public static readonly PropertyChangedEventArgs HasReading = new(nameof(ServerDetailViewModel.HasReading));
        public static readonly PropertyChangedEventArgs HasUptime = new(nameof(ServerDetailViewModel.HasUptime));
        public static readonly PropertyChangedEventArgs UptimeDisplay = new(nameof(ServerDetailViewModel.UptimeDisplay));
        public static readonly PropertyChangedEventArgs LastUpdatedValue = new(nameof(ServerDetailViewModel.LastUpdatedValue));
        public static readonly PropertyChangedEventArgs ConnectionState = new(nameof(ServerDetailViewModel.ConnectionState));
        public static readonly PropertyChangedEventArgs ConnectionStateDisplay = new(nameof(ServerDetailViewModel.ConnectionStateDisplay));
        public static readonly PropertyChangedEventArgs HasConnectionProblem = new(nameof(ServerDetailViewModel.HasConnectionProblem));
        public static readonly PropertyChangedEventArgs IsRouted = new(nameof(ServerDetailViewModel.IsRouted));
        public static readonly PropertyChangedEventArgs HasCpuPercent = new(nameof(ServerDetailViewModel.HasCpuPercent));
        public static readonly PropertyChangedEventArgs CpuDisplay = new(nameof(ServerDetailViewModel.CpuDisplay));
        public static readonly PropertyChangedEventArgs CpuAccessibleValue = new(nameof(ServerDetailViewModel.CpuAccessibleValue));
        public static readonly PropertyChangedEventArgs CpuSeverity = new(nameof(ServerDetailViewModel.CpuSeverity));
        public static readonly PropertyChangedEventArgs CpuPulseSamples = new(nameof(ServerDetailViewModel.CpuPulseSamples));
        public static readonly PropertyChangedEventArgs HasCpuPulse = new(nameof(ServerDetailViewModel.HasCpuPulse));
        public static readonly PropertyChangedEventArgs HasMemoryPercent = new(nameof(ServerDetailViewModel.HasMemoryPercent));
        public static readonly PropertyChangedEventArgs MemoryDisplay = new(nameof(ServerDetailViewModel.MemoryDisplay));
        public static readonly PropertyChangedEventArgs MemoryAccessibleValue = new(nameof(ServerDetailViewModel.MemoryAccessibleValue));
        public static readonly PropertyChangedEventArgs MemorySeverity = new(nameof(ServerDetailViewModel.MemorySeverity));
        public static readonly PropertyChangedEventArgs MemoryLitSegments = new(nameof(ServerDetailViewModel.MemoryLitSegments));
        public static readonly PropertyChangedEventArgs HasDiskPercent = new(nameof(ServerDetailViewModel.HasDiskPercent));
        public static readonly PropertyChangedEventArgs DiskDisplay = new(nameof(ServerDetailViewModel.DiskDisplay));
        public static readonly PropertyChangedEventArgs DiskAccessibleValue = new(nameof(ServerDetailViewModel.DiskAccessibleValue));
        public static readonly PropertyChangedEventArgs DiskSeverity = new(nameof(ServerDetailViewModel.DiskSeverity));
        public static readonly PropertyChangedEventArgs DiskLitSegments = new(nameof(ServerDetailViewModel.DiskLitSegments));
        public static readonly PropertyChangedEventArgs CpuValueText = new(nameof(ServerDetailViewModel.CpuValueText));
        public static readonly PropertyChangedEventArgs CpuAccessibleName = new(nameof(ServerDetailViewModel.CpuAccessibleName));
        public static readonly PropertyChangedEventArgs MemoryValueText = new(nameof(ServerDetailViewModel.MemoryValueText));
        public static readonly PropertyChangedEventArgs MemoryAccessibleName = new(nameof(ServerDetailViewModel.MemoryAccessibleName));
        public static readonly PropertyChangedEventArgs MemoryLegend = new(nameof(ServerDetailViewModel.MemoryLegend));
        public static readonly PropertyChangedEventArgs DiskValueText = new(nameof(ServerDetailViewModel.DiskValueText));
        public static readonly PropertyChangedEventArgs DiskAccessibleName = new(nameof(ServerDetailViewModel.DiskAccessibleName));
        public static readonly PropertyChangedEventArgs DiskLegend = new(nameof(ServerDetailViewModel.DiskLegend));
        public static readonly PropertyChangedEventArgs HeaderSubtitle = new(nameof(ServerDetailViewModel.HeaderSubtitle));
        public static readonly PropertyChangedEventArgs SystemDisplay = new(nameof(ServerDetailViewModel.SystemDisplay));
        public static readonly PropertyChangedEventArgs Username = new(nameof(ServerDetailViewModel.Username));
        public static readonly PropertyChangedEventArgs AuthenticationDisplay = new(nameof(ServerDetailViewModel.AuthenticationDisplay));
        public static readonly PropertyChangedEventArgs IntervalDisplay = new(nameof(ServerDetailViewModel.IntervalDisplay));
        public static readonly PropertyChangedEventArgs UptimeLongDisplay = new(nameof(ServerDetailViewModel.UptimeLongDisplay));
        public static readonly PropertyChangedEventArgs StatusLabel = new(nameof(ServerDetailViewModel.StatusLabel));
        public static readonly PropertyChangedEventArgs UptimeDisplayOrDash = new(nameof(ServerDetailViewModel.UptimeDisplayOrDash));
        public static readonly PropertyChangedEventArgs LastUpdatedOrDash = new(nameof(ServerDetailViewModel.LastUpdatedOrDash));
        public static readonly PropertyChangedEventArgs MemoryLitCount = new(nameof(ServerDetailViewModel.MemoryLitCount));
        public static readonly PropertyChangedEventArgs DiskLitCount = new(nameof(ServerDetailViewModel.DiskLitCount));
    }
}
