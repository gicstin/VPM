using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace VPM
{
    /// <summary>
    /// Converts Color values to SolidColorBrush instances
    /// </summary>
    public class ColorToBrushConverter : IValueConverter
    {
        public static readonly ColorToBrushConverter Instance = new ColorToBrushConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Color color)
            {
                return new SolidColorBrush(color);
            }

            if (value is string colorString)
            {
                try
                {
                    var convertedColor = (Color)ColorConverter.ConvertFromString(colorString);
                    return new SolidColorBrush(convertedColor);
                }
                catch
                {
                    // Return default brush if conversion fails
                    return new SolidColorBrush(Colors.Black);
                }
            }

            // Return default brush for unsupported types
            return new SolidColorBrush(Colors.Black);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SolidColorBrush brush)
            {
                return brush.Color;
            }

            return Colors.Transparent;
        }
    }
}

