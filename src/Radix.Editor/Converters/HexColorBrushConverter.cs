using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Radix.Editor.Converters;

/// <summary>
/// Resolves an authored <c>#RRGGBB</c> string to a brush during rendering. The view
/// model stores only the color text, so constructing a view model does not require
/// the Avalonia UI thread.
/// </summary>
public sealed class HexColorBrushConverter : IValueConverter
{
    public static HexColorBrushConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text && text.Length == 7 && text[0] == '#' &&
            Color.TryParse(text, out Color color))
        {
            return new SolidColorBrush(color);
        }

        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
