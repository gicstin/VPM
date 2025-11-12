using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
            System.Diagnostics.Debug.WriteLine("[CUSTOM ATOM] LoadCustomAtomItemsAsync called");
            
            if (_customAtomPersonScanner == null)
            {
                System.Diagnostics.Debug.WriteLine("[CUSTOM ATOM] Scanner is null!");
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    System.Diagnostics.Debug.WriteLine("[CUSTOM ATOM] Starting scan...");
                    var items = _customAtomPersonScanner.ScanCustomAtomPerson();
                    System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] Scan returned {items.Count} items");
                    
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
                        System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] UI updated with {items.Count} items");
                    });
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] Exception: {ex.Message}");
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
            if (CustomAtomDataGrid?.SelectedItems.Count == 0)
            {
                Dependencies.Clear();
                DependenciesCountText.Text = "(0)";
                ClearCategoryTabs();
                ClearImageGrid();
                SetStatus("No custom atom items selected");
                return;
            }

            // Display thumbnails for selected items
            var selectedItems = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>()?.ToList() ?? new List<CustomAtomItem>();
            DisplayCustomAtomThumbnails(selectedItems);

            // Update the details area
            UpdatePackageButtonBar();

            SetStatus($"Selected {selectedItems.Count} custom atom item(s)");
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
    }
}
