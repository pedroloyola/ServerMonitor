using System.Text.RegularExpressions;
using ServerMonitor.App.Controls;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.12 B12-05 (Beacon P3, Prism decision §2, Vigil reviews). After "Testar ligação" the password box is empty by design
/// (CaptureSecret reads and clears it; the secret is retained for the next Test/Save), which read as "my password was lost".
/// Presentation only: the box's placeholder (and UIA HelpText) says "Já introduzida · escreve para mudar" while a captured
/// secret is held. The flags say only THAT one is held; the secret lifecycle is unchanged (pinned here: capture, context
/// change, handover on save). Over the production view model (UI.7 world).
/// </summary>
public sealed partial class Ui12RetainedSecretPlaceholderTests : IDisposable
{
    private readonly Ui7EditorWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task TheFlag_FollowsCapture_ContextChange_AndTheHandoverOnSave()
    {
        await OpenAddAsync();
        var vm = _world.ViewModel;
        vm.Name = "db";
        vm.Host = "10.0.0.9";
        vm.Username = "ops";
        vm.SelectedAuthenticationIndex = 1; // password
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(vm.HasCapturedSecret);

        vm.CaptureSecret(string.Empty);            // nothing typed: nothing captured
        Assert.False(vm.HasCapturedSecret);

        vm.CaptureSecret("s3cret");
        Assert.True(vm.HasCapturedSecret);
        Assert.Single(raised, name => name == nameof(ServerEditorViewModel.HasCapturedSecret));

        vm.Host = "10.0.0.10";                     // another credential context: the staged secret is dropped (unchanged rule)
        Assert.False(vm.HasCapturedSecret);
        Assert.Equal(2, raised.Count(name => name == nameof(ServerEditorViewModel.HasCapturedSecret)));

        vm.CaptureSecret("s3cret");
        Assert.True(vm.TryCreateResult(out _));     // handed over to the save
        Assert.False(vm.HasCapturedSecret);
    }

    [Fact]
    public async Task TheJumpFlag_FollowsTheJumpSecret()
    {
        await OpenAddAsync();
        var vm = _world.ViewModel;
        vm.UseJumpHost = true;
        vm.JumpHost = "bastion.example.test";
        vm.JumpUsername = "ops";
        vm.SelectedJumpAuthenticationIndex = 1;

        vm.CaptureJumpSecret("jump-s3cret");
        Assert.True(vm.HasCapturedJumpSecret);

        vm.UseJumpHost = false;
        Assert.False(vm.HasCapturedJumpSecret);
    }

    [Theory]
    [InlineData(false, false, "ServerEditorPasswordPlaceholder")]
    [InlineData(true, false, "ServerEditorSecretKeepPlaceholder")]
    [InlineData(false, true, "ServerEditorSecretRetainedPlaceholder")]
    [InlineData(true, true, "ServerEditorSecretRetainedPlaceholder")]
    public void ThePlaceholder_RetainedWins_OverSaved_OverTheFieldsOwn(bool saved, bool captured, string expected) =>
        Assert.Equal(expected, ServerFormControl.SecretPlaceholderKey(saved, captured, "ServerEditorPasswordPlaceholder"));

    [Fact]
    public void TheText_ExistsInEveryLanguage_AndFitsTheBudget()
    {
        foreach (var culture in ResWLocalizationService.Cultures)
        {
            var text = new ResWLocalizationService(culture).GetString(ServerFormControl.RetainedSecretPlaceholderKey);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.InRange(text.Length, 1, 35);
        }

        Assert.Equal("Já introduzida · escreve para mudar", new ResWLocalizationService("pt-PT").GetString(ServerFormControl.RetainedSecretPlaceholderKey));
    }

    /// <summary>The placeholder and HelpText come from resources only - nothing read from a box or the view model's secret.</summary>
    [Fact]
    public void TheForm_SetsPlaceholderAndHelpText_FromResourcesOnly()
    {
        var form = AppSourceTree.CodeWithoutComments("Controls/ServerFormControl.xaml.cs");
        Assert.Matches(PlaceholderFromResource(), form);
        Assert.Matches(HelpTextFromResource(), form);
        Assert.DoesNotContain("SetName(box", form, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"box\.PlaceholderText = localization\.GetString\(SecretPlaceholderKey\(saved, captured, placeholderKey\)\);")]
    private static partial Regex PlaceholderFromResource();

    [GeneratedRegex(@"AutomationProperties\.SetHelpText\(box, captured \? localization\.GetString\(RetainedSecretPlaceholderKey\) : string\.Empty\);")]
    private static partial Regex HelpTextFromResource();

    private async Task OpenAddAsync()
    {
        await _world.StartAsync();
        _ = ((AsyncRelayCommand)_world.Dashboard.AddServerCommand).ExecuteAsync(); // completes when the visit ends
    }
}
