using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.11 H02/H03 static guards (Prism UI11A §2.4, Cortex §3). The toggle's four animated transitions start from the
/// PRESENTED value (no From, no keyframe at 0, so a reversal turns around in place), use the toggle token, cross-fade the
/// On fill and thumb colour, and are gated by Reduced Motion.
/// </summary>
public sealed class Ui11ToggleGuardTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XElement ToggleTemplateRoot() =>
        AppSourceTree.LoadXaml("Styles/Components/Sa.Forms.xaml").Descendants(Presentation + "Style")
            .Single(style => (string?)style.Attribute(AppSourceTree.Xaml + "Key") == "SaToggleSwitchStyle")
            .Descendants(Presentation + "ControlTemplate").First().Elements().First();

    [Fact]
    public void Toggle_HasTheFourAnimatedTransitions_StartingFromThePresentedValue()
    {
        var group = ToggleTemplateRoot().Descendants(Presentation + "VisualStateGroup")
            .Single(g => (string?)g.Attribute(AppSourceTree.Xaml + "Name") == "ToggleStates");
        var transitions = group.Descendants(Presentation + "VisualTransition").ToList();

        Assert.Equal(
            ["Off>On", "On>Off", "Dragging>On", "Dragging>Off"],
            transitions.Select(t => $"{(string?)t.Attribute("From")}>{(string?)t.Attribute("To")}"));
        foreach (var transition in transitions)
        {
            var on = (string?)transition.Attribute("To") == "On";
            var animations = transition.Descendants(Presentation + "DoubleAnimationUsingKeyFrames").ToList();
            Assert.Equal(
                ["KnobTranslateTransform.X", "SwitchKnobBounds.Opacity", "SwitchKnobOn.Opacity", "SwitchKnobOff.Opacity"],
                animations.Select(a => $"{(string?)a.Attribute("Storyboard.TargetName")}.{(string?)a.Attribute("Storyboard.TargetProperty")}"));
            foreach (var animation in animations)
            {
                Assert.Null(animation.Attribute("From")); // never restart from a fixed origin: a reversal turns around in place
                Assert.Single(animation.Elements());
            }

            // Position and track fill travel over the toggle token (spline); Prism D1 (B11-6): the thumb colour is a DISCRETE
            // swap at mid-travel (SaMotionFadeDuration = 83 ms), never a crossfade that crosses the track in Dark.
            foreach (var travel in animations.Take(2))
            {
                var frame = travel.Elements().Single();
                Assert.Equal("SplineDoubleKeyFrame", frame.Name.LocalName);
                Assert.Equal("{StaticResource SaMotionToggleDuration}", (string?)frame.Attribute("KeyTime"));
            }

            foreach (var swap in animations.Skip(2))
            {
                var frame = swap.Elements().Single();
                Assert.Equal("DiscreteDoubleKeyFrame", frame.Name.LocalName);
                Assert.Equal("{StaticResource SaMotionFadeDuration}", (string?)frame.Attribute("KeyTime"));
            }

            Assert.Equal(on ? "18" : "0", (string?)animations[0].Elements().Single().Attribute("Value"));
            Assert.Equal("{StaticResource SaMotionPointToPointKeySpline}", (string?)animations[0].Elements().Single().Attribute("KeySpline"));
            Assert.Equal("{StaticResource SaMotionLinearKeySpline}", (string?)animations[1].Elements().Single().Attribute("KeySpline"));
            Assert.Equal(on ? "1" : "0", (string?)animations[1].Elements().Single().Attribute("Value"));
            Assert.Equal(on ? "1" : "0", (string?)animations[2].Elements().Single().Attribute("Value"));
            Assert.Equal(on ? "0" : "1", (string?)animations[3].Elements().Single().Attribute("Value"));
        }
    }

    [Fact]
    public void Toggle_TransitionsAreGatedByReducedMotion()
    {
        var root = ToggleTemplateRoot();
        var manager = root.Elements(Presentation + "VisualStateManager.CustomVisualStateManager").Single().Elements().Single();

        Assert.Equal("MotionVisualStateManager", manager.Name.LocalName);
        Assert.Equal("using:ServerMonitor.App.Services.Motion", manager.Name.NamespaceName);
    }

    [Fact]
    public void Toggle_OnThumbColour_CrossFadesAsASecondEllipse_NeverASetterSnap()
    {
        var root = ToggleTemplateRoot();

        Assert.DoesNotContain(root.Descendants(Presentation + "Setter"), setter => ((string?)setter.Attribute("Target") ?? "").StartsWith("SwitchKnob", StringComparison.Ordinal));
        var on = root.Descendants(Presentation + "Ellipse").Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "SwitchKnobOn");
        Assert.Equal("0", (string?)on.Attribute("Opacity"));
        Assert.Equal("{ThemeResource SaToggleOnThumbBrush}", (string?)on.Attribute("Fill"));
    }

    /// <summary>Prism D3 (B11-11): at rest exactly ONE thumb ellipse is visible - the On state hides the Off ellipse.</summary>
    [Fact]
    public void Toggle_OnState_ShowsExactlyOneThumbEllipse()
    {
        var on = ToggleTemplateRoot().Descendants(Presentation + "VisualState").Single(s => (string?)s.Attribute(AppSourceTree.Xaml + "Name") == "On");
        var targets = on.Descendants(Presentation + "DoubleAnimation")
            .ToDictionary(a => $"{(string?)a.Attribute("Storyboard.TargetName")}.{(string?)a.Attribute("Storyboard.TargetProperty")}", a => (string?)a.Attribute("To"));

        Assert.Equal("1", targets["SwitchKnobOn.Opacity"]);
        Assert.Equal("0", targets["SwitchKnobOff.Opacity"]);
    }
}
