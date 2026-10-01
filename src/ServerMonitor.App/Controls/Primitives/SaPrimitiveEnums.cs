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

/// <summary>Which Manual metric colour a <see cref="SaMetricBar"/> uses (CPU / memory / disk).</summary>
public enum SaMetricKind
{
    Cpu,
    Memory,
    Disk
}

/// <summary><see cref="SaKeyValueRow"/> layout: label above value (112:1906) or side by side (112:1919).</summary>
public enum SaKeyValueOrientation
{
    Stacked,
    Inline
}

/// <summary><see cref="SaListRow"/> density: one line (h48) or two lines (h70) - Figma 112:1057 / 112:1933.</summary>
public enum SaListRowVariant
{
    Simple,
    Rich
}

/// <summary><see cref="SaInlineNotice"/> severity. Figma designs only Error (callout) and Info (plain note).</summary>
public enum SaNoticeSeverity
{
    Info,
    Error
}

/// <summary>
/// <see cref="SaDialog"/> behaviour: Enter confirms, or (destructive) Enter and first focus take the safe button.
/// <c>Unspecified</c> is the property default (R2, Cortex C2-1): a callback only runs when the value CHANGES, so setting
/// Confirm must differ from the default; an unset dialog still confirms on Enter through SaDialogStyle's
/// DefaultButton=Primary setter.
/// </summary>
public enum SaDialogKind
{
    Unspecified,
    Confirm,
    Destructive
}

/// <summary><see cref="SaGroupNavigation"/> keyboard pattern of an item group (one Tab stop + arrows in both modes).</summary>
public enum SaGroupNavigationMode
{
    None,
    /// <summary>Segmented control: arrows move focus AND select.</summary>
    SelectionFollowsFocus,
    /// <summary>Navigation: arrows move focus only; Space/Enter activate.</summary>
    FocusOnly
}
