using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.Core.Security;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.App.ViewModels;

public sealed class ServerEditorViewModel : ObservableObject, IDisposable
{
    private readonly IServerValidator _validator;
    private readonly ISshConnectionService _sshConnectionService;
    private readonly IHostKeyTrustStore _hostKeyTrustStore;
    private readonly IRoutedHostKeyTrustStore? _routedHostKeyTrustStore;
    private readonly IPrivateKeyFilePicker _privateKeyFilePicker;
    private readonly ILocalizationService _localizationService;
    private readonly Server? _existingServer;
    private readonly ISshConfigImportSource? _sshConfigImportSource;
    private readonly bool _portIsAddDefault;
    private bool _isPortEdited;
    private CancellationTokenSource? _testCancellation;
    private SecretValue? _secret;
    private CredentialContext? _secretContext;
    private string _name;
    private string _host;
    private string _port;
    private string _username;
    private string _privateKeyPath;
    private int _selectedOperatingSystemIndex;
    private int _selectedAuthenticationIndex;
    private int _selectedRefreshIntervalIndex;
    private bool _removeSavedPassphrase;
    private bool _hasValidationErrors;
    private bool _isTestingConnection;
    private bool _hasConnectionStatus;
    private bool _hasUnknownHostKey;
    private bool _hasHostKeyMismatch;
    private string _connectionStatusMessage = string.Empty;
    private string _presentedHostKeyAlgorithm = string.Empty;
    private string _presentedHostKeyFingerprint = string.Empty;
    private string _trustedHostKeyFingerprint = string.Empty;
    private HostKeyIdentity? _pendingHostKey;
    private SshHostKeyHop _pendingHostKeyHop;
    private SshEndpoint? _pendingHostKeyEndpoint;
    private SshRoute? _pendingHostKeyRoute;
    private string _hostKeySubjectDisplay = string.Empty;
    private bool _useJumpHost;
    private string _jumpHost = string.Empty;
    private string _jumpPort = "22";
    private bool _isJumpPortEdited;
    private string _jumpUsername = string.Empty;
    private int _selectedJumpAuthenticationIndex;
    private string _jumpPrivateKeyPath = string.Empty;
    private SecretValue? _jumpSecret;
    private JumpCredentialContext? _jumpSecretContext;
    private SshConnectionResult? _lastConnectionResult;
    private bool _isSshConfigImportOpen;
    private bool _isLoadingSshConfig;
    private string _sshConfigStatusMessage = string.Empty;
    private string _sshConfigFileWarningMessage = string.Empty;
    private IReadOnlyList<SshConfigHostOptionViewModel> _sshConfigHosts = [];
    private CancellationTokenSource? _sshConfigLoadCancellation;
    private SshConfigLoadOutcome _sshConfigLoadOutcome;
    private readonly ILocalSshKeyDiscovery? _localSshKeyDiscovery;
    // Cancelled (never disposed) when the editor goes away, so a discovery still running just stops.
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    // Where stage progress may touch bound properties: the thread/context the editor was created on.
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly SynchronizationContext? _ownerContext = SynchronizationContext.Current;
    private IReadOnlyList<LocalSshKey> _localKeys = [];
    private IReadOnlyList<LocalKeyOptionViewModel> _localKeyOptions = [];
    private bool _localKeyDiscoveryCompleted;
    private bool _isPrivateKeyAutoSelected;
    private StageProgressRelay? _activeStageProgress;
    // UI.7 B-7 (F-2): set once by Dispose. Nothing that resumes after an await may start work, write a store or change a
    // bound property once it is set. Volatile: a test/connection continuation may resume on another thread.
    private volatile bool _disposed;
    // UI.7 B-7 (Vigil H7): true while an accepted key is being written, so no submit path runs in that window.
    private bool _isTrustingHostKey;
    // UI.7 B-15: the form as it was opened (after ctor + prefill); IsDirty compares against it.
    private readonly DirtyState _openedState;
    private bool _isDirty;

    public ServerEditorViewModel(
        IServerValidator validator,
        ISshConnectionService sshConnectionService,
        IHostKeyTrustStore hostKeyTrustStore,
        IServerConnectionStateStore connectionStateStore,
        IPrivateKeyFilePicker privateKeyFilePicker,
        ILocalizationService localizationService,
        Server? server,
        ServerDiscoveryPrefill? prefill = null,
        ISshConfigImportSource? sshConfigImportSource = null,
        IRoutedHostKeyTrustStore? routedHostKeyTrustStore = null,
        ILocalSshKeyDiscovery? localSshKeyDiscovery = null)
    {
        _validator = validator;
        _sshConnectionService = sshConnectionService;
        _hostKeyTrustStore = hostKeyTrustStore;
        // Absent, a target key seen through a jump can never be trusted from this editor (fail closed).
        _routedHostKeyTrustStore = routedHostKeyTrustStore;
        // UI.7 B-8 (F-3): the editor no longer writes the saved server's connection state while testing a draft; that
        // state changes only through the post-Save path in DashboardViewModel. The parameter stays (same ctor/factory, B-2).
        _ = connectionStateStore;
        _privateKeyFilePicker = privateKeyFilePicker;
        _localizationService = localizationService;
        _existingServer = server;
        _sshConfigImportSource = sshConfigImportSource;
        // Discovery prefill only seeds an add (server is null): name/host/port come from the
        // suggestion, everything else keeps its blank add-mode default. It never turns an add into
        // an edit (_existingServer stays null) and never touches auth, credentials or the OS guess.
        _name = server?.Name ?? prefill?.Name ?? string.Empty;
        _host = server?.Host ?? prefill?.Host ?? string.Empty;
        _port = (server?.Port ?? prefill?.Port ?? 22).ToString(CultureInfo.InvariantCulture);
        // Only the untouched add-mode default counts as "empty" for an SSH config import; a saved,
        // discovered or typed port (even a typed 22) is a value the user already has.
        _portIsAddDefault = server is null && prefill is null;
        _username = server?.Username ?? string.Empty;
        _privateKeyPath = server?.PrivateKeyPath ?? string.Empty;
        _selectedOperatingSystemIndex = (int)(server?.OperatingSystem ?? ServerOperatingSystem.Auto);
        _selectedAuthenticationIndex = server?.AuthenticationMethod == AuthenticationMethod.Password ? 1 : 0;
        _selectedRefreshIntervalIndex = IndexOfInterval(
            server?.RefreshIntervalSeconds ?? RefreshIntervalPolicy.DefaultSeconds);
        if (server?.Route?.Jump is { } jump)
        {
            _useJumpHost = true;
            _jumpHost = jump.Host;
            _jumpPort = jump.Port.ToString(CultureInfo.InvariantCulture);
            _jumpUsername = jump.Username;
            _selectedJumpAuthenticationIndex = jump.AuthenticationMethod == AuthenticationMethod.Password ? 1 : 0;
            _jumpPrivateKeyPath = jump.PrivateKeyPath ?? string.Empty;
        }

        ConnectionChecklist = new ConnectionChecklistViewModel(localizationService);
        _localSshKeyDiscovery = localSshKeyDiscovery;
        // UI.7 B-15: the opened form (prefill included) is the clean state. Taken before discovery starts; a key the
        // editor pre-selects later is not an edit (see CaptureDirtyState).
        _openedState = CaptureDirtyState();
        PropertyChanged += OnOwnPropertyChanged;
        // Starts as the editor opens; metadata-only, off the UI thread inside the service.
        LocalKeyDiscovery = DiscoverLocalKeysAsync();
    }

