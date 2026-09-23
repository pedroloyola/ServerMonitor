using System.Globalization;
using ServerMonitor.App.Services;
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
    private readonly IServerConnectionStateStore _connectionStateStore;
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
    private SshConnectionResult? _lastConnectionResult;
    private bool _isSshConfigImportOpen;
    private bool _isLoadingSshConfig;
    private string _sshConfigStatusMessage = string.Empty;
    private string _sshConfigFileWarningMessage = string.Empty;
    private IReadOnlyList<SshConfigHostOptionViewModel> _sshConfigHosts = [];
    private CancellationTokenSource? _sshConfigLoadCancellation;

    public ServerEditorViewModel(
        IServerValidator validator,
        ISshConnectionService sshConnectionService,
        IHostKeyTrustStore hostKeyTrustStore,
        IServerConnectionStateStore connectionStateStore,
        IPrivateKeyFilePicker privateKeyFilePicker,
        ILocalizationService localizationService,
        Server? server,
        ServerDiscoveryPrefill? prefill = null,
        ISshConfigImportSource? sshConfigImportSource = null)
    {
        _validator = validator;
        _sshConnectionService = sshConnectionService;
        _hostKeyTrustStore = hostKeyTrustStore;
        _connectionStateStore = connectionStateStore;
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
        set
        {
            if (SetSecurityContextProperty(ref _privateKeyPath, value))
            {
                OnPropertyChanged(nameof(HasSavedPassphrase));
            }
        }
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
                    break;
                case SshConfigImportStatus.Error:
                    var error = _localizationService.GetString($"SshConfigImportError{result.ErrorCode}");
                    SshConfigStatusMessage = result.ErrorDetail is null
                        ? error
                        : error + " " + Format("SshConfigImportErrorFileFormat", result.ErrorDetail);
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
    }

    public bool ApplySshConfigHost(SshConfigHostOptionViewModel option) => ApplySshConfigHost(option.Entry);

    /// <summary>
    /// Fills only the form fields that are still empty (or the untouched add-mode port) from one
    /// alias. What the user already typed, the chosen authentication method and any saved server
    /// are never changed. A host that needs ProxyJump/ProxyCommand is refused, never imported as a
    /// direct connection. Password authentication is never inferred.
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
            // yet". A form already on password auth, or with a key path, is left exactly as it is.
            if (entry.IdentityFile is not null
                && IsPrivateKeyAuthentication
                && string.IsNullOrWhiteSpace(PrivateKeyPath))
            {
                PrivateKeyPath = entry.IdentityFile;
            }
        }

        CloseSshConfigImport();
        SshConfigStatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            _localizationService.GetString("SshConfigImportAppliedFormat"),
            entry.Alias);
        return true;
    }

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
    }

    public async Task SelectPrivateKeyAsync()
    {
        var selected = await _privateKeyFilePicker.PickAsync();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            PrivateKeyPath = selected;
        }
    }

    public async Task TestConnectionAsync()
    {
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

        try
        {
            var result = await _sshConnectionService.TestConnectionAsync(
                new SshConnectionRequest
                {
                    Server = draft!,
                    CredentialOverride = _secret,
                    Timeout = TimeSpan.FromSeconds(10)
                },
                _testCancellation.Token);
            ApplyConnectionResult(result);
        }
        catch (OperationCanceledException)
        {
            ApplyConnectionResult(new SshConnectionResult
            {
                State = ServerConnectionState.Cancelled,
                ErrorCode = SshConnectionErrorCode.Cancelled
            });
        }
        catch
        {
            ApplyConnectionResult(new SshConnectionResult
            {
                State = ServerConnectionState.Error,
                ErrorCode = SshConnectionErrorCode.Unexpected
            });
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    public async Task TrustAndConnectAsync()
    {
        if (_pendingHostKey is null || !TryCreateEndpoint(out var endpoint))
        {
            return;
        }

        var presentedHostKey = _pendingHostKey;
        try
        {
            await _hostKeyTrustStore.TrustAsync(endpoint!, presentedHostKey);
            HasUnknownHostKey = false;
            _pendingHostKey = null;
            await TestConnectionAsync();
        }
        catch (HostKeyTrustConflictException)
        {
            var trustedHostKey = await _hostKeyTrustStore.GetAsync(endpoint!);
            ApplyConnectionResult(new SshConnectionResult
            {
                State = ServerConnectionState.HostKeyMismatch,
                ErrorCode = SshConnectionErrorCode.HostKeyMismatch,
                PresentedHostKey = presentedHostKey,
                TrustedHostKey = trustedHostKey
            });
        }
        catch
        {
            ApplyConnectionResult(new SshConnectionResult
            {
                State = ServerConnectionState.Error,
                ErrorCode = SshConnectionErrorCode.Unexpected
            });
        }
    }

    public void CancelTest() => _testCancellation?.Cancel();

    public void DismissHostKeyPrompt()
    {
        HasUnknownHostKey = false;
        _pendingHostKey = null;
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
            RefreshIntervalSeconds = SelectedRefreshIntervalSeconds
        };

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
                CredentialChange = credentialChange
            },
            ConnectionResult = _lastConnectionResult
        };
        return true;
    }

    public void Dispose()
    {
        _sshConfigLoadCancellation?.Cancel();
        _testCancellation?.Cancel();
        _testCancellation?.Dispose();
        _secret?.Dispose();
        _secret = null;
        _secretContext = null;
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
        var input = new ServerInput
        {
            Name = Name,
            Host = Host,
            Port = parsedPort,
            Username = Username,
            OperatingSystem = operatingSystem,
            AuthenticationMethod = authenticationMethod,
            PrivateKeyPath = IsPrivateKeyAuthentication ? PrivateKeyPath : null,
            CredentialReferenceId = GetExistingCredentialReference(authenticationMethod)
        };

        var validation = _validator.ValidateDraft(input);
        var passwordMissing = authenticationMethod == AuthenticationMethod.Password
            && _secret is null
            && input.CredentialReferenceId is null;
        HasValidationErrors = !validation.IsValid || passwordMissing;
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
            CreatedAt = _existingServer?.CreatedAt ?? DateTimeOffset.UtcNow
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
        _lastConnectionResult = result;
        if (result.State == ServerConnectionState.HostKeyUnknown && result.PresentedHostKey is not null)
        {
            _pendingHostKey = result.PresentedHostKey;
            PresentedHostKeyAlgorithm = result.PresentedHostKey.Algorithm;
            PresentedHostKeyFingerprint = result.PresentedHostKey.Sha256Fingerprint;
            HasUnknownHostKey = true;
        }
        else if (result.State == ServerConnectionState.HostKeyMismatch)
        {
            PresentedHostKeyAlgorithm = result.PresentedHostKey?.Algorithm ?? string.Empty;
            PresentedHostKeyFingerprint = result.PresentedHostKey?.Sha256Fingerprint ?? string.Empty;
            TrustedHostKeyFingerprint = result.TrustedHostKey?.Identity.Sha256Fingerprint ?? string.Empty;
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
    }

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

        if (_existingServer is not null)
        {
            _connectionStateStore.Set(_existingServer.Id, result);
        }
    }

    private void ResetHostKeyPanels()
    {
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

    private sealed record CredentialContext(
        SshEndpoint Endpoint,
        string Username,
        AuthenticationMethod AuthenticationMethod,
        string? PrivateKeyPath);
}
