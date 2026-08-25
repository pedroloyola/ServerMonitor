using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.Core.Security;

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

    public ServerEditorViewModel(
        IServerValidator validator,
        ISshConnectionService sshConnectionService,
        IHostKeyTrustStore hostKeyTrustStore,
        IServerConnectionStateStore connectionStateStore,
        IPrivateKeyFilePicker privateKeyFilePicker,
        ILocalizationService localizationService,
        Server? server,
        ServerDiscoveryPrefill? prefill = null)
    {
        _validator = validator;
        _sshConnectionService = sshConnectionService;
        _hostKeyTrustStore = hostKeyTrustStore;
        _connectionStateStore = connectionStateStore;
        _privateKeyFilePicker = privateKeyFilePicker;
        _localizationService = localizationService;
        _existingServer = server;
        // Discovery prefill only seeds an add (server is null): name/host/port come from the
        // suggestion, everything else keeps its blank add-mode default. It never turns an add into
        // an edit (_existingServer stays null) and never touches auth, credentials or the OS guess.
        _name = server?.Name ?? prefill?.Name ?? string.Empty;
        _host = server?.Host ?? prefill?.Host ?? string.Empty;
        _port = (server?.Port ?? prefill?.Port ?? 22).ToString(CultureInfo.InvariantCulture);
        _username = server?.Username ?? string.Empty;
        _privateKeyPath = server?.PrivateKeyPath ?? string.Empty;
        _selectedOperatingSystemIndex = (int)(server?.OperatingSystem ?? ServerOperatingSystem.Auto);
        _selectedAuthenticationIndex = server?.AuthenticationMethod == AuthenticationMethod.Password ? 1 : 0;
        _selectedRefreshIntervalIndex = IndexOfInterval(
            server?.RefreshIntervalSeconds ?? RefreshIntervalPolicy.DefaultSeconds);
    }

    public string Name { get => _name; set => SetEditorProperty(ref _name, value); }

    public string Host { get => _host; set => SetSecurityContextProperty(ref _host, value); }

    public string Port { get => _port; set => SetSecurityContextProperty(ref _port, value); }

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
