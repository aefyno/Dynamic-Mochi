using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DynamicMochi;

/// <summary>Capsule corner radius: half the height while collapsed, capped at 28 when expanded.</summary>
public sealed class HeightToRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double h = value is double d ? d : 44;
        return new CornerRadius(Math.Min(h / 2, 28));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
