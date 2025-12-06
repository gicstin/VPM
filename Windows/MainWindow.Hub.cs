using System;
using System.Linq;
using System.Windows;
using VPM.Windows;

namespace VPM
{
    /// <summary>
    /// Hub-related functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Opens the Hub Browser window
        /// </summary>
        private void HubBrowser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Get the destination folder (AddonPackages or AllPackages)
                var destinationFolder = GetHubDownloadFolder();
                
                // Get dictionary of local package names to their file paths for library status checking
                var localPackagePaths = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (Packages != null && _packageManager?.PackageMetadata != null)
                {
                    foreach (var package in Packages)
                    {
                        if (_packageManager.PackageMetadata.TryGetValue(package.MetadataKey, out var metadata) 
                            && !string.IsNullOrEmpty(metadata.FilePath))
                        {
                            // Use package name (without .var) as key
                            var name = package.Name.EndsWith(".var", StringComparison.OrdinalIgnoreCase) 
                                ? package.Name.Substring(0, package.Name.Length - 4) 
                                : package.Name;
                            if (!localPackagePaths.ContainsKey(name))
                            {
                                localPackagePaths[name] = metadata.FilePath;
                            }
                        }
                    }
                }
                
                var hubWindow = new HubBrowserWindow(destinationFolder, localPackagePaths, _packageManager, _settingsManager);
                hubWindow.Owner = this;
                hubWindow.ShowDialog();
                
                // Refresh packages after closing Hub browser (in case new packages were downloaded)
                RefreshPackagesAfterHubDownload();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open Hub Browser:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Check for package updates from Hub
        /// </summary>
        private async void HubCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetStatus("Checking Hub for updates...");
                
                using var hubService = new Services.HubService();
                
                // Load packages.json from Hub
                var loaded = await hubService.LoadPackagesJsonAsync();
                if (!loaded)
                {
                    SetStatus("Failed to load Hub package index");
                    return;
                }
                
                // Check each local package for updates
                int updatesFound = 0;
                foreach (var package in Packages ?? Enumerable.Empty<Models.PackageItem>())
                {
                    var groupName = GetPackageGroupName(package.Name);
                    var localVersion = ExtractVersion(package.Name);
                    
                    if (localVersion > 0 && hubService.HasUpdate(groupName, localVersion))
                    {
                        updatesFound++;
                        // Mark package as having update available
                        package.Status = "Outdated";
                    }
                }
                
                if (updatesFound > 0)
                {
                    SetStatus($"Found {updatesFound} package(s) with updates available");
                    MessageBox.Show($"Found {updatesFound} package(s) with updates available on Hub.\n\n" +
                        "Use Hub > Browse Hub to download updates.", "Updates Available",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    SetStatus("All packages are up to date");
                    MessageBox.Show("All packages are up to date!", "No Updates",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Error checking updates: {ex.Message}");
                MessageBox.Show($"Failed to check for updates:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Find and download missing dependencies from Hub
        /// </summary>
        private async void HubMissingDeps_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Get missing dependencies from the current view
                var missingDeps = Dependencies?
                    .Where(d => d.Status == "Missing" || d.Status == "Not Found")
                    .Select(d => d.Name)
                    .Distinct()
                    .ToList();
                
                if (missingDeps == null || !missingDeps.Any())
                {
                    MessageBox.Show("No missing dependencies found.\n\n" +
                        "Select a package first to see its dependencies.", "No Missing Dependencies",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                SetStatus($"Searching Hub for {missingDeps.Count} missing dependencies...");
                
                using var hubService = new Services.HubService();
                
                // Find packages on Hub
                var foundPackages = await hubService.FindPackagesAsync(missingDeps);
                
                var downloadable = foundPackages.Values.Where(p => !p.NotOnHub).ToList();
                var notFound = missingDeps.Count - downloadable.Count;
                
                if (downloadable.Any())
                {
                    var message = $"Found {downloadable.Count} of {missingDeps.Count} missing dependencies on Hub.";
                    if (notFound > 0)
                    {
                        message += $"\n\n{notFound} package(s) are not available on Hub.";
                    }
                    message += "\n\nWould you like to open the Hub Browser to download them?";
                    
                    var result = MessageBox.Show(message, "Missing Dependencies Found",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        HubBrowser_Click(sender, e);
                    }
                }
                else
                {
                    MessageBox.Show($"None of the {missingDeps.Count} missing dependencies were found on Hub.\n\n" +
                        "They may be from external sources or no longer available.", "Not Found on Hub",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                
                SetStatus("Ready");
            }
            catch (Exception ex)
            {
                SetStatus($"Error finding dependencies: {ex.Message}");
                MessageBox.Show($"Failed to find missing dependencies:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Get the folder where Hub downloads should be saved
        /// </summary>
        private string GetHubDownloadFolder()
        {
            // Use AddonPackages folder if available, otherwise use AllPackages
            if (!string.IsNullOrEmpty(_settingsManager?.Settings?.SelectedFolder))
            {
                var addonPackages = System.IO.Path.Combine(_settingsManager.Settings.SelectedFolder, "AddonPackages");
                if (System.IO.Directory.Exists(addonPackages))
                {
                    return addonPackages;
                }
                
                // Try AllPackages as fallback
                var allPackages = System.IO.Path.Combine(_settingsManager.Settings.SelectedFolder, "AllPackages");
                if (System.IO.Directory.Exists(allPackages))
                {
                    return allPackages;
                }
                
                // Create AddonPackages if neither exists
                System.IO.Directory.CreateDirectory(addonPackages);
                return addonPackages;
            }
            
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
        }

        /// <summary>
        /// Refresh packages after downloading from Hub
        /// </summary>
        private void RefreshPackagesAfterHubDownload()
        {
            // Trigger a refresh to pick up newly downloaded packages
            try
            {
                SetStatus("Refreshing packages after Hub download...");
                RefreshPackages();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow.Hub] Error refreshing after Hub download: {ex.Message}");
                SetStatus("Ready - refresh to see new packages");
            }
        }

        /// <summary>
        /// Extract version number from package name
        /// </summary>
        private static int ExtractVersion(string packageName)
        {
            var name = packageName?.Replace(".var", "") ?? "";
            
            for (int i = name.Length - 1; i >= 0; i--)
            {
                if (name[i] == '.')
                {
                    if (i + 1 < name.Length)
                    {
                        var afterDot = name.Substring(i + 1);
                        if (int.TryParse(afterDot, out var version))
                        {
                            return version;
                        }
                    }
                }
            }
            
            return -1;
        }

        /// <summary>
        /// Get package group name (without version)
        /// </summary>
        private static string GetPackageGroupName(string packageName)
        {
            var name = packageName?.Replace(".var", "") ?? "";
            
            for (int i = name.Length - 1; i >= 0; i--)
            {
                if (name[i] == '.')
                {
                    if (i + 1 < name.Length)
                    {
                        var afterDot = name.Substring(i + 1);
                        if (int.TryParse(afterDot, out _))
                        {
                            return name.Substring(0, i);
                        }
                    }
                }
            }
            
            return name;
        }
    }
}
