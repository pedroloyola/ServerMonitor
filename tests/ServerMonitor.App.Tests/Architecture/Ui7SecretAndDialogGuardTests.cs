using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.7B static guards (Vigil CP-9, CR-8, CR-9, R-6; B-10/B-11/B-12): secrets live only in PasswordBoxes that are never
/// bound, no UIA property is bound to anything secret-derived, no string on the editor view model can carry one, the page
/// accepts a key only through the controller (never the view model's trust method directly, never from Save), the trust
/// dialog's safe button is the default, and the key picker is a picker (no editable path box).
/// </summary>
public sealed partial class Ui7SecretAndDialogGuardTests
{
    private static readonly string[] EditorXaml =
    [
        "Controls/ServerFormControl.xaml",
        "Views/ServerEditorPage.xaml",
        "Views/ServerEditorConnectionDialog.xaml",
        "Views/SshConfigImportDialog.xaml"
    ];

    /// <summary>The only bindable names that merely mention a secret (flags about it, never its value).</summary>
    private static readonly HashSet<string> SecretFlagBindings = new(StringComparer.Ordinal)
    {
        "IsPasswordAuthentication", "IsJumpPasswordAuthentication", "HasSavedPassphrase", "HasSavedPassword",
        "HasSavedJumpSecret", "RemoveSavedPassphrase"
    };

    [Fact]
    public void CP9_SecretFieldsArePasswordBoxes_NeverBound_NeverATextBox()
    {
        var form = AppSourceTree.LoadXaml("Controls/ServerFormControl.xaml");
        var boxes = form.Descendants().Where(e => e.Name.LocalName == "PasswordBox").ToList();
        Assert.Equal(
            ["PassphraseField", "PasswordField", "JumpPassphraseField", "JumpPasswordField"],
            boxes.Select(b => (string?)b.Attribute(AppSourceTree.Xaml + "Name")));

        foreach (var file in EditorXaml)
        {
            foreach (var element in AppSourceTree.LoadXaml(file).Descendants())
            {
                var name = $"{(string?)element.Attribute(AppSourceTree.Xaml + "Name")} {(string?)element.Attribute(AppSourceTree.Xaml + "Uid")}";
                if (element.Name.LocalName == "PasswordBox")
                {
                    Assert.Null(element.Attribute("Password"));
                    Assert.DoesNotContain(element.Attributes(), a => a.Value.Contains('{', StringComparison.Ordinal) && a.Name.LocalName is "Password" or "Tag" or "DataContext");
                }
                else if (element.Name.LocalName is "TextBox" or "RichEditBox" or "AutoSuggestBox")
                {
                    Assert.False(SecretWords().IsMatch(name), $"{file}: a {element.Name.LocalName} named '{name.Trim()}' looks like a secret field");
                }
            }
        }
    }

