using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// Presets (custom atom person) management functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        private List<CustomAtomItem> _originalCustomAtomItems = new List<CustomAtomItem>();
        
        // Search text for custom atom filtering (used by CollectionView.Filter)
        private string _customAtomSearchText = "";
        
        // In-memory cache for custom item thumbnails to avoid re-reading files and prevent file locking
        // Key: ThumbnailPath, Value: (BitmapImage, LastWriteTime) - LastWriteTime used for cache invalidation
        private readonly Dictionary<string, (BitmapImage Image, long LastWriteTicks)> _customThumbnailCache = new();
        
        /// <summary>
        /// Clears the custom thumbnail cache for a specific file path (call when file is deleted/renamed)
        /// </summary>
        public void ClearCustomThumbnailCache(string thumbnailPath)
        {
            if (string.IsNullOrEmpty(thumbnailPath)) return;
            lock (_customThumbnailCache)
            {
                _customThumbnailCache.Remove(thumbnailPath);
            }
        }
        
        /// <summary>
        /// Clears all custom thumbnail cache entries
        /// </summary>
        public void ClearAllCustomThumbnailCache()
        {
            lock (_customThumbnailCache)
            {
                _customThumbnailCache.Clear();
            }
        }
        
        /// <summary>
        /// Loads all custom content (presets and scenes) from both Custom\Atom\Person and Saves\scene folders
        /// </summary>
        public async Task LoadCustomAtomItemsAsync()
        {
            if (_unifiedCustomContentScanner == null)
            {
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    // Scan both presets and scenes locations
                    var items = _unifiedCustomContentScanner.ScanAllCustomContent();
                    
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // Store original items for filtering
                        _originalCustomAtomItems = new List<CustomAtomItem>(items);
                        
                        // Check if each item is marked as favorite or hidden
                        foreach (var item in items)
                        {
                            // For custom atoms, favorites are stored as .vap.fav or .json.fav
                            var favPath = item.FilePath + ".fav";
                            item.IsFavorite = File.Exists(favPath);
                            
                            // For custom atoms, hidden items are stored as .vap.hide or .json.hide
                            var hidePath = item.FilePath + ".hide";
                            item.IsHidden = File.Exists(hidePath);
                        }
                        
                        CustomAtomItems.ReplaceAll(items);
                        SetStatus($"Loaded {items.Count} custom item(s) (presets & scenes)");
                        
                        // Populate custom content filters if we're in Custom mode
                        if (_currentContentMode == "Custom")
                        {
                            PopulatePresetCategoryFilter();
                            PopulatePresetSubfolderFilter();
                            PopulatePresetDateFilter();
                            PopulatePresetFileSizeFilter();
                            PopulatePresetStatusFilter();
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Error loading custom items: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles custom atom search box text changed
        /// </summary>
        private void CustomAtomSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && this.IsLoaded)
            {
                var grayBrush = (System.Windows.Media.SolidColorBrush)FindResource(System.Windows.SystemColors.GrayTextBrushKey);
                bool isPlaceholder = textBox.Foreground.Equals(grayBrush);
                
                if (!isPlaceholder && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Filter the custom atom items list
                    FilterCustomAtomItems(textBox.Text);
                    if (CustomAtomSearchClearButton != null)
                        CustomAtomSearchClearButton.Visibility = Visibility.Visible;
                }
                else if (isPlaceholder || string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Show all items when no filter
                    FilterCustomAtomItems("");
                    if (CustomAtomSearchClearButton != null)
                        CustomAtomSearchClearButton.Visibility = Visibility.Collapsed;
                }
            }
        }

        /// <summary>
        /// Clears the presets search filter
        /// </summary>
        private void ClearCustomAtomFilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (CustomAtomSearchBox != null)
            {
                CustomAtomSearchBox.Text = "🔍 Filter presets by name...";
                CustomAtomSearchBox.Foreground = (System.Windows.Media.Brush)FindResource(System.Windows.SystemColors.GrayTextBrushKey);
            }
            FilterCustomAtomItems("");
            if (CustomAtomSearchClearButton != null)
                CustomAtomSearchClearButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Filters custom atom items by search text using CollectionView.Filter
        /// </summary>
        private void FilterCustomAtomItems(string searchText)
        {
            _customAtomSearchText = searchText ?? "";
            ApplyPresetFilters();
        }

        /// <summary>
        /// Handles custom atom item selection changed
        /// </summary>
        private void CustomAtomDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Update counters immediately
            UpdateOptimizeCounter();
            UpdateFavoriteCounter();
            UpdateAutoinstallCounter();
            UpdateHideCounter();

            if (CustomAtomDataGrid?.SelectedItems.Count == 0)
            {
                Dependencies.Clear();
                DependenciesCountText.Text = "(0)";
                ClearCategoryTabs();
                ClearImageGrid();
                SetStatus("No custom atom items selected");
                return;
            }

            // Cancel any pending preset selection update
            _presetSelectionCts?.Cancel();
            _presetSelectionCts?.Dispose();
            _presetSelectionCts = new System.Threading.CancellationTokenSource();
            var presetToken = _presetSelectionCts.Token;

            // Trigger debounced preset selection handler
            _presetSelectionDebouncer?.Trigger();

            // Schedule the actual content update after debounce delay
            _ = Task.Delay(SELECTION_DEBOUNCE_DELAY_MS, presetToken).ContinueWith(_ =>
            {
                // Check if this operation was cancelled
                if (presetToken.IsCancellationRequested)
                    return;

                Dispatcher.Invoke(() =>
                {
                    // Display thumbnails for selected items
                    var selectedItems = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>()?.ToList() ?? new List<CustomAtomItem>();
                    DisplayCustomAtomThumbnails(selectedItems);

                    // Populate dependencies from selected presets
                    PopulatePresetDependencies(selectedItems);

                    // Update the details area
                    UpdatePackageButtonBar();

                    SetStatus($"Selected {selectedItems.Count} custom atom item(s)");
                });
            });
        }

        /// <summary>
        /// Displays thumbnails for custom atom items in the image grid
        /// </summary>
        private void DisplayCustomAtomThumbnails(List<CustomAtomItem> items)
        {
            try
            {
                // Clear existing images
                PreviewImages.Clear();
                
                // Clear the virtualized manager if it exists
                _virtualizedImageGridManager?.Clear();

                if (items == null || items.Count == 0)
                {
                    return;
                }

                var customItemsPackage = new PackageItem
                {
                    Name = "Selected Items",
                    Status = "Available"
                };

                // Display thumbnail for each selected item
                foreach (var item in items)
                {
                    // Always add the item, even if thumbnail is missing
                    // This allows the grid to show a placeholder/loading state
                    
                    var previewItem = new ImagePreviewItem
                    {
                        Image = null, // Load lazily via callback
                        PackageName = item.Name,
                        InternalPath = item.ThumbnailPath ?? "",
                        StatusBrush = System.Windows.Media.Brushes.Transparent,
                        PackageItem = customItemsPackage,
                        ItemFileSize = item.FileSize, // Set individual item size for banner display
                        ShowLoadButton = false, // Hide Load button in Custom mode
                        GroupKey = item.Name, // Each item gets its own group/banner in Custom mode
                        
                        // Use LoadImageCallback for async lazy loading
                        LoadImageCallback = async () => 
                        {
                            // If no thumbnail path, return null (LazyLoadImage handles this)
                            if (string.IsNullOrEmpty(item.ThumbnailPath) || !System.IO.File.Exists(item.ThumbnailPath))
                            {
                                return null;
                            }

                            // Check memory cache first to avoid re-reading files
                            var thumbnailPath = item.ThumbnailPath;
                            long lastWriteTicks = 0;
                            try
                            {
                                lastWriteTicks = new FileInfo(thumbnailPath).LastWriteTimeUtc.Ticks;
                            }
                            catch
                            {
                                // File may have been deleted
                                return null;
                            }
                            
                            // Check cache - return cached image if file hasn't changed
                            lock (_customThumbnailCache)
                            {
                                if (_customThumbnailCache.TryGetValue(thumbnailPath, out var cached) && cached.LastWriteTicks == lastWriteTicks)
                                {
                                    return cached.Image;
                                }
                            }
                            
                            return await Task.Run(() => 
                            {
                                try 
                                {
                                    // CRITICAL: Read file bytes into memory first to avoid file locking
                                    // Using UriSource can hold file locks even with BitmapCacheOption.OnLoad
                                    // This allows the file to be deleted/renamed immediately after loading
                                    byte[] imageBytes;
                                    using (var fileStream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                    {
                                        imageBytes = new byte[fileStream.Length];
                                        fileStream.Read(imageBytes, 0, imageBytes.Length);
                                    }
                                    // File is now released - create BitmapImage from memory
                                    var bi = new BitmapImage();
                                    bi.BeginInit();
                                    bi.CacheOption = BitmapCacheOption.OnLoad;
                                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
                                    // Decode to a reasonable size for thumbnails to save memory
                                    bi.DecodePixelWidth = 300;
                                    bi.StreamSource = new MemoryStream(imageBytes);
                                    bi.EndInit();
                                    bi.Freeze(); // Must freeze to pass between threads
                                    
                                    // Cache the loaded image for future use
                                    lock (_customThumbnailCache)
                                    {
                                        _customThumbnailCache[thumbnailPath] = (bi, lastWriteTicks);
                                    }
                                    
                                    return bi;
                                }
                                catch
                                {
                                    return null;
                                }
                            });
                        }
                    };
                    
                    PreviewImages.Add(previewItem);
                }
                
                // Trigger initial load for visible images
                // Use fire-and-forget pattern as we can't await here easily
                _ = _virtualizedImageGridManager?.LoadInitialVisibleImagesAsync();
            }
            catch
            {
                // Error displaying thumbnails - silently handled
            }
        }

        /// <summary>
        /// Populates dependencies from selected preset items
        /// </summary>
        private void PopulatePresetDependencies(List<CustomAtomItem> selectedItems)
        {
            try
            {
                // Refresh package status index to ensure we have the latest status of all packages
                // This is critical when switching presets after downloading dependencies
                _packageFileManager?.RefreshPackageStatusIndex();

                // Clear existing dependencies
                Dependencies.Clear();
                _originalDependencies.Clear();

                if (selectedItems == null || selectedItems.Count == 0)
                {
                    DependenciesCountText.Text = "(0)";
                    return;
                }

                // Accumulate dependencies from all selected presets
                var allDependencies = new HashSet<string>(); // Use HashSet to avoid duplicates
                int totalDependencies = 0;

                foreach (var preset in selectedItems)
                {
                    if (preset.Dependencies != null)
                    {
                        totalDependencies += preset.Dependencies.Count;
                        foreach (var dep in preset.Dependencies)
                        {
                            allDependencies.Add(dep);
                        }
                    }
                }

                // Process accumulated dependencies (same logic as scene mode)
                foreach (var dep in allDependencies.OrderBy(d => d))
                {
                    // Use DependencyVersionInfo to parse all version formats:
                    // .latest, .min[NUMBER], and exact versions
                    var depInfo = DependencyVersionInfo.Parse(dep);
                    string baseName = depInfo.BaseName;
                    string version = depInfo.VersionType switch
                    {
                        DependencyVersionType.Latest => "latest",
                        DependencyVersionType.Minimum => $"min{depInfo.VersionNumber}",
                        DependencyVersionType.Exact => depInfo.VersionNumber?.ToString() ?? "",
                        _ => ""
                    };
                    
                    // Get the actual status from package manager using the full dependency string
                    // GetPackageStatus now handles .latest, .min[NUMBER], and exact versions
                    var status = _packageFileManager?.GetPackageStatus(dep) ?? "Missing";
                    // Store base name and version separately in DependencyItem
                    var depItem = new DependencyItem { Name = baseName, Version = version, Status = status };
                    Dependencies.Add(depItem);
                    _originalDependencies.Add(depItem);
                }

                // Update count display
                DependenciesCountText.Text = $"({Dependencies.Count})";

                // Create category tabs for presets
                CreatePresetCategoryTabs(selectedItems);

                SetStatus($"Found {Dependencies.Count} unique dependencies from {selectedItems.Count} preset(s)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating preset dependencies: {ex.Message}");
                SetStatus("Error loading preset dependencies");
            }
        }

        /// <summary>
        /// Creates category tabs for selected presets showing hair, clothing, morphs, etc.
        /// </summary>
        private void CreatePresetCategoryTabs(List<CustomAtomItem> selectedItems)
        {
            try
            {
                ClearCategoryTabs();

                var allHairItems = new List<string>();
                var allClothingItems = new List<string>();
                var allMorphItems = new List<string>();
                var allTextureItems = new List<string>();

                // Collect all items from selected presets
                foreach (var preset in selectedItems)
                {
                    if (preset.HairItems != null)
                        allHairItems.AddRange(preset.HairItems);
                    if (preset.ClothingItems != null)
                        allClothingItems.AddRange(preset.ClothingItems);
                    if (preset.MorphItems != null)
                        allMorphItems.AddRange(preset.MorphItems);
                    if (preset.TextureItems != null)
                        allTextureItems.AddRange(preset.TextureItems);
                }

                // Remove duplicates
                allHairItems = allHairItems.Distinct().ToList();
                allClothingItems = allClothingItems.Distinct().ToList();
                allMorphItems = allMorphItems.Distinct().ToList();
                allTextureItems = allTextureItems.Distinct().ToList();

                // Create tabs for each category that has items
                if (allHairItems.Count > 0)
                {
                    CreatePresetCategoryTab("Hair", allHairItems, "💇");
                }
                if (allClothingItems.Count > 0)
                {
                    CreatePresetCategoryTab("Clothing", allClothingItems, "👗");
                }
                if (allMorphItems.Count > 0)
                {
                    CreatePresetCategoryTab("Morphs", allMorphItems, "🎭");
                }
                if (allTextureItems.Count > 0)
                {
                    CreatePresetCategoryTab("Textures", allTextureItems, "🎨");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating preset category tabs: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a category tab for preset items
        /// </summary>
        private void CreatePresetCategoryTab(string categoryName, List<string> items, string icon)
        {
            try
            {
                var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
                headerPanel.Children.Add(new TextBlock { Text = $"{icon} {categoryName} ({items.Count})", VerticalAlignment = VerticalAlignment.Center });

                var tab = new TabItem 
                { 
                    Header = headerPanel,
                    Style = PackageInfoTabControl.FindResource(typeof(TabItem)) as Style
                };
                var listBox = new ListBox
                {
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(5)
                };
                // Use theme resources
                listBox.SetResourceReference(Control.BackgroundProperty, SystemColors.WindowBrushKey);
                listBox.SetResourceReference(Control.ForegroundProperty, SystemColors.ControlTextBrushKey);

                foreach (var item in items)
                {
                    var listBoxItem = new ListBoxItem
                    {
                        Content = item,
                        Background = Brushes.Transparent
                    };
                    listBoxItem.SetResourceReference(Control.ForegroundProperty, SystemColors.ControlTextBrushKey);
                    listBox.Items.Add(listBoxItem);
                }

                tab.Content = listBox;
                PackageInfoTabControl.Items.Add(tab);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating preset category tab: {ex.Message}");
            }
        }
    }
}
