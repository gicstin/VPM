using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VPM.Models;
using VPM.Services;
using VPM.Windows;

namespace VPM
{
    /// <summary>
    /// Image management functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        // Collection for binding to ImageListView
        public ObservableCollection<ImagePreviewItem> PreviewImages { get; set; } = new ObservableCollection<ImagePreviewItem>();

        // Service for ImageListView integration
        private ImageListViewService _imageListViewService = new ImageListViewService();

        public static readonly DependencyProperty TileSizeProperty = DependencyProperty.Register(
            "TileSize", typeof(double), typeof(MainWindow), new PropertyMetadata(200.0));

        public double TileSize
        {
            get { return (double)GetValue(TileSizeProperty); }
            set { SetValue(TileSizeProperty, value); }
        }

        public static readonly DependencyProperty ImageColumnsProperty = DependencyProperty.Register(
            "ImageColumns", typeof(int), typeof(MainWindow), new PropertyMetadata(3));

        public int ImageColumns
        {
            get { return (int)GetValue(ImageColumnsProperty); }
            set 
            { 
                SetValue(ImageColumnsProperty, value); 
            }
        }

        public static readonly DependencyProperty ImageMatchWidthProperty = DependencyProperty.Register(
            "ImageMatchWidth", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        public bool ImageMatchWidth
        {
            get { return (bool)GetValue(ImageMatchWidthProperty); }
            set 
            { 
                SetValue(ImageMatchWidthProperty, value); 
            }
        }

        public static readonly DependencyProperty ImageShowOnlyExtractedProperty = DependencyProperty.Register(
            "ImageShowOnlyExtracted", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        public bool ImageShowOnlyExtracted
        {
            get { return (bool)GetValue(ImageShowOnlyExtractedProperty); }
            set 
            { 
                SetValue(ImageShowOnlyExtractedProperty, value); 
            }
        }
        
        // Package metadata cache for performance - limited size to prevent memory leak
        private readonly Dictionary<string, VarMetadata> _packageMetadataCache = new Dictionary<string, VarMetadata>();
        private readonly object _metadataCacheLock = new object();
        private const int MAX_METADATA_CACHE_SIZE = 500; // Limit cache to prevent unbounded growth

        /// <summary>
        /// Gets cached package metadata or performs lookup and caches result
        /// </summary>
        public VarMetadata GetCachedPackageMetadata(string packageName)
        {
            lock (_metadataCacheLock)
            {
                // Check cache first
                if (_packageMetadataCache.TryGetValue(packageName, out var cachedMetadata))
                {
                    return cachedMetadata;
                }
                
                // MEMORY FIX: Clear cache if it grows too large
                if (_packageMetadataCache.Count >= MAX_METADATA_CACHE_SIZE)
                {
                    _packageMetadataCache.Clear();
                }

                VarMetadata packageMetadata = null;

                // Direct dictionary lookup (covers archived packages with #archived suffix)
                if (_packageManager.PackageMetadata.TryGetValue(packageName, out var directMetadata))
                {
                    packageMetadata = directMetadata;
                }
                else
                {
                    // Handle .latest suffix for dependency packages
                    var normalizedPackageName = packageName;
                    if (packageName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedPackageName = packageName[..^7]; // Remove ".latest"
                    }
                    
                    // Handle archived packages by stripping suffix for fallback comparisons
                    if (normalizedPackageName.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedPackageName = normalizedPackageName[..^9];
                    }

                    // Try to find matching metadata
                    packageMetadata = _packageManager.PackageMetadata.Values
                        .FirstOrDefault(p =>
                            System.IO.Path.GetFileNameWithoutExtension(p.Filename).Equals(packageName, StringComparison.OrdinalIgnoreCase) ||
                            System.IO.Path.GetFileNameWithoutExtension(p.Filename).Equals(normalizedPackageName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.PackageName, packageName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.PackageName, normalizedPackageName, StringComparison.OrdinalIgnoreCase));
                    
                    // If still not found and packageName has .latest, try finding the latest version
                    if (packageMetadata == null && packageName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                    {
                        var baseName = packageName[..^7]; // Remove ".latest"
                        
                        // Find all versions of this package and get the latest
                        var matchingMetadata = _packageManager.PackageMetadata.Values
                            .Where(p => 
                                System.IO.Path.GetFileNameWithoutExtension(p.Filename).StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase) ||
                                p.PackageName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(p => p.Version)
                            .FirstOrDefault();
                        
                        packageMetadata = matchingMetadata;
                    }
                }

                // Cache the result (even if null to avoid repeated lookups)
                _packageMetadataCache[packageName] = packageMetadata;
                
                return packageMetadata;
            }
        }
        
        /// <summary>
        /// Clears the package metadata cache (call when package list changes)
        /// </summary>
        private void ClearPackageMetadataCache()
        {
            lock (_metadataCacheLock)
            {
                _packageMetadataCache.Clear();
            }
        }

        private void ImagesListView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Increase scroll sensitivity by multiplying the delta by 3
            var imageListView = sender as ImageListView;
            if (imageListView != null)
            {
                // Find the ScrollViewer inside the ImageListView
                var scrollViewer = FindVisualChild<ScrollViewer>(imageListView);
                if (scrollViewer != null)
                {
                    scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta * 3));
                    e.Handled = true;
                }
            }
        }

