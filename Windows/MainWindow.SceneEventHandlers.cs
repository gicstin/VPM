using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Scene-related event handlers for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        private string _currentContentMode = "Packages";

        /// <summary>
        /// Handles the single content mode switch button click
        /// </summary>
        private void ContentModeSwitchButton_Click(object sender, RoutedEventArgs e)
        {
            // Cycle through Packages -> Scenes -> Presets -> Packages
            string newMode = _currentContentMode switch
            {
                "Packages" => "Scenes",
                "Scenes" => "Presets",
                "Presets" => "Packages",
                _ => "Packages"
            };
            SwitchContentMode(newMode);
        }

        /// <summary>
        /// Handles content mode button clicks (Packages vs Scenes)
        /// </summary>
        private void ContentModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string mode)
            {
                SwitchContentMode(mode);
            }
        }

        /// <summary>
        /// Switches between Packages, Scenes, and Presets content mode
        /// </summary>
        private void SwitchContentMode(string mode)
        {
            if (_currentContentMode == mode)
                return;

            _currentContentMode = mode;

            // Update the mode switch button text to show the next mode
            if (ContentModeSwitchButton != null)
            {
                string nextMode = mode switch
                {
                    "Packages" => "Scenes",
                    "Scenes" => "Presets",
                    "Presets" => "Packages",
                    _ => "Packages"
                };
                
                string icon = nextMode switch
                {
                    "Packages" => "📦",
                    "Scenes" => "🎬",
                    "Presets" => "🎨",
                    _ => "📦"
                };
                
                ContentModeSwitchButton.Content = $"{icon} {nextMode}";
            }

            // Update button styles
            if (mode == "Packages")
            {
                PackageDataGrid.Visibility = Visibility.Visible;
                ScenesDataGrid.Visibility = Visibility.Collapsed;
                
                // Show package search, hide scene search
                PackageSearchBox.Visibility = Visibility.Visible;
                PackageSearchClearButton.Visibility = PackageSearchBox.Text != "📦 Filter packages, descriptions, tags..." ? Visibility.Visible : Visibility.Collapsed;
                SceneSearchBox.Visibility = Visibility.Collapsed;
                SceneSearchClearButton.Visibility = Visibility.Collapsed;
                
                // Make sorting button context-aware for packages
                PackageSortButton.IsEnabled = true;
                
                // Enable Favorite and AutoInstall buttons in packages mode, hide Hide button
                if (FavoriteToggleButton != null)
                    FavoriteToggleButton.IsEnabled = true;
                if (AutoInstallToggleButton != null)
                    AutoInstallToggleButton.Visibility = Visibility.Visible;
                if (HideToggleButton != null)
                    HideToggleButton.Visibility = Visibility.Collapsed;
                
                // Show both dependencies and dependents tabs in packages mode
                DependenciesTabsContainer.Visibility = Visibility.Visible;
                DependentsTab.Visibility = Visibility.Visible;
                DependentsTabColumn.Width = new GridLength(1, GridUnitType.Star);
                DependenciesTab.Margin = new Thickness(0, 0, 1, 0);
                
                // Show package filters, hide scene filters
                if (PackageFiltersContainer != null)
                    PackageFiltersContainer.Visibility = Visibility.Visible;
                if (SceneTypeFilterSection != null)
                    SceneTypeFilterSection.Visibility = Visibility.Collapsed;
                if (SceneCreatorFilterSection != null)
                    SceneCreatorFilterSection.Visibility = Visibility.Collapsed;
                if (SceneSourceFilterSection != null)
                    SceneSourceFilterSection.Visibility = Visibility.Collapsed;
                if (SceneTypeFilterSplitter != null)
                    SceneTypeFilterSplitter.Visibility = Visibility.Collapsed;
                if (SceneCreatorFilterSplitter != null)
                    SceneCreatorFilterSplitter.Visibility = Visibility.Collapsed;
                if (SceneSourceFilterSplitter != null)
                    SceneSourceFilterSplitter.Visibility = Visibility.Collapsed;
            }
            else if (mode == "Scenes")
            {
                PackageDataGrid.Visibility = Visibility.Collapsed;
                ScenesDataGrid.Visibility = Visibility.Visible;
                
                // Show scene search, hide package search
                PackageSearchBox.Visibility = Visibility.Collapsed;
                PackageSearchClearButton.Visibility = Visibility.Collapsed;
                SceneSearchBox.Visibility = Visibility.Visible;
                SceneSearchClearButton.Visibility = SceneSearchBox.Text != "🎬 Filter scenes by name, creator, type..." ? Visibility.Visible : Visibility.Collapsed;

                // Enable sorting for scenes mode
                PackageSortButton.IsEnabled = true;
                PackageSortButton.ToolTip = "Sort scenes";

                // Enable Favorite button in scene mode, hide AutoInstall button and show Hide button
                if (FavoriteToggleButton != null)
                    FavoriteToggleButton.IsEnabled = true;
                if (AutoInstallToggleButton != null)
                    AutoInstallToggleButton.Visibility = Visibility.Collapsed;
                if (HideToggleButton != null)
                {
                    HideToggleButton.Visibility = Visibility.Visible;
                    HideToggleButton.IsEnabled = true;
                }

                // Show dependencies tabs but hide Dependents tab in scenes mode
                DependenciesTabsContainer.Visibility = Visibility.Visible;
                DependentsTab.Visibility = Visibility.Collapsed;
                DependentsTabColumn.Width = new GridLength(0);
                DependenciesTab.Margin = new Thickness(0);
                
                // Hide package filters, show scene filters
                if (PackageFiltersContainer != null)
                    PackageFiltersContainer.Visibility = Visibility.Collapsed;
                if (SceneTypeFilterSection != null)
                    SceneTypeFilterSection.Visibility = Visibility.Visible;
                if (SceneCreatorFilterSection != null)
                    SceneCreatorFilterSection.Visibility = Visibility.Visible;
                if (SceneSourceFilterSection != null)
                    SceneSourceFilterSection.Visibility = Visibility.Visible;
                if (SceneTypeFilterSplitter != null)
                    SceneTypeFilterSplitter.Visibility = Visibility.Visible;
                if (SceneCreatorFilterSplitter != null)
                    SceneCreatorFilterSplitter.Visibility = Visibility.Visible;
                if (SceneSourceFilterSplitter != null)
                    SceneSourceFilterSplitter.Visibility = Visibility.Visible;

                // Load scenes if not already loaded
                if (Scenes.Count == 0)
                {
                    _ = LoadScenesAsync();
                }
                else
                {
                    // Populate scene filters if scenes are already loaded
                    PopulateSceneTypeFilter();
                    PopulateSceneCreatorFilter();
                    PopulateSceneSourceFilter();
                }
            }
            else if (mode == "Presets")
            {
                // Show custom atom data grid, hide others
                PackageDataGrid.Visibility = Visibility.Collapsed;
                ScenesDataGrid.Visibility = Visibility.Collapsed;
                if (CustomAtomDataGrid != null)
                    CustomAtomDataGrid.Visibility = Visibility.Visible;
                
                // Show custom atom search box, hide others
                PackageSearchBox.Visibility = Visibility.Collapsed;
                PackageSearchClearButton.Visibility = Visibility.Collapsed;
                SceneSearchBox.Visibility = Visibility.Collapsed;
                SceneSearchClearButton.Visibility = Visibility.Collapsed;
                if (CustomAtomSearchBox != null)
                    CustomAtomSearchBox.Visibility = Visibility.Visible;
                if (CustomAtomSearchClearButton != null)
                    CustomAtomSearchClearButton.Visibility = Visibility.Collapsed;
                
                // Enable sorting button
                PackageSortButton.IsEnabled = true;
                PackageSortButton.ToolTip = "Sort presets";
                
                // Enable Favorite and Hide buttons in presets mode
                if (FavoriteToggleButton != null)
                    FavoriteToggleButton.IsEnabled = true;
                if (AutoInstallToggleButton != null)
                    AutoInstallToggleButton.Visibility = Visibility.Collapsed;
                if (HideToggleButton != null)
                {
                    HideToggleButton.Visibility = Visibility.Visible;
                    HideToggleButton.IsEnabled = true;
                }
                
                // Hide dependencies tabs
                DependenciesTabsContainer.Visibility = Visibility.Collapsed;
                DependentsTab.Visibility = Visibility.Collapsed;
                
                // Hide all filters
                if (PackageFiltersContainer != null)
                    PackageFiltersContainer.Visibility = Visibility.Collapsed;
                if (SceneTypeFilterSection != null)
                    SceneTypeFilterSection.Visibility = Visibility.Collapsed;
                if (SceneCreatorFilterSection != null)
                    SceneCreatorFilterSection.Visibility = Visibility.Collapsed;
                if (SceneSourceFilterSection != null)
                    SceneSourceFilterSection.Visibility = Visibility.Collapsed;
                if (SceneTypeFilterSplitter != null)
                    SceneTypeFilterSplitter.Visibility = Visibility.Collapsed;
                if (SceneCreatorFilterSplitter != null)
                    SceneCreatorFilterSplitter.Visibility = Visibility.Collapsed;
                if (SceneSourceFilterSplitter != null)
                    SceneSourceFilterSplitter.Visibility = Visibility.Collapsed;

                // Load presets if not already loaded
                if (CustomAtomItems.Count == 0)
                {
                    _ = LoadCustomAtomItemsAsync();
                }
            }
        }

        /// <summary>
        /// Handles scenes data grid selection changed
        /// </summary>
        private void ScenesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Update toolbar buttons and optimize counter
            UpdateToolbarButtons();
            UpdateOptimizeCounter();
            
            if (ScenesDataGrid.SelectedItems.Count == 0)
            {
                Dependencies.Clear();
                DependenciesCountText.Text = "(0)";
                ClearCategoryTabs();
                ClearImageGrid();
                SetStatus("No scenes selected");
                return;
            }

            // Accumulate dependencies from all selected scenes
            Dependencies.Clear();
            _originalDependencies.Clear();
            var allDependencies = new HashSet<string>(); // Use HashSet to avoid duplicates
            var allScenes = new List<SceneItem>();
            int totalAtoms = 0;
            int totalDependencies = 0;

            foreach (var selectedItem in ScenesDataGrid.SelectedItems)
            {
                var scene = selectedItem as SceneItem;
                if (scene != null)
                {
                    allScenes.Add(scene);
                    totalAtoms += scene.AtomCount;
                    totalDependencies += scene.Dependencies.Count;
                    foreach (var dep in scene.Dependencies)
                    {
                        allDependencies.Add(dep);
                    }
                }
            }

            // Process accumulated dependencies
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

            // Update dependencies count
            DependenciesCountText.Text = $"({Dependencies.Count})";

            // Display thumbnails for all selected scenes in the image grid
            DisplayMultipleSceneThumbnails(allScenes);

            // Populate package breakdown tabs with combined scene content
            PopulateMultipleSceneContentTabs(allScenes);

            // Update the details area to show scene info
            UpdatePackageButtonBar();

            // Don't update title bar status for scene selection - only update placeholder text at bottom
        }

        /// <summary>
        /// Displays a scene thumbnail in the image grid
        /// </summary>
        private void DisplaySceneThumbnail(SceneItem scene)
        {
            try
            {
                // Clear existing images
                ImagesPanel.Children.Clear();

                if (string.IsNullOrEmpty(scene.ThumbnailPath) || !System.IO.File.Exists(scene.ThumbnailPath))
                    return;

                // Create image element
                var image = new System.Windows.Controls.Image
                {
                    Source = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(scene.ThumbnailPath, System.UriKind.Absolute)),
                    Stretch = System.Windows.Media.Stretch.UniformToFill,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both
                };

                // Wrap image in a border with rounded corners for consistency
                var imageBorder = new Border
                {
                    Child = image,
                    CornerRadius = new CornerRadius(UI_CORNER_RADIUS),
                    ClipToBounds = true,
                    Margin = new System.Windows.Thickness(4),
                    Background = Brushes.Transparent
                };

                // Apply clip geometry that updates with size changes
                void ApplyClipGeometry(Border border)
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

                imageBorder.Loaded += (s, e) => ApplyClipGeometry(s as Border);
                imageBorder.SizeChanged += (s, e) => ApplyClipGeometry(s as Border);

                // Add to grid
                ImagesPanel.Children.Add(imageBorder);
            }
            catch
            {
                // Error displaying thumbnail - silently handled
            }
        }

        /// <summary>
        /// Displays thumbnails for multiple scenes in the image grid
        /// </summary>
        private void DisplayMultipleSceneThumbnails(List<SceneItem> scenes)
        {
            try
            {
                // Clear existing images
                ImagesPanel.Children.Clear();

                if (scenes == null || scenes.Count == 0)
                    return;

                // Display thumbnail for each selected scene
                foreach (var scene in scenes)
                {
                    if (string.IsNullOrEmpty(scene.ThumbnailPath) || !System.IO.File.Exists(scene.ThumbnailPath))
                        continue;

                    // Create image element
                    var image = new System.Windows.Controls.Image
                    {
                        Source = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(scene.ThumbnailPath, System.UriKind.Absolute)),
                        Stretch = System.Windows.Media.Stretch.UniformToFill,
                        StretchDirection = System.Windows.Controls.StretchDirection.Both,
                        ToolTip = scene.Name
                    };

                    // Wrap image in a border with rounded corners for consistency
                    var imageBorder = new Border
                    {
                        Child = image,
                        CornerRadius = new CornerRadius(UI_CORNER_RADIUS),
                        ClipToBounds = true,
                        Margin = new System.Windows.Thickness(4),
                        Background = Brushes.Transparent
                    };

                    // Apply clip geometry that updates with size changes
                    void ApplyClipGeometry(Border border)
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

                    imageBorder.Loaded += (s, e) => ApplyClipGeometry(s as Border);
                    imageBorder.SizeChanged += (s, e) => ApplyClipGeometry(s as Border);

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
        /// Clears the image grid
        /// </summary>
        private void ClearImageGrid()
        {
            ImagesPanel.Children.Clear();
        }

        /// <summary>
        /// Populates the package breakdown tabs with scene content (hair, clothing, morphs, atoms)
        /// </summary>
        private void PopulateSceneContentTabs(SceneItem scene)
        {
            ClearCategoryTabs();

            // Create a dictionary to hold categorized content
            var categoryContent = new Dictionary<string, List<string>>();

            // Add hair items
            if (scene.HairItems != null && scene.HairItems.Count > 0)
            {
                categoryContent["Hair"] = scene.HairItems;
            }

            // Add clothing items
            if (scene.ClothingItems != null && scene.ClothingItems.Count > 0)
            {
                categoryContent["Clothing"] = scene.ClothingItems;
            }

            // Add morph items
            if (scene.MorphItems != null && scene.MorphItems.Count > 0)
            {
                categoryContent["Morphs"] = scene.MorphItems;
            }

            // Add atom types if available
            if (scene.AtomTypes != null && scene.AtomTypes.Count > 0)
            {
                categoryContent["Atoms"] = scene.AtomTypes;
            }

            // Create tabs for each category
            foreach (var kvp in categoryContent.OrderBy(c => c.Key))
            {
                CreateSceneContentTab(kvp.Key, kvp.Value, scene);
            }
        }

        /// <summary>
        /// Populates the package breakdown tabs with combined content from multiple scenes
        /// </summary>
        private void PopulateMultipleSceneContentTabs(List<SceneItem> scenes)
        {
            ClearCategoryTabs();

            if (scenes == null || scenes.Count == 0)
                return;

            // Create a dictionary to hold categorized content with deduplication
            var categoryContent = new Dictionary<string, HashSet<string>>();

            // Accumulate content from all selected scenes
            foreach (var scene in scenes)
            {
                // Add hair items
                if (scene.HairItems != null && scene.HairItems.Count > 0)
                {
                    if (!categoryContent.ContainsKey("Hair"))
                        categoryContent["Hair"] = new HashSet<string>();
                    foreach (var item in scene.HairItems)
                        categoryContent["Hair"].Add(item);
                }

                // Add clothing items
                if (scene.ClothingItems != null && scene.ClothingItems.Count > 0)
                {
                    if (!categoryContent.ContainsKey("Clothing"))
                        categoryContent["Clothing"] = new HashSet<string>();
                    foreach (var item in scene.ClothingItems)
                        categoryContent["Clothing"].Add(item);
                }

                // Add morph items
                if (scene.MorphItems != null && scene.MorphItems.Count > 0)
                {
                    if (!categoryContent.ContainsKey("Morphs"))
                        categoryContent["Morphs"] = new HashSet<string>();
                    foreach (var item in scene.MorphItems)
                        categoryContent["Morphs"].Add(item);
                }

                // Add atom types if available
                if (scene.AtomTypes != null && scene.AtomTypes.Count > 0)
                {
                    if (!categoryContent.ContainsKey("Atoms"))
                        categoryContent["Atoms"] = new HashSet<string>();
                    foreach (var item in scene.AtomTypes)
                        categoryContent["Atoms"].Add(item);
                }
            }

            // Create tabs for each category
            foreach (var kvp in categoryContent.OrderBy(c => c.Key))
            {
                var itemsList = kvp.Value.OrderBy(i => i).ToList();
                CreateSceneContentTab(kvp.Key, itemsList, null);
            }
        }

        /// <summary>
        /// Creates a tab for scene content (hair, clothing, morphs, atoms)
        /// </summary>
        private void CreateSceneContentTab(string category, List<string> items, SceneItem scene)
        {
            if (items == null || items.Count == 0)
                return;

            var tabItem = new TabItem
            {
                Header = $"{category} ({items.Count})",
                Style = PackageInfoTabControl.FindResource(typeof(TabItem)) as Style
            };

            var dataGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.None,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                RowHeaderWidth = 0,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                CanUserResizeRows = false,
                CanUserResizeColumns = true,
                CanUserSortColumns = false,
                BorderThickness = new Thickness(0),
                VerticalGridLinesBrush = Brushes.Transparent,
                RowHeight = double.NaN
            };

            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
            cellStyle.Setters.Add(new Setter(Control.VerticalAlignmentProperty, VerticalAlignment.Stretch));
            cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, FindResource(SystemColors.WindowBrushKey)));
            cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, FindResource(SystemColors.ControlTextBrushKey)));

            // Add trigger for selected cells
            var selectedTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource(SystemColors.HighlightBrushKey)));
            selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, FindResource(SystemColors.HighlightTextBrushKey)));
            cellStyle.Triggers.Add(selectedTrigger);

            // Add trigger for mouse over cells
            var mouseOverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            mouseOverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("ListBoxHoverBrush")));
            cellStyle.Triggers.Add(mouseOverTrigger);

            var templateColumn = new DataGridTemplateColumn
            {
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellStyle = cellStyle
            };

            var cellTemplate = new DataTemplate();
            var textBlockFactory = new FrameworkElementFactory(typeof(TextBlock));
            textBlockFactory.SetValue(TextBlock.TextProperty, new Binding("Content"));
            textBlockFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            textBlockFactory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            textBlockFactory.SetValue(TextBlock.FontSizeProperty, 13.0);
            textBlockFactory.SetValue(TextBlock.PaddingProperty, new Thickness(4, 2, 4, 2));
            textBlockFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

            cellTemplate.VisualTree = textBlockFactory;
            templateColumn.CellTemplate = cellTemplate;

            dataGrid.Columns.Add(templateColumn);

            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, FindResource(SystemColors.WindowBrushKey)));
            rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, FindResource(SystemColors.ControlTextBrushKey)));
            dataGrid.RowStyle = rowStyle;

            var contentItems = new List<SceneContentItem>();
            foreach (var item in items.OrderBy(i => i))
            {
                contentItems.Add(new SceneContentItem { Content = item });
            }

            dataGrid.ItemsSource = contentItems;

            var contextMenu = new ContextMenu();

            var copyItem = new MenuItem { Header = "Copy" };
            copyItem.Click += (s, e) => CopySceneContent(dataGrid);
            contextMenu.Items.Add(copyItem);

            ApplyContextMenuStyling(contextMenu);
            dataGrid.ContextMenu = contextMenu;

            tabItem.Content = dataGrid;
            PackageInfoTabControl.Items.Add(tabItem);
        }

        /// <summary>
        /// Copies selected scene content to clipboard
        /// </summary>
        private void CopySceneContent(DataGrid dataGrid)
        {
            if (dataGrid.SelectedItems.Count > 0)
            {
                try
                {
                    var items = new System.Text.StringBuilder();
                    foreach (var item in dataGrid.SelectedItems)
                    {
                        if (item is SceneContentItem contentItem)
                        {
                            items.AppendLine(contentItem.Content);
                        }
                    }

                    if (items.Length > 0)
                    {
                        Clipboard.SetText(items.ToString().TrimEnd());
                        SetStatus($"Copied {dataGrid.SelectedItems.Count} item(s) to clipboard");
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Handles scene search box text changed
        /// </summary>
        private void SceneSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && this.IsLoaded)
            {
                var grayBrush = (System.Windows.Media.SolidColorBrush)FindResource(System.Windows.SystemColors.GrayTextBrushKey);
                bool isPlaceholder = textBox.Foreground.Equals(grayBrush);
                
                if (!isPlaceholder && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Filter the scenes list
                    FilterScenes(textBox.Text);
                    SceneSearchClearButton.Visibility = Visibility.Visible;
                }
                else if (isPlaceholder || string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Show all scenes when no filter
                    FilterScenes("");
                    SceneSearchClearButton.Visibility = Visibility.Collapsed;
                }
            }
        }

        /// <summary>
        /// Clears the scene search filter
        /// </summary>
        private void ClearSceneFilterButton_Click(object sender, RoutedEventArgs e)
        {
            SceneSearchBox.Text = "📝 Filter scenes by name, creator, type...";
            SceneSearchBox.Foreground = (System.Windows.Media.Brush)FindResource(System.Windows.SystemColors.GrayTextBrushKey);
            FilterScenes("");
            SceneSearchClearButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Updates the visibility of the scene search clear button
        /// </summary>
        private void UpdateSceneSearchClearButton()
        {
            if (SceneSearchBox == null || SceneSearchClearButton == null) return;
            
            var grayBrush = (System.Windows.Media.SolidColorBrush)FindResource(System.Windows.SystemColors.GrayTextBrushKey);
            bool hasText = !SceneSearchBox.Foreground.Equals(grayBrush) && !string.IsNullOrWhiteSpace(SceneSearchBox.Text);
            
            SceneSearchClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Populates the scene type filter list
        /// </summary>
        private void PopulateSceneTypeFilter()
        {
            if (SceneTypeFilterList == null || Scenes == null || Scenes.Count == 0)
                return;

            try
            {
                SceneTypeFilterList.Items.Clear();
                
                // Collect unique scene types
                var sceneTypes = new Dictionary<string, int>();
                foreach (var scene in Scenes)
                {
                    if (!string.IsNullOrEmpty(scene.SceneType))
                    {
                        if (sceneTypes.ContainsKey(scene.SceneType))
                            sceneTypes[scene.SceneType]++;
                        else
                            sceneTypes[scene.SceneType] = 1;
                    }
                }
                
                // Add to list box sorted alphabetically
                foreach (var kvp in sceneTypes.OrderBy(x => x.Key))
                {
                    SceneTypeFilterList.Items.Add($"{kvp.Key} ({kvp.Value})");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating scene type filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Populates the scene creator filter list
        /// </summary>
        private void PopulateSceneCreatorFilter()
        {
            if (SceneCreatorFilterList == null || Scenes == null || Scenes.Count == 0)
                return;

            try
            {
                SceneCreatorFilterList.Items.Clear();
                
                // Collect unique creators
                var creators = new Dictionary<string, int>();
                foreach (var scene in Scenes)
                {
                    if (!string.IsNullOrEmpty(scene.Creator))
                    {
                        if (creators.ContainsKey(scene.Creator))
                            creators[scene.Creator]++;
                        else
                            creators[scene.Creator] = 1;
                    }
                }
                
                // Add to list box sorted alphabetically
                foreach (var kvp in creators.OrderBy(x => x.Key))
                {
                    SceneCreatorFilterList.Items.Add($"{kvp.Key} ({kvp.Value})");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating scene creator filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Populates the scene source filter list
        /// </summary>
        private void PopulateSceneSourceFilter()
        {
            if (SceneSourceFilterList == null || Scenes == null || Scenes.Count == 0)
                return;

            try
            {
                SceneSourceFilterList.Items.Clear();
                
                // Collect unique sources
                var sources = new Dictionary<string, int>();
                foreach (var scene in Scenes)
                {
                    if (!string.IsNullOrEmpty(scene.Source))
                    {
                        if (sources.ContainsKey(scene.Source))
                            sources[scene.Source]++;
                        else
                            sources[scene.Source] = 1;
                    }
                }
                
                // Add to list box sorted alphabetically
                foreach (var kvp in sources.OrderBy(x => x.Key))
                {
                    var displayText = kvp.Key == "Local" ? $"📁 {kvp.Key} ({kvp.Value})" : $"📦 {kvp.Key} ({kvp.Value})";
                    SceneSourceFilterList.Items.Add(displayText);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating scene source filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles double-click on scene in the grid - opens folder and selects the scene file (Shift+Double-click)
        /// </summary>
        private void ScenesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Only handle if Shift is held
            if (!Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
                return;

            try
            {
                // Get the selected scene
                if (ScenesDataGrid.SelectedItem is SceneItem scene && !string.IsNullOrEmpty(scene.FilePath))
                {
                    // Check if file exists
                    if (System.IO.File.Exists(scene.FilePath))
                    {
                        // Open folder and select the file
                        OpenFolderAndSelectFile(scene.FilePath);
                        SetStatus($"Opened folder for: {scene.DisplayName}");
                        e.Handled = true;
                    }
                    else
                    {
                        SetStatus($"Scene file not found: {scene.FilePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to open scene folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles double-click on custom atom item in the grid - opens folder and selects the file (Shift+Double-click)
        /// </summary>
        private void CustomAtomDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Only handle if Shift is held
            if (!Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
                return;

            try
            {
                // Get the selected custom atom item
                if (CustomAtomDataGrid?.SelectedItem is CustomAtomItem item && !string.IsNullOrEmpty(item.FilePath))
                {
                    // Check if file exists
                    if (System.IO.File.Exists(item.FilePath))
                    {
                        // Open folder and select the file
                        OpenFolderAndSelectFile(item.FilePath);
                        SetStatus($"Opened folder for: {item.DisplayName}");
                        e.Handled = true;
                    }
                    else
                    {
                        SetStatus($"Custom atom file not found: {item.FilePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to open custom atom folder: {ex.Message}");
            }
        }
    }
}