    /// <summary>
    /// UI.7 B-15: the form differs from how it was opened (ctor + discovery prefill). A key the editor pre-selected is
    /// not an edit; an import, a typed or picked value, a staged secret and an OS detected by a test are. Reverting a
    /// field by hand makes the form clean again. In memory only.
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    /// <summary>
    /// UI.7 B-7 (Vigil H7): a test is running or an accepted key is being written. No submit path may produce a result
    /// meanwhile.
    /// </summary>
    public bool IsConnectionWorkInProgress => IsTestingConnection || _isTrustingHostKey;

    /// <summary>The name the editor was opened with (the "before" of the discard dialog's name diff).</summary>
    public string OpenedName => _openedState.Name;

    private void OnOwnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsDirty) || _disposed)
        {
            return;
        }

        IsDirty = CaptureDirtyState() != _openedState;
    }

    private void RefreshDirty()
    {
        if (!_disposed)
        {
            IsDirty = CaptureDirtyState() != _openedState;
        }
    }

    // A pre-selected key path counts as the opened value: only a user/import/picker choice is an edit.
    private DirtyState CaptureDirtyState() => new(
        Name,
        Host,
        Port,
        Username,
        IsPrivateKeyAutoSelected ? _openedState.PrivateKeyPath : PrivateKeyPath,
        SelectedOperatingSystemIndex,
        SelectedAuthenticationIndex,
        SelectedRefreshIntervalIndex,
        RemoveSavedPassphrase,
        UseJumpHost,
        JumpHost,
        JumpPort,
        JumpUsername,
        SelectedJumpAuthenticationIndex,
        JumpPrivateKeyPath,
        _secret is not null,
        _jumpSecret is not null);

    private readonly record struct DirtyState(
        string Name,
        string Host,
        string Port,
        string Username,
        string PrivateKeyPath,
        int OperatingSystem,
        int Authentication,
        int RefreshInterval,
        bool RemoveSavedPassphrase,
        bool UseJumpHost,
        string JumpHost,
        string JumpPort,
        string JumpUsername,
        int JumpAuthentication,
        string JumpPrivateKeyPath,
        bool HasStagedSecret,
        bool HasStagedJumpSecret);

    /// <summary>The four-step "Test connection" checklist (M14.5), shown while <see cref="HasConnectionStatus"/>.</summary>
    public ConnectionChecklistViewModel ConnectionChecklist { get; }

    /// <summary>Completes when the default-key discovery started by the constructor is over. Never faults.</summary>
    public Task LocalKeyDiscovery { get; }

    /// <summary>
    /// Default keys found in <c>.ssh</c> followed by "browse for another file"; empty when none was found
    /// (the editor then looks exactly as before). The target and the jump host share this list.
    /// </summary>
    public IReadOnlyList<LocalKeyOptionViewModel> LocalKeyOptions
    {
        get => _localKeyOptions;
        private set
        {
            if (SetProperty(ref _localKeyOptions, value))
            {
                OnPropertyChanged(nameof(HasLocalKeyOptions));
                OnPropertyChanged(nameof(SelectedLocalKeyOption));
                OnPropertyChanged(nameof(SelectedJumpLocalKeyOption));
            }
        }
    }

    public bool HasLocalKeyOptions => LocalKeyOptions.Count > 0;

    /// <summary>The found key the target's key path currently points at, or null (another file, or none).</summary>
    public LocalKeyOptionViewModel? SelectedLocalKeyOption => FindLocalKeyOption(PrivateKeyPath);

    public LocalKeyOptionViewModel? SelectedJumpLocalKeyOption => FindLocalKeyOption(JumpPrivateKeyPath);

    /// <summary>True while the key path is the recommended key this editor pre-selected, not a user/import choice.</summary>
    public bool IsPrivateKeyAutoSelected
    {
        get => _isPrivateKeyAutoSelected;
        private set
        {
            if (SetProperty(ref _isPrivateKeyAutoSelected, value))
            {
                OnPropertyChanged(nameof(PrivateKeyHint));
                OnPropertyChanged(nameof(HasPrivateKeyHint));
            }
        }
    }

    /// <summary>"Key found in .ssh" after a pre-selection; a pointer to the picker when discovery found nothing.</summary>
    public string PrivateKeyHint
    {
        get
        {
            if (IsPrivateKeyAutoSelected)
            {
                return _localizationService.GetString("LocalKeyAutoSelectedHint");
            }

            return _localSshKeyDiscovery is not null
                && _localKeyDiscoveryCompleted
                && _localKeys.Count == 0
                && string.IsNullOrWhiteSpace(PrivateKeyPath)
                    ? _localizationService.GetString("LocalKeyNoneFoundHint")
                    : string.Empty;
        }
    }

    public bool HasPrivateKeyHint => PrivateKeyHint.Length > 0;

    /// <summary>The user picked a found key for the target. The browse entry is handled by <see cref="SelectPrivateKeyAsync"/>.</summary>
    public void SelectLocalKey(LocalKeyOptionViewModel option)
    {
        if (option.Key is { } key && !IsTestingConnection)
        {
            PrivateKeyPath = key.Path;
        }
    }

    /// <summary>The user picked a found key for the jump host (never pre-selected).</summary>
    public void SelectJumpLocalKey(LocalKeyOptionViewModel option)
    {
        if (option.Key is { } key && !IsTestingConnection)
        {
            JumpPrivateKeyPath = key.Path;
        }
    }

    /// <summary>
    /// The helper's commands for the server currently in the form. Form values are substituted only when they
    /// pass <see cref="ServerPrepCommandBuilder"/>'s strict grammar; otherwise placeholders are shown.
    /// </summary>
    public ServerPrepCommands BuildServerPrepCommands() => ServerPrepCommandBuilder.Build(
        Username.Trim(),
        Host.Trim(),
        Port.Trim(),
        IsPrivateKeyAuthentication ? SelectedLocalKeyOption?.Key?.FileName : null,
        _localizationService.GetString("ServerPrepUserPlaceholder"),
        _localizationService.GetString("ServerPrepHostPlaceholder"),
        UseJumpHost,
        JumpUsername.Trim(),
        JumpHost.Trim(),
        JumpPort.Trim(),
        _localizationService.GetString("ServerPrepJumpPlaceholder"));

    /// <summary>What a copy button says once the clipboard has taken its command.</summary>
    public string CopiedLabel => _localizationService.GetString("ServerPrepCopied");

    private LocalKeyOptionViewModel? FindLocalKeyOption(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : LocalKeyOptions.FirstOrDefault(option =>
                option.Key is { } key && string.Equals(key.Path, path, StringComparison.OrdinalIgnoreCase));

    private async Task DiscoverLocalKeysAsync()
    {
        if (_localSshKeyDiscovery is null)
        {
            return;
        }

        IReadOnlyList<LocalSshKey> keys;
        try
        {
            keys = await _localSshKeyDiscovery.DiscoverAsync(_lifetimeCancellation.Token);
        }
        catch
        {
            // Cancelled with the editor, or a discovery that broke its "never throws" contract: no keys.
            keys = [];
        }

        if (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        _localKeys = keys.Where(key => !string.IsNullOrWhiteSpace(key.Path)).ToList();
        _localKeyDiscoveryCompleted = true;
        LocalKeyOptions = _localKeys.Count == 0
            ? []
            : _localKeys
                .Select(key => new LocalKeyOptionViewModel(
                    key,
                    key.IsRecommended ? Format("LocalKeyOptionRecommendedFormat", key.FileName) : key.FileName))
                .Append(new LocalKeyOptionViewModel(null, _localizationService.GetString("LocalKeyOptionBrowse")))
                .ToList();
        TryAutoSelectRecommendedKey();
        OnPropertyChanged(nameof(PrivateKeyHint));
        OnPropertyChanged(nameof(HasPrivateKeyHint));
    }

    /// <summary>
    /// Add mode only, and only into a key path that is still empty once discovery is over: an imported or
    /// chosen path is never replaced, a saved server is never touched, and nothing changes under a test.
    /// </summary>
    private void TryAutoSelectRecommendedKey()
    {
        if (_existingServer is not null
            || !IsPrivateKeyAuthentication
            || !string.IsNullOrWhiteSpace(PrivateKeyPath)
            || IsTestingConnection
            || HasConnectionStatus)
        {
            return;
        }

        if (_localKeys.FirstOrDefault(key => key.IsRecommended) is { } recommended)
        {
            SetPrivateKeyPath(recommended.Path, autoSelected: true);
        }
    }

    private void SetPrivateKeyPath(string value, bool autoSelected)
    {
        var changed = SetSecurityContextProperty(ref _privateKeyPath, value, nameof(PrivateKeyPath));
        if (changed || !autoSelected)
        {
            // Any path set by the user, the picker or an import is theirs, even when it equals the pre-selection.
            IsPrivateKeyAutoSelected = autoSelected;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(HasSavedPassphrase));
            OnPropertyChanged(nameof(SelectedLocalKeyOption));
            OnPropertyChanged(nameof(PrivateKeyHint));
            OnPropertyChanged(nameof(HasPrivateKeyHint));
        }
    }

    /// <summary>"Connect through a jump host": the server is then reached ONLY through the jump (single hop).</summary>
    public bool UseJumpHost
    {
        get => _useJumpHost;
        set
        {
            if (SetJumpContextProperty(ref _useJumpHost, value))
            {
                OnPropertyChanged(nameof(IsJumpPrivateKeyAuthentication));
                OnPropertyChanged(nameof(IsJumpPasswordAuthentication));
                OnPropertyChanged(nameof(HasSavedJumpSecret));
            }
        }
    }

    public string JumpHost { get => _jumpHost; set => SetJumpContextProperty(ref _jumpHost, value); }

    public string JumpPort
    {
        get => _jumpPort;
        set
        {
            if (SetJumpContextProperty(ref _jumpPort, value))
            {
                _isJumpPortEdited = true;
            }
        }
    }

    public string JumpUsername { get => _jumpUsername; set => SetJumpContextProperty(ref _jumpUsername, value); }

    public int SelectedJumpAuthenticationIndex
    {
        get => _selectedJumpAuthenticationIndex;
        set
        {
            if (SetJumpContextProperty(ref _selectedJumpAuthenticationIndex, value))
            {
                OnPropertyChanged(nameof(IsJumpPrivateKeyAuthentication));
                OnPropertyChanged(nameof(IsJumpPasswordAuthentication));
                OnPropertyChanged(nameof(HasSavedJumpSecret));
            }
        }
    }

    public string JumpPrivateKeyPath
    {
        get => _jumpPrivateKeyPath;
        set
        {
            if (SetJumpContextProperty(ref _jumpPrivateKeyPath, value))
            {
                OnPropertyChanged(nameof(HasSavedJumpSecret));
                OnPropertyChanged(nameof(SelectedJumpLocalKeyOption));
            }
        }
    }

    public bool IsJumpPrivateKeyAuthentication => UseJumpHost && SelectedJumpAuthenticationIndex == 0;

    public bool IsJumpPasswordAuthentication => UseJumpHost && SelectedJumpAuthenticationIndex == 1;

    /// <summary>The jump host's secret is protected in Credential Manager and still belongs to this jump login.</summary>
    public bool HasSavedJumpSecret => UseJumpHost && GetExistingJumpCredentialReference() is not null;

    /// <summary>
    /// Who presented the key in the trust panels: the server itself, "jump host bastion:22", or
    /// "target 10.0.0.5:22 via bastion:22". Never the tunnel's loopback address.
    /// </summary>
    public string HostKeySubjectDisplay
    {
        get => _hostKeySubjectDisplay;
        private set => SetProperty(ref _hostKeySubjectDisplay, value);
    }

    /// <summary>
    /// UI.7B (presentation only): which hop presented the key of the trust prompt or mismatch on screen, read from the
    /// result that raised it; null when neither is shown. The trust dialog derives "PASSO 1/2 DE 2" from it. Read-only:
    /// it never feeds <see cref="TrustAndConnectAsync"/>, whose hop check stays on the pending key.
    /// </summary>
    public SshHostKeyHop? HostKeyPromptHop =>
        (HasUnknownHostKey || HasHostKeyMismatch) && _lastConnectionResult is { } result ? result.HostKeyHop : null;

    public void CaptureJumpSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) || !UseJumpHost)
        {
            return;
        }

        _jumpSecret?.Dispose();
        _jumpSecret = new SecretValue(value.AsSpan());
        _jumpSecretContext = TryCreateJumpCredentialContext(out var context) ? context : null;
        InvalidateConnectionResult();
        RefreshDirty();
    }

    public async Task SelectJumpPrivateKeyAsync()
    {
        var selected = await _privateKeyFilePicker.PickAsync();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            JumpPrivateKeyPath = selected;
        }

        // A cancelled picker leaves the path alone: let the selector snap back from its browse entry.
        OnPropertyChanged(nameof(SelectedJumpLocalKeyOption));
    }

    public string Name { get => _name; set => SetEditorProperty(ref _name, value); }

    public string Host { get => _host; set => SetSecurityContextProperty(ref _host, value); }

    public string Port
    {
        get => _port;
        set
        {
            if (SetSecurityContextProperty(ref _port, value))
            {
                _isPortEdited = true;
            }
        }
    }

    public string Username { get => _username; set => SetSecurityContextProperty(ref _username, value); }

    public string PrivateKeyPath
    {
        get => _privateKeyPath;
        set => SetPrivateKeyPath(value, autoSelected: false);
    }

    public int SelectedOperatingSystemIndex
    {
        get => _selectedOperatingSystemIndex;
        set => SetEditorProperty(ref _selectedOperatingSystemIndex, value);
    }

    public int SelectedAuthenticationIndex
    {
        get => _selectedAuthenticationIndex;
        set
        {
            if (SetSecurityContextProperty(ref _selectedAuthenticationIndex, value))
            {
                OnPropertyChanged(nameof(IsPrivateKeyAuthentication));
                OnPropertyChanged(nameof(IsPasswordAuthentication));
                OnPropertyChanged(nameof(HasSavedPassphrase));
                OnPropertyChanged(nameof(HasSavedPassword));
            }
        }
    }

    /// <summary>
    /// Index into <see cref="RefreshIntervalPolicy.SupportedSeconds"/> (10 s, 30 s, 1 min, 5 min).
    /// Automatic monitoring only; it does not affect the connection test, so changing it does
    /// not invalidate a verified connection.
    /// </summary>
    public int SelectedRefreshIntervalIndex
    {
        get => _selectedRefreshIntervalIndex;
        set => SetProperty(ref _selectedRefreshIntervalIndex, value);
    }

    private int SelectedRefreshIntervalSeconds
    {
        get
        {
            var options = RefreshIntervalPolicy.SupportedSeconds;
            var index = Math.Clamp(SelectedRefreshIntervalIndex, 0, options.Count - 1);
            return options[index];
        }
    }

    public bool IsPrivateKeyAuthentication => SelectedAuthenticationIndex == 0;

    public bool IsPasswordAuthentication => SelectedAuthenticationIndex == 1;

    public bool HasSavedPassphrase => IsPrivateKeyAuthentication
        && _existingServer?.AuthenticationMethod == AuthenticationMethod.SshKey
        && _existingServer.CredentialReferenceId is not null;

    public bool HasSavedPassword => IsPasswordAuthentication
        && _existingServer?.AuthenticationMethod == AuthenticationMethod.Password
        && _existingServer.CredentialReferenceId is not null;

    public bool RemoveSavedPassphrase
    {
        get => _removeSavedPassphrase;
        set => SetEditorProperty(ref _removeSavedPassphrase, value);
    }

    public bool HasValidationErrors
    {
        get => _hasValidationErrors;
        private set => SetProperty(ref _hasValidationErrors, value);
    }

    public bool IsTestingConnection
    {
        get => _isTestingConnection;
        private set
        {
            if (SetProperty(ref _isTestingConnection, value))
            {
                OnPropertyChanged(nameof(IsNotTestingConnection));
                OnPropertyChanged(nameof(IsConnectionWorkInProgress));
            }
        }
    }

    public bool IsNotTestingConnection => !IsTestingConnection;

    public bool HasConnectionStatus
    {
        get => _hasConnectionStatus;
        private set => SetProperty(ref _hasConnectionStatus, value);
    }

    public bool HasUnknownHostKey
    {
        get => _hasUnknownHostKey;
        private set => SetProperty(ref _hasUnknownHostKey, value);
    }

    public bool HasHostKeyMismatch
    {
        get => _hasHostKeyMismatch;
        private set => SetProperty(ref _hasHostKeyMismatch, value);
    }

    public string ConnectionStatusMessage
    {
        get => _connectionStatusMessage;
        private set => SetProperty(ref _connectionStatusMessage, value);
    }

    public string PresentedHostKeyAlgorithm
    {
        get => _presentedHostKeyAlgorithm;
        private set => SetProperty(ref _presentedHostKeyAlgorithm, value);
    }

    public string PresentedHostKeyFingerprint
    {
        get => _presentedHostKeyFingerprint;
        private set => SetProperty(ref _presentedHostKeyFingerprint, value);
    }

    public string TrustedHostKeyFingerprint
    {
        get => _trustedHostKeyFingerprint;
        private set => SetProperty(ref _trustedHostKeyFingerprint, value);
    }

    public string EndpointDisplay => TryCreateEndpoint(out var endpoint)
        ? endpoint!.ToString()
        : Host;

    /// <summary>Import is offered only when adding a server; an edit never pulls values from ssh config.</summary>
    public bool IsSshConfigImportAvailable => _sshConfigImportSource is not null && _existingServer is null;

    public bool IsSshConfigImportOpen
    {
        get => _isSshConfigImportOpen;
        private set => SetProperty(ref _isSshConfigImportOpen, value);
    }

    public bool IsLoadingSshConfig
    {
        get => _isLoadingSshConfig;
        private set => SetProperty(ref _isLoadingSshConfig, value);
    }

    public IReadOnlyList<SshConfigHostOptionViewModel> SshConfigHosts
    {
        get => _sshConfigHosts;
        private set
        {
            if (SetProperty(ref _sshConfigHosts, value))
            {
                OnPropertyChanged(nameof(HasSshConfigHosts));
            }
        }
    }

    public bool HasSshConfigHosts => SshConfigHosts.Count > 0;

    public string SshConfigStatusMessage
    {
        get => _sshConfigStatusMessage;
        private set
        {
            if (SetProperty(ref _sshConfigStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasSshConfigStatus));
            }
        }
    }

    public bool HasSshConfigStatus => SshConfigStatusMessage.Length > 0;

    public string SshConfigFileWarningMessage
    {
        get => _sshConfigFileWarningMessage;
        private set
        {
            if (SetProperty(ref _sshConfigFileWarningMessage, value))
            {
                OnPropertyChanged(nameof(HasSshConfigFileWarning));
            }
        }
    }

    public bool HasSshConfigFileWarning => SshConfigFileWarningMessage.Length > 0;

    /// <summary>
    /// UI.7B (presentation only): what the last load of <c>~/.ssh/config</c> ended with, so the import dialog can pick
    /// the Figma 07 state (no file, unreadable, no profiles, a list). The message itself stays
    /// <see cref="SshConfigStatusMessage"/>; nothing about the classification of a host comes from here.
    /// </summary>
    public SshConfigLoadOutcome SshConfigLoadOutcome
    {
        get => _sshConfigLoadOutcome;
        private set => SetProperty(ref _sshConfigLoadOutcome, value);
    }

    /// <summary>
    /// Reads <c>~/.ssh/config</c> (read-only) and lists its concrete aliases for preview. Nothing
    /// in the form changes until the user picks one with <see cref="ApplySshConfigHost(SshConfigHostEntry)"/>.
    /// </summary>
    public async Task LoadSshConfigHostsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSshConfigImportAvailable || IsLoadingSshConfig)
        {
            return;
        }

        IsSshConfigImportOpen = true;
        IsLoadingSshConfig = true;
        SshConfigHosts = [];
        SshConfigStatusMessage = string.Empty;
        SshConfigFileWarningMessage = string.Empty;
        SshConfigLoadOutcome = SshConfigLoadOutcome.None;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sshConfigLoadCancellation = cancellation;
        try
        {
            SshConfigImportResult result;
            try
            {
                result = await _sshConfigImportSource!.LoadAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Closed (or the editor went away) while loading: nothing to show, nothing to report.
                return;
            }
            catch
            {
                result = SshConfigImportResult.Failed(SshConfigImportErrorCode.Unreadable);
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            switch (result.Status)
            {
                case SshConfigImportStatus.NotFound:
                    SshConfigStatusMessage = _localizationService.GetString("SshConfigImportNotFound");
                    SshConfigLoadOutcome = SshConfigLoadOutcome.NotFound;
                    break;
                case SshConfigImportStatus.Error:
                    var error = _localizationService.GetString($"SshConfigImportError{result.ErrorCode}");
                    SshConfigStatusMessage = result.ErrorDetail is null
                        ? error
                        : error + " " + Format("SshConfigImportErrorFileFormat", result.ErrorDetail);
                    SshConfigLoadOutcome = SshConfigLoadOutcome.Error;
                    break;
                default:
                    SshConfigHosts = result.Hosts
                        .Select(host => new SshConfigHostOptionViewModel(host, _localizationService))
                        .ToList();
                    SshConfigStatusMessage = result.Hosts.Count == 0
                        ? _localizationService.GetString("SshConfigImportNoHosts")
                        : string.Empty;
                    SshConfigFileWarningMessage = string.Join(
                        Environment.NewLine,
                        result.FileWarnings
                            .Select(warning => _localizationService.GetString($"SshConfigImportWarning{warning}"))
                            .Concat(result.Diagnostics.Select(DescribeDiagnostic)));
                    SshConfigLoadOutcome = result.Hosts.Count == 0 ? SshConfigLoadOutcome.NoHosts : SshConfigLoadOutcome.Hosts;
                    break;
            }
        }
        finally
        {
            IsLoadingSshConfig = false;
            if (ReferenceEquals(_sshConfigLoadCancellation, cancellation))
            {
                _sshConfigLoadCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private string DescribeDiagnostic(SshConfigDiagnostic diagnostic) => diagnostic.Kind switch
    {
        SshConfigDiagnosticKind.IncludeMatchedNoFiles =>
            Format("SshConfigImportDiagnosticIncludeMatchedNoFiles", diagnostic.Argument),
        _ => Format(
            "SshConfigImportDiagnosticIncludeNotVerified",
            diagnostic.Argument,
            _localizationService.GetString($"SshConfigIncludeIssue{diagnostic.Issue}"))
    };

    private string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, _localizationService.GetString(key), arguments);

    /// <summary>Closes the panel; a load still in progress is cancelled and its result discarded.</summary>
    public void CloseSshConfigImport()
    {
        _sshConfigLoadCancellation?.Cancel();
        IsSshConfigImportOpen = false;
        SshConfigHosts = [];
        SshConfigStatusMessage = string.Empty;
        SshConfigFileWarningMessage = string.Empty;
        SshConfigLoadOutcome = SshConfigLoadOutcome.None;
    }

    public bool ApplySshConfigHost(SshConfigHostOptionViewModel option) => ApplySshConfigHost(option.Entry);

    /// <summary>
    /// Fills only the form fields that are still empty (or the untouched add-mode port) from one
    /// alias. What the user already typed, the chosen authentication method and any saved server
    /// are never changed. A host with a verified single-hop ProxyJump also fills the jump host (see
    /// <see cref="SshConfigHostEntry.Jump"/>); any other proxy is refused, never imported as a direct
    /// connection. Password authentication is never inferred, and no secret or trust is ever set.
    /// </summary>
    public bool ApplySshConfigHost(SshConfigHostEntry entry)
    {
        if (!IsSshConfigImportAvailable || !entry.IsImportable || IsTestingConnection)
        {
            return false;
        }

        // User, port and key belong to a host. They are only taken when the entry's host is known
        // and the form's host is empty (and so comes from this entry) or already is that host —
        // never mixed into a host the user typed, nor attached to an unresolved HostName.
        var before = SshConfigImportFields();
        var hostWasEmpty = string.IsNullOrWhiteSpace(Host);
        var sameHost = entry.HostName is not null
            && (hostWasEmpty || string.Equals(Host.Trim(), entry.HostName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = entry.Alias;
        }

        if (hostWasEmpty && entry.HostName is not null)
        {
            Host = entry.HostName;
        }

        if (sameHost)
        {
            if (entry.Port is { } port
                && (string.IsNullOrWhiteSpace(Port) || (_portIsAddDefault && !_isPortEdited)))
            {
                Port = port.ToString(CultureInfo.InvariantCulture);
            }

            if (string.IsNullOrWhiteSpace(Username) && entry.User is not null)
            {
                Username = entry.User;
            }

            // The add-mode default is key authentication with no key chosen, i.e. "auth not set
            // yet". A form already on password auth, or with a key path, is left exactly as it is —
            // except a path this editor pre-selected from .ssh (M14.5): the host's own IdentityFile wins.
            if (entry.IdentityFile is not null
                && IsPrivateKeyAuthentication
                && (string.IsNullOrWhiteSpace(PrivateKeyPath) || IsPrivateKeyAutoSelected))
            {
                PrivateKeyPath = entry.IdentityFile;
            }
        }

        // The route belongs to the host as well: whenever the form's host comes from this entry (or
        // already is it), a host ssh reaches through a jump is switched to the jump, never left direct.
        // Jump fields follow the host rule: only empty ones (or the untouched default jump port) are
        // filled, and only while the form's jump host is empty or already this jump.
        if (entry.Jump is { } jump && (hostWasEmpty || sameHost))
        {
            UseJumpHost = true;
            var jumpHostWasEmpty = string.IsNullOrWhiteSpace(JumpHost);
            if (jumpHostWasEmpty)
            {
                JumpHost = jump.HostName;
            }

            if (jumpHostWasEmpty || string.Equals(JumpHost.Trim(), jump.HostName, StringComparison.OrdinalIgnoreCase))
            {
                if (jump.Port is { } jumpPort
                    && (string.IsNullOrWhiteSpace(JumpPort) || !_isJumpPortEdited))
                {
                    JumpPort = jumpPort.ToString(CultureInfo.InvariantCulture);
                }

                if (string.IsNullOrWhiteSpace(JumpUsername) && jump.User is not null)
                {
                    JumpUsername = jump.User;
                }

                if (jump.IdentityFile is not null
                    && IsJumpPrivateKeyAuthentication
                    && string.IsNullOrWhiteSpace(JumpPrivateKeyPath))
                {
                    JumpPrivateKeyPath = jump.IdentityFile;
                }
            }
        }

        // The status says what actually happened: "filled" only when a field changed; otherwise why
        // nothing was (a typed host that is not this entry's), never a claim that fields were filled.
        var messageKey = SshConfigImportFields() != before
            ? "SshConfigImportAppliedFormat"
            : !hostWasEmpty && !sameHost && entry.HostName is not null
                ? "SshConfigImportNothingFilledHostDiffersFormat"
                : "SshConfigImportNothingFilledFormat";
        CloseSshConfigImport();
        SshConfigStatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            _localizationService.GetString(messageKey),
            entry.Alias);
        return true;
    }

    // Every form field ApplySshConfigHost may write, compared before/after to tell whether it changed anything.
    private (string, string, string, string, string, bool, string, string, string, string) SshConfigImportFields() =>
        (Name, Host, Port, Username, PrivateKeyPath, UseJumpHost, JumpHost, JumpPort, JumpUsername, JumpPrivateKeyPath);

    public void CaptureSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        _secret?.Dispose();
        _secret = new SecretValue(value.AsSpan());
        _secretContext = TryCreateCredentialContext(out var context) ? context : null;
        RemoveSavedPassphrase = false;
        InvalidateConnectionResult();
        RefreshDirty();
    }

    public async Task SelectPrivateKeyAsync()
    {
        var selected = await _privateKeyFilePicker.PickAsync();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            PrivateKeyPath = selected;
        }

        // A cancelled picker leaves the path alone: let the selector snap back from its browse entry.
        OnPropertyChanged(nameof(SelectedLocalKeyOption));
    }

    public async Task TestConnectionAsync()
    {
        // UI.7 B-7 (F-2): a disposed editor never starts a connection (no new CancellationTokenSource after Dispose).
        if (_disposed)
        {
            return;
        }

        EnsureStagedSecretMatchesCurrentContext();
        if (!TryCreateDraft(out var draft))
        {
            return;
        }

        ResetHostKeyPanels();
        _testCancellation?.Dispose();
        _testCancellation = new CancellationTokenSource();
        IsTestingConnection = true;
        SetConnectionState(new SshConnectionResult
        {
            State = ServerConnectionState.Connecting,
            ErrorCode = SshConnectionErrorCode.None
        });
        // A routed server's first step is the port as reached THROUGH the jump: name the jump on that row.
        ConnectionChecklist.Begin(draft!.Route?.Jump is { } jump
            ? $"{jump.Host}:{jump.Port.ToString(CultureInfo.InvariantCulture)}"
            : null);
        var progress = new StageProgressRelay(this);
        _activeStageProgress = progress;

        try
        {
            var result = await _sshConnectionService.TestConnectionAsync(
                new SshConnectionRequest
                {
                    Server = draft!,
                    CredentialOverride = _secret,
                    JumpCredentialOverride = draft!.Route is null ? null : _jumpSecret,
                    Timeout = TimeSpan.FromSeconds(10),
                    StageProgress = progress
                },
                _testCancellation.Token);
            ApplyConnectionResult(result);
        }
        catch (OperationCanceledException)
        {
            // No result to read a stage from: the steps reported before the cancellation are what was observed.
            ApplyConnectionResult(new SshConnectionResult
            {
                State = ServerConnectionState.Cancelled,
                ErrorCode = SshConnectionErrorCode.Cancelled,
                ReachedStage = ConnectionChecklist.ReportedStage
            });
        }
        catch
        {
            ApplyConnectionResult(new SshConnectionResult
            {
                State = ServerConnectionState.Error,
                ErrorCode = SshConnectionErrorCode.Unexpected,
                ReachedStage = ConnectionChecklist.ReportedStage
            });
        }
        finally
        {
            if (ReferenceEquals(_activeStageProgress, progress))
            {
                _activeStageProgress = null;
            }

            // UI.7 B-7: no bound property changes once the editor is gone.
            if (!_disposed)
            {
                IsTestingConnection = false;
            }
        }
    }

    // Stage progress may be reported from the connection's worker thread. It is applied where the editor
    // lives (inline when already there), and only while its own test is still the running one: a report
    // that arrives after the result was applied can no longer change the checklist.
    private void OnStageProgress(StageProgressRelay source, SshConnectionStage stage)
    {
        if (!_disposed && ReferenceEquals(_activeStageProgress, source))
        {
            ConnectionChecklist.Report(stage);
        }
    }

    private sealed class StageProgressRelay(ServerEditorViewModel owner) : IProgress<SshConnectionStage>
    {
        public void Report(SshConnectionStage value)
        {
            var context = owner._ownerContext;
            if (context is null
                || Environment.CurrentManagedThreadId == owner._ownerThreadId
                || ReferenceEquals(SynchronizationContext.Current, context))
            {
                owner.OnStageProgress(this, value);
            }
            else
            {
                context.Post(_ => owner.OnStageProgress(this, value), null);
            }
        }
    }

    /// <summary>
    /// Trusts the presented key in the ONE store its hop belongs to, then retests (M14.4b-2 two-step flow):
    /// a direct server's key → direct store under the server endpoint; a jump host's key → direct store under
    /// the JUMP endpoint; a target seen through the jump → routed store under (jump, target). A target key never
    /// reaches the direct store, a jump key never reaches the routed store, and a key whose hop no longer
    /// matches the form (the route was edited meanwhile) is not trusted at all.
    /// </summary>
    public async Task TrustAndConnectAsync()
    {
        // UI.7 B-7: a disposed editor trusts nothing.
        if (_pendingHostKey is null || _disposed)
        {
            return;
        }

        var presentedHostKey = _pendingHostKey;
        var hop = _pendingHostKeyHop;
        SshEndpoint? directEndpoint = null;
        SshRoute? route = null;
        switch (hop)
        {
            case SshHostKeyHop.Direct when !UseJumpHost
                && TryCreateEndpoint(out var endpoint)
                && _pendingHostKeyEndpoint is not null
                && endpoint == _pendingHostKeyEndpoint:
                directEndpoint = endpoint;
                break;
            case SshHostKeyHop.Jump when TryCreateRoute(out var jumpRoute)
                && jumpRoute!.Via == _pendingHostKeyEndpoint:
                directEndpoint = jumpRoute.Via;
                break;
            case SshHostKeyHop.Target when _routedHostKeyTrustStore is not null
                && TryCreateRoute(out var targetRoute)
                && targetRoute == _pendingHostKeyRoute:
                route = targetRoute;
                break;
            default:
                DismissHostKeyPrompt();
                return;
        }

        try
        {
            // UI.7 B-7 (Vigil H7): no submit path runs while the accepted key is being written.
            SetTrustingHostKey(true);
            try
            {
                if (route is not null)
                {
                    await _routedHostKeyTrustStore!.TrustAsync(route, presentedHostKey);
                }
                else
                {
                    await _hostKeyTrustStore.TrustAsync(directEndpoint!, presentedHostKey);
                }
            }
            finally
            {
                SetTrustingHostKey(false);
            }

            // UI.7 B-7 (F-2): an accepted key already being written may complete, but nothing after it runs once the
            // editor is gone - no retest (no new connection), no state or property change.
            if (_disposed)
            {
                return;
            }

            HasUnknownHostKey = false;
            _pendingHostKey = null;
            await TestConnectionAsync();
        }
        catch (HostKeyTrustConflictException)
        {
            ApplyConnectionResult(route is not null
                ? new SshConnectionResult
                {
                    State = ServerConnectionState.HostKeyMismatch,
                    ErrorCode = SshConnectionErrorCode.RoutedHostKeyMismatch,
                    // The key was presented by the test that led here, so the port did answer.
                    ReachedStage = SshConnectionStage.PortReachable,
                    PresentedHostKey = presentedHostKey,
                    HostKeyHop = SshHostKeyHop.Target,
                    HostKeyRoute = route,
                    TrustedRoutedHostKey = await _routedHostKeyTrustStore!.GetAsync(route)
                }
                : new SshConnectionResult
                {
                    State = ServerConnectionState.HostKeyMismatch,
                    ErrorCode = hop == SshHostKeyHop.Jump
                        ? SshConnectionErrorCode.JumpHostKeyMismatch
                        : SshConnectionErrorCode.HostKeyMismatch,
                    // A jump-stage failure reaches no step of the target; a direct server's port did answer.
                    ReachedStage = hop == SshHostKeyHop.Jump
                        ? SshConnectionStage.None
                        : SshConnectionStage.PortReachable,
                    PresentedHostKey = presentedHostKey,
                    HostKeyHop = hop,
                    HostKeyEndpoint = directEndpoint,
                    TrustedHostKey = await _hostKeyTrustStore.GetAsync(directEndpoint!)
                });
        }
        catch (ConfigurationLockedException)
        {
            // A restore holds the configuration: nothing was trusted, and retrying cannot help.
            ApplyConnectionResult(new SshConnectionResult
            {
                State = ServerConnectionState.Error,
                ErrorCode = SshConnectionErrorCode.Unexpected,
                ReachedStage = ConnectionChecklist.ReportedStage
            });
            if (!_disposed)
            {
                ConnectionStatusMessage = _localizationService.GetString(BackupMessageKeys.ConfigurationLocked);
            }
        }
        catch
        {
            // The trust store could not be written: the steps the last test reached still stand.
            ApplyConnectionResult(new SshConnectionResult
            {
                State = ServerConnectionState.Error,
                ErrorCode = SshConnectionErrorCode.Unexpected,
                ReachedStage = ConnectionChecklist.ReportedStage
            });
        }
    }

    public void CancelTest() => _testCancellation?.Cancel();

    private void SetTrustingHostKey(bool value)
    {
        if (_isTrustingHostKey == value || _disposed)
        {
            _isTrustingHostKey = value;
            return;
        }

        _isTrustingHostKey = value;
        OnPropertyChanged(nameof(IsConnectionWorkInProgress));
    }

    public void DismissHostKeyPrompt()
    {
        HasUnknownHostKey = false;
        _pendingHostKey = null;
        _pendingHostKeyHop = SshHostKeyHop.Direct;
        _pendingHostKeyEndpoint = null;
        _pendingHostKeyRoute = null;
    }

    public bool TryCreateResult(out ServerEditorResult? result)
    {
        result = null;
        EnsureStagedSecretMatchesCurrentContext();
        if (!TryCreateDraft(out var draft))
        {
            return false;
        }

        var configuration = new ServerInput
        {
            Name = draft!.Name,
            Host = draft.Host,
            Port = draft.Port,
            Username = draft.Username,
            OperatingSystem = draft.OperatingSystem,
            AuthenticationMethod = draft.AuthenticationMethod,
            PrivateKeyPath = draft.PrivateKeyPath,
            CredentialReferenceId = draft.CredentialReferenceId,
            RefreshIntervalSeconds = SelectedRefreshIntervalSeconds,
            // The route comes from the "Connect through a jump host" section; unchecking it is the ONLY way an
            // edit turns a routed server into a direct one.
            Route = draft.Route
        };

        CredentialChange? jumpCredentialChange = null;
        if (draft.Route?.Jump is { } draftJump)
        {
            if (_jumpSecret is not null)
            {
                jumpCredentialChange = CredentialChange.Replace(_jumpSecret);
                _jumpSecret = null;
                _jumpSecretContext = null;
            }
            else if (draftJump.CredentialReferenceId is null && _existingServer?.Route?.Jump?.CredentialReferenceId is not null)
            {
                // The saved jump secret belongs to another jump login (host/user/auth/key changed): drop it.
                jumpCredentialChange = CredentialChange.Clear;
            }
        }

        CredentialChange credentialChange;
        if (_secret is not null)
        {
            credentialChange = CredentialChange.Replace(_secret);
            _secret = null;
            _secretContext = null;
        }
        else if (ShouldKeepExistingCredential(configuration))
        {
            credentialChange = CredentialChange.Keep;
        }
        else
        {
            credentialChange = CredentialChange.Clear;
        }

        result = new ServerEditorResult
        {
            Profile = new ServerProfileInput
            {
                Configuration = configuration,
                CredentialChange = credentialChange,
                JumpCredentialChange = jumpCredentialChange
            },
            ConnectionResult = _lastConnectionResult
        };
        // UI.7A fix c1 (Cortex n-2): the staged secrets were just handed over, so the dirty state is recomputed (additive).
        RefreshDirty();
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetimeCancellation.Cancel();
        _sshConfigLoadCancellation?.Cancel();
        _testCancellation?.Cancel();
        _testCancellation?.Dispose();
        _secret?.Dispose();
        _secret = null;
        _secretContext = null;
        _jumpSecret?.Dispose();
        _jumpSecret = null;
        _jumpSecretContext = null;
    }

    private bool TryCreateDraft(out Server? draft)
    {
        draft = null;
        if (!int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort))
        {
            HasValidationErrors = true;
            return false;
        }

        var operatingSystem = Enum.IsDefined((ServerOperatingSystem)SelectedOperatingSystemIndex)
            ? (ServerOperatingSystem)SelectedOperatingSystemIndex
            : ServerOperatingSystem.Unknown;
        var authenticationMethod = IsPasswordAuthentication
            ? AuthenticationMethod.Password
            : AuthenticationMethod.SshKey;
        ServerRoute? route = null;
        if (UseJumpHost)
        {
            if (!int.TryParse(JumpPort, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedJumpPort))
            {
                HasValidationErrors = true;
                return false;
            }

            var jumpAuthentication = SelectedJumpAuthenticationIndex == 1
                ? AuthenticationMethod.Password
                : AuthenticationMethod.SshKey;
            route = new ServerRoute
            {
                Jump = new JumpHop
                {
                    Host = JumpHost.Trim(),
                    Port = parsedJumpPort,
                    Username = JumpUsername.Trim(),
                    AuthenticationMethod = jumpAuthentication,
                    PrivateKeyPath = jumpAuthentication == AuthenticationMethod.SshKey && !string.IsNullOrWhiteSpace(JumpPrivateKeyPath)
                        ? JumpPrivateKeyPath
                        : null,
                    CredentialReferenceId = GetExistingJumpCredentialReference()
                }
            };
        }

        var input = new ServerInput
        {
            Name = Name,
            Host = Host,
            Port = parsedPort,
            Username = Username,
            OperatingSystem = operatingSystem,
            AuthenticationMethod = authenticationMethod,
            PrivateKeyPath = IsPrivateKeyAuthentication ? PrivateKeyPath : null,
            CredentialReferenceId = GetExistingCredentialReference(authenticationMethod),
            Route = route
        };

        EnsureStagedJumpSecretMatchesCurrentContext();
        var validation = _validator.ValidateDraft(input);
        var passwordMissing = authenticationMethod == AuthenticationMethod.Password
            && _secret is null
            && input.CredentialReferenceId is null;
        var jumpPasswordMissing = route?.Jump is { AuthenticationMethod: AuthenticationMethod.Password } jumpHop
            && _jumpSecret is null
            && jumpHop.CredentialReferenceId is null;
        HasValidationErrors = !validation.IsValid || passwordMissing || jumpPasswordMissing;
        if (HasValidationErrors)
        {
            return false;
        }

        draft = new Server
        {
            Id = _existingServer?.Id ?? Guid.Empty,
            Name = input.Name.Trim(),
            Host = input.Host.Trim(),
            Port = input.Port,
            Username = input.Username.Trim(),
            OperatingSystem = input.OperatingSystem,
            AuthenticationMethod = input.AuthenticationMethod,
            PrivateKeyPath = string.IsNullOrWhiteSpace(input.PrivateKeyPath) ? null : input.PrivateKeyPath,
            CredentialReferenceId = input.CredentialReferenceId,
            CreatedAt = _existingServer?.CreatedAt ?? DateTimeOffset.UtcNow,
            Route = route
        };
        return true;
    }

    private Guid? GetExistingCredentialReference(AuthenticationMethod authenticationMethod)
    {
        if (_existingServer?.AuthenticationMethod != authenticationMethod)
        {
            return null;
        }

        if (authenticationMethod == AuthenticationMethod.SshKey
            && !string.Equals(_existingServer.PrivateKeyPath, PrivateKeyPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TryCreateCredentialContext(_existingServer, out var existingContext)
            && TryCreateCredentialContext(
                Host,
                Port,
                Username,
                authenticationMethod,
                PrivateKeyPath,
                out var currentContext)
            && existingContext == currentContext
                ? _existingServer.CredentialReferenceId
                : null;
    }

    /// <summary>
    /// The existing jump credential reference, kept only while it still belongs to the same jump login
    /// (endpoint, user, auth kind and — for a key — the same key file).
    /// </summary>
    private Guid? GetExistingJumpCredentialReference()
    {
        if (_existingServer?.Route?.Jump is not { CredentialReferenceId: not null } existing)
        {
            return null;
        }

        return TryCreateJumpCredentialContext(out var current)
            && TryCreateJumpCredentialContext(
                existing.Host,
                existing.Port.ToString(CultureInfo.InvariantCulture),
                existing.Username,
                existing.AuthenticationMethod,
                existing.PrivateKeyPath,
                out var saved)
            && current == saved
                ? existing.CredentialReferenceId
                : null;
    }

    private bool TryCreateJumpCredentialContext(out JumpCredentialContext? context) =>
        TryCreateJumpCredentialContext(
            JumpHost,
            JumpPort,
            JumpUsername,
            SelectedJumpAuthenticationIndex == 1 ? AuthenticationMethod.Password : AuthenticationMethod.SshKey,
            JumpPrivateKeyPath,
            out context);

    private static bool TryCreateJumpCredentialContext(
        string host,
        string portText,
        string username,
        AuthenticationMethod authenticationMethod,
        string? privateKeyPath,
        out JumpCredentialContext? context)
    {
        context = null;
        if (!TryCreateCredentialContext(host, portText, username, authenticationMethod, privateKeyPath, out var inner))
        {
            return false;
        }

        context = new JumpCredentialContext(inner!);
        return true;
    }

    /// <summary>The route the form currently describes, normalized; false when the jump section is off or invalid.</summary>
    private bool TryCreateRoute(out SshRoute? route)
    {
        route = null;
        if (!UseJumpHost
            || !TryCreateEndpoint(out var target)
            || !int.TryParse(JumpPort, NumberStyles.None, CultureInfo.InvariantCulture, out var jumpPort))
        {
            return false;
        }

        try
        {
            route = SshRoute.Create(SshEndpoint.Create(JumpHost, jumpPort), target!);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void EnsureStagedJumpSecretMatchesCurrentContext()
    {
        if (_jumpSecret is null)
        {
            return;
        }

        if (!UseJumpHost
            || _jumpSecretContext is null
            || !TryCreateJumpCredentialContext(out var current)
            || _jumpSecretContext != current)
        {
            ClearStagedJumpSecret();
        }
    }

    private void ClearStagedJumpSecret()
    {
        _jumpSecret?.Dispose();
        _jumpSecret = null;
        _jumpSecretContext = null;
    }

    private bool SetJumpContextProperty<T>(
        ref T storage,
        T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        var changed = SetEditorProperty(ref storage, value, propertyName);
        if (changed)
        {
            // A staged jump secret never follows the jump login to another host/user/auth/key.
            ClearStagedJumpSecret();
        }

        return changed;
    }

    private static int IndexOfInterval(int seconds)
    {
        var normalized = RefreshIntervalPolicy.Normalize(seconds);
        var options = RefreshIntervalPolicy.SupportedSeconds;
        for (var index = 0; index < options.Count; index++)
        {
            if (options[index] == normalized)
            {
                return index;
            }
        }

        // Normalize always maps into the catalogue, so this is defensive only.
        return options.Count > 1 ? 1 : 0;
    }

    private bool ShouldKeepExistingCredential(ServerInput configuration) =>
        !RemoveSavedPassphrase
        && configuration.CredentialReferenceId is not null
        && _existingServer?.AuthenticationMethod == configuration.AuthenticationMethod;

    private bool TryCreateEndpoint(out SshEndpoint? endpoint)
    {
        endpoint = null;
        if (!int.TryParse(Port, out var port))
        {
            return false;
        }

        try
        {
            endpoint = SshEndpoint.Create(Host, port);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void ApplyConnectionResult(SshConnectionResult result)
    {
        // UI.7 B-7 (F-2): a result that resumes after Dispose (cancelled test, late trust conflict) changes nothing.
        if (_disposed)
        {
            return;
        }

        _lastConnectionResult = result;
        if (result.State is ServerConnectionState.HostKeyUnknown or ServerConnectionState.HostKeyMismatch)
        {
            HostKeySubjectDisplay = DescribeHostKeySubject(result);
        }

        if (result.State == ServerConnectionState.HostKeyUnknown && result.PresentedHostKey is not null)
        {
            _pendingHostKey = result.PresentedHostKey;
            _pendingHostKeyHop = result.HostKeyHop;
            _pendingHostKeyEndpoint = result.HostKeyEndpoint;
            _pendingHostKeyRoute = result.HostKeyRoute;
            PresentedHostKeyAlgorithm = result.PresentedHostKey.Algorithm;
            PresentedHostKeyFingerprint = result.PresentedHostKey.Sha256Fingerprint;
            HasUnknownHostKey = true;
        }
        else if (result.State == ServerConnectionState.HostKeyMismatch)
        {
            PresentedHostKeyAlgorithm = result.PresentedHostKey?.Algorithm ?? string.Empty;
            PresentedHostKeyFingerprint = result.PresentedHostKey?.Sha256Fingerprint ?? string.Empty;
            TrustedHostKeyFingerprint = result.TrustedHostKey?.Identity.Sha256Fingerprint
                ?? result.TrustedRoutedHostKey?.Identity.Sha256Fingerprint
                ?? string.Empty;
            HasHostKeyMismatch = true;
        }
        else if (result.IsSuccess
            && SelectedOperatingSystemIndex == (int)ServerOperatingSystem.Auto
            && result.DetectedOperatingSystem is ServerOperatingSystem.Linux or ServerOperatingSystem.MacOS)
        {
            SetProperty(
                ref _selectedOperatingSystemIndex,
                (int)result.DetectedOperatingSystem,
                nameof(SelectedOperatingSystemIndex));
        }

        SetConnectionState(result);
        ConnectionChecklist.Complete(result);
    }

    private string DescribeHostKeySubject(SshConnectionResult result) => result.HostKeyHop switch
    {
        SshHostKeyHop.Jump when result.HostKeyEndpoint is not null =>
            Format("HostKeySubjectJumpFormat", result.HostKeyEndpoint),
        SshHostKeyHop.Target when result.HostKeyRoute is not null =>
            Format("HostKeySubjectTargetFormat", result.HostKeyRoute.Target, result.HostKeyRoute.Via),
        _ => result.HostKeyEndpoint?.ToString() ?? EndpointDisplay
    };

    private void SetConnectionState(SshConnectionResult result)
    {
        HasConnectionStatus = true;
        ConnectionStatusMessage = _localizationService.GetString($"ConnectionState{result.State}");
        if (result.ErrorCode != SshConnectionErrorCode.None
            && result.State is not ServerConnectionState.HostKeyUnknown
            && result.State is not ServerConnectionState.HostKeyMismatch)
        {
            ConnectionStatusMessage = _localizationService.GetString($"ConnectionError{result.ErrorCode}");
        }
    }

    private void ResetHostKeyPanels()
    {
        HostKeySubjectDisplay = string.Empty;
        HasUnknownHostKey = false;
        HasHostKeyMismatch = false;
        PresentedHostKeyAlgorithm = string.Empty;
        PresentedHostKeyFingerprint = string.Empty;
        TrustedHostKeyFingerprint = string.Empty;
    }

    private void InvalidateConnectionResult()
    {
        _lastConnectionResult = null;
        _pendingHostKey = null;
        HasConnectionStatus = false;
        ConnectionChecklist.Reset();
        ResetHostKeyPanels();
    }

    private bool SetSecurityContextProperty<T>(
        ref T storage,
        T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        var changed = SetEditorProperty(ref storage, value, propertyName);
        if (changed)
        {
            ClearStagedSecret();
        }

        return changed;
    }

    private void ClearStagedSecret()
    {
        _secret?.Dispose();
        _secret = null;
        _secretContext = null;
    }

    private void EnsureStagedSecretMatchesCurrentContext()
    {
        if (_secret is null)
        {
            return;
        }

        if (_secretContext is null
            || !TryCreateCredentialContext(out var currentContext)
            || _secretContext != currentContext)
        {
            ClearStagedSecret();
        }
    }

    private bool TryCreateCredentialContext(out CredentialContext? context) =>
        TryCreateCredentialContext(
            Host,
            Port,
            Username,
            IsPasswordAuthentication ? AuthenticationMethod.Password : AuthenticationMethod.SshKey,
            PrivateKeyPath,
            out context);

    private static bool TryCreateCredentialContext(Server server, out CredentialContext? context) =>
        TryCreateCredentialContext(
            server.Host,
            server.Port.ToString(CultureInfo.InvariantCulture),
            server.Username,
            server.AuthenticationMethod,
            server.PrivateKeyPath,
            out context);

    private static bool TryCreateCredentialContext(
        string host,
        string portText,
        string username,
        AuthenticationMethod authenticationMethod,
        string? privateKeyPath,
        out CredentialContext? context)
    {
        context = null;
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || string.IsNullOrWhiteSpace(username)
            || authenticationMethod is not (AuthenticationMethod.Password or AuthenticationMethod.SshKey))
        {
            return false;
        }

        try
        {
            var endpoint = SshEndpoint.Create(host, port);
            var normalizedKeyPath = authenticationMethod == AuthenticationMethod.SshKey
                && !string.IsNullOrWhiteSpace(privateKeyPath)
                    ? Path.GetFullPath(privateKeyPath.Trim())
                    : null;
            context = new CredentialContext(
                endpoint,
                username.Trim(),
                authenticationMethod,
                normalizedKeyPath);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool SetEditorProperty<T>(
        ref T storage,
        T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        var changed = SetProperty(ref storage, value, propertyName);
        if (changed)
        {
            if (HasValidationErrors)
            {
                HasValidationErrors = false;
            }

            OnPropertyChanged(nameof(EndpointDisplay));
            InvalidateConnectionResult();
        }

        return changed;
    }

    private sealed record JumpCredentialContext(CredentialContext Login);

    private sealed record CredentialContext(
        SshEndpoint Endpoint,
        string Username,
        AuthenticationMethod AuthenticationMethod,
        string? PrivateKeyPath);
}
