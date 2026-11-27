using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Lokasync.Core.Models;

namespace Lokasync.App.Converters;

public sealed class EntryStatusToBrushConverter : IValueConverter
{
    private static readonly Brush PendingBrush = CreateFrozenBrush(255, 248, 225);
    private static readonly Brush TranslatedBrush = CreateFrozenBrush(232, 245, 233);
    private static readonly Brush FailedValidationBrush = CreateFrozenBrush(255, 235, 238);
    private static readonly Brush ManuallyEditedBrush = CreateFrozenBrush(227, 242, 253);
    private static readonly Brush DefaultBrush = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is EntryStatus status)
        {
            return status switch
            {
                EntryStatus.Pending => PendingBrush,
                EntryStatus.Translated => TranslatedBrush,
                EntryStatus.FailedValidation => FailedValidationBrush,
                EntryStatus.ManuallyEdited => ManuallyEditedBrush,
                _ => DefaultBrush
            };
        }

        return DefaultBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
