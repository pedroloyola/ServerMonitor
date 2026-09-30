using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.ViewModels;

public enum ConnectionStepState
{
    /// <summary>Not started yet in the running test.</summary>
    Pending,

    /// <summary>The step being checked right now.</summary>
    Running,

    Passed,

    Failed,

    /// <summary>The test ended before this step (an earlier step failed, or the test was cancelled).</summary>
    NotReached,

    /// <summary>The server's identity is not known yet: the user has to confirm it in the trust panel.</summary>
    ActionRequired,

    /// <summary>Completed with a remark that is not a failure (operating system not identified).</summary>
    Warning
}

/// <summary>One row of the "Test connection" checklist (M14.5).</summary>
public sealed class ConnectionStepViewModel : ObservableObject
{
    private readonly ILocalizationService _localizationService;
    private string _title;
    private ConnectionStepState _state = ConnectionStepState.Pending;
    private string _stateText;
    private string _detail = string.Empty;
    private string _command = string.Empty;
    private bool _offersPrepHelp;

    public ConnectionStepViewModel(SshConnectionStage stage, string title, ILocalizationService localizationService)
    {
        Stage = stage;
        _title = title;
        _localizationService = localizationService;
        _stateText = StateLabel(ConnectionStepState.Pending);
    }

    /// <summary>The stage that, once reached, means this step passed.</summary>
    public SshConnectionStage Stage { get; }

    public string Title
    {
        get => _title;
        internal set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(AccessibleName));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public ConnectionStepState State => _state;

    public string StateText => _stateText;

    /// <summary>The diagnosis shown under the row (failed, action-required, warning or cancelled steps).</summary>
    public string Detail => _detail;

    public bool HasDetail => _detail.Length > 0;

    /// <summary>A command the user can copy and run on the server; never contains form values.</summary>
    public string Command => _command;

    public bool HasCommand => _command.Length > 0;

    public bool OffersPrepHelp => _offersPrepHelp;

    public bool IsPending => _state == ConnectionStepState.Pending;

    public bool IsRunning => _state == ConnectionStepState.Running;

    public bool IsPassed => _state == ConnectionStepState.Passed;

    public bool IsFailed => _state == ConnectionStepState.Failed;

    public bool IsNotReached => _state == ConnectionStepState.NotReached;

    public bool IsActionRequired => _state == ConnectionStepState.ActionRequired;

    public bool IsWarning => _state == ConnectionStepState.Warning;

    /// <summary>"SSH port: Passed" — the row's name for a screen reader always carries its state.</summary>
    public string AccessibleName => string.Format(
        CultureInfo.CurrentCulture,
        _localizationService.GetString("ConnectionStepAccessibleFormat"),
        Title,
        StateText);

    /// <summary>The accessible name followed by the diagnosis, for the live announcement.</summary>
    public string Summary => HasDetail ? AccessibleName + " " + Detail : AccessibleName;

    internal void Set(
        ConnectionStepState state,
        string? stateText = null,
        string? detail = null,
        string? command = null,
        bool offersPrepHelp = false)
    {
        _state = state;
        _stateText = stateText ?? StateLabel(state);
        _detail = detail ?? string.Empty;
        _command = command ?? string.Empty;
        _offersPrepHelp = offersPrepHelp;
        // One row, a handful of bindings: refresh them all rather than track which changed.
        OnPropertyChanged(string.Empty);
    }

    private string StateLabel(ConnectionStepState state) =>
        _localizationService.GetString($"ConnectionStepState{state}");
}
