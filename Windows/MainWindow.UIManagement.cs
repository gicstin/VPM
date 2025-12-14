using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        
        // Cache for dependents count calculation to avoid O(n²) recalculation
        private Dictionary<string, int> _cachedDependentsCount = null;
        private int _cachedPackageMetadataVersion = -1;
        
        // Track whether packages are currently loading
        private bool _isLoadingPackages = false;
        
        // MEMORY FIX: Cache PackageItem objects to prevent recreating them on every filter
        // Key is MetadataKey, value is the cached PackageItem
        // Using ConcurrentDictionary for thread-safe parallel access
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PackageItem> _packageItemCache = new(StringComparer.OrdinalIgnoreCase);
        private int _packageItemCacheVersion = -1;
        private string _cachedDestinationNamesHash = "";  // Hash of destination names to detect renames
        
        // MEMORY FIX: Cancellation token for filter operations to prevent accumulating tasks
        private System.Threading.CancellationTokenSource _filterCts;

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
                return [];
                
            return [.. PackageDataGrid.SelectedItems.Cast<PackageItem>()
                .Select(p => p.Name)];
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
                return [];
                
            return [.. DependenciesDataGrid.SelectedItems.Cast<DependencyItem>()
                .Select(d => d.Name)];
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
                if (LightThemeMenuItem is not null)
                    LightThemeMenuItem.IsChecked = _currentTheme == "Light";
                
                if (DarkThemeMenuItem is not null)
                    DarkThemeMenuItem.IsChecked = _currentTheme == "Dark";
                
                if (SystemThemeMenuItem is not null)
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
                        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
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
            ImageColumns = settings.ImageColumns;
            
            // Apply image match width setting
            ImageMatchWidth = settings.ImageMatchWidth;
            
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
            // First, ensure the correct filter container is visible based on mode
            if (PackageFiltersContainer != null)
                PackageFiltersContainer.Visibility = (_currentContentMode == "Packages") ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (SceneFiltersContainer != null)
                SceneFiltersContainer.Visibility = (_currentContentMode == "Scenes") ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (PresetFiltersContainer != null)
                PresetFiltersContainer.Visibility = (_currentContentMode == "Presets" || _currentContentMode == "Custom") ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            
            // Only apply package filters in Packages mode
            if (_currentContentMode == "Packages")
            {
                // Date Filter
                if (DateFilterList != null && DateFilterToggleButton != null)
                {
                    DateFilterList.Visibility = settings.DateFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    DateFilterToggleButton.Content = "➖";
                }
                
                // Status Filter
                if (StatusFilterList != null && StatusFilterToggleButton != null)
                {
                    StatusFilterList.Visibility = settings.StatusFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    StatusFilterToggleButton.Content = "➕";
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
                
                // Destinations Filter
                if (DestinationsFilterList != null && DestinationsFilterTextBoxGrid != null && DestinationsFilterCollapsedGrid != null)
                {
                    DestinationsFilterList.Visibility = settings.DestinationsFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    DestinationsFilterTextBoxGrid.Visibility = settings.DestinationsFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    DestinationsFilterCollapsedGrid.Visibility = settings.DestinationsFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
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
            
            // Apply preset filters in Presets mode and Custom mode (unified presets + scenes)
            if (_currentContentMode == "Presets" || _currentContentMode == "Custom")
            {
                // Preset Category Filter
                if (PresetCategoryFilterSection != null)
                    PresetCategoryFilterSection.Visibility = System.Windows.Visibility.Visible;
                if (PresetCategoryFilterList != null && PresetCategoryFilterTextBoxGrid != null && PresetCategoryFilterCollapsedGrid != null)
                {
                    PresetCategoryFilterList.Visibility = settings.PresetCategoryFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetCategoryFilterTextBoxGrid.Visibility = settings.PresetCategoryFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetCategoryFilterCollapsedGrid.Visibility = settings.PresetCategoryFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Preset Subfolder Filter
                if (PresetSubfolderFilterSection != null)
                    PresetSubfolderFilterSection.Visibility = System.Windows.Visibility.Visible;
                if (PresetSubfolderFilterList != null && PresetSubfolderFilterTextBoxGrid != null && PresetSubfolderFilterCollapsedGrid != null)
                {
                    PresetSubfolderFilterList.Visibility = settings.PresetSubfolderFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetSubfolderFilterTextBoxGrid.Visibility = settings.PresetSubfolderFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetSubfolderFilterCollapsedGrid.Visibility = settings.PresetSubfolderFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Preset Date Filter
                if (PresetDateFilterSection != null)
                    PresetDateFilterSection.Visibility = System.Windows.Visibility.Visible;
                if (PresetDateFilterList != null && PresetDateFilterExpandedGrid != null && PresetDateFilterCollapsedGrid != null)
                {
                    PresetDateFilterList.Visibility = settings.PresetDateFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetDateFilterExpandedGrid.Visibility = settings.PresetDateFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetDateFilterCollapsedGrid.Visibility = settings.PresetDateFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Preset File Size Filter
                if (PresetFileSizeFilterSection != null)
                    PresetFileSizeFilterSection.Visibility = System.Windows.Visibility.Visible;
                if (PresetFileSizeFilterList != null && PresetFileSizeFilterExpandedGrid != null && PresetFileSizeFilterCollapsedGrid != null)
                {
                    PresetFileSizeFilterList.Visibility = settings.PresetFileSizeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetFileSizeFilterExpandedGrid.Visibility = settings.PresetFileSizeFilterVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    PresetFileSizeFilterCollapsedGrid.Visibility = settings.PresetFileSizeFilterVisible ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                }
                
                // Preset Status Filter
                if (PresetStatusFilterSection != null)
                    PresetStatusFilterSection.Visibility = System.Windows.Visibility.Visible;
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
            if (ImageColumns != settings.ImageColumns)
            {
                ImageColumns = settings.ImageColumns;
                RefreshImageDisplay();
            }
            
            // Apply image match width if changed
            if (ImageMatchWidth != settings.ImageMatchWidth)
            {
                ImageMatchWidth = settings.ImageMatchWidth;
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
                    case "Custom":
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
                            // Look for the toggle button specifically (contains eye emoji or is a toggle button)
                            string buttonContent = button.Content?.ToString() ?? "";
                            // Check for eye emoji in various forms, or check if it's a toggle button by looking at the button name
                            if (buttonContent.Contains("➖") || 
                                button.Name?.Contains("Toggle") == true)
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
            
            // Disable Hub buttons while loading packages
            _isLoadingPackages = true;
            DisableHubButtons();
            
            SetStatus("Scanning VAR files...");

            try
            {
                // Define VAM folder structure
                string addonPackagesFolder = Path.Combine(_selectedFolder, "AddonPackages");
                string allPackagesFolder = Path.Combine(_selectedFolder, "AllPackages");

                // Get ALL external destinations for scanning (we always scan all, visibility is controlled in UI)
                var allDestinations = _settingsManager?.Settings?.MoveToDestinations ?? new List<MoveToDestination>();

                // Always scan ALL valid destinations - ShowInMainTable only controls UI visibility, not scanning
                var externalDestinations = allDestinations
                    .Where(d => d.IsValid() && d.PathExists())
                    .ToList();

                // Scan for VAR files from multiple sources including external destinations
                List<string> installedFiles, availableFiles;
                Dictionary<string, List<string>> externalFiles;
                (installedFiles, availableFiles, externalFiles) = await _packageManager.ScanVarFilesWithExternalAsync(
                    addonPackagesFolder, allPackagesFolder, externalDestinations);


                var externalCount = externalFiles.Values.Sum(l => l.Count);
                SetStatus($"Found {installedFiles.Count + availableFiles.Count + externalCount} VAR files. Processing...");

                // Update package mapping with progress
                var progress = new Progress<(int current, int total)>(p =>
                {
                    SetStatus($"Processing packages... {p.current}/{p.total} ({(double)p.current/p.total*100:F1}%)");
                });

                // Use synchronous fast method with proper progress reporting
                await Task.Run(() =>
                {
                    _packageManager.UpdatePackageMappingFast(installedFiles, availableFiles, externalFiles, externalDestinations, progress);
                });

                // Initialize reactive filter manager with all packages for live count updates
                if (_reactiveFilterManager != null && _packageManager.PackageMetadata != null)
                {
                    _reactiveFilterManager.Initialize(_packageManager.PackageMetadata);
                }

                // Copy preview image index from ImageManager
                var totalImages = _imageManager.PreviewImageIndex.Values.Sum(list => list.Count);
                var totalPackagesWithImages = _imageManager.PreviewImageIndex.Count;
                var avgImagesPerPackage = totalPackagesWithImages > 0 ? (double)totalImages / totalPackagesWithImages : 0;

                // Reload favorites and autoinstall to get latest changes from game
                if (_favoritesManager != null)
                {
                    _favoritesManager.ReloadFavorites();
                }
                
                if (_autoInstallManager != null)
                {
                    _autoInstallManager.ReloadAutoInstall();
                }

                // Sync filter manager with current UI selections before updating package list
                // This ensures filters are preserved after hub download or other refresh operations
                UpdateFilterManagerFromUI();
                
                // Update UI with real package data
                await UpdatePackageListAsync();
                
                // Check for package updates after packages are loaded
                _ = CheckForPackageUpdatesAsync();

                // Build image index from cached metadata (fast - no VAR file opening required)
                // This uses VarMetadata.AllFiles which is already in the binary cache
                // Packages without AllFiles will have images indexed on-demand when selected
                if (_imageManager.ImageIndex.Count == 0)
                {
                    var packagesWithFiles = _packageManager.PackageMetadata.Count(p => p.Value.AllFiles?.Count > 0);
                    
                    if (packagesWithFiles > 0)
                    {
                        _imageManager.BuildImageIndexFromMetadata(_packageManager.PackageMetadata);
                    }
                }

                SetStatus("Ready");
                App.LogStartupTiming("RefreshPackages completed - app is now idle");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error refreshing packages: {ex.Message}", "Error", 
                               MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Package refresh failed");
            }
            finally
            {
                // Re-enable Hub buttons after loading completes
                // NOTE: _isLoadingPackages is now set to false in UpdatePackageListAsync after packages are loaded
                EnableHubButtons();
                
                // Incremental refresh disabled - using full refresh only
                // if (!string.IsNullOrEmpty(_selectedFolder))
                // {
                //     _incrementalRefresh?.StartMonitoring(_selectedFolder);
                // }
            }
        }

        /// <summary>
        /// Incremental refresh disabled - using full refresh only
        /// </summary>
        private async void RefreshPackagesIncremental()
        {
            // Incremental refresh disabled - always use full refresh
            RefreshPackages();
        }

        /// <summary>
        /// Incremental refresh disabled - using full refresh only
        /// </summary>
        private void OnIncrementalPackagesUpdated(object sender, IncrementalRefreshResult result)
        {
            // Incremental refresh disabled
        }

        /// <summary>
        /// Incremental refresh disabled - using full refresh only
        /// </summary>
        private void OnFullRefreshRecommended(object sender, EventArgs e)
        {
            // Incremental refresh disabled
        }

        /// <summary>
        /// Incremental refresh disabled - using full refresh only
        /// </summary>
        private void ApplyIncrementalUpdates(IncrementalRefreshResult result)
        {
            // Incremental refresh disabled - not applying incremental updates
            return;
        }

        /// <summary>
        /// Creates a PackageItem from VarMetadata for display in the UI
        /// </summary>
        private PackageItem CreatePackageItemFromMetadata(VarMetadata metadata)
        {
            if (metadata == null)
                return null;

            return new PackageItem
            {
                Name = $"{metadata.CreatorName}.{metadata.PackageName}",
                Creator = metadata.CreatorName,
                Status = metadata.Status, // StatusColor is computed from Status automatically
                FileSize = metadata.FileSize,
                ModifiedDate = metadata.ModifiedDate ?? metadata.CreatedDate,
                IsOptimized = metadata.IsOptimized,
                MetadataKey = GetMetadataKeyFromMetadata(metadata),
                MorphCount = metadata.MorphCount,
                HairCount = metadata.HairCount,
                ClothingCount = metadata.ClothingCount,
                SceneCount = metadata.SceneCount,
                IsDamaged = metadata.IsDamaged,
                DamageReason = metadata.DamageReason,
                ExternalDestinationName = metadata.ExternalDestinationName,
                ExternalDestinationColorHex = metadata.ExternalDestinationColorHex
            };
        }

        /// <summary>
        /// Generates a metadata key from VarMetadata
        /// </summary>
        private string GetMetadataKeyFromMetadata(VarMetadata metadata)
        {
            var baseKey = $"{metadata.CreatorName}.{metadata.PackageName}.{metadata.Version}";
            
            if (metadata.Status == "Archived")
                return $"{baseKey}#archived";
            if (metadata.Status == "Available")
                return $"{baseKey}#available";
            
            return baseKey;
        }

        private Task UpdatePackageListAsync(bool refreshFilterLists = true)
        {
            // Cancel any previous filter operation
            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filterCts = new System.Threading.CancellationTokenSource();
            var filterToken = _filterCts.Token;
            
            // Save current state
            var selectedPackageNames = PackageDataGrid?.SelectedItems?.Cast<PackageItem>()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            double scrollOffset = 0;
            if (PackageDataGrid != null)
            {
                var scrollViewer = FindVisualChild<ScrollViewer>(PackageDataGrid);
                scrollOffset = scrollViewer?.VerticalOffset ?? 0;
            }
            
            var savedSortDescriptions = PackagesView?.SortDescriptions?.ToList() 
                ?? new List<System.ComponentModel.SortDescription>();

            // For filter-only operations, use view filtering (memory efficient)
            if (!refreshFilterLists && Packages.Count > 0)
            {
                return ApplyViewFilterAsync(selectedPackageNames, scrollOffset, filterToken);
            }

            // Full refresh - clear and reload
            Packages.Clear();
            Dependencies.Clear();
            SetStatus("Loading packages...");
            
            // Load packages in background
            _ = Task.Run(async () =>
            {
                if (filterToken.IsCancellationRequested) return;
                
                try
                {
                    _versionCacheBuilt = false;
                    var dependentsCount = CalculateDependentsCount();
                    
                    // Rebuild cache if needed
                    var currentMetadataVersion = _packageManager.PackageMetadata.Count;
                    
                    // Also check if destination names have changed (for rename detection)
                    var currentDestinationNames = string.Join("|", 
                        (_settingsManager?.Settings?.MoveToDestinations ?? new List<MoveToDestination>())
                            .Where(d => d?.IsValid() == true)
                            .OrderBy(d => d.Name)
                            .Select(d => d.Name));
                    var currentDestinationHash = currentDestinationNames.GetHashCode().ToString();
                    
                    if (_packageItemCacheVersion != currentMetadataVersion || _cachedDestinationNamesHash != currentDestinationHash)
                    {
                        _packageItemCache.Clear();
                        _packageItemCacheVersion = currentMetadataVersion;
                        _cachedDestinationNamesHash = currentDestinationHash;
                    }

                    if (filterToken.IsCancellationRequested) return;
                    
                    var filterSnapshot = _filterManager.GetSnapshot();
                    
                    // Build external destination visibility lookup
                    var destVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dest in (_settingsManager?.Settings?.MoveToDestinations ?? new List<MoveToDestination>()))
                    {
                        if (dest?.IsValid() == true && !string.IsNullOrWhiteSpace(dest.Name))
                            destVisibility[dest.Name] = dest.ShowInMainTable;
                    }
                    
                    // Filter and create package items
                    var filteredItems = _packageManager.PackageMetadata
                        .AsParallel()
                        .WithCancellation(filterToken)
                        .WithDegreeOfParallelism(Environment.ProcessorCount)
                        .Where(kvp => ShouldIncludePackage(kvp.Value, kvp.Key, filterSnapshot, destVisibility))
                        .Select(kvp => GetOrCreatePackageItem(kvp.Key, kvp.Value, dependentsCount))
                        .ToList();

                    var processedCount = _packageManager.PackageMetadata.Count;
                    
                    // Handle duplicate filtering
                    List<PackageItem> allPackages;
                    if (_filterManager.FilterDuplicates)
                    {
                        allPackages = new List<PackageItem>();
                        var seenDuplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in filteredItems)
                        {
                            if (item.DuplicateLocationCount > 1)
                            {
                                var parts = item.Name.Split('.');
                                var basePackageName = parts.Length >= 3 ? $"{parts[0]}.{parts[1]}" : item.Name;
                                if (seenDuplicates.Contains(basePackageName)) continue;
                                seenDuplicates.Add(basePackageName);
                            }
                            allPackages.Add(item);
                        }
                    }
                    else
                    {
                        allPackages = filteredItems;
                    }

                    if (filterToken.IsCancellationRequested) return;
                    
                    // Update UI
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (filterToken.IsCancellationRequested) return;
                        
                        _suppressSelectionEvents = true;
                        try
                        {
                            if (PackagesView != null)
                            {
                                using (PackagesView.DeferRefresh())
                                {
                                    Packages.ReplaceAll(allPackages);
                                    PackagesView.Filter = null;
                                }
                            }
                            else
                            {
                                Packages.ReplaceAll(allPackages);
                            }
                        }
                        finally
                        {
                            _suppressSelectionEvents = false;
                        }

                        // Defer sorting and selection restoration
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _suppressSelectionEvents = true;
                            try
                            {
                                if (PackagesView != null) PackagesView.Filter = null;
                                
                                // Reapply sorting
                                var packageState = _sortingManager?.GetSortingState("Packages");
                                if (packageState?.CurrentSortOption is PackageSortOption)
                                {
                                    ReapplySorting();
                                }
                                else if (savedSortDescriptions.Count > 0 && PackagesView != null)
                                {
                                    using (PackagesView.DeferRefresh())
                                    {
                                        PackagesView.SortDescriptions.Clear();
                                        foreach (var sortDesc in savedSortDescriptions)
                                            PackagesView.SortDescriptions.Add(sortDesc);
                                    }
                                }

                                // Restore selection
                                if (selectedPackageNames.Count > 0 && PackageDataGrid != null)
                                {
                                    foreach (var item in Packages)
                                    {
                                        if (selectedPackageNames.Contains(item.Name))
                                            PackageDataGrid.SelectedItems.Add(item);
                                    }
                                }
                                
                                UpdateOptimizeCounter();
                            }
                            finally
                            {
                                _suppressSelectionEvents = false;
                            }

                            // Restore scroll position
                            _ = Dispatcher.BeginInvoke(new Action(async () =>
                            {
                                await Task.Delay(50);
                                var scrollViewer = PackageDataGrid != null ? FindVisualChild<ScrollViewer>(PackageDataGrid) : null;
                                scrollViewer?.ScrollToVerticalOffset(scrollOffset);
                                if (PackageDataGrid?.SelectedItems?.Count > 0)
                                    _ = RefreshSelectionDisplaysImmediate();
                            }), DispatcherPriority.ContextIdle);
                        }), DispatcherPriority.ContextIdle);

                        // Update status
                        var uniquePackageCount = allPackages
                            .Select(p => p.Name.EndsWith("#archived", StringComparison.OrdinalIgnoreCase) 
                                ? p.Name.Substring(0, p.Name.Length - 9) : p.Name)
                            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                        
                        var uniqueTotalCount = _packageManager.PackageMetadata.Keys
                            .Select(k => k.EndsWith("#archived", StringComparison.OrdinalIgnoreCase) 
                                ? k.Substring(0, k.Length - 9) : k)
                            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

                        SetStatus(allPackages.Count == processedCount
                            ? $"Showing all {allPackages.Count:N0} entries ({uniquePackageCount:N0} unique packages)"
                            : $"Showing {allPackages.Count:N0} of {processedCount:N0} entries ({uniquePackageCount:N0} of {uniqueTotalCount:N0} unique packages)");
                        
                        // Refresh filter lists AFTER packages are loaded
                        // NOTE: _isLoadingPackages will be set to false at the END of RefreshFilterLists
                        // after all UI updates complete, not here
                        if (refreshFilterLists)
                        {
                            _ = Task.Run(() => RefreshFilterLists());
                        }
                        else
                        {
                            _isLoadingPackages = false;
                        }
                    }, DispatcherPriority.Normal);
                }
                catch (Exception)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        SetStatus("Error loading packages");
                        _isLoadingPackages = false;
                    });
                }
            });

            UpdateUI();
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Determines if a package should be included in the filtered results.
        /// External packages have special filtering rules but still respect search text.
        /// </summary>
        private bool ShouldIncludePackage(VarMetadata metadata, string key, FilterState filterSnapshot, Dictionary<string, bool> destVisibility)
        {
            // External packages have special filtering rules
            if (metadata.IsExternal)
            {
                // Only hide if explicitly configured as hidden (ShowInMainTable=false)
                // If destination not found in config, INCLUDE the package (fail-open)
                if (destVisibility.TryGetValue(metadata.ExternalDestinationName, out var showInTable) && !showInTable)
                    return false;
                
                // Apply search text filter to external packages
                if (!string.IsNullOrEmpty(filterSnapshot.SearchText))
                {
                    string packageName = key.EndsWith("#archived", StringComparison.OrdinalIgnoreCase) 
                        ? key : Path.GetFileNameWithoutExtension(metadata.Filename);
                    if (!SearchHelper.MatchesPackageSearch(packageName, filterSnapshot.SearchTerms))
                        return false;
                }
                
                // If a destination filter is active, only include if this package matches the selected destination
                if (filterSnapshot.SelectedDestinations.Count > 0)
                {
                    // Build the full destination key including subfolder if present
                    string packageDestKey = metadata.ExternalDestinationName;
                    if (!string.IsNullOrEmpty(metadata.ExternalDestinationSubfolder))
                    {
                        packageDestKey = $"{metadata.ExternalDestinationName}/{metadata.ExternalDestinationSubfolder}";
                    }
                    
                    return filterSnapshot.SelectedDestinations.Contains(packageDestKey);
                }

                bool externalExplicitlySelected = filterSnapshot.SelectedStatuses.Contains("External");
                bool localExplicitlySelected = filterSnapshot.SelectedStatuses.Contains("Local");
                
                if (localExplicitlySelected && !externalExplicitlySelected)
                {
                    return false;
                }
                
                bool hasAnyNonExternalFilterActive =
                    (filterSnapshot.SelectedStatuses.Count > 0 && !externalExplicitlySelected && !localExplicitlySelected) ||
                    filterSnapshot.SelectedFavoriteStatuses.Count > 0 ||
                    filterSnapshot.SelectedAutoInstallStatuses.Count > 0 ||
                    filterSnapshot.SelectedOptimizationStatuses.Count > 0 ||
                    filterSnapshot.SelectedVersionStatuses.Count > 0 ||
                    filterSnapshot.FilterDuplicates ||
                    filterSnapshot.FilterNoDependents ||
                    filterSnapshot.FilterNoDependencies ||
                    filterSnapshot.SelectedCategories.Count > 0 ||
                    filterSnapshot.SelectedCreators.Count > 0 ||
                    filterSnapshot.SelectedLicenseTypes.Count > 0 ||
                    filterSnapshot.SelectedFileSizeRanges.Count > 0 ||
                    filterSnapshot.SelectedSubfolders.Count > 0 ||
                    !string.IsNullOrEmpty(filterSnapshot.SelectedDamagedFilter) ||
                    (filterSnapshot.DateFilter != null && filterSnapshot.DateFilter.FilterType != DateFilterType.AllTime);

                if (hasAnyNonExternalFilterActive && !externalExplicitlySelected)
                {
                    return false;
                }

                // External packages are shown (unless hidden via ShowInMainTable above or search filter)
                return true;
            }
            
            // Non-external packages go through normal filtering
            return _filterManager.MatchesFilters(metadata, filterSnapshot, key);
        }
        
        /// <summary>
        /// Gets a cached PackageItem or creates a new one.
        /// </summary>
        private PackageItem GetOrCreatePackageItem(string metadataKey, VarMetadata metadata, Dictionary<string, int> dependentsCount)
        {
            if (_packageItemCache.TryGetValue(metadataKey, out var cachedItem))
            {
                cachedItem.IsFavorite = _favoritesManager?.IsFavorite(cachedItem.Name) ?? false;
                cachedItem.IsAutoInstall = _autoInstallManager?.IsAutoInstall(cachedItem.Name) ?? false;
                return cachedItem;
            }
            
            string packageName = metadataKey.EndsWith("#archived", StringComparison.OrdinalIgnoreCase) 
                ? metadataKey : Path.GetFileNameWithoutExtension(metadata.Filename);
            
            var newItem = new PackageItem
            {
                MetadataKey = metadataKey,
                Name = packageName,
                Status = metadata.Status,
                Creator = metadata.CreatorName,
                DependencyCount = metadata.Dependencies?.Count ?? 0,
                DependentsCount = dependentsCount.TryGetValue(packageName, out var count) ? count : 0,
                FileSize = metadata.FileSize,
                ModifiedDate = metadata.ModifiedDate,
                IsLatestVersion = true,
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
                SkinsCount = metadata.SkinsCount,
                MissingDependencyCount = metadata.MissingDependencyCount,
                ExternalDestinationName = metadata.ExternalDestinationName,
                ExternalDestinationColorHex = metadata.ExternalDestinationColorHex,
                OriginalExternalDestinationColorHex = metadata.OriginalExternalDestinationColorHex
            };
            
            _packageItemCache[metadataKey] = newItem;
            return newItem;
        }
        
        /// <summary>
        /// Apply filtering using CollectionView.Filter (memory efficient - O(1) vs O(n) for ReplaceAll).
        /// </summary>
        private Task ApplyViewFilterAsync(HashSet<string> selectedPackageNames, double scrollOffset, System.Threading.CancellationToken filterToken)
        {
            var filterSnapshot = _filterManager.GetSnapshot();
            
            // Build external destination visibility lookup
            var destVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var dest in (_settingsManager?.Settings?.MoveToDestinations ?? new List<MoveToDestination>()))
            {
                if (dest?.IsValid() == true && !string.IsNullOrWhiteSpace(dest.Name))
                    destVisibility[dest.Name] = dest.ShowInMainTable;
            }
            
            // Build matching keys using the same logic as UpdatePackageListAsync
            var matchingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _packageManager.PackageMetadata)
            {
                if (filterToken.IsCancellationRequested) break;
                if (ShouldIncludePackage(kvp.Value, kvp.Key, filterSnapshot, destVisibility))
                    matchingKeys.Add(kvp.Key);
            }
            
            if (filterToken.IsCancellationRequested) return Task.CompletedTask;
            
            // Apply filter to view
            _suppressSelectionEvents = true;
            try
            {
                if (PackagesView != null)
                {
                    using (PackagesView.DeferRefresh())
                    {
                        PackagesView.Filter = item => item is PackageItem pkg && matchingKeys.Contains(pkg.MetadataKey);
                    }
                }
                
                ReapplySorting();
                
                // Restore selection
                if (selectedPackageNames.Count > 0 && PackageDataGrid != null)
                {
                    foreach (var item in PackagesView)
                    {
                        if (item is PackageItem pkg && selectedPackageNames.Contains(pkg.Name))
                            PackageDataGrid.SelectedItems.Add(pkg);
                    }
                }
                
                // Update status
                var visibleCount = PackagesView?.Cast<object>().Count() ?? 0;
                var totalCount = Packages.Count;
                SetStatus(totalCount == visibleCount 
                    ? $"Showing all {totalCount:N0} packages"
                    : $"Showing {visibleCount:N0} of {totalCount:N0} packages");
                
                // Restore scroll position
                var scrollViewer = PackageDataGrid != null ? FindVisualChild<ScrollViewer>(PackageDataGrid) : null;
                scrollViewer?.ScrollToVerticalOffset(scrollOffset);
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
            
            return Task.CompletedTask;
        }

        private void RefreshFilterLists()
        {
            if (_packageManager?.PackageMetadata == null) 
            {
                return;
            }
            
            // CRITICAL FIX: This method now runs on background thread
            // Build all filter data on background thread first, then update UI on UI thread
            
            // CRITICAL: Suppress selection events while updating filter lists to prevent ApplyFilters from being called
            bool savedSuppressSelectionEvents = _suppressSelectionEvents;
            _suppressSelectionEvents = true;
            
            try
            {
                // In unlinked mode, we need to show counts based on filtered packages
                // to avoid showing creators/categories that have no matching packages
                var packagesToCount = _packageManager.PackageMetadata;
                var allPackagesForDestinationCounts = _packageManager.PackageMetadata; // Always use all packages for destination counts
                
                if (!_cascadeFiltering)
                {
                    // MEMORY FIX: Create filter snapshot ONCE before PLINQ query
                    var filterSnapshot = _filterManager.GetSnapshot();
                    
                    // Build filtered package set for counting (background thread safe)
                    // Use PLINQ for parallel filtering
                    var filteredPackages = _packageManager.PackageMetadata
                        .AsParallel()
                        .Where(kvp => _filterManager.MatchesFilters(kvp.Value, filterSnapshot, kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    
                    packagesToCount = filteredPackages;
                }
                
                // Build all filter data on background thread
                var creatorCounts = _filterManager.GetCreatorCounts(packagesToCount);
                var categoryCounts = _filterManager.GetCategoryCounts(packagesToCount);
                var statusCounts = _filterManager.GetStatusCounts(packagesToCount);
                var optCounts = _filterManager.GetOptimizationStatusCounts(packagesToCount);
                var versionCounts = _filterManager.GetVersionStatusCounts(packagesToCount);
                var licenseCounts = _filterManager.GetLicenseCounts(packagesToCount);
                var fileSizeCounts = _filterManager.GetFileSizeCounts(packagesToCount);
                var subfolderCounts = _filterManager.GetSubfolderCounts(packagesToCount);
                var dateCounts = GetDateFilterCounts(packagesToCount);
                var destinationCountsFiltered = _filterManager.GetDestinationCounts(packagesToCount);
                var destinationCountsAll = _filterManager.GetDestinationCounts(_packageManager.PackageMetadata);

                // Build set of nested destination names to exclude from filter list
                var nestedDestinationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var configuredDestinations = _settingsManager?.Settings?.MoveToDestinations;
                if (configuredDestinations != null)
                {
                    foreach (var dest in configuredDestinations)
                    {
                        if (dest == null || !dest.IsValid())
                            continue;
                        
                        var destPath = System.IO.Path.GetFullPath(dest.Path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                        
                        // Check if this destination is nested inside another configured destination
                        foreach (var other in configuredDestinations)
                        {
                            if (other == null || !other.IsValid() || other.Name.Equals(dest.Name, StringComparison.OrdinalIgnoreCase))
                                continue;
                            
                            var otherPath = System.IO.Path.GetFullPath(other.Path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                            
                            // If destPath is inside otherPath, mark it as nested
                            if (destPath.StartsWith(otherPath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            {
                                nestedDestinationNames.Add(dest.Name);
                                break;
                            }
                        }
                    }
                }

                var destinationNamesAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in destinationCountsAll.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(name) && !nestedDestinationNames.Contains(name))
                        destinationNamesAll.Add(name);
                }

                if (configuredDestinations != null)
                {
                    foreach (var dest in configuredDestinations)
                    {
                        if (dest == null || !dest.IsEnabled || !dest.IsValid())
                            continue;

                        // Skip nested destinations - they shouldn't appear as separate filter options
                        if (nestedDestinationNames.Contains(dest.Name))
                            continue;

                        if (!string.IsNullOrWhiteSpace(dest.Name))
                            destinationNamesAll.Add(dest.Name);
                    }
                }
                
                // Get favorites and autoinstall counts
                int favoriteCount = 0;
                int autoInstallCount = 0;
                if (_favoritesManager != null)
                {
                    var favorites = _favoritesManager.GetAllFavorites();
                    // Use parallel processing for counting
                    favoriteCount = _packageManager.PackageMetadata.AsParallel().Count(kvp => 
                    {
                        var pkgName = !string.IsNullOrEmpty(kvp.Value.PackageName) 
                            ? kvp.Value.PackageName 
                            : System.IO.Path.GetFileNameWithoutExtension(kvp.Value.Filename);
                        return favorites.Contains(pkgName);
                    });
                }
                
                if (_autoInstallManager != null)
                {
                    var autoInstall = _autoInstallManager.GetAllAutoInstall();
                    // Use parallel processing for counting
                    autoInstallCount = _packageManager.PackageMetadata.AsParallel().Count(kvp => 
                    {
                        var pkgName = !string.IsNullOrEmpty(kvp.Value.PackageName) 
                            ? kvp.Value.PackageName 
                            : System.IO.Path.GetFileNameWithoutExtension(kvp.Value.Filename);
                        return autoInstall.Contains(pkgName);
                    });
                }
                
                // Now update UI on UI thread with all pre-built data
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _suppressSelectionEvents = true;
                    try
                    {
                        // Update creators list
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
                        
                        var creatorFilterText = GetSearchText(CreatorsFilterBox);
                        CreatorsList.Items.Clear();
                        
                        var topCreators = creatorCounts
                            .OrderByDescending(c => c.Value)
                            .Take(500)
                            .OrderBy(c => c.Key)
                            .ToList();

                        foreach (var creator in topCreators)
                        {
                            if (!string.IsNullOrWhiteSpace(creatorFilterText) && 
                                creator.Key.IndexOf(creatorFilterText, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }
                            
                            var displayText = $"{creator.Key} ({creator.Value})";
                            CreatorsList.Items.Add(displayText);
                            
                            if (selectedCreators.Contains(creator.Key))
                            {
                                CreatorsList.SelectedItems.Add(displayText);
                            }
                        }

                        // Update content types list
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
                        
                        ContentTypesList.Items.Clear();
                        foreach (var category in categoryCounts.OrderBy(c => c.Key))
                        {
                            var displayText = $"{category.Key} ({category.Value:N0})";
                            ContentTypesList.Items.Add(displayText);
                            
                            if (selectedContentTypes.Contains(category.Key))
                            {
                                ContentTypesList.SelectedItems.Add(displayText);
                            }
                        }

                        // Update status filter
                        var selectedStatuses = new List<string>();
                        foreach (var item in StatusFilterList.SelectedItems)
                        {
                            string itemText = item?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(itemText))
                            {
                                var statusName = itemText.Split('(')[0].Trim();
                                if (statusName.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                                {
                                    statusName = "Duplicate";
                                }
                                selectedStatuses.Add(statusName);
                            }
                        }
                        
                        StatusFilterList.Items.Clear();
                        foreach (var status in statusCounts.OrderBy(s => s.Key))
                        {
                            var displayName = status.Key.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) ? "Duplicates" : status.Key;
                            var displayText = $"{displayName} ({status.Value:N0})";
                            StatusFilterList.Items.Add(displayText);
                            
                            if (selectedStatuses.Contains(status.Key))
                            {
                                StatusFilterList.SelectedItems.Add(displayText);
                            }
                        }

                        foreach (var opt in optCounts.OrderBy(s => s.Key))
                        {
                            var displayText = $"{opt.Key} ({opt.Value:N0})";
                            StatusFilterList.Items.Add(displayText);
                            
                            if (selectedStatuses.Contains(opt.Key))
                            {
                                StatusFilterList.SelectedItems.Add(displayText);
                            }
                        }
                        
                        foreach (var ver in versionCounts.OrderBy(s => s.Key))
                        {
                            var displayText = $"{ver.Key} ({ver.Value:N0})";
                            StatusFilterList.Items.Add(displayText);
                            
                            if (selectedStatuses.Contains(ver.Key))
                            {
                                StatusFilterList.SelectedItems.Add(displayText);
                            }
                        }

                        // Add dependency status counts (No Dependents / No Dependencies)
                        var depCounts = _filterManager.GetDependencyStatusCounts(_packageManager.PackageMetadata);
                        foreach (var dep in depCounts.OrderBy(s => s.Key))
                        {
                            var displayText = $"{dep.Key} ({dep.Value:N0})";
                            StatusFilterList.Items.Add(displayText);
                            
                            if (selectedStatuses.Contains(dep.Key))
                            {
                                StatusFilterList.SelectedItems.Add(displayText);
                            }
                        }

                        // Add External/Local package type filters
                        var externalCount = packagesToCount.Values.Count(p => p.IsExternal);
                        var localCount = packagesToCount.Values.Count(p => !p.IsExternal);
                        
                        if (externalCount > 0)
                        {
                            var displayText = $"External ({externalCount:N0})";
                            StatusFilterList.Items.Add(displayText);
                            if (selectedStatuses.Contains("External"))
                            {
                                StatusFilterList.SelectedItems.Add(displayText);
                            }
                        }
                        
                        if (localCount > 0)
                        {
                            var displayText = $"Local ({localCount:N0})";
                            StatusFilterList.Items.Add(displayText);
                            if (selectedStatuses.Contains("Local"))
                            {
                                StatusFilterList.SelectedItems.Add(displayText);
                            }
                        }

                        if (_favoritesManager != null)
                        {
                            var favText = $"Favorites ({favoriteCount:N0})";
                            StatusFilterList.Items.Add(favText);
                            
                            if (selectedStatuses.Contains("Favorites"))
                            {
                                StatusFilterList.SelectedItems.Add(favText);
                            }
                        }

                        if (_autoInstallManager != null && _packageManager?.PackageMetadata != null)
                        {
                            var autoInstallText = $"AutoInstall ({autoInstallCount:N0})";
                            StatusFilterList.Items.Add(autoInstallText);
                            
                            if (selectedStatuses.Contains("AutoInstall"))
                            {
                                StatusFilterList.SelectedItems.Add(autoInstallText);
                            }
                        }

                        // Update license types list
                        if (LicenseTypeList != null)
                        {
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
                            
                            LicenseTypeList.Items.Clear();
                            foreach (var license in licenseCounts.OrderBy(l => l.Key))
                            {
                                var displayText = $"{license.Key} ({license.Value:N0})";
                                LicenseTypeList.Items.Add(displayText);
                                
                                if (selectedLicenseTypes.Contains(license.Key))
                                {
                                    LicenseTypeList.SelectedItems.Add(displayText);
                                }
                            }
                        }

                        // Update date filter list
                        if (DateFilterList != null)
                        {
                            var selectedTag = "";
                            if (DateFilterList.SelectedItem is ListBoxItem selectedItem)
                            {
                                selectedTag = selectedItem.Tag?.ToString() ?? "";
                            }

                            DateFilterList.Items.Clear();
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

                                if (option.Tag == selectedTag || (string.IsNullOrEmpty(selectedTag) && option.Tag == "AllTime"))
                                {
                                    DateFilterList.SelectedItem = displayText;
                                }
                            }
                        }

                        // Update file size filter list
                        if (FileSizeFilterList != null && _filterManager != null)
                        {
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
                            
                            FileSizeFilterList.Items.Clear();
                            
                            var orderedRanges = new[] { "Tiny", "Small", "Medium", "Large" };
                            foreach (var range in orderedRanges)
                            {
                                if (fileSizeCounts.ContainsKey(range) && fileSizeCounts[range] > 0)
                                {
                                    var displayText = $"{range} ({fileSizeCounts[range]:N0})";
                                    FileSizeFilterList.Items.Add(displayText);
                                    
                                    if (selectedFileSizeRanges.Contains(range))
                                    {
                                        FileSizeFilterList.SelectedItems.Add(displayText);
                                    }
                                }
                            }
                        }

                        // Update subfolders filter list
                        if (SubfoldersFilterList != null && _filterManager != null)
                        {
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
                            
                            SubfoldersFilterList.Items.Clear();
                            
                            var sortedSubfolders = subfolderCounts.Keys.OrderBy(k => k).ToList();
                            foreach (var subfolder in sortedSubfolders)
                            {
                                if (subfolderCounts[subfolder] > 0)
                                {
                                    var displayText = $"{subfolder} ({subfolderCounts[subfolder]:N0})";
                                    SubfoldersFilterList.Items.Add(displayText);
                                    
                                    if (selectedSubfolders.Contains(subfolder))
                                    {
                                        SubfoldersFilterList.SelectedItems.Add(displayText);
                                    }
                                }
                            }
                        }

                        // Update destinations filter list
                        if (DestinationsFilterList != null)
                        {
                            
                            var selectedDestinations = new List<string>();
                            foreach (var item in DestinationsFilterList.SelectedItems)
                            {
                                string itemText = item?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(itemText))
                                {
                                    var destName = itemText.Split('(')[0].Trim().Replace(" [Hidden]", "").Trim();
                                    selectedDestinations.Add(destName);
                                }
                            }
                            
                            // Build visibility lookup
                            var destVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                            foreach (var dest in (_settingsManager?.Settings?.MoveToDestinations ?? new List<MoveToDestination>()))
                            {
                                if (dest?.IsValid() == true && !string.IsNullOrWhiteSpace(dest.Name))
                                    destVisibility[dest.Name] = dest.ShowInMainTable;
                            }
                            
                            DestinationsFilterList.Items.Clear();
                            
                            foreach (var destName in destinationNamesAll.OrderBy(d => d))
                            {
                                // Count ALL packages in THIS specific destination (not just filtered)
                                // This shows users how many packages are available in each destination
                                var packagesInDest = allPackagesForDestinationCounts.Values
                                    .Where(p => p.IsExternal && 
                                               !string.IsNullOrEmpty(p.ExternalDestinationName) && 
                                               p.ExternalDestinationName.Equals(destName, StringComparison.OrdinalIgnoreCase))
                                    .ToList();
                                
                                var totalCount = packagesInDest.Count;
                                
                                // Append "Hidden" tag if ShowInMainTable is false
                                bool isHidden = destVisibility.TryGetValue(destName, out var showInTable) && !showInTable;
                                var displayText = isHidden
                                    ? $"{destName} ({totalCount:N0}) Hidden"
                                    : $"{destName} ({totalCount:N0})";
                                
                                DestinationsFilterList.Items.Add(displayText);
                                
                                if (selectedDestinations.Contains(destName))
                                {
                                    DestinationsFilterList.SelectedItems.Add(displayText);
                                }
                            }
                            
                        }
                        
                        // Restore filter list sorting after lists are populated
                        RestoreFilterListsSorting();
                        
                        // Force refresh of the UI to ensure selections are properly displayed
                        StatusFilterList?.Items.Refresh();
                        
                        // CRITICAL: Set _isLoadingPackages=false AFTER all UI updates complete
                        // This prevents ApplyFilters from being called during the refresh
                        _isLoadingPackages = false;
                    }
                    finally
                    {
                        _suppressSelectionEvents = savedSuppressSelectionEvents;
                    }
                });
            }
            catch (Exception)
            {
                _suppressSelectionEvents = savedSuppressSelectionEvents;
            }
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
        /// Calculate the number of dependents for each package using the dependency graph
        /// This is now O(n) instead of O(n²) thanks to the pre-built graph
        /// </summary>
        private Dictionary<string, int> CalculateDependentsCount()
        {
            // Use cached result if PackageMetadata hasn't changed
            if (_cachedDependentsCount != null && _packageManager?.PackageMetadata?.Count == _cachedPackageMetadataVersion)
            {
                return _cachedDependentsCount;
            }
            
            var dependentsCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            if (_packageManager?.PackageMetadata == null)
                return dependentsCount;
            
            // Use the dependency graph for O(1) lookups per package
            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var metadata = kvp.Value;
                var packageFullName = $"{metadata.CreatorName}.{metadata.PackageName}.{metadata.Version}";
                var displayName = Path.GetFileNameWithoutExtension(metadata.Filename);
                
                // Get dependents count from the graph
                var count = _packageManager.GetPackageDependentsCount(packageFullName);
                
                if (count > 0)
                {
                    dependentsCount[displayName] = count;
                }
            }
            
            // Cache the result
            _cachedDependentsCount = dependentsCount;
            _cachedPackageMetadataVersion = _packageManager.PackageMetadata.Count;
            
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

        /// <summary>
        /// Greys out the VaM Hub and Updates buttons during package loading
        /// </summary>
        private void DisableHubButtons()
        {
            // Use BeginInvoke to prevent UI blocking when called from background threads
            Dispatcher.BeginInvoke(() =>
            {
                if (VamHubImageButton != null)
                {
                    VamHubImageButton.IsEnabled = false;
                    VamHubImageButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60));
                    VamHubImageButton.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100));
                }
                if (CheckUpdatesImageButton != null)
                {
                    CheckUpdatesImageButton.IsEnabled = false;
                    CheckUpdatesImageButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60));
                    CheckUpdatesImageButton.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100));
                }
            });
        }

        /// <summary>
        /// Restores the VaM Hub and Updates buttons after package loading completes
        /// </summary>
        private void EnableHubButtons()
        {
            // Use BeginInvoke to prevent UI blocking when called from background threads
            Dispatcher.BeginInvoke(() =>
            {
                if (VamHubImageButton != null)
                {
                    VamHubImageButton.IsEnabled = true;
                    VamHubImageButton.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
                    VamHubImageButton.ClearValue(System.Windows.Controls.Button.ForegroundProperty);
                }
                if (CheckUpdatesImageButton != null)
                {
                    CheckUpdatesImageButton.IsEnabled = true;
                    CheckUpdatesImageButton.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
                    CheckUpdatesImageButton.ClearValue(System.Windows.Controls.Button.ForegroundProperty);
                }
            });
        }

        #endregion
    }
}

