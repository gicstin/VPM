using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VPM
{
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                string param = parameter as string;
                if (param == "EQ0")
                    return count == 0 ? Visibility.Visible : Visibility.Collapsed;

                return count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
