using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.4 §3: the Servidores directory. A per-visit view (transient, disposed with its page) over the SAME server cards the
/// dashboard owns, so health and metrics come from the engine exactly as everywhere else and nothing is re-collected.
/// The summary uses the dashboard's <see cref="HealthSummary"/> (one source for both screens); the search is the pure
/// <see cref="OverviewPresentation.MatchesSearch"/>. Rows are rebuilt only when the list itself is rebuilt
/// (<see cref="DashboardViewModel.ServersReloaded"/>) and re-filtered on each search change.
/// </summary>
public sealed class ServersViewModel : ObservableObject, IDisposable
{
    private readonly DashboardViewModel _dashboard;
    private readonly INavigationService _navigation;
    private readonly ILocalizationService _localization;
    private List<ServerDirectoryRowViewModel> _allRows = [];
    private IReadOnlyList<ServerDirectoryRowViewModel> _rows = [];
    private string _searchText = string.Empty;
    private bool _disposed;

    public ServersViewModel(DashboardViewModel dashboard, INavigationService navigation, ILocalizationService localization)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));

        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        BackToOverviewCommand = new RelayCommand(navigation.GoToDashboard);

        _dashboard.ServersReloaded += OnServersReloaded;
        _dashboard.PropertyChanged += OnDashboardPropertyChanged;
        RebuildRows();
    }

    /// <summary>The filtered rows (a new list per filter, so a large table re-binds once instead of per item).</summary>
    public IReadOnlyList<ServerDirectoryRowViewModel> Rows => _rows;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public ICommand ClearSearchCommand { get; }

    public ICommand BackToOverviewCommand { get; }

    /// <summary>The current add flow (modal) — until UI.7.</summary>
    public ICommand AddServerCommand => _dashboard.AddServerCommand;

    public bool IsLoading => _dashboard.IsLoading;

    public int TotalCount => _allRows.Count;

    public bool HasServers => TotalCount > 0;

    /// <summary>No configured visible server at all (once loaded): the page's empty state.</summary>
    public bool ShowEmptyState => !IsLoading && !HasServers;

    public bool ShowTable => !IsLoading && HasServers && _rows.Count > 0;

    /// <summary>A search that matches nothing: the "Sem resultados" state with "Limpar pesquisa".</summary>
    public bool HasNoResults => !IsLoading && HasServers && _rows.Count == 0 && !string.IsNullOrWhiteSpace(_searchText);

    public string NoResultsTitle => Format("ServerSearchNoResultsTitleFormat", _searchText.Trim());

    /// <summary>
    /// "6 servidores · 4 saudáveis · 1 atenção · 1 sem ligação" (+ crítico / sem dados when &gt; 0). Healthy is always
    /// shown (Figma 112:1420); the exception states only when present, in the overview's chip order.
    /// </summary>
    public string SummaryDisplay
    {
        get
        {
            var summary = _dashboard.HealthSummary;
            var parts = new List<string>
            {
                Format(WorkloadPresentation.PluralKey("ServersSummaryTotal", summary.Total), summary.Total),
                Format(WorkloadPresentation.PluralKey("ServersSummaryHealthy", summary.Healthy), summary.Healthy)
            };
            foreach (var health in OverviewPresentation.ChipOrder)
            {
                var count = summary.CountOf(health);
                if (count > 0)
                {
                    parts.Add(Format(WorkloadPresentation.PluralKey($"OverviewHealthChip{health}", count), count));
                }
            }

            return ServerContextPresentation.Join([.. parts]);
        }
    }

    /// <summary>
    /// The note "Os servidores ocultos podem ser restaurados nas Definições." DERIVED (pending Prism): shown only when at
    /// least one server is hidden — the concept is real (Server.IsHidden), but the hint is noise when nothing is hidden.
    /// </summary>
    public bool ShowHiddenServersNote => _dashboard.HiddenServerCount > 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dashboard.ServersReloaded -= OnServersReloaded;
        _dashboard.PropertyChanged -= OnDashboardPropertyChanged;
        DisposeRows();
    }

    private void OnServersReloaded(object? sender, EventArgs e) => RebuildRows();

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DashboardViewModel.HealthSummary):
                OnPropertyChanged(nameof(SummaryDisplay));
                break;
            case nameof(DashboardViewModel.IsLoading):
                RaiseStates();
                break;
            case nameof(DashboardViewModel.HiddenServerCount):
                OnPropertyChanged(nameof(ShowHiddenServersNote));
                break;
        }
    }

    private void RebuildRows()
    {
        DisposeRows();
        _allRows = _dashboard.VisibleServers
            .Select(card => new ServerDirectoryRowViewModel(card, _localization, OpenDetail))
            .ToList();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(SummaryDisplay));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _rows = _allRows
            .Where(row => OverviewPresentation.MatchesSearch(row.Name, row.Card.Host, row.Card.Port, _searchText))
            .ToList();
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(NoResultsTitle));
        RaiseStates();
    }

    private void RaiseStates()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowTable));
        OnPropertyChanged(nameof(HasNoResults));
    }

    private void OpenDetail(ServerCardViewModel card) => _navigation.GoToServerDetail(card.Server.Id, ServerDetailOrigin.Servers);

    private void DisposeRows()
    {
        foreach (var row in _allRows)
        {
            row.Dispose();
        }
    }

    private string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, _localization.GetString(key), args);
}
