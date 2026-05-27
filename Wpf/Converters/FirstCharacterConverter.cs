using System.Globalization;
using System.Windows.Data;

namespace RaycastPM.Converters;

public sealed class FirstCharacterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString()?.Trim();
        return string.IsNullOrEmpty(text) ? "#" : text[..1].ToUpper(culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
