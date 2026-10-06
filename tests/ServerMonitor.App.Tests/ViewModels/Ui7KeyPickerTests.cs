using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7B key discovery behind the picker (B-12, Vigil KD-1/KD-2): none / one / many / invalid path / permission denied /
/// key removed between discovery and test, through the production session and view model; and the picker's and route
/// line's presentation (file name only, never an invented key, the derived route) on the real copy.
/// </summary>
public sealed class Ui7KeyPickerTests
{
    private static readonly string SshDirectory = Path.Combine(Path.GetTempPath(), "servermonitor-ui7-tests", "ssh");
    private static readonly string Ed25519 = Path.Combine(SshDirectory, "id_ed25519");
    private static readonly string Ecdsa = Path.Combine(SshDirectory, "id_ecdsa");
    private static readonly string Rsa = Path.Combine(SshDirectory, "id_rsa");

    [Fact]
    public async Task None_TheMenuOffersOnlyBrowse_AndTheHelperSaysNothingWasFound()
    {
        using var world = new Ui7EditorWorld(new ScriptedKeys([]));
        var viewModel = await OpenAddAsync(world);

        Assert.Empty(viewModel.LocalKeyOptions);
        var item = Assert.Single(ServerEditorKeyPicker.MenuItems(viewModel.LocalKeyOptions, new FakeLocalizationService()));
        Assert.True(item.IsBrowse);
        Assert.Equal("ServerEditorKeyPickerBrowse", item.Label);
        Assert.Equal("LocalKeyNoneFoundHint", viewModel.PrivateKeyHint);
        Assert.Equal(string.Empty, viewModel.PrivateKeyPath);
        Assert.Null(ServerEditorKeyPicker.FileName(viewModel.PrivateKeyPath));
    }

    [Fact]
    public async Task One_IsPreselected_AndSaysSo_WithoutMakingTheFormDirty()
    {
        using var world = new Ui7EditorWorld(new ScriptedKeys([Key(Ed25519, LocalSshKeyKind.Ed25519, recommended: true)]));
        var viewModel = await OpenAddAsync(world);

        Assert.Equal(Ed25519, viewModel.PrivateKeyPath);
        Assert.True(viewModel.IsPrivateKeyAutoSelected);
        Assert.Equal("LocalKeyAutoSelectedHint", viewModel.PrivateKeyHint); // visible helper: no silent choice
        Assert.False(viewModel.IsDirty);
        Assert.Equal("id_ed25519", ServerEditorKeyPicker.FileName(viewModel.PrivateKeyPath));
        var menu = ServerEditorKeyPicker.MenuItems(viewModel.LocalKeyOptions, new FakeLocalizationService());
        Assert.Equal(2, menu.Count);
        Assert.Same(viewModel.SelectedLocalKeyOption, menu[0]);
        Assert.True(menu[1].IsBrowse);
    }

    [Fact]
    public async Task Many_OnlyTheRecommendedIsPreselected_ChoosingAnotherIsAnEdit_AndTheJumpIsNeverPreselected()
    {
        using var world = new Ui7EditorWorld(new ScriptedKeys(
        [
            Key(Ed25519, LocalSshKeyKind.Ed25519, recommended: true),
            Key(Ecdsa, LocalSshKeyKind.Ecdsa),
            Key(Rsa, LocalSshKeyKind.Rsa)
        ]));
        var viewModel = await OpenAddAsync(world);

        var menu = ServerEditorKeyPicker.MenuItems(viewModel.LocalKeyOptions, new FakeLocalizationService());
        Assert.Equal(4, menu.Count);
        Assert.Equal([Ed25519, Ecdsa, Rsa], menu.Where(o => !o.IsBrowse).Select(o => o.Key!.Path));
        Assert.Equal(Ed25519, viewModel.PrivateKeyPath);

        viewModel.SelectLocalKey(menu[2]);
        Assert.Equal(Rsa, viewModel.PrivateKeyPath);
        Assert.False(viewModel.IsPrivateKeyAutoSelected);
        Assert.True(viewModel.IsDirty);

        viewModel.UseJumpHost = true;
        Assert.True(viewModel.IsJumpPrivateKeyAuthentication);
        Assert.Equal(string.Empty, viewModel.JumpPrivateKeyPath);
        Assert.Null(viewModel.SelectedJumpLocalKeyOption);
        viewModel.SelectJumpLocalKey(menu[1]); // the same list for the jump, only on a choice
        Assert.Equal(Ecdsa, viewModel.JumpPrivateKeyPath);
    }

    [Fact]
    public async Task AnInvalidPath_IsNeverOffered()
    {
        using var world = new Ui7EditorWorld(new ScriptedKeys(
        [
            Key("   ", LocalSshKeyKind.Ed25519, recommended: true),
            Key(string.Empty, LocalSshKeyKind.Rsa)
        ]));
        var viewModel = await OpenAddAsync(world);

        Assert.Empty(viewModel.LocalKeyOptions);
        Assert.Equal(string.Empty, viewModel.PrivateKeyPath);
        Assert.Equal("LocalKeyNoneFoundHint", viewModel.PrivateKeyHint);
    }

