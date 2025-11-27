using System;
using System.Globalization;
using System.Windows.Data;
using System.Diagnostics;

namespace VPM.Windows
{
    public class DebugConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            Debug.WriteLine($"DebugConverter: Value={value}, TargetType={targetType}, Param={parameter}");
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value;
        }
    }
}
