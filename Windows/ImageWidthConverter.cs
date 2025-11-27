using System;
using System.Globalization;
using System.Windows.Data;

namespace VPM.Windows
{
    public class ImageWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return 200.0;

            try
            {
                double actualWidth = 0;
                if (values[0] is double d) actualWidth = d;
                else if (values[0] != null) double.TryParse(values[0].ToString(), out actualWidth);

                int desiredColumns = 3;
                if (values[1] is int i) desiredColumns = i;
                else if (values[1] != null) 
                {
                    if (double.TryParse(values[1].ToString(), out double dCols))
                        desiredColumns = (int)dCols;
                }

                if (actualWidth <= 0 || desiredColumns <= 0)
                    return 200.0;

                // Parse parameter for margin and border values (format: "margin,border")
                double itemMargin = 2.0;
                double borderThickness = 1.0;
                
                if (parameter is string paramStr && !string.IsNullOrEmpty(paramStr))
                {
                    var parts = paramStr.Split(',');
                    if (parts.Length >= 1 && double.TryParse(parts[0].Trim(), out double margin))
                        itemMargin = margin;
                    if (parts.Length >= 2 && double.TryParse(parts[1].Trim(), out double border))
                        borderThickness = border;
                }

                double scrollbarWidth = 16.0;
                double safetyPadding = 2.0;
                
                // Calculate available width for the entire row
                double availableRowWidth = actualWidth - scrollbarWidth - borderThickness - safetyPadding;
                
                // Calculate how many columns actually fit
                // Each column needs: imageWidth + (2 * itemMargin)
                // We want to find imageWidth such that: (desiredColumns * (imageWidth + 2*itemMargin)) <= availableRowWidth
                // Rearranging: imageWidth = (availableRowWidth / desiredColumns) - (2 * itemMargin)
                
                double imageWidth = (availableRowWidth / desiredColumns) - (2 * itemMargin);
                
                // Ensure we don't return a negative width
                if (imageWidth <= 0) return 50.0;

                double result = Math.Floor(Math.Max(50, imageWidth));
                
                return result;
            }
            catch
            {
                return 200.0;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
