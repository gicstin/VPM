using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// Event handlers functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        #region Console P/Invoke
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();
        
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
        
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        
        #endregion
        #region Selection Event Handlers
        
        // Drag selection variables
        private bool _isDragging = false;
        private Point _dragStartPoint;
        private object _dragStartItem = null; // Can be DataGridRow or ListViewItem
        private MouseButton? _dragButton = null;
        private DispatcherTimer _dragWatchTimer;

        // Track currently displayed selection to prevent duplicate image loading
        private List<string> _currentlyDisplayedPackages = new List<string>();
        private List<string> _currentlyDisplayedDependencies = new List<string>();
        
        // Cached package lookup for ConvertDependenciesToPackages (avoids rebuilding on every call)
        private Dictionary<string, List<(string key, int version)>> _packageLookupCache;
        private int _packageLookupCacheVersion = -1;
        
        // Debounce timer for dependency selection changes
        private DispatcherTimer _dependencySelectionDebounceTimer;
        
        // Debounce timers for search boxes
        private DispatcherTimer _packageSearchDebounceTimer;
        private DispatcherTimer _depsSearchDebounceTimer;
        private DispatcherTimer _creatorsSearchDebounceTimer;
        
        // Flag to prevent concurrent image display operations


        private void PackageDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Auto-select duplicate counterparts
            if (e?.AddedItems != null && e.AddedItems.Count > 0 && !_suppressSelectionEvents)
            {
                _suppressSelectionEvents = true;
                try
                {
                    foreach (var addedItem in e.AddedItems)
                    {
                        if (addedItem is PackageItem pkg && pkg.IsDuplicate)
                        {
                            // Find and select the duplicate counterpart
                            if (PackageDataGrid.ItemsSource != null)
                            {
                                foreach (var item in PackageDataGrid.ItemsSource)
                                {
                                    if (item is PackageItem otherPkg && 
                                        otherPkg.IsDuplicate && 
                                        otherPkg.DisplayName == pkg.DisplayName &&
                                        otherPkg.Name != pkg.Name && // Different entry (one is #loaded)
                                        !PackageDataGrid.SelectedItems.Contains(otherPkg))
                                    {
                                        PackageDataGrid.SelectedItems.Add(otherPkg);
                                        break; // Only one counterpart per package
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _suppressSelectionEvents = false;
                }
            }
            
            // Update toolbar buttons
            UpdateToolbarButtons();
            UpdateOptimizeCounter();
            UpdateFavoriteCounter();
            UpdateAutoinstallCounter();
            UpdateHideCounter();
            
            // Update Hub Overview tab visibility based on selection count
            UpdateHubOverviewTabVisibility();
            
            if (_suppressSelectionEvents) return;

            if (PackageDataGrid?.SelectedItems?.Count == 0)
            {
                Dependencies.Clear();
                DependenciesCountText.Text = "(0)";
                DependentsCountText.Text = "(0)";
                ClearCategoryTabs();
                ClearImageGrid();
                SetStatus("No packages selected");
                return;
            }

            // If drag selection is in progress, skip image loading and wait for mouse release
            if (_isDragging)
            {
                // Start or restart the drag watch timer to detect when drag ends
                if (_dragWatchTimer == null)
                {
                    _dragWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                    _dragWatchTimer.Tick += DragWatchTimer_Tick;
                }
                _dragWatchTimer.Start();
                return; // Skip image loading during drag
            }

            // Cancel any pending package selection update
            _packageSelectionCts?.Cancel();
            _packageSelectionCts?.Dispose();
            _packageSelectionCts = new System.Threading.CancellationTokenSource();
            var packageToken = _packageSelectionCts.Token;

            // Trigger debounced package selection handler
            _packageSelectionDebouncer?.Trigger();

            // Schedule the actual content update after debounce delay
            _ = Task.Delay(SELECTION_DEBOUNCE_DELAY_MS, packageToken).ContinueWith(_ =>
            {
                // Check if this operation was cancelled
                if (packageToken.IsCancellationRequested)
                    return;

                Dispatcher.Invoke(async () =>
                {
                    // Safeguard: if selection is too large, avoid heavy work
                    if (PackageDataGrid?.SelectedItems?.Count > _settingsManager.Settings.MaxSafeSelection)
                    {
                        PackageInfoTextBlock.Text = $"{PackageDataGrid.SelectedItems.Count} packages selected – selection too large to preview\n\n" +
                            $"Preview limit: {_settingsManager.Settings.MaxSafeSelection} packages (configurable via Config ' Preview Selection Limit)";
                        PreviewImages.Clear();
                        Dependencies.Clear();
                        ClearCategoryTabs();
                        UpdatePackageButtonBar();
                        UpdatePackageSearchClearButton();
                        return;
                    }

                    await RefreshSelectionDisplaysImmediate();
                    
                    // Update only package search clear button visibility after main table selection changes
                    UpdatePackageSearchClearButton();
                });
            });
        }

        private void StatusFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Prevent recursion during programmatic updates
            if (_suppressSelectionEvents) return;
            
            // Apply filters immediately when selection changes
            ApplyFilters();
            // Status filter doesn't have its own clear button, so update all
            UpdateClearButtonVisibility();
        }

        private void CreatorsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Prevent recursion during programmatic updates
            if (_suppressSelectionEvents) return;

            // Apply filters immediately when selection changes
            ApplyFilters();
            // Update only creators clear button
            UpdateCreatorsClearButton();
        }

        private void ContentTypesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Prevent recursion during programmatic updates
            if (_suppressSelectionEvents) return;

            // Apply filters immediately when selection changes
            ApplyFilters();
            // Update only content types clear button
            UpdateContentTypesClearButton();
        }

        private void LicenseTypeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Prevent recursion during programmatic updates
            if (_suppressSelectionEvents) return;

            // Apply filters immediately when selection changes
            ApplyFilters();
            // Update only license type clear button
            UpdateLicenseTypeClearButton();
        }

        private void FileSizeFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Prevent recursion during programmatic updates
            if (_suppressSelectionEvents) return;

            // Apply filters immediately when selection changes
            ApplyFilters();
        }

        private void SubfoldersFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Prevent recursion during programmatic updates
            if (_suppressSelectionEvents) return;

            // Apply filters immediately when selection changes
            ApplyFilters();
        }

        private void DamagedFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Prevent recursion during programmatic updates
            if (_suppressSelectionEvents) return;

            // Apply filters immediately when selection changes
            ApplyFilters();
        }

        private void DependenciesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Skip if selection events are suppressed
            if (_suppressDependenciesSelectionEvents)
                return;
                
            // Update dependencies button bar based on selection
            UpdateDependenciesButtonBar();

            // Update only deps search clear button visibility after deps selection changes
            UpdateDepsSearchClearButton();
            
            // Update download button visibility based on missing dependencies
            UpdateDownloadMissingButtonVisibility();

            // Debounce the image display to prevent excessive reloading during rapid selection changes
            DebounceDependencyImageDisplay();
        }

        private void DebounceDependencyImageDisplay()
        {
            // Cancel any pending dependency image display
            _dependencySelectionDebounceTimer?.Stop();
            
            // Create new timer for debounced dependency image display
            _dependencySelectionDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(75) // Fast debounce for responsive UI
            };
            
            _dependencySelectionDebounceTimer.Tick += (s, args) =>
            {
                _dependencySelectionDebounceTimer.Stop();
                DisplaySelectedDependenciesImages();
            };
            
            _dependencySelectionDebounceTimer.Start();
        }

        #endregion

        #region Text Change Handlers

        private void PackageSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && PackageDataGrid != null && this.IsLoaded)
            {
                // Update package search clear button visibility immediately for responsiveness
                UpdatePackageSearchClearButton();

                // Debounce the search
                _packageSearchDebounceTimer?.Stop();
                if (_packageSearchDebounceTimer == null)
                {
                    _packageSearchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _packageSearchDebounceTimer.Tick += (s, args) =>
                    {
                        _packageSearchDebounceTimer.Stop();
                        try
                        {
                            var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                            
                            // If showing placeholder text OR text is empty, show all items
                            if (textBox.Foreground.Equals(grayBrush) || string.IsNullOrWhiteSpace(textBox.Text))
                            {
                                FilterPackages(""); // Empty string to show all
                            }
                            else
                            {
                                FilterPackages(textBox.Text);
                            }
                        }
                        catch
                        {
                            // Ignore errors
                        }
                    };
                }
                _packageSearchDebounceTimer.Start();
            }
        }
        
        private void DepsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && DependenciesDataGrid != null && this.IsLoaded)
            {
                // Update deps search clear button visibility immediately
                UpdateDepsSearchClearButton();

                // Debounce the search
                _depsSearchDebounceTimer?.Stop();
                if (_depsSearchDebounceTimer == null)
                {
                    _depsSearchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _depsSearchDebounceTimer.Tick += (s, args) =>
                    {
                        _depsSearchDebounceTimer.Stop();
                        try
                        {
                            var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                            
                            // If showing placeholder text OR text is empty, show all items
                            if (textBox.Foreground.Equals(grayBrush) || string.IsNullOrWhiteSpace(textBox.Text))
                            {
                                FilterDependencies(""); // Empty string to show all
                            }
                            else
                            {
                                FilterDependencies(textBox.Text);
                            }
                        }
                        catch
                        {
                            // Ignore errors
                        }
                    };
                }
                _depsSearchDebounceTimer.Start();
            }
        }

        private void CreatorsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && this.IsLoaded)
            {
                // Update creators clear button visibility immediately
                UpdateCreatorsClearButton();

                // Debounce the search
                _creatorsSearchDebounceTimer?.Stop();
                if (_creatorsSearchDebounceTimer == null)
                {
                    _creatorsSearchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _creatorsSearchDebounceTimer.Tick += (s, args) =>
                    {
                        _creatorsSearchDebounceTimer.Stop();
                        try
                        {
                            var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                            
                            // If showing placeholder text OR text is empty, show all items
                            if (textBox.Foreground.Equals(grayBrush) || string.IsNullOrWhiteSpace(textBox.Text))
                            {
                                FilterCreators(""); // Empty string to show all
                            }
                            else
                            {
                                FilterCreators(textBox.Text);
                            }
                        }
                        catch
                        {
                            // Ignore errors
                        }
                    };
                }
                _creatorsSearchDebounceTimer.Start();
            }
        }

        #endregion

        #region Focus Handlers

        private void PackageSearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                if (textBox.Foreground.Equals(grayBrush))
                {
                    // Temporarily unsubscribe to prevent triggering ApplyFilters when clearing placeholder
                    PackageSearchBox.TextChanged -= PackageSearchBox_TextChanged;
                    try
                    {
                        textBox.Text = "";
                        textBox.Foreground = (SolidColorBrush)FindResource("TextBrush");
                    }
                    finally
                    {
                        PackageSearchBox.TextChanged += PackageSearchBox_TextChanged;
                    }
                }
            }
        }

        private void PackageSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                if (string.IsNullOrWhiteSpace(textBox.Text))
                {
                    textBox.Foreground = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                    textBox.Text = "📝 Filter packages, descriptions, tags...";
                }
            }
        }

        private void DepsSearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                if (textBox.Foreground.Equals(grayBrush))
                {
                    // Temporarily unsubscribe to prevent triggering filter
                    DepsSearchBox.TextChanged -= DepsSearchBox_TextChanged;
                    try
                    {
                        textBox.Text = "";
                        textBox.Foreground = (SolidColorBrush)FindResource("TextBrush");
                    }
                    finally
                    {
                        DepsSearchBox.TextChanged += DepsSearchBox_TextChanged;
                    }
                }
            }
        }

        private void DepsSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                if (string.IsNullOrWhiteSpace(textBox.Text))
                {
                    textBox.Foreground = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                    textBox.Text = "📝 Filter dependencies...";
                }
            }
        }

        private void CreatorsSearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                if (textBox.Foreground.Equals(grayBrush))
                {
                    textBox.Text = "";
                    textBox.Foreground = (SolidColorBrush)FindResource("TextBrush");
                }
            }
        }

        private void CreatorsSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                if (string.IsNullOrWhiteSpace(textBox.Text))
                {
                    textBox.Text = "Search creators...";
                    textBox.Foreground = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                }
            }
        }

        private void DependenciesDataGrid_GotFocus(object sender, RoutedEventArgs e)
        {
            _dependenciesDataGridHasFocus = true;
            UpdateDependenciesButtonBar();
        }

        private void DependenciesDataGrid_LostFocus(object sender, RoutedEventArgs e)
        {
            _dependenciesDataGridHasFocus = false;
            UpdateDependenciesButtonBar();
        }

        #endregion

        #region Mouse Handlers

        private void PackageDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Check if we actually clicked on a row (not on empty space)
                var dataGrid = sender as DataGrid;
                var hitTest = VisualTreeHelper.HitTest(dataGrid, e.GetPosition(dataGrid));
                var row = FindParent<DataGridRow>(hitTest?.VisualHit as DependencyObject);
                
                if (row == null)
                {
                    // Clicked on empty space, not a row
                    return;
                }

                // Only handle if exactly 1 item is selected (ignore group selections)
                if (PackageDataGrid.SelectedItems.Count != 1)
                {
                    return;
                }

                var selectedPackage = PackageDataGrid.SelectedItems[0] as PackageItem;
                if (selectedPackage == null)
                {
                    return;
                }

                // Handle based on status
                if (selectedPackage.Status == "Loaded" || selectedPackage.Status == "Available" || selectedPackage.Status == "Archived")
                {
                    // Open folder path for loaded/available/archived packages
                    OpenPackageFolderPath(selectedPackage);
                }
                else if (selectedPackage.Status == "Missing")
                {
                    // Copy to clipboard for missing packages
                    CopyPackageToClipboard(selectedPackage);
                }
                
                // Mark event as handled to prevent further processing
                e.Handled = true;
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
            }
        }

        private void OpenPackageFolderPath(PackageItem package)
        {
            try
            {
                if (_packageFileManager == null)
                {
                    SetStatus("Package file manager not initialized");
                    return;
                }

                // Get the file path for this package
                // Use MetadataKey for accurate lookup (handles multiple versions of same package)
                // MetadataKey includes version and status information for precise matching
                PackageFileInfo fileInfo;
                if (!string.IsNullOrEmpty(package.MetadataKey))
                {
                    fileInfo = _packageFileManager.GetPackageFileInfoByMetadataKey(package.MetadataKey);
                }
                else
                {
                    fileInfo = _packageFileManager.GetPackageFileInfo(package.Name);
                }
                
                if (fileInfo != null && !string.IsNullOrEmpty(fileInfo.CurrentPath) && System.IO.File.Exists(fileInfo.CurrentPath))
                {
                    // Open folder and select the file - Explorer will reuse existing window if same folder
                    OpenFolderAndSelectFile(fileInfo.CurrentPath);
                    SetStatus($"Opened folder for: {package.Name}");
                }
                else
                {
                    SetStatus($"File not found: {package.Name}");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to open folder: {ex.Message}");
            }
        }

        private void CopyPackageToClipboard(PackageItem package)
        {
            try
            {
                System.Windows.Clipboard.SetText(package.Name);
                SetStatus($"Copied to clipboard: {package.Name}");
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to copy to clipboard: {ex.Message}");
            }
        }


        private void DragWatchTimer_Tick(object sender, EventArgs e)
        {
            if (!Mouse.LeftButton.HasFlag(MouseButtonState.Pressed) && !Mouse.RightButton.HasFlag(MouseButtonState.Pressed))
            {
                _dragWatchTimer?.Stop();
                _isDragging = false;
                _dragButton = null;
                _dragStartItem = null;
                _suppressSelectionEvents = false;
                
                // Trigger image loading now that drag has ended
                // This ensures images are only loaded after the user finishes selecting rows
                if (PackageDataGrid?.SelectedItems?.Count > 0)
                {
                    // Re-trigger the selection changed handler to load images
                    PackageDataGrid_SelectionChanged(PackageDataGrid, null);
                }
            }
        }

        private void PackageSortButton_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            try
            {
                // Context-aware: use appropriate grid based on current content mode
                DataGrid targetGrid = _currentContentMode switch
                {
                    "Scenes" => ScenesDataGrid,
                    "Presets" => CustomAtomDataGrid,
                    "Custom" => CustomAtomDataGrid,
                    _ => PackageDataGrid
                };
                
                if (targetGrid == null || targetGrid.Items.Count == 0)
                    return;

                // Get current selected index
                int currentIndex = targetGrid.SelectedIndex;
                
                // Determine new index based on scroll direction
                int newIndex;
                if (e.Delta > 0)
                {
                    // Scroll up - move selection up (previous item)
                    newIndex = Math.Max(0, currentIndex - 1);
                }
                else
                {
                    // Scroll down - move selection down (next item)
                    newIndex = Math.Min(targetGrid.Items.Count - 1, currentIndex + 1);
                }

                // Only update if index changed
                if (newIndex != currentIndex)
                {
                    targetGrid.SelectedIndex = newIndex;
                    targetGrid.ScrollIntoView(targetGrid.SelectedItem);
                }

                // Mark event as handled to prevent scrolling the DataGrid itself
                e.Handled = true;
            }
            catch { }
        }

        private void DependenciesSortButton_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            try
            {
                if (DependenciesDataGrid == null || DependenciesDataGrid.Items.Count == 0)
                    return;

                // Treat sort button scrolling as if the DataGrid has focus for keyboard shortcut display
                _dependenciesDataGridHasFocus = true;

                // Get current selected index
                int currentIndex = DependenciesDataGrid.SelectedIndex;
                
                // Determine new index based on scroll direction
                int newIndex;
                if (e.Delta > 0)
                {
                    // Scroll up - move selection up (previous item)
                    newIndex = Math.Max(0, currentIndex - 1);
                }
                else
                {
                    // Scroll down - move selection down (next item)
                    newIndex = Math.Min(DependenciesDataGrid.Items.Count - 1, currentIndex + 1);
                }

                // Only update if index changed
                if (newIndex != currentIndex)
                {
                    DependenciesDataGrid.SelectedIndex = newIndex;
                    DependenciesDataGrid.ScrollIntoView(DependenciesDataGrid.SelectedItem);
                    // Update button bar to show keyboard shortcuts
                    UpdateDependenciesButtonBar();
                }

                // Mark event as handled to prevent scrolling the DataGrid itself
                e.Handled = true;
            }
            catch { }
        }

        private void FilterArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Forward mouse wheel events from the button area to the filter ScrollViewer
            if (FilterScrollViewer != null)
            {
                // Calculate scroll amount (standard is 120 units per notch)
                double scrollAmount = -e.Delta / 3.0; // Adjust sensitivity as needed
                FilterScrollViewer.ScrollToVerticalOffset(FilterScrollViewer.VerticalOffset + scrollAmount);
                e.Handled = true; // Prevent event from bubbling up
            }
        }

        #endregion

        #region Menu Event Handlers

        private void SelectRootFolder_Click(object sender, RoutedEventArgs e)
        {
            // Use Windows Forms FolderBrowserDialog as fallback
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select VAM Root Folder";
                dialog.ShowNewFolderButton = false;
                
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    // Update settings (this will trigger auto-save)
                    _settingsManager.Settings.SelectedFolder = dialog.SelectedPath;
                    _selectedFolder = dialog.SelectedPath;
                    
                    // Initialize PackageFileManager with new folder
                    InitializePackageFileManager();
                    
                    UpdateUI();
                    SetStatus($"Selected folder: {System.IO.Path.GetFileName(_selectedFolder)}");
                    
                    RefreshPackages();
                }
            }
        }
        
        private void RefreshPackages_Click(object sender, RoutedEventArgs e)
        {
            // Hold Shift for full refresh, otherwise use incremental
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                SetStatus("Full refresh requested...");
                RefreshPackages();
            }
            else
            {
                RefreshPackagesIncremental();
            }
        }

        private async void ArchiveOldVersions_Click(object sender, RoutedEventArgs e)
        {
            await ArchiveOldVersionsFromMenu();
        }

        private async void ArchiveOldButton_Click(object sender, RoutedEventArgs e)
        {
            await ArchiveSelectedOldVersions();
        }

        private async void FixSelectedDuplicates_Click(object sender, RoutedEventArgs e)
        {
            await FixSelectedDuplicates();
        }

        private async Task ArchiveOldVersionsFromMenu()
        {
            try
            {
                var oldVersions = _packageManager.GetOldVersionPackages();
                
                if (oldVersions.Count == 0)
                {
                    DarkMessageBox.Show("All packages are at their latest versions.", "No old versions found", 
                                      MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Check if any old versions have dependents
                var packagesWithDependents = _packageManager.CheckPackagesForDependents(oldVersions);
                var warningMessage = _packageManager.GetDependentsWarningMessage(packagesWithDependents);
                
                var message = $"Found {oldVersions.Count} old version package(s).\n\n" +
                             $"These packages will be moved to:\n" +
                             $"{Path.Combine(_selectedFolder, "ArchivedPackages", "OldPackages")}\n\n";
                
                if (!string.IsNullOrEmpty(warningMessage))
                {
                    message += warningMessage + "\n";
                }
                
                message += "Do you want to continue?";
                
                var result = DarkMessageBox.Show(message, "Archive Old Versions", 
                                                MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    await ArchiveOldVersionsAsync(oldVersions);
                }
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show($"Failed to archive old versions: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ArchiveSelectedOldVersions()
        {
            try
            {
                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
                var oldVersionPackages = new List<VarMetadata>();
                
                foreach (var package in selectedPackages)
                {
                    if (package.IsOldVersion && _packageManager.PackageMetadata.TryGetValue(package.MetadataKey, out var metadata))
                    {
                        oldVersionPackages.Add(metadata);
                    }
                }
                
                if (oldVersionPackages.Count == 0)
                {
                    DarkMessageBox.Show("No old version packages selected.", "Archive Old Versions", 
                                      MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Create custom dialog with Archive All button
                var dialog = new ConfirmArchiveWindow(
                    oldVersionPackages.Count,
                    Path.Combine(_selectedFolder, "ArchivedPackages", "OldPackages"),
                    _packageManager.GetOldVersionPackages().Count
                );
                
                dialog.Owner = this;
                var dialogResult = dialog.ShowDialog();
                
                if (dialogResult == true)
                {
                    if (dialog.ArchiveAll)
                    {
                        // Show list of all old packages
                        var allOldPackages = _packageManager.GetOldVersionPackages();
                        var listDialog = new ArchiveAllOldWindow(
                            allOldPackages,
                            Path.Combine(_selectedFolder, "ArchivedPackages", "OldPackages")
                        );
                        listDialog.Owner = this;
                        
                        if (listDialog.ShowDialog() == true)
                        {
                            await ArchiveOldVersionsAsync(allOldPackages);
                        }
                    }
                    else
                    {
                        // Archive only selected
                        await ArchiveOldVersionsAsync(oldVersionPackages);
                    }
                }
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show($"Failed to archive old versions: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetMaxSafeSelection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && int.TryParse(menuItem.Tag?.ToString(), out int value))
            {
                _settingsManager.Settings.MaxSafeSelection = value;
                SetStatus($"Preview selection limit set to {value} packages");
                
                // Refresh current selection display if needed
                if (PackageDataGrid?.SelectedItems?.Count > 0)
                {
                    PackageDataGrid_SelectionChanged(PackageDataGrid, null);
                }
            }
        }


        private void ShowKeyboardShortcuts_Click(object sender, RoutedEventArgs e)
        {
            KeyboardShortcuts_Click(sender, e);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        #endregion

        #region Window Control Handlers

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                MaximizeRestoreButton.Content = "□"; // Maximize symbol
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                MaximizeRestoreButton.Content = "❒"; // Restore symbol
            }
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Allow dragging the window from the title bar
            if (e.ClickCount == 2)
            {
                // Double-click to maximize/restore
                MaximizeRestoreWindow_Click(null, null);
            }
            else
            {
                // Single click drag to move window
                this.DragMove();
            }
        }

        #endregion

        #region Theme and Settings Handlers

        private void SetTheme_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                var themeName = menuItem.Tag?.ToString();
                if (!string.IsNullOrEmpty(themeName))
                {
                    SwitchTheme(themeName);
                }
            }
        }

        private void SetHideArchivedPackages_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is string tagValue)
            {
                bool hideArchived = bool.Parse(tagValue);
                
                // If disabling hide archived, clear any "Archived" status filter FIRST
                if (!hideArchived && StatusFilterList != null)
                {
                    ClearArchivedStatusFilter();
                }
                
                // Update settings (this will auto-save via PropertyChanged event)
                _settingsManager.Settings.HideArchivedPackages = hideArchived;
                
                // Update filter manager
                _filterManager.HideArchivedPackages = hideArchived;
                
                // Update menu item visual state
                UpdateHideArchivedMenuItems(hideArchived);
                
                // If disabling hide archived, perform a manual refresh to reload packages
                if (!hideArchived)
                {
                    RefreshPackages();
                }
                else
                {
                    // Just reapply filters when enabling
                    ApplyFilters();
                }
            }
        }

        private void UpdateHideArchivedMenuItems(bool hideArchived)
        {
            if (HideArchivedEnabledMenuItem != null && HideArchivedDisabledMenuItem != null)
            {
                if (hideArchived)
                {
                    HideArchivedEnabledMenuItem.FontWeight = FontWeights.Bold;
                    HideArchivedDisabledMenuItem.FontWeight = FontWeights.Normal;
                }
                else
                {
                    HideArchivedEnabledMenuItem.FontWeight = FontWeights.Normal;
                    HideArchivedDisabledMenuItem.FontWeight = FontWeights.Bold;
                }
            }
        }

        private void ClearArchivedStatusFilter()
        {
            if (StatusFilterList == null) return;
            
            // Suppress selection events to prevent triggering ApplyFilters during clearing
            _suppressSelectionEvents = true;
            try
            {
                // Convert to list to avoid modification during iteration
                var selectedItems = StatusFilterList.SelectedItems.Cast<object>().ToList();
                
                // Find and remove any "Archived" status filter selections
                foreach (var item in selectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    
                    // Check if this is an "Archived" status filter
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        var status = itemText.Split('(')[0].Trim();
                        if (status.Equals("Archived", StringComparison.OrdinalIgnoreCase))
                        {
                            StatusFilterList.SelectedItems.Remove(item);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore errors
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        private void ConfigureFileSizeRanges_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "Configure File Size Filter Ranges",
                Width = 450,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = this.Background
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Tiny range
            var tinyPanel = new StackPanel { Orientation = Orientation.Horizontal };
            tinyPanel.Children.Add(new TextBlock { Text = "Tiny (0 - ", VerticalAlignment = VerticalAlignment.Center, Width = 80 });
            var tinyBox = new TextBox { Width = 80, Text = _filterManager.FileSizeTinyMax.ToString("F1") };
            tinyPanel.Children.Add(tinyBox);
            tinyPanel.Children.Add(new TextBlock { Text = " MB)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) });
            Grid.SetRow(tinyPanel, 0);
            grid.Children.Add(tinyPanel);

            // Small range
            var smallPanel = new StackPanel { Orientation = Orientation.Horizontal };
            smallPanel.Children.Add(new TextBlock { Text = "Small (", VerticalAlignment = VerticalAlignment.Center, Width = 80 });
            var smallMinLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            smallPanel.Children.Add(smallMinLabel);
            smallPanel.Children.Add(new TextBlock { Text = " - ", VerticalAlignment = VerticalAlignment.Center });
            var smallBox = new TextBox { Width = 80, Text = _filterManager.FileSizeSmallMax.ToString("F1") };
            smallPanel.Children.Add(smallBox);
            smallPanel.Children.Add(new TextBlock { Text = " MB)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) });
            Grid.SetRow(smallPanel, 2);
            grid.Children.Add(smallPanel);

            // Medium range
            var mediumPanel = new StackPanel { Orientation = Orientation.Horizontal };
            mediumPanel.Children.Add(new TextBlock { Text = "Medium (", VerticalAlignment = VerticalAlignment.Center, Width = 80 });
            var mediumMinLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            mediumPanel.Children.Add(mediumMinLabel);
            mediumPanel.Children.Add(new TextBlock { Text = " - ", VerticalAlignment = VerticalAlignment.Center });
            var mediumBox = new TextBox { Width = 80, Text = _filterManager.FileSizeMediumMax.ToString("F1") };
            mediumPanel.Children.Add(mediumBox);
            mediumPanel.Children.Add(new TextBlock { Text = " MB)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) });
            Grid.SetRow(mediumPanel, 4);
            grid.Children.Add(mediumPanel);

            // Large range
            var largePanel = new StackPanel { Orientation = Orientation.Horizontal };
            largePanel.Children.Add(new TextBlock { Text = "Large (", VerticalAlignment = VerticalAlignment.Center, Width = 80 });
            var largeMinLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            largePanel.Children.Add(largeMinLabel);
            largePanel.Children.Add(new TextBlock { Text = " MB+)", VerticalAlignment = VerticalAlignment.Center });
            Grid.SetRow(largePanel, 6);
            grid.Children.Add(largePanel);

            // Update labels when values change
            Action updateLabels = () =>
            {
                if (double.TryParse(tinyBox.Text, out double tiny))
                {
                    smallMinLabel.Text = tiny.ToString("F1");
                }
                if (double.TryParse(smallBox.Text, out double small))
                {
                    mediumMinLabel.Text = small.ToString("F1");
                }
                if (double.TryParse(mediumBox.Text, out double medium))
                {
                    largeMinLabel.Text = medium.ToString("F1");
                }
            };

            tinyBox.TextChanged += (s, args) => updateLabels();
            smallBox.TextChanged += (s, args) => updateLabels();
            mediumBox.TextChanged += (s, args) => updateLabels();
            updateLabels();

            // Buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okButton = new Button { Content = "OK", Width = 80, Height = 30, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
            var cancelButton = new Button { Content = "Cancel", Width = 80, Height = 30, IsCancel = true };
            
            okButton.Click += (s, args) =>
            {
                if (double.TryParse(tinyBox.Text, out double tiny) &&
                    double.TryParse(smallBox.Text, out double small) &&
                    double.TryParse(mediumBox.Text, out double medium) &&
                    tiny > 0 && small > tiny && medium > small)
                {
                    _settingsManager.Settings.FileSizeTinyMax = tiny;
                    _settingsManager.Settings.FileSizeSmallMax = small;
                    _settingsManager.Settings.FileSizeMediumMax = medium;
                    
                    // Update FilterManager
                    _filterManager.FileSizeTinyMax = tiny;
                    _filterManager.FileSizeSmallMax = small;
                    _filterManager.FileSizeMediumMax = medium;
                    
                    // Refresh filters
                    RefreshFilterLists();
                    ApplyFilters();
                    
                    dialog.DialogResult = true;
                    dialog.Close();
                    SetStatus($"File size ranges updated: Tiny<{tiny}MB, Small<{small}MB, Medium<{medium}MB");
                }
                else
                {
                    CustomMessageBox.Show("Please enter valid numbers where each range is larger than the previous.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            
            cancelButton.Click += (s, args) => dialog.Close();
            
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            Grid.SetRow(buttonPanel, 8);
            grid.Children.Add(buttonPanel);

            dialog.Content = grid;
            dialog.ShowDialog();
        }

        private void KeyboardShortcuts_Click(object sender, RoutedEventArgs e)
        {
            CustomMessageBox.Show("Keyboard shortcuts:\n\nF5 - Refresh packages\nCtrl+F - Focus search\nCtrl+B - Build cache\nCtrl+, - Settings\nCtrl+/- - Image columns", "Keyboard Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void UpdatePackageDatabase_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if package downloader is initialized
                if (_packageDownloader == null)
                {
                    CustomMessageBox.Show("Package downloader is not initialized. Please select a VAM folder first.",
                        "Update Database", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Show progress message
                SetStatus("Updating package database...");
                
                // Get count before loading
                int countBefore = _packageDownloader.GetPackageCount();
                
                // Load package list (this will trigger network permission check if needed)
                bool success = await LoadPackageDownloadListAsync();
                
                if (!success)
                {
                    // Database load failed
                    SetStatus("Database update failed");
                    CustomMessageBox.Show("Failed to load package database.\n\nPlease check:\n• Network connection\n• Firewall settings",
                        "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Get count after loading
                int countAfter = _packageDownloader.GetPackageCount();
                bool fromGitHub = _packageDownloader.WasLastLoadFromGitHub();
                
                // Only show success if packages were actually loaded
                if (countAfter > 0)
                {
                    string source = fromGitHub ? "GitHub" : "local cache";
                    
                    SetStatus($"Database updated: {countAfter:N0} packages from {source}");
                    
                    // Database status is now shown in PackageSearchWindow, no need to update button
                }
                else
                {
                    SetStatus("Database update failed - no packages loaded");
                    CustomMessageBox.Show("No packages were loaded. The database may be empty or corrupted.",
                        "Update Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Failed to update package database:\n\n{ex.Message}",
                    "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Database update failed");
            }
        }

        private async void ValidateTextures_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if packages are selected
                if (PackageDataGrid.SelectedItems.Count == 0)
                {
                    CustomMessageBox.Show("Please select at least one package to optimise.", 
                                  "Optimise Package", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Check if selection exceeds maximum
                if (PackageDataGrid.SelectedItems.Count > _settingsManager.Settings.MaxSafeSelection)
                {
                    CustomMessageBox.Show($"Please select a maximum of {_settingsManager.Settings.MaxSafeSelection} packages for bulk optimization.", 
                                  "Optimise Package", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();

                // Use unified bulk optimization dialog for both single and multiple packages
                await DisplayBulkOptimizationDialog(selectedPackages);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error during package analysis: {ex.Message}", 
                              "Package Optimization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus($"Package analysis failed: {ex.Message}");
            }
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow
            {
                Owner = this
            };

            aboutWindow.ShowDialog();
        }

        private void FilterList_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBox listBox && e.OriginalSource is FrameworkElement element)
            {
                // Find the clicked item
                var clickedItem = FindParent<ListBoxItem>(element);
                if (clickedItem != null)
                {
                    // Handle toggle behavior for filter lists
                    HandleFilterToggle(listBox, clickedItem);
                    e.Handled = true; // Prevent default selection behavior
                }
            }
        }

        private void HandleFilterToggle(ListBox listBox, ListBoxItem clickedItem)
        {
            try
            {
                // Suppress selection events during manual toggle
                _suppressSelectionEvents = true;

                // Get the content string to work with
                var contentString = clickedItem.Content?.ToString();
                if (string.IsNullOrEmpty(contentString))
                {
                    return;
                }

                // Check if this content string is currently selected
                // Since items are stored as strings in the ListBox, we need to check against strings
                bool isCurrentlySelected = false;
                foreach (var selectedItem in listBox.SelectedItems)
                {
                    if (selectedItem is string str && str == contentString)
                    {
                        isCurrentlySelected = true;
                        break;
                    }
                }

                if (isCurrentlySelected)
                {
                    // Deselect the item (toggle off)
                    // Find and remove the matching string from SelectedItems
                    object itemToRemove = null;
                    foreach (var selectedItem in listBox.SelectedItems)
                    {
                        if (selectedItem is string str && str == contentString)
                        {
                            itemToRemove = selectedItem;
                            break;
                        }
                    }
                    
                    if (itemToRemove != null)
                    {
                        listBox.SelectedItems.Remove(itemToRemove);
                    }
                }
                else
                {
                    // Select the item (toggle on)
                    // Find the matching string in Items and add it to SelectedItems
                    object itemToAdd = null;
                    foreach (var item in listBox.Items)
                    {
                        if (item is string str && str == contentString)
                        {
                            itemToAdd = item;
                            break;
                        }
                    }
                    
                    if (itemToAdd != null)
                    {
                        if (listBox.SelectionMode == SelectionMode.Multiple || listBox.SelectionMode == SelectionMode.Extended)
                        {
                            listBox.SelectedItems.Add(itemToAdd);
                        }
                        else
                        {
                            listBox.SelectedItem = itemToAdd;
                        }
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }

            // Apply filters after toggle
            ApplyFilters();
            
            // Update clear button visibility
            UpdateClearButtonVisibility();
        }

        private void FilterTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                if (textBox.Foreground.Equals(grayBrush))
                {
                    // Temporarily unsubscribe from TextChanged to prevent triggering filters
                    if (textBox.Name == "PackageSearchBox")
                    {
                        textBox.TextChanged -= PackageSearchBox_TextChanged;
                    }
                    else if (textBox.Name == "DepsSearchBox")
                    {
                        textBox.TextChanged -= DepsSearchBox_TextChanged;
                    }
                    else if (textBox.Name == "ContentTypesFilterBox")
                    {
                        textBox.TextChanged -= ContentTypesFilterBox_TextChanged;
                    }
                    else if (textBox.Name == "CreatorsFilterBox")
                    {
                        textBox.TextChanged -= CreatorsFilterBox_TextChanged;
                    }
                    else if (textBox.Name == "LicenseTypeFilterBox")
                    {
                        textBox.TextChanged -= LicenseTypeFilterBox_TextChanged;
                    }
                    else if (textBox.Name == "SubfoldersFilterBox")
                    {
                        textBox.TextChanged -= SubfoldersFilterBox_TextChanged;
                    }
                    else if (textBox.Name == "SceneSearchBox")
                    {
                        textBox.TextChanged -= SceneSearchBox_TextChanged;
                    }
                    
                    try
                    {
                        textBox.Text = "";
                        textBox.Foreground = (SolidColorBrush)FindResource("TextBrush");
                    }
                    finally
                    {
                        // Re-subscribe
                        if (textBox.Name == "PackageSearchBox")
                        {
                            textBox.TextChanged += PackageSearchBox_TextChanged;
                        }
                        else if (textBox.Name == "DepsSearchBox")
                        {
                            textBox.TextChanged += DepsSearchBox_TextChanged;
                        }
                        else if (textBox.Name == "ContentTypesFilterBox")
                        {
                            textBox.TextChanged += ContentTypesFilterBox_TextChanged;
                        }
                        else if (textBox.Name == "CreatorsFilterBox")
                        {
                            textBox.TextChanged += CreatorsFilterBox_TextChanged;
                        }
                        else if (textBox.Name == "LicenseTypeFilterBox")
                        {
                            textBox.TextChanged += LicenseTypeFilterBox_TextChanged;
                        }
                        else if (textBox.Name == "SubfoldersFilterBox")
                        {
                            textBox.TextChanged += SubfoldersFilterBox_TextChanged;
                        }
                        else if (textBox.Name == "SceneSearchBox")
                        {
                            textBox.TextChanged += SceneSearchBox_TextChanged;
                        }
                    }
                }
            }
        }

        private void FilterTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                if (string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Restore the correct placeholder based on textbox name
                    string placeholderText = textBox.Name switch
                    {
                        "PackageSearchBox" => "📝 Filter packages, descriptions, tags...",
                        "DepsSearchBox" => _showingDependents ? "📝 Filter dependents..." : "📝 Filter dependencies...",
                        "ContentTypesFilterBox" => "📝 Filter content types...",
                        "CreatorsFilterBox" => "😣 Filter creators...",
                        "LicenseTypeFilterBox" => "📄 Filter license types...",
                        "SubfoldersFilterBox" => "✗ Filter subfolders...",
                        "SceneSearchBox" => "📝 Filter scenes by name, creator, type...",
                        _ => "Search..."
                    };
                    
                    // CRITICAL: Set Foreground to gray BEFORE setting Text to prevent TextChanged from triggering filter
                    textBox.Foreground = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                    textBox.Text = placeholderText;
                    
                    // Restore full filter lists when filter is cleared
                    if (textBox.Name == "ContentTypesFilterBox")
                    {
                        FilterContentTypesList("");
                        UpdateContentTypesClearButton();
                    }
                    else if (textBox.Name == "CreatorsFilterBox")
                    {
                        FilterCreatorsList("");
                        UpdateCreatorsClearButton();
                    }
                    else if (textBox.Name == "LicenseTypeFilterBox")
                    {
                        FilterLicenseTypesList("");
                        UpdateLicenseTypeClearButton();
                    }
                    else if (textBox.Name == "SubfoldersFilterBox")
                    {
                        FilterSubfoldersList("");
                        UpdateSubfoldersClearButton();
                    }
                    else if (textBox.Name == "SceneSearchBox")
                    {
                        UpdateSceneSearchClearButton();
                    }
                }
            }
        }

        private void ContentTypesFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && this.IsLoaded)
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                bool isPlaceholder = textBox.Foreground.Equals(grayBrush);
                
                if (!isPlaceholder && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Filter the content types list
                    FilterContentTypesList(textBox.Text);
                }
                else if (isPlaceholder || string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Show all content types when no filter
                    FilterContentTypesList("");
                }
                // Update clear button visibility
                UpdateContentTypesClearButton();
            }
        }

        private void CreatorsFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && this.IsLoaded)
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                bool isPlaceholder = textBox.Foreground.Equals(grayBrush);
                
                if (!isPlaceholder && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Apply creators filter
                    FilterCreators(textBox.Text);
                }
                else if (isPlaceholder || string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Show all creators when no filter
                    FilterCreatorsList("");
                }
                // Update clear button visibility
                UpdateCreatorsClearButton();
            }
        }

        private void LicenseTypeFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && this.IsLoaded)
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                bool isPlaceholder = textBox.Foreground.Equals(grayBrush);
                
                if (!isPlaceholder && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Filter the license types list
                    FilterLicenseTypesList(textBox.Text);
                }
                else if (isPlaceholder || string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Show all license types when no filter
                    FilterLicenseTypesList("");
                }
                // Update clear button visibility
                UpdateLicenseTypeClearButton();
            }
        }

        private void SubfoldersFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && this.IsLoaded)
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                bool isPlaceholder = textBox.Foreground.Equals(grayBrush);
                
                if (!isPlaceholder && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Filter the subfolders list
                    FilterSubfoldersList(textBox.Text);
                }
                else if (isPlaceholder || string.IsNullOrWhiteSpace(textBox.Text))
                {
                    // Show all subfolders when no filter
                    FilterSubfoldersList("");
                }
                // Update clear button visibility
                UpdateSubfoldersClearButton();
            }
        }

        private async void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                TextBox targetTextBox = null;
                
                // Find the associated TextBox based on button name
                if (button.Name == "ContentTypesClearButton")
                {
                    targetTextBox = ContentTypesFilterBox;
                    ContentTypesList.SelectedItems.Clear();
                    // Ensure clear button visibility is updated immediately
                    UpdateContentTypesClearButton();
                }
                else if (button.Name == "CreatorsClearButton")
                {
                    targetTextBox = CreatorsFilterBox;
                    CreatorsList.SelectedItems.Clear();
                    // Ensure clear button visibility is updated immediately
                    UpdateCreatorsClearButton();
                }
                else if (button.Name == "LicenseTypeClearButton")
                {
                    targetTextBox = LicenseTypeFilterBox;
                    LicenseTypeList.SelectedItems.Clear();
                    // Ensure clear button visibility is updated immediately
                    UpdateLicenseTypeClearButton();
                }
                else if (button.Name == "SubfoldersClearButton")
                {
                    targetTextBox = SubfoldersFilterBox;
                    SubfoldersFilterList.SelectedItems.Clear();
                    // Ensure clear button visibility is updated immediately
                    UpdateSubfoldersClearButton();
                }
                else if (button.Name == "PackageSearchClearButton")
                {
                    targetTextBox = PackageSearchBox;
                    // Context-aware: clear text OR clear main table selection
                    var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                    bool hasText = !PackageSearchBox.Foreground.Equals(grayBrush) && !string.IsNullOrWhiteSpace(PackageSearchBox.Text);
                    
                    if (hasText)
                    {
                        // Temporarily unsubscribe from TextChanged to prevent full refresh
                        PackageSearchBox.TextChanged -= PackageSearchBox_TextChanged;
                        try
                        {
                            // Clear text
                            targetTextBox.Text = "";
                            FilterTextBox_LostFocus(targetTextBox, new RoutedEventArgs());
                            
                            // Just refresh the CollectionView filter, don't reload entire table
                            var view = CollectionViewSource.GetDefaultView(PackageDataGrid.ItemsSource);
                            view?.Refresh();
                            
                            UpdatePackageSearchClearButton();
                        }
                        finally
                        {
                            // Re-subscribe to TextChanged
                            PackageSearchBox.TextChanged += PackageSearchBox_TextChanged;
                        }
                        return; // Exit early - no need to call ApplyFilters()
                    }
                    else if (PackageDataGrid.SelectedItems.Count > 0)
                    {
                        // Temporarily disable selection changed events to prevent dependency refresh
                        PackageDataGrid.SelectionChanged -= PackageDataGrid_SelectionChanged;
                        try
                        {
                            PackageDataGrid.SelectedItems.Clear();
                            
                            // Explicitly refresh displays after clearing selection
                            await Dispatcher.InvokeAsync(async () =>
                            {
                                await RefreshSelectionDisplaysImmediate();
                            });
                            
                            // Update visibility after selection change is processed
                            var _ = Dispatcher.BeginInvoke(new Action(() => 
                            {
                                UpdatePackageSearchClearButton();
                                // UpdatePackageButtonBar will handle showing placeholder
                                UpdatePackageButtonBar();
                            }));
                        }
                        finally
                        {
                            // Re-enable selection changed events
                            PackageDataGrid.SelectionChanged += PackageDataGrid_SelectionChanged;
                        }
                        return; // Exit early to avoid calling ApplyFilters() below
                    }
                    return; // Exit early if nothing to do
                }
                else if (button.Name == "DepsSearchClearButton")
                {
                    targetTextBox = DepsSearchBox;
                    // Context-aware: clear text OR clear dependencies table selection
                    var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                    bool hasText = !DepsSearchBox.Foreground.Equals(grayBrush) && !string.IsNullOrWhiteSpace(DepsSearchBox.Text);
                    
                    if (hasText)
                    {
                        // Clear text if there's text
                        targetTextBox.Text = "";
                        FilterTextBox_LostFocus(targetTextBox, new RoutedEventArgs());
                        // Apply dependencies filter after clearing text
                        FilterDependencies("");
                    }
                    else if (DependenciesDataGrid.SelectedItems.Count > 0)
                    {
                        // Clear selection if no text but items are selected
                        DependenciesDataGrid.SelectedItems.Clear();
                        // Update visibility after selection change is processed
                        var _ = Dispatcher.BeginInvoke(new Action(() => UpdateDepsSearchClearButton()));
                        return; // Exit early
                    }
                }
                
                if (targetTextBox != null && button.Name != "PackageSearchClearButton" && button.Name != "DepsSearchClearButton")
                {
                    // Clear the text and restore placeholder (except for package search which is handled above)
                    targetTextBox.Text = "";
                    FilterTextBox_LostFocus(targetTextBox, new RoutedEventArgs());
                    
                    // Update clear button visibility and apply filters after clearing
                    UpdateClearButtonVisibility();
                    ApplyFilters();
                }
            }
        }

        #endregion
        #region Clear Button Handlers

        private void ClearPackageSearch_Click(object sender, RoutedEventArgs e)
        {
            ClearSearchBox(PackageSearchBox, "Search packages...", FilterPackages);
        }

        private void ClearDepsSearch_Click(object sender, RoutedEventArgs e)
        {
            ClearSearchBox(DepsSearchBox, "Search dependencies...", FilterDependencies);
        }

        private void DependenciesTab_Click(object sender, RoutedEventArgs e)
        {
            SwitchToDependenciesTab();
        }

        private void DependentsTab_Click(object sender, RoutedEventArgs e)
        {
            SwitchToDependentsTab();
        }

        private void DependenciesTabs_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Only allow tab switching in packages mode - not in scenes or presets mode
            if (_currentContentMode != "Packages")
            {
                e.Handled = false;
                return;
            }

            if (e.Delta > 0)
            {
                SwitchToDependenciesTab();
            }
            else if (e.Delta < 0)
            {
                SwitchToDependentsTab();
            }
            e.Handled = true;
        }

        private void PackageInfoTabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Only handle scroll wheel if directly over the WrapPanel (tab headers area)
            if (sender is WrapPanel && PackageInfoTabControl?.Items.Count > 1)
            {
                // Check if the original source is actually a tab header element, not content
                var originalSource = e.OriginalSource as DependencyObject;
                bool isOverTabHeader = false;
                
                // Walk up the visual tree to see if we hit a TabItem before hitting content
                while (originalSource != null)
                {
                    // If we find content controls first, we're not over a tab header
                    if (originalSource is DataGrid || originalSource is ListBox || originalSource is ScrollViewer || 
                        originalSource is TextBox || originalSource is Button || originalSource is Border)
                    {
                        // Check if this is the content presenter (tab content area)
                        if (originalSource is ContentPresenter cp && cp.Name == "PART_SelectedContentHost")
                        {
                            return; // Definitely over content area
                        }
                        // Check if this is a border that's part of content
                        if (originalSource is Border border && border.Parent is ContentPresenter)
                        {
                            return; // Over content area
                        }
                    }
                    
                    // If we find a TabItem, we're over a tab header
                    if (originalSource is TabItem)
                    {
                        isOverTabHeader = true;
                        break;
                    }
                    
                    // If we reach the WrapPanel, we're in the header area
                    if (originalSource == sender)
                    {
                        isOverTabHeader = true;
                        break;
                    }
                    
                    originalSource = VisualTreeHelper.GetParent(originalSource);
                }

                // Only proceed if we're actually over a tab header
                if (isOverTabHeader)
                {
                    int currentIndex = PackageInfoTabControl.SelectedIndex;
                    int newIndex = currentIndex;

                    if (e.Delta > 0)
                    {
                        newIndex = (currentIndex - 1 + PackageInfoTabControl.Items.Count) % PackageInfoTabControl.Items.Count;
                    }
                    else if (e.Delta < 0)
                    {
                        newIndex = (currentIndex + 1) % PackageInfoTabControl.Items.Count;
                    }

                    if (newIndex != currentIndex)
                    {
                        PackageInfoTabControl.SelectedIndex = newIndex;
                        e.Handled = true;
                    }
                }
            }
        }

        private void SwitchToDependenciesTab()
        {
            if (_showingDependents)
            {
                _showingDependents = false;
                UpdateTabVisuals();
                RefreshDependenciesDisplay();
            }
        }

        private void SwitchToDependentsTab()
        {
            if (!_showingDependents)
            {
                _showingDependents = true;
                UpdateTabVisuals();
                RefreshDependenciesDisplay();
            }
        }

        private void UpdateTabVisuals()
        {
            var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
            
            DepsSearchBox.TextChanged -= DepsSearchBox_TextChanged;
            try
            {
                if (_showingDependents)
                {
                    DepsSearchBox.Text = "📝 Filter dependents...";
                    DependenciesTab.Tag = null;
                    DependentsTab.Tag = "Active";
                }
                else
                {
                    DepsSearchBox.Text = "📝 Filter dependencies...";
                    DependenciesTab.Tag = "Active";
                    DependentsTab.Tag = null;
                }
                
                DepsSearchBox.Foreground = grayBrush;
            }
            finally
            {
                DepsSearchBox.TextChanged += DepsSearchBox_TextChanged;
            }
        }


        private void ClearCreatorsSearch_Click(object sender, RoutedEventArgs e)
        {
            // CreatorsSearchBox doesn't exist yet - just filter
            FilterCreators("");
            UpdateClearButtonVisibility();
        }

        /// <summary>
        /// Helper to clear search box and reset filter
        /// </summary>
        private void ClearSearchBox(TextBox searchBox, string placeholder, Action<string> filterAction)
        {
            searchBox.Text = placeholder;
            searchBox.Foreground = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
            filterAction("");
            UpdateClearButtonVisibility();
        }

        private void ClearAllFilters_Click(object sender, RoutedEventArgs e)
        {
            // Clear all search boxes
            ClearPackageSearch_Click(sender, e);
            ClearDepsSearch_Click(sender, e);
            ClearCreatorsSearch_Click(sender, e);
            
            // Clear all filter selections
            StatusFilterList.SelectedItems.Clear();
            CreatorsList.SelectedItems.Clear();
            ContentTypesList.SelectedItems.Clear();
            
            // Apply empty filters to show all items
            ApplyFilters();
            UpdateClearButtonVisibility();
        }

        #endregion

        #region Window Event Handlers

        private void ToggleLinkedFilters_Click(object sender, RoutedEventArgs e)
        {
            var settings = _settingsManager?.Settings;
            if (settings != null)
            {
                // Toggle the setting
                settings.CascadeFiltering = !settings.CascadeFiltering;
                _cascadeFiltering = settings.CascadeFiltering;
                
                // Update button appearance
                UpdateLinkedFiltersButtonState();
                
                // Refresh filter lists to apply change
                RefreshFilterLists();
                
                SetStatus(settings.CascadeFiltering ? "Linked filters enabled" : "Linked filters disabled");
            }
        }

        private void UpdateLinkedFiltersButtonState()
        {
            var settings = _settingsManager?.Settings;
            if (settings != null && LinkedFiltersButton != null)
            {
                // Update button appearance based on state
                // CascadeFiltering = true means filters are linked (multiple selections, all visible)
                // CascadeFiltering = false means filters are isolated (single selection, hide incompatible)
                if (settings.CascadeFiltering)
                {
                    // On state - linked/connected
                    if (LinkedStatusText != null)
                        LinkedStatusText.Text = "On";
                    LinkedFiltersButton.FontWeight = FontWeights.Bold;
                    LinkedFiltersButton.BorderThickness = new Thickness(2);
                    LinkedFiltersButton.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(0x40, 0x00, 0xFF, 0x00)); // Subtle green tint
                }
                else
                {
                    // Off state - isolated/disconnected
                    if (LinkedStatusText != null)
                        LinkedStatusText.Text = "Off";
                    LinkedFiltersButton.FontWeight = FontWeights.Normal;
                    LinkedFiltersButton.BorderThickness = new Thickness(1);
                    LinkedFiltersButton.Background = (System.Windows.Media.Brush)FindResource(SystemColors.ControlBrushKey);
                }
            }
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            var settings = _settingsManager.Settings;
            
            // Disable Hub buttons while loading packages
            _isLoadingPackages = true;
            DisableHubButtons();
            
            // Load caches asynchronously, then refresh packages after cache is ready
            // This prevents rebuilding the cache on every startup
            _ = LoadCachesAndRefreshAsync(settings);
            
            // Bind ScenesDataGrid ItemsSource to ScenesView for filtering support
            ScenesDataGrid.ItemsSource = ScenesView;
            
            // Bind CustomAtomDataGrid ItemsSource to CustomAtomItemsView for filtering support
            if (CustomAtomDataGrid != null)
                CustomAtomDataGrid.ItemsSource = CustomAtomItemsView;
            
            // Initialize ImageListView control with service configuration
            InitializeImageListView();
            
            // Initialize button states
            UpdateLinkedFiltersButtonState();
        }
        
        private async Task LoadCachesAndRefreshAsync(AppSettings settings)
        {
            try
            {
                // Load binary cache first (critical for performance)
                await _packageManager.LoadBinaryCacheAsync();
                
                // Load image caches in parallel
                await _imageManager.LoadImageCacheAsync();
                await Task.Run(() => _hubService?.LoadImageCache());
                
                // Now refresh packages with cache ready
                if (!string.IsNullOrEmpty(settings.SelectedFolder) && 
                    System.IO.Directory.Exists(settings.SelectedFolder))
                {
                    RefreshPackages();
                }
                
                // Apply window settings after packages are loaded
                ApplyWindowSettings(settings);
            }
            catch (Exception)
            {
            }
        }
        
        private void ApplyWindowSettings(AppSettings settings)
        {
            // Apply window settings
            if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
            {
                this.Width = settings.WindowWidth;
                this.Height = settings.WindowHeight;
            }
            
            if (settings.WindowLeft >= 0 && settings.WindowTop >= 0)
            {
                this.Left = settings.WindowLeft;
                this.Top = settings.WindowTop;
            }
            
            if (settings.WindowMaximized)
            {
                this.WindowState = WindowState.Maximized;
            }
            
            // Restore splitter positions
            if (settings.LeftPanelWidth > 0)
                LeftPanelColumn.Width = new GridLength(settings.LeftPanelWidth, GridUnitType.Star);
            if (settings.CenterPanelWidth > 0)
                CenterPanelColumn.Width = new GridLength(settings.CenterPanelWidth, GridUnitType.Star);
            if (settings.RightPanelWidth > 0)
                RightPanelColumn.Width = new GridLength(settings.RightPanelWidth, GridUnitType.Star);
            if (settings.ImagesPanelWidth > 0)
                ImagesPanelColumn.Width = new GridLength(settings.ImagesPanelWidth, GridUnitType.Star);
            
            // Restore deps/info splitter height
            if (settings.DepsInfoSplitterHeight > 0 && settings.DepsInfoSplitterHeight < 1)
            {
                DepsListRow.Height = new GridLength(settings.DepsInfoSplitterHeight, GridUnitType.Star);
                InfoRow.Height = new GridLength(Math.Max(0.1, 1 - settings.DepsInfoSplitterHeight), GridUnitType.Star);
            }
            else
            {
                DepsListRow.Height = new GridLength(0.5, GridUnitType.Star);
                InfoRow.Height = new GridLength(0.5, GridUnitType.Star);
            }
            
            // Apply other UI settings
            ApplySettingsToUI();
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Save current window state
            var settings = _settingsManager.Settings;
            
            if (this.WindowState == WindowState.Normal)
            {
                settings.WindowWidth = this.Width;
                settings.WindowHeight = this.Height;
                settings.WindowLeft = this.Left;
                settings.WindowTop = this.Top;
            }
            
            settings.WindowMaximized = this.WindowState == WindowState.Maximized;
            
            // Save splitter positions
            SaveSplitterPositions();
            
            // Force immediate save
            _settingsManager.SaveSettingsImmediate();
            
            // Dispose of managers
            _incrementalRefresh?.Dispose();
            _settingsManager?.Dispose();
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (this.IsLoaded && this.WindowState == WindowState.Normal)
            {
                _settingsManager.Settings.WindowWidth = this.Width;
                _settingsManager.Settings.WindowHeight = this.Height;
            }
        }

        private void OnWindowLocationChanged(object sender, EventArgs e)
        {
            if (this.IsLoaded && this.WindowState == WindowState.Normal)
            {
                _settingsManager.Settings.WindowLeft = this.Left;
                _settingsManager.Settings.WindowTop = this.Top;
            }
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            // Update maximize/restore button icon based on window state
            if (MaximizeRestoreButton != null)
            {
                if (this.WindowState == WindowState.Maximized)
                {
                    MaximizeRestoreButton.Content = "❒"; // Restore symbol
                }
                else
                {
                    MaximizeRestoreButton.Content = "□"; // Maximize symbol
                }
            }
        }

        private void SaveSplitterPositions()
        {
            var settings = _settingsManager.Settings;
            
            // Save column widths
            if (LeftPanelColumn.Width.IsStar)
                settings.LeftPanelWidth = LeftPanelColumn.Width.Value;
            if (CenterPanelColumn.Width.IsStar)
                settings.CenterPanelWidth = CenterPanelColumn.Width.Value;
            if (RightPanelColumn.Width.IsStar)
                settings.RightPanelWidth = RightPanelColumn.Width.Value;
            if (ImagesPanelColumn.Width.IsStar)
                settings.ImagesPanelWidth = ImagesPanelColumn.Width.Value;
            
            // Save deps/info splitter height
            if (DepsListRow.Height.IsStar)
                settings.DepsInfoSplitterHeight = DepsListRow.Height.Value;
        }

        #endregion
        #region Keyboard Navigation Handlers

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Handle Space key for dependencies when using sort button scroll
            if (e.Key == Key.Space && _dependenciesDataGridHasFocus && DependenciesDataGrid?.SelectedItems.Count > 0)
            {
                // Prevent key repeat - only trigger on first press
                if (e.IsRepeat)
                {
                    e.Handled = true;
                    return;
                }

                // Check if Ctrl is pressed for multiple selection, or single item without Ctrl
                bool isCtrlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                bool isSingleSelection = DependenciesDataGrid.SelectedItems.Count == 1;

                // Only allow: single item with Space, or multiple items with Ctrl+Space
                if (isSingleSelection || isCtrlPressed)
                {
                    var selectedDependencies = DependenciesDataGrid.SelectedItems.Cast<DependencyItem>().ToList();

                    // Check if all selected items have the same status
                    var statuses = selectedDependencies.Select(d => d.Status).Distinct().ToList();

                    if (statuses.Count == 1)
                    {
                        // All items have same status - proceed with operation
                        var status = statuses[0];

                        if (status == "Available")
                        {
                            // Trigger load
                            LoadDependencies_Click(sender, e);
                            e.Handled = true;
                        }
                        else if (status == "Loaded")
                        {
                            // Trigger unload
                            UnloadDependencies_Click(sender, e);
                            e.Handled = true;
                        }
                    }
                }
            }
        }

        private async void PackageDataGrid_KeyDown(object sender, KeyEventArgs e)
        {
            // Handle Shift+Space to load with dependencies
            if (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && PackageDataGrid.SelectedItems.Count > 0)
            {
                if (e.IsRepeat)
                {
                    e.Handled = true;
                    return;
                }

                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>()
                    .Where(p => p.Status == "Available")
                    .ToList();

                if (selectedPackages.Count > 0)
                {
                    // Trigger load with deps button click
                    LoadPackagesWithDepsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                    // Restore focus to DataGrid cell after operation
                    _ = Dispatcher.BeginInvoke(new Action(() => 
                    {
                        PackageDataGrid.Focus();
                        if (PackageDataGrid.SelectedItem != null)
                        {
                            PackageDataGrid.CurrentCell = new DataGridCellInfo(PackageDataGrid.SelectedItem, PackageDataGrid.Columns[0]);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                return;
            }

            // Handle spacebar (Space) or Ctrl+Space to toggle load/unload
            if (e.Key == Key.Space && PackageDataGrid.SelectedItems.Count > 0)
            {
                // Prevent key repeat - only trigger on first press
                if (e.IsRepeat)
                {
                    e.Handled = true;
                    return;
                }
                
                // Check if Ctrl is pressed for multiple selection, or single item without Ctrl
                bool isCtrlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                bool isSingleSelection = PackageDataGrid.SelectedItems.Count == 1;
                
                // Only allow: single item with Space, or multiple items with Ctrl+Space
                if (isSingleSelection || isCtrlPressed)
                {
                    var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
                    
                    // Check if all selected items have the same status
                    var statuses = selectedPackages.Select(p => p.Status).Distinct().ToList();
                    
                    if (statuses.Count == 1)
                    {
                        // All items have same status - proceed with operation
                        var status = statuses[0];
                        
                        if (status == "Available")
                        {
                            // Trigger load button click
                            LoadPackagesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            e.Handled = true;
                            // Restore focus to selected row in DataGrid after operation
                            _ = Dispatcher.BeginInvoke(new Action(() => 
                            {
                                PackageDataGrid.Focus();
                                if (PackageDataGrid.SelectedItem != null)
                                {
                                    PackageDataGrid.CurrentCell = new DataGridCellInfo(PackageDataGrid.SelectedItem, PackageDataGrid.Columns[0]);
                                }
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        }
                        else if (status == "Loaded")
                        {
                            // Trigger unload button click
                            UnloadPackagesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            e.Handled = true;
                            // Restore focus to DataGrid cell after operation
                            _ = Dispatcher.BeginInvoke(new Action(() => 
                            {
                                PackageDataGrid.Focus();
                                if (PackageDataGrid.SelectedItem != null)
                                {
                                    PackageDataGrid.CurrentCell = new DataGridCellInfo(PackageDataGrid.SelectedItem, PackageDataGrid.Columns[0]);
                                }
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }
                    // If mixed statuses, do nothing (don't handle the event)
                }
                
                return;
            }
            
            // Handle arrow key navigation to trigger image loading
            if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.PageUp || e.Key == Key.PageDown || e.Key == Key.Home || e.Key == Key.End)
            {
                // Let the default navigation happen first
                await Task.Delay(50); // Small delay to let selection change
                
                // Then trigger image loading for the new selection
                await RefreshSelectionDisplaysImmediate();
            }
        }

        private void DependenciesDataGrid_KeyDown(object sender, KeyEventArgs e)
        {
            // Handle spacebar (Space) or Ctrl+Space to toggle load/unload for dependencies
            if (e.Key == Key.Space && DependenciesDataGrid.SelectedItems.Count > 0)
            {
                // Prevent key repeat - only trigger on first press
                if (e.IsRepeat)
                {
                    e.Handled = true;
                    return;
                }
                
                // Check if Ctrl is pressed for multiple selection, or single item without Ctrl
                bool isCtrlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                bool isSingleSelection = DependenciesDataGrid.SelectedItems.Count == 1;
                
                // Only allow: single item with Space, or multiple items with Ctrl+Space
                if (isSingleSelection || isCtrlPressed)
                {
                    var selectedDependencies = DependenciesDataGrid.SelectedItems.Cast<DependencyItem>().ToList();
                    
                    // Check if all selected items have the same status
                    var statuses = selectedDependencies.Select(d => d.Status).Distinct().ToList();
                    
                    if (statuses.Count == 1)
                    {
                        // All items have same status - proceed with operation
                        var status = statuses[0];
                        
                        if (status == "Available")
                        {
                            // Trigger load
                            LoadDependencies_Click(sender, e);
                            e.Handled = true;
                        }
                        else if (status == "Loaded")
                        {
                            // Trigger unload
                            UnloadDependencies_Click(sender, e);
                            e.Handled = true;
                        }
                        // Missing/Unknown dependencies are now handled through download manager
                        // No keyboard shortcut action needed
                    }
                    // If mixed statuses, do nothing (don't handle the event)
                }
                
                return;
            }
        }

        private void ScenesDataGrid_KeyDown(object sender, KeyEventArgs e)
        {
            // Handle Space or Ctrl+Space to load all dependencies
            if (e.Key == Key.Space && ScenesDataGrid.SelectedItems.Count > 0)
            {
                // Prevent key repeat - only trigger on first press
                if (e.IsRepeat)
                {
                    e.Handled = true;
                    return;
                }

                // Check if Ctrl is pressed for multiple selection, or single item without Ctrl
                bool isCtrlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                bool isSingleSelection = ScenesDataGrid.SelectedItems.Count == 1;

                // Only allow: single item with Space, or multiple items with Ctrl+Space
                if (isSingleSelection || isCtrlPressed)
                {
                    // Check if there are available dependencies to load
                    var hasAvailableDependencies = Dependencies.Any(d => d.Status == "Available" && d.Name != "No dependencies");
                    if (hasAvailableDependencies)
                    {
                        // Trigger load all dependencies button click
                        LoadAllDependenciesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        e.Handled = true;
                        // Restore focus to DataGrid cell after operation
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            ScenesDataGrid.Focus();
                            if (ScenesDataGrid.SelectedItem != null)
                            {
                                ScenesDataGrid.CurrentCell = new DataGridCellInfo(ScenesDataGrid.SelectedItem, ScenesDataGrid.Columns[0]);
                            }
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                }

                return;
            }
        }

        private void CustomAtomDataGrid_KeyDown(object sender, KeyEventArgs e)
        {
            // Handle Space or Ctrl+Space to load all dependencies
            if (e.Key == Key.Space && CustomAtomDataGrid.SelectedItems.Count > 0)
            {
                // Prevent key repeat - only trigger on first press
                if (e.IsRepeat)
                {
                    e.Handled = true;
                    return;
                }

                // Check if Ctrl is pressed for multiple selection, or single item without Ctrl
                bool isCtrlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                bool isSingleSelection = CustomAtomDataGrid.SelectedItems.Count == 1;

                // Only allow: single item with Space, or multiple items with Ctrl+Space
                if (isSingleSelection || isCtrlPressed)
                {
                    // Check if there are available dependencies to load
                    var hasAvailableDependencies = Dependencies.Any(d => d.Status == "Available" && d.Name != "No dependencies");
                    if (hasAvailableDependencies)
                    {
                        // Trigger load all dependencies button click
                        LoadAllDependenciesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        e.Handled = true;
                        // Restore focus to DataGrid cell after operation
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            CustomAtomDataGrid.Focus();
                            if (CustomAtomDataGrid.SelectedItem != null)
                            {
                                CustomAtomDataGrid.CurrentCell = new DataGridCellInfo(CustomAtomDataGrid.SelectedItem, CustomAtomDataGrid.Columns[0]);
                            }
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                }

                return;
            }
        }

        #endregion

        #region Dependencies Drag Selection Handlers

        private void DependenciesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // CRITICAL: Wrap everything in try-catch to prevent app crash
            try
            {
                // Check if we actually clicked on a row (not on empty space)
                var dataGrid = sender as DataGrid;
                if (dataGrid == null)
                    return;
                
                DependencyObject hitElement = null;
                try
                {
                    hitElement = dataGrid.InputHitTest(e.GetPosition(dataGrid)) as DependencyObject;
                }
                catch
                {
                    return;
                }
                
                DataGridRow row = null;
                try
                {
                    row = FindParent<DataGridRow>(hitElement);
                }
                catch
                {
                    return;
                }
                
                if (row == null)
                    return;

                // Only handle if exactly 1 item is selected (ignore group selections)
                if (DependenciesDataGrid.SelectedItems == null || DependenciesDataGrid.SelectedItems.Count != 1)
                    return;

                var selectedDep = DependenciesDataGrid.SelectedItems[0] as DependencyItem;
                if (selectedDep == null || string.IsNullOrEmpty(selectedDep.Name))
                    return;

                // Handle based on status
                if (selectedDep.Status == "Loaded" || selectedDep.Status == "Available")
                {
                    // Open folder path for loaded/available items
                    OpenDependencyFolderPath(selectedDep);
                }
                else if (selectedDep.Status == "Missing" || selectedDep.Status == "Unknown")
                {
                    // Copy to clipboard for missing items
                    CopyDependencyToClipboard(selectedDep);
                }
                
                // Mark event as handled to prevent further processing
                e.Handled = true;
            }
            catch { }
        }

        private void OpenDependencyFolderPath(DependencyItem dependency)
        {
            try
            {
                if (_packageFileManager == null)
                {
                    SetStatus("Package file manager not initialized");
                    return;
                }

                // Dependencies may have .latest suffix, use ResolveDependencyToFilePath
                string filePath = null;
                
                try
                {
                    // Try to resolve the dependency to an actual file path
                    filePath = _packageFileManager.ResolveDependencyToFilePath(dependency.Name);
                }
                catch { }
                
                // If ResolveDependencyToFilePath didn't work, try GetPackageFileInfo
                if (string.IsNullOrEmpty(filePath))
                {
                    try
                    {
                        var fileInfo = _packageFileManager.GetPackageFileInfo(dependency.Name);
                        if (fileInfo != null)
                        {
                            filePath = fileInfo.CurrentPath;
                        }
                    }
                    catch { }
                }
                
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    // Open folder and select the file - Explorer will reuse existing window if same folder
                    OpenFolderAndSelectFile(filePath);
                    SetStatus($"Opened folder for: {dependency.Name}");
                }
                else
                {
                    SetStatus($"File not found: {dependency.Name}");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to open folder: {ex.Message}");
            }
        }

        private void CopyDependencyToClipboard(DependencyItem dependency)
        {
            try
            {
                System.Windows.Clipboard.SetText(dependency.Name);
                SetStatus($"Copied to clipboard: {dependency.Name}");
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to copy to clipboard: {ex.Message}");
            }
        }

        private void PackageDataGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                var dataGrid = sender as DataGrid;
                var hitTest = VisualTreeHelper.HitTest(dataGrid, e.GetPosition(dataGrid));
                var dataGridRow = FindParent<DataGridRow>(hitTest?.VisualHit as DependencyObject);
                
                if (dataGridRow != null)
                {
                    _dragStartPoint = e.GetPosition(dataGrid);
                    _dragStartItem = dataGridRow;
                    _dragButton = e.ChangedButton;
                    _isDragging = false;

                    // Start drag watch timer
                    _dragWatchTimer?.Stop();
                    _dragWatchTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(50)
                    };
                    _dragWatchTimer.Tick += DragWatchTimer_Tick;
                    _dragWatchTimer.Start();
                }
            }
        }

        private void PackageDataGrid_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == _dragButton)
            {
                var wasDragging = _isDragging;
                
                _dragWatchTimer?.Stop();
                _isDragging = false;
                _dragButton = null;
                _dragStartItem = null;
                
                // Ensure selection events are re-enabled
                _suppressSelectionEvents = false;
                
                // Trigger image loading if we were dragging
                if (wasDragging && PackageDataGrid?.SelectedItems?.Count > 0)
                {
                    // Re-trigger the selection changed handler to load images
                    PackageDataGrid_SelectionChanged(PackageDataGrid, null);
                }
            }
        }

        private void PackageDataGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragButton == MouseButton.Left && _dragStartItem != null)
            {
                var dataGrid = sender as DataGrid;
                var currentPoint = e.GetPosition(dataGrid);
                
                // Only start drag selection if we've moved a reasonable distance
                if (Math.Abs(currentPoint.X - _dragStartPoint.X) > 8 || Math.Abs(currentPoint.Y - _dragStartPoint.Y) > 8)
                {
                    // Now we're actually dragging
                    if (!_isDragging)
                    {
                        _isDragging = true;
                    }
                    
                    var hitTest = VisualTreeHelper.HitTest(dataGrid, currentPoint);
                    var currentItem = FindParent<DataGridRow>(hitTest?.VisualHit as DependencyObject);
                    
                    // Normal left button drag selection - select range
                    if (currentItem != null && _dragStartItem != null)
                    {
                        // Suppress selection events only during actual dragging
                        _suppressSelectionEvents = true;
                        try
                        {
                            SelectItemsBetween(dataGrid, _dragStartItem, currentItem);
                        }
                        finally
                        {
                            // Don't re-enable here - wait for mouse up
                        }
                    }
                }
            }
        }

        private void DependenciesDataGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                var dataGrid = sender as DataGrid;
                var hitTest = VisualTreeHelper.HitTest(dataGrid, e.GetPosition(dataGrid));
                var dataGridRow = FindParent<DataGridRow>(hitTest?.VisualHit as DependencyObject);
                
                if (dataGridRow != null)
                {
                    _dragStartPoint = e.GetPosition(dataGrid);
                    _dragStartItem = dataGridRow;
                    _dragButton = e.ChangedButton;
                    _isDragging = false;

                    // Start drag watch timer
                    _dragWatchTimer?.Stop();
                    _dragWatchTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(50)
                    };
                    _dragWatchTimer.Tick += DragWatchTimer_Tick;
                    _dragWatchTimer.Start();

                }
            }
        }

        private void DependenciesDataGrid_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == _dragButton)
            {
                var wasDragging = _isDragging;
                
                _dragWatchTimer?.Stop();
                _isDragging = false;
                _dragButton = null;
                _dragStartItem = null;
                
                // Ensure selection events are re-enabled
                _suppressSelectionEvents = false;
                
                // Update deps search clear button after drag selection
                if (wasDragging)
                {
                    UpdateDepsSearchClearButton();
                }
                
                // Dependencies don't trigger image refresh, but log the completion
            }
        }

        private void DependenciesDataGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragButton == MouseButton.Left && _dragStartItem != null)
            {
                var dataGrid = sender as DataGrid;
                var currentPoint = e.GetPosition(dataGrid);
                
                // Only start drag selection if we've moved a reasonable distance
                if (Math.Abs(currentPoint.X - _dragStartPoint.X) > 8 || Math.Abs(currentPoint.Y - _dragStartPoint.Y) > 8)
                {
                    // Now we're actually dragging
                    if (!_isDragging)
                    {
                        _isDragging = true;
                    }
                    
                    var hitTest = VisualTreeHelper.HitTest(dataGrid, currentPoint);
                    var currentItem = FindParent<DataGridRow>(hitTest?.VisualHit as DependencyObject);
                    
                    // Normal left button drag selection - select range
                    if (currentItem != null && _dragStartItem != null)
                    {
                        // Suppress selection events only during actual dragging
                        _suppressSelectionEvents = true;
                        try
                        {
                            SelectItemsBetween(dataGrid, _dragStartItem, currentItem);
                        }
                        finally
                        {
                            // Don't re-enable here - wait for mouse up
                        }
                    }
                }
            }
        }

        #endregion

        #region Drag Selection Helper Methods

        private void SelectItemsBetween(object control, object startItem, object endItem)
        {
            try
            {
                if (control is DataGrid dataGrid)
                {
                    // DataGrid selection logic
                    var startIndex = dataGrid.ItemContainerGenerator.IndexFromContainer(startItem as DataGridRow);
                    var endIndex = dataGrid.ItemContainerGenerator.IndexFromContainer(endItem as DataGridRow);
                    
                    if (startIndex == -1 || endIndex == -1) return;
                    
                    // Ensure start is before end
                    if (startIndex > endIndex)
                    {
                        var temp = startIndex;
                        startIndex = endIndex;
                        endIndex = temp;
                    }
                    
                    // Clear selection if not holding Ctrl
                    if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl))
                    {
                        dataGrid.SelectedItems.Clear();
                    }
                    
                    // Select all items in range
                    for (int i = startIndex; i <= endIndex; i++)
                    {
                        var item = dataGrid.Items[i];
                        if (item != null && !dataGrid.SelectedItems.Contains(item))
                        {
                            dataGrid.SelectedItems.Add(item);
                        }
                    }
                }
                else if (control is ListView listView)
                {
                    // ListView selection logic (for dependencies)
                    var startIndex = listView.ItemContainerGenerator.IndexFromContainer(startItem as ListViewItem);
                    var endIndex = listView.ItemContainerGenerator.IndexFromContainer(endItem as ListViewItem);
                    
                    if (startIndex == -1 || endIndex == -1) return;
                    
                    // Ensure start is before end
                    if (startIndex > endIndex)
                    {
                        var temp = startIndex;
                        startIndex = endIndex;
                        endIndex = temp;
                    }
                    
                    // Clear selection if not holding Ctrl
                    if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl))
                    {
                        listView.SelectedItems.Clear();
                    }
                    
                    // Select all items in range
                    for (int i = startIndex; i <= endIndex; i++)
                    {
                        var container = listView.ItemContainerGenerator.ContainerFromIndex(i) as ListViewItem;
                        if (container != null)
                        {
                            container.IsSelected = true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore selection errors during drag operations
            }
        }

        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            if (child == null) return null;
            
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            
            if (parentObject == null) return null;
            
            T parent = parentObject as T;
            if (parent != null)
                return parent;
            else
                return FindParent<T>(parentObject);
        }

        private async Task RefreshSelectionDisplays()
        {
            await RefreshSelectionDisplaysImmediate();
        }
        
        private async Task RefreshSelectionDisplaysImmediate()
        {
            // Cancel any previous image loading operation
            _imageLoadingCts?.Cancel();
            _imageLoadingCts?.Dispose();
            _imageLoadingCts = new System.Threading.CancellationTokenSource();
            var imageToken = _imageLoadingCts.Token;
            
            // Always allow new selections to interrupt previous image loading
            // This ensures clicking on a package always loads its images, even if previous loading is in progress
            
            // Skip if already displaying images to prevent concurrent operations
            // if (_isDisplayingImages)
            // {
            //    return;
            // }
            
            try
            {
                _isDisplayingImages = true;
                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
                
                if (selectedPackages.Count == 0)
                {
                    PackageInfoTextBlock.Text = "No packages selected";
                    
                    // Clear images when no packages are selected
                    PreviewImages.Clear();
                    
                    // Clear dependencies when no packages are selected to prevent loading all deps
                    ClearDependenciesDisplay();
                    
                    // Clear category tabs when no packages are selected
                    ClearCategoryTabs();
                    
                    // Hide preview panel when no packages are selected
                    HidePreviewPanel();
                    
                    // Reset both tab counts to 0
                    _dependenciesCount = 0;
                    _dependentsCount = 0;
                    DependenciesCountText.Text = "(0)";
                    DependentsCountText.Text = "(0)";
                    
                    // Don't show dependency images when no packages are selected
                    // DisplaySelectedDependenciesImages();
                }
                else if (selectedPackages.Count == 1)
                {
                    var packageItem = selectedPackages[0];
                    
                    DisplayPackageInfo(packageItem);
                    UpdateBothTabCounts(packageItem);
                    
                    if (_showingDependents)
                        DisplayDependents(packageItem);
                    else
                        DisplayDependencies(packageItem);
                    
                    await DisplayPackageImagesAsync(packageItem, imageToken);
                }
                else
                {
                    DisplayMultiplePackageInfo(selectedPackages);
                    UpdateBothTabCountsForMultiple(selectedPackages);
                    
                    if (_showingDependents)
                        DisplayConsolidatedDependents(selectedPackages);
                    else
                        DisplayConsolidatedDependencies(selectedPackages);
                    
                    // Use standard loading (now optimized)
                    await DisplayMultiplePackageImagesAsync(selectedPackages, null, imageToken);
                }
                
                // Reset scroll position to top for fresh selections
                if (ImagesListView != null)
                {
                    var scrollViewer = FindVisualChild<ScrollViewer>(ImagesListView);
                    scrollViewer?.ScrollToTop();
                }
                
                // Update button bar based on selection
                UpdatePackageButtonBar();
            }
            catch (Exception)
            {
            }
            finally
            {
                _isDisplayingImages = false;
            }
        }
        
        /// <summary>
        /// Refreshes selection displays without loading images (for drag operations)
        /// </summary>
        private async Task RefreshSelectionDisplaysWithoutImages()
        {
            try
            {
                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
                
                if (selectedPackages.Count == 0)
                {
                    // Clear images when no selection
                    PreviewImages.Clear();
                    PackageInfoTextBlock.Text = "No packages selected";
                    ClearDependenciesDisplay();
                    ClearCategoryTabs();
                    
                    // Reset both tab counts to 0
                    _dependenciesCount = 0;
                    _dependentsCount = 0;
                    DependenciesCountText.Text = "(0)";
                    DependentsCountText.Text = "(0)";
                    
                    // Check if dependencies are selected and show their images
                    DisplaySelectedDependenciesImages();
                }
                else if (selectedPackages.Count == 1)
                {
                    var packageItem = selectedPackages[0];
                    DisplayPackageInfo(packageItem);
                    
                    UpdateBothTabCounts(packageItem);
                    
                    if (_showingDependents)
                        DisplayDependents(packageItem);
                    else
                        DisplayDependencies(packageItem);
                    
                    // Skip image loading for drag operations - will be loaded after delay
                }
                else
                {
                    DisplayMultiplePackageInfo(selectedPackages);
                    
                    UpdateBothTabCountsForMultiple(selectedPackages);
                    
                    if (_showingDependents)
                        DisplayConsolidatedDependents(selectedPackages);
                    else
                        DisplayConsolidatedDependencies(selectedPackages);
                    
                    // Skip image loading for drag operations - will be loaded after delay
                }
                
                // Update button bar based on selection
                UpdatePackageButtonBar();
            }
            catch (Exception)
            {
            }
            
            // Return completed task since this is synchronous UI work
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// Loads images for the current selection (used after drag operations)
        /// </summary>
        private async Task LoadImagesForCurrentSelection()
        {
            try
            {
                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
                
                if (selectedPackages.Count == 1)
                {
                    var packageItem = selectedPackages[0];
                    await DisplayPackageImagesAsync(packageItem);
                }
                else if (selectedPackages.Count > 1)
                {
                    await DisplayMultiplePackageImagesAsync(selectedPackages);
                }
                // If no selection, images are already cleared
            }
            catch (Exception)
            {
                // Ignore errors in delayed image loading
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Opens Windows Explorer and selects the specified file.
        /// Note: Windows Explorer opens a new window each time by design when using /select.
        /// </summary>
        private void OpenFolderAndSelectFile(string filePath)
        {
            try
            {
                // Use /select to open Explorer and select the file
                // Note: This will open a new window each time - this is standard Windows behavior
                var argument = $"/select, \"{filePath}\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = argument,
                    UseShellExecute = true
                });
            }
            catch
            {
                throw;
            }
        }

        /// <summary>
        /// Display image previews for selected dependencies
        /// </summary>
        private async void DisplaySelectedDependenciesImages()
        {
            try
            {
                // Skip dependency image display in scene mode - scenes manage their own image display
                if (_currentContentMode == "Scenes")
                {
                    return;
                }
                
                // Skip if already displaying images to prevent concurrent operations
                if (_isDisplayingImages)
                {
                    return;
                }
                
                // Get both selected packages and dependencies
                var selectedPackages = PackageDataGrid?.SelectedItems?.Cast<PackageItem>()?.ToList() ?? new List<PackageItem>();
                var selectedDependencies = DependenciesDataGrid?.SelectedItems?.Cast<DependencyItem>()?.ToList() ?? new List<DependencyItem>();

                // Skip image loading for large selections to prevent UI hang
                // Large selections are typically for batch operations (load/unload/optimize)
                if (selectedDependencies.Count > 50)
                {
                    PreviewImages.Clear();
                    SetStatus($"{selectedDependencies.Count} dependencies selected – image preview disabled for performance");
                    _isDisplayingImages = false;
                    return;
                }

                // Check if the selection has actually changed
                var currentPackageNames = selectedPackages.Select(p => p.Name).OrderBy(n => n).ToList();
                var currentDependencyNames = selectedDependencies.Select(d => d.Name).OrderBy(n => n).ToList();

                if (currentPackageNames.SequenceEqual(_currentlyDisplayedPackages) &&
                    currentDependencyNames.SequenceEqual(_currentlyDisplayedDependencies))
                {
                    // Selection hasn't changed, no need to reload images
                    return;
                }
                
                // Always allow new selections to interrupt previous image loading
                _isDisplayingImages = true;

                // Update the tracking
                _currentlyDisplayedPackages = currentPackageNames;
                _currentlyDisplayedDependencies = currentDependencyNames;

                // Convert selected dependencies to package items
                // DisplayMultiplePackageImagesAsync uses LoadImageAsync which has cache-first strategy
                // and doesn't hold archives open, so no pre-indexing needed here
                List<PackageItem> dependencyPackages = await Task.Run(() =>
                {
                    return ConvertDependenciesToPackages(selectedDependencies);
                });
                
                // Ensure dependency/dependent packages are indexed for image display
                // This is necessary because dependents may not have been in the initial index build
                if (dependencyPackages.Count > 0 && _imageManager != null && _packageManager != null)
                {
                    var unindexedPaths = new List<string>();
                    foreach (var depPackage in dependencyPackages)
                    {
                        var packageKey = !string.IsNullOrEmpty(depPackage.MetadataKey) ? depPackage.MetadataKey : depPackage.Name;
                        if (_packageManager.PackageMetadata.TryGetValue(packageKey, out var metadata))
                        {
                            var packageBase = System.IO.Path.GetFileNameWithoutExtension(metadata.Filename);
                            if (!_imageManager.ImageIndex.ContainsKey(packageBase))
                            {
                                unindexedPaths.Add(metadata.FilePath);
                            }
                        }
                    }
                    
                    // Index any missing packages
                    if (unindexedPaths.Count > 0)
                    {
                        await _imageManager.BuildImageIndexFromVarsAsync(unindexedPaths, false);
                    }
                }

                // If dependencies are selected, show ONLY their images, not the parent packages
                var allPackages = new List<PackageItem>();
                var packageSources = new List<bool>(); // true = package, false = dependency

                if (dependencyPackages != null && dependencyPackages.Count > 0)
                {
                    // Show only dependency/dependent images
                    allPackages = dependencyPackages;
                    packageSources = Enumerable.Repeat(false, dependencyPackages.Count).ToList();
                }
                else
                {
                    // Show only parent package images
                    allPackages = selectedPackages;
                    packageSources = Enumerable.Repeat(true, selectedPackages.Count).ToList();
                }

                if (allPackages.Count == 0)
                {
                    PreviewImages.Clear();
                }
                else
                {
                    // Display images for either parent packages or selected dependencies/dependents
                    // Use cancellation token so image loading can be interrupted by load/unload/optimize operations
                    if (_imageLoadingCts == null)
                    {
                        _imageLoadingCts = new System.Threading.CancellationTokenSource();
                    }
                    
                    if (allPackages.Count == 1)
                    {
                        await DisplayPackageImagesAsync(allPackages[0], _imageLoadingCts.Token);
                    }
                    else
                    {
                        await DisplayMultiplePackageImagesAsync(allPackages, packageSources, _imageLoadingCts.Token);
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                _isDisplayingImages = false;
            }
        }

        /// <summary>
        /// Convert selected dependencies to package items by finding matching packages
        /// </summary>
        private List<PackageItem> ConvertDependenciesToPackages(List<DependencyItem> dependencies)
        {
            var result = new List<PackageItem>();

            if (dependencies == null || dependencies.Count == 0)
            {
                return result;
            }

            if (_packageManager?.PackageMetadata == null || _packageManager.PackageMetadata.Count == 0)
            {
                return result;
            }

            // Use cached lookup to avoid rebuilding on every call (major performance improvement)
            // Only rebuild if package metadata count has changed
            var currentVersion = _packageManager.PackageMetadata.Count;
            if (_packageLookupCache == null || _packageLookupCacheVersion != currentVersion)
            {
                _packageLookupCache = new Dictionary<string, List<(string key, int version)>>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var kvp in _packageManager.PackageMetadata)
                {
                    var key = kvp.Key;
                    var normalizedKey = NormalizePackageName(key); // Remove #archived suffix
                    var version = ExtractVersionFromPackageName(normalizedKey);
                    
                    // Extract base name (without version number)
                    // e.g., "Creator.Package.1" -> "Creator.Package"
                    string baseName = normalizedKey;
                    if (version > 0)
                    {
                        var parts = normalizedKey.Split('.');
                        if (parts.Length >= 3 && int.TryParse(parts.Last(), out _))
                        {
                            baseName = string.Join(".", parts.Take(parts.Length - 1));
                        }
                    }
                    
                    if (!_packageLookupCache.ContainsKey(baseName))
                    {
                        _packageLookupCache[baseName] = new List<(string, int)>();
                    }
                    
                    _packageLookupCache[baseName].Add((key, version));
                }
                
                _packageLookupCacheVersion = currentVersion;
            }

            foreach (var dependency in dependencies)
            {
                // Skip placeholder items
                if (dependency.Name == "No dependencies" || dependency.Name == "No dependencies found" ||
                    dependency.Name == "No dependents" || dependency.Name == "No dependents found")
                    continue;

                string baseDependencyName = dependency.Name;
                bool isLatest = string.Equals(dependency.Version, "latest", StringComparison.OrdinalIgnoreCase);
                int? requestedVersion = null;
                if (!string.IsNullOrEmpty(dependency.Version) && !isLatest)
                {
                    if (int.TryParse(dependency.Version, NumberStyles.Integer, CultureInfo.InvariantCulture, out var versionNumber))
                    {
                        requestedVersion = versionNumber;
                    }
                }

                // Fast lookup using pre-built cache instead of scanning all keys
                if (!_packageLookupCache.TryGetValue(baseDependencyName, out var matchingEntries))
                {
                    continue;
                }

                string selectedKey = null;

                if (requestedVersion.HasValue)
                {
                    // Find exact version match
                    var versionMatch = matchingEntries.FirstOrDefault(e => e.version == requestedVersion.Value);
                    if (versionMatch != default)
                    {
                        selectedKey = versionMatch.key;
                    }
                }

                if (selectedKey == null)
                {
                    if (isLatest)
                    {
                        // Find highest version
                        var maxVersion = matchingEntries.Max(e => e.version);
                        selectedKey = matchingEntries
                            .Where(e => e.version == maxVersion)
                            .Select(e => e.key)
                            .FirstOrDefault();
                        
                        // Prefer archived if available
                        var archivedKey = matchingEntries
                            .Where(e => e.version == maxVersion && e.key.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                            .Select(e => e.key)
                            .FirstOrDefault();
                        if (!string.IsNullOrEmpty(archivedKey))
                        {
                            selectedKey = archivedKey;
                        }
                    }
                    else
                    {
                        // No specific version requested, use first match (prefer archived)
                        var archivedKey = matchingEntries.FirstOrDefault(e => e.key.EndsWith("#archived", StringComparison.OrdinalIgnoreCase));
                        selectedKey = archivedKey != default ? archivedKey.key : matchingEntries.First().key;
                    }
                }

                if (string.IsNullOrEmpty(selectedKey))
                {
                    continue;
                }

                if (_packageManager.PackageMetadata.TryGetValue(selectedKey, out var metadata))
                {
                    var packageItem = CreatePackageItemFromMetadata(selectedKey, metadata);
                    result.Add(packageItem);
                }
            }

            return result;
        }

        /// <summary>
        /// Extract version number from a package name like "Creator.Package.123"
        /// </summary>
        private static string NormalizePackageName(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
            {
                return packageName;
            }

            return packageName.EndsWith("#archived", StringComparison.OrdinalIgnoreCase)
                ? packageName[..^9]
                : packageName;
        }

        private int ExtractVersionFromPackageName(string packageName)
        {
            var normalizedName = NormalizePackageName(packageName);
            var parts = normalizedName.Split('.');
            if (parts.Length >= 3 && int.TryParse(parts.Last(), out var version))
            {
                return version;
            }
            return 0; // Default version if no version found
        }

        private static string SelectPreferredMetadataKey(List<string> candidateKeys)
        {
            if (candidateKeys == null || candidateKeys.Count == 0)
            {
                return null;
            }

            // Prefer archived variant if available to match archived dependency context
            var archivedKey = candidateKeys.FirstOrDefault(k => k.EndsWith("#archived", StringComparison.OrdinalIgnoreCase));
            return archivedKey ?? candidateKeys.First();
        }

        private PackageItem CreatePackageItemFromMetadata(string metadataKey, VarMetadata metadata)
        {
            if (metadata == null)
            {
                return null;
            }

            string packageName = metadataKey.EndsWith("#archived", StringComparison.OrdinalIgnoreCase)
                ? metadataKey
                : Path.GetFileNameWithoutExtension(metadata.Filename);

            return new PackageItem
            {
                MetadataKey = metadataKey,
                Name = packageName,
                Status = metadata.Status,
                Creator = metadata.CreatorName ?? "Unknown",
                DependencyCount = metadata.Dependencies?.Count ?? 0,
                DependentsCount = 0, // Will be calculated on full refresh
                FileSize = metadata.FileSize,
                ModifiedDate = metadata.ModifiedDate,
                IsLatestVersion = true,
                IsOptimized = metadata.IsOptimized,
                IsDuplicate = metadata.IsDuplicate,
                DuplicateLocationCount = metadata.DuplicateLocationCount,
                IsOldVersion = metadata.IsOldVersion,
                LatestVersionNumber = metadata.LatestVersionNumber,
                IsDamaged = metadata.IsDamaged,
                DamageReason = metadata.DamageReason,
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
        }

        #endregion

        #region Filter Resize Thumb Event Handlers

        private void FilterResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.Thumb thumb && thumb.Tag is string filterType)
            {
                try
                {
                    // Update the height dynamically as the thumb is being dragged
                    ListBox targetList = GetFilterListBox(filterType);
                    
                    if (targetList != null)
                    {
                        double newHeight = targetList.ActualHeight + e.VerticalChange;
                        // Clamp to min height only, allow very large max height (effectively unlimited)
                        double minHeight = targetList.MinHeight > 0 ? targetList.MinHeight : 50;
                        double maxHeight = 5000; // Very large maximum height (effectively unlimited for practical use)
                        newHeight = Math.Max(minHeight, Math.Min(maxHeight, newHeight));
                        targetList.Height = newHeight;
                    }
                }
                catch (Exception)
                {
                    // Ignore errors during drag
                }
            }
        }

        private void FilterResizeThumb_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.Thumb thumb && thumb.Tag is string filterType)
            {
                try
                {
                    // Save the new height to settings based on which filter was resized
                    switch (filterType)
                    {
                        case "DateFilter":
                            if (DateFilterList != null)
                                _settingsManager.Settings.DateFilterHeight = DateFilterList.ActualHeight;
                            break;
                        case "StatusFilter":
                            if (StatusFilterList != null)
                                _settingsManager.Settings.StatusFilterHeight = StatusFilterList.ActualHeight;
                            break;
                        case "ContentTypesFilter":
                            if (ContentTypesList != null)
                                _settingsManager.Settings.ContentTypesFilterHeight = ContentTypesList.ActualHeight;
                            break;
                        case "CreatorsFilter":
                            if (CreatorsList != null)
                                _settingsManager.Settings.CreatorsFilterHeight = CreatorsList.ActualHeight;
                            break;
                        case "SubfoldersFilter":
                            if (SubfoldersFilterList != null)
                                _settingsManager.Settings.SubfoldersFilterHeight = SubfoldersFilterList.ActualHeight;
                            break;
                        case "LicenseTypeFilter":
                            if (LicenseTypeList != null)
                                _settingsManager.Settings.LicenseTypeFilterHeight = LicenseTypeList.ActualHeight;
                            break;
                        case "FileSizeFilter":
                            if (FileSizeFilterList != null)
                                _settingsManager.Settings.FileSizeFilterHeight = FileSizeFilterList.ActualHeight;
                            break;
                        case "DamagedFilter":
                            if (DamagedFilterList != null)
                                _settingsManager.Settings.DamagedFilterHeight = DamagedFilterList.ActualHeight;
                            break;
                    }
                }
                catch (Exception)
                {
                    // Ignore errors saving filter heights
                }
            }
        }

        private ListBox GetFilterListBox(string filterType)
        {
            return filterType switch
            {
                "DateFilter" => DateFilterList,
                "StatusFilter" => StatusFilterList,
                "ContentTypesFilter" => ContentTypesList,
                "CreatorsFilter" => CreatorsList,
                "SubfoldersFilter" => SubfoldersFilterList,
                "LicenseTypeFilter" => LicenseTypeList,
                "FileSizeFilter" => FileSizeFilterList,
                "DamagedFilter" => DamagedFilterList,
                _ => null
            };
        }

        private void ToggleFilterVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string filterType)
            {
                try
                {
                    // Toggle visibility for the specified filter section
                    bool newVisibility = false;
                    ListBox targetList = null;
                    Grid textBoxGrid = null;
                    Grid expandedGrid = null;
                    Grid collapsedGrid = null;
                    
                    switch (filterType)
                    {
                        case "DateFilter":
                            newVisibility = !_settingsManager.Settings.DateFilterVisible;
                            _settingsManager.Settings.DateFilterVisible = newVisibility;
                            targetList = DateFilterList;
                            expandedGrid = DateFilterExpandedGrid;
                            collapsedGrid = DateFilterCollapsedGrid;
                            break;
                        case "StatusFilter":
                            newVisibility = !_settingsManager.Settings.StatusFilterVisible;
                            _settingsManager.Settings.StatusFilterVisible = newVisibility;
                            targetList = StatusFilterList;
                            expandedGrid = StatusFilterExpandedGrid;
                            collapsedGrid = StatusFilterCollapsedGrid;
                            break;
                        case "ContentTypesFilter":
                            newVisibility = !_settingsManager.Settings.ContentTypesFilterVisible;
                            _settingsManager.Settings.ContentTypesFilterVisible = newVisibility;
                            targetList = ContentTypesList;
                            textBoxGrid = ContentTypesFilterTextBoxGrid;
                            collapsedGrid = ContentTypesFilterCollapsedGrid;
                            break;
                        case "CreatorsFilter":
                            newVisibility = !_settingsManager.Settings.CreatorsFilterVisible;
                            _settingsManager.Settings.CreatorsFilterVisible = newVisibility;
                            targetList = CreatorsList;
                            textBoxGrid = CreatorsFilterTextBoxGrid;
                            collapsedGrid = CreatorsFilterCollapsedGrid;
                            break;
                        case "LicenseTypeFilter":
                            newVisibility = !_settingsManager.Settings.LicenseTypeFilterVisible;
                            _settingsManager.Settings.LicenseTypeFilterVisible = newVisibility;
                            targetList = LicenseTypeList;
                            textBoxGrid = LicenseTypeFilterTextBoxGrid;
                            collapsedGrid = LicenseTypeFilterCollapsedGrid;
                            break;
                        case "FileSizeFilter":
                            newVisibility = !_settingsManager.Settings.FileSizeFilterVisible;
                            _settingsManager.Settings.FileSizeFilterVisible = newVisibility;
                            targetList = FileSizeFilterList;
                            expandedGrid = FileSizeFilterExpandedGrid;
                            collapsedGrid = FileSizeFilterCollapsedGrid;
                            break;
                        case "SubfoldersFilter":
                            newVisibility = !_settingsManager.Settings.SubfoldersFilterVisible;
                            _settingsManager.Settings.SubfoldersFilterVisible = newVisibility;
                            targetList = SubfoldersFilterList;
                            expandedGrid = SubfoldersFilterTextBoxGrid;
                            collapsedGrid = SubfoldersFilterCollapsedGrid;
                            break;
                        case "DamagedFilter":
                            newVisibility = !_settingsManager.Settings.DamagedFilterVisible;
                            _settingsManager.Settings.DamagedFilterVisible = newVisibility;
                            targetList = DamagedFilterList;
                            expandedGrid = DamagedFilterExpandedGrid;
                            collapsedGrid = DamagedFilterCollapsedGrid;
                            break;
                        case "SceneTypeFilter":
                            newVisibility = !_settingsManager.Settings.SceneTypeFilterVisible;
                            _settingsManager.Settings.SceneTypeFilterVisible = newVisibility;
                            targetList = SceneTypeFilterList;
                            textBoxGrid = SceneTypeFilterTextBoxGrid;
                            expandedGrid = null;
                            collapsedGrid = SceneTypeFilterCollapsedGrid;
                            break;
                        case "SceneCreatorFilter":
                            newVisibility = !_settingsManager.Settings.SceneCreatorFilterVisible;
                            _settingsManager.Settings.SceneCreatorFilterVisible = newVisibility;
                            targetList = SceneCreatorFilterList;
                            textBoxGrid = SceneCreatorFilterTextBoxGrid;
                            expandedGrid = null;
                            collapsedGrid = SceneCreatorFilterCollapsedGrid;
                            break;
                        case "SceneSourceFilter":
                            newVisibility = !_settingsManager.Settings.SceneSourceFilterVisible;
                            _settingsManager.Settings.SceneSourceFilterVisible = newVisibility;
                            targetList = SceneSourceFilterList;
                            expandedGrid = SceneSourceFilterExpandedGrid;
                            collapsedGrid = SceneSourceFilterCollapsedGrid;
                            break;
                        case "PresetCategoryFilter":
                            newVisibility = !_settingsManager.Settings.PresetCategoryFilterVisible;
                            _settingsManager.Settings.PresetCategoryFilterVisible = newVisibility;
                            targetList = PresetCategoryFilterList;
                            textBoxGrid = PresetCategoryFilterTextBoxGrid;
                            collapsedGrid = PresetCategoryFilterCollapsedGrid;
                            break;
                        case "PresetSubfolderFilter":
                            newVisibility = !_settingsManager.Settings.PresetSubfolderFilterVisible;
                            _settingsManager.Settings.PresetSubfolderFilterVisible = newVisibility;
                            targetList = PresetSubfolderFilterList;
                            textBoxGrid = PresetSubfolderFilterTextBoxGrid;
                            collapsedGrid = PresetSubfolderFilterCollapsedGrid;
                            break;
                        case "SceneDateFilter":
                            if (_settingsManager?.Settings != null)
                            {
                                _settingsManager.Settings.SceneDateFilterVisible = !_settingsManager.Settings.SceneDateFilterVisible;
                                ApplyFilterVisibilityStates(_settingsManager.Settings);
                            }
                            break;
                        case "SceneFileSizeFilter":
                            if (_settingsManager?.Settings != null)
                            {
                                _settingsManager.Settings.SceneFileSizeFilterVisible = !_settingsManager.Settings.SceneFileSizeFilterVisible;
                                ApplyFilterVisibilityStates(_settingsManager.Settings);
                            }
                            break;
                        case "PresetDateFilter":
                            newVisibility = !_settingsManager.Settings.PresetDateFilterVisible;
                            _settingsManager.Settings.PresetDateFilterVisible = newVisibility;
                            targetList = PresetDateFilterList;
                            expandedGrid = PresetDateFilterExpandedGrid;
                            collapsedGrid = PresetDateFilterCollapsedGrid;
                            break;
                        case "PresetFileSizeFilter":
                            newVisibility = !_settingsManager.Settings.PresetFileSizeFilterVisible;
                            _settingsManager.Settings.PresetFileSizeFilterVisible = newVisibility;
                            targetList = PresetFileSizeFilterList;
                            expandedGrid = PresetFileSizeFilterExpandedGrid;
                            collapsedGrid = PresetFileSizeFilterCollapsedGrid;
                            break;
                        case "SceneStatusFilter":
                            if (_settingsManager?.Settings != null)
                            {
                                _settingsManager.Settings.SceneStatusFilterVisible = !_settingsManager.Settings.SceneStatusFilterVisible;
                                ApplyFilterVisibilityStates(_settingsManager.Settings);
                            }
                            break;
                        case "PresetStatusFilter":
                            newVisibility = !_settingsManager.Settings.PresetStatusFilterVisible;
                            _settingsManager.Settings.PresetStatusFilterVisible = newVisibility;
                            targetList = PresetStatusFilterList;
                            expandedGrid = PresetStatusFilterExpandedGrid;
                            collapsedGrid = PresetStatusFilterCollapsedGrid;
                            break;
                    }
                    
                    // Update UI elements
                    if (targetList != null)
                    {
                        if (newVisibility)
                        {
                            // Show expanded state
                            targetList.Visibility = System.Windows.Visibility.Visible;
                            if (textBoxGrid != null) textBoxGrid.Visibility = System.Windows.Visibility.Visible;
                            if (expandedGrid != null) expandedGrid.Visibility = System.Windows.Visibility.Visible;
                            if (collapsedGrid != null) collapsedGrid.Visibility = System.Windows.Visibility.Collapsed;
                        }
                        else
                        {
                            // Show collapsed state
                            targetList.Visibility = System.Windows.Visibility.Collapsed;
                            if (textBoxGrid != null) textBoxGrid.Visibility = System.Windows.Visibility.Collapsed;
                            if (expandedGrid != null) expandedGrid.Visibility = System.Windows.Visibility.Collapsed;
                            if (collapsedGrid != null) collapsedGrid.Visibility = System.Windows.Visibility.Visible;
                        }
                    }
                }
                catch (Exception)
                {
                    // Ignore errors toggling filter visibility
                }
            }
        }

        #endregion

        #region Filter Move Handlers

        /// <summary>
        /// Handles moving a filter up in the order
        /// </summary>
        private void FilterMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string filterType)
            {
                MoveFilter(filterType, -1);
            }
        }

        /// <summary>
        /// Handles moving a filter down in the order
        /// </summary>
        private void FilterMoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string filterType)
            {
                MoveFilter(filterType, 1);
            }
        }

        /// <summary>
        /// Moves a filter up or down in the order
        /// </summary>
        private void MoveFilter(string filterType, int direction)
        {
            try
            {
                // Get the appropriate filter order list based on current content mode
                List<string> filterOrder = GetCurrentFilterOrder();
                if (filterOrder == null)
                    return;

                // Find current index
                int currentIndex = filterOrder.IndexOf(filterType);
                if (currentIndex == -1)
                    return;

                // Calculate new index
                int newIndex = currentIndex + direction;
                if (newIndex < 0 || newIndex >= filterOrder.Count)
                    return; // Can't move beyond bounds

                // Swap positions
                filterOrder.RemoveAt(currentIndex);
                filterOrder.Insert(newIndex, filterType);

                // Save the new order and refresh the UI
                SaveCurrentFilterOrder(filterOrder);
                RefreshFilterOrder();
            }
            catch (Exception)
            {
                // Ignore move errors
            }
        }

        /// <summary>
        /// Gets the current filter order list based on content mode
        /// </summary>
        private List<string> GetCurrentFilterOrder()
        {
            switch (_currentContentMode)
            {
                case "Packages":
                    return _settingsManager.Settings.PackageFilterOrder;
                case "Scenes":
                    return _settingsManager.Settings.SceneFilterOrder;
                case "Presets":
                    return _settingsManager.Settings.PresetFilterOrder;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Saves the current filter order to settings
        /// </summary>
        private void SaveCurrentFilterOrder(List<string> filterOrder)
        {
            switch (_currentContentMode)
            {
                case "Packages":
                    _settingsManager.Settings.PackageFilterOrder = new List<string>(filterOrder);
                    break;
                case "Scenes":
                    _settingsManager.Settings.SceneFilterOrder = new List<string>(filterOrder);
                    break;
                case "Presets":
                    _settingsManager.Settings.PresetFilterOrder = new List<string>(filterOrder);
                    break;
            }
        }

        /// <summary>
        /// Refreshes the filter order in the UI
        /// </summary>
        private void RefreshFilterOrder()
        {
            try
            {
                // Get the filter container
                var filterContainer = GetCurrentFilterContainer();
                if (filterContainer == null)
                    return;

                // Get the current filter order
                var filterOrder = GetCurrentFilterOrder();
                if (filterOrder == null)
                    return;

                // Create a dictionary to store filter elements
                var filterElements = new Dictionary<string, StackPanel>();

                // Collect all filter StackPanels
                for (int i = filterContainer.Children.Count - 1; i >= 0; i--)
                {
                    if (filterContainer.Children[i] is StackPanel stackPanel)
                    {
                        string filterType = GetFilterTypeFromStackPanel(stackPanel);
                        if (!string.IsNullOrEmpty(filterType) && filterOrder.Contains(filterType))
                        {
                            filterElements[filterType] = stackPanel;
                            filterContainer.Children.RemoveAt(i);
                        }
                    }
                }

                // Re-add filters in the correct order
                foreach (string filterType in filterOrder)
                {
                    if (filterElements.ContainsKey(filterType))
                    {
                        filterContainer.Children.Add(filterElements[filterType]);
                    }
                }
            }
            catch (Exception)
            {
                // Ignore refresh errors
            }
        }

        /// <summary>
        /// Gets the current filter container based on content mode
        /// </summary>
        private StackPanel GetCurrentFilterContainer()
        {
            switch (_currentContentMode)
            {
                case "Packages":
                    return PackageFiltersContainer;
                case "Scenes":
                    return SceneFiltersContainer;
                case "Presets":
                    return PresetFiltersContainer;
                default:
                    return null;
            }
        }

        #endregion
        
        #region Download Button Handlers
        
        /// <summary>
        /// Updates the visibility of the download missing button
        /// </summary>
        private void UpdateDownloadMissingButtonVisibility()
        {
            try
            {
                if (DownloadMissingButton == null || DependenciesDataGrid == null)
                    return;
                
                // Check if any selected dependencies are missing
                var hasMissingDeps = DependenciesDataGrid.SelectedItems
                    .Cast<DependencyItem>()
                    .Any(d => d.Status == "Missing" || d.Status == "Unknown");
                
                DownloadMissingButton.Visibility = hasMissingDeps ? Visibility.Visible : Visibility.Collapsed;
                
                // Update counter badge
                UpdateDownloadCounter();
            }
            catch { }
        }
        
        /// <summary>
        /// Updates the download counter badge on the download button
        /// </summary>
        private void UpdateDownloadCounter()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    int activeDownloads = _currentProgressWindow?.GetActiveDownloadCount() ?? 0;
                    
                    if (activeDownloads > 0)
                    {
                        DownloadCounterText.Text = activeDownloads.ToString();
                        DownloadCounterBadge.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        DownloadCounterBadge.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch { }
        }
        
        /// <summary>
        /// Handles the download missing button click
        /// Opens Package Downloads window with missing dependencies pre-filled
        /// </summary>
        private void DownloadMissingButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Get missing dependencies
                var missingDeps = Dependencies
                    .Where(d => d.Status == "Missing" || d.Status == "Unknown")
                    .Select(d => d.Name)
                    .ToList();
                
                if (missingDeps.Count == 0)
                {
                    CustomMessageBox.Show("No missing dependencies found.",
                        "No Missing Dependencies", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Check if a folder has been selected
                if (string.IsNullOrEmpty(_selectedFolder))
                {
                    CustomMessageBox.Show(
                        "Please select a VAM root folder first.",
                        "No Folder Selected",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                // Ensure package downloader is initialized
                if (_packageDownloader == null)
                {
                    InitializePackageDownloader();
                }

                // Get the AddonPackages folder path
                string addonPackagesFolder = System.IO.Path.Combine(_selectedFolder, "AddonPackages");
                
                // Create or reuse the Package Downloads window
                if (_packageDownloadsWindow == null || !_packageDownloadsWindow.IsLoaded)
                {
                    _packageDownloadsWindow = new PackageSearchWindow(
                        _packageManager,
                        _packageDownloader,
                        _downloadQueueManager,
                        addonPackagesFolder,
                        LoadPackageDownloadListAsync,
                        OnPackageDownloadedFromSearchWindow)
                    {
                        Owner = this
                    };
                }

                // Show and bring to front
                if (!_packageDownloadsWindow.IsVisible)
                {
                    _packageDownloadsWindow.Show();
                }
                else
                {
                    // Restore from minimized state if needed
                    if (_packageDownloadsWindow.WindowState == WindowState.Minimized)
                    {
                        _packageDownloadsWindow.WindowState = WindowState.Normal;
                    }
                    _packageDownloadsWindow.Activate();
                }
                
                // Append missing dependencies and auto-trigger search
                _packageDownloadsWindow.AppendPackageNames(missingDeps, autoSearch: true);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error opening downloads window: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        #endregion
        
        #region Toolbar Button Handlers
        
        /// <summary>
        /// Updates the toolbar button text with counters
        /// </summary>
        private void UpdateToolbarButtons()
        {
            try
            {
                int selectedCount = PackageDataGrid?.SelectedItems.Count ?? 0;
                
                // Note: Fix Duplicates button is now handled in UpdatePackageButtonBar() to avoid animation conflicts
            }
            catch { }
        }
        
        /// <summary>
        /// Opens the Fix Duplicates window with ALL duplicates detected by the app
        /// </summary>
        private async void FixDuplicates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                
                // Find ALL duplicate packages in the app (not just selected ones)
                var duplicatePackages = FindAllDuplicateInstances();
                
                
                if (duplicatePackages.Count == 0)
                {
                    DarkMessageBox.Show("No duplicates found in the package collection.", "Fix Duplicates",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Cancel any pending image loading operations
                _imageLoadingCts?.Cancel();
                _imageLoadingCts = new System.Threading.CancellationTokenSource();
                
                // Clear image preview grid
                PreviewImages.Clear();
                
                // Release file locks for all duplicate packages
                var packageNames = duplicatePackages.Select(p => p.Name).ToList();
                await _imageManager.ReleasePackagesAsync(packageNames);
                
                // Get folder paths
                string addonPackagesPath = Path.Combine(_selectedFolder, "AddonPackages");
                string allPackagesPath = Path.Combine(_selectedFolder, "AllPackages");
                
                // Open the duplicate management window
                var duplicateWindow = new DuplicateManagementWindow(duplicatePackages, addonPackagesPath, allPackagesPath)
                {
                    Owner = this
                };
                
                var result = duplicateWindow.ShowDialog();
                
                // If user fixed duplicates, refresh the package list
                if (result == true)
                {
                    SetStatus("Refreshing package list after fixing duplicates...");
                    RefreshPackages();
                    
                    // Refresh image grid
                    await RefreshCurrentlyDisplayedImagesAsync();
                }
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show($"Error opening duplicate management window: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Fix only the selected duplicate packages
        /// </summary>
        private async Task FixSelectedDuplicates()
        {
            try
            {
                // Find duplicate instances for selected packages only
                var duplicatePackages = FindSelectedDuplicateInstances();
                
                if (duplicatePackages.Count == 0)
                {
                    DarkMessageBox.Show("No duplicates found in the selected packages.", "Fix Duplicates",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Cancel any pending image loading operations
                _imageLoadingCts?.Cancel();
                _imageLoadingCts = new System.Threading.CancellationTokenSource();
                
                // Clear image preview grid
                PreviewImages.Clear();
                
                // Release file locks for all duplicate packages
                var packageNames = duplicatePackages.Select(p => p.Name).ToList();
                await _imageManager.ReleasePackagesAsync(packageNames);
                
                // Get folder paths
                string addonPackagesPath = Path.Combine(_selectedFolder, "AddonPackages");
                string allPackagesPath = Path.Combine(_selectedFolder, "AllPackages");
                
                // Open the duplicate management window
                var duplicateWindow = new DuplicateManagementWindow(duplicatePackages, addonPackagesPath, allPackagesPath)
                {
                    Owner = this
                };
                
                var result = duplicateWindow.ShowDialog();
                
                // If user fixed duplicates, refresh the package list
                if (result == true)
                {
                    SetStatus("Refreshing package list after fixing duplicates...");
                    RefreshPackages();
                    
                    // Refresh image grid
                    await RefreshCurrentlyDisplayedImagesAsync();
                }
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show($"Error opening duplicate management window: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Find ALL duplicate package instances in the app
        /// </summary>
        private List<PackageItem> FindAllDuplicateInstances()
        {
            var duplicatePackages = new List<PackageItem>();
            
            
            // Find all packages marked as duplicates or with DuplicateLocationCount > 1
            foreach (var package in Packages)
            {
                if (package.IsDuplicate || package.DuplicateLocationCount > 1)
                {
                    duplicatePackages.Add(package);
                }
            }
            
            return duplicatePackages;
        }
        
        /// <summary>
        /// Find duplicate package instances for the selected packages only
        /// </summary>
        private List<PackageItem> FindSelectedDuplicateInstances()
        {
            var duplicatePackages = new List<PackageItem>();
            var selectedBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            
            // First, collect base names of selected duplicate packages
            foreach (var item in PackageDataGrid.SelectedItems)
            {
                if (item is PackageItem pkg)
                {
                    
                    if (pkg.IsDuplicate || pkg.DuplicateLocationCount > 1)
                    {
                        string baseName = ExtractBasePackageName(pkg.DisplayName);
                        selectedBaseNames.Add(baseName);
                    }
                    else
                    {
                    }
                }
            }
            
            
            // Then, find ALL instances of those selected base names across the entire package collection
            // Don't filter by DuplicateLocationCount here - let DuplicateManagementWindow do filesystem scan
            foreach (var package in Packages)
            {
                string baseName = ExtractBasePackageName(package.DisplayName);
                if (selectedBaseNames.Contains(baseName))
                {
                    duplicatePackages.Add(package);
                }
            }
            
            return duplicatePackages;
        }
        
        /// <summary>
        /// Extract base package name from display name (Creator.PackageName without version)
        /// </summary>
        private string ExtractBasePackageName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return displayName;
                
            var parts = displayName.Split('.');
            if (parts.Length >= 3) // Creator.PackageName.Version format
            {
                return $"{parts[0]}.{parts[1]}"; // Return Creator.PackageName
            }
            
            return displayName; // Return as-is if format doesn't match
        }

        /// <summary>
        /// Formats a number with K suffix for counts over 1000
        /// </summary>
        private string FormatCountWithSuffix(int count)
        {
            if (count >= 1000)
            {
                double thousands = count / 1000.0;
                return $"{thousands:0.#}K";
            }
            return count.ToString();
        }
        
        // UpdateDatabaseButtonSuccess method removed - button no longer exists in UI
        // Database status is now shown in the PackageSearchWindow itself
        
        /// <summary>
        /// Callback invoked when a package is downloaded from the PackageSearchWindow
        /// Updates the main package list and dependencies
        /// </summary>
        private async void OnPackageDownloadedFromSearchWindow(string packageName, string filePath)
        {
            try
            {
                
                if (!System.IO.File.Exists(filePath))
                {
                    return;
                }
                
                // Parse metadata in background
                var metadata = await Task.Run(() => _packageManager?.ParseVarMetadataComplete(filePath));
                if (metadata == null)
                {
                    return;
                }
                
                
                // Set status to Loaded before adding to dictionary
                metadata.Status = "Loaded";
                metadata.FilePath = filePath;
                
                // Update UI on dispatcher thread
                await Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        // Preserve package selection before making changes
                        var selectedPackageNames = PreserveDataGridSelections();
                        
                        // Update dependency status - try multiple matching strategies
                        var dep = Dependencies.FirstOrDefault(d => 
                            d.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase) ||
                            d.DisplayName.Equals(packageName, StringComparison.OrdinalIgnoreCase) ||
                            d.DisplayName.Equals(metadata.PackageName, StringComparison.OrdinalIgnoreCase) ||
                            packageName.StartsWith(d.Name + ".", StringComparison.OrdinalIgnoreCase));
                        
                        if (dep != null)
                        {
                            dep.Status = "Available";
                        }
                        
                        // Check if package already exists in the Packages collection
                        var existingPackage = Packages.FirstOrDefault(p => 
                            p.Name.Equals(metadata.PackageName, StringComparison.OrdinalIgnoreCase));
                        
                        if (existingPackage != null)
                        {
                            existingPackage.Status = "Loaded";
                            existingPackage.FileSize = metadata.FileSize;
                            existingPackage.ModifiedDate = metadata.ModifiedDate;
                            existingPackage.IsOptimized = metadata.IsOptimized;
                            existingPackage.IsDuplicate = metadata.IsDuplicate;
                            existingPackage.DuplicateLocationCount = metadata.DuplicateLocationCount;
                            existingPackage.DependencyCount = metadata.Dependencies?.Count ?? 0;
                            existingPackage.DependentsCount = 0; // Will be calculated on full refresh
                        }
                        else
                        {
                            var newPackage = new PackageItem
                            {
                                Name = metadata.PackageName,
                                Status = "Loaded",
                                Creator = metadata.CreatorName ?? "Unknown",
                                DependencyCount = metadata.Dependencies?.Count ?? 0,
                                DependentsCount = 0, // Will be calculated on full refresh
                                FileSize = metadata.FileSize,
                                ModifiedDate = metadata.ModifiedDate,
                                IsLatestVersion = true,
                                IsOptimized = metadata.IsOptimized,
                                IsDuplicate = metadata.IsDuplicate,
                                DuplicateLocationCount = metadata.DuplicateLocationCount
                            };
                            
                            Packages.Add(newPackage);
                        }
                        
                        // Refresh filter lists to include the new package
                        RefreshFilterLists();
                        
                        // Refresh the view so the package appears in the DataGrid
                        PackagesView?.Refresh();
                        
                        // Restore package selection after all UI updates
                        await Dispatcher.InvokeAsync(() =>
                        {
                            RestoreDataGridSelections(selectedPackageNames);
                        }, System.Windows.Threading.DispatcherPriority.Background);
                        
                        // Load preview images for the newly downloaded package
                        if (_packageManager != null && _imageManager != null && _imageManager.PreviewImageIndex.Count > 0)
                        {
                            // The preview images were indexed during ParseVarMetadataComplete
                            // Now load them into the ImageManager
                            await Task.Run(() => _imageManager.LoadExternalImageIndex(_imageManager.PreviewImageIndex.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)));
                        }
                        
                        // Recalculate update count after successful download
                        await RecalculateUpdateCountAsync();
                    }
                    catch { }
                });
            }
            catch { }
        }
        
        /// <summary>
        /// Updates the database and waits for completion
        /// </summary>
        /// <returns>True if update was successful, false otherwise</returns>
        private async Task<bool> UpdateDatabaseAndWait()
        {
            try
            {
                // Check if package downloader is initialized
                if (_packageDownloader == null)
                {
                    return false;
                }

                // Show progress message
                SetStatus("Updating package database...");
                
                // Load package list (this will trigger network permission check if needed)
                bool success = await LoadPackageDownloadListAsync();
                
                if (!success)
                {
                    SetStatus("Database update failed");
                    return false;
                }
                
                // Check if packages were loaded
                int countAfter = _packageDownloader.GetPackageCount();
                if (countAfter > 0)
                {
                    SetStatus($"Database updated - {countAfter:N0} packages available");
                    return true;
                }
                else
                {
                    SetStatus("Database update failed - no packages loaded");
                    return false;
                }
            }
            catch
            {
                SetStatus("Database update failed");
                return false;
            }
        }

        /// <summary>
        /// Handles the Optimize Selected toolbar button click
        /// </summary>
        private void OptimizeSelectedToolbar_Click(object sender, RoutedEventArgs e)
        {
            // Check current content mode
            if (_currentContentMode == "Scenes")
            {
                OptimizeSelectedScenes_Click(sender, e);
            }
            else if (_currentContentMode == "Presets" || _currentContentMode == "Custom")
            {
                OptimizeSelectedPresets_Click(sender, e);
            }
            else
            {
                // Call the existing validate textures method for packages
                ValidateTextures_Click(sender, e);
            }
        }
        
        private async void DownloadMissingToolbar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if package downloader is initialized and database is loaded
                if (_packageDownloader == null || _packageDownloader.GetPackageCount() == 0)
                {
                    // Try to load offline database first if not already loaded
                    if (_packageDownloader != null && _packageDownloader.GetPackageCount() == 0)
                    {
                        await LoadPackageDownloadListAsync();
                    }
                    
                    // If still empty after offline load attempt, offer database update
                    if (_packageDownloader.GetPackageCount() == 0)
                    {
                        // Always grant network access and update database
                        bool updateDatabase = true;
                        
                        if (updateDatabase)
                        {
                            // Update database first
                            bool updateSuccess = await UpdateDatabaseAndWait();
                            
                            if (!updateSuccess || _packageDownloader.GetPackageCount() == 0)
                            {
                                CustomMessageBox.Show("Database update failed or no packages available. Please try again.",
                                    "Update Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                                return;
                            }
                        }
                        else if (_packageDownloader.GetPackageCount() == 0)
                        {
                            CustomMessageBox.Show("The package database is empty. Please update the database first.",
                                "Database Empty", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                }
                
                // Get all missing dependencies from the Dependencies table
                var missingDeps = Dependencies
                    .Where(d => d.Status == "Missing" || d.Status == "Unknown")
                    .Select(d => d.Name)
                    .ToList();
                
                if (missingDeps.Count == 0)
                {
                    CustomMessageBox.Show("No missing dependencies found in the current view.", 
                        "Download Missing", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Get the AddonPackages folder path
                string addonPackagesFolder = System.IO.Path.Combine(_selectedFolder, "AddonPackages");
                
                // Create or reuse the Package Downloads window
                if (_packageDownloadsWindow == null || !_packageDownloadsWindow.IsLoaded)
                {
                    _packageDownloadsWindow = new PackageSearchWindow(
                        _packageManager,
                        _packageDownloader,
                        _downloadQueueManager,
                        addonPackagesFolder,
                        LoadPackageDownloadListAsync,
                        OnPackageDownloadedFromSearchWindow)
                    {
                        Owner = this
                    };
                }

                // Show and bring to front
                if (!_packageDownloadsWindow.IsVisible)
                {
                    _packageDownloadsWindow.Show();
                }
                else
                {
                    // Restore from minimized state if needed
                    if (_packageDownloadsWindow.WindowState == WindowState.Minimized)
                    {
                        _packageDownloadsWindow.WindowState = WindowState.Normal;
                    }
                    _packageDownloadsWindow.Activate();
                }
                
                // Append missing dependencies and auto-trigger search
                _packageDownloadsWindow.AppendPackageNames(missingDeps, autoSearch: true);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error downloading packages: {ex.Message}", 
                    "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Handles the Package Downloads toolbar button click
        /// Opens the unified Package Downloads window for searching and downloading missing packages
        /// This replaces both the old "Download Missing" and "Package Search" functionality
        /// </summary>
        private async void PackageDownloadsToolbar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if a folder has been selected
                if (string.IsNullOrEmpty(_selectedFolder))
                {
                    CustomMessageBox.Show(
                        "Please select a VAM root folder first.\n\n" +
                        "Go to File -> Select Root Folder to choose your VAM installation directory.",
                        "No Folder Selected",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                // Ensure package downloader is initialized
                if (_packageDownloader == null)
                {
                    InitializePackageDownloader();
                }

                // Check if package downloader is initialized and database is loaded
                if (_packageDownloader == null || _packageDownloader.GetPackageCount() == 0)
                {
                    // Try to load offline database first if not already loaded
                    if (_packageDownloader != null && _packageDownloader.GetPackageCount() == 0)
                    {
                        await LoadPackageDownloadListAsync();
                    }
                    
                    // If still empty after offline load attempt, offer database update
                    if (_packageDownloader.GetPackageCount() == 0)
                    {
                        // Always grant network access and update database
                        bool updateDatabase = true;
                        
                        if (updateDatabase)
                        {
                            // Update database first
                            bool updateSuccess = await UpdateDatabaseAndWait();
                            
                            if (!updateSuccess || _packageDownloader.GetPackageCount() == 0)
                            {
                                CustomMessageBox.Show("Database update failed or no packages available. Please try again.",
                                    "Update Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                                return;
                            }
                        }
                        else if (_packageDownloader.GetPackageCount() == 0)
                        {
                            CustomMessageBox.Show("The package database is empty. Please update the database first.",
                                "Database Empty", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                }

                // Get the AddonPackages folder path
                string addonPackagesFolder = System.IO.Path.Combine(_selectedFolder, "AddonPackages");
                
                if (!System.IO.Directory.Exists(addonPackagesFolder))
                {
                    CustomMessageBox.Show(
                        $"AddonPackages folder not found at:\n{addonPackagesFolder}\n\n" +
                        "Please ensure you have selected the correct VAM root folder.",
                        "Folder Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Create or reuse the Package Downloads window
                if (_packageDownloadsWindow == null || !_packageDownloadsWindow.IsLoaded)
                {
                    _packageDownloadsWindow = new PackageSearchWindow(
                        _packageManager,
                        _packageDownloader,
                        _downloadQueueManager,
                        addonPackagesFolder,
                        LoadPackageDownloadListAsync,
                        OnPackageDownloadedFromSearchWindow)
                    {
                        Owner = this
                    };
                }

                // Show and bring to front
                if (!_packageDownloadsWindow.IsVisible)
                {
                    _packageDownloadsWindow.Show();
                }
                else
                {
                    // Restore from minimized state if needed
                    if (_packageDownloadsWindow.WindowState == WindowState.Minimized)
                    {
                        _packageDownloadsWindow.WindowState = WindowState.Normal;
                    }
                    _packageDownloadsWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error opening Package Downloads window: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Handles the Downloads toolbar button click
        /// Shows the download progress window
        /// </summary>
        private void ShowDownloadsToolbar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ShowDownloadWindow();
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error showing downloads window: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Opens the Play VAM dropdown menu
        /// </summary>
        private void PlayVAMButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null)
            {
                button.ContextMenu.IsOpen = true;
            }
        }
        
        /// <summary>
        /// Opens the VaM Hub Browser window
        /// </summary>
        private void VamHubButton_Click(object sender, RoutedEventArgs e)
        {
            HubBrowser_Click(sender, e);
        }
        
        /// <summary>
        /// Launches VirtAMate in Desktop mode
        /// </summary>
        private void LaunchDesktop_Click(object sender, RoutedEventArgs e)
        {
            LaunchVirtAMate("Desktop", "-vrmode None");
        }
        
        /// <summary>
        /// Launches VirtAMate in VR mode
        /// </summary>
        private void LaunchVR_Click(object sender, RoutedEventArgs e)
        {
            LaunchVirtAMate("VR", "-vrmode OpenVR");
        }
        
        /// <summary>
        /// Launches VirtAMate with screen selector (Config mode)
        /// </summary>
        private void LaunchConfig_Click(object sender, RoutedEventArgs e)
        {
            LaunchVirtAMate("Config", "-show-screen-selector");
        }
        
        /// <summary>
        /// Launches VirtAMate in a separate process with specified arguments
        /// </summary>
        private void LaunchVirtAMate(string modeName, string arguments)
        {
            try
            {
                if (string.IsNullOrEmpty(_selectedFolder))
                {
                    CustomMessageBox.Show("Please select a VAM root folder first.", 
                        "No Folder Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Look for VaM.exe in the selected folder
                string vamExePath = System.IO.Path.Combine(_selectedFolder, "VaM.exe");
                
                if (!System.IO.File.Exists(vamExePath))
                {
                    CustomMessageBox.Show($"VaM.exe not found in:\n{_selectedFolder}\n\nPlease ensure you've selected the correct VAM root folder.", 
                        "VaM Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                // Create process start info
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = vamExePath,
                    Arguments = arguments,
                    WorkingDirectory = _selectedFolder,
                    UseShellExecute = true, // Launch as separate process
                    CreateNoWindow = false
                };
                
                // Launch VaM
                System.Diagnostics.Process.Start(startInfo);
                
                SetStatus($"Launched VirtAMate in {modeName} mode");
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error launching VirtAMate:\n\n{ex.Message}", 
                    "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Handles the Refresh Packages button click in the filter panel
        /// Hold Shift for full refresh, otherwise uses incremental refresh
        /// </summary>
        private void RefreshPackagesButton_Click(object sender, RoutedEventArgs e)
        {
            // Hold Shift for full refresh, otherwise use incremental
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                SetStatus("Full refresh requested...");
                RefreshPackages();
            }
            else
            {
                RefreshPackagesIncremental();
            }
        }
        
        private void ScrollHereArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (FilterScrollViewer != null)
            {
                double scrollAmount = e.Delta > 0 ? -50 : 50;
                FilterScrollViewer.ScrollToVerticalOffset(FilterScrollViewer.VerticalOffset + scrollAmount);
                e.Handled = true;
            }
        }
        
        #region Scene Filter Event Handlers
        
        /// <summary>
        /// Handles scene type filter list selection changed
        /// </summary>
        private void SceneTypeFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SceneTypeFilterList == null || ScenesView == null)
                return;

            try
            {
                var selectedItems = SceneTypeFilterList.SelectedItems;
                
                if (selectedItems.Count == 0)
                {
                    // No selection - show all scenes
                    ScenesView.Filter = null;
                }
                else
                {
                    // Extract scene types from selected items (remove count suffix)
                    var selectedTypes = new HashSet<string>();
                    foreach (var item in selectedItems)
                    {
                        var text = item.ToString();
                        // Extract type name from "Type (count)" format
                        var typeMatch = System.Text.RegularExpressions.Regex.Match(text, @"^(.+?)\s+\(\d+\)$");
                        if (typeMatch.Success)
                        {
                            selectedTypes.Add(typeMatch.Groups[1].Value);
                        }
                    }
                    
                    // Apply filter
                    ScenesView.Filter = obj =>
                    {
                        if (obj is SceneItem scene)
                        {
                            return selectedTypes.Contains(scene.SceneType);
                        }
                        return true;
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error filtering by scene type: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles scene creator filter list selection changed
        /// </summary>
        private void SceneCreatorFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SceneCreatorFilterList == null || ScenesView == null)
                return;

            try
            {
                var selectedItems = SceneCreatorFilterList.SelectedItems;
                
                if (selectedItems.Count == 0)
                {
                    // No selection - show all scenes
                    ScenesView.Filter = null;
                }
                else
                {
                    // Extract creators from selected items (remove count suffix)
                    var selectedCreators = new HashSet<string>();
                    foreach (var item in selectedItems)
                    {
                        var text = item.ToString();
                        // Extract creator name from "Creator (count)" format
                        var creatorMatch = System.Text.RegularExpressions.Regex.Match(text, @"^(.+?)\s+\(\d+\)$");
                        if (creatorMatch.Success)
                        {
                            selectedCreators.Add(creatorMatch.Groups[1].Value);
                        }
                    }
                    
                    // Apply filter
                    ScenesView.Filter = obj =>
                    {
                        if (obj is SceneItem scene)
                        {
                            return selectedCreators.Contains(scene.Creator);
                        }
                        return true;
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error filtering by scene creator: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles scene source filter list selection changed
        /// </summary>
        private void SceneSourceFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SceneSourceFilterList == null || ScenesView == null)
                return;

            try
            {
                var selectedItems = SceneSourceFilterList.SelectedItems;
                
                if (selectedItems.Count == 0)
                {
                    // No selection - show all scenes
                    ScenesView.Filter = null;
                }
                else
                {
                    // Extract sources from selected items (remove count suffix and emoji)
                    var selectedSources = new HashSet<string>();
                    foreach (var item in selectedItems)
                    {
                        var text = item.ToString();
                        // Extract source from "✗ Local (count)" or "📦 VAR (count)" format
                        var sourceMatch = System.Text.RegularExpressions.Regex.Match(text, @"[🁰Ÿ“¦]\s+(\w+)\s+\(\d+\)");
                        if (sourceMatch.Success)
                        {
                            selectedSources.Add(sourceMatch.Groups[1].Value);
                        }
                    }
                    
                    // Apply filter
                    ScenesView.Filter = obj =>
                    {
                        if (obj is SceneItem scene)
                        {
                            return selectedSources.Contains(scene.Source);
                        }
                        return true;
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error filtering by scene source: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles scene type filter text box changes
        /// </summary>
        private void SceneTypeFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // TODO: Implement scene type filter search
            // For now, this is a placeholder
        }
        
        /// <summary>
        /// Handles scene creator filter text box changes
        /// </summary>
        private void SceneCreatorFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // TODO: Implement scene creator filter search
            // For now, this is a placeholder
        }
        
        /// <summary>
        /// Handles scene type sort button click
        /// </summary>
        private void SceneTypeSortButton_Click(object sender, RoutedEventArgs e)
        {
            if (SceneTypeFilterList == null)
                return;

            try
            {
                // Get current items
                var items = SceneTypeFilterList.Items.Cast<string>().ToList();
                
                // Toggle sort order (ascending/descending)
                // For simplicity, we'll just reverse the list
                items.Reverse();
                
                // Repopulate the list
                SceneTypeFilterList.Items.Clear();
                foreach (var item in items)
                {
                    SceneTypeFilterList.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error sorting scene type filter: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles scene creator sort button click
        /// </summary>
        private void SceneCreatorSortButton_Click(object sender, RoutedEventArgs e)
        {
            if (SceneCreatorFilterList == null)
                return;

            try
            {
                // Get current items
                var items = SceneCreatorFilterList.Items.Cast<string>().ToList();
                
                // Toggle sort order (ascending/descending)
                // For simplicity, we'll just reverse the list
                items.Reverse();
                
                // Repopulate the list
                SceneCreatorFilterList.Items.Clear();
                foreach (var item in items)
                {
                    SceneCreatorFilterList.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error sorting scene creator filter: {ex.Message}");
            }
        }
        
        #endregion
        
        #endregion
        
        #region Package Context Menu
        
        private void ShowDependencyGraph_Click(object sender, RoutedEventArgs e)
        {
            var selectedPackages = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList();
            if (selectedPackages == null || selectedPackages.Count == 0)
            {
                DarkMessageBox.Show("Please select a package to view its dependency graph.", "No Package Selected",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            // Use the first selected package
            var packageItem = selectedPackages[0];
            _packageManager.PackageMetadata.TryGetValue(packageItem.MetadataKey, out var metadata);
            
            if (metadata == null)
            {
                DarkMessageBox.Show("Could not load package metadata.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            var graphWindow = new Windows.DependencyGraphWindow(_packageManager, _packageFileManager, _imageManager, metadata)
            {
                Owner = this
            };
            graphWindow.Show();
        }
        
        private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            var selectedPackages = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList();
            if (selectedPackages == null || selectedPackages.Count == 0)
                return;
            
            var packageItem = selectedPackages[0];
            _packageManager.PackageMetadata.TryGetValue(packageItem.MetadataKey, out var metadata);
            
            if (metadata != null && !string.IsNullOrEmpty(metadata.FilePath) && File.Exists(metadata.FilePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{metadata.FilePath}\"");
            }
        }
        
        private void CopyPackageName_Click(object sender, RoutedEventArgs e)
        {
            var selectedPackages = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList();
            if (selectedPackages == null || selectedPackages.Count == 0)
                return;
            
            var names = selectedPackages.Select(p => p.DisplayName);
            var text = string.Join(Environment.NewLine, names);
            
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to copy to clipboard: {ex.Message}");
            }
        }
        
        private void OpenDiscardLocation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string gameRoot = _settingsManager?.Settings?.SelectedFolder;
                if (string.IsNullOrEmpty(gameRoot))
                {
                    DarkMessageBox.Show("No game folder selected.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                string discardedFolder = Path.Combine(gameRoot, "DiscardedPackages");
                
                // Create folder if it doesn't exist
                if (!Directory.Exists(discardedFolder))
                {
                    Directory.CreateDirectory(discardedFolder);
                }
                
                // Open the folder in explorer
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{discardedFolder}\"",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show($"Error opening discard location: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"Error opening discard location: {ex}");
            }
        }
        
        private void PackageContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Hide dependency graph and open in explorer when more than 1 package is selected
            // Keep discard location visible for all selections
            var selectedCount = PackageDataGrid?.SelectedItems?.Count ?? 0;
            
            if (sender is ContextMenu contextMenu)
            {
                // Get menu items from the context menu's items collection
                MenuItem showDependencyItem = null;
                MenuItem openInExplorerItem = null;
                
                foreach (var item in contextMenu.Items)
                {
                    if (item is MenuItem menuItem)
                    {
                        if (menuItem.Header?.ToString() == "📊 Show Dependency Graph")
                            showDependencyItem = menuItem;
                        else if (menuItem.Header?.ToString() == "📁 Open in Explorer")
                            openInExplorerItem = menuItem;
                    }
                }
                
                if (showDependencyItem != null)
                    showDependencyItem.Visibility = selectedCount == 1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                
                if (openInExplorerItem != null)
                    openInExplorerItem.Visibility = selectedCount == 1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
        }
        
        private void DependenciesContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Hide dependency graph and open in explorer when more than 1 dependency is selected
            // Keep discard location visible for all selections
            var selectedCount = DependenciesDataGrid?.SelectedItems?.Count ?? 0;
            
            if (sender is ContextMenu contextMenu)
            {
                // Get menu items from the context menu's items collection
                MenuItem showDependencyItem = null;
                MenuItem openInExplorerItem = null;
                
                foreach (var item in contextMenu.Items)
                {
                    if (item is MenuItem menuItem)
                    {
                        if (menuItem.Header?.ToString() == "📊 Show Dependency Graph")
                            showDependencyItem = menuItem;
                        else if (menuItem.Header?.ToString() == "📁 Open in Explorer")
                            openInExplorerItem = menuItem;
                    }
                }
                
                if (showDependencyItem != null)
                    showDependencyItem.Visibility = selectedCount == 1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                
                if (openInExplorerItem != null)
                    openInExplorerItem.Visibility = selectedCount == 1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
        }
        
        private void ShowDependencyGraphDeps_Click(object sender, RoutedEventArgs e)
        {
            var selectedDeps = DependenciesDataGrid?.SelectedItems?.Cast<DependencyItem>().ToList();
            if (selectedDeps == null || selectedDeps.Count == 0)
                return;
            
            // Only handle single selection
            if (selectedDeps.Count != 1)
            {
                DarkMessageBox.Show("Please select only one dependency to view its dependency graph.", "Multiple Selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            var depItem = selectedDeps[0];
            
            // Strip .latest if present
            string depName = depItem.Name;
            if (depName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
            {
                depName = depName.Substring(0, depName.Length - 7);
            }
            
            // Find the package metadata by searching for matching base name with version
            VarMetadata metadata = null;
            
            // Search for keys that start with depName followed by a dot (for version)
            var matchingKeys = _packageManager?.PackageMetadata?.Keys
                .Where(k => k.StartsWith(depName + ".", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? new List<string>();
            
            if (matchingKeys.Count > 0)
            {
                // Get the first matching key
                var key = matchingKeys.FirstOrDefault();
                if (key != null && _packageManager.PackageMetadata.TryGetValue(key, out metadata))
                {
                    // Found it
                }
            }
            
            if (metadata != null)
            {
                var graphWindow = new Windows.DependencyGraphWindow(_packageManager, _packageFileManager, _imageManager, metadata)
                {
                    Owner = this
                };
                graphWindow.Show();
            }
        }
        
        private void OpenInExplorerDeps_Click(object sender, RoutedEventArgs e)
        {
            var selectedDeps = DependenciesDataGrid?.SelectedItems?.Cast<DependencyItem>().ToList();
            if (selectedDeps == null || selectedDeps.Count == 0)
                return;
            
            // Only handle single selection
            if (selectedDeps.Count != 1)
            {
                DarkMessageBox.Show("Please select only one dependency to open in Explorer.", "Multiple Selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            var depItem = selectedDeps[0];
            
            // Strip .latest if present
            string depName = depItem.Name;
            if (depName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
            {
                depName = depName.Substring(0, depName.Length - 7);
            }
            
            // Find the package metadata by searching for matching base name with version
            VarMetadata metadata = null;
            
            // Search for keys that start with depName followed by a dot (for version)
            var matchingKeys = _packageManager?.PackageMetadata?.Keys
                .Where(k => k.StartsWith(depName + ".", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? new List<string>();
            
            if (matchingKeys.Count > 0)
            {
                // Get the first matching key
                var key = matchingKeys.FirstOrDefault();
                if (key != null && _packageManager.PackageMetadata.TryGetValue(key, out metadata))
                {
                    // Found it
                }
            }
            
            if (metadata != null && !string.IsNullOrEmpty(metadata.FilePath))
            {
                string folderPath = Path.GetDirectoryName(metadata.FilePath);
                if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select, \"{metadata.FilePath}\"");
                }
            }
        }
        
        private void CopyPackageNameDeps_Click(object sender, RoutedEventArgs e)
        {
            var selectedDeps = DependenciesDataGrid?.SelectedItems?.Cast<DependencyItem>().ToList();
            if (selectedDeps == null || selectedDeps.Count == 0)
                return;
            
            var names = new List<string>();
            
            foreach (var depItem in selectedDeps)
            {
                // Strip .latest if present
                string depName = depItem.Name;
                if (depName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                {
                    depName = depName.Substring(0, depName.Length - 7);
                }
                
                // Find the package metadata to get the version
                VarMetadata metadata = null;
                
                // Search for keys that start with depName followed by a dot (for version)
                var matchingKeys = _packageManager?.PackageMetadata?.Keys
                    .Where(k => k.StartsWith(depName + ".", StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? new List<string>();
                
                if (matchingKeys.Count > 0)
                {
                    // Get the first matching key
                    var key = matchingKeys.FirstOrDefault();
                    if (key != null && _packageManager.PackageMetadata.TryGetValue(key, out metadata))
                    {
                        // Found it - use the metadata to get the version
                        if (metadata != null && metadata.Version > 0)
                        {
                            names.Add($"{depName}.{metadata.Version}");
                        }
                        else
                        {
                            // Fallback if version is not available
                            names.Add(depName);
                        }
                    }
                    else
                    {
                        // Couldn't get metadata, use base name
                        names.Add(depName);
                    }
                }
                else
                {
                    // No matching metadata found, use base name
                    names.Add(depName);
                }
            }
            
            var text = string.Join(Environment.NewLine, names);
            
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to copy to clipboard: {ex.Message}");
            }
        }
        
        private async void DiscardSelectedScenes_Click(object sender, RoutedEventArgs e)
        {
            var selectedScenes = ScenesDataGrid?.SelectedItems?.Cast<SceneItem>().ToList();
            if (selectedScenes == null || selectedScenes.Count == 0)
                return;
            
            try
            {
                // Create DiscardedPackages folder in game root
                string gameRoot = _settingsManager?.Settings?.SelectedFolder;
                if (string.IsNullOrEmpty(gameRoot))
                {
                    DarkMessageBox.Show("No game folder selected.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                string discardedFolder = Path.Combine(gameRoot, "DiscardedPackages");
                Directory.CreateDirectory(discardedFolder);
                
                int successCount = 0;
                int failureCount = 0;
                var failedScenes = new List<string>();
                
                foreach (var sceneItem in selectedScenes)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(sceneItem.FilePath) && File.Exists(sceneItem.FilePath))
                        {
                            string fileName = Path.GetFileName(sceneItem.FilePath);
                            string destinationPath = Path.Combine(discardedFolder, fileName);
                            
                            // Handle file name conflicts by appending a number
                            int counter = 1;
                            string baseFileName = Path.GetFileNameWithoutExtension(fileName);
                            string extension = Path.GetExtension(fileName);
                            while (File.Exists(destinationPath))
                            {
                                destinationPath = Path.Combine(discardedFolder, $"{baseFileName}_{counter}{extension}");
                                counter++;
                            }
                            
                            // Release file handles before moving
                            try
                            {
                                if (_imageManager != null)
                                    await _imageManager.CloseFileHandlesAsync(sceneItem.FilePath);
                                await Task.Delay(100); // Brief delay to ensure handles are released
                            }
                            catch (Exception)
                            {
                                // Continue even if handle release fails
                            }
                            
                            // Move the file with retry logic for file lock issues
                            int moveRetries = 3;
                            bool fileMoved = false;
                            while (moveRetries > 0 && !fileMoved)
                            {
                                try
                                {
                                    File.Move(sceneItem.FilePath, destinationPath, overwrite: false);
                                    fileMoved = true;
                                    successCount++;
                                }
                                catch (IOException) when (moveRetries > 1)
                                {
                                    moveRetries--;
                                    System.Diagnostics.Debug.WriteLine($"File lock still active for scene {sceneItem.DisplayName}, retrying... ({moveRetries} retries left)");
                                    await Task.Delay(300); // Wait longer before retry
                                }
                            }
                            
                            if (!fileMoved)
                            {
                                failureCount++;
                                failedScenes.Add(sceneItem.DisplayName);
                            }
                        }
                        else
                        {
                            failureCount++;
                            failedScenes.Add(sceneItem.DisplayName);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error discarding scene {sceneItem.DisplayName}: {ex.Message}");
                        failureCount++;
                        failedScenes.Add(sceneItem.DisplayName);
                    }
                }
                
                // Show error message only if there were failures
                if (failureCount > 0)
                {
                    DarkMessageBox.Show($"Failed to discard {failureCount} scene(s):\n\n{string.Join("\n", failedScenes)}",
                        "Discard Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                
                // Remove successfully discarded scenes from the UI
                if (successCount > 0)
                {
                    var scenesToRemove = selectedScenes.Where(s => 
                        string.IsNullOrEmpty(s.FilePath) || !File.Exists(s.FilePath)
                    ).ToList();
                    
                    foreach (var scene in scenesToRemove)
                    {
                        Scenes.Remove(scene);
                    }
                }
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show($"Error during discard operation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"Discard operation error: {ex}");
            }
        }
        
        private async void DiscardSelectedCustomAtoms_Click(object sender, RoutedEventArgs e)
        {
            var selectedCustomAtoms = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>().ToList();
            if (selectedCustomAtoms == null || selectedCustomAtoms.Count == 0)
                return;
            
            try
            {
                // Create DiscardedPackages folder in game root
                string gameRoot = _settingsManager?.Settings?.SelectedFolder;
                if (string.IsNullOrEmpty(gameRoot))
                {
                    DarkMessageBox.Show("No game folder selected.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                string discardedFolder = Path.Combine(gameRoot, "DiscardedPackages");
                Directory.CreateDirectory(discardedFolder);
                
                int successCount = 0;
                int failureCount = 0;
                var failedCustomAtoms = new List<string>();
                
                foreach (var customAtomItem in selectedCustomAtoms)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(customAtomItem.FilePath) && File.Exists(customAtomItem.FilePath))
                        {
                            string fileName = Path.GetFileName(customAtomItem.FilePath);
                            string destinationPath = Path.Combine(discardedFolder, fileName);
                            
                            // Handle file name conflicts by appending a number
                            int counter = 1;
                            string baseFileName = Path.GetFileNameWithoutExtension(fileName);
                            string extension = Path.GetExtension(fileName);
                            while (File.Exists(destinationPath))
                            {
                                destinationPath = Path.Combine(discardedFolder, $"{baseFileName}_{counter}{extension}");
                                counter++;
                            }
                            
                            // Release file handles before moving
                            try
                            {
                                if (_imageManager != null)
                                    await _imageManager.CloseFileHandlesAsync(customAtomItem.FilePath);
                                await Task.Delay(100); // Brief delay to ensure handles are released
                            }
                            catch (Exception)
                            {
                                // Continue even if handle release fails
                            }
                            
                            // Move the file with retry logic for file lock issues
                            int moveRetries = 3;
                            bool fileMoved = false;
                            while (moveRetries > 0 && !fileMoved)
                            {
                                try
                                {
                                    File.Move(customAtomItem.FilePath, destinationPath, overwrite: false);
                                    fileMoved = true;
                                    successCount++;
                                }
                                catch (IOException) when (moveRetries > 1)
                                {
                                    moveRetries--;
                                    System.Diagnostics.Debug.WriteLine($"File lock still active for custom atom {customAtomItem.DisplayName}, retrying... ({moveRetries} retries left)");
                                    await Task.Delay(300); // Wait longer before retry
                                }
                            }
                            
                            if (!fileMoved)
                            {
                                failureCount++;
                                failedCustomAtoms.Add(customAtomItem.DisplayName);
                            }
                        }
                        else
                        {
                            failureCount++;
                            failedCustomAtoms.Add(customAtomItem.DisplayName);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error discarding custom atom {customAtomItem.DisplayName}: {ex.Message}");
                        failureCount++;
                        failedCustomAtoms.Add(customAtomItem.DisplayName);
                    }
                }
                
                // Remove successfully discarded custom atoms from the UI
                if (successCount > 0)
                {
                    var customAtomsToRemove = selectedCustomAtoms.Where(c => 
                        string.IsNullOrEmpty(c.FilePath) || !File.Exists(c.FilePath)
                    ).ToList();
                    
                    foreach (var customAtom in customAtomsToRemove)
                    {
                        CustomAtomItems.Remove(customAtom);
                    }
                }
                
                // Show error message only if there were failures
                if (failureCount > 0)
                {
                    DarkMessageBox.Show($"Failed to discard {failureCount} custom atom(s):\n\n{string.Join("\n", failedCustomAtoms)}",
                        "Discard Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show($"Error during discard operation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"Discard operation error: {ex}");
            }
        }
        
        private async void DiscardSelectedDependencies_Click(object sender, RoutedEventArgs e)
        {
            var selectedDependencies = DependenciesDataGrid?.SelectedItems?.Cast<DependencyItem>().ToList();
            if (selectedDependencies == null || selectedDependencies.Count == 0)
            {
                DarkMessageBox.Show("No dependencies selected.", "No Selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            try
            {
                // Create DiscardedPackages folder in game root
                string gameRoot = _settingsManager?.Settings?.SelectedFolder;
                if (string.IsNullOrEmpty(gameRoot))
                {
                    DarkMessageBox.Show("No game folder selected.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                string discardedFolder = Path.Combine(gameRoot, "DiscardedPackages");
                Directory.CreateDirectory(discardedFolder);
                
                int successCount = 0;
                int failureCount = 0;
                var failedDependencies = new List<string>();
                var processedDependencies = new HashSet<DependencyItem>();
                
                foreach (var depItem in selectedDependencies)
                {
                    try
                    {
                        // Strip .latest if present
                        string depName = depItem.Name;
                        if (depName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                        {
                            depName = depName.Substring(0, depName.Length - 7);
                        }
                        
                        // Find the package metadata by searching for matching base name with version
                        // Keys are in format: "creator.package.version" or "creator.package.version#available" or "creator.package.version#archived"
                        VarMetadata metadata = null;
                        
                        // Search for keys that start with depName followed by a dot (for version)
                        var matchingKeys = _packageManager?.PackageMetadata?.Keys
                            .Where(k => k.StartsWith(depName + ".", StringComparison.OrdinalIgnoreCase))
                            .ToList() ?? new List<string>();
                        
                        if (matchingKeys.Count > 0)
                        {
                            // Get the first matching key (they should all be the same package, just different versions/variants)
                            var key = matchingKeys.FirstOrDefault();
                            if (key != null && _packageManager.PackageMetadata.TryGetValue(key, out metadata))
                            {
                                // Found it
                            }
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"Discard dependency: depName={depName}, found metadata={metadata != null}, filePath={metadata?.FilePath}, exists={metadata != null && File.Exists(metadata.FilePath)}");
                        
                        if (metadata != null && !string.IsNullOrEmpty(metadata.FilePath) && File.Exists(metadata.FilePath))
                        {
                            string fileName = Path.GetFileName(metadata.FilePath);
                            string destinationPath = Path.Combine(discardedFolder, fileName);
                            
                            // Handle file name conflicts by appending a number
                            int counter = 1;
                            string baseFileName = Path.GetFileNameWithoutExtension(fileName);
                            string extension = Path.GetExtension(fileName);
                            while (File.Exists(destinationPath))
                            {
                                destinationPath = Path.Combine(discardedFolder, $"{baseFileName}_{counter}{extension}");
                                counter++;
                            }
                            
                            // Release file handles and cancel preview loading before moving
                            try
                            {
                                if (_imageManager != null)
                                {
                                    // Cancel all pending preview operations and release file locks for this file
                                    await _imageManager.CloseFileHandlesAsync(metadata.FilePath);
                                    await Task.Delay(100); // Brief delay to ensure handles are fully released
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error releasing file locks: {ex.Message}");
                                // Continue even if handle release fails
                            }
                            
                            // Move the file with retry logic for file lock issues
                            int moveRetries = 3;
                            bool fileMoved = false;
                            while (moveRetries > 0 && !fileMoved)
                            {
                                try
                                {
                                    File.Move(metadata.FilePath, destinationPath, overwrite: false);
                                    fileMoved = true;
                                    System.Diagnostics.Debug.WriteLine($"Successfully discarded: {depItem.Name}");
                                    successCount++;
                                    processedDependencies.Add(depItem);
                                }
                                catch (IOException) when (moveRetries > 1)
                                {
                                    moveRetries--;
                                    System.Diagnostics.Debug.WriteLine($"File lock still active for {depItem.Name}, retrying... ({moveRetries} retries left)");
                                    await Task.Delay(300); // Wait longer before retry
                                }
                            }
                            
                            if (!fileMoved)
                            {
                                failureCount++;
                                failedDependencies.Add(depItem.Name);
                            }
                        }
                        else if (metadata == null)
                        {
                            // Ignore missing dependencies - they're not installed
                            System.Diagnostics.Debug.WriteLine($"Dependency not found (missing), will be removed from UI: {depItem.Name}");
                            processedDependencies.Add(depItem);
                        }
                        else if (string.IsNullOrEmpty(metadata.FilePath))
                        {
                            // Metadata exists but no file path
                            System.Diagnostics.Debug.WriteLine($"Metadata found but no file path: {depItem.Name}");
                            processedDependencies.Add(depItem);
                        }
                        else
                        {
                            // File doesn't exist or metadata is invalid
                            System.Diagnostics.Debug.WriteLine($"File not found on disk: {depItem.Name}, path={metadata.FilePath}");
                            failureCount++;
                            failedDependencies.Add(depItem.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error discarding dependency {depItem.Name}: {ex.Message}");
                        failureCount++;
                        failedDependencies.Add(depItem.Name);
                    }
                }
                
                // Remove processed dependencies from the UI (both successfully discarded and missing ones)
                if (processedDependencies.Count > 0)
                {
                    foreach (var dep in processedDependencies)
                    {
                        Dependencies.Remove(dep);
                    }
                }
                
                // Show feedback based on results
                if (successCount == 0 && failureCount == 0)
                {
                    // All selected dependencies were missing (not installed)
                    DarkMessageBox.Show("All selected dependencies are missing (not installed). Nothing to discard.",
                        "No Packages Found", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (failureCount > 0)
                {
                    DarkMessageBox.Show($"Failed to discard {failureCount} package(s):\n\n{string.Join("\n", failedDependencies)}",
                        "Discard Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show($"Error during discard operation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"Discard operation error: {ex}");
            }
        }
        
        private async void DiscardSelected_Click(object sender, RoutedEventArgs e)
        {
            var selectedPackages = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList();
            if (selectedPackages == null || selectedPackages.Count == 0)
                return;
            
            try
            {
                // Create DiscardedPackages folder in game root
                string gameRoot = _settingsManager?.Settings?.SelectedFolder;
                if (string.IsNullOrEmpty(gameRoot))
                {
                    DarkMessageBox.Show("No game folder selected.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                string discardedFolder = Path.Combine(gameRoot, "DiscardedPackages");
                Directory.CreateDirectory(discardedFolder);
                
                int successCount = 0;
                int failureCount = 0;
                var failedPackages = new List<string>();
                
                foreach (var packageItem in selectedPackages)
                {
                    try
                    {
                        // Get package metadata to find the file path
                        if (_packageManager?.PackageMetadata?.TryGetValue(packageItem.MetadataKey, out var metadata) == true)
                        {
                            if (metadata != null && !string.IsNullOrEmpty(metadata.FilePath) && File.Exists(metadata.FilePath))
                            {
                                string fileName = Path.GetFileName(metadata.FilePath);
                                string destinationPath = Path.Combine(discardedFolder, fileName);
                                
                                // Handle file name conflicts by appending a number
                                int counter = 1;
                                string baseFileName = Path.GetFileNameWithoutExtension(fileName);
                                string extension = Path.GetExtension(fileName);
                                while (File.Exists(destinationPath))
                                {
                                    destinationPath = Path.Combine(discardedFolder, $"{baseFileName}_{counter}{extension}");
                                    counter++;
                                }
                                
                                // Release file handles before moving
                                try
                                {
                                    if (_imageManager != null)
                                        await _imageManager.CloseFileHandlesAsync(metadata.FilePath);
                                    await Task.Delay(100); // Brief delay to ensure handles are released
                                }
                                catch (Exception)
                                {
                                    // Continue even if handle release fails
                                }
                                
                                // Move the file with retry logic for file lock issues
                                int moveRetries = 3;
                                bool fileMoved = false;
                                while (moveRetries > 0 && !fileMoved)
                                {
                                    try
                                    {
                                        File.Move(metadata.FilePath, destinationPath, overwrite: false);
                                        fileMoved = true;
                                        successCount++;
                                    }
                                    catch (IOException) when (moveRetries > 1)
                                    {
                                        moveRetries--;
                                        System.Diagnostics.Debug.WriteLine($"File lock still active for package {packageItem.DisplayName}, retrying... ({moveRetries} retries left)");
                                        await Task.Delay(300); // Wait longer before retry
                                    }
                                }
                                
                                if (!fileMoved)
                                {
                                    failureCount++;
                                    failedPackages.Add(packageItem.DisplayName);
                                }
                            }
                            else
                            {
                                failureCount++;
                                failedPackages.Add(packageItem.DisplayName);
                            }
                        }
                        else
                        {
                            failureCount++;
                            failedPackages.Add(packageItem.DisplayName);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error discarding package {packageItem.DisplayName}: {ex.Message}");
                        failureCount++;
                        failedPackages.Add(packageItem.DisplayName);
                    }
                }
                
                // Remove successfully discarded packages from the UI
                if (successCount > 0)
                {
                    var packagesToRemove = selectedPackages.Where(p => 
                    {
                        if (_packageManager?.PackageMetadata?.TryGetValue(p.MetadataKey, out var metadata) == true)
                        {
                            return metadata != null && !string.IsNullOrEmpty(metadata.FilePath) && !File.Exists(metadata.FilePath);
                        }
                        return false;
                    }).ToList();
                    
                    foreach (var package in packagesToRemove)
                    {
                        Packages.Remove(package);
                    }
                }
                
                // Show error message only if there were failures
                if (failureCount > 0)
                {
                    DarkMessageBox.Show($"Failed to discard {failureCount} package(s):\n\n{string.Join("\n", failedPackages)}",
                        "Discard Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show($"Error during discard operation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"Discard operation error: {ex}");
            }
        }
        
        #endregion
    }
}

