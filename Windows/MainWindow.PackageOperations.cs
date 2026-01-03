using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VPM.Models;
using System.IO;

namespace VPM
{
    /// <summary>
    /// Package operations functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        #region Focus Tracking Fields

        /// <summary>
        /// Tracks whether DependenciesDataGrid has focus (for hotkey label display)
        /// </summary>
        private bool _dependenciesDataGridHasFocus = false;

        #endregion

        #region Main Table Loading Overlay

        private void ShowMainTableLoading(string message, int totalItems = 0)
        {
            MainTableLoadingText.Text = message;
            MainTableLoadingProgress.Value = 0;
            MainTableLoadingProgress.Maximum = totalItems > 0 ? totalItems : 100;
            MainTableLoadingProgress.IsIndeterminate = totalItems <= 0;
            MainTableLoadingCount.Text = totalItems > 0 ? $"0/{totalItems}" : "";
            MainTableLoadingOverlay.Visibility = Visibility.Visible;
            
            // Grey out grids
            PackageDataGrid.IsEnabled = false;
            ScenesDataGrid.IsEnabled = false;
        }

        private void UpdateMainTableLoading(int current, int total, string currentItem)
        {
            MainTableLoadingProgress.IsIndeterminate = false;
            MainTableLoadingProgress.Maximum = total;
            MainTableLoadingProgress.Value = current;
            MainTableLoadingCount.Text = $"{current}/{total}";
        }

        private void HideMainTableLoading()
        {
            MainTableLoadingOverlay.Visibility = Visibility.Collapsed;
            
            // Re-enable grids
            PackageDataGrid.IsEnabled = true;
            ScenesDataGrid.IsEnabled = true;
        }

        #endregion

        #region Dependencies Table Loading Overlay

        private void ShowDependenciesTableLoading(string message, int totalItems = 0)
        {
            DependenciesTableLoadingText.Text = message;
            DependenciesTableLoadingProgress.Value = 0;
            DependenciesTableLoadingProgress.Maximum = totalItems > 0 ? totalItems : 100;
            DependenciesTableLoadingProgress.IsIndeterminate = totalItems <= 0;
            DependenciesTableLoadingCount.Text = totalItems > 0 ? $"0/{totalItems}" : "";
            DependenciesTableLoadingOverlay.Visibility = Visibility.Visible;
            
            // Grey out grid
            DependenciesDataGrid.IsEnabled = false;
        }

        private void UpdateDependenciesTableLoading(int current, int total, string currentItem)
        {
            DependenciesTableLoadingProgress.IsIndeterminate = false;
            DependenciesTableLoadingProgress.Maximum = total;
            DependenciesTableLoadingProgress.Value = current;
            DependenciesTableLoadingCount.Text = $"{current}/{total}";
        }

        private void HideDependenciesTableLoading()
        {
            DependenciesTableLoadingOverlay.Visibility = Visibility.Collapsed;
            
            // Re-enable grid
            DependenciesDataGrid.IsEnabled = true;
        }

        #endregion

        #region Package Load/Unload Operations

        private async Task PrepareForPackageFileOperationsAsync(IEnumerable<string> names)
        {
            var namesList = names?.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();

            _imageLoadingCts?.Cancel();
            _imageLoadingCts = new System.Threading.CancellationTokenSource();
            PreviewImages.Clear();

            if (_imageManager != null && namesList.Count > 0)
            {
                await _imageManager.ReleasePackagesAsync(namesList);
            }
        }

        private static Progress<(int completed, int total, string currentPackage)> CreateStatusProgress(Action<(int completed, int total, string currentPackage)> onProgress)
        {
            return new Progress<(int completed, int total, string currentPackage)>(p => onProgress?.Invoke(p));
        }

        private async void LoadPackages_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return;

                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>()
                    .Where(p => p.Status == "Available" || p.IsExternal)
                    .ToList();

