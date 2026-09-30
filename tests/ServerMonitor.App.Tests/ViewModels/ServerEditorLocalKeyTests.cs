using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.SshConfig;
using static ServerMonitor.App.Tests.ViewModels.OnboardingEditorTestKit;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.5 B1 — default keys found in <c>.ssh</c> are offered in the editor. The recommended one is pre-selected
/// only when adding a server, only into a key path that is still empty once discovery is over, and never
/// over a path the user chose or an ssh-config import supplied. A saved server is never touched.
/// </summary>
public sealed class ServerEditorLocalKeyTests
{
    private const string Profile = @"C:\Users\tester";

    private sealed class FakeSshConfigSource(SshConfigImportResult result) : ISshConfigImportSource
    {
        public Task<SshConfigImportResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private static readonly FakeSshConfigSource AnyImportSource = new(SshConfigImportResult.NotFound);

    private static SshConfigHostEntry Entry(string text, string alias) =>
        Assert.Single(SshConfigResolver.Import(text, Profile).Hosts, host => host.Alias == alias);

    [Fact]
    public async Task AddMode_PreselectsTheRecommendedKeyAndSaysSo()
    {
        var discovery = new FakeLocalSshKeyDiscovery(Ed25519(), Ecdsa(), Rsa());
        using var editor = Editor(discovery: discovery);

        await editor.LocalKeyDiscovery;

        Assert.Equal(1, discovery.DiscoverCount);
        Assert.Equal(Ed25519().Path, editor.PrivateKeyPath);
        Assert.True(editor.IsPrivateKeyAutoSelected);
        Assert.Equal("LocalKeyAutoSelectedHint", editor.PrivateKeyHint);
        Assert.True(editor.HasPrivateKeyHint);
        Assert.Same(editor.LocalKeyOptions[0], editor.SelectedLocalKeyOption);
    }

