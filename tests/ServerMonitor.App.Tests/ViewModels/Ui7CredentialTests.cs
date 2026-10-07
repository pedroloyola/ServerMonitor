using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7B credentials over the production session / controller / view model / <c>ServerProfileService</c> with a RECORDING
/// credential store (see <see cref="Ui7EditorWorld"/>): a staged secret never follows a changed login (Vigil CP-5, CR-5,
/// target AND jump, each field), the Edit Keep/Clear/Replace table (CP-8, CR-6), the auth switch through the real save
/// (Cortex R-9) and a Test that only reads the saved secret (Cortex R-15, App side).
/// </summary>
public sealed class Ui7CredentialTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ---- CP-5 (CR-5): target ---------------------------------------------------------------------------------------------

    public static TheoryData<string, bool> TargetFields => new()
    {
        { "Host", false }, { "Host", true },
        { "Port", false }, { "Port", true },
        { "Username", false }, { "Username", true },
        { "Auth", false }, { "Auth", true },
        { "KeyPath", false }, { "KeyPath", true }
    };

    /// <summary>
    /// The staged target secret is dropped the moment its login context changes - also when the field is then put back
    /// (only the setter can catch that: the context matches again at Save).
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetFields))]
    public async Task CP5_AStagedTargetSecret_NeverFollowsAChangedLogin(string field, bool putBack)
    {
        await OpenAddAsync();
        var viewModel = _world.ViewModel;
        var keyAuth = field == "KeyPath";
        FillTarget(viewModel, keyAuth);
        viewModel.CaptureSecret("staged-secret");

        Change(viewModel, field, putBack);

        if (viewModel.TryCreateResult(out var result))
        {
            using (result)
            {
                Assert.NotEqual(CredentialChangeMode.Replace, result!.Profile.CredentialChange.Mode);
                Assert.Null(result.Profile.CredentialChange.Secret);
            }
        }
        else
        {
            Assert.False(keyAuth && field != "Auth", "a key login without a secret is still valid");
        }
    }

    [Fact]
    public async Task CP5_Control_AnUnchangedLogin_KeepsItsStagedSecret()
    {
        await OpenAddAsync();
        FillTarget(_world.ViewModel, keyAuth: false);
        _world.ViewModel.CaptureSecret("staged-secret");

        Assert.True(_world.ViewModel.TryCreateResult(out var result));
        using (result)
        {
            Assert.Equal(CredentialChangeMode.Replace, result!.Profile.CredentialChange.Mode);
            Assert.Equal("staged-secret", new string(result.Profile.CredentialChange.Secret!.Reveal()));
        }
    }

    // ---- CP-5 (CR-5/CR-7): jump ------------------------------------------------------------------------------------------

    public static TheoryData<string, bool> JumpFields => new()
    {
        { "JumpHost", false }, { "JumpHost", true },
        { "JumpPort", false }, { "JumpPort", true },
        { "JumpUsername", false }, { "JumpUsername", true },
        { "JumpAuth", false }, { "JumpAuth", true },
        { "JumpKeyPath", false }, { "JumpKeyPath", true }
    };

    [Theory]
    [MemberData(nameof(JumpFields))]
    public async Task CP5_AStagedJumpSecret_NeverFollowsAChangedJumpLogin(string field, bool putBack)
    {
        await OpenAddAsync();
        var viewModel = _world.ViewModel;
        FillTarget(viewModel, keyAuth: true);
        var jumpKey = field == "JumpKeyPath";
        FillJump(viewModel, jumpKey);
        viewModel.CaptureJumpSecret("jump-secret");

        Change(viewModel, field, putBack);

        if (viewModel.TryCreateResult(out var result))
        {
            using (result)
            {
                Assert.Null(result!.Profile.JumpCredentialChange?.Secret);
                Assert.NotEqual(CredentialChangeMode.Replace, result.Profile.JumpCredentialChange?.Mode ?? CredentialChangeMode.Keep);
            }
        }
        else
        {
            Assert.False(jumpKey && field != "JumpAuth", "a jump key login without a secret is still valid");
        }
    }

    [Fact]
    public async Task CP5_Control_AnUnchangedJumpLogin_KeepsItsStagedJumpSecret_OnlyAsTheJumpChange()
    {
        await OpenAddAsync();
        FillTarget(_world.ViewModel, keyAuth: true);
        FillJump(_world.ViewModel, jumpKey: false);
        _world.ViewModel.CaptureJumpSecret("jump-secret");

        Assert.True(_world.ViewModel.TryCreateResult(out var result));
        using (result)
        {
            Assert.Equal("jump-secret", new string(result!.Profile.JumpCredentialChange!.Secret!.Reveal()));
            Assert.Null(result.Profile.CredentialChange.Secret); // never mixed into the target's (CR-7)
        }
    }

    // ---- CP-8 (CR-6): the Edit table -------------------------------------------------------------------------------------

    public static TheoryData<string> EditCases => new()
    {
        "password-to-key", "remove-saved-passphrase", "same-key-login", "same-password-login", "key-path-changed", "key-to-password-without-one"
    };

    [Theory]
    [MemberData(nameof(EditCases))]
    public async Task CP8_EditingASavedLogin_KeepsClearsOrRefusesExactlyAsTheTableSays(string @case)
    {
        var password = @case is "password-to-key" or "same-password-login";
        var server = await _world.SeedAsync(
            authentication: password ? AuthenticationMethod.Password : AuthenticationMethod.SshKey,
            password: password ? "saved-password" : "saved-passphrase");
        await _world.StartAsync();
        _ = _world.OpenEditAsync(server.Id); // completes when the visit ends
        var viewModel = _world.ViewModel;

        switch (@case)
        {
            case "password-to-key":
                viewModel.SelectedAuthenticationIndex = 0;
                viewModel.PrivateKeyPath = Path.Combine(_world.Directory, "id_ed25519");
                break;
            case "remove-saved-passphrase":
                viewModel.RemoveSavedPassphrase = true;
                break;
            case "key-path-changed":
                viewModel.PrivateKeyPath = Path.Combine(_world.Directory, "id_rsa");
                break;
            case "key-to-password-without-one":
                viewModel.SelectedAuthenticationIndex = 1;
                break;
        }

        var created = viewModel.TryCreateResult(out var result);
        using (result)
        {
            var expected = @case switch
            {
                "password-to-key" or "remove-saved-passphrase" or "key-path-changed" => CredentialChangeMode.Clear,
                "same-key-login" or "same-password-login" => CredentialChangeMode.Keep,
                _ => (CredentialChangeMode?)null
            };
            if (expected is null)
            {
                Assert.False(created); // key → password needs a new password
                Assert.True(viewModel.HasValidationErrors);
            }
            else
            {
                Assert.True(created);
                Assert.Equal(expected, result!.Profile.CredentialChange.Mode);
                Assert.Null(result.Profile.CredentialChange.Secret);
            }
        }
    }

    // ---- Cortex R-9: the auth switch through the real save -------------------------------------------------------------

    [Fact]
    public async Task R9_KeyWithASavedPassphrase_ToATypedPassword_WritesThePasswordFirst_ThenRetiresThePassphrase()
    {
        var server = await _world.SeedAsync(authentication: AuthenticationMethod.SshKey, password: "saved-passphrase");
        await _world.StartAsync();
        _ = _world.OpenEditAsync(server.Id); // completes when the visit ends
        _world.ViewModel.SelectedAuthenticationIndex = 1;
        _world.Page.TypedPassword = "new-password";

        var outcome = await _world.Page.SubmitAsync();

        Assert.Equal(ServerEditorSaveStatus.Saved, outcome!.Status);
        Assert.Equal(
            [("write", ServerCredentialKind.Password), ("delete", ServerCredentialKind.PrivateKeyPassphrase)],
            _world.Credentials.Log);
        Assert.Equal("new-password", _world.Credentials.SecretOf(ServerCredentialKind.Password));
        Assert.Null(_world.Credentials.SecretOf(ServerCredentialKind.PrivateKeyPassphrase));
        var saved = Assert.Single(await _world.ServerService.GetAllAsync());
        Assert.Equal(AuthenticationMethod.Password, saved.AuthenticationMethod);
    }

    [Fact]
    public async Task R9_PasswordToKeyWithoutAPassphrase_ClearsAndRetiresTheOldPassword()
    {
        var server = await _world.SeedAsync(authentication: AuthenticationMethod.Password, password: "saved-password");
        await _world.StartAsync();
        _ = _world.OpenEditAsync(server.Id); // completes when the visit ends
        _world.ViewModel.SelectedAuthenticationIndex = 0;
        _world.ViewModel.PrivateKeyPath = Path.Combine(_world.Directory, "id_ed25519");

        var outcome = await _world.Page.SubmitAsync();

        Assert.Equal(ServerEditorSaveStatus.Saved, outcome!.Status);
        Assert.Equal([("delete", ServerCredentialKind.Password)], _world.Credentials.Log);
        Assert.Equal(0, _world.Credentials.Count);
        Assert.Null(Assert.Single(await _world.ServerService.GetAllAsync()).CredentialReferenceId);
    }

    [Fact]
    public async Task R9_ASecretStagedForOneHost_IsDroppedWhenTheHostChanges_AndNothingIsWritten()
    {
        var server = await _world.SeedAsync(authentication: AuthenticationMethod.Password, password: "saved-password");
        await _world.StartAsync();
        _ = _world.OpenEditAsync(server.Id); // completes when the visit ends
        _world.Page.TypedPassword = "typed-for-the-old-host";
        await _world.Page.TestAsync(); // the typed secret is staged (read + cleared from the box)

        _world.ViewModel.Host = "10.0.0.77";
        var outcome = await _world.Page.SubmitAsync();

        Assert.True(outcome!.IsInvalid); // another login: the saved password is not kept and the typed one is gone
        Assert.Equal(0, _world.Credentials.Writes);
        Assert.Equal(0, _world.Credentials.Deletes);
    }

    // ---- Cortex R-15 (App side): a Test with a saved secret writes nothing ---------------------------------------------

    [Theory]
    [InlineData(AuthenticationMethod.Password)]
    [InlineData(AuthenticationMethod.SshKey)]
    public async Task R15_TestingAnEditWithItsSavedSecret_SendsNoOverride_AndWritesNoCredential(AuthenticationMethod authentication)
    {
        var server = await _world.SeedAsync(authentication: authentication, password: "saved-secret");
        await _world.StartAsync();
        _ = _world.OpenEditAsync(server.Id); // completes when the visit ends

        await _world.Page.TestAsync(); // nothing typed: the service reads the saved credential itself

        var request = Assert.Single(_world.Ssh.Requests);
        Assert.Null(request.CredentialOverride);
        Assert.Equal(server.CredentialReferenceId, request.Server.CredentialReferenceId);
        Assert.Equal(0, _world.Credentials.Writes);
        Assert.Equal(0, _world.Credentials.Deletes);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private async Task OpenAddAsync()
    {
        await _world.StartAsync();
        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
    }

    private void FillTarget(ServerEditorViewModel viewModel, bool keyAuth)
    {
        viewModel.Name = "db";
        viewModel.Host = "10.0.0.9";
        viewModel.Port = "22";
        viewModel.Username = "ops";
        viewModel.SelectedAuthenticationIndex = keyAuth ? 0 : 1;
        if (keyAuth)
        {
            viewModel.PrivateKeyPath = Path.Combine(_world.Directory, "id_ed25519");
        }
    }

    private void FillJump(ServerEditorViewModel viewModel, bool jumpKey)
    {
        viewModel.UseJumpHost = true;
        viewModel.JumpHost = "bastion.example.test";
        viewModel.JumpPort = "2222";
        viewModel.JumpUsername = "jumper";
        viewModel.SelectedJumpAuthenticationIndex = jumpKey ? 0 : 1;
        if (jumpKey)
        {
            viewModel.JumpPrivateKeyPath = Path.Combine(_world.Directory, "id_jump");
        }
    }

    private void Change(ServerEditorViewModel viewModel, string field, bool putBack)
    {
        var dir = _world.Directory;
        switch (field)
        {
            case "Host":
                viewModel.Host = "10.0.0.10";
                if (putBack) viewModel.Host = "10.0.0.9";
                break;
            case "Port":
                viewModel.Port = "2222";
                if (putBack) viewModel.Port = "22";
                break;
            case "Username":
                viewModel.Username = "ops2";
                if (putBack) viewModel.Username = "ops";
                break;
            case "Auth":
                var original = viewModel.SelectedAuthenticationIndex;
                viewModel.SelectedAuthenticationIndex = 1 - original;
                if (viewModel.IsPrivateKeyAuthentication && string.IsNullOrEmpty(viewModel.PrivateKeyPath))
                {
                    viewModel.PrivateKeyPath = Path.Combine(dir, "id_ed25519");
                }

                if (putBack) viewModel.SelectedAuthenticationIndex = original;
                break;
            case "KeyPath":
                viewModel.PrivateKeyPath = Path.Combine(dir, "id_rsa");
                if (putBack) viewModel.PrivateKeyPath = Path.Combine(dir, "id_ed25519");
                break;
            case "JumpHost":
                viewModel.JumpHost = "bastion2.example.test";
                if (putBack) viewModel.JumpHost = "bastion.example.test";
                break;
            case "JumpPort":
                viewModel.JumpPort = "22";
                if (putBack) viewModel.JumpPort = "2222";
                break;
            case "JumpUsername":
                viewModel.JumpUsername = "jumper2";
                if (putBack) viewModel.JumpUsername = "jumper";
                break;
            case "JumpAuth":
                var jumpOriginal = viewModel.SelectedJumpAuthenticationIndex;
                viewModel.SelectedJumpAuthenticationIndex = 1 - jumpOriginal;
                if (viewModel.IsJumpPrivateKeyAuthentication && string.IsNullOrEmpty(viewModel.JumpPrivateKeyPath))
                {
                    viewModel.JumpPrivateKeyPath = Path.Combine(dir, "id_jump");
                }

                if (putBack) viewModel.SelectedJumpAuthenticationIndex = jumpOriginal;
                break;
            case "JumpKeyPath":
                viewModel.JumpPrivateKeyPath = Path.Combine(dir, "id_other");
                if (putBack) viewModel.JumpPrivateKeyPath = Path.Combine(dir, "id_jump");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }
}
