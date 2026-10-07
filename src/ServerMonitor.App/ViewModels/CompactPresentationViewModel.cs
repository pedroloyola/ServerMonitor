using System.ComponentModel;
using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.ViewModels;

/// <summary>What the Compact window's body shows (D-UI8-6 / R-8), derived only from the dashboard's EXISTING flags.</summary>
public enum CompactBodyState
{
    /// <summary>The first load has not finished (<c>IsLoading</c>).</summary>
    Loading,

    /// <summary>At least one visible server: the rows.</summary>
    List,

    /// <summary>No server at all (<c>ShowFirstServerState</c>): "Adicionar servidor".</summary>
    Empty,

    /// <summary>Servers exist but every one is hidden (<c>ShowAllHiddenState</c>) - never the same as empty.</summary>
    AllHidden,

    /// <summary>The configuration could not be read (<c>ShowConfigurationUnavailable</c>).</summary>
    ConfigurationUnavailable
}

/// <summary>
/// UI.8: the Compact window's presentation. A VIEW over the one <see cref="DashboardViewModel"/> - the same
/// <see cref="ServerCardViewModel"/> instances, in the same order, as <c>VisibleServers</c> - and nothing else: no engine,
/// store, server service, clock or timer, and no knowledge of the window mode (the expand / detail / add commands live
/// on <see cref="WindowModeViewModel"/>). RC-7 lifecycle: built from the CURRENT list (it may be created after the first
/// load), rebuilt on <see cref="DashboardViewModel.ServersReloaded"/> - never on the collection's Reset, which fires while
/// the list is momentarily empty - disposing the previous rows; the summary only forwards the dashboard's already
/// coalesced values. <see cref="Dispose"/> removes both subscriptions and disposes the rows.
/// </summary>
public sealed class CompactPresentationViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> RelevantDashboardProperties = new(StringComparer.Ordinal)
    {
        nameof(DashboardViewModel.IsLoading),
        nameof(DashboardViewModel.HasVisibleServers),
        nameof(DashboardViewModel.ShowFirstServerState),
        nameof(DashboardViewModel.ShowAllHiddenState),
        nameof(DashboardViewModel.ShowConfigurationUnavailable),
        nameof(DashboardViewModel.UpdatedAgoDisplay)
    };

    private readonly DashboardViewModel _dashboard;
    private readonly ILocalizationService _localization;
    private readonly MonitoringThresholds _thresholds;
    private IReadOnlyList<CompactServerRowViewModel> _rows = [];
    private CompactBodyState _bodyState;
    private string _serverCountDisplay = string.Empty;
    private string? _updatedAgoDisplay;
    private bool _disposed;

    public CompactPresentationViewModel(
        DashboardViewModel dashboard,
        ILocalizationService localization,
        MonitoringOptions? monitoringOptions = null)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        // D-UI8-5: the SAME thresholds instance the engine is composed with (App registers one MonitoringOptions).
        _thresholds = (monitoringOptions ?? MonitoringOptions.Default).Thresholds;
        RebuildRows(notify: false);
        RefreshSummary(notify: false);
        _dashboard.ServersReloaded += OnServersReloaded;
        _dashboard.PropertyChanged += OnDashboardPropertyChanged;
    }

    /// <summary>One row per visible server, same instances and order as <c>VisibleServers</c>. Replaced as a whole on reload.</summary>
    public IReadOnlyList<CompactServerRowViewModel> Rows => _rows;

    public CompactBodyState BodyState => _bodyState;

    /// <summary>"6 servidores" (R-8: the fleet summary shows only with the list).</summary>
    public string ServerCountDisplay => _serverCountDisplay;

    /// <summary>The overview's own "Atualizado há …" (same semantics, no timer of its own); null hides it.</summary>
    public string? UpdatedAgoDisplay => _updatedAgoDisplay;

    public bool HasUpdatedAgo => _updatedAgoDisplay is not null;

    public bool ShowsSummary => _bodyState == CompactBodyState.List;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dashboard.ServersReloaded -= OnServersReloaded;
        _dashboard.PropertyChanged -= OnDashboardPropertyChanged;
        foreach (var row in _rows)
        {
            row.Dispose();
        }

        _rows = [];
    }

    private void OnServersReloaded(object? sender, EventArgs e)
    {
        RebuildRows(notify: true);
        RefreshSummary(notify: true);
    }

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { Length: > 0 } name && !RelevantDashboardProperties.Contains(name))
        {
            return;
        }

        RefreshSummary(notify: true);
    }

    private void RebuildRows(bool notify)
    {
        var previous = _rows;
        _rows = _dashboard.VisibleServers
            .Select(card => new CompactServerRowViewModel(card, _localization, _thresholds))
            .ToList();
        foreach (var row in previous)
        {
            row.Dispose();
        }

        if (notify)
        {
            OnPropertyChanged(nameof(Rows));
        }
    }

    private void RefreshSummary(bool notify)
    {
        var state = Derive(
            _dashboard.IsLoading,
            _dashboard.HasVisibleServers,
            _dashboard.ShowConfigurationUnavailable,
            _dashboard.ShowAllHiddenState);
        var count = _rows.Count;
        var countDisplay = string.Format(
            CultureInfo.CurrentUICulture,
            _localization.GetString(WorkloadPresentation.PluralKey("ServersSummaryTotal", count)),
            count);
        var updatedAgo = _dashboard.UpdatedAgoDisplay;

        var stateChanged = _bodyState != state;
        _bodyState = state;
        var countChanged = !string.Equals(_serverCountDisplay, countDisplay, StringComparison.Ordinal);
        _serverCountDisplay = countDisplay;
        var updatedChanged = !string.Equals(_updatedAgoDisplay, updatedAgo, StringComparison.Ordinal);
        _updatedAgoDisplay = updatedAgo;
        if (!notify)
        {
            return;
        }

        if (stateChanged)
        {
            OnPropertyChanged(nameof(BodyState));
            OnPropertyChanged(nameof(ShowsSummary));
        }

        if (countChanged)
        {
            OnPropertyChanged(nameof(ServerCountDisplay));
        }

        if (updatedChanged)
        {
            OnPropertyChanged(nameof(UpdatedAgoDisplay));
            OnPropertyChanged(nameof(HasUpdatedAgo));
        }
    }

    /// <summary>
    /// R-7/R-8: loading first; real rows always win; then configuration-unavailable, all-hidden, and finally the
    /// first-server state. Pure, so every combination is testable.
    /// </summary>
    public static CompactBodyState Derive(bool isLoading, bool hasVisibleServers, bool showConfigurationUnavailable, bool showAllHidden) =>
        isLoading ? CompactBodyState.Loading
        : hasVisibleServers ? CompactBodyState.List
        : showConfigurationUnavailable ? CompactBodyState.ConfigurationUnavailable
        : showAllHidden ? CompactBodyState.AllHidden
        : CompactBodyState.Empty;
}
