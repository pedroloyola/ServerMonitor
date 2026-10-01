namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// The primitive's own status vocabulary. Deliberately NOT the domain's health enum: primitives know nothing
/// of ServerMonitor.Core, and a page maps its view-model state onto this when it adopts the primitive (UI.3+).
/// Each value is one visual state of <see cref="SaStatusIndicator"/>'s StatusStates group.
/// </summary>
public enum SaStatusKind
{
    Unknown,
    Healthy,
    Attention,
    Error,
    Offline,
    Stale
}
