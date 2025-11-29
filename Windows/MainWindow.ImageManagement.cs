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
using System.Windows.Threading;
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

        private VPM.Services.VirtualizedImageGridManager _virtualizedImageGridManager;

        private void LazyLoadImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is VPM.Windows.LazyLoadImage lazyImage)
            {
                if (_virtualizedImageGridManager == null)
                {
                    // Try to find the ScrollViewer from the ImageListView
                    var scrollViewer = FindVisualChild<ScrollViewer>(ImagesListView);
                    if (scrollViewer != null)
                    {
                        _virtualizedImageGridManager = new VPM.Services.VirtualizedImageGridManager(scrollViewer);
                    }
                }

                _virtualizedImageGridManager?.RegisterImage(lazyImage);
                
                // Trigger initial load check
                _virtualizedImageGridManager?.ProcessImagesAsync();
            }
        }

        private void LazyLoadImage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is VPM.Windows.LazyLoadImage lazyImage)
            {
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

                PreviewImages.Clear();
                
                // Clear the virtualized manager if it exists
                _virtualizedImageGridManager?.Clear();
                
                if (selectedPackages == null || selectedPackages.Count == 0)
                    return;

                // Capture necessary data for background processing
                var gameFolder = _settingsManager?.Settings?.SelectedFolder;
                
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
                            // Create brush on UI thread or use a frozen one
                            // Since we are on a background thread, we must create and freeze it carefully
                            // However, SolidColorBrush constructor might require STA if not careful with colors
                            // Safer to just use the color property and let the UI bind to it, or create it on UI thread
                            
                            // Option 1: Create on UI thread (slows down loop)
                            // Option 2: Use pre-frozen brushes (better)
                            // Option 3: Just pass the color and let the item create the brush (best for MVVM)
                            
                            // For now, let's try to create it safely. 
                            // System.Windows.Media.Color is a struct, so it's safe.
                            // SolidColorBrush is a DispatcherObject.
                            
                            // FIX: Create the brush on the UI thread before the loop or inside Invoke
                            // But since we are in a background loop, let's just store the color in the item 
                            // and let the UI converter handle it, OR create a frozen brush.
                            
                            // Actually, the error "The calling thread must be STA" happens when creating UI objects.
                            // We can create a frozen brush on any thread IF we don't access DependencyProperties that require thread affinity.
                            // But SolidColorBrush constructor IS safe on background threads if frozen immediately? 
                            // Apparently not always in WPF.
                            
                            // Let's move the brush creation to the UI thread dispatch.
                            // Or better, since all items for a package share the same status color, 
                            // we can just pass the color to the item and let the item create the brush?
                            // No, ImagePreviewItem expects a Brush.
                            
                            // Let's create a thread-safe way to get the brush.
                            // We can't easily do it here without Invoke.
                            
                            // Workaround: Create a frozen brush using Dispatcher
                            SolidColorBrush statusBrush = null;
                            await Dispatcher.InvokeAsync(() => 
                            {
                                statusBrush = new SolidColorBrush(package.StatusColor);
                                statusBrush.Freeze();
                            });

                            foreach (var location in locations)
                            {
                                if (cancellationToken.IsCancellationRequested) 
                                    break;

                                totalImagesFound++;

                                // Check file existence in background
                                bool isExtracted = false;
                                if (!string.IsNullOrEmpty(gameFolder))
                                {
                                    try
                                    {
                                        var targetPath = System.IO.Path.Combine(gameFolder, location.InternalPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                                        isExtracted = System.IO.File.Exists(targetPath);
                                    }
                                    catch { }
                                }

                                // Create item with callback instead of loading immediately
                                var item = new ImagePreviewItem
                                {
                                    Image = null, // Will be loaded lazily
                                    PackageName = package.Name,
                                    InternalPath = location.InternalPath,
                                    StatusBrush = statusBrush,
                                    PackageItem = package,
                                    IsExtracted = isExtracted,
                                    ImageWidth = location.Width,
                                    ImageHeight = location.Height,
                                    LoadImageCallback = async () => 
                                    {
                                        var img = await _imageManager.LoadImageAsync(location.VarFilePath, location.InternalPath, 0, 0);
                                        return img;
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
                            }
                        }, DispatcherPriority.Background);
                    }
                    

                }, cancellationToken);
                
                // Trigger initial load for visible images
                if (_virtualizedImageGridManager != null)
                {
                    await _virtualizedImageGridManager.LoadInitialVisibleImagesAsync();
                }
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
                    // Extract
                    await VPM.Services.VarContentExtractor.ExtractRelatedFilesAsync(metadata.FilePath, imageItem.InternalPath, gameFolder);
                    imageItem.IsExtracted = true;
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
                await VPM.Services.VarContentExtractor.RemoveRelatedFilesAsync(metadata.FilePath, imageItem.InternalPath, gameFolder);
                
                imageItem.IsExtracted = false;
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

        private void OpenExtractedFilesInExplorer(string internalPath)
        {
            try
            {
                if (_settingsManager == null || string.IsNullOrEmpty(_settingsManager.Settings.SelectedFolder))
                    return;

                var targetPath = System.IO.Path.Combine(_settingsManager.Settings.SelectedFolder, internalPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                
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
                            
                            // Clear image preview grid before processing
                            PreviewImages.Clear();
                            
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
                                }
                            }
                            else if (packageItem.Status == "Available")
                            {
                                // Load the package
                                var (success, error) = await _packageFileManager.LoadPackageAsync(packageItem.Name);
                                if (success)
                                {
                                    packageItem.Status = "Loaded";
                                }
                            }
                            
                            // Refresh images to show updated status
                            await RefreshCurrentlyDisplayedImagesAsync();
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
