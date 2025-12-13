using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// Scene-related event handlers for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        private string _currentContentMode = "Packages";

        /// <summary>
        /// Handles the content mode dropdown selection changed
        /// </summary>
        private void ContentModeDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                string content = selectedItem.Content.ToString();
                
                // Extract mode from content (e.g., "📦 Packages" -> "Packages")
                string newMode = content switch
                {
                    "📦 Packages" => "Packages",
                    "🎨 Custom" => "Custom",
                    _ => "Packages"
                };
                
                SwitchContentMode(newMode);
            }
        }

        /// <summary>
        /// Handles right-click on content mode dropdown to cycle to next mode
        /// </summary>
        private void ContentModeDropdown_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Get the next mode in the cycle: Packages -> Custom -> Packages
            string nextMode = _currentContentMode switch
            {
                "Packages" => "Custom",
                "Custom" => "Packages",
                _ => "Packages"
            };

            SwitchContentMode(nextMode);
            e.Handled = true;
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
        /// Switches between Packages and Custom (unified Presets + Scenes) content mode
        /// </summary>
        private void SwitchContentMode(string mode)
        {
            if (_currentContentMode == mode)
                return;

            _currentContentMode = mode;

            // Clear selections for all DataGrids
            if (PackageDataGrid != null)
                PackageDataGrid.SelectedItems.Clear();
            if (ScenesDataGrid != null)
                ScenesDataGrid.SelectedItems.Clear();
            if (CustomAtomDataGrid != null)
                CustomAtomDataGrid.SelectedItems.Clear();

            // Clear details area
            Dependencies.Clear();
            DependenciesCountText.Text = "(0)";
            ClearCategoryTabs();
            ClearImageGrid();

            // Update the dropdown to select the current mode
            if (ContentModeDropdown != null)
            {
                string displayText = mode switch
                {
                    "Packages" => "📦 Packages",
                    "Custom" => "🎨 Custom",
                    _ => "📦 Packages"
                };
                
                for (int i = 0; i < ContentModeDropdown.Items.Count; i++)
                {
                    if (ContentModeDropdown.Items[i] is ComboBoxItem item && item.Content.ToString() == displayText)
                    {
                        ContentModeDropdown.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (mode == "Packages")
            {
                PackageDataGrid.Visibility = Visibility.Visible;
                ScenesDataGrid.Visibility = Visibility.Collapsed;
                if (CustomAtomDataGrid != null)
                    CustomAtomDataGrid.Visibility = Visibility.Collapsed;
                
                PackageSearchBox.Visibility = Visibility.Visible;
                PackageSearchBox.Text = "📦 Filter packages, descriptions, tags...";
                PackageSearchBox.Foreground = (System.Windows.Media.SolidColorBrush)FindResource(System.Windows.SystemColors.GrayTextBrushKey);
                PackageSearchClearButton.Visibility = Visibility.Collapsed;
                SceneSearchBox.Visibility = Visibility.Collapsed;
                SceneSearchClearButton.Visibility = Visibility.Collapsed;
                if (CustomAtomSearchBox != null)
                    CustomAtomSearchBox.Visibility = Visibility.Collapsed;
                if (CustomAtomSearchClearButton != null)
                    CustomAtomSearchClearButton.Visibility = Visibility.Collapsed;
                
                PackageSortButton.IsEnabled = true;
                
                if (FavoriteToggleButton != null)
                    FavoriteToggleButton.IsEnabled = true;
                if (AutoInstallToggleButton != null)
                    AutoInstallToggleButton.Visibility = Visibility.Visible;
                if (HideToggleButton != null)
                    HideToggleButton.Visibility = Visibility.Collapsed;
                
                DependenciesTabsContainer.Visibility = Visibility.Visible;
                DependentsTab.Visibility = Visibility.Visible;
                DependentsTabColumn.Width = new GridLength(1, GridUnitType.Star);
                DependenciesTab.Margin = new Thickness(0, 0, 1, 0);
                
                if (PackageFiltersContainer != null)
                    PackageFiltersContainer.Visibility = Visibility.Visible;
                if (SceneFiltersContainer != null)
                    SceneFiltersContainer.Visibility = Visibility.Collapsed;
                if (PresetFiltersContainer != null)
                    PresetFiltersContainer.Visibility = Visibility.Collapsed;

                if (_settingsManager?.Settings != null)
                {
                    ApplyFilterVisibilityStates(_settingsManager.Settings);
                    ApplyFilterPositions();
                }
                
                if (OptimizeToggleButton != null)
                {
                    OptimizeToggleButton.IsEnabled = true;
                    OptimizeToggleButton.Opacity = 1.0;
                    OptimizeToggleButton.ToolTip = "Optimize selected packages";
                }
            }
            else if (mode == "Custom")
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
                {
                    CustomAtomSearchBox.Visibility = Visibility.Visible;
                    CustomAtomSearchBox.Text = "🔍 Filter presets & scenes by name...";
                    CustomAtomSearchBox.Foreground = (System.Windows.Media.SolidColorBrush)FindResource(System.Windows.SystemColors.GrayTextBrushKey);
                }
                if (CustomAtomSearchClearButton != null)
                    CustomAtomSearchClearButton.Visibility = Visibility.Collapsed;
                
                PackageSortButton.IsEnabled = true;
                PackageSortButton.ToolTip = "Sort (Scroll to navigate)";
                
                if (FavoriteToggleButton != null)
                    FavoriteToggleButton.IsEnabled = true;
                if (AutoInstallToggleButton != null)
                    AutoInstallToggleButton.Visibility = Visibility.Collapsed;
                if (HideToggleButton != null)
                {
                    HideToggleButton.Visibility = Visibility.Visible;
                    HideToggleButton.IsEnabled = true;
                }
                
                DependenciesTabsContainer.Visibility = Visibility.Visible;
                DependentsTab.Visibility = Visibility.Collapsed;
                DependentsTabColumn.Width = new GridLength(0);
                DependenciesTab.Margin = new Thickness(0);
                
                if (PackageFiltersContainer != null)
                    PackageFiltersContainer.Visibility = Visibility.Collapsed;
                if (SceneFiltersContainer != null)
                    SceneFiltersContainer.Visibility = Visibility.Collapsed;
                if (PresetFiltersContainer != null)
                    PresetFiltersContainer.Visibility = Visibility.Visible;

                // Populate custom content filters
                if (CustomAtomItems.Count > 0)
                {
                    PopulatePresetCategoryFilter();
                    PopulatePresetSubfolderFilter();
                    PopulatePresetDateFilter();
                    PopulatePresetFileSizeFilter();
                    PopulatePresetStatusFilter();
                }

                // Load unified custom content if not already loaded
                if (CustomAtomItems.Count == 0)
                {
                    _ = LoadCustomAtomItemsAsync();
                }

                if (_settingsManager?.Settings != null)
                {
                    ApplyFilterVisibilityStates(_settingsManager.Settings);
                    ApplyFilterPositions();
                }
                
                if (OptimizeToggleButton != null)
                {
                    OptimizeToggleButton.IsEnabled = true;
                    OptimizeToggleButton.Opacity = 1.0;
                    OptimizeToggleButton.ToolTip = "Optimize selected items";
                }
            }
        }

        /// <summary>
        /// Handles scenes data grid selection changed
        /// </summary>
        private void ScenesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Update toolbar buttons and optimize counter immediately
            UpdateToolbarButtons();
            UpdateOptimizeCounter();
            UpdateFavoriteCounter();
            UpdateAutoinstallCounter();
            UpdateHideCounter();
            
            if (ScenesDataGrid.SelectedItems.Count == 0)
            {
                Dependencies.Clear();
                DependenciesCountText.Text = "(0)";
                ClearCategoryTabs();
                ClearImageGrid();
                SetStatus("No scenes selected");
                return;
            }

            // Cancel any pending scene selection update
            _sceneSelectionCts?.Cancel();
            _sceneSelectionCts?.Dispose();
            _sceneSelectionCts = new System.Threading.CancellationTokenSource();
            var sceneToken = _sceneSelectionCts.Token;

            // Trigger debounced scene selection handler
            _sceneSelectionDebouncer?.Trigger();

            // Schedule the actual content update after debounce delay
            _ = Task.Delay(SELECTION_DEBOUNCE_DELAY_MS, sceneToken).ContinueWith(_ =>
            {
                // Check if this operation was cancelled
                if (sceneToken.IsCancellationRequested)
                    return;

                Dispatcher.Invoke(() =>
                {
                    // Refresh package status index to ensure we have the latest status of all packages
                    // This is critical when switching scenes/presets after downloading dependencies
                    _packageFileManager?.RefreshPackageStatusIndex();

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

                    // Update dependencies count
                    DependenciesCountText.Text = $"({Dependencies.Count})";

                    // Display thumbnails for all selected scenes in the image grid
                    DisplayMultipleSceneThumbnails(allScenes);

                    // Populate package breakdown tabs with combined scene content
                    PopulateMultipleSceneContentTabs(allScenes);

                    // Update the details area to show scene info
                    UpdatePackageButtonBar();
                });
            });

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
                PreviewImages.Clear();

                if (string.IsNullOrEmpty(scene.ThumbnailPath) || !System.IO.File.Exists(scene.ThumbnailPath))
                    return;

                // Create image item
                var bitmap = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(scene.ThumbnailPath, System.UriKind.Absolute));
                
                // Create a dummy package item for grouping
                var scenePackage = new PackageItem
                {
                    Name = scene.Name,
                    Status = "Available"
                };
                
                PreviewImages.Add(new ImagePreviewItem
                {
                    Image = bitmap,
                    PackageName = scene.Name,
                    InternalPath = scene.ThumbnailPath,
                    PackageItem = scenePackage
                });
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
                PreviewImages.Clear();

                if (scenes == null || scenes.Count == 0)
                    return;

                // Create a dummy package item for grouping all scenes
                var scenesPackage = new PackageItem
                {
                    Name = "Selected Scenes",
                    Status = "Available"
                };

                // Display thumbnail for each selected scene
                foreach (var scene in scenes)
                {
                    if (string.IsNullOrEmpty(scene.ThumbnailPath) || !System.IO.File.Exists(scene.ThumbnailPath))
                        continue;

                    var bitmap = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(scene.ThumbnailPath, System.UriKind.Absolute));
                    
                    PreviewImages.Add(new ImagePreviewItem
                    {
                        Image = bitmap,
                        PackageName = scene.Name,
                        InternalPath = scene.ThumbnailPath,
                        PackageItem = scenesPackage
                    });
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
            PreviewImages.Clear();
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
                    string displayText = $"{kvp.Key} ({kvp.Value})";
                    SceneTypeFilterList.Items.Add(displayText);
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
                    string displayText = $"{kvp.Key} ({kvp.Value})";
                    SceneCreatorFilterList.Items.Add(displayText);
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
        /// Populates the preset category filter list
        /// </summary>
        private void PopulatePresetCategoryFilter()
        {
            if (PresetCategoryFilterList == null || CustomAtomItems == null || CustomAtomItems.Count == 0)
                return;

            try
            {
                PresetCategoryFilterList.Items.Clear();
                
                // Collect unique categories
                var categories = new Dictionary<string, int>();
                foreach (var item in CustomAtomItems)
                {
                    if (!string.IsNullOrEmpty(item.Category))
                    {
                        if (categories.ContainsKey(item.Category))
                            categories[item.Category]++;
                        else
                            categories[item.Category] = 1;
                    }
                }
                
                // Add to list box sorted alphabetically
                foreach (var kvp in categories.OrderBy(x => x.Key))
                {
                    PresetCategoryFilterList.Items.Add($"{kvp.Key} ({kvp.Value})");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating preset category filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Populates the preset subfolder filter list
        /// </summary>
        private void PopulatePresetSubfolderFilter()
        {
            if (PresetSubfolderFilterList == null || CustomAtomItems == null || CustomAtomItems.Count == 0)
                return;

            try
            {
                PresetSubfolderFilterList.Items.Clear();
                
                // Collect unique subfolders
                var subfolders = new Dictionary<string, int>();
                foreach (var item in CustomAtomItems)
                {
                    if (!string.IsNullOrEmpty(item.Subfolder))
                    {
                        if (subfolders.ContainsKey(item.Subfolder))
                            subfolders[item.Subfolder]++;
                        else
                            subfolders[item.Subfolder] = 1;
                    }
                }
                
                // Add to list box sorted alphabetically
                foreach (var kvp in subfolders.OrderBy(x => x.Key))
                {
                    PresetSubfolderFilterList.Items.Add($"{kvp.Key} ({kvp.Value})");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating preset subfolder filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Populates the scene date filter list with live counters
        /// </summary>
        private void PopulateSceneDateFilter()
        {
            if (SceneDateFilterList == null || Scenes == null || Scenes.Count == 0)
                return;

            try
            {
                SceneDateFilterList.Items.Clear();
                
                var now = DateTime.Now;
                var counts = new Dictionary<string, int>();
                
                // Count items in each date range
                foreach (var scene in Scenes)
                {
                    if (!scene.ModifiedDate.HasValue) continue;
                    var date = scene.ModifiedDate.Value;
                    
                    if (date >= now.AddDays(-7))
                        counts["📅 Last 7 days"] = counts.GetValueOrDefault("📅 Last 7 days", 0) + 1;
                    if (date >= now.AddDays(-30))
                        counts["📅 Last 30 days"] = counts.GetValueOrDefault("📅 Last 30 days", 0) + 1;
                    if (date >= now.AddMonths(-3))
                        counts["📅 Last 3 months"] = counts.GetValueOrDefault("📅 Last 3 months", 0) + 1;
                    if (date >= now.AddMonths(-6))
                        counts["📅 Last 6 months"] = counts.GetValueOrDefault("📅 Last 6 months", 0) + 1;
                    if (date >= now.AddYears(-1))
                        counts["📅 Last year"] = counts.GetValueOrDefault("📅 Last year", 0) + 1;
                    if (date < now.AddYears(-1))
                        counts["📅 Older than 1 year"] = counts.GetValueOrDefault("📅 Older than 1 year", 0) + 1;
                }
                
                // Add predefined date ranges with counts
                SceneDateFilterList.Items.Add($"📅 Last 7 days ({counts.GetValueOrDefault("📅 Last 7 days", 0)})");
                SceneDateFilterList.Items.Add($"📅 Last 30 days ({counts.GetValueOrDefault("📅 Last 30 days", 0)})");
                SceneDateFilterList.Items.Add($"📅 Last 3 months ({counts.GetValueOrDefault("📅 Last 3 months", 0)})");
                SceneDateFilterList.Items.Add($"📅 Last 6 months ({counts.GetValueOrDefault("📅 Last 6 months", 0)})");
                SceneDateFilterList.Items.Add($"📅 Last year ({counts.GetValueOrDefault("📅 Last year", 0)})");
                SceneDateFilterList.Items.Add($"📅 Older than 1 year ({counts.GetValueOrDefault("📅 Older than 1 year", 0)})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating scene date filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Populates the scene file size filter list with live counters
        /// </summary>
        private void PopulateSceneFileSizeFilter()
        {
            if (SceneFileSizeFilterList == null || Scenes == null || Scenes.Count == 0)
                return;

            try
            {
                SceneFileSizeFilterList.Items.Clear();
                
                var counts = new Dictionary<string, int>();
                
                // Count items in each size range
                foreach (var scene in Scenes)
                {
                    var fileSizeMB = scene.FileSize / (1024.0 * 1024.0);
                    
                    if (fileSizeMB < 1)
                        counts["💾 < 1 MB"] = counts.GetValueOrDefault("💾 < 1 MB", 0) + 1;
                    else if (fileSizeMB >= 1 && fileSizeMB <= 10)
                        counts["💾 1-10 MB"] = counts.GetValueOrDefault("💾 1-10 MB", 0) + 1;
                    else if (fileSizeMB > 10 && fileSizeMB <= 50)
                        counts["💾 10-50 MB"] = counts.GetValueOrDefault("💾 10-50 MB", 0) + 1;
                    else if (fileSizeMB > 50 && fileSizeMB <= 100)
                        counts["💾 50-100 MB"] = counts.GetValueOrDefault("💾 50-100 MB", 0) + 1;
                    else if (fileSizeMB > 100)
                        counts["💾 > 100 MB"] = counts.GetValueOrDefault("💾 > 100 MB", 0) + 1;
                }
                
                // Add predefined size ranges with counts
                SceneFileSizeFilterList.Items.Add($"💾 < 1 MB ({counts.GetValueOrDefault("💾 < 1 MB", 0)})");
                SceneFileSizeFilterList.Items.Add($"💾 1-10 MB ({counts.GetValueOrDefault("💾 1-10 MB", 0)})");
                SceneFileSizeFilterList.Items.Add($"💾 10-50 MB ({counts.GetValueOrDefault("💾 10-50 MB", 0)})");
                SceneFileSizeFilterList.Items.Add($"💾 50-100 MB ({counts.GetValueOrDefault("💾 50-100 MB", 0)})");
                SceneFileSizeFilterList.Items.Add($"💾 > 100 MB ({counts.GetValueOrDefault("💾 > 100 MB", 0)})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating scene file size filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Populates the preset date filter list with live counters
        /// </summary>
        private void PopulatePresetDateFilter()
        {
            if (PresetDateFilterList == null || CustomAtomItems == null || CustomAtomItems.Count == 0)
                return;

            try
            {
                PresetDateFilterList.Items.Clear();
                
                var now = DateTime.Now;
                var counts = new Dictionary<string, int>();
                
                // Count items in each date range
                foreach (var preset in CustomAtomItems)
                {
                    if (!preset.ModifiedDate.HasValue) continue;
                    var date = preset.ModifiedDate.Value;
                    
                    if (date >= now.AddDays(-7))
                        counts["📅 Last 7 days"] = counts.GetValueOrDefault("📅 Last 7 days", 0) + 1;
                    if (date >= now.AddDays(-30))
                        counts["📅 Last 30 days"] = counts.GetValueOrDefault("📅 Last 30 days", 0) + 1;
                    if (date >= now.AddMonths(-3))
                        counts["📅 Last 3 months"] = counts.GetValueOrDefault("📅 Last 3 months", 0) + 1;
                    if (date >= now.AddMonths(-6))
                        counts["📅 Last 6 months"] = counts.GetValueOrDefault("📅 Last 6 months", 0) + 1;
                    if (date >= now.AddYears(-1))
                        counts["📅 Last year"] = counts.GetValueOrDefault("📅 Last year", 0) + 1;
                    if (date < now.AddYears(-1))
                        counts["📅 Older than 1 year"] = counts.GetValueOrDefault("📅 Older than 1 year", 0) + 1;
                }
                
                // Add predefined date ranges with counts
                PresetDateFilterList.Items.Add($"📅 Last 7 days ({counts.GetValueOrDefault("📅 Last 7 days", 0)})");
                PresetDateFilterList.Items.Add($"📅 Last 30 days ({counts.GetValueOrDefault("📅 Last 30 days", 0)})");
                PresetDateFilterList.Items.Add($"📅 Last 3 months ({counts.GetValueOrDefault("📅 Last 3 months", 0)})");
                PresetDateFilterList.Items.Add($"📅 Last 6 months ({counts.GetValueOrDefault("📅 Last 6 months", 0)})");
                PresetDateFilterList.Items.Add($"📅 Last year ({counts.GetValueOrDefault("📅 Last year", 0)})");
                PresetDateFilterList.Items.Add($"📅 Older than 1 year ({counts.GetValueOrDefault("📅 Older than 1 year", 0)})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating preset date filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Populates the preset file size filter list with live counters
        /// </summary>
        private void PopulatePresetFileSizeFilter()
        {
            if (PresetFileSizeFilterList == null || CustomAtomItems == null || CustomAtomItems.Count == 0)
                return;

            try
            {
                PresetFileSizeFilterList.Items.Clear();
                
                var counts = new Dictionary<string, int>();
                
                // Count items in each size range
                foreach (var preset in CustomAtomItems)
                {
                    var fileSizeMB = preset.FileSize / (1024.0 * 1024.0);
                    
                    if (fileSizeMB < 1)
                        counts["💾 < 1 MB"] = counts.GetValueOrDefault("💾 < 1 MB", 0) + 1;
                    else if (fileSizeMB >= 1 && fileSizeMB <= 10)
                        counts["💾 1-10 MB"] = counts.GetValueOrDefault("💾 1-10 MB", 0) + 1;
                    else if (fileSizeMB > 10 && fileSizeMB <= 50)
                        counts["💾 10-50 MB"] = counts.GetValueOrDefault("💾 10-50 MB", 0) + 1;
                    else if (fileSizeMB > 50 && fileSizeMB <= 100)
                        counts["💾 50-100 MB"] = counts.GetValueOrDefault("💾 50-100 MB", 0) + 1;
                    else if (fileSizeMB > 100)
                        counts["💾 > 100 MB"] = counts.GetValueOrDefault("💾 > 100 MB", 0) + 1;
                }
                
                // Add predefined size ranges with counts
                PresetFileSizeFilterList.Items.Add($"💾 < 1 MB ({counts.GetValueOrDefault("💾 < 1 MB", 0)})");
                PresetFileSizeFilterList.Items.Add($"💾 1-10 MB ({counts.GetValueOrDefault("💾 1-10 MB", 0)})");
                PresetFileSizeFilterList.Items.Add($"💾 10-50 MB ({counts.GetValueOrDefault("💾 10-50 MB", 0)})");
                PresetFileSizeFilterList.Items.Add($"💾 50-100 MB ({counts.GetValueOrDefault("💾 50-100 MB", 0)})");
                PresetFileSizeFilterList.Items.Add($"💾 > 100 MB ({counts.GetValueOrDefault("💾 > 100 MB", 0)})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating preset file size filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Populates the scene status filter list with live counters
        /// </summary>
        private void PopulateSceneStatusFilter()
        {
            if (SceneStatusFilterList == null || Scenes == null)
                return;

            try
            {
                SceneStatusFilterList.Items.Clear();
                
                var counts = new Dictionary<string, int>();
                
                // Count items in each status category
                foreach (var scene in Scenes)
                {
                    if (scene.IsFavorite)
                        counts["❤️ Favorite"] = counts.GetValueOrDefault("❤️ Favorite", 0) + 1;
                    if (scene.IsHidden)
                        counts["🙈 Hidden"] = counts.GetValueOrDefault("🙈 Hidden", 0) + 1;
                    if (scene.IsOptimized)
                        counts["⚡ Optimized"] = counts.GetValueOrDefault("⚡ Optimized", 0) + 1;
                    else
                        counts["🔧 Unoptimized"] = counts.GetValueOrDefault("🔧 Unoptimized", 0) + 1;
                    if (!scene.IsFavorite && !scene.IsHidden)
                        counts["📁 Normal"] = counts.GetValueOrDefault("📁 Normal", 0) + 1;
                }
                
                // Add status options with counts
                SceneStatusFilterList.Items.Add($"❤️ Favorite ({counts.GetValueOrDefault("❤️ Favorite", 0)})");
                SceneStatusFilterList.Items.Add($"🙈 Hidden ({counts.GetValueOrDefault("🙈 Hidden", 0)})");
                SceneStatusFilterList.Items.Add($"⚡ Optimized ({counts.GetValueOrDefault("⚡ Optimized", 0)})");
                SceneStatusFilterList.Items.Add($"🔧 Unoptimized ({counts.GetValueOrDefault("🔧 Unoptimized", 0)})");
                SceneStatusFilterList.Items.Add($"📁 Normal ({counts.GetValueOrDefault("📁 Normal", 0)})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating scene status filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Populates the preset status filter list with live counters
        /// </summary>
        private void PopulatePresetStatusFilter()
        {
            if (PresetStatusFilterList == null || CustomAtomItems == null)
                return;

            try
            {
                PresetStatusFilterList.Items.Clear();
                
                var counts = new Dictionary<string, int>();
                
                // Count items in each status category
                foreach (var preset in CustomAtomItems)
                {
                    if (preset.IsFavorite)
                        counts["❤️ Favorite"] = counts.GetValueOrDefault("❤️ Favorite", 0) + 1;
                    if (preset.IsHidden)
                        counts["🙈 Hidden"] = counts.GetValueOrDefault("🙈 Hidden", 0) + 1;
                    if (!preset.IsFavorite && !preset.IsHidden)
                        counts["📁 Normal"] = counts.GetValueOrDefault("📁 Normal", 0) + 1;
                }
                
                // Add status options with counts
                PresetStatusFilterList.Items.Add($"❤️ Favorite ({counts.GetValueOrDefault("❤️ Favorite", 0)})");
                PresetStatusFilterList.Items.Add($"🙈 Hidden ({counts.GetValueOrDefault("🙈 Hidden", 0)})");
                PresetStatusFilterList.Items.Add($"📁 Normal ({counts.GetValueOrDefault("📁 Normal", 0)})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating preset status filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles preset category filter text box text changed
        /// </summary>
        private void PresetCategoryFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && this.IsLoaded)
            {
                var grayBrush = (System.Windows.Media.SolidColorBrush)FindResource(System.Windows.SystemColors.GrayTextBrushKey);
                bool isPlaceholder = textBox.Foreground.Equals(grayBrush);
                
                if (!isPlaceholder && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Filter the preset category list
                    FilterPresetCategoryList(textBox.Text);
                    if (PresetCategoryClearButton != null)
                        PresetCategoryClearButton.Visibility = Visibility.Visible;
                }
                else if (isPlaceholder || string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Show all categories when no filter
                    FilterPresetCategoryList("");
                    if (PresetCategoryClearButton != null)
                        PresetCategoryClearButton.Visibility = Visibility.Collapsed;
                }
            }
        }

        /// <summary>
        /// Filters preset category list based on search text
        /// </summary>
        private void FilterPresetCategoryList(string searchText)
        {
            if (PresetCategoryFilterList == null)
                return;

            try
            {
                PresetCategoryFilterList.Items.Clear();
                
                // Collect unique categories
                var categories = new Dictionary<string, int>();
                foreach (var item in CustomAtomItems)
                {
                    if (!string.IsNullOrEmpty(item.Category))
                    {
                        if (categories.ContainsKey(item.Category))
                            categories[item.Category]++;
                        else
                            categories[item.Category] = 1;
                    }
                }
                
                // Filter and add to list box
                foreach (var kvp in categories.OrderBy(x => x.Key))
                {
                    if (string.IsNullOrWhiteSpace(searchText) || 
                        kvp.Key.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    {
                        PresetCategoryFilterList.Items.Add($"{kvp.Key} ({kvp.Value})");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error filtering preset category list: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles preset category filter list selection changed
        /// </summary>
        private void PresetCategoryFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PresetCategoryFilterList == null || CustomAtomItemsView == null)
                return;

            // Use CollectionView.Filter instead of ReplaceAll for O(1) memory
            ApplyPresetFilters();
        }

        /// <summary>
        /// Handles preset category sort button click
        /// </summary>
        private void PresetCategorySortButton_Click(object sender, RoutedEventArgs e)
        {
            if (PresetCategoryFilterList == null)
                return;

            try
            {
                // Toggle sort order
                var items = PresetCategoryFilterList.Items.Cast<string>().ToList();
                items.Reverse();
                
                PresetCategoryFilterList.Items.Clear();
                foreach (var item in items)
                {
                    PresetCategoryFilterList.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error sorting preset categories: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles preset subfolder filter text box text changed
        /// </summary>
        private void PresetSubfolderFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && this.IsLoaded)
            {
                var grayBrush = (System.Windows.Media.SolidColorBrush)FindResource(System.Windows.SystemColors.GrayTextBrushKey);
                bool isPlaceholder = textBox.Foreground.Equals(grayBrush);
                
                if (!isPlaceholder && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    FilterPresetSubfolderList(textBox.Text);
                }
                else if (string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Repopulate the full list when text is cleared
                    PopulatePresetSubfolderFilter();
                }
            }
        }

        /// <summary>
        /// Filters the preset subfolder list based on search text
        /// </summary>
        private void FilterPresetSubfolderList(string searchText)
        {
            if (PresetSubfolderFilterList == null || CustomAtomItems == null || CustomAtomItems.Count == 0)
                return;

            try
            {
                PresetSubfolderFilterList.Items.Clear();
                
                // Collect unique subfolders that match the search
                var subfolders = new Dictionary<string, int>();
                foreach (var item in CustomAtomItems)
                {
                    if (!string.IsNullOrEmpty(item.Subfolder) && 
                        item.Subfolder.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    {
                        if (subfolders.ContainsKey(item.Subfolder))
                            subfolders[item.Subfolder]++;
                        else
                            subfolders[item.Subfolder] = 1;
                    }
                }
                
                // Add filtered results to list box
                foreach (var kvp in subfolders.OrderBy(x => x.Key))
                {
                    PresetSubfolderFilterList.Items.Add($"{kvp.Key} ({kvp.Value})");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error filtering preset subfolder list: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles preset subfolder sort button click
        /// </summary>
        private void PresetSubfolderSortButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Reverse the current order
                var items = PresetSubfolderFilterList.Items.Cast<string>().ToList();
                items.Reverse();
                
                PresetSubfolderFilterList.Items.Clear();
                foreach (var item in items)
                {
                    PresetSubfolderFilterList.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error sorting preset subfolders: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles scene date filter list selection changed
        /// </summary>
        private void SceneDateFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySceneFilters();
        }

        /// <summary>
        /// Handles scene file size filter list selection changed
        /// </summary>
        private void SceneFileSizeFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySceneFilters();
        }

        /// <summary>
        /// Handles preset date filter list selection changed
        /// </summary>
        private void PresetDateFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyPresetFilters();
        }

        /// <summary>
        /// Handles preset file size filter list selection changed
        /// </summary>
        private void PresetFileSizeFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyPresetFilters();
        }

        /// <summary>
        /// Handles scene status filter list selection changed
        /// </summary>
        private void SceneStatusFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySceneFilters();
        }

        /// <summary>
        /// Handles preset status filter list selection changed
        /// </summary>
        private void PresetStatusFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyPresetFilters();
        }

        /// <summary>
        /// Handles preset subfolder filter list selection changed
        /// </summary>
        private void PresetSubfolderFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyPresetFilters();
        }

        /// <summary>
        /// Applies all active scene filters to the scenes collection
        /// </summary>
        private void ApplySceneFilters()
        {
            if (ScenesView == null) return;

            try
            {
                ScenesView.Filter = (item) =>
                {
                    if (item is not SceneItem scene) return false;

                    // Apply date filter
                    if (SceneDateFilterList?.SelectedItems.Count > 0)
                    {
                        if (!PassesDateFilter(scene.ModifiedDate, SceneDateFilterList.SelectedItems.Cast<string>()))
                            return false;
                    }

                    // Apply file size filter
                    if (SceneFileSizeFilterList?.SelectedItems.Count > 0)
                    {
                        if (!PassesFileSizeFilter(scene.FileSize, SceneFileSizeFilterList.SelectedItems.Cast<string>()))
                            return false;
                    }

                    // Apply status filter
                    if (SceneStatusFilterList?.SelectedItems.Count > 0)
                    {
                        if (!PassesStatusFilter(scene.IsFavorite, scene.IsHidden, scene.IsOptimized, SceneStatusFilterList.SelectedItems.Cast<string>()))
                            return false;
                    }

                    return true;
                };

                ScenesView.Refresh();
                
                // Refresh filter counters after applying filters
                RefreshSceneFilterCounters();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error applying scene filters: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies all active preset filters to the presets collection using CollectionView.Filter
        /// This is O(1) memory vs O(n) memory for ReplaceAll - filters hide items without copying
        /// </summary>
        private void ApplyPresetFilters()
        {
            if (CustomAtomItemsView == null) return;

            try
            {
                // Pre-compute selected categories for O(1) lookup (avoid repeated enumeration)
                HashSet<string> selectedCategories = null;
                if (PresetCategoryFilterList?.SelectedItems.Count > 0)
                {
                    selectedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in PresetCategoryFilterList.SelectedItems)
                    {
                        if (item is string categoryItem)
                        {
                            var parts = categoryItem.LastIndexOf('(');
                            if (parts > 0)
                            {
                                var categoryName = categoryItem.Substring(0, parts).Trim();
                                selectedCategories.Add(categoryName);
                            }
                        }
                    }
                }
                
                // Capture search text for closure
                var searchText = _customAtomSearchText;
                var hasSearchText = !string.IsNullOrWhiteSpace(searchText);

                CustomAtomItemsView.Filter = (item) =>
                {
                    if (item is not CustomAtomItem preset) return false;

                    // Apply search text filter
                    if (hasSearchText)
                    {
                        if (!VPM.Services.SearchHelper.ContainsSearch(preset.DisplayName, searchText) &&
                            !VPM.Services.SearchHelper.ContainsSearch(preset.Category, searchText) &&
                            !VPM.Services.SearchHelper.ContainsSearch(preset.Subfolder, searchText))
                            return false;
                    }

                    // Apply category filter
                    if (selectedCategories != null && selectedCategories.Count > 0)
                    {
                        if (!selectedCategories.Contains(preset.Category))
                            return false;
                    }

                    // Apply subfolder filter
                    if (PresetSubfolderFilterList?.SelectedItems.Count > 0)
                    {
                        if (!PassesSubfolderFilter(preset.Subfolder, PresetSubfolderFilterList.SelectedItems.Cast<string>()))
                            return false;
                    }

                    // Apply date filter
                    if (PresetDateFilterList?.SelectedItems.Count > 0)
                    {
                        if (!PassesDateFilter(preset.ModifiedDate, PresetDateFilterList.SelectedItems.Cast<string>()))
                            return false;
                    }

                    // Apply file size filter
                    if (PresetFileSizeFilterList?.SelectedItems.Count > 0)
                    {
                        if (!PassesFileSizeFilter(preset.FileSize, PresetFileSizeFilterList.SelectedItems.Cast<string>()))
                            return false;
                    }

                    // Apply status filter
                    if (PresetStatusFilterList?.SelectedItems.Count > 0)
                    {
                        if (!PassesStatusFilter(preset.IsFavorite, preset.IsHidden, false, PresetStatusFilterList.SelectedItems.Cast<string>()))
                            return false;
                    }

                    return true;
                };

                CustomAtomItemsView.Refresh();
                
                // Refresh filter counters after applying filters
                RefreshPresetFilterCounters();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error applying preset filters: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if an item passes the date filter
        /// </summary>
        private bool PassesDateFilter(DateTime? lastModified, IEnumerable<string> selectedFilters)
        {
            if (!lastModified.HasValue) return false;
            
            var now = DateTime.Now;
            var date = lastModified.Value;
            
            foreach (var filter in selectedFilters)
            {
                // Extract the filter type from the display string (remove count)
                var filterType = filter.Contains('(') ? filter.Substring(0, filter.LastIndexOf('(')).Trim() : filter;
                
                switch (filterType)
                {
                    case "📅 Last 7 days":
                        if (date >= now.AddDays(-7)) return true;
                        break;
                    case "📅 Last 30 days":
                        if (date >= now.AddDays(-30)) return true;
                        break;
                    case "📅 Last 3 months":
                        if (date >= now.AddMonths(-3)) return true;
                        break;
                    case "📅 Last 6 months":
                        if (date >= now.AddMonths(-6)) return true;
                        break;
                    case "📅 Last year":
                        if (date >= now.AddYears(-1)) return true;
                        break;
                    case "📅 Older than 1 year":
                        if (date < now.AddYears(-1)) return true;
                        break;
                }
            }
            
            return false;
        }

        /// <summary>
        /// Checks if an item passes the file size filter
        /// </summary>
        private bool PassesFileSizeFilter(long fileSizeBytes, IEnumerable<string> selectedFilters)
        {
            var fileSizeMB = fileSizeBytes / (1024.0 * 1024.0);
            
            foreach (var filter in selectedFilters)
            {
                // Extract the filter type from the display string (remove count)
                var filterType = filter.Contains('(') ? filter.Substring(0, filter.LastIndexOf('(')).Trim() : filter;
                
                switch (filterType)
                {
                    case "💾 < 1 MB":
                        if (fileSizeMB < 1) return true;
                        break;
                    case "💾 1-10 MB":
                        if (fileSizeMB >= 1 && fileSizeMB <= 10) return true;
                        break;
                    case "💾 10-50 MB":
                        if (fileSizeMB > 10 && fileSizeMB <= 50) return true;
                        break;
                    case "💾 50-100 MB":
                        if (fileSizeMB > 50 && fileSizeMB <= 100) return true;
                        break;
                    case "💾 > 100 MB":
                        if (fileSizeMB > 100) return true;
                        break;
                }
            }
            
            return false;
        }

        /// <summary>
        /// Checks if an item passes the status filter
        /// </summary>
        private bool PassesStatusFilter(bool isFavorite, bool isHidden, bool isOptimized, IEnumerable<string> selectedFilters)
        {
            foreach (var filter in selectedFilters)
            {
                // Extract the filter type from the display string (remove count)
                var filterType = filter.Contains('(') ? filter.Substring(0, filter.LastIndexOf('(')).Trim() : filter;
                
                switch (filterType)
                {
                    case "❤️ Favorite":
                        if (isFavorite) return true;
                        break;
                    case "🙈 Hidden":
                        if (isHidden) return true;
                        break;
                    case "⚡ Optimized":
                        if (isOptimized) return true;
                        break;
                    case "🔧 Unoptimized":
                        if (!isOptimized) return true;
                        break;
                    case "📁 Normal":
                        if (!isFavorite && !isHidden) return true;
                        break;
                }
            }
            
            return false;
        }

        /// <summary>
        /// Checks if an item passes the subfolder filter
        /// </summary>
        private bool PassesSubfolderFilter(string itemSubfolder, IEnumerable<string> selectedFilters)
        {
            if (string.IsNullOrEmpty(itemSubfolder)) return false;

            foreach (var filter in selectedFilters)
            {
                // Extract the filter type from the display string (remove count)
                var filterType = filter.Contains('(') ? filter.Substring(0, filter.LastIndexOf('(')).Trim() : filter;
                
                if (itemSubfolder.Equals(filterType, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            return false;
        }

        /// <summary>
        /// Refreshes scene filter counters to reflect current data
        /// </summary>
        private void RefreshSceneFilterCounters()
        {
            try
            {
                // Store current selections
                var dateSelections = SceneDateFilterList?.SelectedItems.Cast<string>().ToList() ?? new List<string>();
                var sizeSelections = SceneFileSizeFilterList?.SelectedItems.Cast<string>().ToList() ?? new List<string>();
                var statusSelections = SceneStatusFilterList?.SelectedItems.Cast<string>().ToList() ?? new List<string>();

                // Temporarily remove event handlers to prevent infinite recursion
                if (SceneDateFilterList != null)
                    SceneDateFilterList.SelectionChanged -= SceneDateFilterList_SelectionChanged;
                if (SceneFileSizeFilterList != null)
                    SceneFileSizeFilterList.SelectionChanged -= SceneFileSizeFilterList_SelectionChanged;
                if (SceneStatusFilterList != null)
                    SceneStatusFilterList.SelectionChanged -= SceneStatusFilterList_SelectionChanged;

                // Repopulate filters with updated counts
                PopulateSceneDateFilter();
                PopulateSceneFileSizeFilter();
                PopulateSceneStatusFilter();

                // Restore selections
                RestoreFilterSelections(SceneDateFilterList, dateSelections);
                RestoreFilterSelections(SceneFileSizeFilterList, sizeSelections);
                RestoreFilterSelections(SceneStatusFilterList, statusSelections);

                // Re-attach event handlers
                if (SceneDateFilterList != null)
                    SceneDateFilterList.SelectionChanged += SceneDateFilterList_SelectionChanged;
                if (SceneFileSizeFilterList != null)
                    SceneFileSizeFilterList.SelectionChanged += SceneFileSizeFilterList_SelectionChanged;
                if (SceneStatusFilterList != null)
                    SceneStatusFilterList.SelectionChanged += SceneStatusFilterList_SelectionChanged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error refreshing scene filter counters: {ex.Message}");
            }
        }

        /// <summary>
        /// Refreshes preset filter counters to reflect current data
        /// </summary>
        private void RefreshPresetFilterCounters()
        {
            try
            {
                // Store current selections
                var subfolderSelections = PresetSubfolderFilterList?.SelectedItems.Cast<string>().ToList() ?? new List<string>();
                var dateSelections = PresetDateFilterList?.SelectedItems.Cast<string>().ToList() ?? new List<string>();
                var sizeSelections = PresetFileSizeFilterList?.SelectedItems.Cast<string>().ToList() ?? new List<string>();
                var statusSelections = PresetStatusFilterList?.SelectedItems.Cast<string>().ToList() ?? new List<string>();

                // Temporarily remove event handlers to prevent infinite recursion
                if (PresetSubfolderFilterList != null)
                    PresetSubfolderFilterList.SelectionChanged -= PresetSubfolderFilterList_SelectionChanged;
                if (PresetDateFilterList != null)
                    PresetDateFilterList.SelectionChanged -= PresetDateFilterList_SelectionChanged;
                if (PresetFileSizeFilterList != null)
                    PresetFileSizeFilterList.SelectionChanged -= PresetFileSizeFilterList_SelectionChanged;
                if (PresetStatusFilterList != null)
                    PresetStatusFilterList.SelectionChanged -= PresetStatusFilterList_SelectionChanged;

                // Repopulate filters with updated counts
                PopulatePresetSubfolderFilter();
                PopulatePresetDateFilter();
                PopulatePresetFileSizeFilter();
                PopulatePresetStatusFilter();

                // Restore selections
                RestoreFilterSelections(PresetSubfolderFilterList, subfolderSelections);
                RestoreFilterSelections(PresetDateFilterList, dateSelections);
                RestoreFilterSelections(PresetFileSizeFilterList, sizeSelections);
                RestoreFilterSelections(PresetStatusFilterList, statusSelections);

                // Re-attach event handlers
                if (PresetSubfolderFilterList != null)
                    PresetSubfolderFilterList.SelectionChanged += PresetSubfolderFilterList_SelectionChanged;
                if (PresetDateFilterList != null)
                    PresetDateFilterList.SelectionChanged += PresetDateFilterList_SelectionChanged;
                if (PresetFileSizeFilterList != null)
                    PresetFileSizeFilterList.SelectionChanged += PresetFileSizeFilterList_SelectionChanged;
                if (PresetStatusFilterList != null)
                    PresetStatusFilterList.SelectionChanged += PresetStatusFilterList_SelectionChanged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error refreshing preset filter counters: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores filter selections after repopulating
        /// </summary>
        private void RestoreFilterSelections(ListBox listBox, List<string> previousSelections)
        {
            if (listBox == null || previousSelections == null || previousSelections.Count == 0) return;

            try
            {
                // Check if ListBox supports multiple selection
                bool supportsMultipleSelection = listBox.SelectionMode == SelectionMode.Multiple || listBox.SelectionMode == SelectionMode.Extended;
                
                if (supportsMultipleSelection)
                {
                    listBox.SelectedItems.Clear();
                }
                else
                {
                    listBox.SelectedItem = null;
                }
                
                foreach (var selection in previousSelections)
                {
                    // Find matching item (compare without count)
                    var filterType = selection.Contains('(') ? selection.Substring(0, selection.LastIndexOf('(')).Trim() : selection;
                    
                    foreach (var item in listBox.Items)
                    {
                        var itemString = item.ToString();
                        var itemType = itemString.Contains('(') ? itemString.Substring(0, itemString.LastIndexOf('(')).Trim() : itemString;
                        
                        if (itemType == filterType)
                        {
                            if (supportsMultipleSelection)
                            {
                                listBox.SelectedItems.Add(item);
                            }
                            else
                            {
                                // For single selection, just select the first match and break
                                listBox.SelectedItem = item;
                                return; // Exit after first selection in single mode
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error restoring filter selections: {ex.Message}");
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
        /// Handles double-click on custom atom item in the grid - opens folder and selects the file
        /// Double-clicking anywhere on the row opens the file location
        /// </summary>
        private void CustomAtomDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Check if the click originated from the edit textbox - if so, don't handle it
                var originalSource = e.OriginalSource as DependencyObject;
                if (originalSource != null)
                {
                    var current = originalSource;
                    while (current != null)
                    {
                        if (current is TextBox textBox && textBox.Name == "PresetEditTextBox")
                        {
                            // This is a click on the edit textbox - don't handle it
                            return;
                        }
                        
                        current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                    }
                }

                // Open the folder location
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