        private void ImagesListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Force the ImageColumns binding to update when size changes
            // This ensures images resize properly without resetting the column count
            if (e.WidthChanged)
            {
                // Trigger a binding update by temporarily setting a dummy value
                var currentColumns = ImageColumns;
                // The binding will recalculate image sizes based on new width
            }
        }

        /// <summary>
        /// Initializes the ImageListView control with service configuration
        /// </summary>
        public void InitializeImageListView()
        {
            if (ImagesListView != null)
            {
                _imageListViewService.ConfigureImageListView(ImagesListView);
            }
        }

        /// <summary>
        /// Gets statistics about the currently displayed images
        /// </summary>
        public ImageListViewStatistics GetCurrentImageStatistics()
        {
            return _imageListViewService.GetStatistics(PreviewImages);
        }

        /// <summary>
        /// Filters displayed images by extraction status
        /// </summary>
        public void FilterImagesByExtractionStatus(bool isExtracted)
        {
            var filtered = _imageListViewService.FilterByExtractionStatus(PreviewImages, isExtracted);
            PreviewImages.Clear();
            foreach (var item in filtered)
            {
                PreviewImages.Add(item);
            }
        }

        /// <summary>
        /// Sorts displayed images by package name
        /// </summary>
        public void SortImagesByPackageName()
        {
            var sorted = _imageListViewService.SortByPackageName(PreviewImages);
            PreviewImages.Clear();
            foreach (var item in sorted)
            {
                PreviewImages.Add(item);
            }
        }

        /// <summary>
        /// Sorts displayed images by internal path
        /// </summary>
        public void SortImagesByPath()
        {
            var sorted = _imageListViewService.SortByPath(PreviewImages);
            PreviewImages.Clear();
            foreach (var item in sorted)
            {
                PreviewImages.Add(item);
            }
        }

        /// <summary>
        /// Updates the image statistics display in the UI
        /// </summary>
        private void UpdateImageStatisticsDisplay()
        {
            try
            {
                var stats = GetCurrentImageStatistics();
                
                if (ImageStatsTextBlock != null)
                {
                    ImageStatsTextBlock.Text = stats.TotalItems == 1 
                        ? "1 image" 
                        : $"{stats.TotalItems} images";
                }
                
                if (ImageExtractedStatsTextBlock != null)
                {
                    ImageExtractedStatsTextBlock.Text = stats.ExtractedItems == 1
                        ? "1 extracted item"
                        : $"{stats.ExtractedItems} extracted items";
                }
                
                // Log loading metrics for performance monitoring
                LogLoadingMetrics();
            }
            catch (Exception)
            {
            }
        }
        
        /// <summary>
        /// Logs loading metrics for performance monitoring
        /// </summary>
        private void LogLoadingMetrics()
        {
            try
            {
                if (_virtualizedImageGridManager != null)
                {
                    var metrics = _virtualizedImageGridManager.GetMetrics();
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Toggles between showing only extracted images and showing all images
        /// </summary>
        private void ToggleExtractedFilter_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ImageShowOnlyExtracted = !ImageShowOnlyExtracted;
                
                // Clear the virtualized image manager to reset lazy loading for new filtered collection
                if (_virtualizedImageGridManager != null)
                {
                    _virtualizedImageGridManager.Clear();
                }
                
                if (ImageShowOnlyExtracted)
                {
                    // Show only extracted images
                    FilterImagesByExtractionStatus(true);
                }
                else
                {
                    // Show all images - reload for currently selected packages
                    var selectedPackages = PackageDataGrid?.SelectedItems?.Cast<PackageItem>()?.ToList();
                    if (selectedPackages != null && selectedPackages.Count > 0)
                    {
                        _ = DisplayMultiplePackageImagesAsync(selectedPackages);
                    }
                }
                
                UpdateImageStatisticsDisplay();
            }
            catch (Exception)
            {
            }
        }

        // Track selected content types for filtering
        private HashSet<string> _selectedContentTypes = new HashSet<string>();
        private List<ImagePreviewItem> _allPreviewImages = new List<ImagePreviewItem>();

        /// <summary>
        /// Opens the content type filter dropdown
        /// </summary>
        private void ContentTypeFilterButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Get all unique content types from current images
                var contentTypes = new HashSet<string>();
                foreach (var item in PreviewImages)
                {
                    var category = VPM.Services.VarContentExtractor.GetCategoryFromPath(item.InternalPath);
                    if (!string.IsNullOrEmpty(category))
                    {
                        contentTypes.Add(category);
                    }
                }

                // Populate checkboxes for each content type
                var checkboxesPanel = FindName("ContentTypeCheckboxes") as System.Windows.Controls.ItemsControl;
                if (checkboxesPanel != null)
                {
                    checkboxesPanel.Items.Clear();
                    foreach (var type in contentTypes.OrderBy(x => x))
                    {
                        var checkbox = new System.Windows.Controls.CheckBox
                        {
                            Content = type,
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White),
                            Margin = new System.Windows.Thickness(0, 6, 0, 6),
                            FontSize = 12,
                            IsChecked = _selectedContentTypes.Contains(type)
                        };

                        // Apply custom checkbox style
                        var style = new System.Windows.Style(typeof(System.Windows.Controls.CheckBox));
                        style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.CheckBox.TemplateProperty, CreateCustomCheckboxTemplate()));
                        checkbox.Style = style;

                        checkbox.Checked += ContentTypeCheckbox_Changed;
                        checkbox.Unchecked += ContentTypeCheckbox_Changed;
                        checkboxesPanel.Items.Add(checkbox);
                    }
                }

                // Update filter clear button visibility
                UpdateFilterClearButtonVisibility();

                // Show the popup
                var popup = FindName("ContentTypeFilterPopup") as System.Windows.Controls.Primitives.Popup;
                if (popup != null)
                {
                    popup.IsOpen = true;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Handles checkbox changes in the filter dropdown
        /// </summary>
        private void ContentTypeCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                var checkbox = sender as System.Windows.Controls.CheckBox;
                if (checkbox == null) return;

                var contentType = checkbox.Content as string;
                if (string.IsNullOrEmpty(contentType)) return;

                if (checkbox.IsChecked == true)
                {
                    _selectedContentTypes.Add(contentType);
                    
                    // Uncheck "Show All" when a content type is selected
                    var filterShowAll = FindName("FilterShowAll") as System.Windows.Controls.CheckBox;
                    if (filterShowAll != null && filterShowAll.IsChecked == true)
                    {
                        filterShowAll.IsChecked = false;
                    }
                }
                else
                {
                    _selectedContentTypes.Remove(contentType);
                }

                ApplyContentTypeFilter();
                UpdateFilterClearButtonVisibility();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Handles "Show All" checkbox
        /// </summary>
        private void FilterShowAll_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                _selectedContentTypes.Clear();
                
                // Uncheck all content type checkboxes
                var checkboxesPanel = FindName("ContentTypeCheckboxes") as System.Windows.Controls.ItemsControl;
                if (checkboxesPanel != null)
                {
                    foreach (var item in checkboxesPanel.Items)
                    {
                        if (item is System.Windows.Controls.CheckBox checkbox)
                        {
                            checkbox.IsChecked = false;
                        }
                    }
                }
                
                ApplyContentTypeFilter();
                UpdateFilterClearButtonVisibility();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Handles "Show All" checkbox unchecked
        /// </summary>
        private void FilterShowAll_Unchecked(object sender, RoutedEventArgs e)
        {
            // No action needed - user will select specific types
        }

        /// <summary>
        /// Clears all filters and selects "Show All"
        /// </summary>
        private void ClearFilters_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _selectedContentTypes.Clear();
                
                // Check "Show All"
                var filterShowAll = FindName("FilterShowAll") as System.Windows.Controls.CheckBox;
                if (filterShowAll != null)
                {
                    filterShowAll.IsChecked = true;
                }
                
                // Uncheck all content type checkboxes
                var checkboxesPanel = FindName("ContentTypeCheckboxes") as System.Windows.Controls.ItemsControl;
                if (checkboxesPanel != null)
                {
                    foreach (var item in checkboxesPanel.Items)
                    {
                        if (item is System.Windows.Controls.CheckBox checkbox)
                        {
                            checkbox.IsChecked = false;
                        }
                    }
                }
                
                ApplyContentTypeFilter();
                UpdateFilterClearButtonVisibility();
                
                // Close the popup
                var popup = FindName("ContentTypeFilterPopup") as System.Windows.Controls.Primitives.Popup;
                if (popup != null)
                {
                    popup.IsOpen = false;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Updates the visibility of the filter clear button
        /// </summary>
        private void UpdateFilterClearButtonVisibility()
        {
            try
            {
                var clearButton = FindName("ClearFiltersButton") as System.Windows.Controls.Button;
                if (clearButton != null)
                {
                    // Show button only if there are selected content types (not "Show All")
                    clearButton.Visibility = _selectedContentTypes.Count > 0 
                        ? System.Windows.Visibility.Visible 
                        : System.Windows.Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Creates a custom checkbox template with dark theme styling
        /// </summary>
        private System.Windows.Controls.ControlTemplate CreateCustomCheckboxTemplate()
        {
            var template = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.CheckBox));

            // Create the checkbox box
            var border = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            border.SetValue(System.Windows.Controls.Border.WidthProperty, 18.0);
            border.SetValue(System.Windows.Controls.Border.HeightProperty, 18.0);
            border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new System.Windows.CornerRadius(2));
            border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45)));
            border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 85, 85)));
            border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new System.Windows.Thickness(1));
            border.SetValue(System.Windows.Controls.Border.MarginProperty, new System.Windows.Thickness(0, 0, 8, 0));

            // Create the checkmark text - bind visibility to IsChecked
            var checkmark = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            checkmark.SetValue(System.Windows.Controls.TextBlock.TextProperty, "✓");
            checkmark.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 212, 255)));
            checkmark.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 12.0);
            checkmark.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, System.Windows.FontWeights.Bold);
            checkmark.SetValue(System.Windows.Controls.TextBlock.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            checkmark.SetValue(System.Windows.Controls.TextBlock.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            
            // Use binding instead of triggers to avoid VisualTree issues
            var visibilityBinding = new System.Windows.Data.Binding("IsChecked");
            visibilityBinding.RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent);
            visibilityBinding.Converter = new BooleanToVisibilityConverter();
            checkmark.SetBinding(System.Windows.Controls.TextBlock.VisibilityProperty, visibilityBinding);

            border.AppendChild(checkmark);

            // Create the content presenter
            var contentPresenter = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            contentPresenter.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);

            // Create the stack panel
            var stackPanel = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel));
            stackPanel.SetValue(System.Windows.Controls.StackPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
            stackPanel.AppendChild(border);
            stackPanel.AppendChild(contentPresenter);

            template.VisualTree = stackPanel;

            return template;
        }

        /// <summary>
        /// Simple converter to convert boolean to visibility
        /// </summary>
        private class BooleanToVisibilityConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is bool b)
                {
                    return b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                }
                return System.Windows.Visibility.Collapsed;
            }

            public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is System.Windows.Visibility v)
                {
                    return v == System.Windows.Visibility.Visible;
                }
                return false;
            }
        }

        /// <summary>
        /// Applies the content type filter to the preview images
        /// </summary>
        private void ApplyContentTypeFilter()
        {
            try
            {
                // Clear the virtualized image manager to reset lazy loading for new filtered collection
                if (_virtualizedImageGridManager != null)
                {
                    _virtualizedImageGridManager.Clear();
                }

                // If no types selected, show all
                if (_selectedContentTypes.Count == 0)
                {
                    PreviewImages.Clear();
                    foreach (var item in _allPreviewImages)
                    {
                        PreviewImages.Add(item);
                    }
                }
                else
                {
                    // Filter to only selected types
                    var filtered = new List<ImagePreviewItem>();
                    foreach (var item in _allPreviewImages)
                    {
                        var category = VPM.Services.VarContentExtractor.GetCategoryFromPath(item.InternalPath);
                        if (_selectedContentTypes.Contains(category))
                        {
                            filtered.Add(item);
                        }
                    }

                    PreviewImages.Clear();
                    foreach (var item in filtered)
                    {
                        PreviewImages.Add(item);
                    }
                }

                UpdateImageStatisticsDisplay();
            }
            catch (Exception)
            {
            }
        }

        private VPM.Services.VirtualizedImageGridManager _virtualizedImageGridManager;
        private List<VPM.Windows.LazyLoadImage> _pendingLazyImages = new List<VPM.Windows.LazyLoadImage>();
        private DispatcherTimer _batchRegistrationTimer;

        private void LazyLoadImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is VPM.Windows.LazyLoadImage lazyImage)
            {
                // Subscribe to extraction events from the internal buttons
                lazyImage.ExtractionRequested -= OnLazyImageExtractionRequested; // Prevent duplicate subscription
                lazyImage.ExtractionRequested += OnLazyImageExtractionRequested;
                
                // Subscribe to texture unloaded event to release memory
                lazyImage.TextureUnloaded -= OnLazyImageTextureUnloaded;
                lazyImage.TextureUnloaded += OnLazyImageTextureUnloaded;

                // Initialize button state based on current IsExtracted value
                // This ensures buttons show even if the property doesn't change
                lazyImage.SetExtractionState(lazyImage.IsExtracted);

                // Ensure manager is initialized
                if (_virtualizedImageGridManager == null)
                {
                    // Try to find the ScrollViewer from the ImageListView
                    var scrollViewer = FindVisualChild<ScrollViewer>(ImagesListView);
                    if (scrollViewer != null)
                    {
                        _virtualizedImageGridManager = new VPM.Services.VirtualizedImageGridManager(scrollViewer);
                    }
                    else
                    {
                        return; // Can't proceed without manager
                    }
                }

                // Collect images for batch registration instead of registering individually
                _pendingLazyImages.Add(lazyImage);
                
                // Debounce batch registration to collect all loaded images
                if (_batchRegistrationTimer == null)
                {
                    _batchRegistrationTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(150) // Increased to 150ms for better batching
                    };
                    
                    _batchRegistrationTimer.Tick += async (s, args) =>
                    {
                        _batchRegistrationTimer.Stop();
                        
                        if (_pendingLazyImages.Count > 0 && _virtualizedImageGridManager != null)
                        {
                            var imagesToRegister = new List<VPM.Windows.LazyLoadImage>(_pendingLazyImages);
                            _pendingLazyImages.Clear();
                            
                            // Batch register all pending images
                            await _virtualizedImageGridManager.BatchRegisterAsync(imagesToRegister);
                        }
                        else if (_virtualizedImageGridManager == null)
                        {
                        }
                    };
                }
                
                _batchRegistrationTimer.Stop();
                _batchRegistrationTimer.Start();
            }
        }

        private void OnLazyImageTextureUnloaded(object sender, System.Windows.Media.Imaging.BitmapImage bitmap)
        {
            if (bitmap != null && _imageManager != null)
            {
                _imageManager.DeregisterTextureUse(bitmap);
            }
        }

        private void LazyLoadImage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is VPM.Windows.LazyLoadImage lazyImage)
            {
                // Unsubscribe from events to prevent leaks
                lazyImage.ExtractionRequested -= OnLazyImageExtractionRequested;
                lazyImage.TextureUnloaded -= OnLazyImageTextureUnloaded;
                
                _virtualizedImageGridManager?.UnregisterImage(lazyImage);
            }
        }

        private async Task DisplayPackageImagesAsync(PackageItem packageItem, System.Threading.CancellationToken cancellationToken = default)
        {
            await DisplayMultiplePackageImagesAsync(new List<PackageItem> { packageItem }, null, cancellationToken);
        }

        private async Task DisplayMultiplePackageImagesAsync(List<PackageItem> selectedPackages, List<bool> packageSources = null, System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // MEMORY FIX: Clear ImageListViewService cache to prevent memory leak
                _imageListViewService.ClearCache();
                
                PreviewImages.Clear();
                _allPreviewImages.Clear();
                _selectedContentTypes.Clear();
                
                // Reset the extracted filter when displaying new packages
                ImageShowOnlyExtracted = false;
                
                // Stop and reset batch registration timer to prevent stale registrations
                if (_batchRegistrationTimer != null)
                {
                    _batchRegistrationTimer.Stop();
                }
                _pendingLazyImages.Clear();
                
                // Clear the virtualized manager if it exists
                _virtualizedImageGridManager?.Clear();
                
                // Clear statistics display
                UpdateImageStatisticsDisplay();
                
                if (selectedPackages == null || selectedPackages.Count == 0)
                    return;

                // Capture necessary data for background processing
                var gameFolder = _settingsManager?.Settings?.SelectedFolder;

                // Ensure selected packages are indexed for image display.
                // On startup, ImageIndex may be empty (or not yet populated for these packages),
                // but we still want previews to show without requiring a load/unload operation.
                try
                {
                    if (_imageManager != null && _packageManager != null)
                    {
                        var pathsToIndex = new List<string>();

                        foreach (var package in selectedPackages)
                        {
                            var packageKey = !string.IsNullOrEmpty(package.MetadataKey) ? package.MetadataKey : package.Name;
                            if (_packageManager.PackageMetadata.TryGetValue(packageKey, out var meta) &&
                                meta != null &&
                                !string.IsNullOrEmpty(meta.FilePath))
                            {
                                var packageBase = System.IO.Path.GetFileNameWithoutExtension(meta.Filename);
                                if (!_imageManager.ImageIndex.ContainsKey(packageBase) && System.IO.File.Exists(meta.FilePath))
                                {
                                    pathsToIndex.Add(meta.FilePath);
                                }
                            }
                        }

                        if (pathsToIndex.Count > 0)
                        {
                            await _imageManager.BuildImageIndexFromVarsAsync(pathsToIndex, forceRebuild: false);
                        }
                    }
                }
                catch
                {
                    // Non-critical: image indexing will fall back to showing no previews.
                }
                
                // Run heavy processing on background thread
                await Task.Run(async () => 
                {
                    var batch = new List<ImagePreviewItem>();
                    var batchSize = 50; // Update UI every 50 items
                    var totalImagesFound = 0;
                    
                    foreach (var package in selectedPackages)
                    {
                        if (cancellationToken.IsCancellationRequested) 
                            break;

                        var packageKey = !string.IsNullOrEmpty(package.MetadataKey) ? package.MetadataKey : package.Name;
                        var metadata = GetCachedPackageMetadata(packageKey);
                        
                        if (metadata == null || string.IsNullOrEmpty(metadata.FilePath))
                        {
                            continue;
                        }

                        var packageBase = System.IO.Path.GetFileNameWithoutExtension(metadata.Filename);
                        
                        if (_imageManager.ImageIndex.TryGetValue(packageBase, out var locations) && locations != null && locations.Count > 0)
                        {
                            
                            SolidColorBrush statusBrush = null;
                            await Dispatcher.InvokeAsync(() => 
                            {
                                statusBrush = new SolidColorBrush(package.StatusColor);
                                statusBrush.Freeze();
                            });

                            // OPTIMIZATION: Batch check extraction status for all images in this package
                            // This avoids opening the VAR archive repeatedly for each image
                            Dictionary<string, bool> extractionStatus = null;
                            if (!string.IsNullOrEmpty(gameFolder))
                            {
                                try
                                {
                                    var internalPaths = locations.Select(l => l.InternalPath).ToList();
                                    // Use the first location's VarFilePath (they should all be the same for one package)
                                    var varPath = locations[0].VarFilePath;
                                    extractionStatus = VPM.Services.VarContentExtractor.BatchCheckExtractionStatus(
                                        varPath, 
                                        internalPaths, 
                                        gameFolder);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[ExtCheck-Batch-ERROR] {ex.Message}");
                                }
                            }

                            // SORTING: Prioritize "Scene" type images to appear at the top
                            var sortedLocations = locations.OrderByDescending(l => 
                                string.Equals(VPM.Services.VarContentExtractor.GetCategoryFromPath(l.InternalPath), "Scene", StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            foreach (var location in sortedLocations)
                            {
                                if (cancellationToken.IsCancellationRequested) 
                                    break;

                                totalImagesFound++;

                                // Check file existence in background using batch result
                                bool isExtracted = false;
                                if (extractionStatus != null && extractionStatus.TryGetValue(location.InternalPath, out var status))
                                {
                                    isExtracted = status;
                                }
                                else if (!string.IsNullOrEmpty(gameFolder) && extractionStatus == null)
                                {
                                    // Fallback to individual check if batch failed (should rarely happen)
                                    try
                                    {
                                        isExtracted = VPM.Services.VarContentExtractor.AreRelatedFilesExtracted(
                                            location.VarFilePath, 
                                            location.InternalPath, 
                                            gameFolder);
                                    }
                                    catch
                                    {
                                        // Ignore errors
                                    }
                                }

                                // Calculate total size including sister files (e.g., Scene.jpg + Scene.vaj + Scene.vap + Scene.json + Scene.vam)
                                var totalItemSize = CalculateTotalItemSizeWithSisterFiles(location.VarFilePath, location.InternalPath);

                                // Create item with callback instead of loading immediately
                                var item = new ImagePreviewItem
                                {
                                    Image = null, // Will be loaded lazily
                                    PackageName = package.Name,
                                    InternalPath = location.InternalPath,
                                    VarFilePath = location.VarFilePath,
                                    StatusBrush = statusBrush,
                                    PackageItem = package,
                                    IsExtracted = isExtracted,
                                    ImageWidth = location.Width,
                                    ImageHeight = location.Height,
                                    ItemFileSize = totalItemSize, // Set total size including sister files
                                    GroupKey = package.Name, // Group by package name in Packages mode
                                    LoadImageCallback = async () => 
                                    {
                                        try
                                        {
                                            var img = await _imageManager.LoadImageAsync(location.VarFilePath, location.InternalPath, 0, 0);
                                            return img;
                                        }
                                        catch (Exception)
                                        {
                                            return null;
                                        }
                                    }
                                };
                                
                                batch.Add(item);

                                // If batch is full, dispatch to UI
                                if (batch.Count >= batchSize)
                                {
                                    var itemsToAdd = new List<ImagePreviewItem>(batch);
                                    batch.Clear();
                                    
                                    await Dispatcher.InvokeAsync(() => 
                                    {
                                        if (cancellationToken.IsCancellationRequested) return;
                                        foreach (var i in itemsToAdd)
                                        {
                                            PreviewImages.Add(i);
                                            _allPreviewImages.Add(i);
                                            // Cache items in the service for fast lookup
                                            _imageListViewService.CacheItem(i);
                                        }
                                    }, DispatcherPriority.Background);
                                }
                            }
                        }
                    }

                    // Add remaining items
                    if (batch.Count > 0)
                    {
                        await Dispatcher.InvokeAsync(() => 
                        {
                            if (cancellationToken.IsCancellationRequested) return;
                            foreach (var i in batch)
                            {
                                PreviewImages.Add(i);
                                _allPreviewImages.Add(i);
                                // Cache items in the service for fast lookup
                                _imageListViewService.CacheItem(i);
                            }
                        }, DispatcherPriority.Background);
                    }
                    

                }, cancellationToken);
                
                // Update statistics display
                UpdateImageStatisticsDisplay();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Refreshes the image preview grid for currently selected packages.
        /// Call this after package status changes (Load/Unload) to reload images.
        /// </summary>
        private async Task RefreshCurrentlyDisplayedImagesAsync()
        {
            try
            {
                // Get currently selected packages from the grid
                if (PackageDataGrid?.SelectedItems == null || PackageDataGrid.SelectedItems.Count == 0)
                {
                    PreviewImages.Clear();
                    return;
                }

                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
                
                // Clear the metadata cache to ensure fresh lookups
                ClearPackageMetadataCache();
                
                // Reload images for the currently selected packages
                await DisplayMultiplePackageImagesAsync(selectedPackages);
            }
            catch (Exception)
            {
            }
        }

        private bool IsContentExtracted(string internalPath)
        {
            try
            {
                if (_settingsManager == null || string.IsNullOrEmpty(_settingsManager.Settings.SelectedFolder))
                    return false;

                var targetPath = System.IO.Path.Combine(_settingsManager.Settings.SelectedFolder, internalPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                return System.IO.File.Exists(targetPath);
            }
            catch
            {
                return false;
            }
        }

        private async void OnLazyImageExtractionRequested(object sender, VPM.Windows.ExtractionRequestedEventArgs e)
        {
            try
            {
                if (!(sender is VPM.Windows.LazyLoadImage lazyImage)) return;
                
                var imageItem = lazyImage.DataContext as ImagePreviewItem;
                if (imageItem == null) return;

                var packageItem = imageItem.PackageItem;
                if (packageItem == null) return;
                
                var metadata = GetCachedPackageMetadata(!string.IsNullOrEmpty(packageItem.MetadataKey) ? packageItem.MetadataKey : packageItem.Name);
                if (metadata == null || string.IsNullOrEmpty(metadata.FilePath)) return;
                
                if (!System.IO.File.Exists(metadata.FilePath))
                {
                    MessageBox.Show($"Package file not found: {metadata.FilePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var gameFolder = _settingsManager.Settings.SelectedFolder;
                if (string.IsNullOrEmpty(gameFolder)) 
                {
                    MessageBox.Show("Game folder not set in settings.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (e.IsRemoval)
                {
                    // Use the extractor service to remove related files
                    await VPM.Services.VarContentExtractor.RemoveRelatedFilesAsync(metadata.FilePath, imageItem.InternalPath, gameFolder);
                    
                    imageItem.IsExtracted = false;
                    
                    // Update only the affected item's button state
                    UpdateImageItemButtonState(imageItem);
                    
                    // Update statistics display to show new extracted count
                    UpdateImageStatisticsDisplay();
                }
                else
                {
                    if (imageItem.IsExtracted)
                    {
                        // Open in explorer
                        OpenExtractedFilesInExplorer(imageItem.InternalPath);
                    }
                    else
                    {
                        // Extract and track parent items that were automatically extracted
                        var (extractedCount, extractedParentPaths) = await VPM.Services.VarContentExtractor.ExtractRelatedFilesWithParentsAsync(metadata.FilePath, imageItem.InternalPath, gameFolder);
                        
                        if (extractedCount > 0)
                        {
                            imageItem.IsExtracted = true;
                            
                            // Update only the affected item's button state
                            UpdateImageItemButtonState(imageItem);
                            
                            // Update parent items that were automatically extracted
                            if (extractedParentPaths.Count > 0)
                            {
                                UpdateExtractedParentItems(extractedParentPaths);
                            }
                            
                            // Update statistics display to show new extracted count
                            UpdateImageStatisticsDisplay();
                        }
                        else
                        {
                            MessageBox.Show($"Failed to extract files from {Path.GetFileName(metadata.FilePath)}. The file may be corrupted or invalid.", "Extraction Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during content operation: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExtractContent_Click(object sender, RoutedEventArgs e)
        {
            Button button = null;
            try
            {
                button = sender as Button;
                if (button == null) return;
                
                var imageItem = button.DataContext as ImagePreviewItem;
                if (imageItem == null) return;
                
                // Prevent double clicks
                button.IsEnabled = false;
                
                var packageItem = imageItem.PackageItem;
                if (packageItem == null) return;
                
                var metadata = GetCachedPackageMetadata(!string.IsNullOrEmpty(packageItem.MetadataKey) ? packageItem.MetadataKey : packageItem.Name);
                if (metadata == null || string.IsNullOrEmpty(metadata.FilePath)) return;
                
                if (!System.IO.File.Exists(metadata.FilePath))
                {
                    MessageBox.Show($"Package file not found: {metadata.FilePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var gameFolder = _settingsManager.Settings.SelectedFolder;
                if (string.IsNullOrEmpty(gameFolder)) 
                {
                    MessageBox.Show("Game folder not set in settings.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (imageItem.IsExtracted)
                {
                    // Open in explorer
                    OpenExtractedFilesInExplorer(imageItem.InternalPath);
                }
                else
                {
                    // Extract and track parent items that were automatically extracted
                    var (extractedCount, extractedParentPaths) = await VPM.Services.VarContentExtractor.ExtractRelatedFilesWithParentsAsync(metadata.FilePath, imageItem.InternalPath, gameFolder);
                    
                    if (extractedCount > 0)
                    {
                        imageItem.IsExtracted = true;
                        
                        // Update only the affected item's button state
                        UpdateImageItemButtonState(imageItem);
                        
                        // Update parent items that were automatically extracted
                        if (extractedParentPaths.Count > 0)
                        {
                            UpdateExtractedParentItems(extractedParentPaths);
                        }
                        
                        // Update statistics display to show new extracted count
                        UpdateImageStatisticsDisplay();
                    }
                    else
                    {
                        MessageBox.Show($"Failed to extract files from {Path.GetFileName(metadata.FilePath)}. The file may be corrupted or invalid.", "Extraction Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during extraction: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }

        private async void DeleteExtractedContent_Click(object sender, RoutedEventArgs e)
        {
            Button button = null;
            try
            {
                button = sender as Button;
                if (button == null) return;
                
                var imageItem = button.DataContext as ImagePreviewItem;
                if (imageItem == null) return;
                
                // Prevent double clicks
                button.IsEnabled = false;
                
                var packageItem = imageItem.PackageItem;
                if (packageItem == null) return;

                var metadata = GetCachedPackageMetadata(!string.IsNullOrEmpty(packageItem.MetadataKey) ? packageItem.MetadataKey : packageItem.Name);
                if (metadata == null || string.IsNullOrEmpty(metadata.FilePath)) return;

                if (!System.IO.File.Exists(metadata.FilePath))
                {
                    MessageBox.Show($"Package file not found: {metadata.FilePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var gameFolder = _settingsManager.Settings.SelectedFolder;
                if (string.IsNullOrEmpty(gameFolder)) 
                    return;
                
                // Use the extractor service to remove related files
                int removedCount = await VPM.Services.VarContentExtractor.RemoveRelatedFilesAsync(metadata.FilePath, imageItem.InternalPath, gameFolder);
                
                // Only update UI if files were actually removed
                if (removedCount > 0)
                {
                    imageItem.IsExtracted = false;
                    
                    // Update only the affected item's button state
                    UpdateImageItemButtonState(imageItem);
                    
                    // Update statistics display to show new extracted count
                    UpdateImageStatisticsDisplay();
                }
                else
                {
                    MessageBox.Show($"No files were deleted. The files may not exist or there was an error during deletion.", "Deletion Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error deleting files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }

        private async void DeletePackageExtractedContent_Click(object sender, RoutedEventArgs e)
        {
            Button button = null;
            try
            {
                button = sender as Button;
                if (button == null) return;

                // Get the GroupItem from the button's DataContext
                var groupItem = button.DataContext as System.Windows.Data.CollectionViewGroup;
                if (groupItem == null) return;

                // Prevent double clicks
                button.IsEnabled = false;

                var gameFolder = _settingsManager.Settings.SelectedFolder;
                if (string.IsNullOrEmpty(gameFolder))
                    return;

                // Get all extracted items in this package group
                var extractedItems = groupItem.Items
                    .OfType<ImagePreviewItem>()
                    .Where(item => item.IsExtracted)
                    .ToList();

                if (extractedItems.Count == 0)
                    return;

                // Get package info from first item
                var firstItem = extractedItems.First();
                var packageItem = firstItem.PackageItem;
                if (packageItem == null) return;

                var metadata = GetCachedPackageMetadata(!string.IsNullOrEmpty(packageItem.MetadataKey) ? packageItem.MetadataKey : packageItem.Name);
                if (metadata == null || string.IsNullOrEmpty(metadata.FilePath)) return;

                if (!System.IO.File.Exists(metadata.FilePath))
                {
                    MessageBox.Show($"Package file not found: {metadata.FilePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Delete all extracted items in this package
                foreach (var item in extractedItems)
                {
                    try
                    {
                        await VPM.Services.VarContentExtractor.RemoveRelatedFilesAsync(metadata.FilePath, item.InternalPath, gameFolder);
                        item.IsExtracted = false;
                        
                        // Update only the affected item's button state
                        UpdateImageItemButtonState(item);
                    }
                    catch (Exception)
                    {
                    }
                }

                // Update statistics display to show new extracted count
                UpdateImageStatisticsDisplay();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error deleting extracted items: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }

        private async void GlobalClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (PreviewImages.Count == 0) return;

            try
            {
                var gameFolder = _settingsManager.Settings.SelectedFolder;
                if (string.IsNullOrEmpty(gameFolder)) return;

                // Create a copy of items to iterate
                var items = PreviewImages.ToList();
                
                // Clear UI immediately (Requirement 5)
                PreviewImages.Clear();
                if (_imageManager != null)
                {
                    _imageManager.Clear();
                }

                await Task.Run(async () =>
                {
                    foreach (var item in items)
                    {
                        try
                        {
                            var packageItem = item.PackageItem;
                            if (packageItem == null) continue;

                            var metadata = GetCachedPackageMetadata(!string.IsNullOrEmpty(packageItem.MetadataKey) ? packageItem.MetadataKey : packageItem.Name);
                            if (metadata == null || string.IsNullOrEmpty(metadata.FilePath)) continue;

                            if (!System.IO.File.Exists(metadata.FilePath)) continue;

                            // Trigger removal logic (Requirement 3)
                            await VPM.Services.VarContentExtractor.RemoveRelatedFilesAsync(metadata.FilePath, item.InternalPath, gameFolder);
                        }
                        catch
                        {
                            // Ignore individual errors
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error clearing items: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenExtractedFilesInExplorer(string internalPath)
        {
            try
            {
                if (_settingsManager == null || string.IsNullOrEmpty(_settingsManager.Settings.SelectedFolder))
                    return;

                var cleanPath = internalPath
                    .TrimStart('/', '\\')
                    .Replace('/', System.IO.Path.DirectorySeparatorChar)
                    .Replace('\\', System.IO.Path.DirectorySeparatorChar);

                var targetPath = System.IO.Path.Combine(_settingsManager.Settings.SelectedFolder, cleanPath);
                
                if (System.IO.File.Exists(targetPath))
                {
                    // Select the file in Explorer
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
                }
                else
                {
                    // If file doesn't exist, try opening the folder
                    var directory = System.IO.Path.GetDirectoryName(targetPath);
                    if (System.IO.Directory.Exists(directory))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", directory);
                    }
                }
            }
            catch
            {
                // Ignore errors opening explorer
            }
        }

        private void UpdatePackageStatusInImageGrid(string packageName, string newStatus, Color newStatusColor)
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    foreach (var item in PreviewImages)
                    {
                        if (item.PackageName == packageName)
                        {
                            item.StatusBrush = new SolidColorBrush(newStatusColor);
                        }
                    }
                });
            }
            catch (Exception)
            {
            }
        }

        private void UpdateMultiplePackageStatusInImageGrid(IEnumerable<(string packageName, string status, Color statusColor)> updates)
        {
            try
            {
                var updateList = updates.ToList();
                if (updateList.Count == 0) return;

                Dispatcher.InvokeAsync(() =>
                {
                    foreach (var (packageName, status, statusColor) in updateList)
                    {
                        UpdatePackageStatusInImageGrid(packageName, status, statusColor);
                    }
                });
            }
            catch (Exception)
            {
            }
        }

        private async Task LoadSinglePackageAsync(PackageItem packageItem, Button loadButton, Button unloadButton)
        {
            // Stub implementation
            await Task.CompletedTask;
        }

        private async Task UnloadSinglePackageAsync(PackageItem packageItem, Button loadButton, Button unloadButton)
        {
            // Stub implementation
            await Task.CompletedTask;
        }

        private void UpdateDependenciesStatus()
        {
            // Refresh the status of all dependencies in the current view
            // This is called after downloads complete to ensure the UI reflects the current state
            if (Dependencies == null || Dependencies.Count == 0)
                return;
            
            // Force refresh package status index to get latest file system state
            // This is critical after downloads to detect newly added files
            _packageFileManager?.RefreshPackageStatusIndex(force: true);
            
            foreach (var dep in Dependencies)
            {
                // Skip placeholder items
                if (dep.Status == "N/A" || dep.Name == "No dependencies" || dep.Name == "No dependents")
                    continue;
                
                // Skip if already marked as Loaded (e.g., by download completion handler)
                // This prevents overwriting the correct status with a stale lookup
                if (dep.Status == "Loaded")
                    continue;
                
                // Get the current status from the package file manager
                var newStatus = _packageFileManager?.GetPackageStatus(dep.Name) ?? "Unknown";
                
                // Only update if the new status is better (Loaded > Available > Missing)
                // This ensures we don't downgrade a status that was correctly set
                if (dep.Status != newStatus && ShouldUpdateDependencyStatus(dep.Status, newStatus))
                {
                    dep.Status = newStatus;
                    
                    // Also update in _originalDependencies to keep in sync
                    var origDep = _originalDependencies.FirstOrDefault(d => 
                        d.Name.Equals(dep.Name, StringComparison.OrdinalIgnoreCase) &&
                        d.Version == dep.Version);
                    if (origDep != null)
                    {
                        origDep.Status = newStatus;
                    }
                }
            }
            
            // Update toolbar buttons to reflect new missing count
            UpdateToolbarButtons();
        }
        
        /// <summary>
        /// Determines if a dependency status should be updated based on priority
        /// Loaded > Available > Downloading > Missing/Unknown
        /// </summary>
        private bool ShouldUpdateDependencyStatus(string currentStatus, string newStatus)
        {
            // Status priority (higher = better)
            int GetPriority(string status) => status switch
            {
                "Loaded" => 4,
                "Available" => 3,
                "Downloading" => 2,
                "Missing" => 1,
                "Unknown" => 0,
                _ => 0
            };
            
            // Only update if new status is better or equal priority
            return GetPriority(newStatus) >= GetPriority(currentStatus);
        }

        private void UpdateDependencyStatus(string packageName, string newStatus)
        {
            if (string.IsNullOrEmpty(packageName) || Dependencies == null)
                return;
            
            // Find and update the dependency with matching name
            foreach (var dep in Dependencies)
            {
                if (dep.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase) ||
                    dep.DisplayName.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                {
                    dep.Status = newStatus;
                    
                    // Also update in _originalDependencies to keep in sync
                    var origDep = _originalDependencies.FirstOrDefault(d => 
                        d.Name.Equals(dep.Name, StringComparison.OrdinalIgnoreCase) &&
                        d.Version == dep.Version);
                    if (origDep != null)
                    {
                        origDep.Status = newStatus;
                    }
                }
            }
        }

        private async Task OpenImageInViewer(string packageNameOrMetadataKey, System.Windows.Media.Imaging.BitmapSource imageSource)
        {
            // Stub implementation
            await Task.CompletedTask;
        }

        private async void RefreshImageDisplay()
        {
            // Stub implementation
            await Task.CompletedTask;
        }

        private void IncreaseImageColumns_Click(object sender, RoutedEventArgs e)
        {
            if (ImageColumns < 6)
            {
                ImageColumns++;
                SaveImageColumnsSetting();
            }
        }

        private void DecreaseImageColumns_Click(object sender, RoutedEventArgs e)
        {
            if (ImageColumns > 1)
            {
                ImageColumns--;
                SaveImageColumnsSetting();
            }
        }

        private void SaveImageColumnsSetting()
        {
            try
            {
                if (_settingsManager != null)
                {
                    _settingsManager.Settings.ImageColumns = ImageColumns;
                    _settingsManager.SaveSettingsImmediate();
                }
            }
            catch (Exception)
            {
            }
        }

        private void ToggleMatchWidth_Click(object sender, RoutedEventArgs e)
        {
            ImageMatchWidth = !ImageMatchWidth;
            SaveImageMatchWidthSetting();
        }

        private void SaveImageMatchWidthSetting()
        {
            try
            {
                if (_settingsManager != null)
                {
                    _settingsManager.Settings.ImageMatchWidth = ImageMatchWidth;
                    _settingsManager.SaveSettingsImmediate();
                }
            }
            catch (Exception)
            {
            }
        }

        public async Task CancelImageLoading()
        {
            try
            {
                PreviewImages.Clear();
                await Task.CompletedTask;
            }
            catch (Exception)
            {
            }
        }

        private async void PackageHeaderLoadUnload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Get the button that was clicked
                var button = sender as Button;
                if (button == null) return;

                // Find the parent GroupItem to get the package information
                var parent = VisualTreeHelper.GetParent(button);
                while (parent != null && !(parent is GroupItem))
                {
                    parent = VisualTreeHelper.GetParent(parent);
                }

                if (parent is GroupItem groupItem && groupItem.Content is CollectionViewGroup group)
                {
                    // Get the first image item from the group to access package information
                    if (group.Items.Count > 0 && group.Items[0] is ImagePreviewItem imageItem)
                    {
                        var packageItem = imageItem.PackageItem;
                        if (packageItem != null && _packageFileManager != null)
                        {
                            // Cancel any pending image loading operations to free up file handles
                            _imageLoadingCts?.Cancel();
                            _imageLoadingCts = new System.Threading.CancellationTokenSource();
                            
                            // Release file locks before operation to prevent conflicts with image grid
                            await _imageManager.ReleasePackagesAsync(new List<string> { packageItem.Name });
                            
                            // Perform load/unload directly without changing DataGrid selection
                            // This preserves the current selection and only updates the status
                            if (packageItem.Status == "Loaded")
                            {
                                // Unload the package
                                var (success, error) = await _packageFileManager.UnloadPackageAsync(packageItem.Name);
                                if (success)
                                {
                                    packageItem.Status = "Available";
                                    // Update status color for all images in this package group without redrawing grid
                                    UpdatePackageStatusColorInGrid(group, packageItem);
                                }
                            }
                            else if (packageItem.Status == "Available")
                            {
                                // Load the package
                                var (success, error) = await _packageFileManager.LoadPackageAsync(packageItem.Name);
                                if (success)
                                {
                                    packageItem.Status = "Loaded";
                                    // Update status color for all images in this package group without redrawing grid
                                    UpdatePackageStatusColorInGrid(group, packageItem);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Updates the status color for all images in a package group without redrawing the entire grid
        /// </summary>
        private void UpdatePackageStatusColorInGrid(CollectionViewGroup group, PackageItem packageItem)
        {
            try
            {
                // Update status brush for all items in the group
                var newStatusBrush = new SolidColorBrush(packageItem.StatusColor);
                newStatusBrush.Freeze();

                foreach (var item in group.Items)
                {
                    if (item is ImagePreviewItem imageItem)
                    {
                        imageItem.StatusBrush = newStatusBrush;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Updates only the button state for a specific image item without refreshing the entire grid
        /// </summary>
        private void UpdateImageItemButtonState(ImagePreviewItem imageItem)
        {
            try
            {
                // Find the ImageListView control
                var imageListView = this.FindName("ImagesListView") as ImageListView;
                if (imageListView == null) return;

                // Find the LazyLoadImage control for this item
                var lazyLoadImages = FindAllVisualChildren<LazyLoadImage>(imageListView);
                foreach (var lazyImage in lazyLoadImages)
                {
                    // Match by internal path since that's unique per image
                    if (lazyImage.InternalImagePath == imageItem.InternalPath)
                    {
                        // Update the extraction state which updates the button UI
                        lazyImage.SetExtractionState(imageItem.IsExtracted);
                        return;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Updates extracted parent items in the preview grid when they're automatically extracted as dependencies
        /// </summary>
        private void UpdateExtractedParentItems(List<string> extractedParentPaths)
        {
            try
            {
                if (extractedParentPaths == null || extractedParentPaths.Count == 0)
                    return;

                // Update all matching items in both PreviewImages and _allPreviewImages collections
                var itemsToUpdateUI = new List<ImagePreviewItem>();
                
                foreach (var parentPath in extractedParentPaths)
                {
                    var normalizedParentPath = parentPath.Replace('\\', '/').ToLower();
                    
                    // Update in PreviewImages (currently displayed)
                    var matchingItems = PreviewImages
                        .Where(item => item.InternalPath.Replace('\\', '/').ToLower() == normalizedParentPath)
                        .ToList();
                    
                    foreach (var item in matchingItems)
                    {
                        item.IsExtracted = true;
                        itemsToUpdateUI.Add(item); // Track for UI update
                    }
                    
                    // Also update in _allPreviewImages (master list for filtering)
                    var allMatchingItems = _allPreviewImages
                        .Where(item => item.InternalPath.Replace('\\', '/').ToLower() == normalizedParentPath)
                        .ToList();
                    
                    foreach (var item in allMatchingItems)
                    {
                        item.IsExtracted = true;
                    }
                }

                // Update UI for items that are currently visible
                // Use dispatcher to ensure UI updates happen on the UI thread with a small delay
                Dispatcher.InvokeAsync(() =>
                {
                    foreach (var item in itemsToUpdateUI)
                    {
                        UpdateImageItemButtonState(item);
                    }

                    // Then, update the visual LazyLoadImage controls if they're loaded
                    var imageListView = this.FindName("ImagesListView") as ImageListView;
                    if (imageListView == null) return;

                    var lazyLoadImages = FindAllVisualChildren<LazyLoadImage>(imageListView);
                    if (lazyLoadImages.Count == 0) return;

                    // For each extracted parent path, find and update matching LazyLoadImage controls
                    foreach (var parentPath in extractedParentPaths)
                    {
                        var normalizedParentPath = parentPath.Replace('\\', '/').ToLower();

                        // Find all LazyLoadImage controls that match this parent image path
                        foreach (var lazyImage in lazyLoadImages)
                        {
                            if (string.IsNullOrEmpty(lazyImage.InternalImagePath)) continue;

                            var normalizedImagePath = lazyImage.InternalImagePath.Replace('\\', '/').ToLower();

                            // Match if paths are the same
                            if (normalizedImagePath == normalizedParentPath)
                            {
                                // Update the extraction state which updates the button UI
                                lazyImage.SetExtractionState(true);
                            }
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Normal);

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateExtractedParentItems-ERROR] {ex.Message}");
            }
        }

        /// <summary>
        /// Helper method to find a visual child by name
        /// </summary>
        private T FindVisualChild<T>(System.Windows.DependencyObject parent, string name = null) where T : System.Windows.DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    if (name == null || (child is System.Windows.FrameworkElement fe && fe.Name == name))
                    {
                        return typedChild;
                    }
                }

                var result = FindVisualChild<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

        /// <summary>
        /// Helper method to find all visual children of a specific type
        /// </summary>
        private List<T> FindAllVisualChildren<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
        {
            var children = new List<T>();
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    children.Add(typedChild);
                }

                children.AddRange(FindAllVisualChildren<T>(child));
            }
            return children;
        }

        /// <summary>
        /// Calculates the total size of an image item including all sister files with the same base name.
        /// For example, Scene.jpg includes Scene.vaj, Scene.vap, Scene.json, Scene.vam, etc.
        /// </summary>
        private long CalculateTotalItemSizeWithSisterFiles(string varFilePath, string internalImagePath)
        {
            try
            {
                if (string.IsNullOrEmpty(varFilePath) || string.IsNullOrEmpty(internalImagePath))
                    return 0;

                // CRITICAL FIX: Check if file exists before trying to open it
                // This prevents errors when packages have been moved between AllPackages and AddonPackages
                // but the ImageIndex hasn't been updated yet
                string actualFilePath = varFilePath;
                if (!System.IO.File.Exists(varFilePath))
                {
                    // Try to find the file at the alternative location
                    var fileName = System.IO.Path.GetFileName(varFilePath);
                    var gameFolder = _settingsManager?.Settings?.SelectedFolder;
                    
                    if (!string.IsNullOrEmpty(gameFolder))
                    {
                        // Check AddonPackages if original was in AllPackages
                        if (varFilePath.Contains("AllPackages", StringComparison.OrdinalIgnoreCase))
                        {
                            var addonPath = System.IO.Path.Combine(gameFolder, "AddonPackages", fileName);
                            if (System.IO.File.Exists(addonPath))
                            {
                                actualFilePath = addonPath;
                            }
                        }
                        // Check AllPackages if original was in AddonPackages
                        else if (varFilePath.Contains("AddonPackages", StringComparison.OrdinalIgnoreCase))
                        {
                            var allPath = System.IO.Path.Combine(gameFolder, "AllPackages", fileName);
                            if (System.IO.File.Exists(allPath))
                            {
                                actualFilePath = allPath;
                            }
                        }
                    }
                    
                    // If still not found, return 0 silently (file may have been deleted or moved elsewhere)
                    if (!System.IO.File.Exists(actualFilePath))
                    {
                        return 0;
                    }
                }

                // Get the base name without extension (e.g., "Scene" from "Scene.jpg")
                var baseName = System.IO.Path.GetFileNameWithoutExtension(internalImagePath);
                var directoryPath = System.IO.Path.GetDirectoryName(internalImagePath);

                // Normalize paths to use forward slashes for consistency
                directoryPath = directoryPath?.Replace('\\', '/') ?? "";

                long totalSize = 0;

                try
                {
                    using var archive = VPM.Services.SharpCompressHelper.OpenForRead(actualFilePath);

                    // Find all files in the archive with the same base name in the same directory
                    var relatedEntries = archive.Entries
                        .Where(e => !e.Key.EndsWith("/"))
                        .Where(e =>
                        {
                            var entryBaseName = System.IO.Path.GetFileNameWithoutExtension(e.Key);
                            var entryDir = System.IO.Path.GetDirectoryName(e.Key)?.Replace('\\', '/') ?? "";

                            // Match if same base name and same directory
                            return entryBaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase) &&
                                   entryDir.Equals(directoryPath, StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();

                    // Sum up the sizes of all related files
                    foreach (var entry in relatedEntries)
                    {
                        totalSize += entry.Size;
                    }
                }
                catch (OperationCanceledException)
                {
                    // File is locked for writing (optimization in progress) - return 0 silently
                    return 0;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SisterFileSize-ERROR] Failed to calculate sister file sizes: {ex.Message}");
                    // Return 0 on error - the image location's FileSize will be used as fallback
                    return 0;
                }

                return totalSize;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SisterFileSize-ERROR] {ex.Message}");
                return 0;
            }
        }
    }
}
