namespace ServerMonitor.App.Controls;

/// <summary>
/// Prism c1 P-4 / Cortex c2 C2-2: the Compact row's name tooltip - the CURRENT name, only while it is cut (the row's
/// accessible name always carries the full name). Evaluated on a trim change and on a text change of the same element, so a
/// recycled row never keeps the previous server's name.
/// </summary>
internal static class CompactNameTooltip
{
    public static string? For(bool isTextTrimmed, string? text) =>
        isTextTrimmed && !string.IsNullOrEmpty(text) ? text : null;
}
