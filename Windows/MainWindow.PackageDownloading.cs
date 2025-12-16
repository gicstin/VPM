using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// Package downloading functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        #region Fields
        
        private PackageDownloader _packageDownloader;
        private DownloadQueueManager _downloadQueueManager;
        private CancellationTokenSource _downloadCancellationTokenSource;
        private Windows.DownloadProgressWindow _currentProgressWindow;
        private PackageSearchWindow _packageDownloadsWindow;
        
        #endregion
        
        #region Initialization
        
        /// <summary>
        /// Initializes the package downloader service
        /// Should be called after folder selection
        /// </summary>
        private void InitializePackageDownloader()
        {
            try
            {
                if (string.IsNullOrEmpty(_selectedFolder))
                {
                    return;
                }
                
                var addonPackagesFolder = System.IO.Path.Combine(_selectedFolder, "AddonPackages");
                
                // Dispose existing downloader if any
                _packageDownloader?.Dispose();
                
                // Create new downloader
                _packageDownloader = new PackageDownloader(addonPackagesFolder);
                
                // Set network permission check callback (always grant access)
                _packageDownloader.SetNetworkPermissionCheck(async () =>
                {
                    return await Task.FromResult(true);
                });
                
                // Subscribe to events
                _packageDownloader.DownloadProgress += OnPackageDownloadProgress;
                _packageDownloader.DownloadCompleted += OnPackageDownloadCompleted;
                _packageDownloader.DownloadError += OnPackageDownloadError;
                
                // Initialize download queue manager with 2 concurrent downloads
                _downloadQueueManager?.Dispose();
                _downloadQueueManager = new DownloadQueueManager(_packageDownloader, addonPackagesFolder, maxConcurrentDownloads: 2);
                _downloadQueueManager.QueueStatusChanged += OnQueueStatusChanged;
                _downloadQueueManager.DownloadQueued += OnDownloadQueued;
                _downloadQueueManager.DownloadStarted += OnDownloadStartedInQueue;
                _downloadQueueManager.DownloadRemoved += OnDownloadRemovedFromQueue;
                
                // Initialize update checker now that downloader is ready
                InitializeUpdateChecker();
            }
            catch (Exception)
            {
            }
        }
        
        /// <summary>
        /// Loads the package download list from local links.txt file
        /// This method is now a no-op as downloads use Hub and local links.txt
        /// </summary>
        /// <returns>True if package downloader is initialized</returns>
        public Task<bool> LoadPackageDownloadListAsync()
        {
            // Downloads now use Hub service and local links.txt file
            // No encrypted database loading needed
            return Task.FromResult(_packageDownloader != null);
        }
        
        
        #endregion
        
        #region Download Operations
        
        /// <summary>
        /// Downloads a single missing package
        /// </summary>
        /// <param name="packageName">Name of the package to download</param>
        public async Task<bool> DownloadMissingPackageAsync(string packageName)
        {
            if (_packageDownloader == null)
            {
                CustomMessageBox.Show("Package downloader is not initialized. Please select a VAM folder first.",
                    "Download Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            
            if (string.IsNullOrWhiteSpace(packageName))
                return false;
            
            try
            {
                // Check if package is available for download
                if (!_packageDownloader.IsPackageAvailable(packageName))
                {
                    CustomMessageBox.Show($"Package '{packageName}' is not available in the download list.",
                        "Package Not Available", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }
                
                // Create cancellation token
                _downloadCancellationTokenSource?.Cancel();
                _downloadCancellationTokenSource = new CancellationTokenSource();
                
                // Download the package
                var success = await _packageDownloader.DownloadPackageAsync(packageName, _downloadCancellationTokenSource.Token);
                
                if (success)
                {
                    // Refresh package list to show the newly downloaded package
                    await RefreshPackagesAfterDownloadAsync();
                }
                
                return success;
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error downloading package: {ex.Message}",
                    "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        
        /// <summary>
        /// Downloads multiple missing packages
        /// </summary>
        /// <param name="packageNames">List of package names to download</param>
        public async Task<Dictionary<string, bool>> DownloadMissingPackagesAsync(IEnumerable<string> packageNames)
        {
            if (_packageDownloader == null)
            {
                CustomMessageBox.Show("Package downloader is not initialized. Please select a VAM folder first.",
                    "Download Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return new Dictionary<string, bool>();
            }
            
            var packageList = packageNames?.ToList() ?? new List<string>();
            if (packageList.Count == 0)
                return new Dictionary<string, bool>();
            
            try
            {
                // Ensure package list is loaded
                int packageCount = _packageDownloader.GetPackageCount();
                
                if (packageCount == 0)
                {
                    var loadResult = await LoadPackageDownloadListAsync();
                    
                    if (!loadResult)
                    {
                        CustomMessageBox.Show("Failed to load package download list. Please try updating the online database first.",
                            "Download Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return new Dictionary<string, bool>();
                    }
                }
                
                // Filter to only available packages
                var availablePackages = packageList.Where(p => _packageDownloader.IsPackageAvailable(p)).ToList();
                
                if (availablePackages.Count == 0)
                {
                    CustomMessageBox.Show("None of the selected packages are available in the download list.",
                        "No Packages Available", MessageBoxButton.OK, MessageBoxImage.Information);
                    return new Dictionary<string, bool>();
                }
                
                var unavailableCount = packageList.Count - availablePackages.Count;
                if (unavailableCount > 0)
                {
                    var result = CustomMessageBox.Show(
                        $"{availablePackages.Count} package(s) are available for download.\n" +
                        $"{unavailableCount} package(s) are not available.\n\n" +
                        "Do you want to download the available packages?",
                        "Download Confirmation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (result != MessageBoxResult.Yes)
                        return new Dictionary<string, bool>();
                }
                else
                {
                    var result = CustomMessageBox.Show(
                        $"Download {availablePackages.Count} package(s)?",
                        "Download Confirmation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (result != MessageBoxResult.Yes)
                        return new Dictionary<string, bool>();
                }
                
                // Create or reuse download progress window
                if (_currentProgressWindow == null || !_currentProgressWindow.IsLoaded)
                {
                    _currentProgressWindow = new Windows.DownloadProgressWindow
                    {
                        Owner = this
                    };
                    
                    // Subscribe to download count changes
                    _currentProgressWindow.DownloadCountChanged += (s, e) => UpdateDownloadCounter();
                }
                
                // Reset cancellation token for new download session
                _currentProgressWindow.ResetCancellationToken();
                
                // Add all packages to the progress window
                foreach (var package in availablePackages)
                {
                    _currentProgressWindow.AddPackage(package);
                }
                
                // Show the window
                if (!_currentProgressWindow.IsVisible)
                {
                    _currentProgressWindow.Show();
                }
                
                // Set up cancellation check so downloads can skip cancelled packages
                _packageDownloader.SetPackageCancelledCheck(_currentProgressWindow.IsPackageCancelled);
                
                // Download packages with progress tracking
                var results = await _packageDownloader.DownloadPackagesAsync(availablePackages, _currentProgressWindow.CancellationToken);
                
                // Refresh package list
                await RefreshPackagesAfterDownloadAsync();
                
                return results;
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error downloading packages: {ex.Message}",
                    "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return new Dictionary<string, bool>();
            }
        }
        
        /// <summary>
        /// Downloads all missing dependencies from the current selection
        /// </summary>
        public async Task DownloadAllMissingDependenciesAsync()
        {
            try
            {
                // Get all missing dependencies
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
                
                await DownloadMissingPackagesAsync(missingDeps);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error downloading missing dependencies: {ex.Message}",
                    "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Downloads selected missing dependencies
        /// </summary>
        public async Task DownloadSelectedMissingDependenciesAsync()
        {
            try
            {
                if (DependenciesDataGrid.SelectedItems.Count == 0)
                {
                    CustomMessageBox.Show("Please select dependencies to download.",
                        "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Get selected missing dependencies
                var selectedMissingDeps = DependenciesDataGrid.SelectedItems
                    .Cast<DependencyItem>()
                    .Where(d => d.Status == "Missing" || d.Status == "Unknown")
                    .Select(d => d.Name)
                    .ToList();
                
                if (selectedMissingDeps.Count == 0)
                {
                    CustomMessageBox.Show("No missing dependencies selected.",
                        "No Missing Dependencies", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                await DownloadMissingPackagesAsync(selectedMissingDeps);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error downloading selected dependencies: {ex.Message}",
                    "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Cancels any ongoing downloads
        /// </summary>
        public void CancelDownloads()
        {
            _downloadCancellationTokenSource?.Cancel();
        }
        
        /// <summary>
        /// Shows the download progress window
        /// </summary>
        public void ShowDownloadWindow()
        {
            if (_currentProgressWindow != null && _currentProgressWindow.IsLoaded)
            {
                if (!_currentProgressWindow.IsVisible)
                {
                    _currentProgressWindow.Show();
                }
                _currentProgressWindow.Activate();
            }
            else
            {
                CustomMessageBox.Show("No downloads in progress.",
                    "Downloads", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        
        #endregion
        
        #region Event Handlers
        
        private void OnPackageDownloadProgress(object sender, DownloadProgressEventArgs e)
        {
            // Use BeginInvoke (fire-and-forget) instead of Invoke to prevent UI blocking
            // Progress updates are non-critical and can be processed asynchronously
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    // Update progress window if it exists
                    _currentProgressWindow?.UpdateProgress(e.PackageName, e.DownloadedBytes, e.TotalBytes, e.ProgressPercentage, e.DownloadSource);
                    
                    // Update dependency status to "Downloading" if it exists in the dependencies list
                    var dep = Dependencies.FirstOrDefault(d => 
                        d.Name.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase) ||
                        d.DisplayName.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase));
                    
                    if (dep != null && dep.Status != "Downloading")
                    {
                        dep.Status = "Downloading";
                    }
                }
                catch (Exception)
                {
                }
            });
        }
        
        private async void OnPackageDownloadCompleted(object sender, DownloadCompletedEventArgs e)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    // Update progress window if it exists
                    string message = e.AlreadyExisted ? "Package already exists" : "Download completed successfully";
                    _currentProgressWindow?.MarkCompleted(e.PackageName, true, message);
                    
                    // Update dependency status after download completes
                    // Find all matching dependencies (could be multiple if .latest is used)
                    UpdateDependencyStatusAfterDownload(e.PackageName);
                    
                    // Incrementally add the downloaded package to the UI
                    await AddDownloadedPackageToUIAsync(e.FilePath, e.PackageName);
                    
                    // Always update toolbar buttons to reflect new missing count
                    UpdateToolbarButtons();
                }
                catch (Exception)
                {
                }
            });
        }
        
        private void OnPackageDownloadError(object sender, DownloadErrorEventArgs e)
        {
            // Use BeginInvoke to prevent UI blocking - error handling doesn't need to block the caller
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    // Update progress window if it exists
                    _currentProgressWindow?.MarkCompleted(e.PackageName, false, e.ErrorMessage);
                    
                    // Revert dependency status back to Missing if download failed
                    // Find all matching dependencies
                    var matchingDeps = FindMatchingDependencies(e.PackageName);
                    
                    foreach (var dep in matchingDeps)
                    {
                        if (dep.Status == "Downloading")
                        {
                            dep.Status = "Missing";
                            
                            // Also update in _originalDependencies to keep in sync
                            var origDep = _originalDependencies.FirstOrDefault(d => 
                                d.Name.Equals(dep.Name, StringComparison.OrdinalIgnoreCase) &&
                                d.Version == dep.Version);
                            if (origDep != null)
                            {
                                origDep.Status = "Missing";
                            }
                        }
                    }
                    
                    if (matchingDeps.Any())
                    {
                        // Update toolbar buttons to reflect current missing count
                        UpdateToolbarButtons();
                    }
                }
                catch (Exception)
                {
                }
            });
        }
        
        /// <summary>
        /// Updates dependency status after a package is successfully downloaded
        /// </summary>
        private void UpdateDependencyStatusAfterDownload(string downloadedPackageName)
        {
            // Find all matching dependencies in both collections
            var matchingDeps = FindMatchingDependencies(downloadedPackageName);
            
            foreach (var dep in matchingDeps)
            {
                dep.Status = "Loaded";
                
                // Also update in _originalDependencies to keep in sync
                var origDep = _originalDependencies.FirstOrDefault(d => 
                    d.Name.Equals(dep.Name, StringComparison.OrdinalIgnoreCase) &&
                    d.Version == dep.Version);
                if (origDep != null)
                {
                    origDep.Status = "Loaded";
                }
            }
        }
        
        /// <summary>
        /// Finds all dependencies that match a downloaded package name
        /// Handles exact matches, DisplayName matches, base name matches, .latest, and .min[NUMBER] dependencies
        /// </summary>
        private List<DependencyItem> FindMatchingDependencies(string packageName)
        {
            var matches = new List<DependencyItem>();
            
            if (string.IsNullOrEmpty(packageName))
                return matches;
            
            // Parse the downloaded package to get base name and version
            var downloadedInfo = DependencyVersionInfo.Parse(packageName);
            var downloadedBaseName = downloadedInfo.BaseName;
            var downloadedVersion = downloadedInfo.VersionNumber ?? 0;
            
            foreach (var dep in Dependencies)
            {
                // Skip placeholder items
                if (dep.Status == "N/A")
                    continue;
                
                bool isMatch = false;
                
                // Match 1: Exact name match (rare, but possible)
                if (dep.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                {
                    isMatch = true;
                }
                // Match 2: DisplayName match (Name.Version == packageName)
                else if (dep.DisplayName.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                {
                    isMatch = true;
                }
                // Match 3: Parse the dependency and check if downloaded package satisfies it
                else
                {
                    // Build the full dependency string from Name and Version
                    var depFullName = string.IsNullOrEmpty(dep.Version) ? dep.Name : $"{dep.Name}.{dep.Version}";
                    var depInfo = DependencyVersionInfo.Parse(depFullName);
                    
                    // Check if base names match
                    if (depInfo.BaseName.Equals(downloadedBaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Check if downloaded version satisfies the dependency requirement
                        isMatch = depInfo.IsSatisfiedBy(downloadedVersion);
                    }
                }
                
                if (isMatch)
                {
                    matches.Add(dep);
                }
            }
            
            return matches;
        }
        
        #endregion
        
        #region Helper Methods
        
        /// <summary>
        /// Adds a single downloaded package to the UI incrementally
        /// </summary>
        private async Task AddDownloadedPackageToUIAsync(string filePath, string packageName)
        {
            try
            {
                if (!System.IO.File.Exists(filePath))
                {
                    return;
                }
                
                // Parse the package metadata
                var metadata = await Task.Run(() => _packageManager?.ParseVarMetadataComplete(filePath));
                if (metadata == null)
                {
                    return;
                }
                
                // CRITICAL FIX: Add the downloaded package to PackageManager metadata
                // so that missing dependencies are recalculated and the dependency graph is updated
                if (_packageManager != null && metadata != null)
                {
                    // Set the file path and status
                    metadata.FilePath = filePath;
                    metadata.Status = "Loaded";
                    
                    // Add to PackageMetadata dictionary using the filename as key
                    var metadataKey = System.IO.Path.GetFileNameWithoutExtension(filePath);
                    _packageManager.PackageMetadata[metadataKey] = metadata;
                    
                    // Recalculate missing dependencies for all packages
                    // This will remove the downloaded package from other packages' MissingDependencies lists
                    await Task.Run(() => _packageManager.DetectMissingDependencies());
                }
                
                // Use the full filename (without .var extension) as the package name for display
                // This ensures we show "VAMBO.Elsa.3" instead of just "Elsa"
                var fullPackageName = System.IO.Path.GetFileNameWithoutExtension(metadata.Filename);
                
                // Index preview images for the newly downloaded package
                await Task.Run(() => _packageManager?.IndexPreviewImagesForPackage(filePath, fullPackageName));
                
                // Reload the preview image index to include this package
                if (_packageManager != null && _imageManager != null && _imageManager.PreviewImageIndex.Count > 0)
                {
                    await Task.Run(() => _imageManager.LoadExternalImageIndex(_imageManager.PreviewImageIndex.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)));
                }
                
                // Check if package already exists in the list
                // Search by full name first, then by short name (metadata.PackageName) to handle old entries
                var existingPackage = Packages.FirstOrDefault(p => 
                    p.Name.Equals(fullPackageName, StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Equals(metadata.PackageName, StringComparison.OrdinalIgnoreCase));
                
                if (existingPackage != null)
                {
                    // Update existing package with correct full name
                    existingPackage.Name = fullPackageName;  // Fix the name if it was wrong
                    existingPackage.Status = "Loaded";
                    existingPackage.Creator = metadata.CreatorName ?? "Unknown";
                    existingPackage.FileSize = metadata.FileSize;
                    existingPackage.ModifiedDate = metadata.ModifiedDate;
                    existingPackage.IsOptimized = metadata.IsOptimized;
                    existingPackage.IsDuplicate = metadata.IsDuplicate;
                    existingPackage.DuplicateLocationCount = metadata.DuplicateLocationCount;
                    existingPackage.DependencyCount = metadata.Dependencies?.Length ?? 0;
                    existingPackage.DependentsCount = 0; // Will be calculated on full refresh
                }
                else
                {
                    // Add new package to the list
                    var newPackage = new PackageItem
                    {
                        Name = fullPackageName,  // Use full package name with creator and version
                        Status = "Loaded",
                        Creator = metadata.CreatorName ?? "Unknown",
                        DependencyCount = metadata.Dependencies?.Length ?? 0,
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
                
                // Refresh filter lists will happen at the end of all downloads
            }
            catch (Exception)
            {
            }
        }
        
        /// <summary>
        /// Refreshes the package list after downloads complete
        /// </summary>
        private async Task RefreshPackagesAfterDownloadAsync()
        {
            try
            {
                // Rebuild package status index - force rebuild to detect newly downloaded files
                if (_packageFileManager != null)
                {
                    await Task.Run(() => _packageFileManager.RefreshPackageStatusIndex(force: true));
                }
                
                // Update dependencies status
                UpdateDependenciesStatus();
                
                // Reload packages to show newly downloaded packages in the main table
                // This will also reload preview images automatically
                await Dispatcher.InvokeAsync(() => RefreshPackages());
            }
            catch (Exception)
            {
            }
        }
        
        /// <summary>
        /// Checks if a package name is available for download
        /// </summary>
        public bool IsPackageAvailableForDownload(string packageName)
        {
            return _packageDownloader?.IsPackageAvailable(packageName) ?? false;
        }
        
        /// <summary>
        /// Gets the count of missing dependencies that are available for download
        /// </summary>
        public int GetDownloadableMissingDependenciesCount()
        {
            if (_packageDownloader == null)
                return 0;
            
            return Dependencies
                .Where(d => (d.Status == "Missing" || d.Status == "Unknown") && 
                           _packageDownloader.IsPackageAvailable(d.Name))
                .Count();
        }
        
        #endregion
        
        #region Download Queue Event Handlers
        
        private void OnQueueStatusChanged(object sender, QueueStatusChangedEventArgs e)
        {
            // Use BeginInvoke to prevent UI blocking
            Dispatcher.BeginInvoke(() =>
            {
                // Update UI status bar or queue window if needed
            });
        }
        
        private void OnDownloadQueued(object sender, DownloadQueuedEventArgs e)
        {
            // Use BeginInvoke to prevent UI blocking
            Dispatcher.BeginInvoke(() =>
            {
            });
        }
        
        private void OnDownloadStartedInQueue(object sender, DownloadStartedEventArgs e)
        {
            // Use BeginInvoke to prevent UI blocking
            Dispatcher.BeginInvoke(() =>
            {
            });
        }
        
        private void OnDownloadRemovedFromQueue(object sender, DownloadRemovedEventArgs e)
        {
            // Use BeginInvoke to prevent UI blocking
            Dispatcher.BeginInvoke(() =>
            {
            });
        }
        
        /// <summary>
        /// Gets the download queue manager (for UI access)
        /// </summary>
        public DownloadQueueManager GetDownloadQueueManager()
        {
            return _downloadQueueManager;
        }
        
        #endregion
        
        #region Cleanup
        
        /// <summary>
        /// Disposes the package downloader
        /// Should be called when closing the application or changing folders
        /// </summary>
        private void DisposePackageDownloader()
        {
            try
            {
                _downloadCancellationTokenSource?.Cancel();
                _downloadCancellationTokenSource?.Dispose();
                _downloadCancellationTokenSource = null;
                
                if (_downloadQueueManager != null)
                {
                    _downloadQueueManager.QueueStatusChanged -= OnQueueStatusChanged;
                    _downloadQueueManager.DownloadQueued -= OnDownloadQueued;
                    _downloadQueueManager.DownloadStarted -= OnDownloadStartedInQueue;
                    _downloadQueueManager.DownloadRemoved -= OnDownloadRemovedFromQueue;
                    _downloadQueueManager.Dispose();
                    _downloadQueueManager = null;
                }
                
                if (_packageDownloader != null)
                {
                    _packageDownloader.DownloadProgress -= OnPackageDownloadProgress;
                    _packageDownloader.DownloadCompleted -= OnPackageDownloadCompleted;
                    _packageDownloader.DownloadError -= OnPackageDownloadError;
                    _packageDownloader.Dispose();
                    _packageDownloader = null;
                }
                
            }
            catch (Exception)
            {
            }
        }
        
        #endregion
    }
}

