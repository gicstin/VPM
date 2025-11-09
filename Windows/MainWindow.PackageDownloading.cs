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
        private NetworkPermissionService _networkPermissionService;
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
                    Console.WriteLine("[PackageDownloader] Cannot initialize: No folder selected");
                    return;
                }
                
                var addonPackagesFolder = System.IO.Path.Combine(_selectedFolder, "AddonPackages");
                
                // Dispose existing downloader if any
                _packageDownloader?.Dispose();
                
                // Create new downloader
                _packageDownloader = new PackageDownloader(addonPackagesFolder);
                
                // Initialize network permission service only once
                if (_networkPermissionService == null)
                {
                    _networkPermissionService = new NetworkPermissionService(_settingsManager);
                }
                
                // Set network permission check callback
                _packageDownloader.SetNetworkPermissionCheck(async () =>
                {
                    return await _networkPermissionService.RequestNetworkAccessAsync();
                });
                
                // Subscribe to events
                _packageDownloader.DownloadProgress += OnPackageDownloadProgress;
                _packageDownloader.DownloadCompleted += OnPackageDownloadCompleted;
                _packageDownloader.DownloadError += OnPackageDownloadError;
                
                // Initialize download queue manager with 2 concurrent downloads
                _downloadQueueManager?.Dispose();
                _downloadQueueManager = new DownloadQueueManager(_packageDownloader, maxConcurrentDownloads: 2);
                _downloadQueueManager.QueueStatusChanged += OnQueueStatusChanged;
                _downloadQueueManager.DownloadQueued += OnDownloadQueued;
                _downloadQueueManager.DownloadStarted += OnDownloadStartedInQueue;
                _downloadQueueManager.DownloadRemoved += OnDownloadRemovedFromQueue;
                
                // Initialize update checker now that downloader is ready
                InitializeUpdateChecker();
                
                Console.WriteLine("[PackageDownloader] Initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PackageDownloader] Initialization error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Loads the package download list from GitHub or local fallback
        /// This method will request network permission if not already granted
        /// </summary>
        /// <returns>True if successfully loaded, false otherwise</returns>
        public async Task<bool> LoadPackageDownloadListAsync()
        {
            try
            {
                if (_packageDownloader == null)
                    return false;
                
                // Subscribe to console output to show retry progress
                var originalOut = Console.Out;
                var statusWriter = new StatusWriter(this, originalOut);
                Console.SetOut(statusWriter);
                
                try
                {
                    // Use Cloudflare Worker proxy for encrypted database
                    const string githubUrl = "https://vpm-gist-proxy.jabcoebi.workers.dev";
                    var success = await _packageDownloader.LoadEncryptedPackageListAsync(githubUrl);
                    
                    // Success or failure is handled by the caller
                    
                    return success;
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PackageDownloader] Error loading package list: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Custom TextWriter to capture console output and update status bar
        /// </summary>
        private class StatusWriter : System.IO.TextWriter
        {
            private readonly MainWindow _window;
            private readonly System.IO.TextWriter _originalOut;
            
            public StatusWriter(MainWindow window, System.IO.TextWriter originalOut)
            {
                _window = window;
                _originalOut = originalOut;
            }
            
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
            
            public override void WriteLine(string value)
            {
                // Update status bar for specific messages
                if (value != null)
                {
                    if (value.Contains("[PackageDownloader]"))
                    {
                        // Extract the message part
                        var message = value.Substring(value.IndexOf("]") + 1).Trim();
                        
                        // Update status bar on UI thread
                        _window.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (message.Contains("Retry attempt"))
                            {
                                _window.SetStatus(message);
                            }
                            else if (message.Contains("Waiting") && message.Contains("firewall"))
                            {
                                _window.SetStatus("Waiting for firewall approval...");
                            }
                        }));
                    }
                    else if (value.Contains("[EncryptedDB]"))
                    {
                        // Update status for key database loading steps
                        if (value.Contains("Downloading") || value.Contains("Decrypting") || 
                            value.Contains("Decompressing") || value.Contains("Parsing"))
                        {
                            var message = value.Substring(value.IndexOf("]") + 1).Trim();
                            _window.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _window.SetStatus(message);
                            }));
                        }
                    }
                }
                
                // Write to both debug output AND the actual console
                System.Diagnostics.Debug.WriteLine(value);
                _originalOut?.WriteLine(value);
            }
            
            public override void Write(string value)
            {
                System.Diagnostics.Debug.Write(value);
                _originalOut?.Write(value);
            }
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
            Dispatcher.Invoke(() =>
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
                catch (Exception ex)
                {
                    Console.WriteLine($"[PackageDownloader] Error in progress handler: {ex.Message}");
                }
            });
        }
        
        private async void OnPackageDownloadCompleted(object sender, DownloadCompletedEventArgs e)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    Console.WriteLine($"[MainWindow] Download completed: {e.PackageName} at {e.FilePath}");
                    
                    // Update progress window if it exists
                    string message = e.AlreadyExisted ? "Package already exists" : "Download completed successfully";
                    _currentProgressWindow?.MarkCompleted(e.PackageName, true, message);
                    
                    // Update dependency status after download completes - try multiple matching strategies
                    var dep = Dependencies.FirstOrDefault(d => 
                        d.Name.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase) ||
                        d.DisplayName.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase) ||
                        e.PackageName.StartsWith(d.Name + ".", StringComparison.OrdinalIgnoreCase));
                    
                    if (dep != null)
                    {
                        Console.WriteLine($"[MainWindow] Updating dependency status for: {dep.Name}");
                        dep.Status = "Available";
                    }
                    
                    // Incrementally add the downloaded package to the UI
                    Console.WriteLine($"[MainWindow] Adding package to UI: {e.PackageName}");
                    await AddDownloadedPackageToUIAsync(e.FilePath, e.PackageName);
                    
                    // Always update toolbar buttons to reflect new missing count
                    UpdateToolbarButtons();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PackageDownloader] Error in completion handler: {ex.Message}");
                    Console.WriteLine($"[PackageDownloader] Stack trace: {ex.StackTrace}");
                }
            });
        }
        
        private void OnPackageDownloadError(object sender, DownloadErrorEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Update progress window if it exists
                    _currentProgressWindow?.MarkCompleted(e.PackageName, false, e.ErrorMessage);
                    
                    // Revert dependency status back to Missing if download failed
                    var dep = Dependencies.FirstOrDefault(d => 
                        d.Name.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase) ||
                        d.DisplayName.Equals(e.PackageName, StringComparison.OrdinalIgnoreCase));
                    
                    if (dep != null && dep.Status == "Downloading")
                    {
                        dep.Status = "Missing";
                        
                        // Update toolbar buttons to reflect current missing count
                        UpdateToolbarButtons();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PackageDownloader] Error in error handler: {ex.Message}");
                }
            });
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
                
                // Use the full filename (without .var extension) as the package name for display
                // This ensures we show "VAMBO.Elsa.3" instead of just "Elsa"
                var fullPackageName = System.IO.Path.GetFileNameWithoutExtension(metadata.Filename);
                
                // Index preview images for the newly downloaded package
                await Task.Run(() => _packageManager?.IndexPreviewImagesForPackage(filePath, fullPackageName));
                
                // Reload the preview image index to include this package
                if (_packageManager != null && _imageManager != null && _packageManager.PreviewImageIndex.Count > 0)
                {
                    await Task.Run(() => _imageManager.LoadExternalImageIndex(_packageManager.PreviewImageIndex));
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
                    existingPackage.DependencyCount = metadata.Dependencies?.Count ?? 0;
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
                
                // Refresh filter lists will happen at the end of all downloads
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to add downloaded package to UI: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Refreshes the package list after downloads complete
        /// </summary>
        private async Task RefreshPackagesAfterDownloadAsync()
        {
            try
            {
                // Rebuild package status index
                if (_packageFileManager != null)
                {
                    await Task.Run(() => _packageFileManager.RefreshPackageStatusIndex());
                }
                
                // Update dependencies status
                UpdateDependenciesStatus();
                
                // Reload packages to show newly downloaded packages in the main table
                // This will also reload preview images automatically
                await Dispatcher.InvokeAsync(() => RefreshPackages());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PackageDownloader] Error refreshing packages: {ex.Message}");
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
            Dispatcher.Invoke(() =>
            {
                Console.WriteLine($"[DownloadQueue] Status: {e.ActiveCount} active, {e.QueuedCount} queued");
                // Update UI status bar or queue window if needed
            });
        }
        
        private void OnDownloadQueued(object sender, DownloadQueuedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                Console.WriteLine($"[DownloadQueue] Queued: {e.Download.PackageName}");
            });
        }
        
        private void OnDownloadStartedInQueue(object sender, DownloadStartedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                Console.WriteLine($"[DownloadQueue] Started: {e.Download.PackageName}");
            });
        }
        
        private void OnDownloadRemovedFromQueue(object sender, DownloadRemovedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                Console.WriteLine($"[DownloadQueue] Removed: {e.Download.PackageName}");
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
                
                Console.WriteLine("[PackageDownloader] Disposed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PackageDownloader] Error during disposal: {ex.Message}");
            }
        }
        
        #endregion
    }
}

