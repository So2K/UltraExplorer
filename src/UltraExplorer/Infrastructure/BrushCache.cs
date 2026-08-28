using System.Collections.Concurrent;
using System.Windows.Media;

namespace UltraExplorer.Infrastructure;

/// <summary>
/// Frozen brushes shared by every node on the canvas.  Thousands of nodes bind
/// to a handful of accent colours, so allocating one brush per node would cost
/// far more than the dictionary lookup.
/// </summary>
public static class BrushCache
{
    private static readonly ConcurrentDictionary<string, Brush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    public static Brush Get(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return System.Windows.Media.Brushes.Transparent;
        }

        return Brushes.GetOrAdd(hex, static value =>
        {
            try
            {
                var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
                brush.Freeze();
                return brush;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidOperationException)
            {
                return System.Windows.Media.Brushes.Gray;
            }
        });
    }
}
