using System;
using System.Globalization;
using System.Windows.Data;

namespace VPM.Windows
{
    /// <summary>
    /// Converts bytes to megabytes for display
    /// </summary>
    public class BytesToMegabytesConverter : IValueConverter
    {
        public static readonly BytesToMegabytesConverter Instance = new BytesToMegabytesConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return 0.0;

            try
            {
                long bytes = 0;
                if (value is long l)
                    bytes = l;
                else if (value is int i)
                    bytes = i;
                else if (long.TryParse(value.ToString(), out long parsed))
                    bytes = parsed;

                return (double)bytes / (1024.0 * 1024.0);
            }
            catch
            {
                return 0.0;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
