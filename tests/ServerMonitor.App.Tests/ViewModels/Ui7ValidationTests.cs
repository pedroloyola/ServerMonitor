using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7C (B-16, Figma 06) and the UI half of H-UI7-1, over the production session, controller, view model, dashboard
/// persist path, ServerProfileService and ServerService on a temp directory. Errors appear only after a Test/Save
/// attempt, then live per field without a validation storm; the first invalid field is the focus target; the UI never
/// saves what Core rejects, and Core rejects it even without the editor's check.
/// </summary>
public sealed class Ui7ValidationTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task NoErrorIsShown_BeforeAnAttempt_NotEvenOnTheFirstKeystroke()
    {
        await OpenAddAsync();
        var changes = Record(_world.ViewModel);

        _world.ViewModel.Name = "w";
        _world.ViewModel.Host = "https://";

        Assert.Empty(_world.ViewModel.FieldErrors);
        Assert.False(_world.ViewModel.HasFieldErrors);
        Assert.DoesNotContain(nameof(ServerEditorViewModel.FieldErrors), changes);
    }

    [Fact]
    public async Task AFailedSave_ShowsEveryFieldError_AndTheFirstInVisualOrderIsTheFocusTarget()
    {
        await OpenAddAsync();

        var outcome = await _world.Page.SubmitAsync();

        Assert.True(outcome!.IsInvalid);
        var errors = _world.ViewModel.FieldErrors;
        Assert.Equal("ServerEditorErrorNameRequired", errors[ServerEditorField.Name]);
        Assert.Equal("ServerEditorErrorHostRequired", errors[ServerEditorField.Host]);
        Assert.Equal("ServerEditorErrorUsernameRequired", errors[ServerEditorField.Username]);
        Assert.Equal("ServerEditorErrorPrivateKeyRequired", errors[ServerEditorField.PrivateKey]);
        Assert.Equal(ServerEditorField.Name, _world.ViewModel.FirstInvalidField);
        Assert.Equal("ServerEditorHintFixFields", _world.Page.Controller.ActionHintKey());
        Assert.Empty(await _world.ServerService.GetAllAsync());
    }

    [Fact]
    public async Task AfterAnAttempt_ErrorsAreLivePerField_AndRaisedOnlyWhenTheSetChanges()
    {
        await OpenAddAsync();
        await _world.Page.SubmitAsync();
        var changes = Record(_world.ViewModel);

        foreach (var text in new[] { "w", "we", "web", "web-0", "web-01" })
        {
            _world.ViewModel.Name = text; // five keystrokes
        }

        Assert.Equal(1, changes.Count(name => name == nameof(ServerEditorViewModel.FieldErrors))); // only when Name became valid
        Assert.False(_world.ViewModel.FieldErrors.ContainsKey(ServerEditorField.Name));
        Assert.Equal(ServerEditorField.Host, _world.ViewModel.FirstInvalidField);

        changes.Clear();
        foreach (var text in new[] { "h", "ht", "htt", "http", "https", "https:", "https:/", "https://" })
        {
            _world.ViewModel.Host = text;
        }

        // "h".."https:" valid (1 raise: the required error went), "https:/" invalid format (1 raise), "https://" same set (0).
        Assert.Equal(2, changes.Count(name => name == nameof(ServerEditorViewModel.FieldErrors)));
        Assert.Equal("ServerEditorErrorHostFormat", _world.ViewModel.FieldErrors[ServerEditorField.Host]);
    }

    public static TheoryData<string, Action<ServerEditorViewModel>, ServerEditorField, string> WriteRules() => new()
    {
        { "scheme", vm => vm.Host = "https://server.example.com", ServerEditorField.Host, "ServerEditorErrorHostFormat" },
        { "path", vm => vm.Host = "server.example.com/admin", ServerEditorField.Host, "ServerEditorErrorHostFormat" },
        { "space", vm => vm.Host = "server example.com", ServerEditorField.Host, "ServerEditorErrorHostFormat" },
        { ".pub", vm => vm.PrivateKeyPath = @"C:\keys\id_ed25519.pub", ServerEditorField.PrivateKey, "ServerEditorErrorPrivateKeyIsPublic" },
        { "jump .pub", vm => { Route(vm, "bastion.example.test"); vm.SelectedJumpAuthenticationIndex = 0; vm.JumpPrivateKeyPath = @"C:\keys\bastion.PUB"; }, ServerEditorField.JumpPrivateKey, "ServerEditorErrorJumpPrivateKeyIsPublic" },
        { "jump == target", vm => Route(vm, "10.0.0.9"), ServerEditorField.JumpHost, "ServerEditorErrorJumpIsTarget" },
        { "jump == target (case, dot)", vm => { vm.Host = "Server.Example.com"; Route(vm, "server.example.com."); }, ServerEditorField.JumpHost, "ServerEditorErrorJumpIsTarget" },
    };

    /// <summary>H-UI7-1 counterproof (b): the UI never saves what Core rejects - the editor shows the field error first.</summary>
    [Theory]
    [MemberData(nameof(WriteRules))]
    public async Task H1_TheEditor_ShowsTheWriteRuleOnItsField_AndSavesNothing(
        string name, Action<ServerEditorViewModel> edit, ServerEditorField field, string messageKey)
    {
        _ = name;
        await OpenAddAsync();
        FillValid(_world.ViewModel);
        edit(_world.ViewModel);
        var before = _world.PersistedSnapshot();

        var outcome = await _world.Page.SubmitAsync();

        Assert.True(outcome!.IsInvalid);
        Assert.Equal(messageKey, _world.ViewModel.FieldErrors[field]);
        Assert.Equal(before, _world.PersistedSnapshot());
        Assert.Equal(0, _world.Credentials.Writes);
        Assert.Equal(0, _world.Ssh.TestConnectionCount);
    }

    /// <summary>H-UI7-1 counterproof (c): the App write path (dashboard → profile service → server service) rejects it too.</summary>
    [Theory]
    [MemberData(nameof(WriteRules))]
    public async Task H1_TheWritePath_RejectsTheRule_WithoutTheEditorCheck(
        string name, Action<ServerEditorViewModel> edit, ServerEditorField field, string messageKey)
    {
        _ = name;
        _ = field;
        _ = messageKey;
        await OpenAddAsync();
        FillValid(_world.ViewModel);
        edit(_world.ViewModel);
        var viewModel = _world.ViewModel;
        var route = viewModel.UseJumpHost
            ? new ServerRoute
            {
                Jump = new JumpHop
                {
                    Host = viewModel.JumpHost,
                    Port = int.Parse(viewModel.JumpPort, System.Globalization.CultureInfo.InvariantCulture),
                    Username = viewModel.JumpUsername,
                    AuthenticationMethod = viewModel.SelectedJumpAuthenticationIndex == 0 ? AuthenticationMethod.SshKey : AuthenticationMethod.Password,
                    PrivateKeyPath = viewModel.SelectedJumpAuthenticationIndex == 0 ? viewModel.JumpPrivateKeyPath : null
                }
            }
            : null;
        using var result = new ServerEditorResult
        {
            Profile = new ServerProfileInput
            {
                Configuration = new ServerInput
                {
                    Name = viewModel.Name,
                    Host = viewModel.Host,
                    Port = 22,
                    Username = viewModel.Username,
                    AuthenticationMethod = AuthenticationMethod.SshKey,
                    PrivateKeyPath = viewModel.PrivateKeyPath,
                    Route = route
                },
                CredentialChange = CredentialChange.Clear,
                JumpCredentialChange = route is null ? null : CredentialChange.Clear
            }
        };
        var before = _world.PersistedSnapshot();

        var persisted = await _world.Dashboard.PersistAddedServerAsync(result);

        Assert.False(persisted.Succeeded);
        Assert.Contains(persisted.Validation.Errors, error => error.Code is ServerValidationErrorCode.HostFormatInvalid
            or ServerValidationErrorCode.PrivateKeyIsPublicKey
            or ServerValidationErrorCode.JumpPrivateKeyIsPublicKey
            or ServerValidationErrorCode.JumpEndpointIsTarget);
        Assert.Equal(before, _world.PersistedSnapshot());
    }

    [Fact]
    public async Task H1_AnExistingServerThatBreaksARule_LoadsAndOpens_AndShowsTheErrorOnlyWhenSaved()
    {
        var legacy = new Server
        {
            Id = Guid.NewGuid(),
            Name = "legacy",
            Host = "https://legacy.example.com",
            Port = 22,
            Username = "ops",
            OperatingSystem = ServerOperatingSystem.Linux,
            AuthenticationMethod = AuthenticationMethod.SshKey,
            PrivateKeyPath = Path.Combine(_world.Directory, "id_ed25519"),
            RefreshIntervalSeconds = 30,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        await _world.Repository.SaveAllAsync([legacy]);
        using (var fresh = new ServerService(_world.Repository, new ServerValidator(), _world.Gate))
        {
            Assert.Single(await fresh.GetAllAsync()); // loads, not quarantined
        }

        await _world.StartAsync(); // the production service loads servers.json now: the legacy server is listed
        _ = _world.OpenEditAsync(legacy.Id);
        Assert.Empty(_world.ViewModel.FieldErrors);

        _world.ViewModel.Name = "legacy-renamed";
        var outcome = await _world.Page.SubmitAsync();

        Assert.True(outcome!.IsInvalid);
        Assert.Equal("ServerEditorErrorHostFormat", _world.ViewModel.FieldErrors[ServerEditorField.Host]);
        Assert.Equal("legacy", Assert.Single(await _world.ServerService.GetAllAsync()).Name);
    }

    [Fact]
    public async Task APasswordTypedNowhere_IsTheOnlyError_AndAJumpOnlyFailureSaysSo()
    {
        await OpenAddAsync();
        FillValid(_world.ViewModel);
        _world.ViewModel.SelectedAuthenticationIndex = 1;

        await _world.Page.SubmitAsync();

        Assert.Equal(ServerEditorField.Password, Assert.Single(_world.ViewModel.FieldErrors).Key);
        Assert.Equal(ServerEditorValidation.PasswordMissingKey, _world.ViewModel.FieldErrors[ServerEditorField.Password]);

        _world.ViewModel.SelectedAuthenticationIndex = 0;
        Route(_world.ViewModel, "bastion.example.test");
        _world.ViewModel.JumpUsername = string.Empty;
        await _world.Page.SubmitAsync();

        Assert.All(_world.ViewModel.FieldErrors.Keys, key => Assert.True(ServerEditorValidation.IsJumpField(key)));
        Assert.Equal("ServerEditorHintFixJumpFields", _world.Page.Controller.ActionHintKey());
    }

    [Fact]
    public async Task AnEditSwitchedToAPasswordItMustReceive_SaysIntroduzAPalavraPasse()
    {
        await _world.StartAsync();
        var server = await _world.SeedAsync();
        _ = _world.OpenEditAsync(server.Id);
        Assert.Equal("ServerEditorHintTestBeforeSave", _world.Page.Controller.ActionHintKey());

        _world.ViewModel.SelectedAuthenticationIndex = 1;

        Assert.Equal("ServerEditorHintPasswordToContinue", _world.Page.Controller.ActionHintKey());
        _world.Page.TypedPassword = "new-secret";
        Assert.Equal("ServerEditorHintUnsaved", _world.Page.Controller.ActionHintKey());
    }

    [Fact]
    public void EveryErrorMessage_IsPlacedOnAField_AndWordedInEveryCulture()
    {
        var placed = Enum.GetValues<ServerValidationErrorCode>()
            .Select(ServerEditorValidation.Place)
            .Where(place => place is not null)
            .Select(place => place!.Value)
            .ToList();
        foreach (var code in new[]
        {
            ServerValidationErrorCode.HostFormatInvalid, ServerValidationErrorCode.PrivateKeyIsPublicKey,
            ServerValidationErrorCode.JumpPrivateKeyIsPublicKey, ServerValidationErrorCode.JumpEndpointIsTarget
        })
        {
            Assert.NotNull(ServerEditorValidation.Place(code));
        }

        Ui7Resources.AssertPresentInEveryCulture(placed.Select(place => place.MessageKey)
            .Concat([ServerEditorValidation.PasswordMissingKey, ServerEditorValidation.JumpPasswordMissingKey,
                "ServerEditorValidationSummary", "ServerEditorHintFixFields", "ServerEditorHintFixJumpFields",
                "ServerEditorHintPasswordToContinue", "ServerEditorHintImported", "ServerEditorSaveFailedSubtitleAdd",
                "ServerEditorSaveFailedSubtitleEdit", "ServerEditorSaveRetryButton.Content"]));
    }

    [Fact]
    public void ThePtPtCopy_SpeaksTu_NotVoce()
    {
        var resources = Ui7Resources.Load("pt-PT");
        var mine = resources.Where(entry => entry.Key.StartsWith("ServerEditor", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(mine);
        foreach (var (key, value) in mine)
        {
            foreach (var voce in new[] { "Verifique", "Introduza", "Use ", "Escolha", "Corrija", "Tente ", "você", "Você", "Revise" })
            {
                Assert.False(value.Contains(voce, StringComparison.Ordinal), $"{key}: \"{value}\" uses \"{voce.Trim()}\"");
            }
        }
    }

    private async Task OpenAddAsync()
    {
        await _world.StartAsync();
        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
    }

    private static void FillValid(ServerEditorViewModel viewModel)
    {
        viewModel.Name = "db";
        viewModel.Host = "10.0.0.9";
        viewModel.Username = "ops";
        viewModel.PrivateKeyPath = @"C:\keys\id_ed25519";
    }

    private static void Route(ServerEditorViewModel viewModel, string jumpHost)
    {
        viewModel.UseJumpHost = true;
        viewModel.JumpHost = jumpHost;
        viewModel.JumpUsername = "jumper";
        viewModel.SelectedJumpAuthenticationIndex = 0;
        viewModel.JumpPrivateKeyPath = @"C:\keys\bastion";
    }

    private static List<string> Record(ServerEditorViewModel viewModel)
    {
        var names = new List<string>();
        viewModel.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);
        return names;
    }
}
