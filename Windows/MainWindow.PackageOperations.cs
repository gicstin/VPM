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

        #region Package Load/Unload Operations

        private async void LoadPackages_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return;

                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>()
                    .Where(p => p.Status == "Available")
                    .ToList();

                if (selectedPackages.Count == 0)
                {
                    MessageBox.Show("No available packages selected.", "No Packages",
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
                var originalCursor = Cursor;
                Cursor = Cursors.Wait;

                try
                {
                    // Use enhanced batch operation with progress reporting
                    var packageNames = selectedPackages.Select(p => p.Name).ToList();
                    var progress = new Progress<(int completed, int total, string currentPackage)>(p =>
                    {
                        // Update status with progress
                        SetStatus(p.total > 1
                            ? $"Loading packages... {p.completed}/{p.total} ({p.completed * 100 / p.total}%)"
                            : $"Loading {p.currentPackage}...");
                    });

                    var results = await _packageFileManager.LoadPackagesAsync(packageNames, progress);

                    // Update package statuses based on results
                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();

                    foreach ((string packageName, bool success, string error) in results)
                    {
                        var package = selectedPackages.FirstOrDefault(p => p.Name == packageName);
                        if (package != null && success)
                        {
                            package.Status = "Loaded";
                            statusUpdates.Add((packageName, "Loaded", package.StatusColor));
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
                    // Re-enable UI
                    LoadPackagesButton.IsEnabled = true;
                    Cursor = originalCursor;
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

        private async void LoadPackagesWithDeps_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return;

                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>()
                    .Where(p => p.Status == "Available")
                    .ToList();

                if (selectedPackages.Count == 0)
                {
                    MessageBox.Show("No available packages selected.", "No Packages",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var packagesToLoad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var package in selectedPackages)
                {
                    packagesToLoad.Add(package.Name);

                    _packageManager.PackageMetadata.TryGetValue(package.MetadataKey, out var packageMetadata);
                    if (packageMetadata?.Dependencies != null)
                    {
                        foreach (var dependency in packageMetadata.Dependencies)
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

                var availableDependencies = allDependencies
                    .Where(d => _packageFileManager?.GetPackageStatus(d) == "Available")
                    .ToList();

                var totalToLoad = packagesToLoad.Count + availableDependencies.Count;

                if (totalToLoad == 0)
                {
                    MessageBox.Show("No packages or dependencies available to load.", "No Packages",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (totalToLoad >= 50)
                {
                    var displayPackages = packagesToLoad.Take(3).ToList();
                    var displayDeps = availableDependencies.Take(3).ToList();
                    var displayText = "Packages:\n" + string.Join("\n", displayPackages);
                    if (packagesToLoad.Count > 3)
                        displayText += $"\n... and {packagesToLoad.Count - 3} more";
                    
                    if (availableDependencies.Count > 0)
                    {
                        displayText += "\n\nDependencies:\n" + string.Join("\n", displayDeps);
                        if (availableDependencies.Count > 3)
                            displayText += $"\n... and {availableDependencies.Count - 3} more";
                    }

                    var result = CustomMessageBox.Show(
                        $"Load {packagesToLoad.Count} packages + {availableDependencies.Count} dependencies ({totalToLoad} total)?\n\nThis operation may take several minutes.\n\n{displayText}",
                        "Confirm Load Operation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                        return;
                }

                LoadPackagesButton.IsEnabled = false;
                LoadPackagesWithDepsButton.IsEnabled = false;
                var originalCursor = Cursor;
                Cursor = Cursors.Wait;

                try
                {
                    var allPackagesToLoad = new List<string>(packagesToLoad);
                    allPackagesToLoad.AddRange(availableDependencies);

                    var progress = new Progress<(int completed, int total, string currentPackage)>(p =>
                    {
                        SetStatus(p.total > 1
                            ? $"Loading packages and dependencies... {p.completed}/{p.total} ({p.completed * 100 / p.total}%)"
                            : $"Loading {p.currentPackage}...");
                    });

                    var results = await _packageFileManager.LoadPackagesAsync(allPackagesToLoad, progress);

                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();

                    foreach ((string packageName, bool success, string error) in results)
                    {
                        var package = selectedPackages.FirstOrDefault(p => p.Name == packageName);
                        if (package != null && success)
                        {
                            package.Status = "Loaded";
                            statusUpdates.Add((packageName, "Loaded", package.StatusColor));
                        }
                    }

                    if (statusUpdates.Count > 0)
                    {
                        UpdateMultiplePackageStatusInImageGrid(statusUpdates);
                    }

                    var successfullyLoaded = results
                        .Where(r => r.success)
                        .Select(r => r.packageName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (successfullyLoaded.Count > 0)
                    {
                        await BulkUpdatePackageStatus(successfullyLoaded, "Loaded");
                    }

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
                    LoadPackagesButton.IsEnabled = true;
                    LoadPackagesWithDepsButton.IsEnabled = true;
                    Cursor = originalCursor;
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

        private async void UnloadPackages_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!EnsureVamFolderSelected()) return;

                var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>()
                    .Where(p => p.Status == "Loaded")
                    .ToList();



                if (selectedPackages.Count == 0)
                {
                    MessageBox.Show("No loaded packages selected.", "No Packages",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Only show confirmation for large batches (100+ packages)
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

                // Disable UI during operation
                UnloadPackagesButton.IsEnabled = false;
                var originalCursor = Cursor;
                Cursor = Cursors.Wait;

                try
                {
                    // Use enhanced batch operation with progress reporting
                    var packageNames = selectedPackages.Select(p => p.Name).ToList();
                    var progress = new Progress<(int completed, int total, string currentPackage)>(p =>
                    {
                        // Update status with progress
                        SetStatus(p.total > 1
                            ? $"Unloading packages... {p.completed}/{p.total} ({p.completed * 100 / p.total}%)"
                            : $"Unloading {p.currentPackage}...");
                    });

                    var results = await _packageFileManager.UnloadPackagesAsync(packageNames, progress);

                    // Update package statuses based on results
                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();

                    foreach ((string packageName, bool success, string error) in results)
                    {
                        var package = selectedPackages.FirstOrDefault(p => p.Name == packageName);
                        if (package != null && success)
                        {
                            package.Status = "Available";
                            statusUpdates.Add((packageName, "Available", package.StatusColor));
                        }
                        if (!success)
                        {
                            // Unload failed - error handled in status reporting below
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
                    // Re-enable UI
                    UnloadPackagesButton.IsEnabled = true;
                    Cursor = originalCursor;
                    UpdatePackageButtonBar();
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

                var selectedDependencies = DependenciesDataGrid.SelectedItems.Cast<DependencyItem>()
                    .Where(d => d.Status == "Available")
                    .ToList();

                if (selectedDependencies.Count == 0)
                {
                    MessageBox.Show("No available dependencies selected.", "No Dependencies",
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
                var originalCursor = Cursor;
                Cursor = Cursors.Wait;

                try
                {
                    // Use enhanced batch operation with progress reporting
                    var dependencyNames = selectedDependencies.Select(d => d.Name).ToList();
                    var progress = new Progress<(int completed, int total, string currentPackage)>(p =>
                    {
                        // Update status with progress
                        SetStatus(p.total > 1
                            ? $"Loading dependencies... {p.completed}/{p.total} ({p.completed * 100 / p.total}%)"
                            : $"Loading {p.currentPackage}...");
                    });

                    var results = await _packageFileManager.LoadPackagesAsync(dependencyNames, progress);

                    // Update dependency statuses based on results
                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();

                    foreach ((string packageName, bool success, string error) in results)
                    {
                        var dependency = selectedDependencies.FirstOrDefault(d => d.Name == packageName);
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
                    LoadDependenciesButton.IsEnabled = true;
                    Cursor = originalCursor;
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
                if (!EnsureVamFolderSelected()) return;

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
                        return;
                }

                // Disable UI during operation
                UnloadDependenciesButton.IsEnabled = false;
                var originalCursor = Cursor;
                Cursor = Cursors.Wait;

                try
                {
                    // Use enhanced batch operation with progress reporting
                    var dependencyNames = selectedDependencies.Select(d => d.Name).ToList();
                    var progress = new Progress<(int completed, int total, string currentPackage)>(p =>
                    {
                        // Update status with progress
                        SetStatus(p.total > 1
                            ? $"Unloading dependencies... {p.completed}/{p.total} ({p.completed * 100 / p.total}%)"
                            : $"Unloading {p.currentPackage}...");
                    });

                    var results = await _packageFileManager.UnloadPackagesAsync(dependencyNames, progress);

                    // Update dependency statuses based on results
                    var statusUpdates = new List<(string packageName, string status, Color statusColor)>();

                    foreach ((string packageName, bool success, string error) in results)
                    {
                        var dependency = selectedDependencies.FirstOrDefault(d => d.Name == packageName);
                        if (dependency != null && success)
                        {
                            dependency.Status = "Available";
                            statusUpdates.Add((packageName, "Available", dependency.StatusColor));
                        }
                        if (!success)
                        {
                            // Unload failed - error handled in status reporting below
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
                    // Re-enable UI
                    UnloadDependenciesButton.IsEnabled = true;
                    Cursor = originalCursor;
                    UpdateDependenciesButtonBar();
                }

            }
            catch (Exception)
            {
                MessageBox.Show("Error unloading dependencies", "Error",
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
                    catch (Exception ex)
                    {
                        // Log error for debugging but continue processing other dependencies
                        Console.WriteLine($"[DEBUG] Error resolving dependency '{fullDependencyName}': {ex.Message}");
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

        #endregion

        #region Button Bar Management

        /// <summary>
        /// Updates the visibility and state of buttons in the package button bar based on selected packages
        /// </summary>
        private void UpdatePackageButtonBar()
        {
            try
            {
                // Context-aware: if in Scenes mode, show scene info instead
                if (_currentContentMode == "Scenes")
                {
                    var selectedScenes = ScenesDataGrid?.SelectedItems?.Cast<SceneItem>()?.ToList() ?? new List<SceneItem>();
                    
                    PackageButtonBar.Visibility = Visibility.Visible;
                    PackageButtonGrid.Visibility = Visibility.Collapsed;
                    ArchiveOldButton.Visibility = Visibility.Collapsed;
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
                    return;
                }

                // Count duplicate vs non-duplicate packages
                int duplicateCount = selectedPackages.Count(p => p.Status == "Duplicate");
                int nonDuplicateCount = selectedPackages.Count - duplicateCount;

                // If ANY duplicates are selected, show Load/Unload buttons
                if (duplicateCount > 0)
                {
                    PackageButtonGrid.Visibility = Visibility.Visible;
                    ArchiveOldButton.Visibility = Visibility.Collapsed;
                    return;
                }

                // Otherwise show Load/Unload buttons for non-duplicates
                PackageButtonGrid.Visibility = Visibility.Visible;
                
                // Count old version packages
                int oldVersionCount = selectedPackages.Count(p => p.IsOldVersion);
                
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

                // If no actionable packages (e.g., all Archive status), hide Load/Unload but keep Archive button visible
                if (!hasLoaded && !hasAvailable)
                {
                    PackageButtonGrid.Visibility = Visibility.Collapsed;
                    return;
                }

                // Show button grid
                PackageButtonGrid.Visibility = Visibility.Visible;

                // Show Load button if any packages are Available
                LoadPackagesButton.Visibility = hasAvailable ? Visibility.Visible : Visibility.Collapsed;

                // Show Unload button if any packages are Loaded
                UnloadPackagesButton.Visibility = hasLoaded ? Visibility.Visible : Visibility.Collapsed;

                // Show Load +Deps button if any packages are Available
                LoadPackagesWithDepsButton.Visibility = hasAvailable ? Visibility.Visible : Visibility.Collapsed;

                // Check if all selected packages have the same status (for keyboard shortcut hint)
                var allStatuses = selectedPackages.Select(p => p.Status).Distinct().ToList();
                bool allSameStatus = allStatuses.Count == 1;

                // Update button text and tooltip to reflect count
                // Show keyboard shortcut hint consistently when all items have same status
                if (hasAvailable)
                {
                    var availableCount = selectedPackages.Count(p => p.Status == "Available");

                    // Show keyboard shortcut if all selected items have same status
                    if (allSameStatus && allStatuses[0] == "Available")
                    {
                        LoadPackagesButton.Content = availableCount == 1 ? "📥 Load (Space)" : $"📥 Load ({availableCount}) (Ctrl+Space)";
                        LoadPackagesButton.ToolTip = availableCount == 1 ? "Load selected package" : $"Load {availableCount} selected packages";
                        LoadPackagesWithDepsButton.Content = availableCount == 1 ? "📥 Load +Deps (Shift+Space)" : $"📥 Load +Deps ({availableCount}) (Shift+Space)";
                        LoadPackagesWithDepsButton.ToolTip = availableCount == 1 ? "Load selected package and dependencies" : $"Load {availableCount} selected packages and their dependencies";
                    }
                    else
                    {
                        // Mixed statuses - no keyboard shortcut
                        LoadPackagesButton.Content = availableCount == 1 ? "📥 Load" : $"📥 Load ({availableCount})";
                        LoadPackagesButton.ToolTip = $"Load {availableCount} available packages";
                        LoadPackagesWithDepsButton.Content = availableCount == 1 ? "📥 Load +Deps" : $"📥 Load +Deps ({availableCount})";
                        LoadPackagesWithDepsButton.ToolTip = $"Load {availableCount} available packages and their dependencies";
                    }
                }

                if (hasLoaded)
                {
                    var loadedCount = selectedPackages.Count(p => p.Status == "Loaded");

                    // Show keyboard shortcut if all selected items have same status
                    if (allSameStatus && allStatuses[0] == "Loaded")
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
                var hasMissing = selectedDependencies.Any(d => d.Status == "Missing");
                var hasUnknown = selectedDependencies.Any(d => d.Status == "Unknown");
                var hasOptimizable = selectedDependencies.Any(d => d.Status != "Missing" && d.Status != "Unknown");

                // Check if all selected dependencies have the same status (for keyboard shortcut hint)
                var allStatuses = selectedDependencies.Select(d => d.Status).Distinct().ToList();
                bool allSameStatus = allStatuses.Count == 1;

                // Show Load button if any dependencies are Available
                LoadDependenciesButton.Visibility = hasAvailable ? Visibility.Visible : Visibility.Collapsed;

                // Show Unload button if any dependencies are Loaded
                UnloadDependenciesButton.Visibility = hasLoaded ? Visibility.Visible : Visibility.Collapsed;

                // Show Optimize button if any dependencies are not Missing/Unknown
                OptimizeDependenciesButton.Visibility = hasOptimizable ? Visibility.Visible : Visibility.Collapsed;

                // Update button text to reflect count and keyboard shortcuts
                if (hasAvailable)
                {
                    var availableCount = selectedDependencies.Count(d => d.Status == "Available");

                    // Show keyboard shortcut only if all selected items have same status AND DependenciesDataGrid has focus
                    if (allSameStatus && allStatuses[0] == "Available" && _dependenciesDataGridHasFocus)
                    {
                        LoadDependenciesButton.Content = availableCount == 1 ? "📥 Load (Space)" : $"📥 Load ({availableCount}) (Ctrl+Space)";
                    }
                    else
                    {
                        LoadDependenciesButton.Content = availableCount == 1 ? "📥 Load" : $"📥 Load ({availableCount})";
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
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.WriteLine($"[Warning] Failed to refresh package statuses: {ex.Message}");
            }
        }

        // Legacy synchronous wrapper
        private void RefreshPackageStatuses(IEnumerable<string> packageNames)
        {
            if (_packageFileManager == null) return;
            RefreshPackageStatusesAsync(packageNames).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Efficiently updates status for multiple packages in bulk without expensive per-package operations
        /// </summary>
        private async Task BulkUpdatePackageStatus(List<string> packageNames, string newStatus)
        {
            if (packageNames == null || packageNames.Count == 0)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Create a HashSet for O(1) lookup
                var packageNameSet = new HashSet<string>(packageNames, StringComparer.OrdinalIgnoreCase);
                int updatedCount = 0;

                await Dispatcher.InvokeAsync(() =>
                {
                    // Update packages in main grid
                    foreach (var package in Packages)
                    {
                        if (packageNameSet.Contains(package.Name))
                        {
                            package.Status = newStatus;
                            updatedCount++;
                        }
                    }

                    // Update dependencies in dependencies grid
                    foreach (var dependency in Dependencies)
                    {
                        if (packageNameSet.Contains(dependency.Name))
                        {
                            dependency.Status = newStatus;
                        }
                    }

                    // Update PackageMetadata status for reactive filtering
                    if (_packageManager?.PackageMetadata != null)
                    {
                        foreach (var packageName in packageNameSet)
                        {
                            // Try both regular and archived keys
                            if (_packageManager.PackageMetadata.TryGetValue(packageName, out var metadata))
                            {
                                metadata.Status = newStatus;
                            }
                            
                            var archivedKey = packageName + "#archived";
                            if (_packageManager.PackageMetadata.TryGetValue(archivedKey, out var archivedMetadata))
                            {
                                archivedMetadata.Status = newStatus;
                            }
                        }
                    }

                }, System.Windows.Threading.DispatcherPriority.Normal);
                
                var selectedNames = await Dispatcher.InvokeAsync(() => PreserveDataGridSelections());
                
                await Dispatcher.InvokeAsync(() =>
                {
                    _suppressSelectionEvents = true;
                    try
                    {
                        PackageDataGrid.Items.Refresh();

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

                        SyncPackageDisplayWithFilters();

                        UpdatePackageButtonBar();
                        UpdateDependenciesButtonBar();
                    }
                    finally
                    {
                        _suppressSelectionEvents = false;
                    }

                }, System.Windows.Threading.DispatcherPriority.Normal);
                
                await Dispatcher.InvokeAsync(() =>
                {
                    _suppressSelectionEvents = true;
                    RestoreDataGridSelections(selectedNames);
                }, System.Windows.Threading.DispatcherPriority.Background);
                
                await Dispatcher.InvokeAsync(() =>
                {
                    _suppressSelectionEvents = false;
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during bulk update: {ex.Message}");
            }
        }

        /// <summary>
        /// Sync package display with current filters - remove non-matching, add newly matching
        /// This is called after status changes to keep the display in sync with active filters
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
            
            // Check displayed packages - remove those that no longer match
            foreach (var package in Packages)
            {
                if (_packageManager.PackageMetadata.TryGetValue(package.MetadataKey, out var metadata))
                {
                    if (!_filterManager.MatchesFilters(metadata))
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
                if (_filterManager.MatchesFilters(metadata))
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
                        DependencyCount = metadata.Dependencies?.Count ?? 0,
                        DependentsCount = 0, // Will be calculated on full refresh
                        FileSize = metadata.FileSize,
                        ModifiedDate = metadata.ModifiedDate,
                        IsLatestVersion = true,
                        IsOptimized = metadata.IsOptimized,
                        IsDuplicate = metadata.IsDuplicate,
                        DuplicateLocationCount = metadata.DuplicateLocationCount,
                        IsOldVersion = metadata.IsOldVersion,
                        LatestVersionNumber = metadata.LatestVersionNumber
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
            
            if (packagesToRemove.Count > 0 || packagesToAdd.Count > 0)
            {
                Console.WriteLine($"[SYNC-DISPLAY] Removed {packagesToRemove.Count}, Added {packagesToAdd.Count} packages ({sw.ElapsedMilliseconds}ms)");
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
                                
                                File.Move(sourceFile, destFile);
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
            }
            catch (Exception ex)
            {
                SetStatus($"Error archiving old versions: {ex.Message}");
                DarkMessageBox.Show($"Failed to archive old versions: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}

