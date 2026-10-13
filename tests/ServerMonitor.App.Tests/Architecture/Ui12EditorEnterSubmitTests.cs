using System.Text.RegularExpressions;
using ServerMonitor.App.Views;
using EnterTarget = ServerMonitor.App.Views.ServerEditorPage.EnterTarget;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 UI12A-KBD-01 (Prism, P2). The editor submitted Save on Enter from ANY focused element except a multi-line text box,
/// so Enter on the jump-host CheckBox, an authentication RadioButton, a ToggleSwitch or a ComboBox saved the form. The
/// decision is the pure <see cref="ServerEditorPage.ShouldSubmitOnEnter"/>; the classification of the focused element
/// (WinUI types) and the call site are pinned by source. Real-keyboard check (SendInput) stays with Beacon (Prism R-3).
/// </summary>
public sealed partial class Ui12EditorEnterSubmitTests
{
    [Theory]
    [InlineData(nameof(EnterTarget.SingleLineText), true)]
    [InlineData(nameof(EnterTarget.SecretField), true)]
    [InlineData(nameof(EnterTarget.SaveButton), true)]
    [InlineData(nameof(EnterTarget.MultiLineText), false)]
    [InlineData(nameof(EnterTarget.Other), false)] // CheckBox, RadioButton, ToggleSwitch, ComboBox, any button but Save
    public void Enter_SubmitsOnlyFromTextPasswordOrSave(string target, bool submits) =>
        Assert.Equal(submits, ServerEditorPage.ShouldSubmitOnEnter(Enum.Parse<EnterTarget>(target)));

    [Fact]
    public void ThePage_ClassifiesTheFocusedElement_AndAsksThePredicate()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");

        Assert.Matches(EnterAsksThePredicate(), page);
        Assert.Matches(Classification(), page);
        Assert.DoesNotContain("is not TextBox { AcceptsReturn: true }", page, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"e\.Key == VirtualKey\.Enter && _viewModel is \{ IsConnectionWorkInProgress: false \}\)\s*\{\s*if \(ShouldSubmitOnEnter\(ClassifyEnterTarget\(FocusManager\.GetFocusedElement\(XamlRoot\)\)\)\)\s*\{\s*e\.Handled = true;\s*Submit\(\);")]
    private static partial Regex EnterAsksThePredicate();

    [GeneratedRegex(@"TextBox \{ AcceptsReturn: true \} => EnterTarget\.MultiLineText,\s*TextBox => EnterTarget\.SingleLineText,\s*PasswordBox => EnterTarget.SecretField,\s*_ when ReferenceEquals\(focused, PrimaryButton\) => EnterTarget\.SaveButton,\s*_ => EnterTarget\.Other,")]
    private static partial Regex Classification();
}
