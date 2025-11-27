using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VPM.Models;

namespace VPM
{
    /// <summary>
    /// Image management functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        // Collection for binding to ImageListView
        public ObservableCollection<ImagePreviewItem> PreviewImages { get; set; } = new ObservableCollection<ImagePreviewItem>();

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
        
        // Package metadata cache for performance
        private readonly Dictionary<string, VarMetadata> _packageMetadataCache = new Dictionary<string, VarMetadata>();
        private readonly object _metadataCacheLock = new object();

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

                VarMetadata packageMetadata = null;

                // Direct dictionary lookup (covers archived packages with #archived suffix)
                if (_packageManager.PackageMetadata.TryGetValue(packageName, out var directMetadata))
                {
                    packageMetadata = directMetadata;
                }
                else
                {
                    // Handle archived packages by stripping suffix for fallback comparisons
                    var normalizedPackageName = packageName.EndsWith("#archived", StringComparison.OrdinalIgnoreCase)
                        ? packageName[..^9]
                        : packageName;

                    packageMetadata = _packageManager.PackageMetadata.Values
                        .FirstOrDefault(p =>
                            System.IO.Path.GetFileNameWithoutExtension(p.Filename).Equals(packageName, StringComparison.OrdinalIgnoreCase) ||
                            System.IO.Path.GetFileNameWithoutExtension(p.Filename).Equals(normalizedPackageName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.PackageName, packageName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.PackageName, normalizedPackageName, StringComparison.OrdinalIgnoreCase));
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
            var listView = sender as ListView;
            if (listView != null)
            {
                // Find the ScrollViewer inside the ListView
                var scrollViewer = FindVisualChild<ScrollViewer>(listView);
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

        private async Task DisplayPackageImagesAsync(PackageItem packageItem, System.Threading.CancellationToken cancellationToken = default)
        {
            await DisplayMultiplePackageImagesAsync(new List<PackageItem> { packageItem }, null, cancellationToken);
        }

        private async Task DisplayMultiplePackageImagesAsync(List<PackageItem> selectedPackages, List<bool> packageSources = null, System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                PreviewImages.Clear();
                
                if (selectedPackages == null || selectedPackages.Count == 0)
                    return;

                // Load images for each package
                foreach (var package in selectedPackages)
                {
                    if (cancellationToken.IsCancellationRequested) 
                        break;

                    var packageKey = !string.IsNullOrEmpty(package.MetadataKey) ? package.MetadataKey : package.Name;
                    var metadata = GetCachedPackageMetadata(packageKey);
                    
                    if (metadata == null || string.IsNullOrEmpty(metadata.FilePath)) 
                        continue;

                    // Get the package base name for image index lookup
                    var packageBase = System.IO.Path.GetFileNameWithoutExtension(metadata.Filename);
                    
                    // Check if images are indexed for this package
                    if (_imageManager.ImageIndex.TryGetValue(packageBase, out var locations))
                    {
                        // Load images from the indexed locations
                        foreach (var location in locations)
                        {
                            if (cancellationToken.IsCancellationRequested) 
                                break;

                            try
                            {
                                // Load the image from the VAR file
                                var image = await _imageManager.LoadImageAsync(location.VarFilePath, location.InternalPath, 0, 0);
                                
                                if (image != null)
                                {
                                    PreviewImages.Add(new ImagePreviewItem
                                    {
                                        Image = image,
                                        PackageName = package.Name,
                                        InternalPath = location.InternalPath,
                                        StatusBrush = new SolidColorBrush(package.StatusColor),
                                        PackageItem = package
                                    });
                                }
                            }
                            catch
                            {
                                // Skip individual image load failures
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
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
            // Stub implementation
        }

        private void UpdateDependencyStatus(string packageName, string newStatus)
        {
            // Stub implementation
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

        private void PackageHeaderLoadUnload_Click(object sender, RoutedEventArgs e)
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
                        if (packageItem != null && PackageDataGrid != null)
                        {
                            // Select the package in the PackageDataGrid
                            PackageDataGrid.SelectedItem = packageItem;
                            PackageDataGrid.ScrollIntoView(packageItem);

                            // Toggle between Load and Unload based on current status
                            if (packageItem.Status == "Loaded")
                            {
                                // Unload the package
                                UnloadPackages_Click(null, null);
                            }
                            else if (packageItem.Status == "Available")
                            {
                                // Load the package
                                LoadPackages_Click(null, null);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
