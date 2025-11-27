using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using VPM.Models;

namespace VPM.Windows
{
    /// <summary>
    /// Converter to determine if an image is the first image of its package
    /// Used to show package header only above the first image
    /// </summary>
    public class FirstImageOfPackageConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return Visibility.Collapsed;

            try
            {
                var currentItem = values[0] as ImagePreviewItem;
                var allItems = values[1] as ObservableCollection<ImagePreviewItem>;

                if (currentItem == null || allItems == null || allItems.Count == 0)
                    return Visibility.Collapsed;

                // Check if this is the first item with this package name
                for (int i = 0; i < allItems.Count; i++)
                {
                    if (allItems[i] == currentItem)
                    {
                        // This is the current item - check if it's the first of its package
                        if (i == 0)
                            return Visibility.Visible;

                        // Check if previous item has different package name
                        if (allItems[i - 1].PackageName != currentItem.PackageName)
                            return Visibility.Visible;

                        return Visibility.Collapsed;
                    }
                }

                return Visibility.Collapsed;
            }
            catch
            {
                return Visibility.Collapsed;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
