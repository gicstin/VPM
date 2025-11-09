using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VPM
{
    /// <summary>
    /// Converts string values to Visibility enum values
    /// </summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public static readonly StringToVisibilityConverter Instance = new StringToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string stringValue)
            {
                // Return Visible if string has content, Collapsed if empty/null
                return string.IsNullOrWhiteSpace(stringValue) ? Visibility.Collapsed : Visibility.Visible;
            }

            // Return Collapsed for non-string values or null
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                return visibility == Visibility.Visible ? "Visible" : string.Empty;
            }

            return string.Empty;
        }
    }
}

