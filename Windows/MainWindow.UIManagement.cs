using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// UI management functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        private string _selectedFolder = "";
        private string _currentTheme = "System";

        // Windows API for dark title bar
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int attrValue, int attrSize);
        
        private const uint DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        #region Selection Preservation
        
        /// <summary>
        /// SELECTION PRESERVATION SYSTEM - READ THIS BEFORE MODIFYING
        /// 
        /// This system ensures user selections persist across all UI operations.
        /// 
        /// ARCHITECTURE:
        /// 1. Capture selections by package name (not object reference)
        /// 2. Perform operations that may modify the DataGrid
        /// 3. Restore selections using Background priority (happens AFTER all other UI updates)
        /// 
        /// WHEN TO USE:
        /// - Use ExecuteWithPreservedSelections() for synchronous operations that call ApplyFilters()
        /// - Use ExecuteWithPreservedSelectionsAsync() for async operations
        /// - For Items.Refresh() calls: capture before refresh, restore with Background priority
        /// - For operations that modify the package list: capture AFTER modifications, restore last
        /// 
        /// CRITICAL RULES:
        /// 1. Always clear selections before restoring (prevents accumulation)
        /// 2. Use Background priority for restoration (ensures it happens LAST)
        /// 3. Capture selections AFTER list modifications (SyncPackageDisplayWithFilters, etc.)
        /// 4. Never capture inside a Dispatcher block that continues after scheduling restoration
        /// 
        /// EXAMPLES:
        /// 
        /// Simple operation with ApplyFilters():
        ///   ExecuteWithPreservedSelections(() => {
        ///       // modify data
        ///       ApplyFilters(); // rebuilds list
        ///   });
        /// 
        /// Items.Refresh() pattern:
        ///   var selectedNames = PreserveDataGridSelections();
        ///   PackageDataGrid.Items.Refresh();
        ///   Dispatcher.BeginInvoke(() => RestoreDataGridSelections(selectedNames), Background);
        /// 
        /// Complex async with list modifications:
        ///   await Dispatcher.InvokeAsync(() => {
        ///       selectedNames = PreserveDataGridSelections();
        ///       Items.Refresh();
        ///       SyncPackageDisplayWithFilters(); // may add/remove items
        ///   }, Normal);
        ///   await Dispatcher.InvokeAsync(() => {
        ///       RestoreDataGridSelections(selectedNames);
        ///   }, Background);
        /// </summary>
        
        /// <summary>
        /// Preserves current selections in the DataGrid and returns a list of selected package names.
        /// Thread-safe with null checks.
        /// </summary>
        private List<string> PreserveDataGridSelections()
        {
            if (PackageDataGrid?.SelectedItems == null)
                return new List<string>();
                
            return PackageDataGrid.SelectedItems.Cast<PackageItem>()
                .Select(p => p.Name)
                .ToList();
        }
        
        /// <summary>
        /// Restores selections in the DataGrid based on package names.
        /// Uses HashSet for O(1) lookup performance.
        /// IMPORTANT: Clears existing selections first to prevent accumulation.
        /// Suppresses selection events during restoration to prevent heavy processing.
        /// </summary>
        private void RestoreDataGridSelections(List<string> selectedPackageNames)
        {
            if (PackageDataGrid == null || selectedPackageNames == null || selectedPackageNames.Count == 0)
                return;
            
            var selectedNamesSet = new HashSet<string>(selectedPackageNames, StringComparer.OrdinalIgnoreCase);
            
            try
            {
                // Suppress selection events during restoration to prevent image loading and other heavy operations
                _suppressSelectionEvents = true;
                
                PackageDataGrid.SelectedItems.Clear();
                
                foreach (var item in PackageDataGrid.Items)
                {
                    if (item is PackageItem package && selectedNamesSet.Contains(package.Name))
                    {
                        PackageDataGrid.SelectedItems.Add(package);
                        selectedNamesSet.Remove(package.Name);
                        
                        if (selectedNamesSet.Count == 0)
                            break;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                // Re-enable selection events after restoration
                _suppressSelectionEvents = false;
            }
        }
        
        /// <summary>
        /// Executes a synchronous action while preserving DataGrid selections.
        /// Use this for operations that call ApplyFilters() or modify the package list.
        /// Restoration happens with Background priority to ensure it occurs AFTER all UI updates.
        /// </summary>
        private void ExecuteWithPreservedSelections(Action action)
        {
            var selectedNames = PreserveDataGridSelections();
            
            try
            {
                action();
            }
            finally
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RestoreDataGridSelections(selectedNames);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        
        /// <summary>
        /// Executes an async action while preserving DataGrid selections.
        /// Use this for async operations that need selection preservation.
        /// Restoration happens with Background priority to ensure it occurs AFTER all UI updates.
        /// </summary>
        private async Task ExecuteWithPreservedSelectionsAsync(Func<Task> action)
        {
            var selectedNames = PreserveDataGridSelections();
            
            try
            {
                await action();
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    RestoreDataGridSelections(selectedNames);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        
        /// <summary>
        /// Preserves current selections in the DependenciesDataGrid and returns a list of selected dependency names.
        /// Thread-safe with null checks.
        /// </summary>
        private List<string> PreserveDependenciesDataGridSelections()
        {
            if (DependenciesDataGrid?.SelectedItems == null)
                return new List<string>();
                
            return DependenciesDataGrid.SelectedItems.Cast<DependencyItem>()
                .Select(d => d.Name)
                .ToList();
        }
        
        /// <summary>
        /// Restores selections in the DependenciesDataGrid based on dependency names.
        /// Uses HashSet for O(1) lookup performance.
        /// IMPORTANT: Clears existing selections first to prevent accumulation.
        /// </summary>
        private void RestoreDependenciesDataGridSelections(List<string> selectedDependencyNames)
        {
            if (DependenciesDataGrid == null || selectedDependencyNames == null || selectedDependencyNames.Count == 0)
                return;
            
            var selectedNamesSet = new HashSet<string>(selectedDependencyNames, StringComparer.OrdinalIgnoreCase);
            
            try
            {
                DependenciesDataGrid.SelectedItems.Clear();
                
                foreach (var item in DependenciesDataGrid.Items)
                {
                    if (item is DependencyItem dependency && selectedNamesSet.Contains(dependency.Name))
                    {
                        DependenciesDataGrid.SelectedItems.Add(dependency);
                        selectedNamesSet.Remove(dependency.Name);
                        
                        if (selectedNamesSet.Count == 0)
                            break;
                    }
                }
            }
            catch
            {
            }
        }
        
        #endregion

        #region Console Window Management
        
        private void InitializeConsoleWindow()
        {
            string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string consoleFilePath = Path.Combine(exeDirectory, ".console");
            bool shouldShowConsole = File.Exists(consoleFilePath);
            
            var consoleWindow = GetConsoleWindow();
            
            if (consoleWindow == IntPtr.Zero)
            {
                if (shouldShowConsole)
                {
                    if (AllocConsole())
                    {
                        Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                        Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                        
                        Console.WriteLine("VPM - Debug Console");
                        Console.WriteLine("Console initialized. Debug messages will appear here.");
                        Console.WriteLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                    }
                }
            }
            else
            {
                ShowWindow(consoleWindow, shouldShowConsole ? SW_SHOW : SW_HIDE);
            }
        }
        
        #endregion

        #region Theme Management

        private void SwitchTheme(string themeName)
        {
            try
            {
                _currentTheme = themeName;
                
                // Update settings (this will trigger auto-save)
                _settingsManager.Settings.Theme = themeName;
                
                // Clear existing theme resources
                Application.Current.Resources.MergedDictionaries.Clear();
                
                // Load the appropriate theme
                var themeUri = themeName switch
                {
                    "Light" => new Uri("Themes/LightTheme.xaml", UriKind.Relative),
                    "Dark" => new Uri("Themes/DarkTheme.xaml", UriKind.Relative),
                    _ => new Uri("Themes/DarkTheme.xaml", UriKind.Relative) // Default to dark
                };
                
                var themeDict = new ResourceDictionary { Source = themeUri };
                Application.Current.Resources.MergedDictionaries.Add(themeDict);
                
                // Update menu checkmarks
                UpdateThemeMenuItems();
                
                // Apply dark title bar for dark theme
                if (themeName == "Dark")
                {
                    ApplyDarkTitleBar();
                }
                
                SetStatus($"Switched to {themeName} theme");
            }
            catch (Exception)
            {
            }
        }

        private void UpdateThemeMenuItems()
        {
            try
            {
                // Update menu item checkmarks based on current theme
                if (LightThemeMenuItem != null)
                    LightThemeMenuItem.IsChecked = _currentTheme == "Light";
                
                if (DarkThemeMenuItem != null)
                    DarkThemeMenuItem.IsChecked = _currentTheme == "Dark";
                
                if (SystemThemeMenuItem != null)
                    SystemThemeMenuItem.IsChecked = _currentTheme == "System";
            }
            catch (Exception)
            {
            }
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int darkMode = 1;
                    // Try Windows 11/10 20H1+ attribute first, then fall back to older Windows 10 attribute
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Settings Management

        /// <summary>
        /// Applies loaded settings to UI elements
        /// </summary>
        private void ApplySettingsToUI()
        {
            var settings = _settingsManager.Settings;
            
            // Apply theme
            SwitchTheme(settings.Theme);
            
            // Apply selected folder
            _selectedFolder = settings.SelectedFolder;
            
            // Apply image columns
            _imageColumns = settings.ImageColumns;
            
            
            // Apply cascade filtering setting
            _cascadeFiltering = settings.CascadeFiltering;
            
            // Apply hide archived packages setting
            if (_filterManager != null)
            {
                _filterManager.HideArchivedPackages = settings.HideArchivedPackages;
            }
            // Update menu items to show current state
            UpdateHideArchivedMenuItems(settings.HideArchivedPackages);
            
            // Set DataContext for filter grid to enable height bindings
            if (FilterGrid != null)
            {
                FilterGrid.DataContext = settings;
                
                // Apply filter visibility states
                ApplyFilterVisibilityStates(settings);
                
                // Apply saved filter positions
                ApplyFilterPositions();
                
                
                // Ensure the ScrollViewer can scroll to show content
                var scrollViewer = FilterGrid.Parent as ScrollViewer;
                if (scrollViewer != null)
                {
                    // Small delay to ensure layout is complete
                    Dispatcher.BeginInvoke(new Action(() => 
                    {
                        scrollViewer.ScrollToTop();
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }

        /// <summary>
        /// Applies filter visibility states from settings
        /// </summary>
        private void ApplyFilterVisibilityStates(AppSettings settings)
        {
            
            // Only apply package filters in Packages mode
            if (_currentContentMode == "Packages")
            {
                // Date Filter
                if (DateFilterList != null && DateFilterToggleButton != null)
                {
                    DateFilterList.Visibility = settings.DateFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    DateFilterToggleButton.Content = "👁";
                }
                
                // Status Filter
                if (StatusFilterList != null && StatusFilterToggleButton != null)
                {
                    StatusFilterList.Visibility = settings.StatusFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    StatusFilterToggleButton.Content = "👁";
                }
                
                // Content Types Filter
                if (ContentTypesList != null && ContentTypesFilterTextBoxGrid != null && ContentTypesFilterCollapsedGrid != null)
                {
                    ContentTypesList.Visibility = settings.ContentTypesFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    ContentTypesFilterTextBoxGrid.Visibility = settings.ContentTypesFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    ContentTypesFilterCollapsedGrid.Visibility = settings.ContentTypesFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Creators Filter
                if (CreatorsList != null && CreatorsFilterTextBoxGrid != null && CreatorsFilterCollapsedGrid != null)
                {
                    CreatorsList.Visibility = settings.CreatorsFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    CreatorsFilterTextBoxGrid.Visibility = settings.CreatorsFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    CreatorsFilterCollapsedGrid.Visibility = settings.CreatorsFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // License Type Filter
                if (LicenseTypeList != null && LicenseTypeFilterTextBoxGrid != null && LicenseTypeFilterCollapsedGrid != null)
                {
                    LicenseTypeList.Visibility = settings.LicenseTypeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    LicenseTypeFilterTextBoxGrid.Visibility = settings.LicenseTypeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    LicenseTypeFilterCollapsedGrid.Visibility = settings.LicenseTypeFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // File Size Filter
                if (FileSizeFilterList != null && FileSizeFilterExpandedGrid != null && FileSizeFilterCollapsedGrid != null)
                {
                    FileSizeFilterList.Visibility = settings.FileSizeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    FileSizeFilterExpandedGrid.Visibility = settings.FileSizeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    FileSizeFilterCollapsedGrid.Visibility = settings.FileSizeFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Subfolders Filter
                if (SubfoldersFilterList != null && SubfoldersFilterTextBoxGrid != null && SubfoldersFilterCollapsedGrid != null)
                {
                    SubfoldersFilterList.Visibility = settings.SubfoldersFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SubfoldersFilterTextBoxGrid.Visibility = settings.SubfoldersFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SubfoldersFilterCollapsedGrid.Visibility = settings.SubfoldersFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Damaged Filter
                if (DamagedFilterList != null && DamagedFilterExpandedGrid != null && DamagedFilterCollapsedGrid != null)
                {
                    DamagedFilterList.Visibility = settings.DamagedFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    DamagedFilterExpandedGrid.Visibility = settings.DamagedFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    DamagedFilterCollapsedGrid.Visibility = settings.DamagedFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
            }
            
            // Only apply scene filters in Scenes mode
            if (_currentContentMode == "Scenes")
            {
                // Scene Type Filter
                if (SceneTypeFilterList != null && SceneTypeFilterTextBoxGrid != null && SceneTypeFilterCollapsedGrid != null)
                {
                    SceneTypeFilterList.Visibility = settings.SceneTypeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneTypeFilterTextBoxGrid.Visibility = settings.SceneTypeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneTypeFilterCollapsedGrid.Visibility = settings.SceneTypeFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Scene Creator Filter
                if (SceneCreatorFilterList != null && SceneCreatorFilterTextBoxGrid != null && SceneCreatorFilterCollapsedGrid != null)
                {
                    SceneCreatorFilterList.Visibility = settings.SceneCreatorFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneCreatorFilterTextBoxGrid.Visibility = settings.SceneCreatorFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneCreatorFilterCollapsedGrid.Visibility = settings.SceneCreatorFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Scene Source Filter
                if (SceneSourceFilterList != null && SceneSourceFilterExpandedGrid != null && SceneSourceFilterCollapsedGrid != null)
                {
                    SceneSourceFilterList.Visibility = settings.SceneSourceFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneSourceFilterExpandedGrid.Visibility = settings.SceneSourceFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneSourceFilterCollapsedGrid.Visibility = settings.SceneSourceFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Scene Date Filter
                if (SceneDateFilterList != null && SceneDateFilterExpandedGrid != null && SceneDateFilterCollapsedGrid != null)
                {
                    SceneDateFilterList.Visibility = settings.SceneDateFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneDateFilterExpandedGrid.Visibility = settings.SceneDateFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneDateFilterCollapsedGrid.Visibility = settings.SceneDateFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Scene File Size Filter
                if (SceneFileSizeFilterList != null && SceneFileSizeFilterExpandedGrid != null && SceneFileSizeFilterCollapsedGrid != null)
                {
                    SceneFileSizeFilterList.Visibility = settings.SceneFileSizeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneFileSizeFilterExpandedGrid.Visibility = settings.SceneFileSizeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneFileSizeFilterCollapsedGrid.Visibility = settings.SceneFileSizeFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Scene Status Filter
                if (SceneStatusFilterList != null && SceneStatusFilterExpandedGrid != null && SceneStatusFilterCollapsedGrid != null)
                {
                    SceneStatusFilterList.Visibility = settings.SceneStatusFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneStatusFilterExpandedGrid.Visibility = settings.SceneStatusFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    SceneStatusFilterCollapsedGrid.Visibility = settings.SceneStatusFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
            }
            
            // Only apply preset filters in Presets mode
            if (_currentContentMode == "Presets")
            {
                // Preset Category Filter
                if (PresetCategoryFilterList != null && PresetCategoryFilterTextBoxGrid != null && PresetCategoryFilterCollapsedGrid != null)
                {
                    PresetCategoryFilterList.Visibility = settings.PresetCategoryFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetCategoryFilterTextBoxGrid.Visibility = settings.PresetCategoryFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetCategoryFilterCollapsedGrid.Visibility = settings.PresetCategoryFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Preset Subfolder Filter
                if (PresetSubfolderFilterList != null && PresetSubfolderFilterTextBoxGrid != null && PresetSubfolderFilterCollapsedGrid != null)
                {
                    PresetSubfolderFilterList.Visibility = settings.PresetSubfolderFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetSubfolderFilterTextBoxGrid.Visibility = settings.PresetSubfolderFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetSubfolderFilterCollapsedGrid.Visibility = settings.PresetSubfolderFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Preset Date Filter
                if (PresetDateFilterList != null && PresetDateFilterExpandedGrid != null && PresetDateFilterCollapsedGrid != null)
                {
                    PresetDateFilterList.Visibility = settings.PresetDateFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetDateFilterExpandedGrid.Visibility = settings.PresetDateFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetDateFilterCollapsedGrid.Visibility = settings.PresetDateFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Preset File Size Filter
                if (PresetFileSizeFilterList != null && PresetFileSizeFilterExpandedGrid != null && PresetFileSizeFilterCollapsedGrid != null)
                {
                    PresetFileSizeFilterList.Visibility = settings.PresetFileSizeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetFileSizeFilterExpandedGrid.Visibility = settings.PresetFileSizeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetFileSizeFilterCollapsedGrid.Visibility = settings.PresetFileSizeFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Preset Status Filter
                if (PresetStatusFilterList != null && PresetStatusFilterExpandedGrid != null && PresetStatusFilterCollapsedGrid != null)
                {
                    PresetStatusFilterList.Visibility = settings.PresetStatusFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetStatusFilterExpandedGrid.Visibility = settings.PresetStatusFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetStatusFilterCollapsedGrid.Visibility = settings.PresetStatusFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
            }
        }

        /// <summary>
        /// Handles settings changes and updates UI accordingly
        /// </summary>
        private void OnSettingsChanged(object sender, AppSettings settings)
        {
            
            // Apply theme if changed
            if (_currentTheme != settings.Theme)
            {
                SwitchTheme(settings.Theme);
            }
            
            // Apply folder if changed
            if (_selectedFolder != settings.SelectedFolder)
            {
                _selectedFolder = settings.SelectedFolder;
                InitializePackageFileManager();
                UpdateUI();
            }
            
            // Apply image columns if changed
            if (_imageColumns != settings.ImageColumns)
            {
                _imageColumns = settings.ImageColumns;
                RefreshImageDisplay();
            }
            
        }

        /// <summary>
        /// Applies saved filter positions from settings on startup
        /// </summary>
        public void ApplyFilterPositions()
        {
            try
            {
                // Apply filter positions based on current content mode
                switch (_currentContentMode)
                {
                    case "Packages":
                        ApplyFilterOrder(_settingsManager.Settings.PackageFilterOrder, PackageFiltersContainer);
                        break;
                    case "Scenes":
                        ApplyFilterOrder(_settingsManager.Settings.SceneFilterOrder, SceneFiltersContainer);
                        break;
                    case "Presets":
                        ApplyFilterOrder(_settingsManager.Settings.PresetFilterOrder, PresetFiltersContainer);
                        break;
                }
            }
            catch (Exception)
            {
                // Ignore errors applying filter positions
            }
        }

        /// <summary>
        /// Applies a specific filter order to a container
        /// </summary>
        private void ApplyFilterOrder(List<string> filterOrder, StackPanel container)
        {
            if (filterOrder == null || container == null)
                return;

            try
            {
                // Create a dictionary to store filter elements
                var filterElements = new Dictionary<string, StackPanel>();

                // Collect all filter StackPanels
                for (int i = container.Children.Count - 1; i >= 0; i--)
                {
                    if (container.Children[i] is StackPanel stackPanel)
                    {
                        string filterType = GetFilterTypeFromStackPanel(stackPanel);
                        if (!string.IsNullOrEmpty(filterType) && filterOrder.Contains(filterType))
                        {
                            filterElements[filterType] = stackPanel;
                            container.Children.RemoveAt(i);
                        }
                    }
                }

                // Re-add filters in the correct order
                foreach (string filterType in filterOrder)
                {
                    if (filterElements.ContainsKey(filterType))
                    {
                        container.Children.Add(filterElements[filterType]);
                    }
                }
            }
            catch (Exception)
            {
                // Ignore errors applying filter order
            }
        }

        /// <summary>
        /// Gets the filter type from a StackPanel by examining its child elements
        /// </summary>
        private string GetFilterTypeFromStackPanel(StackPanel stackPanel)
        {
            // Look for a Grid with a toggle Button (eye button) that has a Tag
            foreach (var child in stackPanel.Children)
            {
                if (child is Grid grid)
                {
                    foreach (var gridChild in grid.Children)
                    {
                        if (gridChild is Button button && button.Tag is string tag)
                        {
                            // Look for the toggle button specifically (contains eye emoji)
                            if (button.Content?.ToString()?.Contains("👁") == true)
                            {
                                return tag;
                            }
                        }
                    }
                }
            }
            return null;
        }
        
        /// <summary>
        /// Helper method to get ScrollViewer from ListBox
        /// </summary>
        private ScrollViewer GetScrollViewerFromListBox(ListBox listBox)
        {
            try
            {
                var border = VisualTreeHelper.GetChild(listBox, 0) as Border;
                if (border != null)
                {
                    return border.Child as ScrollViewer;
                }
            }
            catch { }
            return null;
        }
        
        /// <summary>
        /// Helper method to get ScrollViewer from DataGrid
        /// </summary>
        private ScrollViewer GetScrollViewerFromDataGrid(DataGrid dataGrid)
        {
            try
            {
                var border = VisualTreeHelper.GetChild(dataGrid, 0) as Border;
                if (border != null)
                {
                    return border.Child as ScrollViewer;
                }
            }
            catch { }
            return null;
        }

        #endregion
        #region UI Updates

        private void UpdateUI()
        {
            // Update status with folder and package count info
            if (string.IsNullOrEmpty(_selectedFolder))
            {
                SetStatus("Ready - Select a VAM folder to begin");
            }
            else
            {
                SetStatus($"{Packages.Count} packages - {Path.GetFileName(_selectedFolder)}");
                
                // Load scenes in background
                _ = LoadScenesAsync();
            }
        }

        #endregion


        #region Package Management Operations

        private async void RefreshPackages()
        {
            // Ensure _selectedFolder is in sync with settings
            if (string.IsNullOrEmpty(_selectedFolder) && !string.IsNullOrEmpty(_settingsManager?.Settings.SelectedFolder))
            {
                _selectedFolder = _settingsManager.Settings.SelectedFolder;
            }
            
            if (string.IsNullOrEmpty(_selectedFolder))
            {
                MessageBox.Show("Please select a VAM root folder first.", "No Folder Selected", 
                               MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            SetStatus("Scanning VAR files...");

            try
            {
                // Define VAM folder structure
                string addonPackagesFolder = Path.Combine(_selectedFolder, "AddonPackages");
                string allPackagesFolder = Path.Combine(_selectedFolder, "AllPackages");

                // Scan for VAR files from multiple sources
                List<string> installedFiles, availableFiles;
                (installedFiles, availableFiles) = await _packageManager.ScanVarFilesAsync(
                    addonPackagesFolder, allPackagesFolder);

                SetStatus($"Found {installedFiles.Count + availableFiles.Count} VAR files. Processing...");

                // Update package mapping with progress
                var progress = new Progress<(int current, int total)>(p =>
                {
                    SetStatus($"Processing packages... {p.current}/{p.total} ({(double)p.current/p.total*100:F1}%)");
                });

                // Use synchronous fast method with proper progress reporting
                await Task.Run(() =>
                {
                    _packageManager.UpdatePackageMappingFast(installedFiles, availableFiles, progress);
                });

                // Initialize reactive filter manager with all packages for live count updates
                if (_reactiveFilterManager != null && _packageManager.PackageMetadata != null)
                {
                    _reactiveFilterManager.Initialize(_packageManager.PackageMetadata);
                }

                // Copy preview image index from package manager (piggy-back)
                // Always reload to include newly downloaded packages
                if (_packageManager.PreviewImageIndex.Count > 0)
                {
                    _imageManager.LoadExternalImageIndex(_packageManager.PreviewImageIndex);
                }

                // Reload favorites to get latest changes from game
                if (_favoritesManager != null)
                {
                    _favoritesManager.ReloadFavorites();
                }

                // Update UI with real package data
                await UpdatePackageListAsync();

                // Auto-build image index if it doesn't exist
                if (_imageManager.ImageIndex.Count == 0)
                {
                    SetStatus("Building image index for preview images...");

                    try
                    {
                        var varFiles = _packageManager.PackageMetadata.Values
                            .Where(p => File.Exists(p.FilePath))
                            .Select(p => p.FilePath)
                            .ToList();

                        if (varFiles.Count > 0)
                        {
                            await _imageManager.BuildImageIndexFromVarsAsync(varFiles, false);
                        }
                    }
                    catch (Exception)
                    {
                        // Auto-build failed, continue without image index
                    }
                }

                SetStatus($"Package refresh completed. Found {_packageManager.PackageMetadata.Count} packages.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error refreshing packages: {ex.Message}", "Error", 
                               MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Package refresh failed");
            }
        }

        private Task UpdatePackageListAsync(bool refreshFilterLists = true)
        {
            // Save current state before clearing
            var selectedPackageNames = PackageDataGrid?.SelectedItems?.Cast<PackageItem>()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            double scrollOffset = 0;
            if (PackageDataGrid != null)
            {
                var scrollViewer = FindVisualChild<ScrollViewer>(PackageDataGrid);
                if (scrollViewer != null)
                {
                    scrollOffset = scrollViewer.VerticalOffset;
                }
            }

            // Step 1: Clear UI immediately
            Packages.Clear();
            Dependencies.Clear();
            SetStatus("Loading packages...");

            // Step 2: Update filter lists (only if requested - don't refresh when just applying filters)
            if (refreshFilterLists)
            {
                RefreshFilterLists();
            }
            
            // Step 3: Load packages in background - WPF handles virtualization
            _ = Task.Run(async () =>
            {
                try
                {
                    // Clear version cache
                    _versionCacheBuilt = false;

                    var allPackages = new List<PackageItem>();
                    var processedCount = 0;
                    var filteredCount = 0;

                    // Calculate dependents count for all packages
                    var dependentsCount = CalculateDependentsCount();

                    // Process and filter packages in background thread
                    var seenDuplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    
                    foreach (var kvp in _packageManager.PackageMetadata)
                    {
                        var metadataKey = kvp.Key;
                        var metadata = kvp.Value;
                        processedCount++;

                        // Apply filter in background thread to avoid UI delay
                        if (!_filterManager.MatchesFilters(metadata))
                        {
                            continue; // Skip packages that don't match filter
                        }

                        // Special handling for duplicate filtering - show only one instance per duplicate group
                        if (_filterManager.FilterDuplicates && metadata.DuplicateLocationCount > 1)
                        {
                            // Get the base package name from the filename
                            string basePackageName = Path.GetFileNameWithoutExtension(metadata.Filename);
                            
                            // Remove version suffixes to get the core package name (consistent with counting logic)
                            var parts = basePackageName.Split('.');
                            if (parts.Length >= 3) // Creator.PackageName.Version format
                            {
                                basePackageName = $"{parts[0]}.{parts[1]}"; // Creator.PackageName
                            }
                            
                            // Skip if we've already seen this duplicate
                            if (seenDuplicates.Contains(basePackageName))
                            {
                                continue;
                            }
                            seenDuplicates.Add(basePackageName);
                        }

                        filteredCount++;

                        // Create PackageItem only for filtered packages
                        // For archived packages, use the metadataKey which includes #archived suffix
                        string packageName = metadataKey.EndsWith("#archived", StringComparison.OrdinalIgnoreCase) 
                            ? metadataKey 
                            : Path.GetFileNameWithoutExtension(metadata.Filename);
                        
                        var dependentsCountValue = dependentsCount.TryGetValue(packageName, out var count) ? count : 0;
                        
                        // Debug: Log MacGruber.PostMagic.4
                        if (packageName.Equals("MacGruber.PostMagic.4", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"[TABLE] Setting DependentsCount for {packageName} to {dependentsCountValue}");
                        }
                        
                        var packageItem = new PackageItem
                        {
                            MetadataKey = metadataKey, // Store for fast lookup later
                            Name = packageName,
                            Status = metadata.Status,
                            Creator = metadata.CreatorName,
                            DependencyCount = metadata.Dependencies?.Count ?? 0,
                            DependentsCount = dependentsCountValue,
                            FileSize = metadata.FileSize,
                            ModifiedDate = metadata.ModifiedDate,
                            IsLatestVersion = true, // Skip version check for performance
                            IsOptimized = metadata.IsOptimized,
                            IsDuplicate = metadata.IsDuplicate,
                            DuplicateLocationCount = metadata.DuplicateLocationCount,
                            IsOldVersion = metadata.IsOldVersion,
                            LatestVersionNumber = metadata.LatestVersionNumber,
                            IsFavorite = _favoritesManager?.IsFavorite(packageName) ?? false,
                            IsAutoInstall = _autoInstallManager?.IsAutoInstall(packageName) ?? false,
                            MorphCount = metadata.MorphCount,
                            HairCount = metadata.HairCount,
                            ClothingCount = metadata.ClothingCount,
                            SceneCount = metadata.SceneCount,
                            LooksCount = metadata.LooksCount,
                            PosesCount = metadata.PosesCount,
                            AssetsCount = metadata.AssetsCount,
                            ScriptsCount = metadata.ScriptsCount,
                            PluginsCount = metadata.PluginsCount,
                            SubScenesCount = metadata.SubScenesCount,
                            SkinsCount = metadata.SkinsCount
                        };
                        
                        allPackages.Add(packageItem);
                    }

                    // Update UI in one shot - packages already filtered in background
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _suppressSelectionEvents = true;
                        try
                        {
                            Packages.ReplaceAll(allPackages);

                            // No filter needed - collection is already pre-filtered
                            if (PackagesView != null)
                            {
                                PackagesView.Filter = null;
                            }
                        }
                        finally
                        {
                            _suppressSelectionEvents = false;
                        }

                        // Defer sorting and selection restoration to ensure view is fully updated
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _suppressSelectionEvents = true;
                            try
                            {
                                // Reapply sorting to maintain sort order after package list update
                                ReapplySorting();
                                
                                // Force view refresh AFTER sorting to ensure bindings and sort are applied
                                if (PackagesView != null)
                                {
                                    PackagesView.Refresh();
                                }

                                // Restore selection after sorting is applied
                                if (selectedPackageNames.Count > 0 && PackageDataGrid != null)
                                {
                                    foreach (var item in Packages)
                                    {
                                        if (selectedPackageNames.Contains(item.Name))
                                        {
                                            PackageDataGrid.SelectedItems.Add(item);
                                        }
                                    }
                                }
                                
                                UpdateOptimizeCounter();
                            }
                            finally
                            {
                                _suppressSelectionEvents = false;
                            }

                            // Restore scroll position and refresh images after selection is restored
                            _ = Dispatcher.BeginInvoke(new Action(async () =>
                            {
                                // Small delay to ensure virtualization is complete
                                await Task.Delay(50);
                                
                                if (PackageDataGrid != null)
                                {
                                    var scrollViewer = FindVisualChild<ScrollViewer>(PackageDataGrid);
                                    if (scrollViewer != null)
                                    {
                                        scrollViewer.ScrollToVerticalOffset(scrollOffset);
                                    }
                                }
                                
                                // Refresh images for restored selection
                                if (PackageDataGrid?.SelectedItems?.Count > 0)
                                {
                                    _ = RefreshSelectionDisplaysImmediate();
                                }
                            }), DispatcherPriority.ContextIdle);
                        }), DispatcherPriority.DataBind);

                        // Count unique packages (archived and optimized versions count as one)
                        var uniquePackageCount = allPackages
                            .Select(p => p.Name.EndsWith("#archived", StringComparison.OrdinalIgnoreCase) 
                                ? p.Name.Substring(0, p.Name.Length - 9) 
                                : p.Name)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count();
                        
                        var uniqueTotalCount = _packageManager.PackageMetadata.Keys
                            .Select(k => k.EndsWith("#archived", StringComparison.OrdinalIgnoreCase) 
                                ? k.Substring(0, k.Length - 9) 
                                : k)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count();

                        SetStatus(allPackages.Count == processedCount
                            ? $"Showing all {allPackages.Count:N0} entries ({uniquePackageCount:N0} unique packages)"
                            : $"Showing {allPackages.Count:N0} of {processedCount:N0} entries ({uniquePackageCount:N0} of {uniqueTotalCount:N0} unique packages)");
                    }, DispatcherPriority.Normal);
                }
                catch (Exception)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        SetStatus("Error loading packages");
                    });
                }
            });

            // Return immediately - background task continues
            UpdateUI();
            
            return Task.CompletedTask;
        }

        private void RefreshFilterLists()
        {
            if (_packageManager?.PackageMetadata == null) 
            {
                return;
            }
            
            // Suppress selection events to prevent infinite loop during filter list updates
            _suppressSelectionEvents = true;
            try
            {
                // In unlinked mode, we need to show counts based on filtered packages
                // to avoid showing creators/categories that have no matching packages
                var packagesToCount = _packageManager.PackageMetadata;
                if (!_cascadeFiltering)
                {
                    // CRITICAL: Update filter manager from UI FIRST to ensure we have current filter state
                    // This is needed because RefreshFilterLists can be called from various places,
                    // not just from ApplyFilters, and we need the latest UI selections
                    var previousSuppress = _suppressSelectionEvents;
                    _suppressSelectionEvents = false; // Temporarily allow UpdateFilterManagerFromUI to read selections
                    UpdateFilterManagerFromUI();
                    _suppressSelectionEvents = previousSuppress;
                    
                    // Build filtered package set for counting
                    int totalChecked = 0;
                    int matchedCount = 0;
                    int favoritesFilterActive = _filterManager.SelectedFavoriteStatuses.Count;
                    
                    var filteredPackages = new Dictionary<string, VarMetadata>();
                    foreach (var kvp in _packageManager.PackageMetadata)
                    {
                        totalChecked++;
                        if (_filterManager.MatchesFilters(kvp.Value))
                        {
                            filteredPackages.Add(kvp.Key, kvp.Value);
                            matchedCount++;
                        }
                    }
                    
                    if (favoritesFilterActive > 0)
                    {
                        Console.WriteLine($"[FILTER] Checked {totalChecked} packages, {matchedCount} matched favorites filter");
                    }
                    
                    packagesToCount = filteredPackages;
                }
                
                // Update creators list - OPTIMIZED for large datasets
                // Store current selections before clearing
                var selectedCreators = new List<string>();
                foreach (var item in CreatorsList.SelectedItems)
                {
                    string itemText = item?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        var creatorName = itemText.Split('(')[0].Trim();
                        selectedCreators.Add(creatorName);
                    }
                }
                
                // Get the creator filter text to respect it when rebuilding the list
                var creatorFilterText = GetSearchText(CreatorsFilterBox);
                
                CreatorsList.Items.Clear();
                var creatorCounts = _filterManager.GetCreatorCounts(packagesToCount);

                // Limit to top 500 creators to prevent UI slowdown
                var topCreators = creatorCounts
                    .OrderByDescending(c => c.Value) // Sort by package count
                    .Take(500) // Limit to top 500
                    .OrderBy(c => c.Key) // Then alphabetically
                    .ToList();

                foreach (var creator in topCreators)
                {
                    // Apply creator filter text if present
                    if (!string.IsNullOrWhiteSpace(creatorFilterText) && 
                        creator.Key.IndexOf(creatorFilterText, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue; // Skip creators that don't match the filter text
                    }
                    
                    var displayText = $"{creator.Key} ({creator.Value})";
                    CreatorsList.Items.Add(displayText);
                    
                    // Restore selection if this creator was previously selected
                    if (selectedCreators.Contains(creator.Key))
                    {
                        CreatorsList.SelectedItems.Add(displayText);
                    }
                }

                // Update content types list with counts
                // Store current selections before clearing
                var selectedContentTypes = new List<string>();
                foreach (var item in ContentTypesList.SelectedItems)
                {
                    string itemText = item?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        var contentTypeName = itemText.Split('(')[0].Trim();
                        selectedContentTypes.Add(contentTypeName);
                    }
                }
                
                var categoryCounts = _filterManager.GetCategoryCounts(packagesToCount);
                ContentTypesList.Items.Clear();
                foreach (var category in categoryCounts.OrderBy(c => c.Key))
                {
                    var displayText = $"{category.Key} ({category.Value:N0})";
                    ContentTypesList.Items.Add(displayText);
                    
                    // Restore selection if this content type was previously selected
                    if (selectedContentTypes.Contains(category.Key))
                    {
                        ContentTypesList.SelectedItems.Add(displayText);
                    }
                }

                // Update status filter with counts - OPTIMIZED
                // Store current selections before clearing
                var selectedStatuses = new List<string>();
                foreach (var item in StatusFilterList.SelectedItems)
                {
                    string itemText = item?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        var statusName = itemText.Split('(')[0].Trim();
                        // Normalize "Duplicates" to "Duplicate" for comparison
                        if (statusName.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                        {
                            statusName = "Duplicate";
                        }
                        selectedStatuses.Add(statusName);
                    }
                }
                
                var statusCounts = _filterManager.GetStatusCounts(packagesToCount);

                // Update status filter items with counts - use clean text format without emojis
                StatusFilterList.Items.Clear();
                foreach (var status in statusCounts.OrderBy(s => s.Key))
                {
                    var displayName = status.Key.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) ? "Duplicates" : status.Key;
                    var displayText = $"{displayName} ({status.Value:N0})";
                    StatusFilterList.Items.Add(displayText);
                    
                    // Restore selection if this status was previously selected
                    if (selectedStatuses.Contains(status.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }

                // Add optimization status counts to status filter
                var optCounts = _filterManager.GetOptimizationStatusCounts(packagesToCount);
                foreach (var opt in optCounts.OrderBy(s => s.Key))
                {
                    var displayText = $"{opt.Key} ({opt.Value:N0})";
                    StatusFilterList.Items.Add(displayText);
                    
                    // Restore selection if this optimization status was previously selected
                    if (selectedStatuses.Contains(opt.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }
                
                // Add version status counts to status filter
                var versionCounts = _filterManager.GetVersionStatusCounts(packagesToCount);
                foreach (var ver in versionCounts.OrderBy(s => s.Key))
                {
                    var displayText = $"{ver.Key} ({ver.Value:N0})";
                    StatusFilterList.Items.Add(displayText);
                    
                    // Restore selection if this version status was previously selected
                    if (selectedStatuses.Contains(ver.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }

                // Add favorites counts to status filter
                if (_favoritesManager != null)
                {
                    var favorites = _favoritesManager.GetAllFavorites();
                    int favoriteCount = 0;
                    int nonFavoriteCount = 0;

                    // IMPORTANT: Count from ALL packages, not filtered packages
                    // Otherwise when favorites filter is active, count becomes 0
                    var packagesForFavCount = _packageManager.PackageMetadata;
                    
                    foreach (var pkg in packagesForFavCount.Values)
                    {
                        var pkgName = System.IO.Path.GetFileNameWithoutExtension(pkg.Filename);
                        if (favorites.Contains(pkgName))
                            favoriteCount++;
                        else
                            nonFavoriteCount++;
                    }

                    // Always show Favorites option even if count is 0
                    var favText = $"Favorites ({favoriteCount:N0})";
                    StatusFilterList.Items.Add(favText);
                    
                    if (selectedStatuses.Contains("Favorites"))
                    {
                        StatusFilterList.SelectedItems.Add(favText);
                    }
                }

                // Add autoinstall option
                if (_autoInstallManager != null && _packageManager?.PackageMetadata != null)
                {
                    var autoInstall = _autoInstallManager.GetAllAutoInstall();
                    int autoInstallCount = 0;
                    
                    var packagesForAutoInstallCount = _packageManager.PackageMetadata;
                    
                    foreach (var pkg in packagesForAutoInstallCount.Values)
                    {
                        var pkgName = System.IO.Path.GetFileNameWithoutExtension(pkg.Filename);
                        if (autoInstall.Contains(pkgName))
                            autoInstallCount++;
                    }

                    var autoInstallText = $"AutoInstall ({autoInstallCount:N0})";
                    StatusFilterList.Items.Add(autoInstallText);
                    
                    if (selectedStatuses.Contains("AutoInstall"))
                    {
                        StatusFilterList.SelectedItems.Add(autoInstallText);
                    }
                }

                // Update license types list with counts
                if (LicenseTypeList != null)
                {
                    // Store current selections before clearing
                    var selectedLicenseTypes = new List<string>();
                    foreach (var item in LicenseTypeList.SelectedItems)
                    {
                        string itemText = item?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var licenseTypeName = itemText.Split('(')[0].Trim();
                            selectedLicenseTypes.Add(licenseTypeName);
                        }
                    }
                    
                    var licenseCounts = _filterManager.GetLicenseCounts(packagesToCount);
                    LicenseTypeList.Items.Clear();
                    foreach (var license in licenseCounts.OrderBy(l => l.Key))
                    {
                        var displayText = $"{license.Key} ({license.Value:N0})";
                        LicenseTypeList.Items.Add(displayText);
                        
                        // Restore selection if this license type was previously selected
                        if (selectedLicenseTypes.Contains(license.Key))
                        {
                            LicenseTypeList.SelectedItems.Add(displayText);
                        }
                    }
                }

                // Update date filter list with counts
                PopulateDateFilterList(packagesToCount);

                // Update file size filter list with counts
                if (FileSizeFilterList != null && _filterManager != null)
                {
                    // Store current selections before clearing
                    var selectedFileSizeRanges = new List<string>();
                    foreach (var item in FileSizeFilterList.SelectedItems)
                    {
                        string itemText = item?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var rangeName = itemText.Split('(')[0].Trim();
                            selectedFileSizeRanges.Add(rangeName);
                        }
                    }
                    
                    var fileSizeCounts = _filterManager.GetFileSizeCounts(packagesToCount);
                    FileSizeFilterList.Items.Clear();
                    
                    var orderedRanges = new[] { "Tiny", "Small", "Medium", "Large" };
                    foreach (var range in orderedRanges)
                    {
                        if (fileSizeCounts.ContainsKey(range) && fileSizeCounts[range] > 0)
                        {
                            var displayText = $"{range} ({fileSizeCounts[range]:N0})";
                            FileSizeFilterList.Items.Add(displayText);
                            
                            // Restore selection if this range was previously selected
                            if (selectedFileSizeRanges.Contains(range))
                            {
                                FileSizeFilterList.SelectedItems.Add(displayText);
                            }
                        }
                    }
                }

                // Update subfolders filter list with counts
                if (SubfoldersFilterList != null && _filterManager != null)
                {
                    // Store current selections before clearing
                    var selectedSubfolders = new List<string>();
                    foreach (var item in SubfoldersFilterList.SelectedItems)
                    {
                        string itemText = item?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var subfolderName = itemText.Split('(')[0].Trim();
                            selectedSubfolders.Add(subfolderName);
                        }
                    }
                    
                    var subfolderCounts = _filterManager.GetSubfolderCounts(packagesToCount);
                    SubfoldersFilterList.Items.Clear();
                    
                    // Sort subfolders alphabetically
                    var sortedSubfolders = subfolderCounts.Keys.OrderBy(k => k).ToList();
                    foreach (var subfolder in sortedSubfolders)
                    {
                        if (subfolderCounts[subfolder] > 0)
                        {
                            var displayText = $"{subfolder} ({subfolderCounts[subfolder]:N0})";
                            SubfoldersFilterList.Items.Add(displayText);
                            
                            // Restore selection if this subfolder was previously selected
                            if (selectedSubfolders.Contains(subfolder))
                            {
                                SubfoldersFilterList.SelectedItems.Add(displayText);
                            }
                        }
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
            
            // Restore filter list sorting after lists are populated
            RestoreFilterListsSorting();
            
            // Force refresh of the UI to ensure selections are properly displayed
            StatusFilterList?.Items.Refresh();
        }

        private void PopulateDateFilterList(Dictionary<string, VarMetadata> packagesToCount = null)
        {
            if (DateFilterList == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                // Store current selection
                var selectedTag = "";
                if (DateFilterList.SelectedItem is ListBoxItem selectedItem)
                {
                    selectedTag = selectedItem.Tag?.ToString() ?? "";
                }

                // Clear and repopulate with counts
                DateFilterList.Items.Clear();
                var dateCounts = GetDateFilterCounts(packagesToCount ?? _packageManager.PackageMetadata);

                // Add all date filter options with counts
                var dateOptions = new[]
                {
                    new { Text = "All Time", Tag = "AllTime", Count = dateCounts["AllTime"] },
                    new { Text = "Today", Tag = "Today", Count = dateCounts["Today"] },
                    new { Text = "Past Week", Tag = "PastWeek", Count = dateCounts["PastWeek"] },
                    new { Text = "Past Month", Tag = "PastMonth", Count = dateCounts["PastMonth"] },
                    new { Text = "Past 3 Months", Tag = "Past3Months", Count = dateCounts["Past3Months"] },
                    new { Text = "Past Year", Tag = "PastYear", Count = dateCounts["PastYear"] },
                    new { Text = "Custom Range...", Tag = "CustomRange", Count = 0 }
                };

                foreach (var option in dateOptions)
                {
                    var displayText = option.Tag == "CustomRange" ? option.Text : $"{option.Text} ({option.Count})";
                    DateFilterList.Items.Add(displayText);

                    // Restore selection
                    if (option.Tag == selectedTag || (string.IsNullOrEmpty(selectedTag) && option.Tag == "AllTime"))
                    {
                        DateFilterList.SelectedItem = displayText;
                    }
                }

            }
            catch (Exception)
            {
            }
        }

        private Dictionary<string, int> GetDateFilterCounts(Dictionary<string, VarMetadata> packages)
        {
            if (packages == null)
            {
                return new Dictionary<string, int>
                {
                    ["AllTime"] = 0,
                    ["Today"] = 0,
                    ["PastWeek"] = 0,
                    ["PastMonth"] = 0,
                    ["Past3Months"] = 0,
                    ["PastYear"] = 0
                };
            }

            var counts = new Dictionary<string, int>
            {
                ["AllTime"] = packages.Count,
                ["Today"] = 0,
                ["PastWeek"] = 0,
                ["PastMonth"] = 0,
                ["Past3Months"] = 0,
                ["PastYear"] = 0
            };

            var now = DateTime.Now;
            var today = now.Date;

            foreach (var package in packages.Values)
            {
                var dateToCheck = package.ModifiedDate ?? package.CreatedDate;
                if (!dateToCheck.HasValue) continue;

                var date = dateToCheck.Value.Date;
                
                // Calculate days difference (positive = past, negative = future)
                var daysDiff = (today - date).TotalDays;
                
                // Count packages from the past (daysDiff >= 0)
                if (daysDiff >= 0 && daysDiff < 1)
                    counts["Today"]++;
                if (daysDiff >= 0 && daysDiff <= 7)
                    counts["PastWeek"]++;
                if (daysDiff >= 0 && daysDiff <= 30)
                    counts["PastMonth"]++;
                if (daysDiff >= 0 && daysDiff <= 90)
                    counts["Past3Months"]++;
                if (daysDiff >= 0 && daysDiff <= 365)
                    counts["PastYear"]++;
            }

            return counts;
        }

        // Cache for version lookups to avoid O(nÂ²) complexity
        private static readonly Dictionary<string, int> _versionCache = new Dictionary<string, int>();
        private static bool _versionCacheBuilt = false;
        
        private bool DetermineIfLatestVersion(VarMetadata metadata)
        {
            // Build version cache once for all packages
            if (!_versionCacheBuilt)
            {
                BuildVersionCache();
                _versionCacheBuilt = true;
            }
            
            // Extract base package name (creator.packagename) without version
            var parts = Path.GetFileNameWithoutExtension(metadata.Filename).Split('.');
            if (parts.Length < 3) return true; // If no version info, assume latest
            
            var basePackageName = string.Join(".", parts.Take(parts.Length - 1));
            var currentVersion = parts.LastOrDefault();
            
            if (!int.TryParse(currentVersion, out var currentVersionNumber)) return true;
            
            // O(1) lookup instead of O(n) search
            if (_versionCache.TryGetValue(basePackageName, out var maxVersion))
            {
                return currentVersionNumber >= maxVersion;
            }
            
            return true; // If not in cache, assume latest
        }
        
        private void BuildVersionCache()
        {
            var startTime = DateTime.Now;

            _versionCache.Clear();

            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var metadata = kvp.Value;
                var parts = Path.GetFileNameWithoutExtension(metadata.Filename).Split('.');

                if (parts.Length >= 3 && int.TryParse(parts.LastOrDefault(), out var version))
                {
                    var basePackageName = string.Join(".", parts.Take(parts.Length - 1));

                    if (!_versionCache.ContainsKey(basePackageName) || _versionCache[basePackageName] < version)
                    {
                        _versionCache[basePackageName] = version;
                    }
                }
            }
        }


        private Task UpdateImageDisplayAsync()
        {
            try
            {
                // Get total image count from index
                var totalImages = _imageManager.GetTotalImageCount();

                // TODO: Implement image display in the Images panel
                // This would involve creating image controls and adding them to the ImagesPanel
            }
            catch (Exception)
            {
                // Error updating image display - silently handled
            }

            return Task.CompletedTask;
        }

        #endregion

        #region Initialization Helpers

        /// <summary>
        /// Set up event handlers for keyboard navigation manager
        /// </summary>
        private void SetupKeyboardNavigationEvents()
        {
            if (_keyboardNavigationManager == null) return;

            // Connect keyboard navigation events
            _keyboardNavigationManager.RefreshRequested += () => RefreshPackages();
            _keyboardNavigationManager.ImageColumnsChanged += (delta) =>
            {
                if (delta > 0)
                {
                    IncreaseImageColumns_Click(this, new RoutedEventArgs());
                }
                else if (delta < 0)
                {
                    DecreaseImageColumns_Click(this, new RoutedEventArgs());
                }
            };
        }

        /// <summary>
        /// Initializes the PackageFileManager with the current selected folder
        /// </summary>
        private void InitializePackageFileManager()
        {
            try
            {
                if (!string.IsNullOrEmpty(_selectedFolder))
                {
                    _packageFileManager = new PackageFileManager(_selectedFolder, _imageManager);
                    
                    // Initialize package downloader
                    InitializePackageDownloader();
                }
                else
                {
                    _packageFileManager = null;
                    
                    // Dispose package downloader
                    DisposePackageDownloader();
                }
            }
            catch (Exception)
            {
                _packageFileManager = null;
                DisposePackageDownloader();
            }
        }

        /// <summary>
        /// Calculate the number of dependents for each package
        /// </summary>
        private Dictionary<string, int> CalculateDependentsCount()
        {
            Console.WriteLine("[CALC] CalculateDependentsCount called");
            var dependentsCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            if (_packageManager?.PackageMetadata == null)
                return dependentsCount;
            
            // Build a lookup table: baseName -> latest version package name
            var latestVersionLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var pkgName = Path.GetFileNameWithoutExtension(kvp.Value.Filename);
                var lastDot = pkgName.LastIndexOf('.');
                if (lastDot > 0)
                {
                    var suffix = pkgName.Substring(lastDot + 1);
                    if (int.TryParse(suffix, out var version))
                    {
                        var baseName = pkgName.Substring(0, lastDot);
                        
                        // Check if this is the highest version we've seen
                        if (!latestVersionLookup.ContainsKey(baseName))
                        {
                            latestVersionLookup[baseName] = pkgName;
                        }
                        else
                        {
                            var existingPkg = latestVersionLookup[baseName];
                            var existingVersion = int.Parse(existingPkg.Substring(existingPkg.LastIndexOf('.') + 1));
                            if (version > existingVersion)
                            {
                                latestVersionLookup[baseName] = pkgName;
                            }
                        }
                    }
                }
            }
            
            // For each package, track which unique dependents reference it
            var packageDependents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var metadata = kvp.Value;
                
                // Skip archived packages if HideArchivedPackages is enabled
                if (_filterManager?.HideArchivedPackages == true)
                {
                    if (metadata.Status != null && metadata.Status.Equals("Archived", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (metadata.VariantRole != null && metadata.VariantRole.Equals("Archived", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (metadata.Filename != null && metadata.Filename.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (kvp.Key.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                
                if (metadata.Dependencies == null || metadata.Dependencies.Count == 0)
                    continue;
                
                // Get the dependent package name (the package that has dependencies)
                string dependentPackageName = kvp.Key.EndsWith("#archived", StringComparison.OrdinalIgnoreCase)
                    ? kvp.Key
                    : Path.GetFileNameWithoutExtension(metadata.Filename);
                
                foreach (var dependency in metadata.Dependencies)
                {
                    if (string.IsNullOrEmpty(dependency))
                        continue;
                    
                    // Remove .var extension if present
                    var dependencyName = dependency;
                    if (dependency.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                    {
                        dependencyName = Path.GetFileNameWithoutExtension(dependency);
                    }
                    
                    // Add this dependent to the dependency's set
                    if (!packageDependents.ContainsKey(dependencyName))
                    {
                        packageDependents[dependencyName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    packageDependents[dependencyName].Add(dependentPackageName);
                    
                    // If dependency is X.latest, also add to the actual latest version (X.4, X.5, etc.)
                    if (dependencyName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                    {
                        var baseName = dependencyName.Substring(0, dependencyName.Length - 7); // Remove ".latest"
                        
                        // Look up the latest version from our pre-built cache
                        if (latestVersionLookup.TryGetValue(baseName, out var latestVersionPackage))
                        {
                            if (!packageDependents.ContainsKey(latestVersionPackage))
                            {
                                packageDependents[latestVersionPackage] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            }
                            packageDependents[latestVersionPackage].Add(dependentPackageName);
                        }
                    }
                }
            }
            
            // Convert HashSets to counts
            foreach (var kvp in packageDependents)
            {
                dependentsCount[kvp.Key] = kvp.Value.Count;
            }
            
            // Debug: Log MacGruber.PostMagic count
            if (dependentsCount.TryGetValue("MacGruber.PostMagic.4", out var postMagicCount))
            {
                Console.WriteLine($"[CALC] MacGruber.PostMagic.4 has {postMagicCount} unique dependents");
            }
            
            return dependentsCount;
        }

        #endregion

        #region Status Management

        /// <summary>
        /// Updates the status text in the title bar
        /// </summary>
        private void SetStatus(string message)
        {
            // Update the status text in the title bar
            if (StatusText != null)
            {
                StatusText.Text = message;
            }
            // Also keep window title updated for reference
            this.Title = $"VPM - {message}";
        }

        /// <summary>
        /// Ensures VAM folder is selected, shows error if not
        /// </summary>
        private bool EnsureVamFolderSelected()
        {
            if (_packageFileManager == null)
            {
                MessageBox.Show("Please select a VAM folder first.", "No VAM Folder", 
                               MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        #endregion
    }
}

