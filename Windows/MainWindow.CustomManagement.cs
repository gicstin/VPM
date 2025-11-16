using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        /// <summary>
        /// Loads all custom atom person files from the Custom\Atom\Person folder
        /// </summary>
        public async Task LoadCustomAtomItemsAsync()
        {
            if (_customAtomPersonScanner == null)
            {
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    var items = _customAtomPersonScanner.ScanCustomAtomPerson();
                    
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // Store original items for filtering
                        _originalCustomAtomItems = new List<CustomAtomItem>(items);
                        
                        // Check if each item is marked as favorite or hidden
                        foreach (var item in items)
                        {
                            // For custom atoms, favorites are stored as .vap.fav
                            var favPath = item.FilePath + ".fav";
                            item.IsFavorite = File.Exists(favPath);
                            
                            // For custom atoms, hidden items are stored as .vap.hide
                            var hidePath = item.FilePath + ".hide";
                            item.IsHidden = File.Exists(hidePath);
                        }
                        
                        CustomAtomItems.ReplaceAll(items);
                        SetStatus($"Loaded {items.Count} custom atom item(s)");
                        
                        // Populate preset filters if we're in Presets mode
                        if (_currentContentMode == "Presets")
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
                SetStatus($"Error loading custom atom items: {ex.Message}");
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
        /// Filters custom atom items by search text
        /// </summary>
        private void FilterCustomAtomItems(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                CustomAtomItems.ReplaceAll(_originalCustomAtomItems);
            }
            else
            {
                var filtered = _originalCustomAtomItems
                    .Where(item => item.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                                   item.Category.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                                   item.Subfolder.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                CustomAtomItems.ReplaceAll(filtered);
            }
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

                    // Set opacity to 0 before animating to ensure animation runs
                    if (DependenciesDataGrid != null)
                        DependenciesDataGrid.Opacity = 0;
                    if (ImagesPanel != null)
                        ImagesPanel.Opacity = 0;

                    // Snap in dependencies and images after update with smooth effect (prevents flicker on rapid switches)
                    if (DependenciesDataGrid != null && Dependencies.Count > 0)
                    {
                        AnimationHelper.SnapInSmooth(DependenciesDataGrid, 250);
                    }
                    if (ImagesPanel != null && ImagesPanel.Children.Count > 0)
                    {
                        AnimationHelper.SnapInSmooth(ImagesPanel, 250);
                    }

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
                ImagesPanel.Children.Clear();

                if (items == null || items.Count == 0)
                    return;

                // Display thumbnail for each selected item
                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.ThumbnailPath) || !System.IO.File.Exists(item.ThumbnailPath))
                        continue;

                    // Create image element
                    var image = new System.Windows.Controls.Image
                    {
                        Source = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(item.ThumbnailPath, System.UriKind.Absolute)),
                        Stretch = System.Windows.Media.Stretch.UniformToFill,
                        StretchDirection = System.Windows.Controls.StretchDirection.Both,
                        ToolTip = item.Name
                    };

                    // Wrap image in a border with rounded corners for consistency
                    var imageBorder = new System.Windows.Controls.Border
                    {
                        Child = image,
                        CornerRadius = new System.Windows.CornerRadius(UI_CORNER_RADIUS),
                        ClipToBounds = true,
                        Margin = new System.Windows.Thickness(4),
                        Background = System.Windows.Media.Brushes.Transparent
                    };

                    // Apply clip geometry that updates with size changes
                    void ApplyClipGeometry(System.Windows.Controls.Border border)
                    {
                        if (border != null && border.ActualWidth > 0 && border.ActualHeight > 0)
                        {
                            border.Clip = new System.Windows.Media.RectangleGeometry
                            {
                                RadiusX = UI_CORNER_RADIUS,
                                RadiusY = UI_CORNER_RADIUS,
                                Rect = new System.Windows.Rect(0, 0, border.ActualWidth, border.ActualHeight)
                            };
                        }
                    }

                    imageBorder.Loaded += (s, e) => ApplyClipGeometry(s as System.Windows.Controls.Border);
                    imageBorder.SizeChanged += (s, e) => ApplyClipGeometry(s as System.Windows.Controls.Border);

                    // Add to grid
                    ImagesPanel.Children.Add(imageBorder);
                }
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
                    // Extract base package name (remove version suffix)
                    // Dependencies come in format: "creator.package.version" or "creator.package" or "creator.package.latest"
                    string baseName = dep;
                    string version = "";
                    
                    // Check if it ends with .latest
                    if (dep.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                    {
                        baseName = dep.Substring(0, dep.Length - 7); // Remove .latest
                        version = "latest";
                    }
                    else
                    {
                        // Check for numeric version at the end (e.g., ".4" or ".13")
                        var lastDotIndex = dep.LastIndexOf('.');
                        if (lastDotIndex > 0)
                        {
                            var potentialVersion = dep.Substring(lastDotIndex + 1);
                            if (int.TryParse(potentialVersion, out _))
                            {
                                version = potentialVersion;
                                baseName = dep.Substring(0, lastDotIndex);
                            }
                        }
                    }
                    
                    // Get the actual status from package manager using base name
                    var status = _packageFileManager?.GetPackageStatus(baseName) ?? "Missing";
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

                var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
                var listBox = new ListBox
                {
                    Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(5)
                };

                foreach (var item in items)
                {
                    listBox.Items.Add(new ListBoxItem
                    {
                        Content = item,
                        Background = Brushes.Transparent,
                        Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220))
                    });
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
