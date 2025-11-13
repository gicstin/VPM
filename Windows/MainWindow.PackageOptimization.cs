using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VPM.Models;
using VPM.Services;
using Path = System.IO.Path;

namespace VPM
{
    /// <summary>
    /// Package optimization functionality for MainWindow (Textures and Hair)
    /// </summary>
    public partial class MainWindow
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        
        // UI Constants are defined in MainWindow.xaml.cs

        /// <summary>
        /// Gets the file path for a package
        /// </summary>
        private string GetPackagePath(PackageItem package)
        {
            try
            {
                if (_packageFileManager == null)
                {
                    return null;
                }

                // Use MetadataKey for accurate lookup (handles multiple versions of same package)
                // MetadataKey is the actual filename from the metadata dictionary
                string lookupKey = !string.IsNullOrEmpty(package.MetadataKey) ? package.MetadataKey : package.Name;
                
                // Try to get the package file info
                var fileInfo = _packageFileManager.GetPackageFileInfo(lookupKey);
                if (fileInfo != null && !string.IsNullOrEmpty(fileInfo.CurrentPath))
                {
                    // Check if it's a .var file
                    if (System.IO.File.Exists(fileInfo.CurrentPath))
                    {
                        return fileInfo.CurrentPath;
                    }
                }

                // Check if there's an unarchived folder version
                if (!string.IsNullOrEmpty(_selectedFolder))
                {
                    string unarchivedPath = System.IO.Path.Combine(_selectedFolder, "AddonPackages", lookupKey);
                    if (System.IO.Directory.Exists(unarchivedPath))
                    {
                        return unarchivedPath;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Converts and repackages textures in a VAR file
        /// </summary>
        private async System.Threading.Tasks.Task ConvertAndRepackageTextures(string packageName, List<TextureValidator.TextureInfo> textures, Window parentDialog)
        {
            try
            {
                // Get selected textures for conversion
                var selectedTextures = textures.Where(t => t.HasConversionSelected).ToList();
                
                if (selectedTextures.Count == 0)
                {
                    MessageBox.Show("No textures selected for conversion.", "No Selection", 
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Get package path
                var pkgInfo = _packageFileManager?.GetPackageFileInfo(packageName);
                if (pkgInfo == null || string.IsNullOrEmpty(pkgInfo.CurrentPath))
                {
                    MessageBox.Show($"Could not find package: {packageName}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string packagePath = pkgInfo.CurrentPath;
                
                // Only support VAR files (not unarchived folders)
                if (!packagePath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Texture conversion is only supported for .var files, not unarchived packages.", 
                                  "Unsupported Format", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Get VAM root folder for ArchivedPackages
                string packageDirectory = Path.GetDirectoryName(packagePath);
                string vamRootFolder = Path.GetDirectoryName(packageDirectory); // Go up one level from AddonPackages
                string archivedFolder = Path.Combine(vamRootFolder, "ArchivedPackages");
                
                // Check if package is already optimized (exists in archive)
                bool isAlreadyOptimized = false;
                string archiveFilePath = Path.Combine(archivedFolder, Path.GetFileName(packagePath));
                if (File.Exists(archiveFilePath))
                {
                    isAlreadyOptimized = true;
                }
                
                // Confirm conversion
                string confirmMessage;
                if (isAlreadyOptimized)
                {
                    confirmMessage = $"Re-optimize {selectedTextures.Count} texture(s) in {Path.GetFileName(packagePath)}?\n\n" +
                                   " Original preserved in: ArchivedPackages folder\n" +
                                   " New optimized version replaces current\n" +
                                   " Description updated with conversion info\n\n" +
                                   "This may take a few minutes...";
                }
                else
                {
                    confirmMessage = $"Convert {selectedTextures.Count} texture(s) in {Path.GetFileName(packagePath)}?\n\n" +
                                   " Original saved to: ArchivedPackages folder\n" +
                                   " Optimized version replaces original\n" +
                                   " Description updated with conversion info\n\n" +
                                   "This may take a few minutes...";
                }
                
                var result = MessageBox.Show(
                    confirmMessage,
                    isAlreadyOptimized ? "Confirm Re-optimization" : "Confirm Conversion",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;
                
                // Check if we're reading from archive and file exists in main folder
                if (isAlreadyOptimized)
                {
                    // Check if optimized file already exists in AddonPackages
                    string addonPackagesFolder = Path.Combine(vamRootFolder, "AddonPackages");
                    string existingOptimizedPath = Path.Combine(addonPackagesFolder, Path.GetFileName(packagePath));
                    
                    if (File.Exists(existingOptimizedPath))
                    {
                        var overwriteResult = ShowFileConflictDialog(Path.GetFileName(packagePath));
                        
                        if (overwriteResult == MessageBoxResult.Cancel)
                        {
                            return;
                        }
                        else if (overwriteResult == MessageBoxResult.No)
                        {
                            // Will create timestamped version - handled in VarRepackager
                        }
                        else // Yes - overwrite
                        {
                            // Delete the existing file so it can be overwritten
                            try
                            {
                                File.Delete(existingOptimizedPath);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Could not delete existing file:\n{ex.Message}", "Error", 
                                              MessageBoxButton.OK, MessageBoxImage.Error);
                                return;
                            }
                        }
                    }
                }

                // Close the texture validation dialog BEFORE starting conversion
                // This ensures any file handles are released
                parentDialog.Close();
                
                // Small delay to ensure dialog cleanup completes
                await System.Threading.Tasks.Task.Delay(200);

                // Create progress dialog
                var progressDialog = CreateProgressDialog(this); // Use main window as owner now
                var progressText = progressDialog.Content as Grid;
                var progressTextBlock = progressText?.Children.OfType<TextBlock>().FirstOrDefault();
                var progressBar = progressText?.Children.OfType<ProgressBar>().FirstOrDefault();

                progressDialog.Show();

                try
                {
                    // Build conversion dictionary with texture details
                    var conversions = new Dictionary<string, (string targetResolution, int originalWidth, int originalHeight, long originalSize)>();
                    foreach (var texture in selectedTextures)
                    {
                        string targetResolution = texture.ConvertTo8K ? "8K" :
                                                texture.ConvertTo4K ? "4K" :
                                                texture.ConvertTo2K ? "2K" : "2K";
                        conversions[texture.ReferencedPath] = (targetResolution, texture.Width, texture.Height, texture.FileSize);
                    }

                    // Perform conversion in background
                    string outputPath = null;
                    long originalPackageSize = 0;
                    long newPackageSize = 0;
                    int texturesConverted = 0;
                    
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        var repackager = new VarRepackager(_imageManager);
                        var result = repackager.RepackageVarWithStats(packagePath, archivedFolder, conversions, (message, current, total) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (progressTextBlock != null)
                                    progressTextBlock.Text = message;
                                if (progressBar != null && total > 0)
                                    progressBar.Value = (double)current / total * 100;
                            });
                        });
                        
                        outputPath = result.outputPath;
                        originalPackageSize = result.originalSize;
                        newPackageSize = result.newSize;
                        texturesConverted = result.texturesConverted;
                    });

                    progressDialog.Close();

                    // Refresh package data for the converted package
                    await RefreshSinglePackage(packageName);

                    // Calculate savings
                    long spaceSaved = originalPackageSize - newPackageSize;
                    double percentSaved = originalPackageSize > 0 ? (100.0 * spaceSaved / originalPackageSize) : 0;
                    
                    // Check if any actual optimizations were performed
                    if (texturesConverted == 0 || spaceSaved <= 0)
                    {
                        // No textures were converted or no space was saved - they're already at optimal resolution
                        var infoResult = MessageBox.Show(
                            " Package Analysis Complete!\n\n" +
                            "No optimizations needed.\n" +
                            "All selected textures are already at or below your target resolution.\n\n" +
                            $"Package Size: {FormatBytes(originalPackageSize)}\n\n" +
                            $"The package has not been modified since no changes were required.",
                            "Already Optimized",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        
                        SetStatus($" Package already optimized: {packageName} - No changes needed");
                        return;
                    }
                    
                    // Show success message with stats
                    var successResult = MessageBox.Show(
                        " Conversion Complete!\n\n" +
                        $"Textures Converted: {texturesConverted}\n" +
                        $"Space Saved: {FormatBytes(spaceSaved)} ({percentSaved:F1}%)\n" +
                        $"Original Size: {FormatBytes(originalPackageSize)}\n" +
                        $"New Size: {FormatBytes(newPackageSize)}\n\n" +
                        $"Original backed up to ArchivedPackages folder.\n\n" +
                        $"Open package folder?",
                        "Success",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (successResult == MessageBoxResult.Yes)
                    {
                        OpenFolderAndSelectFile(outputPath);
                    }

                    SetStatus($" Textures optimized: {Path.GetFileName(outputPath)} - Saved {FormatBytes(spaceSaved)}");
                }
                catch (Exception ex)
                {
                    progressDialog.Close();
                    MessageBox.Show($"Error during conversion:\n\n{ex.Message}", "Conversion Error",
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    SetStatus($" Texture conversion failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error preparing conversion:\n\n{ex.Message}", "Error",
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Completely reloads a package from disk, clearing all cached data
        /// </summary>
        private async System.Threading.Tasks.Task ReloadPackageFromDisk(string packageName)
        {
            try
            {
                // Find the package in the grid
                var packageItem = PackageDataGrid.Items.Cast<PackageItem>()
                    .FirstOrDefault(p => p.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                
                if (packageItem == null)
                    return;

                // Get package file info using MetadataKey for accurate lookup (handles multiple versions of same package)
                PackageFileInfo pkgInfo;
                if (!string.IsNullOrEmpty(packageItem.MetadataKey))
                {
                    pkgInfo = _packageFileManager?.GetPackageFileInfoByMetadataKey(packageItem.MetadataKey);
                }
                else
                {
                    pkgInfo = _packageFileManager?.GetPackageFileInfo(packageName);
                }
                if (pkgInfo == null || string.IsNullOrEmpty(pkgInfo.CurrentPath))
                    return;

                // Clear all cached data for this package
                _imageManager?.ClearBitmapCache();
                
                // Remove old metadata from cache (all possible keys)
                if (_packageManager?.PackageMetadata != null)
                {
                    _packageManager.PackageMetadata.Remove(packageName);
                    if (!string.IsNullOrEmpty(packageItem.MetadataKey))
                    {
                        _packageManager.PackageMetadata.Remove(packageItem.MetadataKey);
                    }
                }

                // Re-parse the package file completely from disk
                var freshMetadata = await System.Threading.Tasks.Task.Run(() => 
                    _packageManager?.ParseVarMetadataComplete(pkgInfo.CurrentPath));
                
                if (freshMetadata != null)
                {
                    // Preserve the correct status from pkgInfo
                    freshMetadata.Status = pkgInfo.Status;
                    freshMetadata.FilePath = pkgInfo.CurrentPath;
                    
                    // Add fresh metadata to cache
                    if (_packageManager?.PackageMetadata != null)
                    {
                        _packageManager.PackageMetadata[packageName] = freshMetadata;
                        packageItem.MetadataKey = packageName;
                    }
                    
                    // Update all package item properties from fresh metadata
                    await Dispatcher.InvokeAsync(() =>
                    {
                        string originalStatus = packageItem.Status;
                        
                        packageItem.FileSize = freshMetadata.FileSize;
                        packageItem.ModifiedDate = freshMetadata.ModifiedDate;
                        packageItem.IsOptimized = freshMetadata.IsOptimized;
                        
                        if (!string.IsNullOrEmpty(freshMetadata.Status) && freshMetadata.Status != "Unknown")
                        {
                            packageItem.Status = freshMetadata.Status;
                        }
                        else
                        {
                            packageItem.Status = originalStatus;
                        }
                        
                        packageItem.Creator = freshMetadata.CreatorName;
                        packageItem.DependencyCount = freshMetadata.Dependencies?.Count ?? 0;
                        
                        var selectedNames = PreserveDataGridSelections();
                        PackageDataGrid.Items.Refresh();
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            RestoreDataGridSelections(selectedNames);
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    });
                    
                    // Small delay to ensure DataGrid refresh completes
                    await System.Threading.Tasks.Task.Delay(100);
                    
                    // Now refresh the information area if this package is selected
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (PackageDataGrid.SelectedItem == packageItem || 
                            (PackageDataGrid.SelectedItems != null && PackageDataGrid.SelectedItems.Contains(packageItem)))
                        {
                            var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
                            if (selectedPackages.Count == 1 && selectedPackages[0] == packageItem)
                            {
                                DisplayPackageInfo(packageItem);
                            }
                            else if (selectedPackages.Count > 1)
                            {
                                DisplayMultiplePackageInfo(selectedPackages);
                            }
                        }
                        
                        // Refresh filter lists to update optimization counts
                        RefreshFilterLists();
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reloading package: {ex.Message}");
            }
        }

        /// <summary>
        /// Refreshes data for a single package after conversion
        /// </summary>
        private async System.Threading.Tasks.Task RefreshSinglePackage(string packageName, bool refreshFilters = true)
        {
            try
            {
                // Clear bitmap cache to force reload of images
                _imageManager?.ClearBitmapCache();
                
                // Find the package in the grid
                var packageItem = PackageDataGrid.Items.Cast<PackageItem>()
                    .FirstOrDefault(p => p.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                
                if (packageItem == null)
                {
                    return;
                }
                
                // Get updated file info using MetadataKey for accurate lookup (handles multiple versions of same package)
                PackageFileInfo pkgInfo;
                if (!string.IsNullOrEmpty(packageItem.MetadataKey))
                {
                    pkgInfo = _packageFileManager?.GetPackageFileInfoByMetadataKey(packageItem.MetadataKey);
                }
                else
                {
                    pkgInfo = _packageFileManager?.GetPackageFileInfo(packageName);
                }
                if (pkgInfo == null || string.IsNullOrEmpty(pkgInfo.CurrentPath))
                {
                    return;
                }
                
                // Remove old metadata from ALL caches
                if (_packageManager != null)
                {
                    // Use the new public method to invalidate all caches
                    _packageManager.InvalidatePackageCache(packageName);
                    if (!string.IsNullOrEmpty(packageItem.MetadataKey))
                    {
                        _packageManager.InvalidatePackageCache(packageItem.MetadataKey);
                    }
                }
                
                // Re-parse the package metadata to get updated optimization flags and description
                var updatedMetadata = await System.Threading.Tasks.Task.Run(() => 
                    _packageManager?.ParseVarMetadataComplete(pkgInfo.CurrentPath));
                
                if (updatedMetadata != null)
                {
                    // Preserve the correct status from pkgInfo
                    updatedMetadata.Status = pkgInfo.Status;
                    updatedMetadata.FilePath = pkgInfo.CurrentPath;
                    
                    // Update all caches with fresh metadata (including thread cache and signature cache)
                    if (_packageManager != null)
                    {
                        _packageManager.UpdatePackageCache(packageName, updatedMetadata, pkgInfo.CurrentPath);
                        packageItem.MetadataKey = packageName;
                    }
                    
                    // Update the package item with new data
                    await Dispatcher.InvokeAsync(() =>
                    {
                        
                        // Preserve the original status if the new status is Unknown
                        string originalStatus = packageItem.Status;
                        
                        packageItem.FileSize = updatedMetadata.FileSize;
                        packageItem.ModifiedDate = updatedMetadata.ModifiedDate;
                        packageItem.IsOptimized = updatedMetadata.IsOptimized;
                        
                        // Only update status if it's not Unknown, otherwise keep the original
                        if (!string.IsNullOrEmpty(updatedMetadata.Status) && updatedMetadata.Status != "Unknown")
                        {
                            packageItem.Status = updatedMetadata.Status;
                        }
                        else
                        {
                            packageItem.Status = originalStatus;
                        }
                    });
                }
                else
                {
                    // Fallback: just update file size and modified date
                    var fileInfo = new FileInfo(pkgInfo.CurrentPath);
                    if (fileInfo.Exists)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            packageItem.FileSize = fileInfo.Length;
                            packageItem.ModifiedDate = fileInfo.LastWriteTime;
                        });
                    }
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    // Refresh the DataGrid to show updated data
                    PackageDataGrid.Items.Refresh();
                    
                    // Restore selection if needed
                    if (PackageDataGrid.SelectedItem == null)
                    {
                        var selectedNames = new List<string> { packageName };
                        RestoreDataGridSelections(selectedNames);
                    }
                }, System.Windows.Threading.DispatcherPriority.Render);
                
                // Small delay to ensure all property updates are processed (only in single-package mode)
                if (refreshFilters)
                {
                    await System.Threading.Tasks.Task.Delay(100);
                }
                
                // Refresh the information area if this package is currently selected
                await Dispatcher.InvokeAsync(() =>
                {
                    var isSelected = PackageDataGrid.SelectedItem == packageItem || 
                                   (PackageDataGrid.SelectedItems != null && PackageDataGrid.SelectedItems.Contains(packageItem));
                    
                    if (isSelected)
                    {
                        // Refresh the package info display to show updated optimization status
                        var selectedPackages = PackageDataGrid.SelectedItems.Cast<PackageItem>().ToList();
                        if (selectedPackages.Count == 1 && selectedPackages[0] == packageItem)
                        {
                            DisplayPackageInfo(packageItem);
                        }
                        else if (selectedPackages.Count > 1)
                        {
                            DisplayMultiplePackageInfo(selectedPackages);
                        }
                    }
                    
                    // Refresh filter lists to update optimization counts (only if requested)
                    if (refreshFilters)
                    {
                        RefreshFilterLists();
                    }
                });
            }
            catch
            {
                // Silently handle errors - package refresh is not critical
            }
        }

        // MOVED TO: Windows/Optimizers/OptimizerUIHelpers.cs

        private string GetProcessUsingFile(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                    return null;

                var processes = System.Diagnostics.Process.GetProcesses();
                foreach (var process in processes)
                {
                    try
                    {
                        // Check if process name contains common VaM-related names
                        string processName = process.ProcessName.ToLower();
                        if (processName.Contains("vam") || 
                            processName.Contains("virtamate") || 
                            processName.Contains("virt-a-mate"))
                        {
                            return $"VaM - {process.ProcessName}";
                        }
                    }
                    catch
                    {
                        // Skip processes we can't access
                    }
                }

                // If we can't determine the specific process, return a generic message
                return "VaM or another application";
            }
            catch
            {
                return null;
            }
        }

        // MOVED TO: Windows/Optimizers/OptimizerUIHelpers.cs
        
        private TabItem CreateSummaryTab(string packageName, TextureValidator.ValidationResult textureResult, HairOptimizer.OptimizationResult hairResult, Window parentDialog)
        {
            var tab = new TabItem
            {
                Header = "Summary",
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons

            // Header
            var headerText = new TextBlock
            {
                Text = "Optimization Summary",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(headerText, 0);
            tabGrid.Children.Add(headerText);

            // Scrollable content
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var contentPanel = new StackPanel { Margin = new Thickness(5) };

            // Texture Summary Section
            var textureHeader = new TextBlock
            {
                Text = " Texture Optimizations",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            contentPanel.Children.Add(textureHeader);

            var selectedTextures = textureResult.Textures.Where(t => t.HasActualConversion).ToList();
            if (selectedTextures.Count > 0)
            {
                var textureInfoPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 15) };
                
                var countText = new TextBlock
                {
                    Text = $" {selectedTextures.Count} texture(s) will be converted",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                textureInfoPanel.Children.Add(countText);

                foreach (var texture in selectedTextures)
                {
                    string targetRes = texture.ConvertTo8K ? "8K" : texture.ConvertTo4K ? "4K" : texture.ConvertTo2K ? "2K" : "?";
                    string currentRes = texture.Resolution;
                    
                    var itemText = new TextBlock
                    {
                        Text = $"  • {Path.GetFileName(texture.ReferencedPath)}: {currentRes} †’ {targetRes}",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                        FontFamily = new FontFamily("Consolas"),
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    textureInfoPanel.Children.Add(itemText);
                }

                contentPanel.Children.Add(textureInfoPanel);
            }
            else
            {
                var noTextureText = new TextBlock
                {
                    Text = "  No texture conversions selected",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(15, 0, 0, 15)
                };
                contentPanel.Children.Add(noTextureText);
            }

            // Hair Summary Section
            var hairHeader = new TextBlock
            {
                Text = " Hair Optimizations",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 10, 0, 10)
            };
            contentPanel.Children.Add(hairHeader);

            var selectedHairs = hairResult.HairItems.Where(h => h.HasConversionSelected).ToList();
            if (selectedHairs.Count > 0)
            {
                var hairInfoPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 15) };
                
                var countText = new TextBlock
                {
                    Text = $" {selectedHairs.Count} hair item(s) will be modified",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                hairInfoPanel.Children.Add(countText);

                foreach (var hair in selectedHairs)
                {
                    int targetDensity = hair.ConvertTo32 ? 32 : hair.ConvertTo24 ? 24 : hair.ConvertTo16 ? 16 : hair.ConvertTo8 ? 8 : 0;
                    string status = hair.HasCurveDensity ? $"{hair.CurveDensity} †’ {targetDensity}" : $"Add †’ {targetDensity}";
                    
                    var itemText = new TextBlock
                    {
                        Text = $"  • {hair.HairName}: curveDensity {status}",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                        FontFamily = new FontFamily("Consolas"),
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    hairInfoPanel.Children.Add(itemText);
                }

                contentPanel.Children.Add(hairInfoPanel);
            }
            else
            {
                var noHairText = new TextBlock
                {
                    Text = "  No hair modifications selected",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(15, 0, 0, 15)
                };
                contentPanel.Children.Add(noHairText);
            }

            // Important Notes Section
            var notesHeader = new TextBlock
            {
                Text = "ÃƒÂ¢Ã…Â¡Ã‚Â ÃƒÂ¯Ã‚Â¸Ã‚Â Important Notes",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                Margin = new Thickness(0, 20, 0, 10)
            };
            contentPanel.Children.Add(notesHeader);

            var notesPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 0) };
            
            var note1 = new TextBlock
            {
                Text = "• Original package will be backed up to ArchivedPackages folder",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
            notesPanel.Children.Add(note1);

            var note2 = new TextBlock
            {
                Text = "• Optimized package will replace the original in AddonPackages",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
            notesPanel.Children.Add(note2);

            var note3 = new TextBlock
            {
                Text = "• Package description will be updated with optimization details",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
            notesPanel.Children.Add(note3);

            var note4 = new TextBlock
            {
                Text = "• This operation may take several minutes depending on package size",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
            notesPanel.Children.Add(note4);

            contentPanel.Children.Add(notesPanel);

            scrollViewer.Content = contentPanel;
            Grid.SetRow(scrollViewer, 2);
            tabGrid.Children.Add(scrollViewer);

            // Button panel
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            // Apply Optimizations button
            var applyButton = new Button
            {
                Content = "Apply Optimizations",
                Width = 180,
                Height = 35,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                IsEnabled = selectedTextures.Count > 0 || selectedHairs.Count > 0
            };

            // Update button state when selections change
            var updateApplyButton = new Action(() =>
            {
                int textureCount = textureResult.Textures.Count(t => t.HasActualConversion);
                int hairCount = hairResult.HairItems.Count(h => h.HasConversionSelected);
                int totalCount = textureCount + hairCount;
                
                applyButton.Content = $"Apply Optimizations ({totalCount})";
                applyButton.IsEnabled = totalCount > 0;
                
                // Update summary content
                Dispatcher.InvokeAsync(() =>
                {
                    // Recreate the summary tab content
                    var newTab = CreateSummaryTab(packageName, textureResult, hairResult, parentDialog);
                    int tabIndex = -1;
                    for (int i = 0; i < ((TabControl)tab.Parent).Items.Count; i++)
                    {
                        if (((TabControl)tab.Parent).Items[i] == tab)
                        {
                            tabIndex = i;
                            break;
                        }
                    }
                    if (tabIndex >= 0)
                    {
                        ((TabControl)tab.Parent).Items[tabIndex] = newTab;
                    }
                });
            });

            applyButton.Click += (s, e) =>
            {
                // Note: This Summary tab button is deprecated. Use the main Optimize button instead.
                MessageBox.Show("Please use the main 'Optimize' button at the top right of the window to apply optimizations.", 
                              "Use Main Optimize Button", MessageBoxButton.OK, MessageBoxImage.Information);
            };

            buttonPanel.Children.Add(applyButton);

            Grid.SetRow(buttonPanel, 4);
            tabGrid.Children.Add(buttonPanel);

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Applies package optimizations (textures, hair, mirrors, and lights) using unified repackager with inline progress
        /// </summary>
        private async System.Threading.Tasks.Task ApplyPackageOptimizations(
            string packageName,
            TextureValidator.ValidationResult textureResult,
            HairOptimizer.OptimizationResult hairResult,
            DependencyScanner.DependencyScanResult dependencyResult,
            System.Windows.Controls.CheckBox forceLatestCheckbox,
            Window parentDialog,
            Button optimizeButton,
            TabControl tabControl,
            Button upArrowButton,
            Button downArrowButton,
            Button leftArrowButton,
            Button rightArrowButton,
            int currentPackageIndex,
            List<PackageItem> allPackages)
        {
            // ===== BENCHMARK START =====
            var benchmarkStart = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                // Get selected items
                var selectedTextures = textureResult.Textures.Where(t => t.HasConversionSelected).ToList();
                var selectedHairs = hairResult.HairItems.Where(h => h.HasConversionSelected).ToList();
                var selectedMirrors = hairResult.MirrorItems.Where(m => m.Disable == m.IsCurrentlyOn).ToList();
                var selectedLights = hairResult.LightItems.Where(l => l.HasActualShadowConversion).ToList();
                bool disableMirrors = selectedMirrors.Any(m => m.Disable);
                
                // Get disabled dependencies with parent info
                var disabledDependencies = dependencyResult?.Dependencies
                    ?.Where(d => !d.IsEnabled)
                    .Select(d => string.IsNullOrEmpty(d.ParentName) ? d.Name : $"{d.Name}|PARENT:{d.ParentName}")
                    .ToList() ?? new List<string>();
                
                // Get previously disabled dependencies (from package metadata)
                var previouslyDisabledDependencies = dependencyResult?.Dependencies
                    ?.Where(d => d.IsDisabledByUser)
                    .Select(d => string.IsNullOrEmpty(d.ParentName) ? d.Name : $"{d.Name}|PARENT:{d.ParentName}")
                    .ToHashSet() ?? new HashSet<string>();
                
                // Check if dependency state has changed
                var currentDisabledSet = new HashSet<string>(disabledDependencies);
                bool hasDependencyChanges = !currentDisabledSet.SetEquals(previouslyDisabledDependencies);
                
                // Check if Force .latest is enabled
                bool forceLatestEnabled = _settingsManager?.Settings?.ForceLatestDependencies ?? false;
                
                // Count dependencies that will be converted to .latest
                // Include dependencies where ForceLatest is true OR the global setting is enabled
                int latestConversionCount = dependencyResult?.Dependencies
                    ?.Count(d => d.IsEnabled && (d.ForceLatest || forceLatestEnabled) && d.WillBeConvertedToLatest) ?? 0;
                
                bool hasAnyChanges = selectedTextures.Count > 0 || selectedHairs.Count > 0 || 
                                    selectedMirrors.Count > 0 || selectedLights.Count > 0 || 
                                    hasDependencyChanges || latestConversionCount > 0;
                
                if (!hasAnyChanges)
                {
                    var result = MessageBox.Show(
                        "No visible options are changed.\n\n" +
                        "Do you want to re-optimize this package anyway?\n" +
                        "This can be useful to:\n" +
                        "  • Force re-optimization with current settings\n" +
                        "  • Update .latest dependencies\n" +
                        "  • Refresh optimization metadata\n\n" +
                        "Continue with optimization?",
                        "Confirm Re-optimization",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                // Get package path using MetadataKey for accurate lookup (handles multiple versions of same package)
                PackageFileInfo pkgInfo = null;
                if (currentPackageIndex >= 0 && currentPackageIndex < allPackages.Count)
                {
                    var packageItem = allPackages[currentPackageIndex];
                    if (!string.IsNullOrEmpty(packageItem.MetadataKey))
                    {
                        pkgInfo = _packageFileManager?.GetPackageFileInfoByMetadataKey(packageItem.MetadataKey);
                    }
                    else
                    {
                        pkgInfo = _packageFileManager?.GetPackageFileInfo(packageName);
                    }
                }
                else
                {
                    pkgInfo = _packageFileManager?.GetPackageFileInfo(packageName);
                }
                
                if (pkgInfo == null || string.IsNullOrEmpty(pkgInfo.CurrentPath))
                {
                    MessageBox.Show($"Could not find package: {packageName}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                

                string packagePath = pkgInfo.CurrentPath;
                
                // Only support VAR files
                if (!packagePath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Package optimization is only supported for .var files, not unarchived packages.", 
                                  "Unsupported Format", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Check if package already has optimizations and if there are actual changes
                VarMetadata packageMetadata = null;
                if (_packageManager?.PackageMetadata != null)
                {
                    _packageManager.PackageMetadata.TryGetValue(packageName, out packageMetadata);
                    
                    // CRITICAL FIX: If IsMorphAsset is false but we have content, re-detect it now
                    // The cached metadata might be stale or from before morph detection was added
                    if (packageMetadata != null && !packageMetadata.IsMorphAsset && packageMetadata.ContentList != null && packageMetadata.ContentList.Count > 0)
                    {
                        // Force re-detection of morph asset status by clearing the cache first
                        _packageManager?.InvalidatePackageCache(packageName);
                        
                        var freshMetadata = _packageManager?.ParseVarMetadataComplete(packagePath);
                        if (freshMetadata != null)
                        {
                            packageMetadata = freshMetadata;
                            // Update cache with fresh metadata
                            _packageManager.PackageMetadata[packageName] = freshMetadata;
                        }
                    }
                }
                
                bool isAlreadyOptimized = packageMetadata != null && packageMetadata.IsOptimized;
                bool hasActualTextureChanges = false;
                bool hasActualHairChanges = false;
                bool hasActualMirrorChanges = false;
                
                // Check if textures are selected for optimization
                // Allow re-optimization even if textures were previously optimized
                // User may want to optimize to a different resolution
                if (selectedTextures.Count > 0)
                {
                    hasActualTextureChanges = true;
                }
                
                // Check if hair settings have actual changes
                if (selectedHairs.Count > 0)
                {
                    // If package is already optimized and has hair optimization, check if there are new changes
                    if (!isAlreadyOptimized || !packageMetadata.HasHairOptimization)
                    {
                        hasActualHairChanges = true;
                    }
                    else
                    {
                        // For already optimized packages, assume hair changes are intentional
                        hasActualHairChanges = true;
                    }
                }
                
                // Check if mirror settings have actual changes
                if (disableMirrors)
                {
                    if (!isAlreadyOptimized || !packageMetadata.HasMirrorOptimization)
                    {
                        hasActualMirrorChanges = true;
                    }
                }
                
                // Determine if we need to backup (copy to archive)
                bool needsBackup = !isAlreadyOptimized && (hasActualTextureChanges || hasActualHairChanges || hasActualMirrorChanges || hasDependencyChanges || latestConversionCount > 0);
                
                // Check if there are no actual changes (dependencies being converted to .latest count as changes)
                if (!hasActualTextureChanges && !hasActualHairChanges && !hasActualMirrorChanges && !hasDependencyChanges && latestConversionCount == 0)
                {
                    // Build message based on what was selected
                    var messageBuilder = new StringBuilder();
                    messageBuilder.AppendLine("No actual changes detected in the optimization settings.\n");
                    
                    if (selectedTextures.Count > 0)
                        messageBuilder.AppendLine("• All selected textures are already at or below the target resolution");
                    if (selectedHairs.Count > 0)
                        messageBuilder.AppendLine("• Hair settings match current state");
                    if (selectedMirrors.Count > 0)
                        messageBuilder.AppendLine("• Mirror settings match current state");
                    if (selectedLights.Count > 0)
                        messageBuilder.AppendLine("• Light settings match current state");
                    
                    messageBuilder.AppendLine("\nDo you want to re-optimize this package anyway?");
                    messageBuilder.AppendLine("This can be useful to:");
                    messageBuilder.AppendLine("  • Force re-optimization with current settings");
                    messageBuilder.AppendLine("  • Update .latest dependencies");
                    messageBuilder.AppendLine("  • Refresh optimization metadata");
                    
                    var result = MessageBox.Show(
                        messageBuilder.ToString(),
                        "Confirm Re-optimization",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }
                

                // Get VAM root folder for ArchivedPackages - always use game root, not subfolder
                string archivedFolder = Path.Combine(_selectedFolder, "ArchivedPackages");
                
                // Check if we're reading from archive and file exists in main folder
                string archiveFilePath = Path.Combine(archivedFolder, Path.GetFileName(packagePath));
                bool readingFromArchive = File.Exists(archiveFilePath) && needsBackup;
                
                if (readingFromArchive)
                {
                    // Check if optimized file already exists in AddonPackages
                    string addonPackagesFolder = Path.Combine(_selectedFolder, "AddonPackages");
                    string existingOptimizedPath = Path.Combine(addonPackagesFolder, Path.GetFileName(packagePath));
                    
                    if (File.Exists(existingOptimizedPath))
                    {
                        var overwriteResult = ShowFileConflictDialog(Path.GetFileName(packagePath));
                        
                        if (overwriteResult == MessageBoxResult.Cancel)
                        {
                            return;
                        }
                        else if (overwriteResult == MessageBoxResult.No)
                        {
                            // Will create timestamped version - handled in PackageRepackager
                        }
                        else // Yes - overwrite
                        {
                            // Delete the existing file so it can be overwritten
                            try
                            {
                                File.Delete(existingOptimizedPath);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Could not delete existing file:\n{ex.Message}", "Error", 
                                              MessageBoxButton.OK, MessageBoxImage.Error);
                                return;
                            }
                        }
                    }
                }
                
                // Disable navigation buttons and optimize button during optimization
                upArrowButton.IsEnabled = false;
                downArrowButton.IsEnabled = false;
                leftArrowButton.IsEnabled = false;
                rightArrowButton.IsEnabled = false;
                optimizeButton.IsEnabled = false;
                optimizeButton.Content = "Optimizing...";
                
                // Build optimization config
                var config = new PackageRepackager.OptimizationConfig();
                
                // Add texture conversions
                foreach (var texture in selectedTextures)
                {
                    string targetResolution = texture.ConvertTo8K ? "8K" :
                                            texture.ConvertTo4K ? "4K" :
                                            texture.ConvertTo2K ? "2K" : "2K";
                    config.TextureConversions[texture.ReferencedPath] = 
                        (targetResolution, texture.Width, texture.Height, texture.FileSize);
                }
                
                // Add hair conversions
                foreach (var hair in selectedHairs)
                {
                    int targetDensity = hair.ConvertTo32 ? 32 : hair.ConvertTo24 ? 24 : hair.ConvertTo16 ? 16 : hair.ConvertTo8 ? 8 : 16;
                    string key = $"{hair.SceneFile}_{hair.HairId}";
                    config.HairConversions[key] = (hair.SceneFile, hair.HairId, targetDensity, hair.HasCurveDensity);
                }
                
                // Add light shadow conversions
                foreach (var light in selectedLights)
                {
                    bool castShadows = !light.SetShadowsOff;
                    int shadowResolution = light.SetShadows512 ? 512 : 
                                         light.SetShadows1024 ? 1024 : 
                                         light.SetShadows2048 ? 2048 : 0;
                    string key = $"{light.SceneFile}_{light.LightId}";
                    config.LightConversions[key] = (light.SceneFile, light.LightId, castShadows, shadowResolution);
                }
                
                // Set mirrors flag
                config.DisableMirrors = disableMirrors;
                
                // Set force latest dependencies flag from settings
                config.ForceLatestDependencies = _settingsManager?.Settings?.ForceLatestDependencies ?? false;
                
                // Add disabled dependencies
                config.DisabledDependencies = disabledDependencies;
                
                // Set disable morph preload flag from settings
                config.DisableMorphPreload = _settingsManager?.Settings?.DisableMorphPreload ?? true;
                
                // Set IsMorphAsset flag from package metadata (already retrieved earlier)
                config.IsMorphAsset = packageMetadata?.IsMorphAsset ?? false;

                // Perform optimization in background
                string outputPath = null;
                long originalPackageSize = 0;
                long newPackageSize = 0;
                int texturesConverted = 0;
                int hairsModified = 0;
                
                var repackageStartTime = benchmarkStart.ElapsedMilliseconds;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    var repackager = new PackageRepackager(_imageManager);
                    var optimizationResult = repackager.RepackageVarWithOptimizations(
                        packagePath, 
                        archivedFolder, 
                        config, 
                        null, // No progress callback
                        needsBackup); // Pass the backup flag
                    
                    outputPath = optimizationResult.OutputPath;
                    originalPackageSize = optimizationResult.OriginalSize;
                    newPackageSize = optimizationResult.NewSize;
                    texturesConverted = optimizationResult.TexturesConverted;
                    hairsModified = optimizationResult.HairsModified;
                });
                
                // Remove disabled dependencies if any
                if (disabledDependencies.Count > 0)
                {
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        var dependencyRemover = new Services.DependencyRemover();
                        var removalResult = dependencyRemover.RemoveDependenciesFromPackage(outputPath ?? packagePath, disabledDependencies);
                    });
                }

                // CRITICAL: Clear metadata cache BEFORE refresh to force re-parsing
                if (_packageManager?.PackageMetadata != null)
                {
                    _packageManager.PackageMetadata.Remove(packageName);
                }

                // Delay to ensure file system has flushed
                await System.Threading.Tasks.Task.Delay(1000);

                // Refresh package data
                await RefreshSinglePackage(packageName);

                // Calculate savings
                long spaceSaved = originalPackageSize - newPackageSize;
                double percentSaved = originalPackageSize > 0 ? (100.0 * spaceSaved / originalPackageSize) : 0;
                
                // Re-enable button and navigation
                optimizeButton.IsEnabled = true;
                optimizeButton.Content = "Optimize";
                upArrowButton.IsEnabled = currentPackageIndex > 0;
                downArrowButton.IsEnabled = currentPackageIndex < allPackages.Count - 1;
                leftArrowButton.IsEnabled = true;
                rightArrowButton.IsEnabled = true;
                
                // Refresh the window content with updated data
                SetStatus($"Refreshing optimization data...");
                await RefreshOptimizationTabsData(allPackages, textureResult, hairResult, tabControl);
                
                SetStatus($"✓ Optimization complete! Saved {PackageItem.FormatFileSize(spaceSaved)} ({percentSaved:F1}%)");
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show($"Error during optimization:\n\n{ex.Message}", "Optimization Error",
                                  MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus($"ÃƒÂ¢Ã‚ÂÃ…â€™ Package optimization failed: {ex.Message}");
        }
    }

        /// <summary>
        /// Shows a dark-themed message box
        /// </summary>
        private MessageBoxResult ShowDarkMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
        {
            // For now, use standard MessageBox
            // TODO: Create custom dark-themed message box window
            return MessageBox.Show(message, title, buttons, icon);
        }

        /// <summary>
        /// Displays bulk optimization dialog for multiple packages
        /// </summary>
        private async System.Threading.Tasks.Task DisplayBulkOptimizationDialog(List<PackageItem> packages)
        {
            Window progressDialog = null;
            TextBlock progressTextBlock = null;
            ProgressBar progressBar = null;
            
            try
            {
                // Create and show progress dialog immediately
                progressDialog = CreateProgressDialog(this);
                progressDialog.Title = "Analyzing Packages";
                var progressContent = progressDialog.Content as Grid;
                progressTextBlock = progressContent?.Children.OfType<TextBlock>().FirstOrDefault();
                progressBar = progressContent?.Children.OfType<ProgressBar>().FirstOrDefault();
                
                if (progressTextBlock != null)
                    progressTextBlock.Text = $"Analyzing {packages.Count} package(s)...\n0 / {packages.Count}";
                
                progressDialog.Show();
                
                // Run analysis in background thread
                var allTextures = new List<TextureValidator.TextureInfo>();
                var allHairItems = new List<HairOptimizer.HairInfo>();
                var allMirrorItems = new List<HairOptimizer.MirrorInfo>();
                var allLightItems = new List<HairOptimizer.LightInfo>();
                var allDependencies = new List<DependencyItemModel>();
                var skippedPackages = new List<string>();
                
                await System.Threading.Tasks.Task.Run(() =>
                {
                    int processedCount = 0;
                    string archivedFolder = Path.Combine(_selectedFolder, "ArchivedPackages");
                    
                    foreach (var package in packages)
                    {
                        try
                        {
                            string packagePath = GetPackagePath(package);
                            if (string.IsNullOrEmpty(packagePath))
                            {
                                lock (skippedPackages)
                                {
                                    skippedPackages.Add($"{package.Name}: Package file not found");
                                }
                                continue;
                            }

                            // Validate textures
                            var textureValidator = new Services.TextureValidator();
                            var textureResult = textureValidator.ValidatePackageTextures(packagePath, archivedFolder);
                            
                            // Get package metadata for description parsing
                            VarMetadata packageMetadata = null;
                            if (_packageManager?.PackageMetadata != null)
                            {
                                // Use MetadataKey for accurate lookup (handles multiple versions of same package)
                                string lookupKey = !string.IsNullOrEmpty(package.MetadataKey) ? package.MetadataKey : package.Name;
                                _packageManager.PackageMetadata.TryGetValue(lookupKey, out packageMetadata);
                            }
                            
                            // Parse original texture data from metadata if available
                            if (packageMetadata != null && !string.IsNullOrEmpty(packageMetadata.Description))
                            {
                                string descLower = packageMetadata.Description.ToLowerInvariant();
                                bool hasCompressedTextures = descLower.Contains("texture") && 
                                                             (descLower.Contains("compress") || 
                                                              descLower.Contains("convert") || 
                                                              descLower.Contains("4k") || 
                                                              descLower.Contains("2k") || 
                                                              descLower.Contains("8k") ||
                                                              descLower.Contains("optimiz"));
                                
                                if (hasCompressedTextures)
                                {
                                    ParseOriginalTextureData(packageMetadata.Description, textureResult.Textures);
                                }
                            }
                            
                            // Add package name to each texture
                            lock (allTextures)
                            {
                                foreach (var texture in textureResult.Textures)
                                {
                                    texture.PackageName = package.Name;
                                    allTextures.Add(texture);
                                }
                            }

                            // Scan hair
                            var hairOptimizer = new Services.HairOptimizer();
                            var hairResult = hairOptimizer.ScanPackageHair(packagePath);
                            
                            // Add package name to each item
                            lock (allHairItems)
                            {
                                foreach (var hair in hairResult.HairItems)
                                {
                                    hair.PackageName = package.Name;
                                    allHairItems.Add(hair);
                                }
                            }
                            
                            lock (allMirrorItems)
                            {
                                foreach (var mirror in hairResult.MirrorItems)
                                {
                                    mirror.PackageName = package.Name;
                                    allMirrorItems.Add(mirror);
                                }
                            }
                            
                            lock (allLightItems)
                            {
                                foreach (var light in hairResult.LightItems)
                                {
                                    light.PackageName = package.Name;
                                    allLightItems.Add(light);
                                }
                            }

                            // Scan dependencies
                            var dependencyScanner = new Services.DependencyScanner();
                            var dependencyResult = dependencyScanner.ScanPackageDependencies(packagePath);
                            
                            // Add package name to each dependency
                            lock (allDependencies)
                            {
                                foreach (var dependency in dependencyResult.Dependencies)
                                {
                                    dependency.PackageName = package.Name;
                                    allDependencies.Add(dependency);
                                }
                            }

                            if (!string.IsNullOrEmpty(textureResult.ErrorMessage))
                            {
                                lock (skippedPackages)
                                {
                                    skippedPackages.Add($"{package.Name}: {textureResult.ErrorMessage}");
                                }
                                continue;
                            }
                            if (!string.IsNullOrEmpty(hairResult.ErrorMessage))
                            {
                                lock (skippedPackages)
                                {
                                    skippedPackages.Add($"{package.Name}: {hairResult.ErrorMessage}");
                                }
                                continue;
                            }
                            if (!string.IsNullOrEmpty(dependencyResult.ErrorMessage))
                            {
                                lock (skippedPackages)
                                {
                                    skippedPackages.Add($"{package.Name}: {dependencyResult.ErrorMessage}");
                                }
                                continue;
                            }

                            processedCount++;
                            
                            // Update progress on UI thread
                            Dispatcher.Invoke(() =>
                            {
                                if (progressTextBlock != null)
                                {
                                    progressTextBlock.Text = $"Analyzing {packages.Count} package(s)...\n{processedCount} / {packages.Count} completed\n\nCurrent: {package.Name}";
                                }
                                if (progressBar != null && packages.Count > 0)
                                {
                                    progressBar.Value = (double)processedCount / packages.Count * 100;
                                }
                                SetStatus($"Analyzing packages... {processedCount}/{packages.Count}");
                            });
                        }
                        catch (Exception ex)
                        {
                            lock (skippedPackages)
                            {
                                skippedPackages.Add($"{package.Name}: {ex.Message}");
                            }
                            continue;
                        }
                    }
                });

                // Close progress dialog
                progressDialog.Close();

                string statusMessage = $"Analysis complete. {packages.Count - skippedPackages.Count} packages processed";
                if (skippedPackages.Count > 0)
                {
                    statusMessage += $", {skippedPackages.Count} skipped due to errors";
                }
                SetStatus(statusMessage);

                // Create aggregated results
                var aggregatedTextureResult = new TextureValidator.ValidationResult
                {
                    Textures = allTextures,
                    ErrorMessage = null
                };

                var aggregatedHairResult = new HairOptimizer.OptimizationResult
                {
                    HairItems = allHairItems,
                    MirrorItems = allMirrorItems,
                    LightItems = allLightItems
                };

                var aggregatedDependencyResult = new DependencyScanner.DependencyScanResult
                {
                    Dependencies = allDependencies
                };

                // Display bulk optimization dialog
                DisplayBulkOptimizationDialogUI(packages, aggregatedTextureResult, aggregatedHairResult, aggregatedDependencyResult);
            }
            catch (Exception ex)
            {
                if (progressDialog != null)
                {
                    progressDialog.Close();
                }
                
                MessageBox.Show($"Error during bulk package analysis: {ex.Message}",
                              "Bulk Optimization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus($"Bulk analysis failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Displays the UI for bulk optimization dialog
        /// </summary>
        private void DisplayBulkOptimizationDialogUI(List<PackageItem> packages, TextureValidator.ValidationResult textureResult, HairOptimizer.OptimizationResult hairResult, DependencyScanner.DependencyScanResult dependencyResult)
        {
            try
            {
                // Store current package selection for restoration
                var currentPackageSelection = PackageDataGrid?.SelectedItems?.Cast<PackageItem>()?.Select(p => p.Name)?.ToHashSet() ?? new HashSet<string>();
                
                // Store current dependencies selection for restoration
                var currentDependenciesSelection = DependenciesDataGrid?.SelectedItems?.Cast<DependencyItem>()?.Select(d => d.DisplayName)?.ToHashSet() ?? new HashSet<string>();
                
                // Store package names for selection restoration
                var selectedPackageNames = packages.Select(p => p.Name).ToHashSet();
                
                // Create result dialog
                var dialog = new Window
                {
                    Title = packages.Count == 1 ? $"Optimise Package - {packages[0].Name}" : $"Bulk Optimise - {packages.Count} Packages",
                    Width = 1200,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.CanResize,
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
                };

                // Apply dark theme to window chrome
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(dialog).EnsureHandle();
                    int useImmersiveDarkMode = 1;
                    // Try Windows 11/10 20H1+ attribute first, then fall back to older Windows 10 attribute
                    if (DwmSetWindowAttribute(hwnd, 20, ref useImmersiveDarkMode, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, 19, ref useImmersiveDarkMode, sizeof(int));
                    }
                }
                catch
                {
                    // Dark mode not available on this system
                }

                var mainGrid = new Grid { Margin = new Thickness(15) };
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // TabControl
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Close button

                // Create TabControl with dark theme styling
                var tabControl = new TabControl
                {
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    BorderThickness = new Thickness(0)
                };

                // Style the TabControl for dark theme with rounded corners
                var tabControlStyle = new Style(typeof(TabControl));
                tabControlStyle.Setters.Add(new Setter(TabControl.BackgroundProperty, new SolidColorBrush(Color.FromRgb(30, 30, 30))));
                tabControl.Style = tabControlStyle;

                // Create custom template for TabItem to ensure dark theme
                var tabItemStyle = new Style(typeof(TabItem));
                
                // Create control template for TabItem
                var tabTemplate = new ControlTemplate(typeof(TabItem));
                
                // Create a Grid to hold the border and allow it to stretch
                var tabGridFactory = new FrameworkElementFactory(typeof(Grid));
                tabGridFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
                tabGridFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
                
                var bulkTabBorderFactory = new FrameworkElementFactory(typeof(Border));
                bulkTabBorderFactory.Name = "Border";
                bulkTabBorderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45)));
                bulkTabBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
                bulkTabBorderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Colors.Transparent));
                bulkTabBorderFactory.SetValue(Border.PaddingProperty, new Thickness(20, 10, 20, 10));
                bulkTabBorderFactory.SetValue(Border.MarginProperty, new Thickness(0, 0, 2, 0));
                bulkTabBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS, UI_CORNER_RADIUS, 0, 0));
                bulkTabBorderFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
                bulkTabBorderFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
                
                var bulkTabContentPresenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
                bulkTabContentPresenterFactory.SetValue(ContentPresenter.ContentSourceProperty, "Header");
                bulkTabContentPresenterFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                bulkTabContentPresenterFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                
                bulkTabBorderFactory.AppendChild(bulkTabContentPresenterFactory);
                tabGridFactory.AppendChild(bulkTabBorderFactory);
                tabTemplate.VisualTree = tabGridFactory;
                
                // Trigger for selected state
                var selectedTrigger = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
                selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(60, 60, 60)), "Border"));
                selectedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "Border"));
                selectedTrigger.Setters.Add(new Setter(TabItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80))));
                tabTemplate.Triggers.Add(selectedTrigger);
                
                // Trigger for hover state
                var bulkTabHoverTrigger = new Trigger { Property = TabItem.IsMouseOverProperty, Value = true };
                bulkTabHoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(55, 55, 55)), "Border"));
                tabTemplate.Triggers.Add(bulkTabHoverTrigger);
                
                tabItemStyle.Setters.Add(new Setter(TabItem.TemplateProperty, tabTemplate));
                tabItemStyle.Setters.Add(new Setter(TabItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
                tabItemStyle.Setters.Add(new Setter(TabItem.FontSizeProperty, 14.0));
                tabItemStyle.Setters.Add(new Setter(TabItem.FontWeightProperty, FontWeights.SemiBold));
                tabItemStyle.Setters.Add(new Setter(TabItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
                tabItemStyle.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
                // MinWidth will be set dynamically based on window width in dialog.Loaded handler
                
                tabControl.Resources.Add(typeof(TabItem), tabItemStyle);

                // Create placeholder tabs first (empty content with loading message) - will be populated after window shows
                var loadingMessage = new TextBlock
                {
                    Text = "Loading data...",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(20)
                };
                
                var bulkTextureTab = new TabItem 
                { 
                    Header = new StackPanel 
                    { 
                        Orientation = Orientation.Horizontal,
                        Children = { new TextBlock { Text = $"Textures ({textureResult.Textures.Count})", VerticalAlignment = VerticalAlignment.Center } }
                    },
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    Content = new TextBlock
                    {
                        Text = "Loading textures...",
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(20)
                    }
                };
                tabControl.Items.Add(bulkTextureTab);

                var bulkHairTab = new TabItem 
                { 
                    Header = new StackPanel 
                    { 
                        Orientation = Orientation.Horizontal,
                        Children = { new TextBlock { Text = $"Hair ({hairResult.HairItems.Count})", VerticalAlignment = VerticalAlignment.Center } }
                    },
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
                };
                tabControl.Items.Add(bulkHairTab);

                var mirrorsTab = new TabItem 
                { 
                    Header = new StackPanel 
                    { 
                        Orientation = Orientation.Horizontal,
                        Children = { new TextBlock { Text = $"Mirrors ({hairResult.MirrorItems.Count})", VerticalAlignment = VerticalAlignment.Center } }
                    },
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
                };
                tabControl.Items.Add(mirrorsTab);

                var shadowsTab = new TabItem 
                { 
                    Header = new StackPanel 
                    { 
                        Orientation = Orientation.Horizontal,
                        Children = { new TextBlock { Text = $"Shadows ({hairResult.LightItems.Count})", VerticalAlignment = VerticalAlignment.Center } }
                    },
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
                };
                tabControl.Items.Add(shadowsTab);

                var dependenciesTab = new TabItem 
                { 
                    Header = "Dependencies",
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
                };
                tabControl.Items.Add(dependenciesTab);

                var miscTab = new TabItem 
                { 
                    Header = "Misc",
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
                };
                tabControl.Items.Add(miscTab);

                var summaryTab = new TabItem 
                { 
                    Header = "Summary",
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
                };
                tabControl.Items.Add(summaryTab);

                // Add SelectionChanged handler to refresh summary tab when selected
                // This handler will be called after tabs are populated
                tabControl.SelectionChanged += (s, e) =>
                {
                    // Only refresh if summary tab is selected
                    var selectedTab = tabControl.SelectedItem as TabItem;
                    if (selectedTab?.Header is string header && header == "Summary")
                    {
                        // Recreate summary tab content with current data
                        var newSummaryTab = CreateBulkSummaryTab(packages, textureResult, hairResult, dependencyResult, dialog);
                        selectedTab.Content = newSummaryTab.Content;
                    }
                };

                // Add mouse wheel handler for TabControl to scroll through tabs (only when hovering over tab headers)
                tabControl.PreviewMouseWheel += (s, e) =>
                {
                    // Check if mouse is over a TabItem header (not the content area)
                    var mousePosition = e.GetPosition(tabControl);
                    var element = tabControl.InputHitTest(mousePosition) as DependencyObject;
                    
                    bool isOverTabHeader = false;
                    while (element != null)
                    {
                        if (element is TabItem)
                        {
                            isOverTabHeader = true;
                            break;
                        }
                        element = System.Windows.Media.VisualTreeHelper.GetParent(element);
                    }
                    
                    if (!isOverTabHeader)
                        return;
                    
                    if (e.Delta > 0)
                    {
                        if (tabControl.SelectedIndex > 0)
                        {
                            tabControl.SelectedIndex--;
                        }
                        else
                        {
                            tabControl.SelectedIndex = tabControl.Items.Count - 1;
                        }
                    }
                    else
                    {
                        if (tabControl.SelectedIndex < tabControl.Items.Count - 1)
                        {
                            tabControl.SelectedIndex++;
                        }
                        else
                        {
                            tabControl.SelectedIndex = 0;
                        }
                    }
                    e.Handled = true;
                };

                // Add TabControl directly to main grid
                Grid.SetRow(tabControl, 0);
                mainGrid.Children.Add(tabControl);

                // Bottom panel with progress bar on top row, status/checkbox/buttons on bottom row
                var bottomGrid = new Grid();
                bottomGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Progress bar row
                bottomGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Status + checkbox + buttons row
                bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Left side: status
                bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Middle: checkbox
                bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Right side: buttons

                // Create modern progress bar (spans all columns on row 0)
                var progressBar = new ProgressBar
                {
                    Height = 3,
                    Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                    Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    Value = 0,
                    Maximum = 100,
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(0, 0, 8, 0) // Add right margin
                };
                Grid.SetRow(progressBar, 0);
                Grid.SetColumn(progressBar, 0);
                Grid.SetColumnSpan(progressBar, 1);
                bottomGrid.Children.Add(progressBar);

                // Create status panel with messages (row 1, column 0)
                var statusPanelBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                    CornerRadius = new CornerRadius(UI_CORNER_RADIUS),
                    Margin = new Thickness(0, 0, 8, 0),
                    Padding = new Thickness(12, 8, 12, 8),
                    MinHeight = 40
                };

                var statusPanel = new Grid();
                statusPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                statusPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Current package text
                var currentPackageText = new TextBlock
                {
                    Text = "Ready to optimize",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Colors.White),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(currentPackageText, 0);
                statusPanel.Children.Add(currentPackageText);

                // Time info text (elapsed, estimated, etc)
                var timeInfoText = new TextBlock
                {
                    Text = "",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(15, 0, 0, 0)
                };
                Grid.SetColumn(timeInfoText, 1);
                statusPanel.Children.Add(timeInfoText);

                statusPanelBorder.Child = statusPanel;

                Grid.SetRow(statusPanelBorder, 1);
                Grid.SetColumn(statusPanelBorder, 0);
                bottomGrid.Children.Add(statusPanelBorder);

                // Create Alert checkbox with modern dark theme styling
                var alertCheckBox = new CheckBox
                {
                    Content = "Alert",
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                    FontSize = 12,
                    Background = Brushes.Transparent,
                    ToolTip = "Play a sound notification when optimization completes",
                    IsChecked = true
                };
                
                // Apply modern checkbox template matching the UI style
                var alertCheckBoxTemplate = new ControlTemplate(typeof(CheckBox));
                
                var gridFactory = new FrameworkElementFactory(typeof(Grid));
                gridFactory.SetValue(Grid.BackgroundProperty, Brushes.Transparent);
                
                var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
                col1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
                var col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
                col2.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
                gridFactory.AppendChild(col1);
                gridFactory.AppendChild(col2);
                
                var borderFactory = new FrameworkElementFactory(typeof(Border));
                borderFactory.Name = "CheckBoxBorder";
                borderFactory.SetValue(Grid.ColumnProperty, 0);
                borderFactory.SetValue(Border.WidthProperty, 18.0);
                borderFactory.SetValue(Border.HeightProperty, 18.0);
                borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 48)));
                borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(85, 85, 85)));
                borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
                borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS));
                borderFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
                
                var pathFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
                pathFactory.Name = "CheckMark";
                pathFactory.SetValue(System.Windows.Shapes.Path.DataProperty, System.Windows.Media.Geometry.Parse("M 0,4 L 3,7 L 8,0"));
                pathFactory.SetValue(System.Windows.Shapes.Path.StrokeProperty, Brushes.White);
                pathFactory.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
                pathFactory.SetValue(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Collapsed);
                pathFactory.SetValue(System.Windows.Shapes.Path.StretchProperty, System.Windows.Media.Stretch.Uniform);
                pathFactory.SetValue(System.Windows.Shapes.Path.MarginProperty, new Thickness(3));
                
                borderFactory.AppendChild(pathFactory);
                gridFactory.AppendChild(borderFactory);
                
                var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
                contentFactory.SetValue(Grid.ColumnProperty, 1);
                contentFactory.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 0, 0));
                contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
                gridFactory.AppendChild(contentFactory);
                
                alertCheckBoxTemplate.VisualTree = gridFactory;
                
                var checkedTrigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
                checkedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Visible, "CheckMark"));
                checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
                checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
                alertCheckBoxTemplate.Triggers.Add(checkedTrigger);
                
                var hoverTrigger = new Trigger { Property = CheckBox.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
                alertCheckBoxTemplate.Triggers.Add(hoverTrigger);
                
                var checkBoxStyle = new Style(typeof(CheckBox));
                checkBoxStyle.Setters.Add(new Setter(CheckBox.TemplateProperty, alertCheckBoxTemplate));
                checkBoxStyle.Setters.Add(new Setter(CheckBox.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
                alertCheckBox.Style = checkBoxStyle;
                
                Grid.SetRow(alertCheckBox, 1);
                Grid.SetColumn(alertCheckBox, 1);
                bottomGrid.Children.Add(alertCheckBox);

                // Create rounded button template
                var buttonTemplate = new ControlTemplate(typeof(Button));
                var buttonBorderFactory = new FrameworkElementFactory(typeof(Border));
                buttonBorderFactory.Name = "ButtonBorder";
                buttonBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS));
                buttonBorderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
                buttonBorderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
                buttonBorderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
                var buttonContentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
                buttonContentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                buttonContentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                buttonBorderFactory.AppendChild(buttonContentFactory);
                buttonTemplate.VisualTree = buttonBorderFactory;
                
                // Create Optimize button (initially disabled until tabs are loaded)
                var optimizeButton = new Button
                {
                    Content = packages.Count == 1 ? "Loading..." : "Loading...",
                    Width = 140,
                    Height = 40,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    Foreground = new SolidColorBrush(Colors.White),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Template = buttonTemplate,
                    IsEnabled = false
                };

                // Add hover effect
                optimizeButton.MouseEnter += (s, e) => optimizeButton.Background = new SolidColorBrush(Color.FromRgb(56, 142, 60));
                optimizeButton.MouseLeave += (s, e) => optimizeButton.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));

                // Optimize button click handler
                optimizeButton.Click += async (s, e) =>
                {
                    // Extract minify JSON option from Misc tab
                    // Handle both string and StackPanel headers (tabs are populated asynchronously)
                    bool minifyJson = false;
                    var miscTab = tabControl.Items.Cast<TabItem>().FirstOrDefault(t => 
                        (t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(tb => tb.Text == "Misc")) ||
                        (t.Header is string s && s == "Misc"));
                    if (miscTab?.Tag is CheckBox minifyCheckBox)
                    {
                        minifyJson = minifyCheckBox.IsChecked ?? false;
                    }
                    
                    await ApplyBulkPackageOptimizations(packages, textureResult, hairResult, dependencyResult, dialog, optimizeButton, tabControl, minifyJson, progressBar, currentPackageText, timeInfoText, alertCheckBox, bottomGrid);
                };

                Grid.SetRow(optimizeButton, 1);
                Grid.SetColumn(optimizeButton, 2);
                Grid.SetRowSpan(optimizeButton, 1);
                optimizeButton.VerticalAlignment = VerticalAlignment.Center;
                bottomGrid.Children.Add(optimizeButton);

                Grid.SetRow(bottomGrid, 2);
                mainGrid.Children.Add(bottomGrid);

                dialog.Content = mainGrid;
                
                // Calculate dynamic MinWidth for tabs based on window width
                // This ensures tabs fit in single row at full width but wrap to 2 rows when text is at risk of clipping
                Action updateTabWidths = () =>
                {
                    int tabCount = tabControl.Items.Count;
                    double windowWidth = dialog.ActualWidth;
                    // Calculate MinWidth to keep tabs in single row as long as possible
                    // Only wrap to 2 rows when window gets too small
                    // Use a larger divisor to allow more text space before wrapping
                    double calculatedMinWidth = (windowWidth - 50) / tabCount;
                    // Ensure minimum width doesn't go below a reasonable size (e.g., 80px) to prevent excessive wrapping
                    calculatedMinWidth = Math.Max(calculatedMinWidth, 80.0);
                    
                    foreach (TabItem tab in tabControl.Items)
                    {
                        tab.MinWidth = calculatedMinWidth;
                    }
                };
                
                // Populate tabs after window is shown - use Dispatcher to ensure UI updates properly
                dialog.Loaded += (s, e) => 
                {
                    updateTabWidths();
                    
                    // Queue tab population to give UI time to render
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            // Replace placeholder tabs with fully populated tabs
                            // Must store index and replace entire TabItem to avoid "element already child" error
                            int textureIndex = tabControl.Items.IndexOf(bulkTextureTab);
                            tabControl.Items.RemoveAt(textureIndex);
                            bulkTextureTab = CreateBulkTextureTab(textureResult, dialog);
                            tabControl.Items.Insert(textureIndex, bulkTextureTab);
                            
                            int hairIndex = tabControl.Items.Count > 1 ? 1 : tabControl.Items.Count;
                            if (tabControl.Items.Count > hairIndex)
                            {
                                tabControl.Items.RemoveAt(hairIndex);
                            }
                            bulkHairTab = CreateBulkHairTab(hairResult, dialog);
                            tabControl.Items.Insert(hairIndex, bulkHairTab);
                            
                            int mirrorsIndex = tabControl.Items.Count > 2 ? 2 : tabControl.Items.Count;
                            if (tabControl.Items.Count > mirrorsIndex)
                            {
                                tabControl.Items.RemoveAt(mirrorsIndex);
                            }
                            mirrorsTab = CreateBulkMirrorsTab(hairResult, dialog);
                            tabControl.Items.Insert(mirrorsIndex, mirrorsTab);
                            
                            int shadowsIndex = tabControl.Items.Count > 3 ? 3 : tabControl.Items.Count;
                            if (tabControl.Items.Count > shadowsIndex)
                            {
                                tabControl.Items.RemoveAt(shadowsIndex);
                            }
                            shadowsTab = CreateBulkShadowsTab(hairResult, dialog);
                            tabControl.Items.Insert(shadowsIndex, shadowsTab);
                            
                            int dependenciesIndex = tabControl.Items.Count > 4 ? 4 : tabControl.Items.Count;
                            if (tabControl.Items.Count > dependenciesIndex)
                            {
                                tabControl.Items.RemoveAt(dependenciesIndex);
                            }
                            dependenciesTab = CreateBulkDependenciesTab(dependencyResult, _settingsManager?.Settings?.ForceLatestDependencies ?? false, dialog);
                            tabControl.Items.Insert(dependenciesIndex, dependenciesTab);
                            
                            int miscIndex = tabControl.Items.Count > 5 ? 5 : tabControl.Items.Count;
                            if (tabControl.Items.Count > miscIndex)
                            {
                                tabControl.Items.RemoveAt(miscIndex);
                            }
                            miscTab = CreateBulkMiscTab(packages, dialog);
                            tabControl.Items.Insert(miscIndex, miscTab);
                            
                            int summaryIndex = tabControl.Items.Count > 6 ? 6 : tabControl.Items.Count;
                            if (tabControl.Items.Count > summaryIndex)
                            {
                                tabControl.Items.RemoveAt(summaryIndex);
                            }
                            summaryTab = CreateBulkSummaryTab(packages, textureResult, hairResult, dependencyResult, dialog);
                            tabControl.Items.Insert(summaryIndex, summaryTab);
                            
                            // Select the first tab (Textures) to avoid starting on Misc
                            tabControl.SelectedIndex = 0;
                            
                            // Enable the Optimize button now that all tabs are loaded
                            optimizeButton.IsEnabled = true;
                            optimizeButton.Content = packages.Count == 1 ? "Optimize" : "Optimize All";
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Error populating tabs: {ex.Message}\n\nStack trace:\n{ex.StackTrace}", 
                                "Tab Population Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            // Enable button even if there's an error to allow user to proceed
                            optimizeButton.IsEnabled = true;
                            optimizeButton.Content = packages.Count == 1 ? "Optimize" : "Optimize All";
                        }
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                };
                
                dialog.SizeChanged += (s, e) => updateTabWidths();
                
                // Restore selection when dialog closes
                dialog.Closed += async (s, e) =>
                {
                    try
                    {
                        // Use Dispatcher to ensure this runs after the dialog is fully closed
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            // Restore the original package selection in the main grid
                            if (PackageDataGrid?.ItemsSource != null)
                            {
                                PackageDataGrid.SelectedItems.Clear();
                                foreach (var item in PackageDataGrid.ItemsSource)
                                {
                                    if (item is PackageItem pkg && currentPackageSelection.Contains(pkg.Name))
                                    {
                                        PackageDataGrid.SelectedItems.Add(pkg);
                                    }
                                }
                            }
                            
                            // Give the dependencies table time to refresh with the correct packages
                            await Task.Delay(100);
                            
                            // Restore the original dependencies selection
                            if (DependenciesDataGrid?.ItemsSource != null)
                            {
                                DependenciesDataGrid.SelectedItems.Clear();
                                foreach (var item in DependenciesDataGrid.ItemsSource)
                                {
                                    if (item is DependencyItem dep && currentDependenciesSelection.Contains(dep.DisplayName))
                                    {
                                        DependenciesDataGrid.SelectedItems.Add(dep);
                                    }
                                }
                            }
                            
                            UpdateOptimizeCounter();
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error displaying bulk optimization dialog: {ex.Message}",
                              "Display Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };
                
                dialog.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error displaying bulk optimization dialog: {ex.Message}",
                              "Display Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // TODO: Extract bulk optimization tabs to Windows/Optimizers/BulkOptimizationTabs.cs
        // This includes: CreateBulkTextureTab, CreateBulkHairTab, CreateBulkMirrorsTab, CreateBulkShadowsTab, CreateBulkDependenciesTab
        
        /// <summary>
        /// Creates the Texture tab for bulk optimization
        /// </summary>
        // EXTRACTED: Bulk optimization tab methods moved to Windows/Optimizers/BulkOptimizationTabs.cs
        // - CreateBulkTextureTab, CreateBulkHairTab, CreateBulkMirrorsTab, CreateBulkShadowsTab, CreateBulkDependenciesTab

        /// <summary>
        /// Applies bulk package optimizations
        /// </summary>
        private async Task ApplyBulkPackageOptimizations(List<PackageItem> packages, TextureValidator.ValidationResult textureResult, HairOptimizer.OptimizationResult hairResult, DependencyScanner.DependencyScanResult dependencyResult, Window parentDialog, Button optimizeButton, TabControl tabControl, bool minifyJson = false, ProgressBar progressBar = null, TextBlock currentPackageText = null, TextBlock timeInfoText = null, CheckBox alertCheckBox = null, Grid bottomGrid = null)
        {
            try
            {
                // Group items by package (only actual conversions - different from current resolution)
                var texturesByPackage = textureResult.Textures.Where(t => t.HasActualConversion).GroupBy(t => t.PackageName).ToDictionary(g => g.Key, g => g.ToList());
                var hairsByPackage = hairResult.HairItems.Where(h => h.HasConversionSelected).GroupBy(h => h.PackageName).ToDictionary(g => g.Key, g => g.ToList());
                var mirrorsByPackage = hairResult.MirrorItems.Where(m => m.Disable == m.IsCurrentlyOn).GroupBy(m => m.PackageName).ToDictionary(g => g.Key, g => g.ToList());
                var lightsByPackage = hairResult.LightItems.Where(l => l.HasActualShadowConversion).GroupBy(l => l.PackageName).ToDictionary(g => g.Key, g => g.ToList());
                
                // Group dependencies by package
                var disabledDepsByPackage = dependencyResult?.Dependencies?.Where(d => !d.IsEnabled).GroupBy(d => d.PackageName).ToDictionary(g => g.Key, g => g.ToList()) ?? new Dictionary<string, List<DependencyItemModel>>();
                
                // Count dependencies that will be converted to .latest
                // Include dependencies where ForceLatest is true AND they will be converted (not already .latest)
                // Also check the global ForceLatestDependencies setting
                bool forceLatestGlobalSetting = _settingsManager?.Settings?.ForceLatestDependencies ?? false;
                var latestDepsToConvert = dependencyResult?.Dependencies?.Where(d => 
                    d.IsEnabled && 
                    (d.ForceLatest || forceLatestGlobalSetting) && 
                    d.WillBeConvertedToLatest).ToList() ?? new List<DependencyItemModel>();
                var latestDepsByPackage = latestDepsToConvert.GroupBy(d => d.PackageName).ToDictionary(g => g.Key, g => g.ToList()) ?? new Dictionary<string, List<DependencyItemModel>>();
                
                // Also detect packages where dependencies have changed (including re-enabled dependencies)
                var packagesWithDepChanges = new HashSet<string>();
                if (dependencyResult?.Dependencies != null)
                {
                    foreach (var depGroup in dependencyResult.Dependencies.GroupBy(d => d.PackageName))
                    {
                        var currentDisabled = depGroup.Where(d => !d.IsEnabled).Select(d => string.IsNullOrEmpty(d.ParentName) ? d.Name : $"{d.Name}|PARENT:{d.ParentName}").ToHashSet();
                        var previouslyDisabled = depGroup.Where(d => d.IsDisabledByUser).Select(d => string.IsNullOrEmpty(d.ParentName) ? d.Name : $"{d.Name}|PARENT:{d.ParentName}").ToHashSet();
                        
                        if (!currentDisabled.SetEquals(previouslyDisabled))
                        {
                            packagesWithDepChanges.Add(depGroup.Key);
                        }
                    }
                }

                var packagesToOptimize = new HashSet<string>();
                packagesToOptimize.UnionWith(texturesByPackage.Keys);
                packagesToOptimize.UnionWith(hairsByPackage.Keys);
                packagesToOptimize.UnionWith(mirrorsByPackage.Keys);
                packagesToOptimize.UnionWith(lightsByPackage.Keys);
                packagesToOptimize.UnionWith(packagesWithDepChanges);
                packagesToOptimize.UnionWith(latestDepsByPackage.Keys);
                
                // If minify JSON is enabled, include all packages
                if (minifyJson)
                {
                    packagesToOptimize.UnionWith(packages.Select(p => p.Name));
                }

                if (packagesToOptimize.Count == 0)
                {
                    DarkMessageBox.Show("No optimizations selected.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Create cancel flag
                bool shouldCancel = false;
                
                // Replace optimize button with cancel and minimize buttons
                var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
                
                // Create rounded button template for action buttons
                var actionButtonTemplate = new ControlTemplate(typeof(Button));
                var actionButtonBorderFactory = new FrameworkElementFactory(typeof(Border));
                actionButtonBorderFactory.Name = "ActionButtonBorder";
                actionButtonBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS));
                actionButtonBorderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
                actionButtonBorderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
                actionButtonBorderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
                var actionButtonContentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
                actionButtonContentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                actionButtonContentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                actionButtonBorderFactory.AppendChild(actionButtonContentFactory);
                actionButtonTemplate.VisualTree = actionButtonBorderFactory;
                
                var cancelButton = new Button
                {
                    Content = "Cancel",
                    Width = 100,
                    Height = 40,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Background = new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                    Foreground = new SolidColorBrush(Colors.White),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0, 0, 8, 0),
                    Template = actionButtonTemplate
                };
                
                var minimizeButton = new Button
                {
                    Content = "Minimize",
                    Width = 100,
                    Height = 40,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Background = new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                    Foreground = new SolidColorBrush(Colors.White),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Template = actionButtonTemplate
                };
                
                // Add hover effects
                cancelButton.MouseEnter += (s, e) => cancelButton.Background = new SolidColorBrush(Color.FromRgb(229, 57, 53));
                cancelButton.MouseLeave += (s, e) => cancelButton.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                minimizeButton.MouseEnter += (s, e) => minimizeButton.Background = new SolidColorBrush(Color.FromRgb(30, 136, 229));
                minimizeButton.MouseLeave += (s, e) => minimizeButton.Background = new SolidColorBrush(Color.FromRgb(33, 150, 243));
                
                // Cancel button handler
                cancelButton.Click += (s, e) =>
                {
                    shouldCancel = true;
                    cancelButton.IsEnabled = false;
                    cancelButton.Content = "Cancelling...";
                };
                
                // Minimize button handler
                minimizeButton.Click += (s, e) =>
                {
                    parentDialog.WindowState = WindowState.Minimized;
                    this.WindowState = WindowState.Minimized;
                };
                
                buttonPanel.Children.Add(cancelButton);
                buttonPanel.Children.Add(minimizeButton);
                
                // Replace the optimize button with the button panel
                if (bottomGrid != null)
                {
                    int index = bottomGrid.Children.IndexOf(optimizeButton);
                    bottomGrid.Children.Remove(optimizeButton);
                    
                    // Set Grid properties on buttonPanel to match optimizeButton's position
                    Grid.SetRow(buttonPanel, 1);
                    Grid.SetColumn(buttonPanel, 2);
                    buttonPanel.VerticalAlignment = VerticalAlignment.Center;
                    
                    bottomGrid.Children.Insert(index, buttonPanel);
                }
                
                // Disable optimize button (keep reference for later)
                optimizeButton.IsEnabled = false;
                optimizeButton.Content = $"Optimizing 0/{packagesToOptimize.Count}...";

                int completed = 0;
                var errors = new List<string>();
                long totalOriginalSize = 0;
                long totalNewSize = 0;
                var packageDetails = new Dictionary<string, OptimizationDetails>();
                
                // Get backup folder path for summary window
                string backupFolderPath = Path.Combine(_selectedFolder, "ArchivedPackages");

                // Start timing
                var startTime = DateTime.Now;
                var packageStartTimes = new Dictionary<string, DateTime>();

                foreach (var packageName in packagesToOptimize)
                {
                    try
                    {
                        // Check if cancel was requested - break after current package completes
                        if (shouldCancel)
                        {
                            SetStatus("Optimization cancelled by user");
                            break;
                        }
                        
                        // Track start time for this package
                        packageStartTimes[packageName] = DateTime.Now;
                        
                        // Update status with current package
                        if (currentPackageText != null)
                        {
                            currentPackageText.Text = $"Processing: {packageName}";
                        }

                        // Create package-specific results
                        var pkgTextureResult = new TextureValidator.ValidationResult { Textures = texturesByPackage.ContainsKey(packageName) ? texturesByPackage[packageName] : new List<TextureValidator.TextureInfo>() };
                        var pkgHairResult = new HairOptimizer.OptimizationResult
                        {
                            HairItems = hairsByPackage.ContainsKey(packageName) ? hairsByPackage[packageName] : new List<HairOptimizer.HairInfo>(),
                            MirrorItems = mirrorsByPackage.ContainsKey(packageName) ? mirrorsByPackage[packageName] : new List<HairOptimizer.MirrorInfo>(),
                            LightItems = lightsByPackage.ContainsKey(packageName) ? lightsByPackage[packageName] : new List<HairOptimizer.LightInfo>()
                        };

                        // Find the package item
                        var packageItem = packages.FirstOrDefault(p => p.Name == packageName);
                        if (packageItem == null) continue;

                        // Get original size using MetadataKey for accurate lookup (handles multiple versions of same package)
                        PackageFileInfo pkgInfo;
                        if (!string.IsNullOrEmpty(packageItem.MetadataKey))
                        {
                            pkgInfo = _packageFileManager?.GetPackageFileInfoByMetadataKey(packageItem.MetadataKey);
                        }
                        else
                        {
                            pkgInfo = _packageFileManager?.GetPackageFileInfo(packageName);
                        }
                        long originalSize = 0;
                        if (pkgInfo != null && !string.IsNullOrEmpty(pkgInfo.CurrentPath) && System.IO.File.Exists(pkgInfo.CurrentPath))
                        {
                            originalSize = new FileInfo(pkgInfo.CurrentPath).Length;
                        }

                        // Get dependencies for this package
                        var pkgDisabledDeps = disabledDepsByPackage.ContainsKey(packageName) ? disabledDepsByPackage[packageName] : null;
                        var pkgLatestDeps = latestDepsByPackage.ContainsKey(packageName) ? latestDepsByPackage[packageName] : null;
                        
                        // Create progress callback to update UI with detailed operation status
                        PackageRepackager.ProgressCallback progressCallback = (message, current, total) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (currentPackageText != null)
                                {
                                    currentPackageText.Text = $"Processing: {packageName} — {message}";
                                }
                            });
                        };
                        
                        // Call the core optimization logic directly without UI manipulation
                        var optimizationCoreResult = await OptimizeSinglePackageCore(packageName, pkgTextureResult, pkgHairResult, pkgDisabledDeps, pkgLatestDeps, minifyJson, progressCallback);

                        // Get new size
                        long newSize = 0;
                        if (pkgInfo != null && !string.IsNullOrEmpty(pkgInfo.CurrentPath) && System.IO.File.Exists(pkgInfo.CurrentPath))
                        {
                            newSize = new FileInfo(pkgInfo.CurrentPath).Length;
                        }

                        totalOriginalSize += originalSize;
                        totalNewSize += newSize;

                        // Track package details for report
                        var details = new OptimizationDetails
                        {
                            OriginalSize = originalSize,
                            NewSize = newSize,
                            TextureCount = texturesByPackage.ContainsKey(packageName) ? texturesByPackage[packageName].Count(t => t.HasActualConversion) : 0,
                            HairCount = hairsByPackage.ContainsKey(packageName) ? hairsByPackage[packageName].Count : 0,
                            MirrorCount = mirrorsByPackage.ContainsKey(packageName) ? mirrorsByPackage[packageName].Count : 0,
                            LightCount = lightsByPackage.ContainsKey(packageName) ? lightsByPackage[packageName].Count : 0,
                            DisabledDependencies = pkgDisabledDeps?.Count ?? 0,
                            LatestDependencies = pkgLatestDeps?.Count ?? 0,
                            JsonMinified = minifyJson,
                            JsonSizeBeforeMinify = optimizationCoreResult.JsonSizeBeforeMinify,
                            JsonSizeAfterMinify = optimizationCoreResult.JsonSizeAfterMinify,
                            TextureDetailsWithSizes = optimizationCoreResult.TextureDetails ?? new List<string>()
                        };

                        // Capture detailed texture information (only actual conversions - different from current resolution)
                        if (texturesByPackage.ContainsKey(packageName))
                        {
                            foreach (var texture in texturesByPackage[packageName])
                            {
                                // Only include textures with actual conversions (different from current resolution)
                                if (!texture.HasActualConversion) continue;
                                
                                string targetRes = texture.ConvertTo8K ? "8K" :
                                                 texture.ConvertTo4K ? "4K" :
                                                 texture.ConvertTo2K ? "2K" : "2K";
                                // Use OriginalResolution if available (from archive), otherwise use current Resolution
                                string originalRes = !string.IsNullOrEmpty(texture.OriginalResolution) ? texture.OriginalResolution : texture.Resolution;
                                
                                details.TextureDetails.Add(
                                    $"{texture.ReferencedPath} | {originalRes} → {targetRes}");
                            }
                        }

                        // Capture detailed hair information
                        if (hairsByPackage.ContainsKey(packageName))
                        {
                            foreach (var hair in hairsByPackage[packageName])
                            {
                                string originalDensity = hair.CurveDensity > 0 ? hair.CurveDensity.ToString() : "-";
                                int targetDensity = hair.ConvertTo32 ? 32 : hair.ConvertTo24 ? 24 : hair.ConvertTo16 ? 16 : hair.ConvertTo8 ? 8 : 16;
                                details.HairDetails.Add(
                                    $"{hair.SceneFile} - Hair {hair.HairId} ({hair.HairName}) | Density: {originalDensity} †’ {targetDensity}");
                            }
                        }

                        // Capture detailed light information
                        if (lightsByPackage.ContainsKey(packageName))
                        {
                            foreach (var light in lightsByPackage[packageName])
                            {
                                string originalShadow = light.CastShadows ? $"{light.ShadowResolution}px" : "Off";
                                string targetShadow = light.SetShadowsOff ? "Off" :
                                                    light.SetShadows512 ? "512px" :
                                                    light.SetShadows1024 ? "1024px" :
                                                    light.SetShadows2048 ? "2048px" : "Default";
                                details.LightDetails.Add(
                                    $"{light.SceneFile} - Light {light.LightId} ({light.LightName}) | Shadows: {originalShadow} †’ {targetShadow}");
                            }
                        }

                        // Capture disabled dependencies
                        if (pkgDisabledDeps != null)
                        {
                            foreach (var dep in pkgDisabledDeps)
                            {
                                details.DisabledDependencyDetails.Add($"{dep.Name} †’ REMOVED");
                            }
                        }

                        // Capture latest dependencies
                        if (pkgLatestDeps != null)
                        {
                            foreach (var dep in pkgLatestDeps)
                            {
                                // Extract base name without version (e.g., "Package.Name.123" -> "Package.Name")
                                int lastDotIndex = dep.Name.LastIndexOf('.');
                                string baseName = lastDotIndex > 0 ? dep.Name.Substring(0, lastDotIndex) : dep.Name;
                                details.LatestDependencyDetails.Add($"{dep.Name} †’ {baseName}.latest");
                            }
                        }

                        packageDetails[packageName] = details;

                        completed++;
                        
                        // Invalidate package index so next package can be found in its new location
                        _packageFileManager?.InvalidatePackageIndex();
                        
                        // Update progress bar and timing info
                        if (progressBar != null)
                        {
                            progressBar.Maximum = packagesToOptimize.Count;
                            progressBar.Value = completed;
                        }
                        
                        // Calculate timing information
                        var currentElapsedTime = DateTime.Now - startTime;
                        double avgTimePerPackage = currentElapsedTime.TotalSeconds / completed;
                        int remainingPackages = packagesToOptimize.Count - completed;
                        double estimatedRemainingSeconds = avgTimePerPackage * remainingPackages;
                        var estimatedTimeSpan = TimeSpan.FromSeconds(estimatedRemainingSeconds);
                        
                        // Format timing strings
                        string elapsedStr = $"{currentElapsedTime.Hours:D2}:{currentElapsedTime.Minutes:D2}:{currentElapsedTime.Seconds:D2}";
                        string estimatedStr = $"{estimatedTimeSpan.Hours:D2}:{estimatedTimeSpan.Minutes:D2}:{estimatedTimeSpan.Seconds:D2}";
                        
                        if (timeInfoText != null)
                        {
                            timeInfoText.Text = $"Elapsed: {elapsedStr} | Est. Remaining: {estimatedStr}";
                        }
                        
                        optimizeButton.Content = $"Optimizing {completed}/{packagesToOptimize.Count}...";
                        SetStatus($"Optimized {completed}/{packagesToOptimize.Count} packages");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{packageName}: {ex.Message}");
                        // Still track the package even if it failed
                        if (!packageDetails.ContainsKey(packageName))
                        {
                            packageDetails[packageName] = new OptimizationDetails { Error = ex.Message };
                        }
                    }
                }

                // Refresh the data in the tabs to show updated values
                SetStatus($"Refreshing optimization data...");
                await RefreshOptimizationTabsData(packages, textureResult, hairResult, tabControl);

                // Replace cancel/minimize buttons back with optimize button
                if (bottomGrid != null)
                {
                    // Find and remove the button panel (cancel/minimize buttons)
                    var actionButtonPanel = bottomGrid.Children.OfType<StackPanel>().FirstOrDefault();
                    if (actionButtonPanel != null)
                    {
                        int index = bottomGrid.Children.IndexOf(actionButtonPanel);
                        bottomGrid.Children.Remove(actionButtonPanel);
                        
                        // Restore Grid properties on optimizeButton
                        Grid.SetRow(optimizeButton, 1);
                        Grid.SetColumn(optimizeButton, 2);
                        optimizeButton.VerticalAlignment = VerticalAlignment.Center;
                        
                        bottomGrid.Children.Insert(index, optimizeButton);
                    }
                }

                optimizeButton.IsEnabled = true;
                optimizeButton.Content = packages.Count == 1 ? "Optimize" : "Optimize All";

                // Play alert sound if checkbox is checked
                if (alertCheckBox?.IsChecked == true)
                {
                    try
                    {
                        // Play system notification sound
                        System.Media.SystemSounds.Asterisk.Play();
                    }
                    catch
                    {
                        // Silently fail if sound cannot be played
                    }
                }

                // Calculate space saved (handle negative values for size increases)
                long spaceSaved = totalOriginalSize - totalNewSize;
                double percentSaved = totalOriginalSize > 0 ? (100.0 * spaceSaved / totalOriginalSize) : 0;
                bool sizeIncreased = spaceSaved < 0;

                // Calculate elapsed time and skipped packages
                var elapsedTime = DateTime.Now - startTime;
                int totalSelected = packages.Count;
                int packagesSkipped = totalSelected - packagesToOptimize.Count;

                // Show completion message with storage saved info
                if (completed > 0 || errors.Count > 0)
                {
                    string spaceMessage = sizeIncreased 
                        ? $"Size Increased: {FormatBytes(Math.Abs(spaceSaved))} (+{Math.Abs(percentSaved):F1}%)"
                        : $"Space Saved: {FormatBytes(spaceSaved)} ({percentSaved:F1}%)";
                    
                    SetStatus($"✓ Optimization complete: {completed} packages - {spaceMessage}");
                    
                    // Show new summary window with full report capability
                    var summaryWindow = new OptimizationSummaryWindow();
                    summaryWindow.Owner = this;
                    summaryWindow.ShowInTaskbar = true;
                    summaryWindow.SetSummaryData(
                        completed,
                        errors.Count,
                        spaceSaved,
                        percentSaved,
                        sizeIncreased,
                        totalOriginalSize,
                        totalNewSize,
                        errors,
                        packageDetails,
                        backupFolderPath,
                        elapsedTime,
                        packagesSkipped,
                        totalSelected);
                    
                    summaryWindow.Show();
                    
                    // Close the main optimization window, leaving only the summary
                    parentDialog.Close();
                }

                // Refresh filter lists to update optimization counts after bulk optimization
                RefreshFilterLists();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during bulk optimization: {ex.Message}", "Bulk Optimization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                
                // Restore the optimize button even if error occurred
                if (bottomGrid != null)
                {
                    var actionButtonPanel = bottomGrid.Children.OfType<StackPanel>().FirstOrDefault();
                    if (actionButtonPanel != null)
                    {
                        int index = bottomGrid.Children.IndexOf(actionButtonPanel);
                        bottomGrid.Children.Remove(actionButtonPanel);
                        
                        Grid.SetRow(optimizeButton, 1);
                        Grid.SetColumn(optimizeButton, 2);
                        optimizeButton.VerticalAlignment = VerticalAlignment.Center;
                        
                        bottomGrid.Children.Insert(index, optimizeButton);
                    }
                }
                
                optimizeButton.IsEnabled = true;
                optimizeButton.Content = packages.Count == 1 ? "Optimize" : "Optimize All";
            }
        }

        /// <summary>
        /// Holds optimization result details including texture details and JSON minification sizes
        /// </summary>
        private class OptimizationCoreResult
        {
            public List<string> TextureDetails { get; set; } = new List<string>();
            public long JsonSizeBeforeMinify { get; set; } = 0;
            public long JsonSizeAfterMinify { get; set; } = 0;
        }

        /// <summary>
        /// Core optimization logic without UI manipulation
        /// </summary>
        private async Task<OptimizationCoreResult> OptimizeSinglePackageCore(string packageName, TextureValidator.ValidationResult textureResult, HairOptimizer.OptimizationResult hairResult, List<DependencyItemModel> disabledDependencies = null, List<DependencyItemModel> latestDependencies = null, bool minifyJson = false, PackageRepackager.ProgressCallback progressCallback = null)
        {
            // Get selected items
            var selectedTextures = textureResult.Textures.Where(t => t.HasConversionSelected).ToList();
            var selectedHairs = hairResult.HairItems.Where(h => h.HasConversionSelected).ToList();
            var selectedMirrors = hairResult.MirrorItems.Where(m => m.Disable == m.IsCurrentlyOn).ToList();
            var selectedLights = hairResult.LightItems.Where(l => l.HasActualShadowConversion).ToList();
            bool disableMirrors = selectedMirrors.Any(m => m.Disable);

            // Get package path using MetadataKey for accurate lookup (handles multiple versions of same package)
            PackageFileInfo pkgInfo = null;
            
            // Try to find the package item in the grid to get MetadataKey
            var packageItem = PackageDataGrid?.Items.Cast<PackageItem>()
                .FirstOrDefault(p => p.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase));
            
            if (packageItem != null && !string.IsNullOrEmpty(packageItem.MetadataKey))
            {
                pkgInfo = _packageFileManager?.GetPackageFileInfoByMetadataKey(packageItem.MetadataKey);
            }
            else
            {
                pkgInfo = _packageFileManager?.GetPackageFileInfo(packageName);
            }
            
            if (pkgInfo == null || string.IsNullOrEmpty(pkgInfo.CurrentPath))
            {
                throw new Exception($"Could not find package: {packageName}");
            }

            string packagePath = pkgInfo.CurrentPath;

            // Only support VAR files
            if (!packagePath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Package optimization is only supported for .var files");
            }

            // Check if package already has optimizations and if there are actual changes
            VarMetadata packageMetadata = null;
            if (_packageManager?.PackageMetadata != null)
            {
                _packageManager.PackageMetadata.TryGetValue(packageName, out packageMetadata);
            }
            
            bool isAlreadyOptimized = packageMetadata != null && packageMetadata.IsOptimized;
            
            // Check for dependency changes
            int disabledDepsCount = disabledDependencies?.Count ?? 0;
            int latestDepsCount = latestDependencies?.Count ?? 0;
            bool hasDependencyChanges = disabledDepsCount > 0 || latestDepsCount > 0;
            
            // Determine if ANY optimization is being applied
            // This is resilient to future optimizer additions - any optimization triggers backup
            bool hasAnyOptimizations = selectedTextures.Count > 0 || 
                                      selectedHairs.Count > 0 || 
                                      selectedMirrors.Count > 0 || 
                                      selectedLights.Count > 0 || 
                                      hasDependencyChanges || 
                                      minifyJson;
            
            // Determine if we need to backup (copy to archive)
            // Backup if NOT already optimized AND any optimization is being applied
            // This ensures first-time optimizations always create a backup, regardless of which optimizer features are used
            bool needsBackup = !isAlreadyOptimized && hasAnyOptimizations;

            // Get VAM root folder for ArchivedPackages - always use game root, not subfolder
            string archivedFolder = Path.Combine(_selectedFolder, "ArchivedPackages");

            // Build optimization config
            var config = new PackageRepackager.OptimizationConfig();

            // Add texture conversions
            foreach (var texture in selectedTextures)
            {
                string targetResolution = texture.ConvertTo8K ? "8K" :
                                        texture.ConvertTo4K ? "4K" :
                                        texture.ConvertTo2K ? "2K" : "2K";
                config.TextureConversions[texture.ReferencedPath] =
                    (targetResolution, texture.Width, texture.Height, texture.FileSize);
            }

            // Add hair conversions
            foreach (var hair in selectedHairs)
            {
                int targetDensity = hair.ConvertTo32 ? 32 : hair.ConvertTo24 ? 24 : hair.ConvertTo16 ? 16 : hair.ConvertTo8 ? 8 : 16;
                string key = $"{hair.SceneFile}_{hair.HairId}";
                config.HairConversions[key] = (hair.SceneFile, hair.HairId, targetDensity, hair.HasCurveDensity);
            }

            // Add light shadow conversions
            foreach (var light in selectedLights)
            {
                // Determine shadow resolution based on selected option
                int shadowResolution = 0;
                bool castShadows = false;
                
                if (light.SetShadows2048)
                {
                    shadowResolution = 2048;
                    castShadows = true;
                }
                else if (light.SetShadows1024)
                {
                    shadowResolution = 1024;
                    castShadows = true;
                }
                else if (light.SetShadows512)
                {
                    shadowResolution = 512;
                    castShadows = true;
                }
                else if (light.SetShadowsOff)
                {
                    shadowResolution = 0;
                    castShadows = false;
                }
                // This shouldn't happen since selectedLights is filtered by HasActualShadowConversion
                else
                {
                    continue; // Skip lights with no conversion
                }
                
                string key = $"{light.SceneFile}_{light.LightId}";
                config.LightConversions[key] = (light.SceneFile, light.LightId, castShadows, shadowResolution);
            }

            // Set mirrors flag
            config.DisableMirrors = disableMirrors;
            
            // Set disable morph preload flag from settings
            config.DisableMorphPreload = _settingsManager?.Settings?.DisableMorphPreload ?? true;
            
            // Set IsMorphAsset flag
            config.IsMorphAsset = packageMetadata?.IsMorphAsset ?? false;
            
            // Set dependency modifications
            // Check both the passed dependencies and the global setting
            bool shouldForceLatest = latestDepsCount > 0 || (_settingsManager?.Settings?.ForceLatestDependencies ?? false);
            config.ForceLatestDependencies = shouldForceLatest;
            if (disabledDependencies != null)
            {
                config.DisabledDependencies = disabledDependencies
                    .Select(d => string.IsNullOrEmpty(d.ParentName) ? d.Name : $"{d.Name}|PARENT:{d.ParentName}")
                    .ToList();
            }

            // Set minify JSON flag
            config.MinifyJson = minifyJson;

            // Create repackager and run optimization
            var coreResult = new OptimizationCoreResult();
            await Task.Run(() =>
            {
                var repackager = new PackageRepackager(_imageManager);
                var optimizationResult = repackager.RepackageVarWithOptimizations(
                    packagePath,
                    archivedFolder,
                    config,
                    progressCallback, // Pass progress callback for detailed status updates
                    needsBackup // Pass the backup flag
                );
                coreResult.TextureDetails = optimizationResult.TextureDetails ?? new List<string>();
                coreResult.JsonSizeBeforeMinify = optimizationResult.JsonSizeBeforeMinify;
                coreResult.JsonSizeAfterMinify = optimizationResult.JsonSizeAfterMinify;
            });

            // Refresh package in main grid
            await RefreshSinglePackage(packageName);
            
            return coreResult;
        }

        /// <summary>
        /// Refreshes the data in optimization tabs after optimization completes
        /// </summary>
        private async Task RefreshOptimizationTabsData(List<PackageItem> packages, TextureValidator.ValidationResult textureResult, HairOptimizer.OptimizationResult hairResult, TabControl tabControl)
        {
            try
            {
                // Re-scan all packages to get updated data
                var allTextures = new List<TextureValidator.TextureInfo>();
                var allHairItems = new List<HairOptimizer.HairInfo>();
                var allMirrorItems = new List<HairOptimizer.MirrorInfo>();
                var allLightItems = new List<HairOptimizer.LightInfo>();

                string archivedFolder = Path.Combine(_selectedFolder, "ArchivedPackages");

                foreach (var package in packages)
                {
                    try
                    {
                        string packagePath = GetPackagePath(package);
                        if (string.IsNullOrEmpty(packagePath)) continue;

                        // Scan textures
                        var textureValidator = new Services.TextureValidator();
                        var textureResultRefresh = textureValidator.ValidatePackageTextures(packagePath, archivedFolder);
                        foreach (var texture in textureResultRefresh.Textures)
                        {
                            texture.PackageName = package.Name;
                            allTextures.Add(texture);
                        }

                        // Scan hair, mirrors, and lights
                        var hairOptimizer = new Services.HairOptimizer();
                        var hairResultRefresh = hairOptimizer.ScanPackageHair(packagePath);
                        foreach (var hair in hairResultRefresh.HairItems)
                        {
                            hair.PackageName = package.Name;
                            allHairItems.Add(hair);
                        }
                        foreach (var mirror in hairResultRefresh.MirrorItems)
                        {
                            mirror.PackageName = package.Name;
                            allMirrorItems.Add(mirror);
                        }
                        foreach (var light in hairResultRefresh.LightItems)
                        {
                            light.PackageName = package.Name;
                            allLightItems.Add(light);
                        }
                    }
                    catch { }
                }

                // Update the existing collections and rebind DataGrids
                await Dispatcher.InvokeAsync(() =>
                {
                    // Update textures
                    textureResult.Textures.Clear();
                    foreach (var texture in allTextures)
                    {
                        textureResult.Textures.Add(texture);
                    }

                    // Update hair items
                    hairResult.HairItems.Clear();
                    foreach (var hair in allHairItems)
                    {
                        hairResult.HairItems.Add(hair);
                    }

                    // Update mirror items
                    hairResult.MirrorItems.Clear();
                    foreach (var mirror in allMirrorItems)
                    {
                        hairResult.MirrorItems.Add(mirror);
                    }

                    // Update light items
                    hairResult.LightItems.Clear();
                    foreach (var light in allLightItems)
                    {
                        hairResult.LightItems.Add(light);
                    }

                    // Force DataGrid refresh by finding and rebinding ItemsSource
                    foreach (TabItem tab in tabControl.Items)
                    {
                        if (tab.Content is Grid grid)
                        {
                            foreach (var child in grid.Children)
                            {
                                if (child is DataGrid dataGrid && dataGrid.ItemsSource != null)
                                {
                                    var currentSource = dataGrid.ItemsSource;
                                    dataGrid.ItemsSource = null;
                                    dataGrid.ItemsSource = currentSource;
                                }
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                // Silently fail - not critical if refresh doesn't work
                System.Diagnostics.Debug.WriteLine($"Error refreshing optimization tabs: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles preset optimization when Optimize button is clicked in preset mode
        /// </summary>
        private void OptimizeSelectedPresets_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CustomAtomDataGrid?.SelectedItems.Count == 0)
                {
                    CustomMessageBox.Show("Please select one or more presets to optimize.",
                        "No Presets Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Get selected presets
                var selectedPresets = CustomAtomDataGrid.SelectedItems.Cast<CustomAtomItem>().ToList();
                
                if (selectedPresets.Count == 0)
                {
                    return;
                }

                SetStatus($"Preparing to optimize {selectedPresets.Count} preset(s)...");

                // Display unified optimization dialog for presets
                DisplayUnifiedPresetOptimizationDialog(selectedPresets);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during preset optimization: {ex.Message}",
                    "Preset Optimization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus($"Preset optimization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles scene optimization when Optimize button is clicked in scene mode
        /// </summary>
        private void OptimizeSelectedScenes_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ScenesDataGrid?.SelectedItems.Count == 0)
                {
                    CustomMessageBox.Show("Please select one or more scenes to optimize.",
                        "No Scenes Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Get selected scenes
                var selectedScenes = ScenesDataGrid.SelectedItems.Cast<SceneItem>().ToList();
                
                if (selectedScenes.Count == 0)
                {
                    return;
                }

                SetStatus($"Preparing to optimize {selectedScenes.Count} scene(s)...");

                // Display unified optimization dialog for scenes
                DisplayUnifiedOptimizationDialog(selectedScenes);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during scene optimization: {ex.Message}",
                    "Scene Optimization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus($"Scene optimization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Displays unified optimization dialog that works for both packages and scenes
        /// </summary>
        private void DisplayUnifiedOptimizationDialog(List<SceneItem> scenes)
        {
            try
            {
                SetStatus($"Preparing scene optimization for {scenes.Count} scene(s)...");

                // Create dialog matching package optimizer style
                var dialog = new Window
                {
                    Title = scenes.Count == 1 ? $"Optimize Scene - {scenes[0].DisplayName}" : $"Optimize {scenes.Count} Scenes",
                    Width = 1200,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.CanResize,
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
                };

                // Apply dark theme
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(dialog).EnsureHandle();
                    int useImmersiveDarkMode = 1;
                    if (DwmSetWindowAttribute(hwnd, 20, ref useImmersiveDarkMode, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, 19, ref useImmersiveDarkMode, sizeof(int));
                    }
                }
                catch { }

                var mainGrid = new Grid { Margin = new Thickness(15) };
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                // Create TabControl with styling matching package optimizer
                var tabControl = new TabControl
                {
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    BorderThickness = new Thickness(0)
                };

                // Create custom template for TabItem to match package optimizer dark theme
                var tabItemStyle = new Style(typeof(TabItem));
                
                // Create control template for TabItem
                var tabTemplate = new ControlTemplate(typeof(TabItem));
                var tabBorderFactory = new FrameworkElementFactory(typeof(Border));
                tabBorderFactory.Name = "Border";
                tabBorderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45)));
                tabBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
                tabBorderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Colors.Transparent));
                tabBorderFactory.SetValue(Border.PaddingProperty, new Thickness(20, 10, 20, 10));
                tabBorderFactory.SetValue(Border.MarginProperty, new Thickness(0, 0, 2, 0));
                
                var tabContentPresenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
                tabContentPresenterFactory.SetValue(ContentPresenter.ContentSourceProperty, "Header");
                tabContentPresenterFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                tabContentPresenterFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                
                tabBorderFactory.AppendChild(tabContentPresenterFactory);
                tabTemplate.VisualTree = tabBorderFactory;
                
                // Trigger for selected state
                var selectedTrigger = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
                selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(60, 60, 60)), "Border"));
                selectedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "Border"));
                selectedTrigger.Setters.Add(new Setter(TabItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80))));
                tabTemplate.Triggers.Add(selectedTrigger);
                
                // Trigger for hover state
                var tabHoverTrigger = new Trigger { Property = TabItem.IsMouseOverProperty, Value = true };
                tabHoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(55, 55, 55)), "Border"));
                tabTemplate.Triggers.Add(tabHoverTrigger);
                
                tabItemStyle.Setters.Add(new Setter(TabItem.TemplateProperty, tabTemplate));
                tabItemStyle.Setters.Add(new Setter(TabItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
                tabItemStyle.Setters.Add(new Setter(TabItem.FontSizeProperty, 14.0));
                tabItemStyle.Setters.Add(new Setter(TabItem.FontWeightProperty, FontWeights.SemiBold));
                
                tabControl.Resources.Add(typeof(TabItem), tabItemStyle);

                // Add all tabs: Dependencies, Misc, Summary
                var depsTab = CreateSceneDependenciesTab(scenes);
                tabControl.Items.Add(depsTab);

                var miscTab = CreateSceneMiscTab(scenes);
                tabControl.Items.Add(miscTab);

                var summaryTab = CreateSceneSummaryTab(scenes);
                tabControl.Items.Add(summaryTab);

                Grid.SetRow(tabControl, 0);
                mainGrid.Children.Add(tabControl);

                // Create Optimize button overlaid on top right of tab area
                var optimizeButton = new Button
                {
                    Content = "Optimize",
                    MinWidth = 140,
                    Height = 42,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    Foreground = new SolidColorBrush(Colors.White),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 0, 0)
                };

                // Add hover effect
                optimizeButton.MouseEnter += (s, e) => optimizeButton.Background = new SolidColorBrush(Color.FromRgb(56, 142, 60));
                optimizeButton.MouseLeave += (s, e) => optimizeButton.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));

                // Store reference to tabControl for accessing dependency data later
                var tabControlRef = tabControl;
                
                optimizeButton.Click += async (s, e) =>
                {
                    await ApplySceneOptimizations(scenes, dialog, tabControlRef);
                };

                // Place button on top of TabControl with higher ZIndex
                Panel.SetZIndex(optimizeButton, 1000);
                Grid.SetRow(optimizeButton, 0);
                mainGrid.Children.Add(optimizeButton);

                dialog.Content = mainGrid;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error displaying scene optimization dialog: {ex.Message}",
                    "Display Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Displays unified optimization dialog for presets
        /// </summary>
        private void DisplayUnifiedPresetOptimizationDialog(List<CustomAtomItem> presets)
        {
            try
            {
                SetStatus($"Preparing preset optimization for {presets.Count} preset(s)...");

                // Create dialog matching package optimizer style
                var dialog = new Window
                {
                    Title = presets.Count == 1 ? $"Optimize Preset - {presets[0].DisplayName}" : $"Optimize {presets.Count} Presets",
                    Width = 1200,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.CanResize,
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
                };

                // Apply dark theme
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(dialog).EnsureHandle();
                    int useImmersiveDarkMode = 1;
                    if (DwmSetWindowAttribute(hwnd, 20, ref useImmersiveDarkMode, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, 19, ref useImmersiveDarkMode, sizeof(int));
                    }
                }
                catch { }

                var mainGrid = new Grid { Margin = new Thickness(15) };
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                // Create TabControl with styling matching package optimizer
                var tabControl = new TabControl
                {
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    BorderThickness = new Thickness(0)
                };

                // Create custom template for TabItem to match package optimizer dark theme
                var tabItemStyle = new Style(typeof(TabItem));
                
                // Create control template for TabItem
                var tabTemplate = new ControlTemplate(typeof(TabItem));
                var tabBorderFactory = new FrameworkElementFactory(typeof(Border));
                tabBorderFactory.Name = "Border";
                tabBorderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45)));
                tabBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
                tabBorderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Colors.Transparent));
                tabBorderFactory.SetValue(Border.PaddingProperty, new Thickness(20, 10, 20, 10));
                tabBorderFactory.SetValue(Border.MarginProperty, new Thickness(0, 0, 2, 0));
                
                var tabContentPresenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
                tabContentPresenterFactory.SetValue(ContentPresenter.ContentSourceProperty, "Header");
                tabContentPresenterFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                tabContentPresenterFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                
                tabBorderFactory.AppendChild(tabContentPresenterFactory);
                tabTemplate.VisualTree = tabBorderFactory;
                
                // Trigger for selected state
                var selectedTrigger = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
                selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(60, 60, 60)), "Border"));
                selectedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "Border"));
                selectedTrigger.Setters.Add(new Setter(TabItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80))));
                tabTemplate.Triggers.Add(selectedTrigger);
                
                // Trigger for hover state
                var tabHoverTrigger = new Trigger { Property = TabItem.IsMouseOverProperty, Value = true };
                tabHoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(55, 55, 55)), "Border"));
                tabTemplate.Triggers.Add(tabHoverTrigger);
                
                tabItemStyle.Setters.Add(new Setter(TabItem.TemplateProperty, tabTemplate));
                tabItemStyle.Setters.Add(new Setter(TabItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
                tabItemStyle.Setters.Add(new Setter(TabItem.FontSizeProperty, 14.0));
                tabItemStyle.Setters.Add(new Setter(TabItem.FontWeightProperty, FontWeights.SemiBold));
                
                tabControl.Resources.Add(typeof(TabItem), tabItemStyle);

                // Add tabs: Dependencies, Misc, and Summary
                var depsTab = CreatePresetDependenciesTab(presets);
                tabControl.Items.Add(depsTab);

                var miscTab = CreatePresetMiscTab(presets);
                tabControl.Items.Add(miscTab);

                var summaryTab = CreatePresetSummaryTab(presets);
                tabControl.Items.Add(summaryTab);

                Grid.SetRow(tabControl, 0);
                mainGrid.Children.Add(tabControl);

                // Create Optimize button overlaid on top right of tab area
                var optimizeButton = new Button
                {
                    Content = "Optimize",
                    MinWidth = 140,
                    Height = 42,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    Foreground = new SolidColorBrush(Colors.White),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 0, 0)
                };

                // Add hover effect
                optimizeButton.MouseEnter += (s, e) => optimizeButton.Background = new SolidColorBrush(Color.FromRgb(56, 142, 60));
                optimizeButton.MouseLeave += (s, e) => optimizeButton.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));

                // Store reference to tabControl for accessing dependency data later
                var tabControlRef = tabControl;
                
                optimizeButton.Click += async (s, e) =>
                {
                    await ApplyPresetOptimizations(presets, dialog, tabControlRef);
                };

                // Place button on top of TabControl with higher ZIndex
                Panel.SetZIndex(optimizeButton, 1000);
                Grid.SetRow(optimizeButton, 0);
                mainGrid.Children.Add(optimizeButton);

                dialog.Content = mainGrid;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error displaying preset optimization dialog: {ex.Message}",
                    "Display Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Creates the Hair optimization tab for scenes with configurable density options
        /// </summary>
        private TabItem CreateSceneHairTab(List<SceneItem> scenes)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = $"Hair ({scenes.Sum(s => s.HairCount)})", VerticalAlignment = VerticalAlignment.Center });

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var summaryText = new TextBlock
            {
                Text = $"Found {scenes.Sum(s => s.HairCount)} hair items across {scenes.Count} scene(s)",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(summaryText, 0);
            tabGrid.Children.Add(summaryText);

            // Create DataGrid with hair items
            var dataGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 32,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220))
            };

            // Apply styling
            var columnHeaderStyle = new Style(typeof(DataGridColumnHeader));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(60, 60, 60))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(8, 8, 8, 8)));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
            dataGrid.ColumnHeaderStyle = columnHeaderStyle;

            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(30, 30, 30))));
            rowStyle.Setters.Add(new Setter(DataGridRow.BorderThicknessProperty, new Thickness(0)));
            var alternateTrigger = new Trigger { Property = DataGridRow.AlternationIndexProperty, Value = 1 };
            alternateTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(35, 35, 35))));
            rowStyle.Triggers.Add(alternateTrigger);
            var rowHoverTrigger = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
            rowHoverTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
            rowStyle.Triggers.Add(rowHoverTrigger);
            dataGrid.RowStyle = rowStyle;
            dataGrid.AlternationCount = 2;

            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0)));
            cellStyle.Setters.Add(new Setter(DataGridCell.FocusVisualStyleProperty, null));
            cellStyle.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
            dataGrid.CellStyle = cellStyle;

            // Hair name column
            var nameColumn = new DataGridTextColumn
            {
                Header = "Hair Name",
                Binding = new Binding("Name"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                HeaderStyle = CreateCenteredHeaderStyle()
            };
            nameColumn.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters = {
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)),
                    new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220)))
                }
            };
            dataGrid.Columns.Add(nameColumn);

            // Create hair items from scenes
            var hairItems = new System.Collections.ObjectModel.ObservableCollection<dynamic>();
            foreach (var scene in scenes)
            {
                foreach (var hair in scene.HairItems)
                {
                    hairItems.Add(new { Name = hair });
                }
            }

            dataGrid.ItemsSource = hairItems;
            Grid.SetRow(dataGrid, 2);
            tabGrid.Children.Add(dataGrid);

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Creates the Shadows optimization tab for scenes with configurable options
        /// </summary>
        private TabItem CreateSceneShadowsTab(List<SceneItem> scenes)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = "Shadows", VerticalAlignment = VerticalAlignment.Center });

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var summaryText = new TextBlock
            {
                Text = "Shadow Optimization",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(summaryText, 0);
            tabGrid.Children.Add(summaryText);

            var infoText = new TextBlock
            {
                Text = "Shadows will be disabled (castShadows: false) to improve performance",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(infoText, 2);
            tabGrid.Children.Add(infoText);

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Creates the Mirrors optimization tab for scenes with configurable options
        /// </summary>
        private TabItem CreateSceneMirrorsTab(List<SceneItem> scenes)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = "Mirrors", VerticalAlignment = VerticalAlignment.Center });

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var summaryText = new TextBlock
            {
                Text = "Mirror Optimization",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(summaryText, 0);
            tabGrid.Children.Add(summaryText);

            var infoText = new TextBlock
            {
                Text = "All mirrors will be disabled (active: false) to improve performance",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(infoText, 2);
            tabGrid.Children.Add(infoText);

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Creates the Dependencies optimization tab for scenes with interactive table matching package optimizer style
        /// </summary>
        private TabItem CreateSceneDependenciesTab(List<SceneItem> scenes)
        {
            var allDeps = new HashSet<string>();
            foreach (var scene in scenes)
            {
                foreach (var dep in scene.Dependencies)
                {
                    allDeps.Add(dep);
                }
            }

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = $"Dependencies ({allDeps.Count})", VerticalAlignment = VerticalAlignment.Center });

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Summary + Force .latest checkbox
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Search row
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Table

            // Summary row with Force .latest checkbox
            var summaryRow = new Grid();
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var summaryText = new TextBlock
            {
                Text = $"Found {allDeps.Count} unique dependencies across {scenes.Count} scene(s)",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(summaryText, 0);
            summaryRow.Children.Add(summaryText);

            // Create Force .latest checkbox with modern styling
            var forceLatestCheckbox = new System.Windows.Controls.CheckBox
            {
                Content = "Force .latest",
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(15, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Convert all dependency versions to .latest when optimizing.",
                IsChecked = _settingsManager?.Settings?.ForceLatestDependencies ?? true
            };

            // Apply modern checkbox style
            var checkboxStyle = new Style(typeof(System.Windows.Controls.CheckBox));
            var checkboxTemplate = new ControlTemplate(typeof(System.Windows.Controls.CheckBox));
            
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            gridFactory.SetValue(Grid.BackgroundProperty, Brushes.Transparent);
            
            var col1 = new FrameworkElementFactory(typeof(System.Windows.Controls.ColumnDefinition));
            col1.SetValue(System.Windows.Controls.ColumnDefinition.WidthProperty, GridLength.Auto);
            var col2 = new FrameworkElementFactory(typeof(System.Windows.Controls.ColumnDefinition));
            col2.SetValue(System.Windows.Controls.ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            gridFactory.AppendChild(col1);
            gridFactory.AppendChild(col2);
            
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "CheckBoxBorder";
            borderFactory.SetValue(Grid.ColumnProperty, 0);
            borderFactory.SetValue(Border.WidthProperty, 18.0);
            borderFactory.SetValue(Border.HeightProperty, 18.0);
            borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 48)));
            borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(85, 85, 85)));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS));
            borderFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            
            var pathFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            pathFactory.Name = "CheckMark";
            pathFactory.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0,4 L 3,7 L 8,0"));
            pathFactory.SetValue(System.Windows.Shapes.Path.StrokeProperty, Brushes.White);
            pathFactory.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
            pathFactory.SetValue(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Collapsed);
            pathFactory.SetValue(System.Windows.Shapes.Path.StretchProperty, Stretch.Uniform);
            pathFactory.SetValue(System.Windows.Shapes.Path.MarginProperty, new Thickness(3));
            
            borderFactory.AppendChild(pathFactory);
            gridFactory.AppendChild(borderFactory);
            
            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(Grid.ColumnProperty, 1);
            contentFactory.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 0, 0));
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            gridFactory.AppendChild(contentFactory);
            
            checkboxTemplate.VisualTree = gridFactory;
            
            var checkedTrigger = new Trigger { Property = System.Windows.Controls.CheckBox.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Visible, "CheckMark"));
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
            checkboxTemplate.Triggers.Add(checkedTrigger);
            
            var checkboxHoverTrigger = new Trigger { Property = System.Windows.Controls.CheckBox.IsMouseOverProperty, Value = true };
            checkboxHoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(120, 120, 120)), "CheckBoxBorder"));
            checkboxTemplate.Triggers.Add(checkboxHoverTrigger);
            
            checkboxStyle.Setters.Add(new Setter(System.Windows.Controls.CheckBox.TemplateProperty, checkboxTemplate));
            forceLatestCheckbox.Style = checkboxStyle;

            Grid.SetColumn(forceLatestCheckbox, 1);
            summaryRow.Children.Add(forceLatestCheckbox);

            Grid.SetRow(summaryRow, 0);
            tabGrid.Children.Add(summaryRow);

            // Create search row
            var searchRow = new Grid();
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchBox = new System.Windows.Controls.TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                FontSize = 13,
                Height = 32,
                ToolTip = "Search dependencies by name"
            };

            var searchPlaceholder = new TextBlock
            {
                Text = "Search dependencies...",
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                FontSize = 13,
                Padding = new Thickness(8, 8, 0, 0),
                IsHitTestVisible = false,
                Opacity = 0.6
            };

            var searchPlaceholderPanel = new Grid();
            searchPlaceholderPanel.Children.Add(searchBox);
            searchPlaceholderPanel.Children.Add(searchPlaceholder);

            // Show/hide placeholder based on text
            searchBox.TextChanged += (s, e) =>
            {
                searchPlaceholder.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            };

            Grid.SetColumn(searchPlaceholderPanel, 0);
            searchRow.Children.Add(searchPlaceholderPanel);

            // Clear button
            var clearButton = new Button
            {
                Content = "✓",
                Width = 32,
                Height = 32,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Clear search"
            };

            clearButton.Click += (s, e) =>
            {
                searchBox.Text = "";
            };

            Grid.SetColumn(clearButton, 1);
            searchRow.Children.Add(clearButton);

            Grid.SetRow(searchRow, 2);
            tabGrid.Children.Add(searchRow);

            // Create DataGrid for dependencies
            var dataGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 32,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220))
            };

            // Column header style
            var columnHeaderStyle = new Style(typeof(DataGridColumnHeader));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(60, 60, 60))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(8, 8, 8, 8)));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
            dataGrid.ColumnHeaderStyle = columnHeaderStyle;

            // Row style
            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(30, 30, 30))));
            rowStyle.Setters.Add(new Setter(DataGridRow.BorderThicknessProperty, new Thickness(0)));
            
            var alternateTrigger = new Trigger { Property = DataGridRow.AlternationIndexProperty, Value = 1 };
            alternateTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(35, 35, 35))));
            rowStyle.Triggers.Add(alternateTrigger);
            
            var rowHoverTrigger = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
            rowHoverTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
            rowStyle.Triggers.Add(rowHoverTrigger);
            
            var selectedTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(55, 65, 75))));
            selectedTrigger.Setters.Add(new Setter(DataGridRow.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))));
            rowStyle.Triggers.Add(selectedTrigger);
            
            dataGrid.RowStyle = rowStyle;
            dataGrid.AlternationCount = 2;

            // Cell style
            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0)));
            cellStyle.Setters.Add(new Setter(DataGridCell.FocusVisualStyleProperty, null));
            cellStyle.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
            
            var cellSelectedTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            cellSelectedTrigger.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
            cellSelectedTrigger.Setters.Add(new Setter(DataGridCell.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))));
            cellStyle.Triggers.Add(cellSelectedTrigger);
            
            dataGrid.CellStyle = cellStyle;

            // Dependency name column
            var nameColumn = new DataGridTextColumn 
            { 
                Header = "Dependency Name", 
                Binding = new Binding("DisplayName"), 
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                HeaderStyle = CreateCenteredHeaderStyle()
            };
            nameColumn.ElementStyle = new Style(typeof(TextBlock)) 
            { 
                Setters = { 
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), 
                    new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)), 
                    new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))),
                    new Setter(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Consolas"))
                } 
            };
            dataGrid.Columns.Add(nameColumn);

            // Add Enable/Disable toggle columns with styled bubbles
            AddDependencyToggleColumn(dataGrid, "Enable", true, Color.FromRgb(76, 175, 80));
            AddDependencyToggleColumn(dataGrid, "Disable", false, Color.FromRgb(244, 67, 54));

            // Create dependency items for the grid
            var dependencyItems = new System.Collections.ObjectModel.ObservableCollection<DependencyItemModel>();
            bool initialForceLatestState = _settingsManager?.Settings?.ForceLatestDependencies ?? true;
            foreach (var dep in allDeps.OrderBy(d => d))
            {
                dependencyItems.Add(new DependencyItemModel
                {
                    Name = dep,
                    IsEnabled = true,
                    ForceLatest = initialForceLatestState
                });
            }

            // Create a CollectionViewSource for filtering
            var collectionViewSource = new System.Windows.Data.CollectionViewSource();
            collectionViewSource.Source = dependencyItems;
            
            dataGrid.ItemsSource = collectionViewSource.View;

            // Add search filtering
            searchBox.TextChanged += (s, e) =>
            {
                string searchText = searchBox.Text.ToLower();
                if (string.IsNullOrEmpty(searchText))
                {
                    collectionViewSource.View.Filter = null;
                }
                else
                {
                    collectionViewSource.View.Filter = item =>
                    {
                        if (item is DependencyItemModel dep)
                        {
                            return dep.Name.ToLower().Contains(searchText);
                        }
                        return true;
                    };
                }
            };

            // Update ForceLatest property when checkbox changes and save to settings
            forceLatestCheckbox.Checked += (s, e) =>
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.ForceLatestDependencies = true;
                }
                foreach (var dep in dependencyItems)
                {
                    dep.ForceLatest = true;
                }
            };
            forceLatestCheckbox.Unchecked += (s, e) =>
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.ForceLatestDependencies = false;
                }
                foreach (var dep in dependencyItems)
                {
                    dep.ForceLatest = false;
                }
            };

            Grid.SetRow(dataGrid, 4);
            tabGrid.Children.Add(dataGrid);

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Adds a toggle column for dependency enable/disable with bubble styling
        /// </summary>
        private void AddDependencyToggleColumn(DataGrid dataGrid, string header, bool targetState, Color color)
        {
            var column = new DataGridTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(80),
                HeaderStyle = CreateCenteredHeaderStyle()
            };

            // Create cell template with clickable bubble
            var cellTemplate = new DataTemplate();
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            borderFactory.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            borderFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.SetValue(Border.CursorProperty, System.Windows.Input.Cursors.Hand);
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(5));

            var ellipseFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            ellipseFactory.SetValue(System.Windows.Shapes.Ellipse.WidthProperty, 16.0);
            ellipseFactory.SetValue(System.Windows.Shapes.Ellipse.HeightProperty, 16.0);
            ellipseFactory.SetValue(System.Windows.Shapes.Ellipse.StrokeProperty, new SolidColorBrush(color));
            ellipseFactory.SetValue(System.Windows.Shapes.Ellipse.StrokeThicknessProperty, 2.0);

            // Bind fill based on IsEnabled state
            var fillBinding = new Binding("IsEnabled");
            fillBinding.Converter = new DependencyStateToFillConverter(targetState, color);
            ellipseFactory.SetBinding(System.Windows.Shapes.Ellipse.FillProperty, fillBinding);

            borderFactory.AppendChild(ellipseFactory);

            // Add click handler
            borderFactory.AddHandler(Border.MouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler((s, e) =>
            {
                if (s is Border border && border.DataContext is DependencyItemModel dep)
                {
                    if (targetState)
                    {
                        dep.IsEnabled = true;
                    }
                    else
                    {
                        dep.IsEnabled = false;
                    }
                    e.Handled = true;
                }
            }));

            cellTemplate.VisualTree = borderFactory;
            column.CellTemplate = cellTemplate;

            dataGrid.Columns.Add(column);
        }

        /// <summary>
        /// Creates the Misc tab for scene optimization with JSON minification feature
        /// </summary>
        private TabItem CreateSceneMiscTab(List<SceneItem> scenes)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = "Misc", VerticalAlignment = VerticalAlignment.Center });

            string tooltipText = "MISCELLANEOUS OPTIMIZATIONS:\n\n" +
                "✓ JSON Minification - Removes whitespace and formatting\n" +
                "✓ Reduces file size without changing functionality\n" +
                "✓ Makes files harder to read but smaller on disk\n\n" +
                "Example: Minified JSON removes all indentation and line breaks,\n" +
                "reducing scene file size by 20-40% typically.\n\n" +
                "Note: You can always format JSON later if needed for editing.";
            
            headerPanel.Children.Add(CreateTooltipInfoIcon(tooltipText));

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var summaryText = new TextBlock
            {
                Text = "Additional Optimization Options",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(summaryText, 0);
            tabGrid.Children.Add(summaryText);

            // Content panel with checkbox
            var contentPanel = new StackPanel { Margin = new Thickness(5) };

            // JSON Minification checkbox with modern styling
            var minifyCheckbox = new System.Windows.Controls.CheckBox
            {
                Name = "MinifyJsonCheckbox",
                Content = "Minify JSON (Remove whitespace and formatting)",
                IsChecked = true,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 0, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Removes all unnecessary whitespace, indentation, and line breaks from JSON files.\nTypically saves 20-40% of file size without affecting functionality.",
                Style = CreateModernCheckboxStyle()
            };
            contentPanel.Children.Add(minifyCheckbox);

            // Info text
            var infoText = new TextBlock
            {
                Text = "JSON minification reduces file size by removing formatting characters (spaces, tabs, newlines).\n\n" +
                       "Benefits:\n" +
                       "  • Smaller file size (20-40% reduction)\n" +
                       "  • Faster loading in VaM\n" +
                       "  • No functional changes to scenes\n\n" +
                       "Note: Minified JSON is harder to read/edit manually, but VaM handles it perfectly.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(25, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            contentPanel.Children.Add(infoText);

            Grid.SetRow(contentPanel, 2);
            tabGrid.Children.Add(contentPanel);

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Creates the Summary tab for scene optimization
        /// </summary>
        private TabItem CreateSceneSummaryTab(List<SceneItem> scenes)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = "Summary", VerticalAlignment = VerticalAlignment.Center });

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var headerText = new TextBlock
            {
                Text = "Optimization Summary",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(headerText, 0);
            tabGrid.Children.Add(headerText);

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var contentPanel = new StackPanel { Margin = new Thickness(5) };

            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"•••••••••••••••••••••••••••••••••••••••••••••••••••••••••••");
            summary.AppendLine($"SCENES TO OPTIMIZE: {scenes.Count}");
            summary.AppendLine($"•••••••••••••••••••••••••••••••••••••••••••••••••••••••••••");
            summary.AppendLine();
            
            foreach (var scene in scenes)
            {
                summary.AppendLine($"📝 {scene.DisplayName}");
                summary.AppendLine($"   ─ Hair Items: {scene.HairCount}");
                summary.AppendLine($"   ─ Clothing Items: {scene.ClothingCount}");
                summary.AppendLine($"   ─ Morphs: {scene.MorphCount}");
                summary.AppendLine($"   └─ Dependencies: {scene.Dependencies.Count}");
                summary.AppendLine();
            }
            
            summary.AppendLine($"─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─");
            summary.AppendLine($"OPTIMIZATIONS TO BE APPLIED:");
            summary.AppendLine($"─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─");
            summary.AppendLine();
            summary.AppendLine("✓ Hair density reduction (max 16)");
            summary.AppendLine("✓ Shadow disabling (castShadows: false)");
            summary.AppendLine("✓ Mirror disabling (active: false)");
            summary.AppendLine("✓ Dependency management (remove/force .latest as configured)");
            summary.AppendLine("✓ JSON minification (optional, see Misc tab)");
            summary.AppendLine();
            summary.AppendLine($"─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─");
            summary.AppendLine($"BACKUP & SAFETY:");
            summary.AppendLine($"─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─");
            summary.AppendLine();
            summary.AppendLine($"📦 Original scenes backed up to: ArchivedPackages/Scenes/");
            summary.AppendLine($"– Optimized scenes marked with lightning symbol");
            summary.AppendLine($"📝 Can re-optimize anytime (reads from backup)");
            summary.AppendLine();
            summary.AppendLine("Click 'Optimize' button to proceed with optimization.");

            var summaryText = new TextBlock
            {
                Text = summary.ToString(),
                FontSize = 12,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            };
            contentPanel.Children.Add(summaryText);

            scrollViewer.Content = contentPanel;
            Grid.SetRow(scrollViewer, 2);
            tabGrid.Children.Add(scrollViewer);

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Applies scene optimizations
        /// </summary>
        private async Task ApplySceneOptimizations(List<SceneItem> scenes, Window parentDialog, TabControl tabControl = null)
        {
            try
            {
                // Start timing
                var startTime = DateTime.Now;
                
                SetStatus($"Optimizing {scenes.Count} scene(s)...");

                // Get dependency settings from the Dependencies tab if available
                var disabledDependencies = new List<string>();
                var forceLatestDependencies = new List<string>();
                bool minifyJson = true; // Default to minify
                
                if (tabControl != null)
                {
                    // Find the Dependencies tab (should be at index 0)
                    if (tabControl.Items.Count > 0)
                    {
                        var depsTab = tabControl.Items[0] as TabItem;
                        if (depsTab?.Content is Grid depsGrid)
                        {
                            // Find the DataGrid in the tab
                            var dataGrid = depsGrid.Children.OfType<DataGrid>().FirstOrDefault();
                            if (dataGrid?.ItemsSource != null)
                            {
                                System.Collections.ObjectModel.ObservableCollection<DependencyItemModel> items = null;
                                
                                // Handle both direct ObservableCollection and CollectionViewSource wrapper
                                if (dataGrid.ItemsSource is System.Collections.ObjectModel.ObservableCollection<DependencyItemModel> directItems)
                                {
                                    items = directItems;
                                }
                                else if (dataGrid.ItemsSource is System.Windows.Data.ListCollectionView collectionView)
                                {
                                    // Extract the underlying source collection from the view
                                    if (collectionView.SourceCollection is System.Collections.ObjectModel.ObservableCollection<DependencyItemModel> sourceItems)
                                    {
                                        items = sourceItems;
                                    }
                                }
                                
                                if (items != null)
                                {
                                    foreach (var item in items)
                                    {
                                        if (!item.IsEnabled)
                                        {
                                            disabledDependencies.Add(item.Name);
                                        }
                                        if (item.ForceLatest)
                                        {
                                            forceLatestDependencies.Add(item.Name);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    // Get Misc tab minify checkbox (should be at index 1)
                    if (tabControl.Items.Count > 1)
                    {
                        var miscTab = tabControl.Items[1] as TabItem;
                        if (miscTab?.Content is Grid miscGrid)
                        {
                            // Find the checkbox recursively by type
                            var minifyCheckbox = FindVisualChild<System.Windows.Controls.CheckBox>(miscGrid);
                            if (minifyCheckbox != null)
                            {
                                minifyJson = minifyCheckbox.IsChecked == true;
                            }
                        }
                    }
                }

                // Create backup folder
                string archiveFolder = Path.Combine(_selectedFolder, "ArchivedPackages", "Scenes");
                Directory.CreateDirectory(archiveFolder);

                int optimized = 0;
                var errors = new List<string>();
                var sceneDetails = new Dictionary<string, OptimizationDetails>();
                long totalOriginalSize = 0;
                long totalNewSize = 0;

                foreach (var scene in scenes)
                {
                    var details = new OptimizationDetails();
                    
                    try
                    {
                        if (string.IsNullOrEmpty(scene.FilePath) || !File.Exists(scene.FilePath))
                        {
                            errors.Add($"{scene.DisplayName}: Scene file not found");
                            details.Error = "Scene file not found";
                            sceneDetails[scene.DisplayName] = details;
                            continue;
                        }

                        // Backup original scene
                        string backupPath = Path.Combine(archiveFolder, Path.GetFileName(scene.FilePath));
                        if (!File.Exists(backupPath))
                        {
                            File.Copy(scene.FilePath, backupPath, overwrite: false);
                        }

                        // Read scene JSON
                        string jsonContent = File.ReadAllText(scene.FilePath);
                        long originalSize = System.Text.Encoding.UTF8.GetByteCount(jsonContent);
                        details.OriginalSize = originalSize;
                        totalOriginalSize += originalSize;
                        
                        string optimizedJson = jsonContent;

                        // Track JSON minification sizes if enabled
                        if (minifyJson)
                        {
                            details.JsonSizeBeforeMinify = System.Text.Encoding.UTF8.GetByteCount(jsonContent);
                        }

                        // Apply optimizations
                        optimizedJson = OptimizeSceneJson(optimizedJson, forceLatestDependencies, disabledDependencies, minifyJson);

                        // Track JSON minification
                        if (minifyJson)
                        {
                            details.JsonSizeAfterMinify = System.Text.Encoding.UTF8.GetByteCount(optimizedJson);
                            details.JsonMinified = true;
                        }

                        long newSize = System.Text.Encoding.UTF8.GetByteCount(optimizedJson);
                        details.NewSize = newSize;
                        totalNewSize += newSize;

                        // Track optimizations applied
                        details.HairCount = scene.HairCount;
                        details.MirrorCount = scene.Dependencies.Count(d => d.Contains("Mirror", StringComparison.OrdinalIgnoreCase));
                        details.DisabledDependencies = disabledDependencies.Count;
                        details.LatestDependencies = forceLatestDependencies.Count;

                        // Only write if content changed
                        bool contentChanged = optimizedJson != jsonContent;
                        
                        if (contentChanged)
                        {
                            // Write optimized scene
                            File.WriteAllText(scene.FilePath, optimizedJson);
                            SetStatus($"Scene '{scene.DisplayName}' modified and written to disk");
                        }
                        else
                        {
                            SetStatus($"Scene '{scene.DisplayName}' - no changes needed");
                        }

                        // Mark scene as optimized
                        scene.IsOptimized = true;

                        optimized++;
                        SetStatus($"Optimized {optimized}/{scenes.Count} scenes");
                        
                        sceneDetails[scene.DisplayName] = details;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{scene.DisplayName}: {ex.Message}");
                        details.Error = ex.Message;
                        sceneDetails[scene.DisplayName] = details;
                    }
                }

                SetStatus($"✓ Scene optimization complete: {optimized} scenes optimized");

                // Close the optimization dialog
                parentDialog?.Close();

                // Calculate savings
                long spaceSaved = totalOriginalSize - totalNewSize;
                double percentSaved = totalOriginalSize > 0 ? (100.0 * spaceSaved / totalOriginalSize) : 0;
                bool sizeIncreased = spaceSaved < 0;

                // Calculate elapsed time
                var elapsedTime = DateTime.Now - startTime;
                int totalSelected = scenes.Count;

                // Show summary window with detailed report
                var summaryWindow = new OptimizationSummaryWindow();
                summaryWindow.Owner = this;
                summaryWindow.ShowInTaskbar = true;
                summaryWindow.SetSummaryData(
                    optimized,
                    errors.Count,
                    spaceSaved,
                    percentSaved,
                    sizeIncreased,
                    totalOriginalSize,
                    totalNewSize,
                    errors,
                    sceneDetails,
                    archiveFolder,
                    elapsedTime,
                    0, // scenes don't have skipped count (all are processed)
                    totalSelected);
                
                summaryWindow.Show();

                // Refresh scene list
                await LoadScenesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying scene optimizations: {ex.Message}",
                    "Optimization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus($"Scene optimization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Optimizes scene JSON by applying various performance improvements
        /// </summary>
        private string OptimizeSceneJson(string jsonContent, List<string> forceLatestDependencies, List<string> disabledDependencies, bool minifyJson = false)
        {
            try
            {
                
                int originalLength = jsonContent.Length;
                
                // Parse JSON into a mutable document
                using var doc = System.Text.Json.JsonDocument.Parse(jsonContent);
                // Minified JSON has no indentation, formatted JSON has indentation
                var options = new System.Text.Json.JsonWriterOptions { Indented = !minifyJson };
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.Text.Json.Utf8JsonWriter(stream, options);
                
                // Process the JSON tree and add optimization flag
                ProcessJsonElementWithOptimizationFlag(doc.RootElement, writer, forceLatestDependencies, disabledDependencies);
                writer.Flush();
                
                string optimized = System.Text.Encoding.UTF8.GetString(stream.ToArray());

                // Note: Hair density, mirrors, and shadows are handled in ProcessJsonElement

                int finalLength = optimized.Length;

                return optimized;
            }
            catch
            {
                // If optimization fails, return original
                return jsonContent;
            }
        }

        /// <summary>
        /// Processes root JSON element and adds optimization flag
        /// </summary>
        private void ProcessJsonElementWithOptimizationFlag(System.Text.Json.JsonElement element, System.Text.Json.Utf8JsonWriter writer,
            List<string> forceLatestDeps, List<string> disabledDeps)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                writer.WriteStartObject();
                
                // Add optimization flag at the beginning
                writer.WriteBoolean("_VPM_Optimized", true);
                writer.WriteString("_VPM_OptimizedDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                
                // Process all existing properties
                foreach (var property in element.EnumerateObject())
                {
                    string propName = property.Name;
                    var propValue = property.Value;
                    
                    // Skip if this is already our optimization flag (shouldn't happen on first pass)
                    if (propName.StartsWith("_VPM_"))
                        continue;
                    
                    writer.WritePropertyName(propName);
                    ProcessJsonElement(propValue, writer, forceLatestDeps, disabledDeps);
                }
                
                writer.WriteEndObject();
            }
            else
            {
                ProcessJsonElement(element, writer, forceLatestDeps, disabledDeps);
            }
        }

        /// <summary>
        /// Recursively processes JSON elements and applies optimizations
        /// </summary>
        private void ProcessJsonElement(System.Text.Json.JsonElement element, System.Text.Json.Utf8JsonWriter writer, 
            List<string> forceLatestDeps, List<string> disabledDeps)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    ProcessJsonObject(element, writer, forceLatestDeps, disabledDeps);
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    ProcessJsonArray(element, writer, forceLatestDeps, disabledDeps);
                    break;
                case System.Text.Json.JsonValueKind.String:
                    writer.WriteStringValue(element.GetString());
                    break;
                case System.Text.Json.JsonValueKind.Number:
                    if (element.TryGetInt32(out int intValue))
                        writer.WriteNumberValue(intValue);
                    else if (element.TryGetInt64(out long longValue))
                        writer.WriteNumberValue(longValue);
                    else if (element.TryGetDouble(out double doubleValue))
                        writer.WriteNumberValue(doubleValue);
                    break;
                case System.Text.Json.JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;
                case System.Text.Json.JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;
                case System.Text.Json.JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;
            }
        }

        /// <summary>
        /// Processes a JSON object and applies optimizations
        /// </summary>
        private void ProcessJsonObject(System.Text.Json.JsonElement element, System.Text.Json.Utf8JsonWriter writer,
            List<string> forceLatestDeps, List<string> disabledDeps)
        {
            writer.WriteStartObject();

            foreach (var property in element.EnumerateObject())
            {
                string propName = property.Name;
                var propValue = property.Value;

                // Check if this object should be removed (disabled dependency)
                if (propName == "id" && propValue.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string idValue = propValue.GetString();
                    if (ShouldRemoveDependency(idValue, disabledDeps))
                    {
                        // Skip this entire object by not writing it
                        writer.WriteEndObject();
                        return;
                    }
                }

                // Optimize hair density
                if (propName == "curveDensity" && propValue.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    if (propValue.TryGetInt32(out int density) && density > 16)
                    {
                        writer.WriteNumber(propName, 16);
                        continue;
                    }
                }

                // Disable mirrors
                if (propName == "active" && propValue.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    // Check if parent object has an "id" field starting with "Mirror"
                    bool isMirror = false;
                    foreach (var siblingProp in element.EnumerateObject())
                    {
                        if (siblingProp.Name == "id" && siblingProp.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string idValue = siblingProp.Value.GetString();
                            if (idValue != null && idValue.StartsWith("Mirror", StringComparison.OrdinalIgnoreCase))
                            {
                                isMirror = true;
                                break;
                            }
                        }
                    }
                    
                    if (isMirror)
                    {
                        writer.WriteBoolean(propName, false);
                        continue;
                    }
                }

                // Disable shadows
                if (propName == "castShadows" && propValue.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    writer.WriteBoolean(propName, false);
                    continue;
                }

                // Handle dependency fields (storePath, uid, url, assetUrl, plugin#N, etc.)
                if (propValue.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string stringValue = propValue.GetString();
                    
                    // Check if this is a dependency reference
                    if (!string.IsNullOrEmpty(stringValue) && stringValue.Contains(":/"))
                    {
                        // Check if should be removed
                        if (ShouldRemoveDependency(stringValue, disabledDeps))
                        {
                            // Skip this property
                            continue;
                        }
                        
                        // Check if should convert to .latest
                        string convertedValue = ConvertDependencyToLatest(stringValue, forceLatestDeps);
                        if (convertedValue != stringValue)
                        {
                            writer.WriteString(propName, convertedValue);
                            continue;
                        }
                    }
                }

                // Write property name and recursively process value
                writer.WritePropertyName(propName);
                ProcessJsonElement(propValue, writer, forceLatestDeps, disabledDeps);
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Processes a JSON array and applies optimizations
        /// </summary>
        private void ProcessJsonArray(System.Text.Json.JsonElement element, System.Text.Json.Utf8JsonWriter writer,
            List<string> forceLatestDeps, List<string> disabledDeps)
        {
            writer.WriteStartArray();

            foreach (var item in element.EnumerateArray())
            {
                // Check if this array item is an object with a dependency that should be removed
                if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    bool shouldRemove = false;
                    
                    // Check if object has an "id" field with a disabled dependency
                    foreach (var prop in item.EnumerateObject())
                    {
                        if (prop.Name == "id" && prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string idValue = prop.Value.GetString();
                            if (ShouldRemoveDependency(idValue, disabledDeps))
                            {
                                shouldRemove = true;
                                break;
                            }
                        }
                    }
                    
                    if (shouldRemove)
                    {
                        // Skip this array item
                        continue;
                    }
                }

                ProcessJsonElement(item, writer, forceLatestDeps, disabledDeps);
            }

            writer.WriteEndArray();
        }

        /// <summary>
        /// Checks if a dependency reference should be removed based on disabled dependencies list
        /// </summary>
        private bool ShouldRemoveDependency(string value, List<string> disabledDeps)
        {
            if (disabledDeps == null || disabledDeps.Count == 0 || string.IsNullOrEmpty(value))
                return false;

            // Extract package name from dependency reference (e.g., "creator.package.5:/path" -> "creator.package")
            string packageRef = value;
            int colonIndex = value.IndexOf(":/");
            if (colonIndex > 0)
            {
                packageRef = value.Substring(0, colonIndex);
            }

            // Remove version suffix to get base name
            string baseName = GetBaseDependencyName(packageRef);

            // Check if this base name matches any disabled dependency
            foreach (var disabledDep in disabledDeps)
            {
                string disabledBaseName = GetBaseDependencyName(disabledDep);
                if (baseName.Equals(disabledBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Converts a dependency reference to use .latest version if it's in the forceLatest list
        /// </summary>
        private string ConvertDependencyToLatest(string value, List<string> forceLatestDeps)
        {
            if (forceLatestDeps == null || forceLatestDeps.Count == 0 || string.IsNullOrEmpty(value))
                return value;

            // Extract package reference before ":/" separator
            int colonIndex = value.IndexOf(":/");
            if (colonIndex <= 0)
                return value;

            string packageRef = value.Substring(0, colonIndex);
            string pathPart = value.Substring(colonIndex);

            // Get base name without version
            string baseName = GetBaseDependencyName(packageRef);

            // Check if this dependency should be converted to .latest
            foreach (var forceLatestDep in forceLatestDeps)
            {
                string forceLatestBaseName = GetBaseDependencyName(forceLatestDep);
                if (baseName.Equals(forceLatestBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    string newValue = baseName + ".latest" + pathPart;
                    return newValue;
                }
            }

            return value;
        }

        /// <summary>
        /// Extracts base dependency name by removing version suffix (.N or .latest)
        /// </summary>
        private string GetBaseDependencyName(string depName)
        {
            if (string.IsNullOrEmpty(depName))
                return depName;

            int lastDotIndex = depName.LastIndexOf('.');
            if (lastDotIndex > 0 && lastDotIndex < depName.Length - 1)
            {
                string lastPart = depName.Substring(lastDotIndex + 1);
                
                // Check if it's .latest or a numeric version
                if (lastPart.Equals("latest", StringComparison.OrdinalIgnoreCase) || int.TryParse(lastPart, out _))
                {
                    return depName.Substring(0, lastDotIndex);
                }
            }

            return depName;
        }

        /// <summary>
        /// Checks if a scene JSON file has been optimized by looking for the optimization flag
        /// </summary>
        private bool IsSceneOptimized(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return false;

                string jsonContent = File.ReadAllText(filePath);
                using var doc = System.Text.Json.JsonDocument.Parse(jsonContent);
                
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("_VPM_Optimized", out var optimizedProp))
                    {
                        return optimizedProp.ValueKind == System.Text.Json.JsonValueKind.True;
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks if a preset .vap file has been optimized by looking for the optimization flag
        /// </summary>
        private bool IsPresetOptimized(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return false;

                string jsonContent = File.ReadAllText(filePath);
                using var doc = System.Text.Json.JsonDocument.Parse(jsonContent);
                
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("_VPM_Optimized", out var optimizedProp))
                    {
                        return optimizedProp.ValueKind == System.Text.Json.JsonValueKind.True;
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Applies preset optimizations (dependencies only)
        /// </summary>
        private async Task ApplyPresetOptimizations(List<CustomAtomItem> presets, Window parentDialog, TabControl tabControl)
        {
            try
            {
                // Get dependency data from the Dependencies tab (first tab)
                System.Collections.ObjectModel.ObservableCollection<DependencyItemModel> dependencyItems = null;
                
                if (tabControl.Items.Count > 0)
                {
                    var depsTab = tabControl.Items[0] as TabItem;
                    if (depsTab?.Content is Grid depsGrid)
                    {
                        // Find the DataGrid in the tab
                        var dataGrid = depsGrid.Children.OfType<DataGrid>().FirstOrDefault();
                        if (dataGrid?.ItemsSource != null)
                        {
                            // Handle both direct ObservableCollection and CollectionViewSource wrapper
                            if (dataGrid.ItemsSource is System.Collections.ObjectModel.ObservableCollection<DependencyItemModel> directItems)
                            {
                                dependencyItems = directItems;
                            }
                            else if (dataGrid.ItemsSource is System.Windows.Data.ListCollectionView collectionView)
                            {
                                // Extract the underlying source collection from the view
                                if (collectionView.SourceCollection is System.Collections.ObjectModel.ObservableCollection<DependencyItemModel> sourceItems)
                                {
                                    dependencyItems = sourceItems;
                                }
                            }
                        }
                    }
                }
                
                if (dependencyItems != null)
                {
                    // Get disabled dependencies
                    var disabledDependencies = dependencyItems
                        .Where(d => !d.IsEnabled)
                        .Select(d => d.Name)
                        .ToList();

                    // Get Force .latest setting from global checkbox in Dependencies tab
                    var depsTab = tabControl.Items[0] as TabItem;
                    var depsTabContent = depsTab?.Content as Grid;
                    var summaryRow = depsTabContent?.Children.OfType<Grid>().FirstOrDefault();
                    var forceLatestCheckbox = summaryRow?.Children.OfType<System.Windows.Controls.CheckBox>().FirstOrDefault();
                    
                    bool globalForceLatest = forceLatestCheckbox?.IsChecked ?? true;
                    
                    var forceLatestDependencies = globalForceLatest ? 
                        dependencyItems.Where(d => d.IsEnabled).Select(d => d.Name).ToList() : 
                        new List<string>();

                    // Get minification setting from Misc tab (should be at index 1)
                    bool shouldMinify = true;
                    if (tabControl.Items.Count > 1)
                    {
                        var miscTab = tabControl.Items[1] as TabItem;
                        if (miscTab?.Content is Grid miscGrid)
                        {
                            // Find the checkbox recursively by type
                            var minifyCheckbox = FindVisualChild<System.Windows.Controls.CheckBox>(miscGrid);
                            if (minifyCheckbox != null)
                            {
                                shouldMinify = minifyCheckbox.IsChecked == true;
                            }
                        }
                    }

                    // Close the dialog
                    parentDialog.Close();

                    // Apply optimizations to each preset
                    int optimizedCount = 0;
                    foreach (var preset in presets)
                    {
                        try
                        {
                            if (await OptimizePresetFile(preset.FilePath, disabledDependencies, forceLatestDependencies, shouldMinify))
                            {
                                optimizedCount++;
                                
                                // Update preset status to optimized
                                preset.Status = "Optimized";
                                preset.StatusIcon = "✓"; // Checkmark icon
                                preset.IsOptimized = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Error optimizing preset {preset.DisplayName}:\n{ex.Message}",
                                "Optimization Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }

                    // Optimization complete - no message needed

                    SetStatus($"Optimized {optimizedCount} preset(s)");

                    // Update the dependencies table to show .latest versions
                    await UpdateDependenciesTableAfterOptimization(dependencyItems, forceLatestDependencies, disabledDependencies);
                    
                    // Refresh the preset list to show updated dependency counts and status
                    await RefreshPresetListAfterOptimization(presets);
                    
                    SetStatus($"Successfully optimized {optimizedCount} of {presets.Count} preset(s)");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during preset optimization:\n{ex.Message}",
                    "Optimization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus($"Preset optimization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a backup of the preset file and its preview image in the ArchivedPackages/Presets folder
        /// </summary>
        private async Task CreatePresetBackup(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(_selectedFolder))
                {
                    SetStatus($"Warning: Cannot create backup - no VAM root folder selected");
                    return;
                }

                var fileName = Path.GetFileName(filePath);
                var fileDirectory = Path.GetDirectoryName(filePath);
                
                // Create backup in ArchivedPackages/Presets folder in game root
                var archiveFolder = Path.Combine(_selectedFolder, "ArchivedPackages", "Presets");
                
                // Create archive folder if it doesn't exist
                if (!Directory.Exists(archiveFolder))
                {
                    Directory.CreateDirectory(archiveFolder);
                }
                
                // Use original filename for backup
                var backupPath = Path.Combine(archiveFolder, fileName);
                
                // Copy original preset file to backup location
                await Task.Run(() => File.Copy(filePath, backupPath, true));
                
                // Find and copy associated preview image
                var basePath = Path.ChangeExtension(filePath, null);
                var extensions = new[] { ".jpg", ".jpeg", ".png", ".JPG", ".JPEG", ".PNG" };
                
                foreach (var ext in extensions)
                {
                    var previewPath = basePath + ext;
                    if (File.Exists(previewPath))
                    {
                        var previewFileName = Path.GetFileName(previewPath);
                        var backupPreviewPath = Path.Combine(archiveFolder, previewFileName);
                        
                        await Task.Run(() => File.Copy(previewPath, backupPreviewPath, true));
                        break; // Only copy the first preview image found
                    }
                }
            }
            catch (Exception ex)
            {
                // Log backup error but don't fail the optimization
                SetStatus($"Warning: Could not create backup for {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the dependencies table to show .latest versions and disabled dependencies
        /// </summary>
        private async Task UpdateDependenciesTableAfterOptimization(
            System.Collections.ObjectModel.ObservableCollection<DependencyItemModel> dependencyItems,
            List<string> forceLatestDependencies,
            List<string> disabledDependencies)
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var item in dependencyItems)
                    {
                        // Update dependencies that were forced to .latest
                        if (forceLatestDependencies.Contains(item.Name))
                        {
                            item.ForceLatest = true;
                            // Update the name to show .latest
                            if (!item.Name.EndsWith(".latest", System.StringComparison.OrdinalIgnoreCase))
                            {
                                item.Name = item.Name.Split('.')[0] + ".latest";
                            }
                        }
                        
                        // Mark disabled dependencies as disabled by user
                        if (disabledDependencies.Contains(item.Name) || disabledDependencies.Any(d => item.Name.StartsWith(d.Split('.')[0])))
                        {
                            item.IsDisabledByUser = true;
                        }
                    }
                    
                    // Force refresh of the dependencies DataGrid if visible
                    var dependenciesTab = GetVisibleDependenciesTab();
                    if (dependenciesTab?.Content is Grid tabGrid)
                    {
                        var dataGrid = tabGrid.Children.OfType<DataGrid>().FirstOrDefault();
                        if (dataGrid != null)
                        {
                            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(dataGrid.ItemsSource);
                            view?.Refresh();
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Warning: Could not update dependencies table: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the currently visible dependencies tab from any open optimization dialogs
        /// </summary>
        private TabItem GetVisibleDependenciesTab()
        {
            try
            {
                // Look for any TabControl in the visual tree that might contain the dependencies tab
                var windows = System.Windows.Application.Current.Windows;
                foreach (Window window in windows)
                {
                    if (window.IsVisible && window.Title.Contains("Optimize"))
                    {
                        var tabControls = FindVisualChildren<TabControl>(window);
                        foreach (var tabControl in tabControls)
                        {
                            var depsTab = tabControl.Items.Cast<TabItem>().FirstOrDefault(t =>
                            {
                                if (t.Header is StackPanel headerPanel)
                                {
                                    var textBlock = headerPanel.Children.OfType<TextBlock>().FirstOrDefault();
                                    return textBlock?.Text?.Contains("Dependencies") == true;
                                }
                                return t.Header?.ToString()?.Contains("Dependencies") == true;
                            });
                            if (depsTab != null)
                                return depsTab;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Helper method to find visual children of a specific type
        /// </summary>
        private IEnumerable<T> FindVisualChildren<T>(DependencyObject obj) where T : DependencyObject
        {
            if (obj != null)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
                {
                    DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(obj, i);
                    if (child != null && child is T)
                    {
                        yield return (T)child;
                    }
                    foreach (T childOfChild in FindVisualChildren<T>(child))
                    {
                        yield return childOfChild;
                    }
                }
            }
        }

        /// <summary>
        /// Refreshes the preset list after optimization to show updated dependencies and status
        /// </summary>
        private async Task RefreshPresetListAfterOptimization(List<CustomAtomItem> optimizedPresets)
        {
            try
            {
                // Re-scan the optimized preset files to update their dependency data
                await Task.Run(() =>
                {
                    foreach (var preset in optimizedPresets)
                    {
                        // Re-parse the preset file to get updated dependencies
                        Services.PresetScanner.ParsePresetDependencies(preset);
                    }
                });

                // Refresh the UI on the main thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // Trigger UI refresh for the preset DataGrid
                    if (CustomAtomDataGrid?.ItemsSource is System.Collections.ObjectModel.ObservableCollection<CustomAtomItem> collection)
                    {
                        // Force refresh of the collection view
                        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(collection);
                        view?.Refresh();
                    }

                    // If presets are currently selected, refresh the dependencies table
                    var selectedPresets = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>().ToList();
                    if (selectedPresets?.Any() == true)
                    {
                        // Check if any of the selected presets were optimized
                        var selectedOptimizedPresets = selectedPresets.Where(p => optimizedPresets.Any(op => op.FilePath == p.FilePath)).ToList();
                        if (selectedOptimizedPresets.Any())
                        {
                            // Refresh the dependencies display for the currently selected presets
                            PopulatePresetDependencies(selectedPresets);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Warning: Could not refresh preset list: {ex.Message}");
            }
        }

        /// <summary>
        /// Optimizes a single preset file by modifying its dependencies and optionally minifying JSON
        /// </summary>
        private async Task<bool> OptimizePresetFile(string filePath, List<string> disabledDependencies, List<string> forceLatestDependencies, bool shouldMinify)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return false;
                }

                // Create backup in archive folder
                await CreatePresetBackup(filePath);

                // Read the preset file
                string jsonContent = await File.ReadAllTextAsync(filePath);
                
                // Parse JSON
                using var document = System.Text.Json.JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                // Create optimized JSON
                using var stream = new MemoryStream();
                using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = !shouldMinify });
                
                ProcessPresetJsonElement(root, writer, forceLatestDependencies, disabledDependencies, true);
                
                writer.Flush();
                string optimizedJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());

                // Write back to file
                await File.WriteAllTextAsync(filePath, optimizedJson);
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Processes a JSON element for preset optimization
        /// </summary>
        private void ProcessPresetJsonElement(System.Text.Json.JsonElement element, System.Text.Json.Utf8JsonWriter writer,
            List<string> forceLatestDeps, List<string> disabledDeps, bool isRootElement = false)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    ProcessPresetJsonObject(element, writer, forceLatestDeps, disabledDeps, isRootElement);
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    ProcessPresetJsonArray(element, writer, forceLatestDeps, disabledDeps);
                    break;
                case System.Text.Json.JsonValueKind.String:
                    string stringValue = element.GetString();
                    // Check if this is a dependency reference that should be converted to .latest
                    string convertedValue = ConvertDependencyToLatest(stringValue, forceLatestDeps);
                    writer.WriteStringValue(convertedValue);
                    break;
                case System.Text.Json.JsonValueKind.Number:
                    if (element.TryGetInt32(out int intValue))
                        writer.WriteNumberValue(intValue);
                    else if (element.TryGetDouble(out double doubleValue))
                        writer.WriteNumberValue(doubleValue);
                    break;
                case System.Text.Json.JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;
                case System.Text.Json.JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;
                case System.Text.Json.JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;
            }
        }

        /// <summary>
        /// Processes a JSON object for preset optimization
        /// </summary>
        private void ProcessPresetJsonObject(System.Text.Json.JsonElement element, System.Text.Json.Utf8JsonWriter writer,
            List<string> forceLatestDeps, List<string> disabledDeps, bool isRootObject = false)
        {
            writer.WriteStartObject();

            // Add optimization flag at the beginning if this is the root object
            if (isRootObject)
            {
                writer.WriteBoolean("_VPM_Optimized", true);
                writer.WriteString("_VPM_OptimizedDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            foreach (var property in element.EnumerateObject())
            {
                string propName = property.Name;
                var propValue = property.Value;

                // Check if this object should be removed (disabled dependency)
                if (propValue.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string stringValue = propValue.GetString();
                    if (ShouldRemoveDependency(stringValue, disabledDeps))
                    {
                        // Skip this property
                        continue;
                    }
                    
                    // Check if should convert to .latest
                    string convertedValue = ConvertDependencyToLatest(stringValue, forceLatestDeps);
                    if (convertedValue != stringValue)
                    {
                        writer.WriteString(propName, convertedValue);
                        continue;
                    }
                }

                // Write property name and recursively process value
                writer.WritePropertyName(propName);
                ProcessPresetJsonElement(propValue, writer, forceLatestDeps, disabledDeps, false);
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Processes a JSON array for preset optimization
        /// </summary>
        private void ProcessPresetJsonArray(System.Text.Json.JsonElement element, System.Text.Json.Utf8JsonWriter writer,
            List<string> forceLatestDeps, List<string> disabledDeps)
        {
            writer.WriteStartArray();

            foreach (var item in element.EnumerateArray())
            {
                // Check if this array item is an object with a dependency that should be removed
                if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    bool shouldRemove = false;
                    
                    // Check if object has an "id" field with a disabled dependency
                    foreach (var prop in item.EnumerateObject())
                    {
                        if (prop.Name == "id" && prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string idValue = prop.Value.GetString();
                            if (ShouldRemoveDependency(idValue, disabledDeps))
                            {
                                shouldRemove = true;
                                break;
                            }
                        }
                    }
                    
                    if (shouldRemove)
                    {
                        continue; // Skip this array item
                    }
                }

                ProcessPresetJsonElement(item, writer, forceLatestDeps, disabledDeps, false);
            }

            writer.WriteEndArray();
        }

        /// <summary>
        /// Creates the Misc tab for preset optimization with JSON minification feature
        /// </summary>
        private TabItem CreatePresetMiscTab(List<CustomAtomItem> presets)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = "Misc", VerticalAlignment = VerticalAlignment.Center });

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var summaryText = new TextBlock
            {
                Text = "Miscellaneous optimization options",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(summaryText, 0);
            tabGrid.Children.Add(summaryText);

            // Scrollable content
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var contentPanel = new StackPanel { Margin = new Thickness(5) };

            // JSON Minification Section
            var minifyHeader = new TextBlock
            {
                Text = "📄 JSON Optimization",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 0, 0, 15)
            };
            contentPanel.Children.Add(minifyHeader);

            var minifyPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 20) };

            // Minify JSON checkbox
            var minifyCheckbox = new System.Windows.Controls.CheckBox
            {
                Content = "Minify JSON files",
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 13,
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Remove unnecessary whitespace and formatting from preset JSON files to reduce file size.",
                IsChecked = _settingsManager?.Settings?.MinifyJsonFiles ?? true
            };
            minifyPanel.Children.Add(minifyCheckbox);

            var minifyDescription = new TextBlock
            {
                Text = "Removes unnecessary whitespace and formatting from preset files to reduce file size.\n" +
                       "This can significantly reduce the size of large preset files with minimal impact on loading times.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(25, 0, 0, 0)
            };
            minifyPanel.Children.Add(minifyDescription);

            contentPanel.Children.Add(minifyPanel);

            // File Processing Section
            var processingHeader = new TextBlock
            {
                Text = "⚙️ Processing Options",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 10, 0, 15)
            };
            contentPanel.Children.Add(processingHeader);

            var processingPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 20) };

            var processingDescription = new TextBlock
            {
                Text = $"• {presets.Count} preset file(s) will be processed\n" +
                       "• Files are modified in-place (no backup created)\n" +
                       "• Original file timestamps are preserved",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                TextWrapping = TextWrapping.Wrap
            };
            processingPanel.Children.Add(processingDescription);

            contentPanel.Children.Add(processingPanel);

            scrollViewer.Content = contentPanel;
            Grid.SetRow(scrollViewer, 2);
            tabGrid.Children.Add(scrollViewer);

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Creates the Dependencies optimization tab for presets with interactive table matching scene mode style exactly
        /// </summary>
        private TabItem CreatePresetDependenciesTab(List<CustomAtomItem> presets)
        {
            var allDeps = new HashSet<string>();
            foreach (var preset in presets)
            {
                if (preset.Dependencies != null)
                {
                    foreach (var dep in preset.Dependencies)
                    {
                        allDeps.Add(dep);
                    }
                }
            }

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = $"Dependencies ({allDeps.Count})", VerticalAlignment = VerticalAlignment.Center });

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Summary + Force .latest checkbox
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Search row
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Table

            // Summary row with Force .latest checkbox
            var summaryRow = new Grid();
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var summaryText = new TextBlock
            {
                Text = $"Found {allDeps.Count} unique dependencies across {presets.Count} preset(s)",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(summaryText, 0);
            summaryRow.Children.Add(summaryText);

            // Create Force .latest checkbox with modern styling
            var forceLatestCheckbox = new System.Windows.Controls.CheckBox
            {
                Content = "Force .latest",
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(15, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Convert all dependency versions to .latest when optimizing.",
                IsChecked = _settingsManager?.Settings?.ForceLatestDependencies ?? true
            };

            Grid.SetColumn(forceLatestCheckbox, 1);
            summaryRow.Children.Add(forceLatestCheckbox);

            Grid.SetRow(summaryRow, 0);
            tabGrid.Children.Add(summaryRow);

            // Create search row
            var searchRow = new Grid();
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchBox = new System.Windows.Controls.TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                FontSize = 13,
                Height = 32,
                ToolTip = "Search dependencies by name"
            };

            var searchPlaceholder = new TextBlock
            {
                Text = "Search dependencies...",
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                FontSize = 13,
                Padding = new Thickness(8, 8, 0, 0),
                IsHitTestVisible = false,
                Opacity = 0.6
            };

            var searchPlaceholderPanel = new Grid();
            searchPlaceholderPanel.Children.Add(searchBox);
            searchPlaceholderPanel.Children.Add(searchPlaceholder);

            // Show/hide placeholder based on text
            searchBox.TextChanged += (s, e) =>
            {
                searchPlaceholder.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            };

            Grid.SetColumn(searchPlaceholderPanel, 0);
            searchRow.Children.Add(searchPlaceholderPanel);

            // Clear button
            var clearButton = new Button
            {
                Content = "✓",
                Width = 32,
                Height = 32,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Clear search"
            };

            clearButton.Click += (s, e) =>
            {
                searchBox.Text = "";
            };

            Grid.SetColumn(clearButton, 1);
            searchRow.Children.Add(clearButton);

            Grid.SetRow(searchRow, 2);
            tabGrid.Children.Add(searchRow);

            // Create DataGrid for dependencies - matching scene mode exactly
            var dataGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 32,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220))
            };

            // Column header style - exact match to scene mode
            var columnHeaderStyle = new Style(typeof(DataGridColumnHeader));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(60, 60, 60))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(8, 8, 8, 8)));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
            dataGrid.ColumnHeaderStyle = columnHeaderStyle;

            // Row style - exact match to scene mode
            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(30, 30, 30))));
            rowStyle.Setters.Add(new Setter(DataGridRow.BorderThicknessProperty, new Thickness(0)));
            
            var alternateTrigger = new Trigger { Property = DataGridRow.AlternationIndexProperty, Value = 1 };
            alternateTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(35, 35, 35))));
            rowStyle.Triggers.Add(alternateTrigger);
            
            var rowHoverTrigger = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
            rowHoverTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
            rowStyle.Triggers.Add(rowHoverTrigger);
            
            var selectedTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(55, 65, 75))));
            selectedTrigger.Setters.Add(new Setter(DataGridRow.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))));
            rowStyle.Triggers.Add(selectedTrigger);
            
            dataGrid.RowStyle = rowStyle;
            dataGrid.AlternationCount = 2;

            // Cell style - exact match to scene mode
            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0)));
            cellStyle.Setters.Add(new Setter(DataGridCell.FocusVisualStyleProperty, null));
            cellStyle.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
            
            var cellSelectedTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            cellSelectedTrigger.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
            cellSelectedTrigger.Setters.Add(new Setter(DataGridCell.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))));
            cellStyle.Triggers.Add(cellSelectedTrigger);
            
            dataGrid.CellStyle = cellStyle;

            // Dependency name column - exact match to scene mode
            var nameColumn = new DataGridTextColumn 
            { 
                Header = "Dependency Name", 
                Binding = new Binding("DisplayName"), 
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                HeaderStyle = CreateCenteredHeaderStyle()
            };
            nameColumn.ElementStyle = new Style(typeof(TextBlock)) 
            { 
                Setters = { 
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), 
                    new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)), 
                    new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))),
                    new Setter(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Consolas"))
                } 
            };
            dataGrid.Columns.Add(nameColumn);

            // Add Enable/Disable toggle columns with styled bubbles - exact match to scene mode
            AddDependencyToggleColumn(dataGrid, "Enable", true, Color.FromRgb(76, 175, 80));
            AddDependencyToggleColumn(dataGrid, "Disable", false, Color.FromRgb(244, 67, 54));

            // Create dependency items for the grid - using DependencyItemModel like scene mode
            var dependencyItems = new System.Collections.ObjectModel.ObservableCollection<DependencyItemModel>();
            bool initialForceLatestState = _settingsManager?.Settings?.ForceLatestDependencies ?? true;
            foreach (var dep in allDeps.OrderBy(d => d))
            {
                dependencyItems.Add(new DependencyItemModel
                {
                    Name = dep,
                    IsEnabled = true,
                    ForceLatest = initialForceLatestState
                });
            }

            dataGrid.ItemsSource = dependencyItems;
            Grid.SetRow(dataGrid, 4);
            tabGrid.Children.Add(dataGrid);

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Creates the Summary tab for preset optimization
        /// </summary>
        private TabItem CreatePresetSummaryTab(List<CustomAtomItem> presets)
        {
            var tab = new TabItem
            {
                Header = "Summary",
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons

            // Header
            var headerText = new TextBlock
            {
                Text = "Preset Optimization Summary",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(headerText, 0);
            tabGrid.Children.Add(headerText);

            // Scrollable content
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var contentPanel = new StackPanel { Margin = new Thickness(5) };

            // Preset Summary Section
            var presetHeader = new TextBlock
            {
                Text = " Preset Optimizations",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            contentPanel.Children.Add(presetHeader);

            var presetInfoPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 15) };
            
            var countText = new TextBlock
            {
                Text = $" {presets.Count} preset(s) selected for optimization",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            presetInfoPanel.Children.Add(countText);

            foreach (var preset in presets)
            {
                var itemText = new TextBlock
                {
                    Text = $"  • {preset.DisplayName}",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(0, 2, 0, 2)
                };
                presetInfoPanel.Children.Add(itemText);
            }

            contentPanel.Children.Add(presetInfoPanel);

            // Dependencies Summary Section
            var allDeps = new HashSet<string>();
            foreach (var preset in presets)
            {
                if (preset.Dependencies != null)
                {
                    foreach (var dep in preset.Dependencies)
                    {
                        allDeps.Add(dep);
                    }
                }
            }

            var depsHeader = new TextBlock
            {
                Text = " Dependency Optimizations",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 10, 0, 10)
            };
            contentPanel.Children.Add(depsHeader);

            var depsInfoPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 15) };
            
            var depsCountText = new TextBlock
            {
                Text = $" {allDeps.Count} unique dependencies found",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            depsInfoPanel.Children.Add(depsCountText);

            var depsNoteText = new TextBlock
            {
                Text = "  • Dependencies can be disabled or forced to .latest version",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2)
            };
            depsInfoPanel.Children.Add(depsNoteText);

            contentPanel.Children.Add(depsInfoPanel);

            // Important Notes Section
            var notesHeader = new TextBlock
            {
                Text = "⚠️ Important Notes",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                Margin = new Thickness(0, 20, 0, 10)
            };
            contentPanel.Children.Add(notesHeader);

            var notesPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 0) };
            
            var note1 = new TextBlock
            {
                Text = "• Preset files will be modified in-place (no backup created)",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
            notesPanel.Children.Add(note1);

            var note2 = new TextBlock
            {
                Text = "• Disabled dependencies will be removed from preset files",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
            notesPanel.Children.Add(note2);

            var note3 = new TextBlock
            {
                Text = "• Force .latest will convert version numbers to .latest",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
            notesPanel.Children.Add(note3);

            contentPanel.Children.Add(notesPanel);

            scrollViewer.Content = contentPanel;
            Grid.SetRow(scrollViewer, 2);
            tabGrid.Children.Add(scrollViewer);

            tab.Content = tabGrid;
            return tab;
        }

    }
}