    [Fact]
    public void CP9_NoBindingAndNoAutomationPropertyReadsASecret()
    {
        foreach (var file in EditorXaml)
        {
            foreach (var attribute in AppSourceTree.LoadXaml(file).Descendants().SelectMany(e => e.Attributes()))
            {
                var value = attribute.Value;
                if (!value.Contains("{Binding", StringComparison.Ordinal) && !value.Contains("{x:Bind", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match path in BindingPath().Matches(value))
                {
                    var member = path.Groups["path"].Value.Split('.')[^1];
                    Assert.False(
                        SecretWords().IsMatch(member) && !SecretFlagBindings.Contains(member),
                        $"{file}: {attribute.Name} binds '{member}'");
                }
            }
        }

        // Code-behind never hands a password box's text to UI Automation, a tooltip or a log.
        foreach (var code in new[] { "Controls/ServerFormControl.xaml.cs", "Views/ServerEditorPage.xaml.cs", "Views/ServerEditorConnectionDialog.xaml.cs", "Views/SshConfigImportDialog.xaml.cs" })
        {
            var text = AppSourceTree.CodeWithoutComments(code);
            foreach (var line in text.Split('\n').Where(l => l.Contains("AutomationProperties.Set", StringComparison.Ordinal) || l.Contains("ToolTipService.SetToolTip", StringComparison.Ordinal) || l.Contains("Log", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain(".Password", line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void CP9_ThePasswordBoxesAreReadOnlyToStageAndCleared_InTheFormOnly()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/ServerFormControl.xaml.cs");
        // Read: CaptureSecret (4) + HasTypedSecret (4); cleared: CaptureSecret (4) + ClearSecrets (4).
        Assert.Equal(8, Regex.Matches(code, @"Field\.Password\.Length > 0|\? (Jump)?(Password|Passphrase)Field\.Password|: (Jump)?(Password|Passphrase)Field\.Password").Count);
        Assert.Equal(8, Regex.Matches(code, @"Field\.Password = string\.Empty;").Count);
        foreach (var other in new[] { "Views/ServerEditorPage.xaml.cs", "Views/ServerEditorConnectionDialog.xaml.cs", "Views/SshConfigImportDialog.xaml.cs" })
        {
            Assert.DoesNotContain(".Password", AppSourceTree.CodeWithoutComments(other), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CP9_TheEditorViewModelHasNoStringThatCouldHoldASecret()
    {
        var offenders = typeof(ServerEditorViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && SecretWords().IsMatch(p.Name))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(offenders);
        Assert.DoesNotContain("RevealAsString", AppSourceTree.CodeWithoutComments("ViewModels/ServerEditorViewModel.cs"), StringComparison.Ordinal);
    }

    // ---- B-10: the page accepts only through the controller, the dialog defaults to the safe button ------------------

    [Fact]
    public void TheTrustAccept_IsOneExplicitDialogGesture_ThroughTheController()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");
        Assert.Equal(1, Count(page, "_controller.AcceptTrustAsync("));
        Assert.Contains("OnTrustPrimaryClick", page, StringComparison.Ordinal);
        foreach (var code in new[] { "Views/ServerEditorPage.xaml.cs", "Controls/ServerFormControl.xaml.cs", "Views/ServerEditorConnectionDialog.xaml.cs" })
        {
            Assert.DoesNotContain("TrustAndConnectAsync", AppSourceTree.CodeWithoutComments(code), StringComparison.Ordinal);
        }

        var controller = AppSourceTree.CodeWithoutComments("ViewModels/ServerEditorPageController.cs");
        Assert.Equal(1, Count(controller, "TrustAndConnectAsync()"));
        var submit = controller[controller.IndexOf("public async Task<ServerEditorSubmitOutcome?> SubmitAsync", StringComparison.Ordinal)..];
        submit = submit[..submit.IndexOf("public HostKeyTrustPrompt? PromptToShow", StringComparison.Ordinal)];
        Assert.DoesNotContain("Trust", submit, StringComparison.Ordinal); // Save never trusts

        var dialog = AppSourceTree.CodeWithoutComments("Views/ServerEditorConnectionDialog.xaml.cs");
        Assert.Contains("DefaultButton = ContentDialogButton.Close;", dialog, StringComparison.Ordinal);
        Assert.Contains("PrimaryButtonText = prompt.AcceptKey is { } accept", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void TheImport_IsAppliedOnlyThroughTheController_AndTheFormHasNoInlineImportOrTrustPanel()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");
        Assert.Equal(1, Count(page, "_controller.ApplyImport("));
        Assert.DoesNotContain("ApplySshConfigHost", page, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadSshConfigHostsAsync", page, StringComparison.Ordinal);

        var form = AppSourceTree.LoadXaml("Controls/ServerFormControl.xaml").ToString();
        foreach (var gone in new[] { "SshConfigHosts", "HasUnknownHostKey", "HasHostKeyMismatch", "PresentedHostKeyFingerprint", "HostKeyTrustAndConnectButton" })
        {
            Assert.DoesNotContain(gone, form, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("ApplySshConfigHost", AppSourceTree.CodeWithoutComments("Controls/ServerFormControl.xaml.cs"), StringComparison.Ordinal);
    }

    // ---- B-12: a picker, not a path box --------------------------------------------------------------------------------

    [Fact]
    public void TheKeyPickers_ArePickerButtons_AndNoPathIsEditableOrBound()
    {
        var form = AppSourceTree.LoadXaml("Controls/ServerFormControl.xaml");
        foreach (var name in new[] { "KeyPickerButton", "JumpKeyPickerButton" })
        {
            var button = form.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == name);
            Assert.Equal("{StaticResource SaPickerButtonStyle}", (string?)button.Attribute("Style"));
            Assert.Contains(button.Descendants(), e => e.Name.LocalName == "MenuFlyout");
        }

        var xaml = form.ToString();
        Assert.DoesNotContain("{Binding PrivateKeyPath", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding JumpPrivateKeyPath", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(form.Descendants(), e => e.Name.LocalName == "ComboBox" && (string?)e.Attribute("ItemsSource") == "{Binding LocalKeyOptions}");
    }

    [Fact]
    public void SaPickerButtonStyle_IsTheFigmaPicker()
    {
        var style = AppSourceTree.LoadXaml("Styles/Components/Sa.Buttons.xaml").Descendants()
            .Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SaPickerButtonStyle");
        string? Setter(string property) => style.Elements()
            .Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property)
            .Select(e => (string?)e.Attribute("Value")).SingleOrDefault();

        Assert.Equal("{StaticResource SaButtonBaseStyle}", (string?)style.Attribute("BasedOn"));
        Assert.Equal("40", Setter("Height"));
        Assert.Equal("{StaticResource SaRadiusControl}", Setter("CornerRadius"));
        Assert.Equal("14,0", Setter("Padding"));
        Assert.Equal("Left", Setter("HorizontalContentAlignment"));
        Assert.Equal("Normal", Setter("FontWeight"));
    }

    [Fact]
    public void EveryNewDialogKey_ExistsInEveryCulture_AndPtPtSaysTu()
    {
        var uids = new[] { "Views/ServerEditorConnectionDialog.xaml", "Views/SshConfigImportDialog.xaml", "Controls/ServerFormControl.xaml" }
            .SelectMany(file => AppSourceTree.LoadXaml(file).Descendants())
            .Select(e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid")).OfType<string>().Distinct().ToList();
        var code = string.Concat(new[] { "Views/ServerEditorConnectionDialog.xaml.cs", "Views/SshConfigImportDialog.xaml.cs", "Controls/ServerFormControl.xaml.cs", "ViewModels/ServerEditorPresentation.cs" }
            .Select(AppSourceTree.CodeWithoutComments));
        var keys = Regex.Matches(code, "\"(ServerEditor[A-Za-z]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(keys.Count >= 50, $"only {keys.Count} keys found in code");

        foreach (var culture in new[] { "pt-PT", "pt-BR", "en-US" })
        {
            var resources = XDocument.Load(AppSourceTree.Full($"Resources/{culture}/Resources.resw")).Root!.Elements("data")
                .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);
            Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(resources.GetValueOrDefault(key)), $"{culture}: {key}"));
            Assert.All(uids, uid => Assert.Contains(resources.Keys, name => name.StartsWith(uid + ".", StringComparison.Ordinal)));
            if (culture == "pt-PT")
            {
                Assert.All(keys, key => Assert.DoesNotContain("você", resources[key], StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private static int Count(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;

    [GeneratedRegex("password|passphrase|secret|senha|palavra", RegexOptions.IgnoreCase)]
    private static partial Regex SecretWords();

    [GeneratedRegex(@"\{(?:Binding|x:Bind)\s+(?:Path=)?(?<path>[A-Za-z0-9_.]+)")]
    private static partial Regex BindingPath();
}
