using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>QA-ONLY (UI.2 S2, F-1): one probe per token; read back by <see cref="QaTokenSelfCheck"/>.</summary>
public sealed partial class QaTokenProbePage : Page
{
    public QaTokenProbePage()
    {
        InitializeComponent();
    }
}
