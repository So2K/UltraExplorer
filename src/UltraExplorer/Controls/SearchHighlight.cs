using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using UltraExplorer.Models;

namespace UltraExplorer.Controls;

/// <summary>
/// Fills a <see cref="TextBlock"/> with a name in pieces, the parts a search
/// matched drawn in the accent colour and a heavier weight:
/// <c>controls:SearchHighlight.Segments="{Binding NameSegments}"</c>.
/// </summary>
public static class SearchHighlight
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments",
        typeof(IReadOnlyList<TextSegment>),
        typeof(SearchHighlight),
        new PropertyMetadata(null, OnSegmentsChanged));

    public static IReadOnlyList<TextSegment>? GetSegments(DependencyObject element) => (IReadOnlyList<TextSegment>?)element.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject element, IReadOnlyList<TextSegment>? value) => element.SetValue(SegmentsProperty, value);

    private static void OnSegmentsChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock text)
        {
            return;
        }

        text.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<TextSegment> segments)
        {
            return;
        }

        var accent = text.TryFindResource("AccentBrightBrush") as Brush ?? Brushes.DeepSkyBlue;
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.IsMatch)
            {
                run.Foreground = accent;
                run.FontWeight = FontWeights.SemiBold;
            }

            text.Inlines.Add(run);
        }
    }
}
