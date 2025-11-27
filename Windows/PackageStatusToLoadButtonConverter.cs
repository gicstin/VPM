using System;
using System.Globalization;
using System.Windows.Data;

namespace VPM.Windows
{
    /// <summary>
    /// Converts package status to Load/Unload button text
    /// </summary>
    public class PackageStatusToLoadButtonConverter : IValueConverter
    {
        public static readonly PackageStatusToLoadButtonConverter Instance = new PackageStatusToLoadButtonConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return "📥 Load";

            try
            {
                string status = value.ToString();
                return status == "Loaded" ? "📤 Unload" : "📥 Load";
            }
            catch
            {
                return "📥 Load";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