    [Fact]
    public async Task Options_ListTheFoundKeysThenBrowse_WithOnlyTheRecommendedOneMarked()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ed25519(), Ecdsa(), Rsa()));

        await editor.LocalKeyDiscovery;

        Assert.True(editor.HasLocalKeyOptions);
        Assert.Equal(
            ["id_ed25519 · Recommended", "id_ecdsa", "id_rsa", "LocalKeyOptionBrowse"],
            editor.LocalKeyOptions.Select(option => option.Label));
        Assert.Equal([false, false, false, true], editor.LocalKeyOptions.Select(option => option.IsBrowse));
    }

    [Fact]
    public async Task RecommendedKey_IsTheOneTheDiscoveryFlagged_NotSimplyTheFirst()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ecdsa(recommended: true), Rsa()));

        await editor.LocalKeyDiscovery;

        Assert.Equal(Ecdsa().Path, editor.PrivateKeyPath);
        Assert.Equal(["id_ecdsa · Recommended", "id_rsa", "LocalKeyOptionBrowse"], editor.LocalKeyOptions.Select(option => option.Label));
    }

    [Fact]
    public async Task KeysWithoutARecommendedOne_AreListedButNothingIsPreselected()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ed25519(recommended: false), Rsa()));

        await editor.LocalKeyDiscovery;

        Assert.Equal(string.Empty, editor.PrivateKeyPath);
        Assert.False(editor.IsPrivateKeyAutoSelected);
        Assert.Equal(3, editor.LocalKeyOptions.Count);
        Assert.Null(editor.SelectedLocalKeyOption);
    }

    [Fact]
    public async Task NoKeysFound_KeepsTodaysEditorAndAddsAHint()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery());

        await editor.LocalKeyDiscovery;

        Assert.Equal(string.Empty, editor.PrivateKeyPath);
        Assert.False(editor.HasLocalKeyOptions);
        Assert.Empty(editor.LocalKeyOptions);
        Assert.False(editor.IsPrivateKeyAutoSelected);
        Assert.Equal("LocalKeyNoneFoundHint", editor.PrivateKeyHint);
    }

    [Fact]
    public async Task WithoutADiscoveryService_TheEditorIsExactlyAsBefore()
    {
        using var editor = Editor(discovery: null);

        await editor.LocalKeyDiscovery;

        Assert.Equal(string.Empty, editor.PrivateKeyPath);
        Assert.False(editor.HasLocalKeyOptions);
        Assert.False(editor.HasPrivateKeyHint);
    }

    [Fact]
    public async Task EditMode_NeverAutoSelects_EvenWhenTheSavedServerHasNoKeyPath()
    {
        var discovery = new FakeLocalSshKeyDiscovery(Ed25519(), Rsa());
        using var editor = Editor(SavedKeyServer(keyPath: null), discovery: discovery);

        await editor.LocalKeyDiscovery;

        Assert.Equal(string.Empty, editor.PrivateKeyPath);
        Assert.False(editor.IsPrivateKeyAutoSelected);
        Assert.False(editor.HasPrivateKeyHint);
        // The found keys are still offered for the user to pick.
        Assert.Equal(3, editor.LocalKeyOptions.Count);
    }

    [Fact]
    public async Task EditMode_NeverChangesASavedKeyPath()
    {
        const string saved = @"D:\keys\prod_key";
        using var editor = Editor(SavedKeyServer(saved), discovery: new FakeLocalSshKeyDiscovery(Ed25519(), Rsa()));

        await editor.LocalKeyDiscovery;

        Assert.Equal(saved, editor.PrivateKeyPath);
        Assert.False(editor.IsPrivateKeyAutoSelected);
        Assert.Null(editor.SelectedLocalKeyOption);
        Assert.True(editor.TryCreateResult(out var result));
        using (result)
        {
            Assert.Equal(saved, result!.Profile.Configuration.PrivateKeyPath);
        }
    }

    [Fact]
    public async Task EditMode_WithPasswordAuthentication_IsLeftAlone()
    {
        using var editor = Editor(
            TestData.LinuxServer() with { CredentialReferenceId = Guid.NewGuid() },
            discovery: new FakeLocalSshKeyDiscovery(Ed25519()));

        await editor.LocalKeyDiscovery;

        Assert.True(editor.IsPasswordAuthentication);
        Assert.Equal(string.Empty, editor.PrivateKeyPath);
    }

    [Fact]
    public async Task PathChosenBeforeDiscoveryCompletes_IsNeverOverwritten()
    {
        var discovery = new FakeLocalSshKeyDiscovery(Ed25519(), Rsa()) { Blocked = true };
        var picker = new ScriptedPicker { PickedPath = @"D:\keys\work_key" };
        using var editor = Editor(discovery: discovery, picker: picker);

        await editor.SelectPrivateKeyAsync();
        discovery.Release();
        await editor.LocalKeyDiscovery;

        Assert.Equal(@"D:\keys\work_key", editor.PrivateKeyPath);
        Assert.False(editor.IsPrivateKeyAutoSelected);
        Assert.False(editor.HasPrivateKeyHint);
        Assert.Null(editor.SelectedLocalKeyOption);
    }

    [Fact]
    public async Task ImportBeforeDiscoveryCompletes_Wins()
    {
        var discovery = new FakeLocalSshKeyDiscovery(Ed25519(), Rsa()) { Blocked = true };
        using var editor = Editor(discovery: discovery, importSource: AnyImportSource);

        Assert.True(editor.ApplySshConfigHost(Entry("Host web\n  HostName 10.0.0.5\n  User deploy\n  IdentityFile ~/.ssh/web_key\n", "web")));
        var imported = editor.PrivateKeyPath;
        discovery.Release();
        await editor.LocalKeyDiscovery;

        Assert.EndsWith("web_key", imported);
        Assert.Equal(imported, editor.PrivateKeyPath);
        Assert.False(editor.IsPrivateKeyAutoSelected);
    }

    [Fact]
    public async Task ImportAfterAPreselection_ReplacesItWithTheHostsIdentityFile()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ed25519(), Rsa()), importSource: AnyImportSource);
        await editor.LocalKeyDiscovery;
        Assert.True(editor.IsPrivateKeyAutoSelected);

        Assert.True(editor.ApplySshConfigHost(Entry("Host web\n  HostName 10.0.0.5\n  User deploy\n  IdentityFile ~/.ssh/web_key\n", "web")));

        Assert.EndsWith("web_key", editor.PrivateKeyPath);
        Assert.False(editor.IsPrivateKeyAutoSelected);
        Assert.False(editor.HasPrivateKeyHint);
    }

    [Fact]
    public async Task ImportWithoutAnIdentityFile_KeepsThePreselection()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ed25519(), Rsa()), importSource: AnyImportSource);
        await editor.LocalKeyDiscovery;

        Assert.True(editor.ApplySshConfigHost(Entry("Host web\n  HostName 10.0.0.5\n  User deploy\n", "web")));

        Assert.Equal(Ed25519().Path, editor.PrivateKeyPath);
        Assert.True(editor.IsPrivateKeyAutoSelected);
    }

    [Fact]
    public async Task ImportAfterTheUserPickedAFoundKey_DoesNotReplaceIt()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ed25519(), Rsa()), importSource: AnyImportSource);
        await editor.LocalKeyDiscovery;

        // Even the very key that was pre-selected becomes the user's once they pick it themselves.
        editor.SelectLocalKey(editor.LocalKeyOptions[0]);
        Assert.False(editor.IsPrivateKeyAutoSelected);
        Assert.True(editor.ApplySshConfigHost(Entry("Host web\n  HostName 10.0.0.5\n  IdentityFile ~/.ssh/web_key\n", "web")));

        Assert.Equal(Ed25519().Path, editor.PrivateKeyPath);
    }

    [Fact]
    public async Task UserPicksAnotherFoundKey_AndItIsTheirs()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ed25519(), Ecdsa(), Rsa()));
        await editor.LocalKeyDiscovery;

        editor.SelectLocalKey(editor.LocalKeyOptions[2]);

        Assert.Equal(Rsa().Path, editor.PrivateKeyPath);
        Assert.False(editor.IsPrivateKeyAutoSelected);
        Assert.False(editor.HasPrivateKeyHint);
        Assert.Same(editor.LocalKeyOptions[2], editor.SelectedLocalKeyOption);
    }

    [Fact]
    public async Task BrowseEntry_UsesTheExistingPicker_AndACancelledPickerKeepsThePath()
    {
        var picker = new ScriptedPicker { PickedPath = null };
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ed25519(), Rsa()), picker: picker);
        await editor.LocalKeyDiscovery;
        var selectionRefreshes = 0;
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ServerEditorViewModel.SelectedLocalKeyOption))
            {
                selectionRefreshes++;
            }
        };

        // Selecting the browse entry itself changes nothing...
        editor.SelectLocalKey(editor.LocalKeyOptions[^1]);
        Assert.Equal(Ed25519().Path, editor.PrivateKeyPath);
        Assert.True(editor.IsPrivateKeyAutoSelected);

        // ...the view opens the picker; cancelled, the selector is told to snap back to the current key.
        await editor.SelectPrivateKeyAsync();
        Assert.Equal(1, picker.PickCount);
        Assert.Equal(Ed25519().Path, editor.PrivateKeyPath);
        Assert.Equal(1, selectionRefreshes);

        picker.PickedPath = @"D:\keys\other";
        await editor.SelectPrivateKeyAsync();
        Assert.Equal(@"D:\keys\other", editor.PrivateKeyPath);
        Assert.False(editor.IsPrivateKeyAutoSelected);
        Assert.Null(editor.SelectedLocalKeyOption);
    }

    [Fact]
    public async Task PasswordAuthenticationWhenDiscoveryCompletes_IsNotGivenAKey()
    {
        var discovery = new FakeLocalSshKeyDiscovery(Ed25519()) { Blocked = true };
        using var editor = Editor(discovery: discovery);

        editor.SelectedAuthenticationIndex = 1;
        discovery.Release();
        await editor.LocalKeyDiscovery;

        Assert.Equal(string.Empty, editor.PrivateKeyPath);
        Assert.False(editor.IsPrivateKeyAutoSelected);
    }

    [Fact]
    public async Task DiscoveryPrefilledAdd_IsStillAnAddAndGetsThePreselection()
    {
        using var editor = Editor(
            discovery: new FakeLocalSshKeyDiscovery(Ed25519()),
            prefill: new ServerDiscoveryPrefill { Name = "Example SSH", Host = "example.local", Port = 22 });

        await editor.LocalKeyDiscovery;

        Assert.Equal(Ed25519().Path, editor.PrivateKeyPath);
        Assert.Equal("example.local", editor.Host);
        Assert.Equal(string.Empty, editor.Username);
    }

    [Fact]
    public async Task JumpHost_GetsTheSameListButIsNeverPreselected()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ed25519(), Rsa()));
        editor.UseJumpHost = true;

        await editor.LocalKeyDiscovery;

        Assert.Equal(string.Empty, editor.JumpPrivateKeyPath);
        Assert.Null(editor.SelectedJumpLocalKeyOption);

        editor.SelectJumpLocalKey(editor.LocalKeyOptions[1]);

        Assert.Equal(Rsa().Path, editor.JumpPrivateKeyPath);
        Assert.Same(editor.LocalKeyOptions[1], editor.SelectedJumpLocalKeyOption);
        // The target keeps its own pre-selection.
        Assert.Equal(Ed25519().Path, editor.PrivateKeyPath);
    }

    [Fact]
    public async Task Dispose_CancelsADiscoveryStillRunning_AndItsResultIsDiscarded()
    {
        var discovery = new FakeLocalSshKeyDiscovery(Ed25519()) { Blocked = true };
        var editor = Editor(discovery: discovery);
        Assert.False(editor.LocalKeyDiscovery.IsCompleted);

        editor.Dispose();
        await editor.LocalKeyDiscovery;

        Assert.True(discovery.LastToken.IsCancellationRequested);
        Assert.Equal(string.Empty, editor.PrivateKeyPath);
        Assert.Empty(editor.LocalKeyOptions);
    }

    [Fact]
    public async Task DiscoveryThatThrows_LeavesTheEditorUsable()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ed25519())
        {
            Throws = new UnauthorizedAccessException("synthetic")
        });

        await editor.LocalKeyDiscovery;

        Assert.True(editor.LocalKeyDiscovery.IsCompletedSuccessfully);
        Assert.Equal(string.Empty, editor.PrivateKeyPath);
        Assert.False(editor.HasLocalKeyOptions);
    }

    [Fact]
    public async Task PreselectedPath_IsWhatATestAndASaveUse()
    {
        var ssh = new StagedSsh();
        using var editor = Editor(null, ssh, discovery: new FakeLocalSshKeyDiscovery(Ed25519()));
        await editor.LocalKeyDiscovery;
        editor.Name = "web-01";
        editor.Host = "10.0.0.5";
        editor.Username = "deploy";

        await editor.TestConnectionAsync();

        Assert.Equal(Ed25519().Path, Assert.Single(ssh.Requests).Server.PrivateKeyPath);
        Assert.True(editor.TryCreateResult(out var result));
        using (result)
        {
            Assert.Equal(Ed25519().Path, result!.Profile.Configuration.PrivateKeyPath);
            Assert.Equal(AuthenticationMethod.SshKey, result.Profile.Configuration.AuthenticationMethod);
        }
    }

    [Fact]
    public async Task Helper_UsesTheFormValuesAndTheSelectedDefaultKey()
    {
        using var editor = Editor(discovery: new FakeLocalSshKeyDiscovery(Ed25519(), Rsa()));
        await editor.LocalKeyDiscovery;
        editor.Host = " web-01.example.com ";
        editor.Username = "deploy";
        editor.Port = "2222";
        editor.SelectLocalKey(editor.LocalKeyOptions[1]);

        var commands = editor.BuildServerPrepCommands();

        Assert.Equal("ssh-keygen -t ed25519", commands.GenerateKey);
        Assert.StartsWith(
            "type $env:USERPROFILE\\.ssh\\id_rsa.pub | ssh -p 2222 deploy@web-01.example.com \"umask 077;",
            commands.CopyPublicKey);
        Assert.False(commands.UsesPlaceholders);
    }

    [Fact]
    public void Helper_ShowsPlaceholdersForHostileFormValues()
    {
        using var editor = Editor();
        editor.Host = "example.local; Remove-Item -Recurse $HOME";
        editor.Username = "$(whoami)";

        var commands = editor.BuildServerPrepCommands();

        Assert.Equal(
            "type $env:USERPROFILE\\.ssh\\id_ed25519.pub | ssh <user>@<server> \"umask 077; mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys\"",
            commands.CopyPublicKey);
        Assert.True(commands.UsesPlaceholders);
    }

    [Fact]
    public void Helper_ForAKeyPickedByHand_NamesTheDefaultPublicKey()
    {
        using var editor = Editor();
        editor.Host = "10.0.0.5";
        editor.Username = "deploy";
        editor.PrivateKeyPath = @"D:\keys\evil"" | calc";

        var commands = editor.BuildServerPrepCommands();

        Assert.StartsWith("type $env:USERPROFILE\\.ssh\\id_ed25519.pub | ssh deploy@10.0.0.5 ", commands.CopyPublicKey);
        Assert.DoesNotContain("calc", commands.CopyPublicKey);
    }
}
