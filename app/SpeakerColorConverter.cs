using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace XScribeDesktop;

public class SpeakerColorConverter : IValueConverter
{
    private static readonly SolidColorBrush You = new(System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xFF));
    private static readonly SolidColorBrush Them = new(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x9D));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) == "you" ? You : Them;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
