using System;
using System.Globalization;
using System.Windows.Data;

namespace VPM.Windows
{
    public class CountToColumnsConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[1] is int maxColumns)
            {
                // Always return the configured maxColumns, regardless of item count.
                // This ensures that if the user wants 5 columns, the grid is divided into 5,
                // even if there are fewer items (they will just take up 1/5th of the width each).
                return Math.Max(1, maxColumns);
            }
            return 1;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
