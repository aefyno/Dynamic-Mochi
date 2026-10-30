using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DynamicMochi;

/// <summary>Corner radius follows the capsule height.</summary>
public sealed class HeightToRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double h = value is double d ? d : 38;
        return new CornerRadius(Math.Min(h / 2, 26));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