    [Fact]
    public async Task PermissionDenied_IsNoKeys_AndTheEditorStaysUsable()
    {
        using var world = new Ui7EditorWorld(new ScriptedKeys(null, new UnauthorizedAccessException("denied")));
        var viewModel = await OpenAddAsync(world);

        Assert.Empty(viewModel.LocalKeyOptions);
        Assert.Equal("LocalKeyNoneFoundHint", viewModel.PrivateKeyHint);
        Assert.True(Assert.Single(ServerEditorKeyPicker.MenuItems(viewModel.LocalKeyOptions, new FakeLocalizationService())).IsBrowse);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task AKeyRemovedBetweenDiscoveryAndTest_FailsTheTestOnThatPath_AndWritesNothing()
    {
        using var world = new Ui7EditorWorld(new ScriptedKeys([Key(Ed25519, LocalSshKeyKind.Ed25519, recommended: true)]));
        var viewModel = await OpenAddAsync(world);
        viewModel.Name = "db";
        viewModel.Host = "10.0.0.9";
        viewModel.Username = "ops";
        world.ResetTrustCounts();
        // The file went away after discovery: the connection service reports it (the editor never opens key files).
        world.Ssh.Result = new SshConnectionResult
        {
            State = ServerConnectionState.Error,
            ErrorCode = SshConnectionErrorCode.PrivateKeyUnavailable,
            ReachedStage = SshConnectionStage.HostKeyVerified
        };

        await world.Page.TestAsync();

        Assert.Equal(Ed25519, Assert.Single(world.Ssh.Requests).Server.PrivateKeyPath);
        Assert.Equal("ConnectionErrorPrivateKeyUnavailable", viewModel.ConnectionStatusMessage);
        Assert.Equal(Ed25519, viewModel.PrivateKeyPath); // nothing silently swapped
        Assert.Null(world.Page.EvaluateDialogs());
        Assert.Equal(0, world.DirectTrust.Trusts + world.RoutedTrust.Trusts);
        Assert.Equal(0, world.Credentials.Writes);
    }

    // ---- presentation --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(@"C:\Users\someone\.ssh\id_ed25519", "id_ed25519")]
    [InlineData(@"  C:\keys\id_rsa  ", "id_rsa")]
    [InlineData(@"C:\keys\deploy\", "deploy")]
    [InlineData("id_ecdsa", "id_ecdsa")]
    public void TheButtonShowsTheFileName_NeverTheFullPath(string? path, string? expected) =>
        Assert.Equal(expected, ServerEditorKeyPicker.FileName(path));

    [Theory]
    [InlineData("pt-PT", null, "Escolher ficheiro…")]
    [InlineData("pt-PT", @"C:\Users\someone\.ssh\id_ed25519", "id_ed25519  ·  Alterar ficheiro…")]
    [InlineData("en-US", @"C:\Users\someone\.ssh\id_ed25519", "id_ed25519  ·  Change file…")]
    public void TheButtonText_IsTheFigmaCopy(string culture, string? path, string expected)
    {
        var text = ServerEditorKeyPicker.ButtonText(path, new ResWLocalizationService(culture));

        Assert.Equal(expected, text);
        Assert.DoesNotContain(@"\Users\", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "10.0.0.5", "Jump host por preencher")]
    [InlineData("  ", "10.0.0.5", "Jump host por preencher")]
    [InlineData("bastion.example.com", "10.0.0.5", "Este dispositivo   →   bastion.example.com   →   10.0.0.5")]
    [InlineData(" bastion.example.com ", " ", "Este dispositivo   →   bastion.example.com   →   servidor por preencher")]
    public void TheRouteLine_IsDerivedFromTheForm(string jump, string host, string expected) =>
        Assert.Equal(expected, ServerEditorRouteLine.Describe(jump, host, new ResWLocalizationService("pt-PT")));

    private static async Task<ServerEditorViewModel> OpenAddAsync(Ui7EditorWorld world)
    {
        await world.StartAsync();
        _ = ((AsyncRelayCommand)world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
        await world.ViewModel.LocalKeyDiscovery;
        return world.ViewModel;
    }

    private static LocalSshKey Key(string path, LocalSshKeyKind kind, bool recommended = false) =>
        new(path, Path.GetFileName(path), kind, recommended);

    private sealed class ScriptedKeys(IReadOnlyList<LocalSshKey>? keys, Exception? failure = null) : ILocalSshKeyDiscovery
    {
        public Task<IReadOnlyList<LocalSshKey>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            failure is null ? Task.FromResult(keys!) : Task.FromException<IReadOnlyList<LocalSshKey>>(failure);
    }
}
