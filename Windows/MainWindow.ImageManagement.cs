using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        // Image layout fields
        private const int LazyPreviewBatch = 20;
        private List<PackageItem> _lazyPackageQueue = new();
        private int _imageColumns = 3;
        private double _lastTileSize = -1;
        private DispatcherTimer _reflowTimer;
        
        // Package metadata cache for performance
        private readonly Dictionary<string, VarMetadata> _packageMetadataCache = new Dictionary<string, VarMetadata>();
        private readonly object _metadataCacheLock = new object();
        
        // Package status indicator tracking for real-time updates
        private readonly Dictionary<string, System.Windows.Shapes.Ellipse> _packageStatusIndicators = new Dictionary<string, System.Windows.Shapes.Ellipse>();
        private readonly object _statusIndicatorsLock = new object();

        // Package load/unload button tracking for real-time updates
        private readonly Dictionary<string, (Button loadButton, Button unloadButton)> _packageButtons = new Dictionary<string, (Button, Button)>();
        private readonly object _buttonsLock = new object();

        // Virtualized image grid manager for lazy loading images
        private VirtualizedImageGridManager _virtualizedImageManager;

        #region Image Display Methods

        private async Task DisplayPackageImagesAsync(PackageItem packageItem)
        {
            try
            {
                // Clear existing images and status indicator tracking
                ImagesPanel.Children.Clear();
                lock (_statusIndicatorsLock)
                {
                    _packageStatusIndicators.Clear();
                }
                lock (_buttonsLock)
                {
                    _packageButtons.Clear();
                }
                
                // Use the same method as multiple packages but with a single package list
                await DisplayMultiplePackageImagesAsync(new List<PackageItem> { packageItem });
            }
            catch (Exception)
            {
            }
        }

        private void ImagesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Increase scroll sensitivity by multiplying the delta by 3
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta * 3));
                e.Handled = true;
            }
        }

        private async Task DisplayMultiplePackageImagesAsync(List<PackageItem> selectedPackages, List<bool> packageSources = null)
        {
            try
            {
                // Memory pressure management (adjusted for full-resolution images)
                var memoryBefore = GC.GetTotalMemory(false);
                if (memoryBefore > 1_200_000_000) // 1.2GB threshold - higher for full-res images
                {
                    _imageManager.ClearBitmapCache();
                    GC.Collect();
                }
                else if (memoryBefore > 600_000_000) // 600MB threshold for partial cleanup
                {
                    _imageManager.PartialCacheCleanup();
                }
                
                // Initialize packageSources if not provided (all packages are considered packages)
                if (packageSources == null || packageSources.Count != selectedPackages.Count)
                {
                    packageSources = selectedPackages.Select(p => true).ToList(); // true = package
                }

                // Clear existing images and status indicator tracking
                ImagesPanel.Children.Clear();
                lock (_statusIndicatorsLock)
                {
                    _packageStatusIndicators.Clear();
                }
                lock (_buttonsLock)
                {
                    _packageButtons.Clear();
                }
                
                // Initialize virtualized image grid manager
                _virtualizedImageManager?.Dispose();
                _virtualizedImageManager = new VirtualizedImageGridManager(ImagesScrollViewer)
                {
                    LoadBufferSize = 300
                };
                
                var settings = _settingsManager?.Settings;
                
                // With virtualization enabled, skip expensive upfront image counting
                // Individual containers will return null if they have no images (fast fail)
                // This makes large selections instant regardless of package count
                
                // Load ALL available images (virtualization ensures only visible ones actually load)
                // Always display grouped by package
                await DisplayGroupedPackageImagesAsync(selectedPackages, packageSources);
                
            }
            catch (Exception)
            {
            }
        }

        private async Task DisplayGroupedPackageImagesAsync(List<PackageItem> selectedPackages, List<bool> packageSources)
        {
            var startTime = DateTime.Now;
            
            // Optimized batching: Smaller first batch for instant feedback, larger for remaining
            const int firstBatchSize = 3; // Show first few packages immediately
            const int batchSize = 8; // Larger batches for remaining packages
            var remainingContainers = new List<StackPanel>();
            var totalImagesLoaded = 0;
            var isFirstBatch = true;
            var firstBatchAdded = false;
            
            for (int i = 0; i < selectedPackages.Count; i += (isFirstBatch ? firstBatchSize : batchSize))
            {
                var currentBatchSize = isFirstBatch ? firstBatchSize : batchSize;
                var batch = selectedPackages.Skip(i).Take(currentBatchSize).ToList();
                
                // Process this batch in parallel - load ALL available images (virtualization handles lazy loading)
                var batchTasks = batch.Select((packageItem, index) => 
                    CreatePackageContainerAsync(packageItem, packageSources[i + index])
                ).ToArray();
                
                var batchContainers = await Task.WhenAll(batchTasks);
                var validBatchContainers = batchContainers.Where(c => c != null).ToList();
                
                totalImagesLoaded += validBatchContainers.Sum(c => CountImagesInContainer(c));
                
                // After first batch, immediately show it to user for instant feedback
                if (isFirstBatch && validBatchContainers.Count > 0)
                {
                    foreach (var container in validBatchContainers)
                    {
                        ImagesPanel.Children.Add(container);
                    }
                    firstBatchAdded = true;
                    
                    // Trigger initial visible image load for first batch
                    _ = Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        ImagesScrollViewer?.UpdateLayout();
                        ImagesPanel?.UpdateLayout();
                        await Task.Delay(50);
                        if (_virtualizedImageManager != null)
                        {
                            await _virtualizedImageManager.LoadInitialVisibleImagesAsync();
                        }
                    }), DispatcherPriority.Loaded);
                    
                    isFirstBatch = false;
                }
                else
                {
                    // Add to remaining containers for batch add at end
                    remainingContainers.AddRange(validBatchContainers);
                    isFirstBatch = false;
                }
            }
            
            // Batch UI update: Add remaining containers
            if (remainingContainers.Count > 0)
            {
                foreach (var container in remainingContainers)
                {
                    ImagesPanel.Children.Add(container);
                }
            }
            else if (!firstBatchAdded)
            {
                // No packages had images - don't show message to avoid interfering with image loading
                return; // Early exit
            }
            
            // Trigger initial load of visible images after UI layout
            _ = Dispatcher.BeginInvoke(new Action(async () =>
            {
                // Force layout update to ensure ActualHeight/Width are calculated
                ImagesScrollViewer?.UpdateLayout();
                ImagesPanel?.UpdateLayout();
                
                // Add small delay to ensure layout is complete
                await Task.Delay(100);
                
                // Now load visible images
                if (_virtualizedImageManager != null)
                {
                    await _virtualizedImageManager.LoadInitialVisibleImagesAsync();
                }
            }), DispatcherPriority.Loaded);
            
            var elapsed = DateTime.Now - startTime;
            var cacheStats = _imageManager.GetCacheStats();
        }

        /// <summary>
        /// Creates a package container with images (virtualization handles lazy loading)
        /// </summary>
        private async Task<StackPanel> CreatePackageContainerAsync(PackageItem packageItem, bool isDependency = false)
        {
            try
            {
                
                // Find the package metadata - use cached lookup for performance
                // Use MetadataKey for accurate lookup (handles multiple versions of same package)
                var packageMetadata = GetCachedPackageMetadata(!string.IsNullOrEmpty(packageItem.MetadataKey) ? packageItem.MetadataKey : packageItem.Name);
                
                if (packageMetadata == null)
                {
                    return null;
                }
                
                // Get cached images for this package
                var packageBase = Path.GetFileNameWithoutExtension(packageMetadata.Filename);
                
                // First get total available images to show in header
                var totalAvailableImages = _imageManager.GetCachedImageCount(packageBase);
                if (totalAvailableImages == 0)
                {
                    return null;
                }
                
                // Queue for preloading and load ALL available images (virtualization ensures only visible ones load)
                _imageManager.QueueForPreloading(packageBase);
                var images = await _imageManager.LoadImagesFromCacheAsync(packageBase, int.MaxValue);
                
                // Create package group container
                var packageGroupContainer = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(0, 0, 0, 20)
                };
                
                // Create package header with status - different styling for dependencies
                var packageHeader = new Border
                {
                    Background = isDependency 
                        ? new SolidColorBrush(Color.FromArgb(40, 149, 100, 237)) // Green background for dependencies
                        : new SolidColorBrush(Color.FromArgb(40, 100, 149, 237)), // Blue background for packages
                    CornerRadius = new CornerRadius(UI_CORNER_RADIUS),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                
                // Create header content with status indicator, load/unload buttons, and name
                var headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal
                };

                // Status indicator dot
                var statusIndicator = new System.Windows.Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = new SolidColorBrush(packageItem.StatusColor),
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(statusIndicator);

                // Create load/unload buttons panel (right of status indicator)
                var buttonPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(5, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = packageItem.Name // Store package name for reference
                };

                // Load button - visible only for available packages (not archived)
                var loadButton = new Button
                {
                    Content = "Load",
                    ToolTip = "Load this package",
                    Width = 45,
                    Height = 28,
                    FontSize = 10,
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = (packageItem.Status == "Available" && packageItem.Status != "Archived") ? Visibility.Visible : Visibility.Collapsed,
                    Tag = new Tuple<Button, Button, PackageItem>(null, null, packageItem) // Will be updated after buttons are created
                };
                loadButton.Click += async (s, e) =>
                {
                    var btn = s as Button;
                    var tuple = btn?.Tag as Tuple<Button, Button, PackageItem>;
                    if (tuple?.Item3 != null)
                    {
                        await LoadSinglePackageAsync(tuple.Item3, btn, tuple.Item2);
                    }
                };

                // Unload button - visible only for loaded packages (not archived)
                var unloadButton = new Button
                {
                    Content = "Unload",
                    ToolTip = "Unload this package",
                    Width = 55,
                    Height = 28,
                    FontSize = 10,
                    Padding = new Thickness(6, 4, 6, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = (packageItem.Status == "Loaded" && packageItem.Status != "Archived") ? Visibility.Visible : Visibility.Collapsed,
                    Tag = new Tuple<Button, Button, PackageItem>(null, null, packageItem) // Will be updated after buttons are created
                };
                unloadButton.Click += async (s, e) =>
                {
                    var btn = s as Button;
                    var tuple = btn?.Tag as Tuple<Button, Button, PackageItem>;
                    if (tuple?.Item3 != null)
                    {
                        await UnloadSinglePackageAsync(tuple.Item3, tuple.Item1, btn);
                    }
                };

                // Cross-reference the buttons in their tags for easier management
                loadButton.Tag = new Tuple<Button, Button, PackageItem>(loadButton, unloadButton, packageItem);
                unloadButton.Tag = new Tuple<Button, Button, PackageItem>(loadButton, unloadButton, packageItem);

                buttonPanel.Children.Add(loadButton);
                buttonPanel.Children.Add(unloadButton);
                headerPanel.Children.Add(buttonPanel);

                // Package name - add dependency indicator
                var headerText = new TextBlock
                {
                    Text = isDependency ? $"📝 {packageItem.DisplayName}" : packageItem.DisplayName,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Colors.White),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(headerText);
                
                // Store status indicator reference for real-time updates
                lock (_statusIndicatorsLock)
                {
                    _packageStatusIndicators[packageItem.Name] = statusIndicator;
                }

                // Store button references for real-time updates
                lock (_buttonsLock)
                {
                    _packageButtons[packageItem.Name] = (loadButton, unloadButton);
                }
                
                // Additional info panel (right-aligned) - remove images counter per request
                var infoPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                
                // File size only
                var fileSizeText = new TextBlock
                {
                    Text = packageItem.FileSizeFormatted,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), // More transparent white
                    VerticalAlignment = VerticalAlignment.Center
                };
                infoPanel.Children.Add(fileSizeText);
                
                // Create a grid to properly align left and right content
                var headerGrid = new Grid();
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                
                Grid.SetColumn(headerPanel, 0);
                Grid.SetColumn(infoPanel, 1);
                
                headerGrid.Children.Add(headerPanel);
                headerGrid.Children.Add(infoPanel);
                
                packageHeader.Child = headerGrid;
                packageGroupContainer.Children.Add(packageHeader);
                
                // Create image tiles for this package using lazy loading
                var packageImageTiles = new List<LazyLoadImage>();
                
                for (int i = 0; i < images.Count; i++)
                {
                    try
                    {
                        var image = images[i];
                        var packageKey = !string.IsNullOrEmpty(packageItem.MetadataKey) ? packageItem.MetadataKey : packageItem.Name;
                        
                        // Create lazy load image tile
                        var lazyImageTile = new LazyLoadImage
                        {
                            ImageSource = image,
                            PackageKey = packageKey,
                            ImageIndex = i,
                            CornerRadius = new CornerRadius(UI_CORNER_RADIUS),
                            Margin = new Thickness(3),
                            ToolTip = $"{packageItem.Name}\nDouble-click to open in image viewer"
                        };
                        
                        // Apply clip geometry that updates with size changes
                        void ApplyClipGeometry(LazyLoadImage border)
                        {
                            if (border != null && border.ActualWidth > 0 && border.ActualHeight > 0)
                            {
                                border.Clip = new System.Windows.Media.RectangleGeometry
                                {
                                    RadiusX = UI_CORNER_RADIUS,
                                    RadiusY = UI_CORNER_RADIUS,
                                    Rect = new Rect(0, 0, border.ActualWidth, border.ActualHeight)
                                };
                            }
                        }
                        
                        lazyImageTile.Loaded += (s, e) => ApplyClipGeometry(s as LazyLoadImage);
                        lazyImageTile.SizeChanged += (s, e) => ApplyClipGeometry(s as LazyLoadImage);
                        
                        // Add double-click handler to open image in default viewer
                        lazyImageTile.MouseLeftButtonDown += async (s, e) =>
                        {
                            if (e.ClickCount == 2)
                            {
                                var tile = s as LazyLoadImage;
                                if (tile?.ImageSource != null)
                                {
                                    await OpenImageInViewer(tile.PackageKey, tile.ImageSource);
                                }
                                e.Handled = true;
                            }
                        };
                        
                        // Register with virtualization manager
                        _virtualizedImageManager?.RegisterImage(lazyImageTile);
                        
                        packageImageTiles.Add(lazyImageTile);
                    }
                    catch (Exception)
                    {
                    }
                }
                
                // Create grid for this package's images
                if (packageImageTiles.Count > 0)
                {
                    var imageGrid = await CreatePackageImageGrid(packageImageTiles);
                    packageGroupContainer.Children.Add(imageGrid);
                    return packageGroupContainer;
                }
                
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task<UIElement> CreatePackageImageGrid(List<LazyLoadImage> imageTiles)
        {
            return await Task.Run(() =>
            {
                return Dispatcher.Invoke(() =>
                {
                    var grid = new UniformGrid
                    {
                        Columns = _imageColumns,
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    
                    foreach (var tile in imageTiles)
                    {
                        // Don't set explicit Width/Height - let UniformGrid handle sizing
                        // This ensures tiles fill the full width with no gaps
                        grid.Children.Add(tile);
                    }
                    
                    return grid;
                });
            });
        }
        
        private async Task CreateResponsiveImageGrid(List<LazyLoadImage> imageTiles)
        {
            // Create a container that will handle the responsive layout
            var container = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top
            };
            
            // Add the container to the panel first
            ImagesPanel.Children.Add(container);
            
            // Force immediate layout update and get stable dimensions
            await ForceStableLayoutAsync();
            
            // Create the grid with stable measurements - no dispatcher delay needed
            CreateImageGridWithMeasurements(container, imageTiles);
        }
        
        private void CreateImageGridWithMeasurements(StackPanel container, List<LazyLoadImage> imageTiles)
        {
            try
            {
                // Get the actual available width
                double availableWidth = GetActualAvailableWidth();
                
                if (availableWidth <= 50)
                {
                    // Use fallback width instead of retrying to prevent infinite loops
                    availableWidth = Math.Max(300, _imageColumns * 120);
                }
                
                // ALWAYS use the configured column count (_imageColumns)
                int configuredColumns = _imageColumns;
                const double tileMargin = 6; // 3px on each side
                
                // Calculate tile size to fill all available width with the configured columns
                double totalMarginSpace = configuredColumns * tileMargin;
                double actualTileSize = Math.Floor((availableWidth - totalMarginSpace) / configuredColumns);
                
                // Ensure minimum tile size
                const double minTileSize = 50;
                if (actualTileSize < minTileSize)
                {
                    actualTileSize = minTileSize;
                }
                
                // Clear the container and create the grid
                container.Children.Clear();
                
                // Create rows of images
                int currentIndex = 0;
                while (currentIndex < imageTiles.Count)
                {
                    var rowPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 3)
                    };
                    
                    // Add images to this row (always use configured column count)
                    for (int col = 0; col < configuredColumns && currentIndex < imageTiles.Count; col++)
                    {
                        var tile = imageTiles[currentIndex];
                        tile.Width = actualTileSize;
                        tile.Height = actualTileSize;
                        
                        // Ensure the image inside the tile is properly sized
                        if (tile.Child is System.Windows.Controls.Image img)
                        {
                            img.Width = actualTileSize - 6; // Account for border margin
                            img.Height = actualTileSize - 6;
                        }
                        
                        rowPanel.Children.Add(tile);
                        currentIndex++;
                    }
                    
                    container.Children.Add(rowPanel);
                }
                
                _lastTileSize = actualTileSize;
            }
            catch (Exception)
            {
            }
        }
        
        private double GetActualAvailableWidth()
        {
            double scrollViewerWidth = ImagesScrollViewer?.ActualWidth ?? 0;
            double panelWidth = ImagesPanel?.ActualWidth ?? 0;
            double mainWindowWidth = this.ActualWidth;
            
            // Get parent container information for better calculation
            var parentBorder = ImagesScrollViewer?.Parent as Border;
            double parentBorderWidth = parentBorder?.ActualWidth ?? 0;
            
            double availableWidth = 0;
            
            // FIXED: Use consistent width calculation to prevent scrollbar-related resizing
            // Always use ScrollViewer ActualWidth as primary source, accounting for scrollbar space
            if (scrollViewerWidth > 50)
            {
                // Always reserve space for potential vertical scrollbar (17px) to prevent layout shifts
                availableWidth = scrollViewerWidth - 25; // 17px scrollbar + 8px padding
            }
            // Priority 2: Use parent border width
            else if (parentBorderWidth > 50)
            {
                availableWidth = parentBorderWidth - 30; // Account for border margins and scrollbar
            }
            // Priority 3: Use ImagesPanel width
            else if (panelWidth > 50)
            {
                availableWidth = panelWidth - 25; // Reserve scrollbar space
            }
            // Priority 4: Estimate from main window width
            else if (mainWindowWidth > 200)
            {
                // More accurate estimation: images panel is typically in a splitter on the right side
                // Assuming it takes about 40-50% of the window width
                availableWidth = (mainWindowWidth * 0.45) - 60;
            }
            else
            {
                // Fallback to a reasonable default based on configured columns
                availableWidth = Math.Max(300, _imageColumns * 120);
            }
            
            return availableWidth;
        }

        #endregion

        #region Layout Management

        /// <summary>
        /// Forces a stable layout by updating all relevant UI elements and waiting for layout to settle
        /// </summary>
        private async Task ForceStableLayoutAsync()
        {
            try
            {
                // Force comprehensive layout update
                this.UpdateLayout();
                ImagesScrollViewer?.UpdateLayout();
                ImagesPanel?.UpdateLayout();
                
                // Update parent containers
                var parent = ImagesScrollViewer?.Parent as FrameworkElement;
                parent?.UpdateLayout();
                
                // Reduced delay for faster response
                await Task.Delay(15);
            }
            catch (Exception)
            {
            }
        }
        
        private void ForceLayoutUpdate()
        {
            
            try
            {
                // Update layout on all relevant UI elements in the visual tree
                if (ImagesScrollViewer != null)
                {
                    ImagesScrollViewer.UpdateLayout();
                    ImagesScrollViewer.InvalidateVisual();
                    ImagesScrollViewer.InvalidateMeasure();
                    ImagesScrollViewer.InvalidateArrange();
                }
                
                if (ImagesPanel != null)
                {
                    ImagesPanel.UpdateLayout();
                    ImagesPanel.InvalidateVisual();
                    ImagesPanel.InvalidateMeasure();
                    ImagesPanel.InvalidateArrange();
                }
                
                // Update all child elements
                foreach (UIElement child in ImagesPanel?.Children ?? new UIElementCollection(null, null))
                {
                    child?.UpdateLayout();
                    child?.InvalidateVisual();
                    child?.InvalidateMeasure();
                    child?.InvalidateArrange();
                }
                
                // Update main window layout
                this.UpdateLayout();
                this.InvalidateVisual();
                this.InvalidateMeasure();
                this.InvalidateArrange();
                
                
                // Force a render pass
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => { }));
                
            }
            catch (Exception)
            {
            }
            
        }

        /// <summary>
        /// Schedules multiple delayed reflow operations to ensure layout stability
        /// </summary>
        private void ScheduleDelayedReflows()
        {
            // Cancel existing timer
            _reflowTimer?.Stop();
            
            // Create new timer for delayed reflows - reduced from 100ms to 50ms for faster response
            _reflowTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            
            var reflowCount = 0;
            _reflowTimer.Tick += (s, e) =>
            {
                reflowCount++;
                
                try
                {
                    ForceLayoutUpdate();
                    
                    // Stop after 2 reflows instead of 3 for faster completion
                    if (reflowCount >= 2)
                    {
                        _reflowTimer.Stop();
                    }
                }
                catch (Exception)
                {
                    _reflowTimer.Stop();
                }
            };
            
            _reflowTimer.Start();
        }

        /// <summary>
        /// Refreshes the image display with current settings - used when settings change
        /// </summary>
        private async void RefreshImageDisplay()
        {
            try
            {
                // Save current scroll position as a percentage (more reliable for layout changes)
                double scrollPercentage = 0;
                if (ImagesScrollViewer != null)
                {
                    var extentHeight = ImagesScrollViewer.ExtentHeight;
                    var viewportHeight = ImagesScrollViewer.ViewportHeight;
                    var offset = ImagesScrollViewer.VerticalOffset;

                    if (extentHeight > viewportHeight)
                    {
                        scrollPercentage = offset / (extentHeight - viewportHeight);
                    }
                }

                // Handle scene mode separately
                if (_currentContentMode == "Scenes")
                {
                    // In scene mode, re-display the selected scenes' thumbnails
                    var selectedScenes = ScenesDataGrid?.SelectedItems?.Cast<SceneItem>()?.ToList();
                    if (selectedScenes != null && selectedScenes.Count > 0)
                    {
                        DisplayMultipleSceneThumbnails(selectedScenes);
                    }
                    else
                    {
                        ImagesPanel?.Children.Clear();
                    }
                }
                else
                {
                    // Package mode - get currently selected packages
                    var selectedPackages = PackageDataGrid?.SelectedItems?.Cast<PackageItem>()?.ToList();

                    if (selectedPackages != null && selectedPackages.Count > 0)
                    {

                        if (selectedPackages.Count == 1)
                        {
                            await DisplayPackageImagesAsync(selectedPackages[0]);
                        }
                        else if (selectedPackages.Count > 20)
                        {
                            await DisplayMultiplePackageImagesProgressiveAsync(selectedPackages);
                        }
                        else
                        {
                            await DisplayMultiplePackageImagesAsync(selectedPackages);
                        }
                    }
                    else
                    {
                        ImagesPanel?.Children.Clear();
                    }
                }

                // Restore scroll position as percentage after refresh - use Dispatcher to ensure UI is fully updated
                if (ImagesScrollViewer != null && scrollPercentage > 0)
                {
                    await Dispatcher.BeginInvoke(() =>
                    {
                        var extentHeight = ImagesScrollViewer.ExtentHeight;
                        var viewportHeight = ImagesScrollViewer.ViewportHeight;

                        if (extentHeight > viewportHeight)
                        {
                            var newOffset = scrollPercentage * (extentHeight - viewportHeight);
                            ImagesScrollViewer.ScrollToVerticalOffset(newOffset);
                        }
                    }, DispatcherPriority.Render);
                }

            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Gets cached package metadata or performs lookup and caches result
        /// </summary>
        private VarMetadata GetCachedPackageMetadata(string packageName)
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
                            Path.GetFileNameWithoutExtension(p.Filename).Equals(packageName, StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileNameWithoutExtension(p.Filename).Equals(normalizedPackageName, StringComparison.OrdinalIgnoreCase) ||
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

        #endregion

        #region Image Column Management

        private void IncreaseImageColumns_Click(object sender, RoutedEventArgs e)
        {
            if (_imageColumns < 10) // Maximum of 10 columns
            {
                _imageColumns++;
                _settingsManager.Settings.ImageColumns = _imageColumns;
                
                // Invalidate tile size cache since column count changed
                InvalidateTileSizeCache();
                
                // Refresh the current image display
                RefreshImageDisplay();
            }
        }

        private void DecreaseImageColumns_Click(object sender, RoutedEventArgs e)
        {
            if (_imageColumns > 1) // Minimum of 1 column
            {
                _imageColumns--;
                _settingsManager.Settings.ImageColumns = _imageColumns;
                
                // Invalidate tile size cache since column count changed
                InvalidateTileSizeCache();
                
                // Refresh the current image display
                RefreshImageDisplay();
            }
        }

        #endregion

        #region Responsive Sizing Methods

        // Cache for tile size calculation
        private double _cachedTileSize = -1;
        private double _cachedAvailableWidth = -1;
        private int _cachedImageColumns = -1;
        private DateTime _lastWidthCalculation = DateTime.MinValue;

        /// <summary>
        /// Calculate the tile size based on available width and column count (with caching for performance)
        /// </summary>
        private double CalculateTileSize()
        {
            // Use the same logic as GetActualAvailableWidth for consistency
            double availableWidth = GetActualAvailableWidth();
            
            // Check if we can use cached value (performance optimization)
            if (_cachedTileSize > 0 && 
                Math.Abs(_cachedAvailableWidth - availableWidth) < 1 && 
                _cachedImageColumns == _imageColumns)
            {
                return _cachedTileSize;
            }
            
            // Use the same calculation as CreateImageGridWithMeasurements for consistency
            const double tileMargin = 6; // 3px on each side
            double totalMarginSpace = _imageColumns * tileMargin;
            double tileSize = Math.Floor((availableWidth - totalMarginSpace) / _imageColumns);
            
            // Ensure minimum tile size
            const double minTileSize = 50;
            if (tileSize < minTileSize)
            {
                tileSize = minTileSize;
            }
            
            // Cache the result
            _cachedTileSize = tileSize;
            _cachedAvailableWidth = availableWidth;
            _cachedImageColumns = _imageColumns;
            
            return tileSize;
        }
        
        /// <summary>
        /// Invalidate the tile size cache (call when window is resized or columns change)
        /// </summary>
        private void InvalidateTileSizeCache()
        {
            _cachedTileSize = -1;
            _cachedAvailableWidth = -1;
            _cachedImageColumns = -1;
        }

        #endregion

        #region Image Statistics Methods

        /// <summary>
        /// Calculate the total file size of a collection of images
        /// </summary>
        private long CalculateImageTotalSize(IEnumerable<System.Windows.Media.Imaging.BitmapSource> images)
        {
            try
            {
                long totalSize = 0;
                foreach (var image in images)
                {
                    if (image != null)
                    {
                        // Estimate size based on pixel dimensions and format
                        var pixelWidth = image.PixelWidth;
                        var pixelHeight = image.PixelHeight;
                        var bitsPerPixel = image.Format.BitsPerPixel;
                        
                        // Calculate approximate size in bytes
                        var estimatedSize = (long)(pixelWidth * pixelHeight * bitsPerPixel / 8.0);
                        totalSize += estimatedSize;
                    }
                }
                return totalSize;
            }
            catch (Exception)
            {
                return 0;
            }
        }


        /// <summary>
        /// Counts the number of images in a package container for benchmarking
        /// </summary>
        private int CountImagesInContainer(StackPanel container)
        {
            int count = 0;
            foreach (UIElement child in container.Children)
            {
                if (child is UniformGrid grid)
                {
                    count += grid.Children.Count;
                }
            }
            return count;
        }

        #endregion

        #region Virtual Scrolling and Lazy Loading

        /// <summary>
        /// Implements lazy loading by only loading images when they become visible
        /// </summary>
        private void SetupLazyLoading()
        {
            if (ImagesScrollViewer != null)
            {
                ImagesScrollViewer.ScrollChanged += OnImagesScrollChanged;
            }
        }

        private void OnImagesScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Only process if there was actual scrolling
            if (e.VerticalChange == 0) return;

            // Find images that are now visible and load them if needed
            LoadVisibleImages();
        }

        private void LoadVisibleImages()
        {
            try
            {
                if (ImagesScrollViewer == null || ImagesPanel == null) return;

                var scrollTop = ImagesScrollViewer.VerticalOffset;
                var scrollBottom = scrollTop + ImagesScrollViewer.ViewportHeight;
                
                // Add buffer for smoother scrolling
                var buffer = ImagesScrollViewer.ViewportHeight * 0.5;
                var loadTop = Math.Max(0, scrollTop - buffer);
                var loadBottom = scrollBottom + buffer;

                // Find image tiles in the visible area
                foreach (UIElement child in ImagesPanel.Children)
                {
                    if (child is StackPanel packageContainer)
                    {
                        LoadVisibleImagesInContainer(packageContainer, loadTop, loadBottom);
                    }
                    else if (child is Border imageTile)
                    {
                        LoadImageIfVisible(imageTile, loadTop, loadBottom);
                    }
                }
            }
            catch (Exception)
            {
                // Ignore errors in lazy loading
            }
        }

        private void LoadVisibleImagesInContainer(StackPanel container, double loadTop, double loadBottom)
        {
            foreach (UIElement child in container.Children)
            {
                if (child is UniformGrid grid)
                {
                    foreach (UIElement gridChild in grid.Children)
                    {
                        if (gridChild is Border imageTile)
                        {
                            LoadImageIfVisible(imageTile, loadTop, loadBottom);
                        }
                    }
                }
            }
        }

        private void LoadImageIfVisible(Border imageTile, double loadTop, double loadBottom)
        {
            try
            {
                var position = imageTile.TransformToAncestor(ImagesScrollViewer).Transform(new Point(0, 0));
                var tileTop = position.Y;
                var tileBottom = tileTop + imageTile.ActualHeight;

                // Check if tile is in visible area
                if (tileBottom >= loadTop && tileTop <= loadBottom)
                {
                    // Load image if not already loaded (placeholder system)
                    if (imageTile.Child is System.Windows.Controls.Image img && img.Source == null)
                    {
                        // Load the actual image (this would need the image path stored in Tag or similar)
                        var imagePath = imageTile.Tag as string;
                        if (!string.IsNullOrEmpty(imagePath))
                        {
                            LoadImageAsync(img, imagePath);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore individual tile errors
            }
        }

        private async void LoadImageAsync(System.Windows.Controls.Image imageControl, string imagePath)
        {
            try
            {
                // Load image on background thread
                var bitmap = await Task.Run(() =>
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 200;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                });

                // Set on UI thread
                imageControl.Source = bitmap;
            }
            catch (Exception)
            {
                // Ignore loading errors
            }
        }

        #endregion

        #region Smart Image Distribution Helpers

        /// <summary>
        /// Gets packages with their actual image counts, skipping packages with no images
        /// </summary>
        private Task<List<(PackageItem package, int imageCount)>> GetPackagesWithImageCountAsync(List<PackageItem> selectedPackages)
        {
            var result = new List<(PackageItem package, int imageCount)>();
            
            foreach (var packageItem in selectedPackages)
            {
                // Find the package metadata using cached lookup (handles archived packages)
                // Use MetadataKey for accurate lookup (handles multiple versions of same package)
                var packageMetadata = GetCachedPackageMetadata(!string.IsNullOrEmpty(packageItem.MetadataKey) ? packageItem.MetadataKey : packageItem.Name);
                
                if (packageMetadata == null)
                {
                    result.Add((packageItem, 0));
                    continue;
                }
                
                // Check if package has cached images
                var packageBase = Path.GetFileNameWithoutExtension(packageMetadata.Filename);
                var imageCount = _imageManager.GetCachedImageCount(packageBase);
                
                result.Add((packageItem, imageCount));
            }
            
            return Task.FromResult(result);
        }

        /// <summary>
        /// Aligns the image count to column multiples for better grid layout
        /// Examples: 4 images with 3 columns -> use 3 or 6 instead of 4
        /// </summary>
        private int AlignToColumnCount(int baseCount, int columns)
        {
            if (baseCount <= 0 || columns <= 0) return 1;
            
            // If base count is already a multiple of columns, use it
            if (baseCount % columns == 0) return baseCount;
            
            // Find the closest multiples
            var lowerMultiple = (baseCount / columns) * columns;
            var upperMultiple = lowerMultiple + columns;
            
            // Choose the closer multiple, but prefer the lower one if they're equally close
            var diffLower = baseCount - lowerMultiple;
            var diffUpper = upperMultiple - baseCount;
            
            // If lower multiple is 0, use upper multiple
            if (lowerMultiple == 0) return upperMultiple;
            
            // Use lower multiple if it's closer or equal distance (to avoid showing too many images)
            return diffLower <= diffUpper ? lowerMultiple : upperMultiple;
        }

        #endregion

        #region Real-time Status Updates

        /// <summary>
        /// Updates the status indicator for a package in the image grid
        /// </summary>
        private void UpdatePackageStatusInImageGrid(string packageName, string newStatus, Color newStatusColor)
        {
            try
            {
                lock (_statusIndicatorsLock)
                {
                    if (_packageStatusIndicators.TryGetValue(packageName, out var indicator))
                    {
                        // Update on UI thread
                        Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                // Update status indicator color
                                indicator.Fill = new SolidColorBrush(newStatusColor);
                            }
                            catch (Exception)
                            {
                            }
                        }, DispatcherPriority.Normal);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Updates status for multiple packages at once (batch operation)
        /// </summary>
        private void UpdateMultiplePackageStatusInImageGrid(IEnumerable<(string packageName, string status, Color statusColor)> updates)
        {
            try
            {
                var updateList = updates.ToList();
                if (updateList.Count == 0) return;

                Dispatcher.InvokeAsync(() =>
                {
                    lock (_statusIndicatorsLock)
                    {
                        foreach (var (packageName, status, statusColor) in updateList)
                        {
                            if (_packageStatusIndicators.TryGetValue(packageName, out var indicator))
                            {
                                try
                                {
                                    indicator.Fill = new SolidColorBrush(statusColor);
                                }
                                catch (Exception)
                                {
                                }
                            }

                            // Update button visibility based on status
                            if (_packageButtons.TryGetValue(packageName, out var buttons))
                            {
                                try
                                {
                                    var (loadButton, unloadButton) = buttons;
                                    if (loadButton != null && unloadButton != null)
                                    {
                                        if (status == "Available")
                                        {
                                            loadButton.Visibility = Visibility.Visible;
                                            unloadButton.Visibility = Visibility.Collapsed;
                                        }
                                        else if (status == "Loaded")
                                        {
                                            loadButton.Visibility = Visibility.Collapsed;
                                            unloadButton.Visibility = Visibility.Visible;
                                        }
                                        else
                                        {
                                            // For other statuses (Missing, Unknown, Archived, etc.), hide both buttons
                                            loadButton.Visibility = Visibility.Collapsed;
                                            unloadButton.Visibility = Visibility.Collapsed;
                                        }
                                    }
                                }
                                catch (Exception)
                                {
                                }
                            }
                        }
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Progressive loading for multiple packages when selection exceeds 20 packages.
        /// Creates placeholders immediately and loads content as packages scroll into view.
        /// </summary>
        private async Task DisplayMultiplePackageImagesProgressiveAsync(List<PackageItem> selectedPackages, List<bool> packageSources = null)
        {
            try
            {

                // Clear existing images and status indicator tracking
                ImagesPanel.Children.Clear();
                lock (_statusIndicatorsLock)
                {
                    _packageStatusIndicators.Clear();
                }
                lock (_buttonsLock)
                {
                    _packageButtons.Clear();
                }

                var settings = _settingsManager?.Settings;

                // Initialize packageSources if not provided (all packages are considered packages)
                if (packageSources == null || packageSources.Count != selectedPackages.Count)
                {
                    packageSources = selectedPackages.Select(p => true).ToList(); // true = package
                }

                // With virtualization, skip expensive upfront image counting
                // CreatePackagePlaceholderContainer will return null for packages with no images (fast fail)
                // This makes large selections instant regardless of package count
                var packageContainers = new List<(PackageItem package, StackPanel container, bool isLoaded)>();
                for (int i = 0; i < selectedPackages.Count; i++)
                {
                    var packageItem = selectedPackages[i];
                    var isDependency = i < packageSources.Count ? packageSources[i] : false;
                    var placeholderContainer = CreatePackagePlaceholderContainer(packageItem, !isDependency); // Note: false means dependency
                    if (placeholderContainer != null)
                    {
                        ImagesPanel.Children.Add(placeholderContainer);
                        packageContainers.Add((packageItem, placeholderContainer, false));
                    }
                }
                
                // If no containers were created, show message
                if (packageContainers.Count == 0)
                {
                    var noImagesText = new TextBlock
                    {
                        Text = "No cached images found for selected packages.\nTry building the image cache first.",
                        FontSize = 14,
                        Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(20),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    ImagesPanel.Children.Add(noImagesText);
                    return;
                }

                // Set up scroll-based lazy loading
                SetupProgressiveLoading(packageContainers, packageSources);
            }
            catch (Exception)
            {
                // Fallback to regular loading if progressive loading fails
                ImagesPanel.Children.Clear();
                await DisplayMultiplePackageImagesAsync(selectedPackages, packageSources);
            }
        }

        /// <summary>
        /// Creates a placeholder package container that will be filled with actual images when scrolled into view
        /// </summary>
        private StackPanel CreatePackagePlaceholderContainer(PackageItem packageItem, bool isDependency = false)
        {
            try
            {
                // Find the package metadata
                // Use MetadataKey for accurate lookup (handles multiple versions of same package)
                var packageMetadata = GetCachedPackageMetadata(!string.IsNullOrEmpty(packageItem.MetadataKey) ? packageItem.MetadataKey : packageItem.Name);

                if (packageMetadata == null)
                {
                    return null;
                }

                // Get cached images for this package
                var packageBase = Path.GetFileNameWithoutExtension(packageMetadata.Filename);

                // Get total available images to show in header
                var totalAvailableImages = _imageManager.GetCachedImageCount(packageBase);
                if (totalAvailableImages == 0)
                {
                    return null;
                }

                // Create package group container
                var packageGroupContainer = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(0, 0, 0, 20),
                    Tag = packageItem.Name // Store package name for later loading
                };

                // Create package header with status - different styling for dependencies
                var packageHeader = new Border
                {
                    Background = isDependency 
                        ? new SolidColorBrush(Color.FromArgb(40, 149, 100, 237)) // Green background for dependencies
                        : new SolidColorBrush(Color.FromArgb(40, 100, 149, 237)), // Blue background for packages
                    CornerRadius = new CornerRadius(UI_CORNER_RADIUS),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                // Create header content with status indicator
                var headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal
                };

                // Status indicator dot
                var statusIndicator = new System.Windows.Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = new SolidColorBrush(packageItem.StatusColor),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(statusIndicator);

                // Package name - add dependency indicator
                var headerText = new TextBlock
                {
                    Text = isDependency ? $"📝 {packageItem.DisplayName}" : packageItem.DisplayName,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Colors.White),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(headerText);

                // Store status indicator reference for real-time updates
                lock (_statusIndicatorsLock)
                {
                    _packageStatusIndicators[packageItem.Name] = statusIndicator;
                }

                // Add info panel without image count
                var infoPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                
                var fileSizeText = new TextBlock
                {
                    Text = packageItem.FileSizeFormatted,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), // More transparent white
                    VerticalAlignment = VerticalAlignment.Center
                };
                infoPanel.Children.Add(fileSizeText);

                // Create a grid to properly align left and right content
                var headerGrid = new Grid();
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Grid.SetColumn(headerPanel, 0);
                Grid.SetColumn(infoPanel, 1);

                headerGrid.Children.Add(headerPanel);
                headerGrid.Children.Add(infoPanel);

                packageHeader.Child = headerGrid;
                packageGroupContainer.Children.Add(packageHeader);

                // Create placeholder image grid with loading indicators
                var placeholderGrid = CreatePlaceholderImageGrid();
                packageGroupContainer.Children.Add(placeholderGrid);

                return packageGroupContainer;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Creates a placeholder image grid that shows loading indicators
        /// </summary>
        private UIElement CreatePlaceholderImageGrid()
        {
            var grid = new UniformGrid
            {
                Columns = _imageColumns,
                Margin = new Thickness(0, 0, 0, 8)
            };

            // Calculate proper tile size to match actual images
            var tileSize = CalculateTileSize();

            // Create placeholder tiles (show a reasonable number of loading indicators)
            for (int i = 0; i < 12; i++)
            {
                var placeholderTile = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(20, 100, 149, 237)), // Very light blue background
                    CornerRadius = new CornerRadius(UI_CORNER_RADIUS),
                    Margin = new Thickness(3),
                    Width = tileSize,
                    Height = tileSize, // Use calculated tile size instead of fixed 80
                    Child = new TextBlock
                    {
                        Text = "³", // Loading icon
                        FontSize = 24,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromArgb(100, 100, 149, 237))
                    }
                };
                grid.Children.Add(placeholderTile);
            }

            return grid;
        }

        /// <summary>
        /// Sets up progressive loading based on scroll position
        /// </summary>
        private void SetupProgressiveLoading(List<(PackageItem package, StackPanel container, bool isLoaded)> packageContainers, List<bool> packageSources = null)
        {
            // Store the package containers for scroll-based loading
            _progressivePackageContainers = packageContainers;
            _progressivePackageSources = packageSources ?? packageContainers.Select(p => true).ToList(); // true = package

            // Set up scroll event handler if not already done
            if (ImagesScrollViewer != null && !_progressiveLoadingSetup)
            {
                ImagesScrollViewer.ScrollChanged += OnProgressiveScrollChanged;
                _progressiveLoadingSetup = true;
            }

            // Load initially visible packages after UI layout is complete
            // Use Dispatcher to ensure UI is fully rendered before calculating positions
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LoadVisiblePackageContainers();

                // Start background loading of ALL remaining packages
                StartBackgroundLoading();
            }), DispatcherPriority.Render);
        }

        /// <summary>
        /// Starts background loading of all remaining packages not yet loaded
        /// </summary>
        private async void StartBackgroundLoading()
        {
            if (_progressivePackageContainers == null || _progressivePackageContainers.Count == 0)
                return;

            try
            {
                // Load all packages that aren't already loaded, in batches
                var packagesToLoad = _progressivePackageContainers
                    .Where(p => !p.isLoaded)
                    .ToList();

                if (packagesToLoad.Count == 0)
                    return;

                // Load in smaller batches for background processing (don't overwhelm)
                const int backgroundBatchSize = 2; // Smaller batches for background
                for (int i = 0; i < packagesToLoad.Count; i += backgroundBatchSize)
                {
                    var batch = packagesToLoad.Skip(i).Take(backgroundBatchSize).ToList();

                    // Load this batch
                    var loadTasks = batch.Select(async item =>
                    {
                        int index = _progressivePackageContainers.IndexOf(item);
                        await LoadPackageContainerContentAsync(item.package, item.container, index);
                    });

                    await Task.WhenAll(loadTasks);

                    // Small delay between batches to keep UI responsive and not compete with scroll loading
                    if (i + backgroundBatchSize < packagesToLoad.Count)
                    {
                        await Task.Delay(100); // Longer delay for background loading
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // Progressive loading state
        private List<(PackageItem package, StackPanel container, bool isLoaded)> _progressivePackageContainers;
        private List<bool> _progressivePackageSources; // true = package, false = dependency
        private bool _progressiveLoadingSetup = false;

        /// <summary>
        /// Handles scroll changes for progressive loading
        /// </summary>
        private void OnProgressiveScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0) return; // Only process if there was actual scrolling

            // Debounce rapid scroll events
            _progressiveScrollDebounceTimer?.Stop();
            _progressiveScrollDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100),
                IsEnabled = false
            };
            _progressiveScrollDebounceTimer.Tick += (s, args) =>
            {
                _progressiveScrollDebounceTimer.Stop();
                LoadVisiblePackageContainers();
            };
            _progressiveScrollDebounceTimer.Start();
        }

        private DispatcherTimer _progressiveScrollDebounceTimer;

        /// <summary>
        /// Loads package containers that are currently visible in the scroll viewer
        /// </summary>
        private async void LoadVisiblePackageContainers()
        {
            if (_progressivePackageContainers == null || ImagesScrollViewer == null) return;

            try
            {
                var scrollTop = ImagesScrollViewer.VerticalOffset;
                var scrollBottom = scrollTop + ImagesScrollViewer.ViewportHeight;

                // Add buffer for smoother scrolling (load packages slightly before they come into view)
                var buffer = ImagesScrollViewer.ViewportHeight * 0.5;
                var loadTop = Math.Max(0, scrollTop - buffer);
                var loadBottom = scrollBottom + buffer;

                // Find containers that are in the visible/load area and not yet loaded
                var containersToLoad = new List<(PackageItem package, StackPanel container, int index)>();

                for (int i = 0; i < _progressivePackageContainers.Count; i++)
                {
                    var (package, container, isLoaded) = _progressivePackageContainers[i];

                    if (isLoaded) continue; // Already loaded

                    // Check if this container is in the load area
                    var position = container.TransformToAncestor(ImagesScrollViewer).Transform(new Point(0, 0));
                    var containerTop = position.Y;
                    var containerBottom = containerTop + container.ActualHeight;

                    if (containerBottom >= loadTop && containerTop <= loadBottom)
                    {
                        containersToLoad.Add((package, container, i));
                    }
                }

                // Load containers in parallel, but limit concurrency to avoid overwhelming the system
                const int maxConcurrentLoads = 3;
                for (int i = 0; i < containersToLoad.Count; i += maxConcurrentLoads)
                {
                    var batch = containersToLoad.Skip(i).Take(maxConcurrentLoads).ToList();
                    var loadTasks = batch.Select(async item =>
                    {
                        await LoadPackageContainerContentAsync(item.package, item.container, item.index);
                    });

                    await Task.WhenAll(loadTasks);

                    // Small delay between batches to keep UI responsive
                    if (i + maxConcurrentLoads < containersToLoad.Count)
                    {
                        await Task.Delay(50);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Loads the actual content for a package container placeholder
        /// </summary>
        private async Task LoadPackageContainerContentAsync(PackageItem packageItem, StackPanel container, int containerIndex)
        {
            try
            {
                // Mark as loaded to prevent duplicate loading
                _progressivePackageContainers[containerIndex] = (packageItem, container, true);

                // Create the actual package container with images
                var realContainer = await CreatePackageContainerAsync(packageItem, !_progressivePackageSources[containerIndex]);

                if (realContainer != null)
                {
                    // Replace the entire placeholder container with the real container
                    await Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            // Find the placeholder container in the ImagesPanel and replace it
                            var panelIndex = ImagesPanel.Children.IndexOf(container);
                            if (panelIndex >= 0)
                            {
                                // Remove the old container first
                                ImagesPanel.Children.RemoveAt(panelIndex);
                                // Insert the new container at the same position
                                ImagesPanel.Children.Insert(panelIndex, realContainer);
                            }
                        }
                        catch (Exception)
                        {
                        }
                    });
                }
            }
            catch (Exception)
            {
            }
        }
        #endregion

        #region Single Package Load/Unload Operations

        /// <summary>
        /// Loads a single package and updates related UI elements
        /// </summary>
        private async Task LoadSinglePackageAsync(PackageItem packageItem, Button loadButton, Button unloadButton)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return;

                if (packageItem.Status != "Available" || packageItem.Status == "Archived")
                {
                    return; // Package is not available for loading or is archived
                }

                // Perform the load operation
                var result = await _packageFileManager.LoadPackageAsync(packageItem.Name);

                if (result.success)
                {
                    // Update package status
                    packageItem.Status = "Loaded";

                    // Update buttons visibility
                    await Dispatcher.InvokeAsync(() =>
                    {
                        loadButton.Visibility = Visibility.Collapsed;
                        unloadButton.Visibility = Visibility.Visible;

                        // Update status indicator color
                        UpdatePackageStatusInImageGrid(packageItem.Name, "Loaded", packageItem.StatusColor);
                    });

                    // Update dependency status specifically for this package
                    UpdateDependencyStatus(packageItem.Name, "Loaded");

                    // Update button bars
                    UpdateDependenciesButtonBar();
                    UpdatePackageButtonBar();

                    SetStatus($"Loaded package {packageItem.DisplayName}");
                }
                else
                {
                    SetStatus($"Failed to load package {packageItem.DisplayName}: {result.error}");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Error loading package {packageItem.DisplayName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Unloads a single package and updates related UI elements
        /// </summary>
        private async Task UnloadSinglePackageAsync(PackageItem packageItem, Button loadButton, Button unloadButton)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return;

                if (packageItem.Status != "Loaded" || packageItem.Status == "Archived")
                {
                    return; // Package is not loaded or is archived
                }

                // Perform the unload operation
                var result = await _packageFileManager.UnloadPackageAsync(packageItem.Name);

                if (result.success)
                {
                    // Update package status
                    packageItem.Status = "Available";

                    // Update buttons visibility
                    await Dispatcher.InvokeAsync(() =>
                    {
                        unloadButton.Visibility = Visibility.Collapsed;
                        loadButton.Visibility = Visibility.Visible;

                        // Update status indicator color
                        UpdatePackageStatusInImageGrid(packageItem.Name, "Available", packageItem.StatusColor);
                    });

                    // Update dependency status specifically for this package
                    UpdateDependencyStatus(packageItem.Name, "Available");

                    // Update button bars
                    UpdateDependenciesButtonBar();
                    UpdatePackageButtonBar();

                    SetStatus($"Unloaded package {packageItem.DisplayName}");
                }
                else
                {
                    SetStatus($"Failed to unload package {packageItem.DisplayName}: {result.error}");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Error unloading package {packageItem.DisplayName}: {ex.Message}");
            }
        }

        #endregion

        #region Dependency Status Updates

        /// <summary>
        /// Updates the status of all DependencyItem objects in the Dependencies collection
        /// to reflect current package load states
        /// </summary>
        private void UpdateDependenciesStatus()
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        foreach (var dependencyItem in Dependencies)
                        {
                            if (!string.IsNullOrEmpty(dependencyItem.Name))
                            {
                                // The dependencyItem.Name is already the clean base name for GetPackageStatus
                                // (processed in DisplayConsolidatedDependencies to remove version/file extensions)
                                var status = _packageFileManager?.GetPackageStatus(dependencyItem.Name) ?? "Unknown";
                                dependencyItem.Status = status;
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Updates the status of a specific dependency in the Dependencies collection
        /// </summary>
        private void UpdateDependencyStatus(string packageName, string newStatus)
        {
            try
            {
                // Extract base name from package name (same logic as DisplayConsolidatedDependencies)
                string baseName = packageName;
                if (packageName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = packageName.Substring(0, packageName.Length - 7); // Remove .latest
                }
                else
                {
                    // Check for numeric version at the end
                    var lastDotIndex = packageName.LastIndexOf('.');
                    if (lastDotIndex > 0)
                    {
                        var potentialVersion = packageName.Substring(lastDotIndex + 1);
                        if (int.TryParse(potentialVersion, out _))
                        {
                            baseName = packageName.Substring(0, lastDotIndex);
                        }
                    }
                }

                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Find dependency item by cleaned base name
                        var dependencyItem = Dependencies.FirstOrDefault(d => d.Name == baseName);
                        if (dependencyItem != null)
                        {
                            dependencyItem.Status = newStatus;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Image Opening

        /// <summary>
        /// Opens an image in the default system image viewer
        /// </summary>
        private async Task OpenImageInViewer(string packageNameOrMetadataKey, BitmapSource imageSource)
        {
            try
            {
                // packageNameOrMetadataKey is the MetadataKey from the image tile Tag (e.g., "DJ.TanLines.1")
                // Extract the base package name (without version) for metadata lookup
                string basePackageName = packageNameOrMetadataKey;
                if (packageNameOrMetadataKey.Contains("#"))
                {
                    basePackageName = packageNameOrMetadataKey.Split('#')[0];
                }
                
                // Get package metadata to find the VAR file
                var packageMetadata = GetCachedPackageMetadata(basePackageName);
                if (packageMetadata == null)
                {
                    MessageBox.Show($"Could not find package: {packageNameOrMetadataKey}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Get the actual file path using MetadataKey for accurate lookup (handles multiple versions of same package)
                PackageFileInfo pkgInfo = _packageFileManager?.GetPackageFileInfoByMetadataKey(packageNameOrMetadataKey);
                if (pkgInfo == null || string.IsNullOrEmpty(pkgInfo.CurrentPath))
                {
                    MessageBox.Show($"Could not find package file: {packageNameOrMetadataKey}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Create temp directory for extracted images
                // Use base package name (without version) for temp directory
                string tempDir = Path.Combine(Path.GetTempPath(), "VAM_Images", basePackageName);
                Directory.CreateDirectory(tempDir);

                // Generate a unique filename based on image hash or timestamp
                string tempFile = Path.Combine(tempDir, $"image_{DateTime.Now.Ticks}.jpg");

                // Save the BitmapSource to temp file
                await Task.Run(() =>
                {
                    var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(imageSource));
                    
                    using (var fileStream = new FileStream(tempFile, FileMode.Create))
                    {
                        encoder.Save(fileStream);
                    }
                });

                // Open with default image viewer
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempFile,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}

