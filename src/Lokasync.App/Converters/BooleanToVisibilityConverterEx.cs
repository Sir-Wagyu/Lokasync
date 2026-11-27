using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lokasync.App.Converters;

public sealed class BooleanToVisibilityConverterEx : IValueConverter
{
    public bool Invert { get; set; }

    public bool CollapseWhenHidden { get; set; } = true;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value switch
        {
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            _ => value is not null
        };

        if (Invert)
        {
            flag = !flag;
        }

        if (flag)
        {
            return Visibility.Visible;
        }

        return CollapseWhenHidden ? Visibility.Collapsed : Visibility.Hidden;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Visibility visibility)
        {
            return false;
        }

        bool isVisible = visibility == Visibility.Visible;
        return Invert ? !isVisible : isVisible;
    }
}
