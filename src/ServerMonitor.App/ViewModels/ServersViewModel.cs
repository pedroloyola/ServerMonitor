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
    private ServersNotice? _notice;
    private readonly TransientNoticeTimer _noticeTimer;

    public ServersViewModel(
        DashboardViewModel dashboard,
        INavigationService navigation,
        ILocalizationService localization,
        PresentationClock clock,
        ServersReturnNotice? returnNotice = null)
    {
        ArgumentNullException.ThrowIfNull(clock); // UI.5 fix round 4: required - the notice countdown never defaults to the system clock
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));

        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        // UI.5 Boss B2 answer 1: the one-shot notice the Server Detail left for this visit (taken once, never re-shown).
        // Fix round 2 (Boss decision 2): it closes itself after TransientNoticeTimer.Duration, or when the visit ends.
        _noticeTimer = new TransientNoticeTimer(clock.TimeProvider);
        _notice = returnNotice?.Take();
        DismissNoticeCommand = new RelayCommand(DismissNotice);
        if (_notice is not null)
        {
            _noticeTimer.Start(DismissNotice);
        }

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

    /// <summary>H-UI5-1 / Figma 112:21833 · 112:20994: "Servidor ocultado" / "Servidor removido" after the Detail's own action.</summary>
    public bool IsNoticeOpen => _notice is not null;

    public string NoticeTitle => _notice is null
        ? string.Empty
        : _localization.GetString(_notice.Kind == ServersNoticeKind.Hidden ? "ServersNoticeHiddenTitle" : "ServersNoticeRemovedTitle");

    public string NoticeMessage => _notice is null
        ? string.Empty
        : Format(_notice.Kind == ServersNoticeKind.Hidden ? "ServersNoticeHiddenMessageFormat" : "ServersNoticeRemovedMessageFormat", _notice.ServerName);

    public string NoticeCloseAutomationName => _localization.GetString("ServersNoticeCloseName");

    public ICommand DismissNoticeCommand { get; }


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

    /// <summary>Figma §13 112:14283: "Nenhum servidor encontrado" + the query in the message (Prism r1 e).</summary>
    public string NoResultsTitle => _localization.GetString("ServersNoResultsTitle");

    public string NoResultsMessage => Format("ServersNoResultsMessageFormat", _searchText.Trim());

    /// <summary>The header line: "A carregar os teus servidores…" while loading (Figma §14 112:15760), then the summary.</summary>
    public string HeaderContextDisplay => IsLoading ? _localization.GetString("ServersLoadingSubtitle") : SummaryDisplay;

    /// <summary>
    /// "6 servidores · 4 saudáveis · 1 atenção · 1 sem ligação" (+ crítico / sem dados when &gt; 0). Healthy is always
    /// shown (Figma 112:1420); the exception states only when present, in the overview's chip order.
    /// </summary>
    public string SummaryDisplay
    {
        get
        {
            var summary = _dashboard.HealthSummary;
            var total = Format(WorkloadPresentation.PluralKey("ServersSummaryTotal", summary.Total), summary.Total);
            if (summary.Total == 0)
            {
                return total; // Prism r2 R2-B2: Figma 112:14077 "0 servidores", nothing else.
            }

            var parts = new List<string>
            {
                total,
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
    /// The note "Os servidores ocultos podem ser restaurados nas Definições." (Figma 112:1533), Prism r1 (c): always
    /// under a table that has rows; never on the no-results state nor the plain empty state (§13 draws none); and on the
    /// empty state when servers are hidden — otherwise the user would think they were lost.
    /// </summary>
    public bool ShowHiddenServersNote => !IsLoading && (_rows.Count > 0 || (!HasServers && _dashboard.HiddenServerCount > 0));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _noticeTimer.Dispose();
        _dashboard.ServersReloaded -= OnServersReloaded;
        _dashboard.PropertyChanged -= OnDashboardPropertyChanged;
        DisposeRows();
    }

    private void OnServersReloaded(object? sender, EventArgs e) => RebuildRows();

    private void DismissNotice()
    {
        _noticeTimer.Cancel();
        if (_notice is null)
        {
            return;
        }

        _notice = null;
        OnPropertyChanged(nameof(IsNoticeOpen));
    }

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DashboardViewModel.HealthSummary):
                OnPropertyChanged(nameof(SummaryDisplay));
                OnPropertyChanged(nameof(HeaderContextDisplay));
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
            .Select(card => new ServerDirectoryRowViewModel(card, _localization, OpenDetail, _dashboard.Thresholds))
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
        OnPropertyChanged(nameof(NoResultsMessage));
        RaiseStates();
    }

    private void RaiseStates()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowTable));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(ShowHiddenServersNote));
        OnPropertyChanged(nameof(HeaderContextDisplay));
    }

    private void OpenDetail(ServerCardViewModel card)
    {
        // Beacon r1 SHOULD-1: coming back, the next Servidores page puts focus back on this row.
        _dashboard.RememberDirectoryReturnFocus(card.Server.Id);
        _navigation.GoToServerDetail(card.Server.Id, ServerDetailOrigin.Servers);
    }

    /// <summary>The row to refocus when this page opens after the interim page (taken once), with its index in <see cref="Rows"/>.</summary>
    public int TakeReturnFocusIndex()
    {
        if (_dashboard.TakeDirectoryReturnFocus() is not { } id)
        {
            return -1;
        }

        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].ServerId == id)
            {
                return i;
            }
        }

        return -1;
    }

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
