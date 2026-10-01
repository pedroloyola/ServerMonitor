using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>
/// QA-ONLY (UI.2 S2) gallery page. The raw SaColor* swatches are built in code from the manifest: XAML outside
/// Styles/Tokens/** may never reference SaColor* (SaColourTokensStayInsideTokensAndSaBrushesAreThemeResources).
/// </summary>
public sealed partial class QaColorsPage : Page
{
    public QaColorsPage()
    {
        InitializeComponent();

        Application.Current.Resources.TryGetValue("SaMonoTextStyle", out var monoStyle);
        foreach (var entry in QaTokenManifest.Entries.Where(entry => entry.Kind == QaTokenKind.Color))
        {
            var found = Application.Current.Resources.TryGetValue(entry.Key, out var value) && value is Color;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Border
            {
                Width = 48,
                Height = 24,
                Background = found ? new SolidColorBrush((Color)value!) : null
            });
            row.Children.Add(new TextBlock
            {
                Style = monoStyle as Style,
                VerticalAlignment = VerticalAlignment.Center,
                Text = found ? $"{entry.Key}  {QaTokenSelfCheck.Describe(value)}" : $"{entry.Key}  MISSING"
            });
            PrimitiveSwatches.Children.Add(row);
        }
    }
}
