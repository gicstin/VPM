using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VPM.Models;
using VPM.Services;

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

    /// <summary>
    /// Converts the ListView ActualWidth to the WrapPanel width
    /// </summary>
    public class PanelWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double width && width > 0)
            {
                double result = Math.Max(100, width);
                return result;
            }
            return 200.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

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

    /// <summary>
    /// Debug converter for troubleshooting bindings
    /// </summary>
    public class DebugConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            System.Diagnostics.Debug.WriteLine($"DebugConverter: Value={value}, TargetType={targetType}, Param={parameter}");
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value;
        }
    }

    /// <summary>
    /// Converts item count to column count for grid layouts
    /// </summary>
    public class CountToColumnsConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[1] is int maxColumns)
            {
                return Math.Max(1, maxColumns);
            }
            return 1;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Calculates image width based on container width and desired columns
    /// </summary>
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

                bool matchWidth = false;
                if (values.Length > 2 && values[2] is bool b)
                {
                    matchWidth = b;
                }

                if (actualWidth <= 0 || desiredColumns <= 0)
                    return 200.0;

                double itemMargin = 3.0;
                double borderThickness = 1.0;

                if (parameter is string paramStr && !string.IsNullOrEmpty(paramStr))
                {
                    var parts = paramStr.Split(',');
                    if (parts.Length >= 1 && double.TryParse(parts[0].Trim(), out double margin))
                        itemMargin = margin;
                    if (parts.Length >= 2 && double.TryParse(parts[1].Trim(), out double border))
                        borderThickness = border;
                }

                double minImageWidth = 100.0;
                double totalMarginWidth = (desiredColumns + 1) * itemMargin;
                double availableRowWidth = actualWidth - totalMarginWidth;

                int actualColumns = desiredColumns;
                double minWidthPerColumn = minImageWidth + (2 * itemMargin);

                while (actualColumns > 1 && (actualColumns * minWidthPerColumn) > availableRowWidth)
                {
                    actualColumns--;
                }

                double imageWidth = (availableRowWidth - ((actualColumns - 1) * itemMargin)) / actualColumns;

                if (imageWidth <= 0) return minImageWidth;

                double result = Math.Floor(Math.Max(minImageWidth, imageWidth));
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

    /// <summary>
    /// Determines if an image is the first image of its package
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

                for (int i = 0; i < allItems.Count; i++)
                {
                    if (allItems[i] == currentItem)
                    {
                        if (i == 0)
                            return Visibility.Visible;

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
                    return new SolidColorBrush(Colors.Black);
                }
            }

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
                return string.IsNullOrWhiteSpace(stringValue) ? Visibility.Collapsed : Visibility.Visible;
            }

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

    /// <summary>
    /// Converts DataGridHeadersVisibility values to Visibility enum values
    /// </summary>
    public class DataGridHeadersVisibilityConverter : IValueConverter
    {
        public static readonly DataGridHeadersVisibilityConverter Instance = new DataGridHeadersVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DataGridHeadersVisibility headersVisibility)
            {
                return headersVisibility == DataGridHeadersVisibility.None ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                return visibility == Visibility.Visible ? DataGridHeadersVisibility.Column : DataGridHeadersVisibility.None;
            }
            return DataGridHeadersVisibility.Column;
        }
    }

    /// <summary>
    /// Converts thumbnail path strings to ImageSource objects
    /// </summary>
    public class ThumbnailPathConverter : IValueConverter
    {
        public static readonly ThumbnailPathConverter Instance = new ThumbnailPathConverter();

        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<string, BitmapImage> _cache = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> _lru = new LinkedList<string>();
        private static readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.OrdinalIgnoreCase);
        private const int MaxCacheSize = 256;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string thumbnailPath && !string.IsNullOrWhiteSpace(thumbnailPath))
            {
                try
                {
                    if (File.Exists(thumbnailPath))
                    {
                        lock (_cacheLock)
                        {
                            if (_cache.TryGetValue(thumbnailPath, out var cached))
                            {
                                if (_lruNodes.TryGetValue(thumbnailPath, out var node))
                                {
                                    _lru.Remove(node);
                                    _lru.AddFirst(node);
                                }
                                return cached;
                            }
                        }

                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        bitmap.UriSource = new Uri(thumbnailPath, UriKind.Absolute);
                        bitmap.DecodePixelWidth = 128;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        lock (_cacheLock)
                        {
                            if (_cache.ContainsKey(thumbnailPath))
                            {
                                return _cache[thumbnailPath];
                            }

                            _cache[thumbnailPath] = bitmap;
                            if (_lruNodes.TryGetValue(thumbnailPath, out var existingNode))
                            {
                                _lru.Remove(existingNode);
                                _lruNodes.Remove(thumbnailPath);
                            }
                            var newNode = _lru.AddFirst(thumbnailPath);
                            _lruNodes[thumbnailPath] = newNode;

                            while (_cache.Count > MaxCacheSize && _lru.Last != null)
                            {
                                var keyToRemove = _lru.Last.Value;
                                _lru.RemoveLast();
                                _lruNodes.Remove(keyToRemove);
                                _cache.Remove(keyToRemove);
                            }
                        }

                        return bitmap;
                    }
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts PackageItem to content counts display string
    /// </summary>
    public class ContentCountsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not PackageItem package)
                return string.Empty;

            var parts = new System.Collections.Generic.List<string>();

            if (package.MorphCount > 0)
                parts.Add($"Morphs: {package.MorphCount}");
            if (package.HairCount > 0)
                parts.Add($"Hair: {package.HairCount}");
            if (package.ClothingCount > 0)
                parts.Add($"Clothing: {package.ClothingCount}");
            if (package.SceneCount > 0)
                parts.Add($"Scenes: {package.SceneCount}");
            if (package.LooksCount > 0)
                parts.Add($"Looks: {package.LooksCount}");
            if (package.PosesCount > 0)
                parts.Add($"Poses: {package.PosesCount}");
            if (package.AssetsCount > 0)
                parts.Add($"Assets: {package.AssetsCount}");
            if (package.ScriptsCount > 0)
                parts.Add($"Scripts: {package.ScriptsCount}");
            if (package.PluginsCount > 0)
                parts.Add($"Plugins: {package.PluginsCount}");
            if (package.SubScenesCount > 0)
                parts.Add($"SubScenes: {package.SubScenesCount}");
            if (package.SkinsCount > 0)
                parts.Add($"Skins: {package.SkinsCount}");

            return string.Join("   ", parts);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a collection of ImagePreviewItems to Visibility based on extracted items
    /// </summary>
    public class HasExtractedItemsConverter : IValueConverter, IMultiValueConverter
    {
        public static readonly HasExtractedItemsConverter Instance = new HasExtractedItemsConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is IEnumerable collection)
            {
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

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length > 1 && values[1] is bool showLoadButton && !showLoadButton)
            {
                return Visibility.Collapsed;
            }

            if (values.Length > 0 && values[0] is IEnumerable collection)
            {
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

    /// <summary>
    /// Converts a collection count to visibility - shows only if count equals 1
    /// </summary>
    public class SingleSelectionVisibilityConverter : IValueConverter
    {
        public static readonly SingleSelectionVisibilityConverter Instance = new SingleSelectionVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ICollection collection)
            {
                return collection.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
