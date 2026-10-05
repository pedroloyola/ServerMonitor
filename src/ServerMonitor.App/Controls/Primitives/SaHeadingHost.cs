using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>A native content-control focus target around a plain, non-selectable heading.</summary>
public sealed class SaHeadingHost : ContentControl
{
    public SaHeadingHost()
    {
        DefaultStyleKey = typeof(ContentControl);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        UseSystemFocusVisuals = true;
        IsTabStop = false;
        LostFocus += (_, _) => IsTabStop = false;
        Unloaded += (_, _) => IsTabStop = false;
    }

    public bool FocusHeading()
    {
        // Native Control focus requires IsTabStop. Only this programmatic visit enables it;
        // LostFocus removes it from subsequent Tab traversal. The TextBlock is never focused.
        IsTabStop = true;
        var focused = Focus(FocusState.Programmatic);
        if (!focused) IsTabStop = false;
        return focused;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HeadingPeer(this);

    private sealed class HeadingPeer(SaHeadingHost owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(SaHeadingHost);
        protected override string GetNameCore() => (owner.Content as TextBlock)?.Text ?? base.GetNameCore();
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}