                if (selectedPackages.Count == 0)
                {
                    MessageBox.Show("No available or external packages selected.", "No Packages",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Enhanced confirmation dialog with better information
                if (selectedPackages.Count >= 100)
                {
                    var packageNames = selectedPackages.Take(5).Select(p => p.Name).ToList();
                    var displayNames = string.Join("\n", packageNames);
                    if (selectedPackages.Count > 5)
                    {
                        displayNames += $"\n... and {selectedPackages.Count - 5} more packages";
                    }

                    var result = CustomMessageBox.Show(
                        $"Load {selectedPackages.Count} packages?\n\nThis operation may take several minutes for large batches.\n\n{displayNames}",
                        "Confirm Load Operation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                        return;
                }

                // Disable UI during operation
                LoadPackagesButton.IsEnabled = false;

                var externalPackages = selectedPackages.Where(p => p.IsExternal).ToList();
                var regularPackages = selectedPackages.Where(p => !p.IsExternal && p.Status == "Available").ToList();
                var totalCount = externalPackages.Count + regularPackages.Count;

                ShowMainTableLoading("Loading packages...", totalCount);

                try
                {
                    var packageNames = selectedPackages.Select(p => p.Name).ToList();

                    await PrepareForPackageFileOperationsAsync(packageNames);

                    var results = new List<(string packageName, bool success, string error)>();
                    var completed = 0;

                    // Load external packages first using their file paths from metadata
                    foreach (var package in externalPackages)
                    {
                        completed++;
                        UpdateMainTableLoading(completed, totalCount, package.Name);
                        SetStatus(totalCount > 1
                            ? $"Loading packages... {completed}/{totalCount} ({completed * 100 / totalCount}%)"
                            : $"Loading {package.Name}...");

                        // Get the file path from metadata
                        if (_packageManager.PackageMetadata.TryGetValue(package.MetadataKey, out var metadata) && 
                            !string.IsNullOrEmpty(metadata.FilePath))
                        {
                            var (success, error) = await _packageFileManager.LoadPackageFromExternalPathAsync(package.Name, metadata.FilePath);
                            results.Add((package.Name, success, error));
                        }
                        else
                        {
                            results.Add((package.Name, false, "External package file path not found in metadata"));
                        }
                    }

                    // Load regular available packages
                    if (regularPackages.Count > 0)
                    {
                        var regularNames = regularPackages.Select(p => p.Name).ToList();
                        var progress = CreateStatusProgress(p =>
                        {
                            UpdateMainTableLoading(completed + p.completed, totalCount, p.currentPackage);
                            SetStatus(totalCount > 1
                                ? $"Loading packages... {completed + p.completed}/{totalCount} ({(completed + p.completed) * 100 / totalCount}%)"
                                : $"Loading {p.currentPackage}...");
                        });

                        var regularResults = await _packageFileManager.LoadPackagesAsync(regularNames, progress);
                        results.AddRange(regularResults);
                    }

                    // Clear metadata cache to ensure new paths are picked up
                    ClearPackageMetadataCache();

                    // Update package statuses based on results
                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();

                    // PERFORMANCE FIX: Pre-build lookup dictionary for O(1) access instead of O(n) FirstOrDefault
                    var packageLookup = selectedPackages.ToDictionary(
                        p => p.Name, 
                        p => p, 
                        StringComparer.OrdinalIgnoreCase);

                    foreach ((string packageName, bool success, string error) in results)
                    {
                        if (packageLookup.TryGetValue(packageName, out var package) && success)
                        {
                            // Clear external destination properties so status color reflects "Loaded"
                            package.ExternalDestinationName = "";
                            package.ExternalDestinationColorHex = "";
                            package.Status = "Loaded";
                            statusUpdates.Add((packageName, "Loaded", package.StatusColor));
                        }
                        // Load failed - error handled in status reporting below
                    }

                    // Update image grid status indicators in batch
                    if (statusUpdates.Count > 0)
                    {
                        UpdateMultiplePackageStatusInImageGrid(statusUpdates);
                    }

                    // Update status for successfully loaded packages (efficient bulk update)
                    var successfullyLoaded = results
                        .Where(r => r.success)
                        .Select(r => r.packageName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (successfullyLoaded.Count > 0)
                    {
                        await BulkUpdatePackageStatus(successfullyLoaded, "Loaded");
                    }

                    // Re-enable UI BEFORE refreshing image grid
                    LoadPackagesButton.IsEnabled = true;
                    UpdatePackageButtonBar();

                    // Refresh image grid to show newly loaded packages
                    await RefreshCurrentlyDisplayedImagesAsync();

                    // Enhanced status reporting
                    var successCount = results.Count(r => r.success);
                    var failureCount = results.Count(r => !r.success);

                    // Filter out throttling errors (recently performed operations)
                    var throttledCount = results.Count(r => !r.success && r.error.Contains("recently performed"));
                    var actualFailureCount = failureCount - throttledCount;

                    if (successCount > 0 && actualFailureCount == 0)
                    {
                        SetStatus($"Successfully loaded {successCount} packages" +
                                 (throttledCount > 0 ? $" ({throttledCount} skipped - too soon)" : ""));
                    }
                    else if (successCount > 0 && actualFailureCount > 0)
                    {
                        SetStatus($"Loaded {successCount} packages ({actualFailureCount} failed)");
                    }
                    else if (actualFailureCount > 0)
                    {
                        SetStatus($"Failed to load {actualFailureCount} packages");

                        // Show detailed error for small batches (excluding throttling errors)
                        var actualErrors = results.Where(r => !r.success && !r.error.Contains("recently performed")).ToList();
                        if (actualErrors.Count > 0 && actualErrors.Count <= 5)
                        {
                            var errors = actualErrors.Select(r => $"{r.packageName}: {r.error}");
                            MessageBox.Show($"Load operation failed:\n\n{string.Join("\n", errors)}",
                                          "Load Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else if (throttledCount > 0)
                    {
                        // All operations were throttled
                        SetStatus($"Operation skipped - please wait a moment before retrying");
                    }

                }
                finally
                {
                    HideMainTableLoading();

                    // Re-enable UI if not already enabled
                    if (!LoadPackagesButton.IsEnabled)
                    {
                        LoadPackagesButton.IsEnabled = true;
                        UpdatePackageButtonBar();
                    }
                }
            }
            catch (Exception)
            {
                MessageBox.Show("Error during load operation", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Load operation failed");
            }
        }

        public async Task<(bool success, int missingCount)> LoadPackagesWithDependenciesAsync(List<string> packageNames, bool interactive = true, bool suppressEmptyMessage = false)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return (false, 0);

                if (packageNames == null || packageNames.Count == 0)
                {
                    if (interactive && !suppressEmptyMessage)
                        MessageBox.Show("No packages specified.", "No Packages", MessageBoxButton.OK, MessageBoxImage.Information);
                    return (false, 0);
                }

                var packagesToLoad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                var externalPackages = new List<string>();
                var regularPackages = new List<string>();

                foreach (var packageName in packageNames)
                {
                    if (!_packageManager.PackageMetadata.TryGetValue(packageName, out var metadata))
                        continue;

                    // Load if Available or External (and not already loaded/cleared)
                    if (metadata.Status == "Available")
                    {
                        packagesToLoad.Add(packageName);
                        regularPackages.Add(packageName);
                    }
                    else if (metadata.IsExternal)
                    {
                        packagesToLoad.Add(packageName);
                        externalPackages.Add(packageName);
                    }
                    
                    // Resolve dependencies
                    if (metadata.Dependencies != null)
                    {
                        foreach (var dependency in metadata.Dependencies)
                        {
                            var dependencyName = dependency;
                            if (dependency.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                            {
                                dependencyName = Path.GetFileNameWithoutExtension(dependency);
                            }

                            string baseName = dependencyName;
                            var lastDotIndex = dependencyName.LastIndexOf('.');
                            if (lastDotIndex > 0)
                            {
                                var potentialVersion = dependencyName.Substring(lastDotIndex + 1);
                                if (int.TryParse(potentialVersion, out _) || potentialVersion.Equals("latest", StringComparison.OrdinalIgnoreCase))
                                {
                                    baseName = dependencyName.Substring(0, lastDotIndex);
                                }
                            }

                            allDependencies.Add(baseName);
                        }
                    }
                }

                // Find available dependencies (local)
                var availableDependencies = allDependencies
                    .Where(d => !packagesToLoad.Contains(d))
                    .Where(d => {
                        var status = _packageFileManager?.GetPackageStatus(d);
                        return status == "Available" || status == "Outdated" || status == "Archived";
                    })
                    .ToList();

                // Find external dependencies
                var externalDependencies = new List<(string name, VarMetadata metadata)>();
                foreach (var depName in allDependencies.Where(d => !packagesToLoad.Contains(d) && !availableDependencies.Contains(d)))
                {
                    var depMetadata = _packageManager?.PackageMetadata?.Values
                        .Where(p => $"{p.CreatorName}.{p.PackageName}".Equals(depName, StringComparison.OrdinalIgnoreCase) && p.IsExternal)
                        .OrderByDescending(p => p.Version)
                        .FirstOrDefault();
                    
                    if (depMetadata != null && !string.IsNullOrEmpty(depMetadata.FilePath))
                    {
                        externalDependencies.Add((depName, depMetadata));
                    }
                }

                // PERFORMANCE FIX: Create HashSets for O(1) lookups during missing dependency calculation
                var availableDepsSet = new HashSet<string>(availableDependencies, StringComparer.OrdinalIgnoreCase);
                var externalDepsSet = new HashSet<string>(externalDependencies.Select(e => e.name), StringComparer.OrdinalIgnoreCase);

                // Calculate missing dependencies (neither to-be-loaded, nor already loaded)
                var missingCount = 0;
                foreach (var depName in allDependencies)
                {
                    // Check if it's being loaded
                    if (packagesToLoad.Contains(depName) || availableDepsSet.Contains(depName) || externalDepsSet.Contains(depName))
                        continue;

                    // Check if it's already loaded or has another valid status that doesn't require loading but isn't "Missing"
                    var status = _packageFileManager?.GetPackageStatus(depName);
                    if (status == "Loaded") 
                        continue;
                    
                    // Note: "Unknown" usually implies missing if not found in metadata
                    // If GetPackageStatus returns "Missing", or if it's "Unknown" (and not in metadata properly)
                    // Simplified check: If it's not Loaded, Available, Outdated, Archived, or External -> It's missing
                    
                    // We already filtered Available/Outdated/Archived into availableDependencies.
                    // We already filtered External into externalDependencies.
                    // So if we are here, it's not any of those.
                    // If it was Loaded, we skipped it above.
                    // So it must be Missing.
                    missingCount++;
                }

                var totalToLoad = packagesToLoad.Count + availableDependencies.Count + externalDependencies.Count;

                if (totalToLoad == 0)
                {
                    if (interactive && !suppressEmptyMessage)
                        MessageBox.Show("No packages or dependencies available to load.", "No Packages", MessageBoxButton.OK, MessageBoxImage.Information);
                    return (true, missingCount);
                }

                if (interactive && totalToLoad >= 50)
                {
                    var displayPackages = packagesToLoad.Take(3).ToList();
                    var allDeps = availableDependencies.Concat(externalDependencies.Select(e => e.name)).ToList();
                    var displayDeps = allDeps.Take(3).ToList();
                    var displayText = "Packages:\n" + string.Join("\n", displayPackages);
                    if (packagesToLoad.Count > 3)
                        displayText += $"\n... and {packagesToLoad.Count - 3} more";
                    
                    if (allDeps.Count > 0)
                    {
                        displayText += "\n\nDependencies:\n" + string.Join("\n", displayDeps);
                        if (allDeps.Count > 3)
                            displayText += $"\n... and {allDeps.Count - 3} more";
                    }

                    var result = MessageBox.Show(
                        $"Load {packagesToLoad.Count} packages + {allDeps.Count} dependencies ({totalToLoad} total)?\n\nThis operation may take several minutes.\n\n{displayText}",
                        "Confirm Load Operation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                        return (false, missingCount);
                }

                LoadPackagesButton.IsEnabled = false;
                LoadPackagesWithDepsButton.IsEnabled = false;

                if (interactive)
                    ShowMainTableLoading("Loading packages and dependencies...", totalToLoad);

                try
                {
                    var allPackagesToLoad = new List<string>(packagesToLoad);
                    allPackagesToLoad.AddRange(availableDependencies);
                    allPackagesToLoad.AddRange(externalDependencies.Select(e => e.name));

                    await PrepareForPackageFileOperationsAsync(allPackagesToLoad);

                    var results = new List<(string packageName, bool success, string error)>();
                    var totalCount = totalToLoad;
                    var completed = 0;

                    foreach (var pkgName in externalPackages)
                    {
                        completed++;
                        if (interactive) UpdateMainTableLoading(completed, totalCount, pkgName);
                        SetStatus(totalCount > 1
                            ? $"Loading packages and dependencies... {completed}/{totalCount} ({completed * 100 / totalCount}%)"
                            : $"Loading {pkgName}...");

                        if (_packageManager.PackageMetadata.TryGetValue(pkgName, out var metadata) && 
                            !string.IsNullOrEmpty(metadata.FilePath))
                        {
                            var (success, error) = await _packageFileManager.LoadPackageFromExternalPathAsync(pkgName, metadata.FilePath);
                            results.Add((pkgName, success, error));
                        }
                        else
                        {
                            results.Add((pkgName, false, "External package file path not found in metadata"));
                        }
                    }

                    foreach (var (depName, depMetadata) in externalDependencies)
                    {
                        completed++;
                        if (interactive) UpdateMainTableLoading(completed, totalCount, depName);
                        SetStatus(totalCount > 1
                            ? $"Loading packages and dependencies... {completed}/{totalCount} ({completed * 100 / totalCount}%)"
                            : $"Loading {depName}...");

                        var (success, error) = await _packageFileManager.LoadPackageFromExternalPathAsync(depName, depMetadata.FilePath);
                        results.Add((depName, success, error));
                    }

                    var regularAndDeps = regularPackages.Concat(availableDependencies).ToList();
                    if (regularAndDeps.Count > 0)
                    {
                        var progress = CreateStatusProgress(p =>
                        {
                            if (interactive) UpdateMainTableLoading(completed + p.completed, totalCount, p.currentPackage);
                            SetStatus(totalCount > 1
                                ? $"Loading packages and dependencies... {completed + p.completed}/{totalCount} ({(completed + p.completed) * 100 / totalCount}%)"
                                : $"Loading {p.currentPackage}...");
                        });

                        var regularResults = await _packageFileManager.LoadPackagesAsync(regularAndDeps, progress);
                        results.AddRange(regularResults);
                    }

                    ClearPackageMetadataCache();

                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();
                    
                    var successfullyLoaded = results
                        .Where(r => r.success)
                        .Select(r => r.packageName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (successfullyLoaded.Count > 0)
                    {
                        await BulkUpdatePackageStatus(successfullyLoaded, "Loaded");
                        
                        _packageFileManager?.RefreshPackageStatusIndex(force: true);
                        
                        if (PackageDataGrid?.SelectedItems?.Count > 0)
                        {
                            var selectedPackage = PackageDataGrid.SelectedItems[0] as PackageItem;
                            if (selectedPackage != null)
                            {
                                DisplayDependencies(selectedPackage);
                            }
                        }
                    }

                    LoadPackagesButton.IsEnabled = true;
                    LoadPackagesWithDepsButton.IsEnabled = true;
                    UpdatePackageButtonBar();

                    await RefreshCurrentlyDisplayedImagesAsync();

                    var successCount = results.Count(r => r.success);
                    var failureCount = results.Count(r => !r.success);
                    var throttledCount = results.Count(r => !r.success && r.error.Contains("recently performed"));
                    var actualFailureCount = failureCount - throttledCount;

                    if (successCount > 0 && actualFailureCount == 0)
                    {
                        SetStatus($"Successfully loaded {successCount} packages and dependencies" +
                                 (throttledCount > 0 ? $" ({throttledCount} skipped - too soon)" : ""));
                    }
                    else if (successCount > 0 && actualFailureCount > 0)
                    {
                        SetStatus($"Loaded {successCount} packages and dependencies ({actualFailureCount} failed)");
                    }
                    else if (actualFailureCount > 0)
                    {
                        SetStatus($"Failed to load {actualFailureCount} packages and dependencies");

                        if (interactive)
                        {
                            var actualErrors = results.Where(r => !r.success && !r.error.Contains("recently performed")).ToList();
                            if (actualErrors.Count > 0 && actualErrors.Count <= 5)
                            {
                                var errors = actualErrors.Select(r => $"{r.packageName}: {r.error}");
                                MessageBox.Show($"Load operation failed:\n\n{string.Join("\n", errors)}",
                                              "Load Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                    }
                    else if (throttledCount > 0)
                    {
                        SetStatus($"Operation skipped - please wait a moment before retrying");
                    }
                    
                    return (actualFailureCount == 0, missingCount);
                }
                finally
                {
                    if (interactive)
                        HideMainTableLoading();

                    if (!LoadPackagesButton.IsEnabled)
                    {
                        LoadPackagesButton.IsEnabled = true;
                        LoadPackagesWithDepsButton.IsEnabled = true;
                        UpdatePackageButtonBar();
                    }
                }
            }
            catch (Exception ex)
            {
                if (interactive)
                    MessageBox.Show($"Error during load operation: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Load operation failed");
                return (false, 0);
            }
        }

        private async void LoadPackagesWithDeps_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return;

                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>()
                    .Where(p => p.Status == "Available" || p.IsExternal)
                    .ToList();

                if (selectedPackages.Count == 0)
                {
                    MessageBox.Show("No available or external packages selected.", "No Packages",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await LoadPackagesWithDependenciesAsync(selectedPackages.Select(p => p.Name).ToList(), interactive: true);
            }
            catch (Exception)
            {
                MessageBox.Show("Error during load operation", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Load operation failed");
            }
        }

        private async void UnloadPackages_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return;

                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>()
                    .Where(p => p.Status == "Loaded" || p.IsExternal)
                    .ToList();

                if (selectedPackages.Count == 0)
                {
                    MessageBox.Show("No loaded or external packages selected.", "No Packages",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (selectedPackages.Count >= 100)
                {
                    var packageNames = selectedPackages.Take(10).Select(p => p.Name).ToList();
                    var displayNames = string.Join("\n", packageNames);
                    if (selectedPackages.Count > 10)
                    {
                        displayNames += $"\n... and {selectedPackages.Count - 10} more packages";
                    }

                    var result = CustomMessageBox.Show(
                        $"Unload {selectedPackages.Count} packages?\n\n{displayNames}",
                        "Confirm Unload",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                        return;
                }

                // CRITICAL: If any of the selected packages are being previewed, clear the preview first
                // This ensures images are released before files are unloaded
                if (_currentPreviewPackage != null && selectedPackages.Any(p => p.Name == _currentPreviewPackage.Name))
                {
                    HidePreviewPanel();
                }

                // Disable UI during operation
                UnloadPackagesButton.IsEnabled = false;

                ShowMainTableLoading("Unloading packages...", selectedPackages.Count);

                try
                {
                    // Use enhanced batch operation with progress reporting
                    var packageNames = selectedPackages.Select(p => p.Name).ToList();

                    await PrepareForPackageFileOperationsAsync(packageNames);
                    
                    var progress = CreateStatusProgress(p =>
                    {
                        UpdateMainTableLoading(p.completed, p.total, p.currentPackage);
                        SetStatus(p.total > 1
                            ? $"Unloading packages... {p.completed}/{p.total} ({p.completed * 100 / p.total}%)"
                            : $"Unloading {p.currentPackage}...");
                    });

                    var results = await _packageFileManager.UnloadPackagesAsync(packageNames, progress);

                    // Clear metadata cache to ensure new paths are picked up
                    ClearPackageMetadataCache();

                    // Update package statuses based on results
                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();

                    // PERFORMANCE FIX: Pre-build lookup dictionary for O(1) access instead of O(n) FirstOrDefault
                    var packageLookup = selectedPackages.ToDictionary(
                        p => p.Name, 
                        p => p, 
                        StringComparer.OrdinalIgnoreCase);

                    foreach ((string packageName, bool success, string error) in results)
                    {
                        if (packageLookup.TryGetValue(packageName, out var package) && success)
                        {
                            package.Status = "Available";
                            statusUpdates.Add((packageName, "Available", package.StatusColor));
                        }
                        // Unload failed - error handled in status reporting below
                    }

                    // Update image grid status indicators in batch
                    if (statusUpdates.Count > 0)
                    {
                        UpdateMultiplePackageStatusInImageGrid(statusUpdates);
                    }

                    // Update status for successfully unloaded packages (efficient bulk update)
                    var successfullyUnloaded = results
                        .Where(r => r.success)
                        .Select(r => r.packageName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (successfullyUnloaded.Count > 0)
                    {
                        await BulkUpdatePackageStatus(successfullyUnloaded, "Available");
                    }

                    // Re-enable UI BEFORE refreshing image grid
                    UnloadPackagesButton.IsEnabled = true;
                    UpdatePackageButtonBar();

                    // Refresh image grid to show updated package status
                    await RefreshCurrentlyDisplayedImagesAsync();

                    // Enhanced status reporting
                    var successCount = results.Count(r => r.success);
                    var failureCount = results.Count(r => !r.success);

                    if (successCount > 0 && failureCount == 0)
                    {
                        SetStatus($"Successfully unloaded {successCount} packages");
                    }
                    else if (successCount > 0 && failureCount > 0)
                    {
                        SetStatus($"Unloaded {successCount} packages ({failureCount} failed)");
                    }
                    else if (failureCount > 0)
                    {
                        SetStatus($"Failed to unload {failureCount} packages");

                        // Show detailed error for small batches
                        if (results.Count <= 5)
                        {
                            var errors = results.Where(r => !r.success).Select(r => $"{r.packageName}: {r.error}");
                            MessageBox.Show($"Unload operation failed:\n\n{string.Join("\n", errors)}",
                                          "Unload Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }

                }
                finally
                {
                    HideMainTableLoading();

                    // Re-enable UI if not already enabled
                    if (!UnloadPackagesButton.IsEnabled)
                    {
                        UnloadPackagesButton.IsEnabled = true;
                        UpdatePackageButtonBar();
                    }
                }

            }
            catch (Exception)
            {
                MessageBox.Show("Error unloading dependencies", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LoadDependencies_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return;

                // Include Available, Outdated, Archived, and external dependencies (hex color status indicates external)
                var selectedDependencies = DependenciesDataGrid.SelectedItems.Cast<DependencyItem>()
                    .Where(d => d.Status == "Available" || d.Status == "Outdated" || d.Status == "Archived" || (d.Status?.StartsWith("#") == true))
                    .ToList();

                if (selectedDependencies.Count == 0)
                {
                    MessageBox.Show("No available, outdated, archived, or external dependencies selected.", "No Dependencies",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Enhanced confirmation dialog with better information
                if (selectedDependencies.Count >= 100)
                {
                    var dependencyNames = selectedDependencies.Take(5).Select(d => d.Name).ToList();
                    var displayNames = string.Join("\n", dependencyNames);
                    if (selectedDependencies.Count > 5)
                    {
                        displayNames += $"\n... and {selectedDependencies.Count - 5} more dependencies";
                    }

                    var result = CustomMessageBox.Show(
                        $"Load {selectedDependencies.Count} dependencies?\n\nThis operation may take several minutes for large batches.\n\n{displayNames}",
                        "Confirm Load Operation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                        return;
                }

                // Disable UI during operation
                LoadDependenciesButton.IsEnabled = false;

                var externalDeps = selectedDependencies.Where(d => d.Status?.StartsWith("#") == true).ToList();
                var regularDeps = selectedDependencies.Where(d => d.Status == "Available" || d.Status == "Outdated" || d.Status == "Archived").ToList();
                var totalCount = externalDeps.Count + regularDeps.Count;

                ShowDependenciesTableLoading("Loading dependencies...", totalCount);

                try
                {
                    // Separate external dependencies (hex color status) from regular available/outdated/archived dependencies
                    
                    var dependencyNames = selectedDependencies.Select(d => d.Name).ToList();

                    await PrepareForPackageFileOperationsAsync(dependencyNames);

                    var results = new List<(string packageName, bool success, string error)>();
                    var completed = 0;

                    // Load external dependencies first using their file paths from metadata
                    foreach (var dep in externalDeps)
                    {
                        completed++;
                        UpdateDependenciesTableLoading(completed, totalCount, dep.Name);
                        SetStatus(totalCount > 1
                            ? $"Loading dependencies... {completed}/{totalCount} ({completed * 100 / totalCount}%)"
                            : $"Loading {dep.Name}...");

                        // Find metadata for this dependency by matching Creator.PackageName
                        // Get the highest version if multiple versions exist
                        var depMetadata = _packageManager?.PackageMetadata?.Values
                            .Where(p => $"{p.CreatorName}.{p.PackageName}".Equals(dep.Name, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(p => p.Version)
                            .FirstOrDefault();

                        if (depMetadata != null && !string.IsNullOrEmpty(depMetadata.FilePath))
                        {
                            var (success, error) = await _packageFileManager.LoadPackageFromExternalPathAsync(dep.Name, depMetadata.FilePath);
                            results.Add((dep.Name, success, error));
                        }
                        else
                        {
                            results.Add((dep.Name, false, "External dependency file path not found in metadata"));
                        }
                    }

                    // Load regular available dependencies
                    if (regularDeps.Count > 0)
                    {
                        var regularNames = regularDeps.Select(d => d.Name).ToList();
                        var progress = CreateStatusProgress(p =>
                        {
                            UpdateDependenciesTableLoading(completed + p.completed, totalCount, p.currentPackage);
                            SetStatus(totalCount > 1
                                ? $"Loading dependencies... {completed + p.completed}/{totalCount} ({(completed + p.completed) * 100 / totalCount}%)"
                                : $"Loading {p.currentPackage}...");
                        });

                        var regularResults = await _packageFileManager.LoadPackagesAsync(regularNames, progress);
                        results.AddRange(regularResults);
                    }

                    // Update dependency statuses based on results
                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();

                    // PERFORMANCE FIX: Pre-build lookup dictionary for O(1) access instead of O(n) FirstOrDefault
                    var dependencyLookup = selectedDependencies.ToDictionary(
                        d => d.Name, 
                        d => d, 
                        StringComparer.OrdinalIgnoreCase);

                    foreach ((string packageName, bool success, string error) in results)
                    {
                        if (dependencyLookup.TryGetValue(packageName, out var dependency) && success)
                        {
                            dependency.Status = "Loaded";
                            statusUpdates.Add((packageName, "Loaded", dependency.StatusColor));
                        }
                        if (!success)
                        {
                            // Load failed - error handled in status reporting below
                        }
                    }

                    // Update image grid status indicators in batch
                    if (statusUpdates.Count > 0)
                    {
                        UpdateMultiplePackageStatusInImageGrid(statusUpdates);
                    }

                    // Update status for successfully loaded packages (efficient bulk update)
                    var successfullyLoaded = results
                        .Where(r => r.success)
                        .Select(r => r.packageName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (successfullyLoaded.Count > 0)
                    {
                        // Refresh package status index to reflect newly loaded packages
                        _packageFileManager?.RefreshPackageStatusIndex(force: true);
                        
                        await BulkUpdatePackageStatus(successfullyLoaded, "Loaded");
                    }

                    // Refresh image grid to show newly loaded dependencies
                    await RefreshCurrentlyDisplayedImagesAsync();

                    // Enhanced status reporting
                    var successCount = results.Count(r => r.success);
                    var failureCount = results.Count(r => !r.success);

                    var throttledCount = results.Count(r => !r.success && r.error.Contains("recently performed"));
                    var actualFailureCount = failureCount - throttledCount;

                    if (successCount > 0 && actualFailureCount == 0)
                    {
                        SetStatus($"Successfully loaded {successCount} dependencies" +
                                 (throttledCount > 0 ? $" ({throttledCount} skipped - too soon)" : ""));
                    }
                    else if (successCount > 0 && actualFailureCount > 0)
                    {
                        SetStatus($"Loaded {successCount} dependencies ({actualFailureCount} failed)");
                    }
                    else if (actualFailureCount > 0)
                    {
                        SetStatus($"Failed to load {actualFailureCount} dependencies");

                        var actualErrors = results.Where(r => !r.success && !r.error.Contains("recently performed")).ToList();
                        if (actualErrors.Count > 0 && actualErrors.Count <= 5)
                        {
                            var errors = actualErrors.Select(r => $"{r.packageName}: {r.error}");
                            MessageBox.Show($"Load operation failed:\n\n{string.Join("\n", errors)}",
                                          "Load Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else if (throttledCount > 0)
                    {
                        SetStatus($"Operation skipped - please wait a moment before retrying");
                    }

                }
                finally
                {
                    HideDependenciesTableLoading();

                    // Re-enable UI
                    LoadDependenciesButton.IsEnabled = true;
                    UpdateDependenciesButtonBar();
                }
            }
            catch (Exception)
            {
                MessageBox.Show("Error during load operation", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Load operation failed");
            }
        }

        private async void UnloadDependencies_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!EnsureVamFolderSelected()) 
                {
                    return;
                }

                var selectedDependencies = DependenciesDataGrid.SelectedItems.Cast<DependencyItem>()
                    .Where(d => d.Status == "Loaded")
                    .ToList();

                if (selectedDependencies.Count == 0)
                {
                    MessageBox.Show("No loaded dependencies selected.", "No Dependencies",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Only show confirmation for large batches (100+ dependencies)
                if (selectedDependencies.Count >= 100)
                {
                    var dependencyNames = selectedDependencies.Take(10).Select(d => d.Name).ToList();
                    var displayNames = string.Join("\n", dependencyNames);
                    if (selectedDependencies.Count > 10)
                    {
                        displayNames += $"\n... and {selectedDependencies.Count - 10} more dependencies";
                    }

                    var result = CustomMessageBox.Show(
                        $"Unload {selectedDependencies.Count} dependencies?\n\n{displayNames}",
                        "Confirm Unload",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                // Disable UI during operation
                UnloadDependenciesButton.IsEnabled = false;

                ShowDependenciesTableLoading("Unloading dependencies...", selectedDependencies.Count);

                try
                {
                    // Use enhanced batch operation with progress reporting
                    var dependencyNames = selectedDependencies.Select(d => d.Name).ToList();

                    await PrepareForPackageFileOperationsAsync(dependencyNames);
                    
                    var progress = CreateStatusProgress(p =>
                    {
                        UpdateDependenciesTableLoading(p.completed, p.total, p.currentPackage);
                        SetStatus(p.total > 1
                            ? $"Unloading dependencies... {p.completed}/{p.total} ({p.completed * 100 / p.total}%)"
                            : $"Unloading {p.currentPackage}...");
                    });

                    var results = await _packageFileManager.UnloadPackagesAsync(dependencyNames, progress);

                    // Clear metadata cache to ensure new paths are picked up
                    ClearPackageMetadataCache();

                    // Update dependency statuses based on results
                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();

                    // PERFORMANCE FIX: Pre-build lookup dictionary for O(1) access instead of O(n) FirstOrDefault
                    var dependencyLookup = selectedDependencies.ToDictionary(
                        d => d.Name, 
                        d => d, 
                        StringComparer.OrdinalIgnoreCase);

                    foreach ((string packageName, bool success, string error) in results)
                    {
                        if (dependencyLookup.TryGetValue(packageName, out var dependency) && success)
                        {
                            dependency.Status = "Available";
                            statusUpdates.Add((packageName, "Available", dependency.StatusColor));
                        }
                    }

                    // Update image grid status indicators in batch
                    if (statusUpdates.Count > 0)
                    {
                        UpdateMultiplePackageStatusInImageGrid(statusUpdates);
                    }

                    // Update status for successfully unloaded packages (efficient bulk update)
                    var successfullyUnloaded = results
                        .Where(r => r.success)
                        .Select(r => r.packageName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (successfullyUnloaded.Count > 0)
                    {
                        await BulkUpdatePackageStatus(successfullyUnloaded, "Available");
                    }

                    // Refresh image grid to show updated dependency status
                    await RefreshCurrentlyDisplayedImagesAsync();

                    // Enhanced status reporting
                    var successCount = results.Count(r => r.success);
                    var failureCount = results.Count(r => !r.success);

                    if (successCount > 0 && failureCount == 0)
                    {
                        SetStatus($"Successfully unloaded {successCount} dependencies");
                    }
                    else if (successCount > 0 && failureCount > 0)
                    {
                        SetStatus($"Unloaded {successCount} dependencies ({failureCount} failed)");
                    }
                    else if (failureCount > 0)
                    {
                        SetStatus($"Failed to unload {failureCount} dependencies");

                        if (results.Count <= 5)
                        {
                            var errors = results.Where(r => !r.success).Select(r => $"{r.packageName}: {r.error}");
                            MessageBox.Show($"Unload operation failed:\n\n{string.Join("\n", errors)}",
                                          "Unload Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }

                }
                finally
                {
                    HideDependenciesTableLoading();

                    // Re-enable UI
                    UnloadDependenciesButton.IsEnabled = true;
                    UpdateDependenciesButtonBar();
                }

            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error unloading dependencies: {ex.Message}", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OptimizeDependencies_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedDependencies = DependenciesDataGrid.SelectedItems.Cast<DependencyItem>()
                    .Where(d => d.Status != "Missing" && d.Status != "Unknown")
                    .ToList();

                if (selectedDependencies.Count == 0)
                {
                    CustomMessageBox.Show("No optimizable dependencies selected.", "No Dependencies",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (selectedDependencies.Count > 500)
                {
                    CustomMessageBox.Show("Please select a maximum of 500 packages for bulk optimization.", 
                                  "Optimise Package", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var packageItems = new List<PackageItem>();
                
                foreach (var dependency in selectedDependencies)
                {
                    var baseName = dependency.Name;
                    var version = dependency.Version;
                    var fullDependencyName = !string.IsNullOrEmpty(version) ? $"{baseName}.{version}" : baseName;
                    
                    PackageItem packageItem = null;
                    
                    try
                    {
                        string resolvedPath = _packageFileManager?.ResolveDependencyToFilePath(fullDependencyName);
                        
                        if (!string.IsNullOrEmpty(resolvedPath))
                        {
                            var fileName = Path.GetFileNameWithoutExtension(resolvedPath);
                            
                            packageItem = Packages.FirstOrDefault(p => 
                                p.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                            
                            if (packageItem == null)
                            {
                                packageItem = new PackageItem
                                {
                                    Name = fileName,
                                    Status = dependency.Status
                                };
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Continue processing other dependencies
                    }
                    
                    if (packageItem != null)
                    {
                        packageItems.Add(packageItem);
                    }
                }

                if (packageItems.Count == 0)
                {
                    CustomMessageBox.Show("Could not find package information for selected dependencies.", 
                                  "No Packages Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await DisplayBulkOptimizationDialog(packageItems);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error opening optimizer: {ex.Message}", 
                              "Optimization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyMissing_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedDependencies = DependenciesDataGrid.SelectedItems.Cast<DependencyItem>()
                    .Where(d => d.Status == "Missing" || d.Status == "Unknown")
                    .ToList();

                if (selectedDependencies.Count == 0)
                {
                    MessageBox.Show("No missing or unknown dependencies selected.", "No Dependencies",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Copy dependency names to clipboard (one per line)
                var dependencyNames = selectedDependencies.Select(d => d.Name);
                var clipboardText = string.Join("\n", dependencyNames);

                Clipboard.SetText(clipboardText);

                SetStatus($"Copied {selectedDependencies.Count} missing/unknown dependency names to clipboard");
            }
            catch (Exception)
            {
                MessageBox.Show("Error copying to clipboard", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LoadAllDependencies_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return;

                // Get all available dependencies from the Dependencies collection
                var allAvailableDependencies = Dependencies
                    .Where(d => d.Status == "Available" && d.Name != "No dependencies")
                    .ToList();

                if (allAvailableDependencies.Count == 0)
                {
                    MessageBox.Show("No available dependencies to load.", "No Dependencies",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Enhanced confirmation dialog with better information
                if (allAvailableDependencies.Count >= 100)
                {
                    var dependencyNames = allAvailableDependencies.Take(5).Select(d => d.Name).ToList();
                    var displayNames = string.Join("\n", dependencyNames);
                    if (allAvailableDependencies.Count > 5)
                    {
                        displayNames += $"\n... and {allAvailableDependencies.Count - 5} more dependencies";
                    }

                    var result = CustomMessageBox.Show(
                        $"Load {allAvailableDependencies.Count} dependencies?\n\nThis operation may take several minutes for large batches.\n\n{displayNames}",
                        "Confirm Load Operation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                        return;
                }

                // Disable UI during operation
                LoadAllDependenciesButton.IsEnabled = false;

                try
                {
                    // Use enhanced batch operation with progress reporting
                    var dependencyNames = allAvailableDependencies.Select(d => d.Name).ToList();

                    await PrepareForPackageFileOperationsAsync(dependencyNames);

                    var progress = CreateStatusProgress(p =>
                    {
                        SetStatus(p.total > 1
                            ? $"Loading dependencies... {p.completed}/{p.total} ({p.completed * 100 / p.total}%)"
                            : $"Loading {p.currentPackage}...");
                    });

                    var results = await _packageFileManager.LoadPackagesAsync(dependencyNames, progress);

                    // Update dependency statuses based on results
                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();

                    foreach ((string packageName, bool success, string error) in results)
                    {
                        var dependency = allAvailableDependencies.FirstOrDefault(d => d.Name == packageName);
                        if (dependency != null && success)
                        {
                            dependency.Status = "Loaded";
                            statusUpdates.Add((packageName, "Loaded", dependency.StatusColor));
                        }
                        if (!success)
                        {
                            // Load failed - error handled in status reporting below
                        }
                    }

                    // Update image grid status indicators in batch
                    if (statusUpdates.Count > 0)
                    {
                        UpdateMultiplePackageStatusInImageGrid(statusUpdates);
                    }

                    // Update status for successfully loaded packages (efficient bulk update)
                    var successfullyLoaded = results
                        .Where(r => r.success)
                        .Select(r => r.packageName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (successfullyLoaded.Count > 0)
                    {
                        await BulkUpdatePackageStatus(successfullyLoaded, "Loaded");
                    }

                    // Refresh image grid to show newly loaded dependencies
                    await RefreshCurrentlyDisplayedImagesAsync();

                    // Enhanced status reporting
                    var successCount = results.Count(r => r.success);
                    var failureCount = results.Count(r => !r.success);

                    var throttledCount = results.Count(r => !r.success && r.error.Contains("recently performed"));
                    var actualFailureCount = failureCount - throttledCount;

                    if (successCount > 0 && actualFailureCount == 0)
                    {
                        SetStatus($"Successfully loaded {successCount} dependencies" +
                                 (throttledCount > 0 ? $" ({throttledCount} skipped - too soon)" : ""));
                    }
                    else if (successCount > 0 && actualFailureCount > 0)
                    {
                        SetStatus($"Loaded {successCount} dependencies ({actualFailureCount} failed)");
                    }
                    else if (actualFailureCount > 0)
                    {
                        SetStatus($"Failed to load {actualFailureCount} dependencies");

                        var actualErrors = results.Where(r => !r.success && !r.error.Contains("recently performed")).ToList();
                        if (actualErrors.Count > 0 && actualErrors.Count <= 5)
                        {
                            var errors = actualErrors.Select(r => $"{r.packageName}: {r.error}");
                            MessageBox.Show($"Load operation failed:\n\n{string.Join("\n", errors)}",
                                          "Load Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else if (throttledCount > 0)
                    {
                        SetStatus($"Operation skipped - please wait a moment before retrying");
                    }

                }
                finally
                {
                    // Re-enable UI
                    LoadAllDependenciesButton.IsEnabled = true;
                    UpdatePackageButtonBar();
                }
            }
            catch (Exception)
            {
                MessageBox.Show("Error during load operation", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Load operation failed");
            }
        }

        #endregion

        #region Button Bar Management

        /// <summary>
        /// Updates the visibility and state of buttons in the package button bar based on selected packages
        /// </summary>
        private void UpdatePackageButtonBar()
        {
            try
            {
                // Context-aware: if in Scenes, Presets, or Custom mode, show scene/preset info instead
                if (_currentContentMode == "Scenes" || _currentContentMode == "Presets" || _currentContentMode == "Custom")
                {
                    var selectedItems = _currentContentMode == "Scenes" 
                        ? ScenesDataGrid?.SelectedItems?.Cast<object>()?.ToList() ?? new List<object>()
                        : CustomAtomDataGrid?.SelectedItems?.Cast<object>()?.ToList() ?? new List<object>();
                    
                    PackageButtonBar.Visibility = Visibility.Visible;
                    PackageButtonGrid.Visibility = Visibility.Collapsed;
                    ArchiveOldButton.Visibility = Visibility.Collapsed;
                    FixDuplicatesButton.Visibility = Visibility.Collapsed;
                    
                    // Show Load All Dependencies button if there are available dependencies
                    var hasAvailableDependencies = Dependencies.Any(d => d.Status == "Available" && d.Name != "No dependencies");
                    LoadAllDependenciesButton.Visibility = selectedItems.Count > 0 && hasAvailableDependencies ? Visibility.Visible : Visibility.Collapsed;
                    
                    if (selectedItems.Count > 0 && hasAvailableDependencies)
                    {
                        var availableCount = Dependencies.Count(d => d.Status == "Available");
                        if (selectedItems.Count == 1)
                        {
                            LoadAllDependenciesButton.Content = availableCount == 1 
                                ? "📥 Load Dependency (Space)" 
                                : $"📥 Load All Dependencies ({availableCount}) (Space)";
                        }
                        else
                        {
                            LoadAllDependenciesButton.Content = availableCount == 1 
                                ? "📥 Load Dependency (Ctrl+Space)" 
                                : $"📥 Load All Dependencies ({availableCount}) (Ctrl+Space)";
                        }
                    }
                    
                    return;
                }

                var selectedPackages = PackageDataGrid?.SelectedItems?.Cast<PackageItem>()?.ToList() ?? new List<PackageItem>();

                // Always keep the button bar visible
                PackageButtonBar.Visibility = Visibility.Visible;

                if (selectedPackages.Count == 0)
                {
                    // Show placeholder message when no packages are selected
                    PackageButtonGrid.Visibility = Visibility.Collapsed;
                    ArchiveOldButton.Visibility = Visibility.Collapsed;
                    FixDuplicatesButton.Visibility = Visibility.Collapsed;
                    LoadAllDependenciesButton.Visibility = Visibility.Collapsed;
                    return;
                }

                // Count duplicate vs non-duplicate packages
                int duplicateCount = selectedPackages.Count(p => p.IsDuplicate);
                int nonDuplicateCount = selectedPackages.Count - duplicateCount;
                
                // Count old version packages
                int oldVersionCount = selectedPackages.Count(p => p.IsOldVersion);

                // If ANY duplicates are selected, show Fix Duplicates button
                if (duplicateCount > 0)
                {
                    PackageButtonGrid.Visibility = Visibility.Collapsed;
                    ArchiveOldButton.Visibility = Visibility.Collapsed;
                    LoadAllDependenciesButton.Visibility = Visibility.Collapsed;
                    
                    // Show Fix Duplicates button
                    FixDuplicatesButton.Visibility = Visibility.Visible;
                    FixDuplicatesButton.Content = duplicateCount == 1 
                        ? "🔧 Fix Duplicate" 
                        : $"🔧 Fix Duplicates ({duplicateCount})";
                    return;
                }

                // Otherwise show Load/Unload buttons for non-duplicates
                PackageButtonGrid.Visibility = Visibility.Visible;
                FixDuplicatesButton.Visibility = Visibility.Collapsed;
                LoadAllDependenciesButton.Visibility = Visibility.Collapsed;
                
                // If ANY old versions are selected, show Archive button
                if (oldVersionCount > 0)
                {
                    ArchiveOldButton.Visibility = Visibility.Visible;
                    ArchiveOldButton.Content = oldVersionCount == 1 
                        ? "📦 Archive" 
                        : $"📦 Archive ({oldVersionCount})";
                }
                else
                {
                    ArchiveOldButton.Visibility = Visibility.Collapsed;
                }

                // Analyze selected package statuses
                var hasLoaded = selectedPackages.Any(p => p.Status == "Loaded");
                var hasAvailable = selectedPackages.Any(p => p.Status == "Available");
                var hasExternal = selectedPackages.Any(p => p.IsExternal);

                // If no actionable packages (e.g., all Archive status), hide Load/Unload but keep Archive button visible
                if (!hasLoaded && !hasAvailable && !hasExternal)
                {
                    PackageButtonGrid.Visibility = Visibility.Collapsed;
                    LoadAllDependenciesButton.Visibility = Visibility.Collapsed;
                    return;
                }

                // Show button grid
                PackageButtonGrid.Visibility = Visibility.Visible;

                // Show Load button if any packages are Available or External
                LoadPackagesButton.Visibility = (hasAvailable || hasExternal) ? Visibility.Visible : Visibility.Collapsed;

                // Show Unload button if any packages are Loaded
                UnloadPackagesButton.Visibility = hasLoaded ? Visibility.Visible : Visibility.Collapsed;

                // Show Load +Deps button if any packages are Available or External
                LoadPackagesWithDepsButton.Visibility = (hasAvailable || hasExternal) ? Visibility.Visible : Visibility.Collapsed;

                // Check if all selected packages have the same normalized status (for keyboard shortcut hint)
                // For EXTERNAL packages, treat them as "Available" for status comparison
                var normalizedStatuses = selectedPackages.Select(p => p.IsExternal ? "Available" : p.Status).Distinct().ToList();
                bool allSameStatus = normalizedStatuses.Count == 1;

                // Update button text and tooltip to reflect count
                // Show keyboard shortcut hint consistently when all items have same status
                if (hasAvailable || hasExternal)
                {
                    // Count both available and external packages for load operations
                    var loadableCount = selectedPackages.Count(p => p.Status == "Available" || p.IsExternal);

                    // Show keyboard shortcut if all selected items have same normalized status
                    if (allSameStatus && normalizedStatuses[0] == "Available")
                    {
                        LoadPackagesButton.Content = loadableCount == 1 ? "📥 Load (Space)" : $"📥 Load ({loadableCount}) (Ctrl+Space)";
                        LoadPackagesButton.ToolTip = loadableCount == 1 ? "Load selected package" : $"Load {loadableCount} selected packages";
                        LoadPackagesWithDepsButton.Content = loadableCount == 1 ? "📥 Load +Deps (Shift+Space)" : $"📥 Load +Deps ({loadableCount}) (Shift+Space)";
                        LoadPackagesWithDepsButton.ToolTip = loadableCount == 1 ? "Load selected package and dependencies" : $"Load {loadableCount} selected packages and their dependencies";
                    }
                    else
                    {
                        // Mixed statuses - no keyboard shortcut
                        LoadPackagesButton.Content = loadableCount == 1 ? "📥 Load" : $"📥 Load ({loadableCount})";
                        LoadPackagesButton.ToolTip = $"Load {loadableCount} available/external packages";
                        LoadPackagesWithDepsButton.Content = loadableCount == 1 ? "📥 Load +Deps" : $"📥 Load +Deps ({loadableCount})";
                        LoadPackagesWithDepsButton.ToolTip = $"Load {loadableCount} available/external packages and their dependencies";
                    }
                }

                if (hasLoaded)
                {
                    var loadedCount = selectedPackages.Count(p => p.Status == "Loaded");

                    // Show keyboard shortcut if all selected items have same status
                    if (allSameStatus && normalizedStatuses[0] == "Loaded")
                    {
                        UnloadPackagesButton.Content = loadedCount == 1 ? "📤 Unload (Space)" : $"📤 Unload ({loadedCount}) (Ctrl+Space)";
                        UnloadPackagesButton.ToolTip = loadedCount == 1 ? "Unload selected package" : $"Unload {loadedCount} selected packages";
                    }
                    else
                    {
                        // Mixed statuses - no keyboard shortcut
                        UnloadPackagesButton.Content = loadedCount == 1 ? "📤 Unload" : $"📤 Unload ({loadedCount})";
                        UnloadPackagesButton.ToolTip = $"Unload {loadedCount} loaded packages";
                    }
                }

                // Update grid layout for VR-friendly button sizing
                AdjustButtonGridLayout();

            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Updates the UniformGrid layout for package buttons to prevent clipping and optimize for VR
        /// </summary>
        private void AdjustButtonGridLayout()
        {
            try
            {
                // Count all visible buttons in the UniformGrid
                var visibleButtons = 0;
                if (LoadPackagesButton.Visibility == Visibility.Visible) visibleButtons++;
                if (UnloadPackagesButton.Visibility == Visibility.Visible) visibleButtons++;
                if (LoadPackagesWithDepsButton.Visibility == Visibility.Visible) visibleButtons++;
                if (ArchiveOldButton.Visibility == Visibility.Visible) visibleButtons++;

                if (visibleButtons == 0)
                {
                    PackageButtonGrid.Columns = 1;
                    PackageButtonGrid.Rows = 1;
                    return;
                }

                // Smart layout: arrange buttons for optimal visual balance
                if (visibleButtons == 1)
                {
                    PackageButtonGrid.Columns = 1;
                    PackageButtonGrid.Rows = 1;
                }
                else if (visibleButtons == 2)
                {
                    PackageButtonGrid.Columns = 2;
                    PackageButtonGrid.Rows = 1;
                }
                else if (visibleButtons == 3)
                {
                    // 3 buttons: arrange as 2 on first row, 1 on second
                    PackageButtonGrid.Columns = 2;
                    PackageButtonGrid.Rows = 2;
                }
                else if (visibleButtons == 4)
                {
                    // 4 buttons: arrange in 2x2 grid
                    PackageButtonGrid.Columns = 2;
                    PackageButtonGrid.Rows = 2;
                }
                else
                {
                    // 5+ buttons: arrange in 2-column grid with multiple rows
                    PackageButtonGrid.Columns = 2;
                    PackageButtonGrid.Rows = (visibleButtons + 1) / 2;
                }

            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Updates the visibility and state of buttons in the dependencies button bar based on selected dependencies
        /// </summary>
        private void UpdateDependenciesButtonBar()
        {
            try
            {
                var selectedDependencies = DependenciesDataGrid?.SelectedItems?.Cast<DependencyItem>()?.ToList() ?? new List<DependencyItem>();

                if (selectedDependencies.Count == 0)
                {
                    // Hide the entire button bar when no dependencies are selected
                    DependenciesButtonBar.Visibility = Visibility.Collapsed;
                    return;
                }

                // Show the button bar
                DependenciesButtonBar.Visibility = Visibility.Visible;

                // Analyze selected dependency statuses
                var hasLoaded = selectedDependencies.Any(d => d.Status == "Loaded");
                var hasAvailable = selectedDependencies.Any(d => d.Status == "Available");
                var hasExternal = selectedDependencies.Any(d => d.Status?.StartsWith("#") == true); // Hex color = external
                var hasMissing = selectedDependencies.Any(d => d.Status == "Missing");
                var hasUnknown = selectedDependencies.Any(d => d.Status == "Unknown");
                var hasOutdated = selectedDependencies.Any(d => d.Status == "Outdated");
                var hasArchived = selectedDependencies.Any(d => d.Status == "Archived");
                var hasOptimizable = selectedDependencies.Any(d => d.Status != "Missing" && d.Status != "Unknown");

                // Check if all selected dependencies have the same status (for keyboard shortcut hint)
                var allStatuses = selectedDependencies.Select(d => d.Status).Distinct().ToList();
                bool allSameStatus = allStatuses.Count == 1;

                // Show Load button if any dependencies are Available, Outdated, Archived, or External
                // Outdated/Archived means an old version exists but can still be loaded
                LoadDependenciesButton.Visibility = (hasAvailable || hasOutdated || hasArchived || hasExternal) ? Visibility.Visible : Visibility.Collapsed;

                // Show Unload button if any dependencies are Loaded
                UnloadDependenciesButton.Visibility = hasLoaded ? Visibility.Visible : Visibility.Collapsed;

                // Show Optimize button if any dependencies are not Missing/Unknown
                OptimizeDependenciesButton.Visibility = hasOptimizable ? Visibility.Visible : Visibility.Collapsed;

                // Update button text to reflect count and keyboard shortcuts
                if (hasAvailable || hasOutdated || hasArchived || hasExternal)
                {
                    // Count available, outdated, archived, and external dependencies for load operations
                    var loadableCount = selectedDependencies.Count(d => d.Status == "Available" || d.Status == "Outdated" || d.Status == "Archived" || (d.Status?.StartsWith("#") == true));

                    // Show keyboard shortcut only if all selected items have same status AND DependenciesDataGrid has focus
                    if (allSameStatus && (allStatuses[0] == "Available" || allStatuses[0] == "Outdated" || allStatuses[0] == "Archived") && _dependenciesDataGridHasFocus)
                    {
                        LoadDependenciesButton.Content = loadableCount == 1 ? "📥 Load (Space)" : $"📥 Load ({loadableCount}) (Ctrl+Space)";
                    }
                    else
                    {
                        LoadDependenciesButton.Content = loadableCount == 1 ? "📥 Load" : $"📥 Load ({loadableCount})";
                    }
                }

                if (hasLoaded)
                {
                    var loadedCount = selectedDependencies.Count(d => d.Status == "Loaded");

                    // Show keyboard shortcut only if all selected items have same status AND DependenciesDataGrid has focus
                    if (allSameStatus && allStatuses[0] == "Loaded" && _dependenciesDataGridHasFocus)
                    {
                        UnloadDependenciesButton.Content = loadedCount == 1 ? "📤 Unload (Space)" : $"📤 Unload ({loadedCount}) (Ctrl+Space)";
                    }
                    else
                    {
                        UnloadDependenciesButton.Content = loadedCount == 1 ? "📤 Unload" : $"📤 Unload ({loadedCount})";
                    }
                }

                if (hasOptimizable)
                {
                    var optimizableCount = selectedDependencies.Count(d => d.Status != "Missing" && d.Status != "Unknown");
                    OptimizeDependenciesButton.Content = optimizableCount == 1 ? "⚡ Optimize" : $"⚡ Optimize ({optimizableCount})";
                }

                // Update layout (StackPanel handles this automatically now)
                UpdateDependenciesButtonGridLayout();

            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Updates button layout using Grid columns for proper width distribution
        /// </summary>
        private void UpdateDependenciesButtonGridLayout()
        {
            try
            {
                // Determine visibility
                bool showLoad = LoadDependenciesButton.Visibility == Visibility.Visible;
                bool showUnload = UnloadDependenciesButton.Visibility == Visibility.Visible;
                bool showOptimize = OptimizeDependenciesButton.Visibility == Visibility.Visible;

                // Hide the entire button bar if no buttons are visible
                if (!showLoad && !showUnload && !showOptimize)
                {
                    DependenciesButtonBar.Visibility = Visibility.Collapsed;
                    return;
                }

                // Reset spans and rows
                Grid.SetRow(LoadDependenciesButton, 0);
                Grid.SetRow(UnloadDependenciesButton, 0);
                Grid.SetRow(OptimizeDependenciesButton, 1);
                Grid.SetColumnSpan(LoadDependenciesButton, 1);
                Grid.SetColumnSpan(UnloadDependenciesButton, 1);
                Grid.SetColumnSpan(OptimizeDependenciesButton, 1);

                // Count top row buttons (Load/Unload)
                int topCount = (showLoad ? 1 : 0) + (showUnload ? 1 : 0);
                
                // Layout top row buttons
                if (topCount == 1)
                {
                    if (showLoad)
                    {
                        Grid.SetColumn(LoadDependenciesButton, 0);
                        Grid.SetColumnSpan(LoadDependenciesButton, 2);
                    }
                    else if (showUnload)
                    {
                        Grid.SetColumn(UnloadDependenciesButton, 0);
                        Grid.SetColumnSpan(UnloadDependenciesButton, 2);
                    }
                }
                else if (topCount == 2)
                {
                    Grid.SetColumn(LoadDependenciesButton, 0);
                    Grid.SetColumn(UnloadDependenciesButton, 1);
                }

                // Layout optimize button on second row (always spans both columns)
                if (showOptimize)
                {
                    Grid.SetColumn(OptimizeDependenciesButton, 0);
                    Grid.SetColumnSpan(OptimizeDependenciesButton, 2);
                }

                // Button alignments
                LoadDependenciesButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                UnloadDependenciesButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                OptimizeDependenciesButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Package Status Management

        /// <summary>
        /// Updates the status of specific packages based on their actual file system state
        /// </summary>
        private async Task RefreshPackageStatusesAsync(IEnumerable<string> packageNames)
        {
            if (_packageFileManager == null) return;

            try
            {
                foreach (var packageName in packageNames)
                {
                    // Find the package in our collection
                    var packageItem = Packages.FirstOrDefault(p => p.Name == packageName);
                    if (packageItem != null)
                    {
                        // Get the actual status from the file system
                        var actualStatus = await _packageFileManager.GetPackageStatusAsync(packageName);

                        await Dispatcher.InvokeAsync(() =>
                        {
                            // Update the package status if it changed
                            if (packageItem.Status != actualStatus)
                            {
                                packageItem.Status = actualStatus;
                            }
                        });
                    }

                    // Also check dependencies list
                    var dependencyItem = Dependencies.FirstOrDefault(d => d.Name == packageName);
                    if (dependencyItem != null)
                    {
                        var actualStatus = await _packageFileManager.GetPackageStatusAsync(packageName);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (dependencyItem.Status != actualStatus)
                            {
                                dependencyItem.Status = actualStatus;
                            }
                        });
                    }
                }

                // Refresh the button bars to reflect the new statuses
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdatePackageButtonBar();
                    UpdateDependenciesButtonBar();
                });
            }
            catch (Exception)
            {
            }
        }

        // Legacy synchronous wrapper
        private void RefreshPackageStatuses(IEnumerable<string> packageNames)
        {
            if (_packageFileManager == null) return;
            RefreshPackageStatusesAsync(packageNames).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Efficiently updates status for multiple packages in bulk without expensive per-package operations.
        /// IMPORTANT: This method preserves DataGrid selections across the update operation.
        /// NOTE: Does NOT sync with filters - packages remain visible even if they no longer match filters.
        /// This is intentional so users can see the status change result.
        /// </summary>
        private async Task BulkUpdatePackageStatus(List<string> packageNames, string newStatus)
        {
            if (packageNames == null || packageNames.Count == 0)
                return;

            try
            {
                // Create a HashSet for O(1) lookup
                var inputNameSet = new HashSet<string>(packageNames, StringComparer.OrdinalIgnoreCase);

                await Dispatcher.InvokeAsync(() =>
                {
                    // SELECTION PRESERVATION: Save selections before any updates
                    var savedPackageSelections = PreserveDataGridSelections();
                    var savedDependencySelections = PreserveDependenciesDataGridSelections();
                    _suppressSelectionEvents = true;

                    try
                    {
                        var affectedMetadataKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var affectedBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        // 1. Update PackageMetadata and collect affected keys/names
                        // Iterate all metadata to ensure we catch all variations (BaseName, FullName, Filename)
                        if (_packageManager?.PackageMetadata != null)
                        {
                            foreach (var kvp in _packageManager.PackageMetadata)
                            {
                                var metadata = kvp.Value;
                                var metadataKey = kvp.Key;
                                var fileNameNoExt = Path.GetFileNameWithoutExtension(metadataKey);
                                var baseName = $"{metadata.CreatorName}.{metadata.PackageName}";

                                // Check if this package matches any input name
                                // Input names can be: "Creator.Package", "Creator.Package.Version", "Creator.Package.Version.var"
                                if (inputNameSet.Contains(metadataKey) || 
                                    inputNameSet.Contains(fileNameNoExt) || 
                                    inputNameSet.Contains(baseName))
                                {
                                    // Update metadata status
                                    metadata.Status = newStatus;
                                    
                                    // Track affected identifiers
                                    affectedMetadataKeys.Add(metadataKey);
                                    affectedBaseNames.Add(baseName);
                                    
                                    // Handle #archived counterpart if it exists
                                    var archivedKey = metadataKey + "#archived";
                                    if (_packageManager.PackageMetadata.TryGetValue(archivedKey, out var archivedMetadata))
                                    {
                                        archivedMetadata.Status = newStatus;
                                        affectedMetadataKeys.Add(archivedKey);
                                    }
                                }
                            }
                        }

                        // 2. Update packages in main grid
                        foreach (var package in Packages)
                        {
                            if (affectedMetadataKeys.Contains(package.MetadataKey))
                            {
                                package.Status = newStatus;
                            }
                        }

                        // 3. Update dependencies in dependencies/dependents grid
                        foreach (var dependency in Dependencies)
                        {
                            if (affectedBaseNames.Contains(dependency.Name))
                            {
                                dependency.Status = newStatus;
                            }
                        }

                        // Update reactive filter counts without full refresh
                        if (_reactiveFilterManager != null)
                        {
                            _reactiveFilterManager.InvalidateCounts();
                            
                            if (_cascadeFiltering)
                            {
                                var currentFilters = GetSelectedFilters();
                                UpdateCascadeFilteringLive(currentFilters);
                            }
                            else
                            {
                                UpdateFilterCountsLive();
                            }
                        }

                        // NOTE: Do NOT call SyncPackageDisplayWithFilters() here!
                        // This would remove packages that no longer match filters, causing selection loss.
                        // Users should see the status change result - they can manually refresh filters if needed.

                        // SELECTION PRESERVATION: Restore selections after all updates
                        RestoreDataGridSelections(savedPackageSelections);
                        
                        // Ensure dependencies display is updated (re-sorted/filtered)
                        RefreshDependenciesDisplay();
                        
                        RestoreDependenciesDataGridSelections(savedDependencySelections);
                    }
                    finally
                    {
                        _suppressSelectionEvents = false;
                    }

                    // Update button states
                    UpdatePackageButtonBar();
                    UpdateDependenciesButtonBar();

                }, System.Windows.Threading.DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during bulk update: {ex.Message}");
            }
        }

        /// <summary>
        /// Sync package display with current filters - remove non-matching, add newly matching
        /// This is called after filter changes to keep the display in sync with active filters.
        /// NOTE: Not called during load/unload operations to preserve selection.
        /// </summary>
        private void SyncPackageDisplayWithFilters()
        {
            if (_packageManager?.PackageMetadata == null || _filterManager == null)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            // Build a set of currently displayed package keys for fast lookup
            var displayedKeys = new HashSet<string>(Packages.Select(p => p.MetadataKey), StringComparer.OrdinalIgnoreCase);
            
            // Track packages to remove and add
            var packagesToRemove = new List<PackageItem>();
            var packagesToAdd = new List<PackageItem>();
            
            // MEMORY FIX: Create filter snapshot ONCE before loops
            var filterSnapshot = _filterManager.GetSnapshot();
            
            // Check displayed packages - remove those that no longer match
            foreach (var package in Packages)
            {
                if (_packageManager.PackageMetadata.TryGetValue(package.MetadataKey, out var metadata))
                {
                    if (!_filterManager.MatchesFilters(metadata, filterSnapshot, package.MetadataKey))
                    {
                        packagesToRemove.Add(package);
                    }
                }
            }
            
            // Check all packages - add those that now match but aren't displayed
            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var metadataKey = kvp.Key;
                var metadata = kvp.Value;
                
                // Skip if already displayed
                if (displayedKeys.Contains(metadataKey))
                    continue;
                    
                // Add if it matches filters
                if (_filterManager.MatchesFilters(metadata, filterSnapshot, metadataKey))
                {
                    string packageName = metadataKey.EndsWith("#archived", StringComparison.OrdinalIgnoreCase) 
                        ? metadataKey 
                        : Path.GetFileNameWithoutExtension(metadata.Filename);
                    
                    var packageItem = new PackageItem
                    {
                        MetadataKey = metadataKey,
                        Name = packageName,
                        Status = metadata.Status,
                        Creator = metadata.CreatorName ?? "Unknown",
                        DependencyCount = metadata.Dependencies?.Length ?? 0,
                        DependentsCount = 0, // Will be calculated on full refresh
                        FileSize = metadata.FileSize,
                        ModifiedDate = metadata.ModifiedDate,
                        IsLatestVersion = true,
                        IsOptimized = metadata.IsOptimized,
                        IsDuplicate = metadata.IsDuplicate,
                        DuplicateLocationCount = metadata.DuplicateLocationCount,
                        IsOldVersion = metadata.IsOldVersion,
                        LatestVersionNumber = metadata.LatestVersionNumber,
                        ExternalDestinationName = metadata.ExternalDestinationName,
                        ExternalDestinationColorHex = metadata.ExternalDestinationColorHex
                    };
                    
                    packagesToAdd.Add(packageItem);
                }
            }
            
            // Apply changes
            foreach (var package in packagesToRemove)
            {
                Packages.Remove(package);
            }
            
            foreach (var package in packagesToAdd)
            {
                Packages.Add(package);
            }
            
        }

        #endregion

        #region Archive Old Versions

        private async Task ArchiveOldVersionsAsync(List<VarMetadata> oldVersions)
        {
            try
            {
                SetStatus($"Archiving {oldVersions.Count} old version(s)...");
                
                var oldPackagesFolder = Path.Combine(_selectedFolder, "ArchivedPackages", "OldPackages");
                
                // Run file operations on background thread to avoid blocking UI
                await Task.Run(() =>
                {
                    if (!Directory.Exists(oldPackagesFolder))
                    {
                        Directory.CreateDirectory(oldPackagesFolder);
                    }
                });
                
                // Cancel any pending image loading operations to free up file handles
                var packagesToRelease = oldVersions.Select(v => Path.GetFileNameWithoutExtension(v.FilePath)).ToList();
                await PrepareForPackageFileOperationsAsync(packagesToRelease);
                
                int movedCount = 0;
                int failedCount = 0;
                var errors = new List<string>();
                
                // Move files on background thread
                await Task.Run(() =>
                {
                    foreach (var metadata in oldVersions)
                    {
                        try
                        {
                            var sourceFile = metadata.FilePath;
                            var fileName = Path.GetFileName(sourceFile);
                            var destFile = Path.Combine(oldPackagesFolder, fileName);
                            
                            if (File.Exists(sourceFile))
                            {
                                if (File.Exists(destFile))
                                {
                                    File.Delete(destFile);
                                }

                                var sourceInfo = new FileInfo(sourceFile);
                                var originalCreationTime = sourceInfo.CreationTime;
                                var originalLastWriteTime = sourceInfo.LastWriteTime;

                                bool moved = false;
                                string lastError = null;

                                // Fast path: try rename move with short retries for transient locks.
                                for (int attempt = 1; attempt <= 5; attempt++)
                                {
                                    try
                                    {
                                        File.Move(sourceFile, destFile);
                                        moved = true;
                                        break;
                                    }
                                    catch (IOException ex) when (attempt < 5)
                                    {
                                        lastError = ex.Message;
                                        System.Threading.Thread.Sleep(50 * attempt);
                                    }
                                    catch (UnauthorizedAccessException ex) when (attempt < 5)
                                    {
                                        lastError = ex.Message;
                                        System.Threading.Thread.Sleep(50 * attempt);
                                    }
                                }

                                // Fallback: copy+delete (cross-volume or persistent lock)
                                if (!moved)
                                {
                                    File.Copy(sourceFile, destFile, overwrite: false);
                                    try
                                    {
                                        File.SetLastWriteTime(destFile, originalLastWriteTime);
                                        File.SetCreationTime(destFile, originalCreationTime);
                                    }
                                    catch
                                    {
                                    }

                                    for (int attempt = 1; attempt <= 5; attempt++)
                                    {
                                        try
                                        {
                                            File.Delete(sourceFile);
                                            moved = true;
                                            break;
                                        }
                                        catch (IOException ex) when (attempt < 5)
                                        {
                                            lastError = ex.Message;
                                            System.Threading.Thread.Sleep(50 * attempt);
                                        }
                                        catch (UnauthorizedAccessException ex) when (attempt < 5)
                                        {
                                            lastError = ex.Message;
                                            System.Threading.Thread.Sleep(50 * attempt);
                                        }
                                    }
                                }

                                if (!moved)
                                {
                                    throw new IOException(lastError ?? "Failed to move file");
                                }

                                try
                                {
                                    File.SetLastWriteTime(destFile, originalLastWriteTime);
                                }
                                catch
                                {
                                }

                                movedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            errors.Add($"{Path.GetFileName(metadata.FilePath)}: {ex.Message}");
                        }
                    }
                });
                
                SetStatus($"Archived {movedCount} old version(s). Failed: {failedCount}");
                
                var message = $"Successfully archived {movedCount} old version package(s) to:\n{oldPackagesFolder}";
                
                if (failedCount > 0)
                {
                    message += $"\n\nFailed to archive {failedCount} package(s):\n" + string.Join("\n", errors.Take(5));
                    if (errors.Count > 5)
                    {
                        message += $"\n... and {errors.Count - 5} more";
                    }
                }
                
                DarkMessageBox.Show(message, "Archive Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                
                RefreshPackages();
                
                // Refresh image grid to show updated package status
                await RefreshCurrentlyDisplayedImagesAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"Error archiving old versions: {ex.Message}");
                DarkMessageBox.Show($"Failed to archive old versions: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LoadPackageFromHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PackageItem packageItem)
            {
                await LoadSinglePackageAsync(packageItem, null, null);
            }
        }

        private async void UnloadPackageFromHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PackageItem packageItem)
            {
                await UnloadSinglePackageAsync(packageItem, null, null);
            }
        }

        #endregion
    }
}

