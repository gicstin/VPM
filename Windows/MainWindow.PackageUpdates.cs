using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// Package update checking functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        #region Fields
        
        private PackageUpdateChecker _updateChecker;
        private List<string> _availableUpdatePackages;
        private List<PackageUpdateInfo> _cachedUpdateInfo;  // Cache the full update info
        private int _updateCount = 0;
        
        #endregion
        
        #region Initialization
        
        /// <summary>
        /// Initializes the update checker service
        /// Should be called after package downloader is initialized
        /// </summary>
        private void InitializeUpdateChecker()
        {
            try
            {
                if (_packageDownloader == null)
                {
                    Console.WriteLine("[UpdateChecker] Cannot initialize: Package downloader not ready");
                    return;
                }
                
                _updateChecker = new PackageUpdateChecker(_packageDownloader);
                _availableUpdatePackages = new List<string>();
                
                Console.WriteLine("[UpdateChecker] Initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UpdateChecker] Initialization error: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Update Checking
        
        /// <summary>
        /// Checks for package updates in the background
        /// This is called automatically after packages are loaded
        /// </summary>
        public async Task CheckForPackageUpdatesAsync()
        {
            try
            {
                // Ensure update checker is initialized
                if (_updateChecker == null)
                {
                    InitializeUpdateChecker();
                }
                
                if (_updateChecker == null || _packageDownloader == null)
                {
                    Console.WriteLine("[UpdateChecker] Cannot check updates: Services not initialized");
                    return;
                }
                
                // Ensure online database is loaded
                int packageCount = _packageDownloader.GetPackageCount();
                if (packageCount == 0)
                {
                    Console.WriteLine("[UpdateChecker] Online database not loaded, attempting to load...");
                    bool loaded = await LoadPackageDownloadListAsync();
                    
                    if (!loaded)
                    {
                        Console.WriteLine("[UpdateChecker] Failed to load online database");
                        return;
                    }
                }
                
                Console.WriteLine("[UpdateChecker] Starting update check...");
                SetStatus("Checking for package updates...");
                
                // Use the already-loaded package list from the main table (in-memory, very fast!)
                // This includes both Loaded and Available packages
                var allPackages = Packages.ToList();
                
                Console.WriteLine($"[UpdateChecker] Checking {allPackages.Count} packages (in-memory)");
                Console.WriteLine($"[UpdateChecker] Online database has {_packageDownloader.GetPackageCount()} packages");
                
                if (allPackages.Count == 0)
                {
                    Console.WriteLine("[UpdateChecker] No packages to check");
                    SetStatus("No packages to check for updates");
                    return;
                }
                
                // Check for updates (all in-memory, no file I/O)
                var updates = await _updateChecker.CheckForUpdatesAsync(allPackages);
                
                // Cache the results
                _cachedUpdateInfo = updates;
                _updateCount = updates.Count;
                _availableUpdatePackages = updates.Select(u => u.PackageName).ToList();
                
                // Log some examples
                if (_updateCount > 0)
                {
                    Console.WriteLine($"[UpdateChecker] Update examples:");
                    foreach (var update in updates.Take(5))
                    {
                        Console.WriteLine($"  - {update.BaseName}: v{update.LocalVersion} †’ v{update.OnlineVersion}");
                    }
                }
                
                Console.WriteLine($"[UpdateChecker] Found {_updateCount} package updates");
                
                // Update UI
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateCheckUpdatesButton();
                });
                
                if (_updateCount > 0)
                {
                    SetStatus($"Found {_updateCount} package update(s) available");
                }
                else
                {
                    SetStatus("All packages are up to date");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UpdateChecker] Error checking for updates: {ex.Message}");
                SetStatus("Error checking for updates");
            }
        }
        
        /// <summary>
        /// Updates the Check Updates button text based on results
        /// </summary>
        private void UpdateCheckUpdatesButton()
        {
            try
            {
                if (_updateCount > 0)
                {
                    CheckUpdatesToolbarButton.Header = $"✓ Updates Available ({_updateCount})";
                    CheckUpdatesToolbarButton.ToolTip = $"Click to view and download {_updateCount} package update(s)";
                }
                else
                {
                    CheckUpdatesToolbarButton.Header = "✓ No Updates";
                    CheckUpdatesToolbarButton.ToolTip = "All packages are up to date";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UpdateChecker] Error updating button: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Recalculates the update count after a package has been downloaded
        /// This removes downloaded packages from the cached update list
        /// </summary>
        private async Task RecalculateUpdateCountAsync()
        {
            try
            {
                if (_cachedUpdateInfo == null || _cachedUpdateInfo.Count == 0)
                {
                    return;
                }
                
                Console.WriteLine("[UpdateChecker] Recalculating update count after download...");
                
                // Get current package list
                var currentPackages = Packages.ToList();
                
                // Filter out updates for packages that are now loaded
                var remainingUpdates = _cachedUpdateInfo.Where(update =>
                {
                    // Check if this package is now loaded (downloaded)
                    var pkg = currentPackages.FirstOrDefault(p => 
                        p.Name.StartsWith(update.BaseName + ".", StringComparison.OrdinalIgnoreCase) &&
                        p.Status == "Loaded");
                    
                    if (pkg != null)
                    {
                        // Extract version from the loaded package
                        var loadedVersion = _updateChecker.ExtractVersion(pkg.Name);
                        
                        // If the loaded version is >= online version, this update is no longer needed
                        if (loadedVersion >= update.OnlineVersion)
                        {
                            Console.WriteLine($"[UpdateChecker] Removed update: {update.BaseName} (now at v{loadedVersion})");
                            return false;
                        }
                    }
                    
                    return true;
                }).ToList();
                
                // Update cached data
                _cachedUpdateInfo = remainingUpdates;
                _updateCount = remainingUpdates.Count;
                _availableUpdatePackages = remainingUpdates.Select(u => u.PackageName).ToList();
                
                Console.WriteLine($"[UpdateChecker] Update count recalculated: {_updateCount} remaining");
                
                // Update UI
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateCheckUpdatesButton();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UpdateChecker] Error recalculating update count: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Event Handlers
        
        /// <summary>
        /// Handles the Check Updates toolbar button click
        /// First click: Checks for updates, changes button text, and opens window if updates found
        /// Subsequent clicks: Opens Package Downloads window if updates available
        /// </summary>
        private async void CheckUpdatesToolbar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Track if this is the first click (checking for updates)
                bool isFirstClick = CheckUpdatesToolbarButton.Header.ToString().Contains("Check for Updates");
                
                // If button shows "Check for Updates", run the check
                if (isFirstClick)
                {
                    Console.WriteLine("[UpdateChecker] Manual update check triggered");
                    SetStatus("Checking for updates...");
                    await CheckForPackageUpdatesAsync();
                    
                    // If no updates found, return early
                    if (_availableUpdatePackages == null || _availableUpdatePackages.Count == 0)
                    {
                        return;
                    }
                    // Continue to open the window below
                }
                
                // Otherwise, button shows results - open downloads window if updates available
                if (_availableUpdatePackages == null || _availableUpdatePackages.Count == 0)
                {
                    CustomMessageBox.Show("No updates available.",
                        "No Updates", MessageBoxButton.OK, MessageBoxImage.Information);
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

                // Ensure network permission service is initialized
                if (_networkPermissionService == null)
                {
                    _networkPermissionService = new NetworkPermissionService(_settingsManager);
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
                        _networkPermissionService,
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
                
                // Simply remove version numbers from package names
                // This forces the search to find .latest (highest available version)
                var packageBaseNames = _availableUpdatePackages
                    .Select(packageName => {
                        // Find last dot followed by a number and remove it
                        for (int i = packageName.Length - 1; i >= 0; i--)
                        {
                            if (packageName[i] == '.')
                            {
                                var afterDot = packageName.Substring(i + 1);
                                if (int.TryParse(afterDot, out _))
                                {
                                    // This is a version number, remove it
                                    return packageName.Substring(0, i);
                                }
                                break; // Not a version number, keep the full name
                            }
                        }
                        return packageName; // No version found, keep as is
                    })
                    .Distinct()
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                
                // Append base names and auto-trigger search
                _packageDownloadsWindow.AppendPackageNames(packageBaseNames, autoSearch: true);
                
                Console.WriteLine($"[UpdateChecker] Opened Package Downloads window with {_availableUpdatePackages.Count} updates");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UpdatesAvailableToolbar_Click] Error: {ex.Message}");
                CustomMessageBox.Show($"Error opening downloads window: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        #endregion
    }
}

