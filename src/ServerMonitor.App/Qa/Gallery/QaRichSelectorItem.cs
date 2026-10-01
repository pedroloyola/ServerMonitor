namespace ServerMonitor.App.Qa.Gallery;

/// <summary>
/// QA-ONLY (UI.3 foundation): sample item for the SaSelectorRichStyle gallery entry. It satisfies the
/// SaSelectorRichItemTemplate contract (a <c>Name</c> and a <c>Subtitle</c> string) without any view model.
/// </summary>
public sealed class QaRichSelectorItem
{
    public string Name { get; set; } = string.Empty;

    public string Subtitle { get; set; } = string.Empty;

    public override string ToString() => Name;
}
