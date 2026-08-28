using System.Globalization;
using System.Windows;
using System.Windows.Data;
using UltraExplorer.Models;

namespace UltraExplorer.Converters;

/// <summary>
/// Converter parameter is the minimum level name: Glyph, Compact or Detailed.
/// </summary>
public sealed class ViewAllDetailVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ViewAllDetailLevel actual
            || parameter is not string requested
            || !Enum.TryParse<ViewAllDetailLevel>(requested, ignoreCase: true, out var minimum))
        {
            return Visibility.Collapsed;
        }

        return actual >= minimum ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
