using System;
using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using VPM.Models;

namespace VPM
{
    /// <summary>
    /// Converts a collection of ImagePreviewItems to Visibility based on whether any item has IsExtracted = true
    /// Supports both IValueConverter and IMultiValueConverter for live updates
    /// </summary>
    public class HasExtractedItemsConverter : IValueConverter, IMultiValueConverter
    {
        public static readonly HasExtractedItemsConverter Instance = new HasExtractedItemsConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is IEnumerable collection)
            {
                // Check if any item in the collection has IsExtracted = true
                foreach (var item in collection)
                {
                    if (item is ImagePreviewItem previewItem && previewItem.IsExtracted)
                    {
                        return Visibility.Visible;
                    }
                }
            }

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }

        // IMultiValueConverter implementation for MultiBinding support
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // Check if ShowLoadButton is false (Custom mode) - hide delete button in Custom mode
            if (values.Length > 1 && values[1] is bool showLoadButton && !showLoadButton)
            {
                return Visibility.Collapsed;
            }

            if (values.Length > 0 && values[0] is IEnumerable collection)
            {
                // Check if any item in the collection has IsExtracted = true
                foreach (var item in collection)
                {
                    if (item is ImagePreviewItem previewItem && previewItem.IsExtracted)
                    {
                        return Visibility.Visible;
                    }
                }
            }

            return Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            return new object[targetTypes.Length];
        }
    }
}
