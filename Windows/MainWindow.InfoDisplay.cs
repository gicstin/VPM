using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SharpCompress.Archives;
using VPM.Models;
using VPM.Services;
using static VPM.Models.PackageItem;

namespace VPM
{
    /// <summary>
    /// Information display functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        #region Package Information Display

        private void DisplayPackageInfo(PackageItem packageItem)
        {
            // Use stored metadata key for O(1) performance
            _packageManager.PackageMetadata.TryGetValue(packageItem.MetadataKey, out var packageMetadata);

            if (packageMetadata != null)
            {
                var info = new StringBuilder();
                info.AppendLine($"Package: {packageItem.Name}");
                info.AppendLine($"Creator: {packageMetadata.CreatorName}");
                info.AppendLine($"Status: {packageItem.Status}");
                info.AppendLine($"File Size: {packageItem.FileSizeFormatted}");
                info.AppendLine($"Modified: {packageItem.DateFormatted}");
                info.AppendLine($"Version: {packageMetadata.Version}");
                
                if (!string.IsNullOrEmpty(packageMetadata.Description))
                {
                    info.AppendLine($"Description: {packageMetadata.Description}");
                }

                PackageInfoTextBlock.Text = info.ToString();
                
                PopulatePackageCategoryTabs(packageItem, packageMetadata);
            }
            else
            {
                PackageInfoTextBlock.Text = $"Package: {packageItem.Name}\nStatus: {packageItem.Status}\nNo additional metadata available.";
                ClearCategoryTabs();
            }
        }
        
        private void PopulatePackageCategoryTabs(PackageItem packageItem, VarMetadata packageMetadata)
        {
            ClearCategoryTabs();
            
            var categoryFiles = new Dictionary<string, List<string>>();
            
            // Use AllFiles (expanded file list) if available, otherwise fall back to ContentList
            var filesToProcess = (packageMetadata.AllFiles != null && packageMetadata.AllFiles.Count > 0) 
                ? packageMetadata.AllFiles 
                : packageMetadata.ContentList;
            
            if (filesToProcess != null && filesToProcess.Count > 0)
            {
                foreach (var file in filesToProcess)
                {
                    var category = GetFileCategory(file);
                    if (!string.IsNullOrEmpty(category))
                    {
                        if (!categoryFiles.ContainsKey(category))
                        {
                            categoryFiles[category] = new List<string>();
                        }
                        categoryFiles[category].Add(file);
                    }
                }
            }
            
            var orderedCategories = new[] { "Morphs", "Hair", "Clothing", "Looks", "Scenes", "Poses", "Assets", "Textures", "Scripts", "Plugins", "Skins" };
            // Convert to HashSet for O(1) lookups instead of O(n) Array.Contains()
            var orderedCategoriesSet = new HashSet<string>(orderedCategories, StringComparer.OrdinalIgnoreCase);
            
            foreach (var category in orderedCategories)
            {
                if (categoryFiles.ContainsKey(category) && categoryFiles[category].Count > 0)
                {
                    CreateCategoryTab(category, categoryFiles[category], packageItem, packageMetadata);
                }
            }
            
            foreach (var kvp in categoryFiles.Where(c => !orderedCategoriesSet.Contains(c.Key)).OrderBy(c => c.Key))
            {
                CreateCategoryTab(kvp.Key, kvp.Value, packageItem, packageMetadata);
            }
        }
        
        private string GetFileCategory(string filePath)
        {
            var lowerPath = filePath.ToLowerInvariant();
            
            if (lowerPath.Contains("/morphs/") || lowerPath.EndsWith(".vmi") || lowerPath.EndsWith(".vmb") || lowerPath.EndsWith(".dsf"))
                return "Morphs";
            if (lowerPath.Contains("/hair/"))
                return "Hair";
            if (lowerPath.Contains("/clothing/") || lowerPath.Contains("/atom/person/clothing/"))
                return "Clothing";
            if (lowerPath.Contains("/looks/") || lowerPath.Contains("/appearance/"))
                return "Looks";
            if (lowerPath.Contains("/scenes/") || lowerPath.EndsWith(".json"))
                return "Scenes";
            if (lowerPath.Contains("/poses/"))
                return "Poses";
            if (lowerPath.Contains("/assets/"))
                return "Assets";
            if (lowerPath.EndsWith(".jpg") || lowerPath.EndsWith(".png") || lowerPath.EndsWith(".jpeg"))
                return "Textures";
            if (lowerPath.EndsWith(".cs") || lowerPath.EndsWith(".cslist"))
                return "Scripts";
            if (lowerPath.Contains("/custom/scripts/") && lowerPath.EndsWith(".dll"))
                return "Plugins";
            if (lowerPath.Contains("/textures/") || lowerPath.Contains("/skins/"))
                return "Skins";
            if (lowerPath.EndsWith(".vap"))
                return "Looks";
            
            return null;
        }
        
        private void CreateCategoryTab(string category, List<string> files, PackageItem packageItem, VarMetadata packageMetadata)
        {
            // Use actual count from metadata for categories that have been counted
            int displayCount = files.Count;
            if (category == "Clothing" && packageMetadata?.ClothingCount > 0)
                displayCount = packageMetadata.ClothingCount;
            else if (category == "Hair" && packageMetadata?.HairCount > 0)
                displayCount = packageMetadata.HairCount;
            else if (category == "Morphs" && packageMetadata?.MorphCount > 0)
                displayCount = packageMetadata.MorphCount;
            else if (category == "Scenes" && packageMetadata?.SceneCount > 0)
                displayCount = packageMetadata.SceneCount;
            else if (category == "Looks" && packageMetadata?.LooksCount > 0)
                displayCount = packageMetadata.LooksCount;
            else if (category == "Poses" && packageMetadata?.PosesCount > 0)
                displayCount = packageMetadata.PosesCount;
            
            var tabItem = new TabItem
            {
                Header = $"{category} ({displayCount})",
                Style = PackageInfoTabControl.FindResource(typeof(TabItem)) as Style
            };
            
            var dataGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.None,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                RowHeaderWidth = 0,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                CanUserResizeRows = false,
                CanUserResizeColumns = true,
                CanUserSortColumns = false,
                BorderThickness = new Thickness(0),
                VerticalGridLinesBrush = Brushes.Transparent,
                RowHeight = double.NaN
            };
            
            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
            cellStyle.Setters.Add(new Setter(Control.VerticalAlignmentProperty, VerticalAlignment.Stretch));
            cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, FindResource(SystemColors.WindowBrushKey)));
            cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, FindResource(SystemColors.ControlTextBrushKey)));
            
            // Add trigger for selected cells
            var selectedTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource(SystemColors.HighlightBrushKey)));
            selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, FindResource(SystemColors.HighlightTextBrushKey)));
            cellStyle.Triggers.Add(selectedTrigger);
            
            // Add trigger for mouse over cells
            var mouseOverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            mouseOverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("ListBoxHoverBrush")));
            cellStyle.Triggers.Add(mouseOverTrigger);
            
            var templateColumn = new DataGridTemplateColumn
            {
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellStyle = cellStyle
            };
            
            var cellTemplate = new DataTemplate();
            var textBlockFactory = new FrameworkElementFactory(typeof(TextBlock));
            textBlockFactory.SetValue(TextBlock.TextProperty, new Binding("FilePath"));
            textBlockFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            textBlockFactory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            textBlockFactory.SetValue(TextBlock.FontSizeProperty, 13.0);
            textBlockFactory.SetValue(TextBlock.PaddingProperty, new Thickness(4, 2, 4, 2));
            textBlockFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            
            cellTemplate.VisualTree = textBlockFactory;
            templateColumn.CellTemplate = cellTemplate;
            
            dataGrid.Columns.Add(templateColumn);
            
            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, FindResource(SystemColors.WindowBrushKey)));
            rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, FindResource(SystemColors.ControlTextBrushKey)));
            dataGrid.RowStyle = rowStyle;
            
            var fileItems = new List<PackageFileItem>();
            
            // For clothing/hair categories, expand directory paths to show individual items
            var expandedFiles = new List<string>();
            foreach (var file in files)
            {
                // Check if this is a directory path (no file extension)
                var ext = Path.GetExtension(file);
                if (string.IsNullOrEmpty(ext) || (!ext.StartsWith(".") && file.Contains("/")))
                {
                    // This looks like a directory path - show it as a group header
                    // The actual items will be shown based on the category count
                    expandedFiles.Add(file);
                }
                else
                {
                    expandedFiles.Add(file);
                }
            }
            
            foreach (var file in expandedFiles.OrderBy(f => f))
            {
                // Check if this is a directory path
                var ext = Path.GetExtension(file);
                var isDirectory = string.IsNullOrEmpty(ext) || (!ext.StartsWith(".") && file.Contains("/"));
                
                var fileItem = new PackageFileItem
                {
                    FilePath = file,
                    FileName = isDirectory ? $"[Directory] {Path.GetFileName(file)}" : Path.GetFileName(file),
                    FileExtension = ext?.ToUpperInvariant() ?? ""
                };
                fileItems.Add(fileItem);
            }
            
            dataGrid.ItemsSource = fileItems;
            dataGrid.MouseDoubleClick += (s, e) => DataGrid_FileDoubleClick(s, e, packageItem);
            dataGrid.SelectionChanged += (s, e) => DataGrid_FileSelectionChanged(s, e, packageItem);
            
            var contextMenu = new ContextMenu();
            
            var openItem = new MenuItem { Header = "Open File" };
            openItem.Click += (s, e) => DataGrid_OpenFile(dataGrid);
            contextMenu.Items.Add(openItem);
            
            contextMenu.Items.Add(new Separator());
            
            var copyItem = new MenuItem { Header = "Copy Path" };
            copyItem.Click += (s, e) => DataGrid_CopyPath(dataGrid);
            contextMenu.Items.Add(copyItem);
            
            ApplyContextMenuStyling(contextMenu);
            dataGrid.ContextMenu = contextMenu;
            
            tabItem.Content = dataGrid;
            PackageInfoTabControl.Items.Add(tabItem);
        }
        
        private void DataGrid_FileDoubleClick(object sender, MouseButtonEventArgs e, PackageItem packageItem)
        {
            if (sender is DataGrid dataGrid && dataGrid.SelectedItem is PackageFileItem fileItem)
            {
                OpenFileInViewer(fileItem.FilePath, packageItem);
            }
        }
        
        private void DataGrid_FileSelectionChanged(object sender, SelectionChangedEventArgs e, PackageItem packageItem)
        {
            if (sender is DataGrid dataGrid && dataGrid.SelectedItem is PackageFileItem fileItem)
            {
                // Skip directories for preview
                if (fileItem.FileName.StartsWith("[Directory]"))
                    return;
                    
                // Show preview for the selected file
                ShowFilePreview(fileItem.FilePath, packageItem);
            }
            else
            {
                // Hide preview if no valid file is selected
                HidePreviewPanel();
            }
        }
        
        private void DataGrid_OpenFile(DataGrid dataGrid)
        {
            if (dataGrid.SelectedItem is PackageFileItem fileItem)
            {
                var packageItem = PackageDataGrid?.SelectedItem as PackageItem;
                if (packageItem != null)
                {
                    OpenFileInViewer(fileItem.FilePath, packageItem);
                }
            }
        }
        
        private void DataGrid_CopyPath(DataGrid dataGrid)
        {
            if (dataGrid.SelectedItems.Count > 0)
            {
                try
                {
                    var paths = new StringBuilder();
                    foreach (var item in dataGrid.SelectedItems)
                    {
                        if (item is PackageFileItem fileItem)
                        {
                            paths.AppendLine(fileItem.FilePath);
                        }
                    }
                    
                    if (paths.Length > 0)
                    {
                        Clipboard.SetText(paths.ToString().TrimEnd());
                        SetStatus($"Copied {dataGrid.SelectedItems.Count} path(s) to clipboard");
                    }
                }
                catch { }
            }
        }
        
        private void OpenFileInViewer(string filePath, PackageItem packageItem)
        {
            try
            {
                if (string.IsNullOrEmpty(_settingsManager?.Settings?.SelectedFolder))
                {
                    SetStatus("VAM folder not configured");
                    return;
                }

                string packageVarPath = null;
                string vamFolder = _settingsManager.Settings.SelectedFolder;
                
                if (_packageManager?.PackageMetadata.TryGetValue(packageItem.MetadataKey, out var metadata) == true)
                {
                    var possiblePaths = new[]
                    {
                        Path.Combine(vamFolder, "AddonPackages", metadata.Filename),
                        Path.Combine(vamFolder, "AllPackages", metadata.Filename),
                        Path.Combine(vamFolder, "ArchivedPackages", metadata.Filename)
                    };
                    
                    foreach (var path in possiblePaths)
                    {
                        if (File.Exists(path))
                        {
                            packageVarPath = path;
                            break;
                        }
                    }
                }
                
                if (string.IsNullOrEmpty(packageVarPath))
                {
                    SetStatus($"Package file not found for: {packageItem.Name}");
                    return;
                }
                
                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                string tempDir = Path.Combine(Path.GetTempPath(), "VPM", packageItem.Name);
                Directory.CreateDirectory(tempDir);
                
                try
                {
                    using (var archive = SharpCompressHelper.OpenForRead(packageVarPath))
                    {
                        var entry = archive.Entries.FirstOrDefault(e => 
                            e.Key.Equals(filePath, StringComparison.OrdinalIgnoreCase) ||
                            e.Key.Replace("\\", "/").Equals(filePath.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase));
                        
                        if (entry == null)
                        {
                            SetStatus($"File not found in archive: {filePath}");
                            return;
                        }
                        
                        string extractedPath = Path.Combine(tempDir, Path.GetFileName(filePath));
                        using (var entryStream = entry.OpenEntryStream())
                        using (var fileStream = File.Create(extractedPath))
                        {
                            entryStream.CopyTo(fileStream);
                        }
                        
                        if (extension == ".json" || extension == ".vap")
                        {
                            System.Diagnostics.Process.Start(new ProcessStartInfo
                            {
                                FileName = "notepad.exe",
                                Arguments = $"\"{extractedPath}\"",
                                UseShellExecute = false
                            });
                            SetStatus($"Opening: {Path.GetFileName(filePath)}");
                        }
                        else if (extension == ".jpg" || extension == ".jpeg" || extension == ".png")
                        {
                            System.Diagnostics.Process.Start(new ProcessStartInfo
                            {
                                FileName = extractedPath,
                                UseShellExecute = true
                            });
                            SetStatus($"Opening: {Path.GetFileName(filePath)}");
                        }
                        else
                        {
                            System.Diagnostics.Process.Start(new ProcessStartInfo
                            {
                                FileName = extractedPath,
                                UseShellExecute = true
                            });
                            SetStatus($"Opening: {Path.GetFileName(filePath)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetStatus($"Error extracting file: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Error opening file: {ex.Message}");
            }
        }
        
        
        private void ClearCategoryTabs()
        {
            // Keep Package Info tab (index 0), remove all dynamically added category tabs
            while (PackageInfoTabControl.Items.Count > 1)
            {
                PackageInfoTabControl.Items.RemoveAt(PackageInfoTabControl.Items.Count - 1);
            }
            
            PackageInfoTabControl.SelectedIndex = 0;
        }

        private void DisplayMultiplePackageInfo(List<PackageItem> selectedPackages)
        {
            var totalPackages = selectedPackages.Count;
            var statusCounts = new Dictionary<string, int>();
            var creatorCounts = new Dictionary<string, int>();
            var categoryCounts = new Dictionary<string, int>();
            var licenseCounts = new Dictionary<string, int>();
            var totalDependencies = 0;
            var totalFileCount = 0;
            var totalSize = 0L;
            var oldestDate = DateTime.MaxValue;
            var newestDate = DateTime.MinValue;

            foreach (var packageItem in selectedPackages)
            {
                // Use stored metadata key for O(1) performance
                _packageManager.PackageMetadata.TryGetValue(packageItem.MetadataKey, out var packageMetadata);

                if (packageMetadata != null)
                {
                    // Count statuses
                    statusCounts[packageMetadata.Status] = statusCounts.ContainsKey(packageMetadata.Status) ? statusCounts[packageMetadata.Status] + 1 : 1;

                    // Count creators
                    creatorCounts[packageMetadata.CreatorName] = creatorCounts.ContainsKey(packageMetadata.CreatorName) ? creatorCounts[packageMetadata.CreatorName] + 1 : 1;

                    // Count categories
                    foreach (var category in packageMetadata.Categories)
                    {
                        categoryCounts[category] = categoryCounts.ContainsKey(category) ? categoryCounts[category] + 1 : 1;
                    }

                    // Count licenses
                    var license = string.IsNullOrEmpty(packageMetadata.LicenseType) ? "Unknown" : packageMetadata.LicenseType;
                    licenseCounts[license] = licenseCounts.ContainsKey(license) ? licenseCounts[license] + 1 : 1;

                    // Sum totals
                    totalDependencies += packageMetadata.Dependencies?.Count ?? 0;
                    totalFileCount += packageMetadata.FileCount;
                }

                // Sum file sizes from package items
                totalSize += packageItem.FileSize;

                // Track date range
                if (packageItem.ModifiedDate.HasValue)
                {
                    if (packageItem.ModifiedDate.Value < oldestDate)
                        oldestDate = packageItem.ModifiedDate.Value;
                    if (packageItem.ModifiedDate.Value > newestDate)
                        newestDate = packageItem.ModifiedDate.Value;
                }
            }

            var info = $"📦 SELECTION SUMMARY ({totalPackages} packages)\n\n";

            // Status breakdown
            info += "📊 Status:\n";
            foreach (var status in statusCounts.OrderByDescending(s => s.Value))
            {
                info += $"  • {status.Key}: {status.Value}\n";
            }

            // Creator breakdown (top 5)
            info += "\n👤 Creators:\n";
            foreach (var creator in creatorCounts.OrderByDescending(c => c.Value).Take(5))
            {
                info += $"  • {creator.Key}: {creator.Value}\n";
            }
            if (creatorCounts.Count > 5)
            {
                info += $"  • ... and {creatorCounts.Count - 5} more\n";
            }

            // Category breakdown
            info += "\n🏷️ Categories:\n";
            foreach (var category in categoryCounts.OrderByDescending(c => c.Value))
            {
                info += $"  • {category.Key}: {category.Value}\n";
            }

            // License breakdown
            info += "\n⚖️ Licenses:\n";
            foreach (var license in licenseCounts.OrderByDescending(l => l.Value))
            {
                info += $"  • {license.Key}: {license.Value}\n";
            }

            // Totals
            info += $"\n📊 Totals:\n";
            info += $"  • Total Size: {FormatHelper.FormatFileSize(totalSize)}\n";
            info += $"  • Total Files: {totalFileCount:N0}\n";
            info += $"  • Total Dependencies: {totalDependencies}\n";
            info += $"  • Unique Dependencies: {Dependencies.Count}\n";

            // Date range
            if (oldestDate != DateTime.MaxValue && newestDate != DateTime.MinValue)
            {
                info += $"  • Date Range: {oldestDate:MMM dd, yyyy} - {newestDate:MMM dd, yyyy}\n";
            }

            PackageInfoTextBlock.Text = info;
            ClearCategoryTabs();
        }

        // FormatFileSize is now shared from PackageItem

        #endregion

        #region Dependencies Display

        private void UpdateBothTabCounts(PackageItem packageItem)
        {
            _packageManager.PackageMetadata.TryGetValue(packageItem.MetadataKey, out var packageMetadata);
            
            _dependenciesCount = packageMetadata?.Dependencies?.Count ?? 0;
            
            // Use dependency graph for O(1) lookup instead of iterating all packages
            var packageFullName = $"{packageMetadata?.CreatorName}.{packageMetadata?.PackageName}.{packageMetadata?.Version}";
            _dependentsCount = _packageManager?.GetPackageDependentsCount(packageFullName) ?? 0;
            
            DependenciesCountText.Text = $"({_dependenciesCount})";
            DependentsCountText.Text = $"({_dependentsCount})";
        }

        private void UpdateBothTabCountsForMultiple(List<PackageItem> selectedPackages)
        {
            var allDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allDependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var package in selectedPackages)
            {
                _packageManager.PackageMetadata.TryGetValue(package.MetadataKey, out var packageMetadata);
                if (packageMetadata != null)
                {
                    // Collect dependencies
                    if (packageMetadata.Dependencies != null)
                    {
                        foreach (var dependency in packageMetadata.Dependencies)
                        {
                            var dependencyName = dependency;
                            if (dependency.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                            {
                                dependencyName = Path.GetFileNameWithoutExtension(dependency);
                            }
                            allDependencies.Add(dependencyName);
                        }
                    }
                    
                    // Use dependency graph for O(1) lookup of dependents
                    var packageFullName = $"{packageMetadata.CreatorName}.{packageMetadata.PackageName}.{packageMetadata.Version}";
                    var dependents = _packageManager?.GetPackageDependents(packageFullName);
                    if (dependents != null)
                    {
                        foreach (var dep in dependents)
                            allDependents.Add(dep);
                    }
                }
            }
            
            _dependenciesCount = allDependencies.Count;
            _dependentsCount = allDependents.Count;
            
            DependenciesCountText.Text = $"({_dependenciesCount})";
            DependentsCountText.Text = $"({_dependentsCount})";
        }

        private void DisplayDependencies(PackageItem packageItem)
        {
            // Use cached package status index - only refresh after actual file changes (downloads)
            // RefreshPackageStatusIndex(force: true) is expensive and should not be called on every selection
            _packageFileManager?.RefreshPackageStatusIndex(force: false);

            // Clear any existing filter first
            var view = CollectionViewSource.GetDefaultView(Dependencies);
            if (view != null)
            {
                view.Filter = null;
            }

            Dependencies.Clear();
            _originalDependencies.Clear(); // Clear original dependencies when loading new ones

            // Use stored metadata key for O(1) performance
            _packageManager.PackageMetadata.TryGetValue(packageItem.MetadataKey, out var packageMetadata);
            
            if (packageMetadata?.Dependencies != null && packageMetadata.Dependencies.Any())
            {
                int depCount = 0;
                foreach (var dependency in packageMetadata.Dependencies)
                {
                    // dependency is the RAW string from metadata - could be "package.name.5.var" or "package.name:/path/to/file.var"

                    // Only remove .var extension if it exists, otherwise use as-is
                    var dependencyName = dependency;
                    if (dependency.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                    {
                        dependencyName = Path.GetFileNameWithoutExtension(dependency);
                    }

                    // Extract version from dependency name
                    string baseName = dependencyName;
                    string version = "";

                    // First, check for .latest suffix
                    if (dependencyName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                    {
                        baseName = dependencyName.Substring(0, dependencyName.Length - 7); // Remove .latest
                        version = "latest";
                    }
                    else
                    {
                        // Check for numeric version at the end
                        var lastDotIndex = dependencyName.LastIndexOf('.');
                        if (lastDotIndex > 0)
                        {
                            var potentialVersion = dependencyName.Substring(lastDotIndex + 1);
                            if (int.TryParse(potentialVersion, out _))
                            {
                                baseName = dependencyName.Substring(0, lastDotIndex);
                                version = potentialVersion;
                            }
                        }
                    }
                    
                    var status = _packageFileManager?.GetPackageStatus(baseName) ?? "Unknown";
                    
                    // Check if dependency exists in external destinations
                    var externalDestinationColor = CheckDependencyInExternalDestinations(baseName);
                    if (!string.IsNullOrEmpty(externalDestinationColor))
                    {
                        // Found in external destination - use the destination's configured status color
                        status = externalDestinationColor;
                    }
                    
                    var dependencyItem = new DependencyItem
                    {
                        Name = baseName,
                        Version = version,
                        Status = status
                    };
                    
                    Dependencies.Add(dependencyItem);
                    _originalDependencies.Add(dependencyItem); // Store for filtering
                    depCount++;
                }
            }
            else
            {
                // Add a placeholder item to show that there are no dependencies
                var noDepsItem = new DependencyItem
                {
                    Name = "No dependencies",
                    Status = "N/A"
                };
                Dependencies.Add(noDepsItem);
                _originalDependencies.Add(noDepsItem); // Store for filtering
            }
            
            // Reapply dependencies sorting after loading
            var depsState = _sortingManager?.GetSortingState("Dependencies");
            if (depsState?.CurrentSortOption is DependencySortOption depsSort)
            {
                ReapplyDependenciesSortingInternal(depsSort, depsState.IsAscending);
            }
            
            // Update toolbar buttons after dependencies change
            UpdateToolbarButtons();
        }
        
        /// <summary>
        /// Checks if a dependency package exists in any external destination
        /// Returns the destination's status color if found, otherwise empty string
        /// </summary>
        private string CheckDependencyInExternalDestinations(string packageBaseName)
        {
            if (string.IsNullOrEmpty(packageBaseName) || _packageManager?.PackageMetadata == null)
                return "";
            
            // Search through all packages in metadata to find external ones matching the dependency
            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var metadata = kvp.Value;
                
                // Check if this is an external package
                if (!metadata.IsExternal || string.IsNullOrEmpty(metadata.ExternalDestinationName))
                    continue;
                
                // Build the package name from metadata (Creator.PackageName format)
                var packageName = $"{metadata.CreatorName}.{metadata.PackageName}";
                
                // Check if this matches the dependency we're looking for
                if (packageName.Equals(packageBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    // Return the external destination's configured status color
                    return metadata.ExternalDestinationColorHex ?? "#808080";
                }
            }
            
            return "";
        }

        private void DisplayConsolidatedDependencies(List<PackageItem> selectedPackages)
        {
            // Use cached package status index - only refresh after actual file changes (downloads)
            // RefreshPackageStatusIndex(force: true) is expensive and should not be called on every selection
            _packageFileManager?.RefreshPackageStatusIndex(force: false);

            // Clear any existing filter first
            var view = CollectionViewSource.GetDefaultView(Dependencies);
            if (view != null)
            {
                view.Filter = null;
            }

            Dependencies.Clear();
            _originalDependencies.Clear(); // Clear original dependencies when loading new ones
            var allDependencies = new Dictionary<string, DependencyItem>();
            foreach (var package in selectedPackages)
            {
                // Use stored metadata key for O(1) performance
                _packageManager.PackageMetadata.TryGetValue(package.MetadataKey, out var packageMetadata);

                if (packageMetadata?.Dependencies != null)
                {
                    foreach (var dependency in packageMetadata.Dependencies)
                    {
                        // dependency is the RAW string from metadata - could be "package.name.5.var" or "package.name:/path/to/file.var"
                        var dependencyName = dependency;
                        if (dependency.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                        {
                            dependencyName = Path.GetFileNameWithoutExtension(dependency);
                        }
                        
                        // Extract version from dependency name
                        string baseName = dependencyName;
                        string version = "";
                        
                        // First, check for .latest suffix
                        if (dependencyName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                        {
                            baseName = dependencyName.Substring(0, dependencyName.Length - 7); // Remove .latest
                            version = "latest";
                        }
                        else
                        {
                            // Check for numeric version at the end
                            var lastDotIndex = dependencyName.LastIndexOf('.');
                            if (lastDotIndex > 0)
                            {
                                var potentialVersion = dependencyName.Substring(lastDotIndex + 1);
                                if (int.TryParse(potentialVersion, out _))
                                {
                                    baseName = dependencyName.Substring(0, lastDotIndex);
                                    version = potentialVersion;
                                }
                            }
                        }
                        
                        if (!allDependencies.ContainsKey(dependencyName))
                        {
                            var status = _packageFileManager?.GetPackageStatus(baseName) ?? "Unknown";
                            
                            // Check if dependency exists in external destinations
                            var externalDestinationColor = CheckDependencyInExternalDestinations(baseName);
                            if (!string.IsNullOrEmpty(externalDestinationColor))
                            {
                                // Found in external destination - use the destination's configured status color
                                status = externalDestinationColor;
                            }
                            
                            allDependencies[dependencyName] = new DependencyItem
                            {
                                Name = baseName,
                                Version = version,
                                Status = status
                            };
                        }
                    }
                }
            }
            if (allDependencies.Any())
            {
                // Sort dependencies by name
                foreach (var dependency in allDependencies.Values.OrderBy(d => d.Name))
                {
                    Dependencies.Add(dependency);
                    _originalDependencies.Add(dependency); // Store for filtering
                }
            }
            else
            {
                // Add a placeholder item to show that there are no dependencies
                var noDepsItem = new DependencyItem
                {
                    Name = "No dependencies found",
                    Status = "N/A"
                };
                Dependencies.Add(noDepsItem);
                _originalDependencies.Add(noDepsItem); // Store for filtering
            }
            
            // Reapply dependencies sorting after loading
            var depsState = _sortingManager?.GetSortingState("Dependencies");
            if (depsState?.CurrentSortOption is DependencySortOption depsSort)
            {
                ReapplyDependenciesSortingInternal(depsSort, depsState.IsAscending);
            }
            
            // Update toolbar buttons after dependencies change
            UpdateToolbarButtons();
        }

        /// <summary>
        /// Clear the dependencies display when no packages are selected
        /// </summary>
        private void ClearDependenciesDisplay()
        {
            // Clear any existing filter first
            var view = CollectionViewSource.GetDefaultView(Dependencies);
            if (view != null)
            {
                view.Filter = null;
            }

            // Clear all dependencies - no placeholder needed since nothing is selected
            Dependencies.Clear();
            _originalDependencies.Clear();
            
            // Update toolbar buttons after dependencies change
            UpdateToolbarButtons();
        }

        /// <summary>
        /// Display all available dependencies from currently filtered packages when no packages are selected
        /// </summary>
        private void DisplayAllAvailableDependencies()
        {
            // Use cached package status index - only refresh after actual file changes (downloads)
            // RefreshPackageStatusIndex(force: true) is expensive and should not be called on every selection
            _packageFileManager?.RefreshPackageStatusIndex(force: false);

            // Clear any existing filter first
            var depsView = CollectionViewSource.GetDefaultView(Dependencies);
            if (depsView != null)
            {
                depsView.Filter = null;
            }
            
            Dependencies.Clear();
            _originalDependencies.Clear(); // Clear original dependencies when loading new ones
            var allDependencies = new Dictionary<string, string>(); // dependency -> status
            
            
            // Build lookup dictionary once for all dependency resolution
            var packageLookup = BuildPackageLookupDictionary();
            
            // Get the filtered packages from the CollectionView (not the raw Packages collection)
            var packagesView = CollectionViewSource.GetDefaultView(Packages);
            var filteredPackages = packagesView?.Cast<PackageItem>().ToList() ?? Packages.ToList();
            
            
            // Collect all dependencies from currently visible/filtered packages only
            foreach (var packageItem in filteredPackages)
            {
                // Find the metadata for this visible package
                var packageMetadata = _packageManager.PackageMetadata.Values
                    .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p.Filename) == packageItem.Name);
                
                if (packageMetadata?.Dependencies != null)
                {
                    foreach (var dependency in packageMetadata.Dependencies)
                    {
                        if (!allDependencies.ContainsKey(dependency))
                        {
                            // Check if dependency is available in our packages
                            // Use GetPackageStatus which handles .latest, .min[NUMBER], and exact versions
                            var depStatus = _packageFileManager?.GetPackageStatus(dependency) ?? "Missing";
                            
                            // If still missing, try lookup dictionary as fallback
                            if (depStatus == "Missing")
                            {
                                // Parse the dependency to get base name for lookup
                                var depInfo = DependencyVersionInfo.Parse(dependency);
                                
                                // Try to find by base name
                                if (packageLookup.TryGetValue(depInfo.BaseName, out var depPackage) ||
                                    packageLookup.TryGetValue(depInfo.BaseName.ToLowerInvariant(), out depPackage))
                                {
                                    depStatus = _packageFileManager?.GetPackageStatus(Path.GetFileNameWithoutExtension(depPackage.Filename)) ?? "Unknown";
                                }
                            }
                            
                            allDependencies[dependency] = depStatus;
                        }
                    }
                }
            }
            
            // Add all dependencies to the list, sorted by name
            foreach (var kvp in allDependencies.OrderBy(d => d.Key))
            {
                var dependencyItem = new DependencyItem 
                { 
                    Name = kvp.Key, 
                    Status = kvp.Value
                };
                Dependencies.Add(dependencyItem);
                _originalDependencies.Add(dependencyItem); // Store for filtering
            }
            
            // Update toolbar buttons after dependencies change
            UpdateToolbarButtons();
        }

        /// <summary>
        /// Build a lookup dictionary for fast package resolution
        /// </summary>
        private Dictionary<string, VarMetadata> BuildPackageLookupDictionary()
        {
            var lookup = new Dictionary<string, VarMetadata>(StringComparer.OrdinalIgnoreCase);
            
            if (_packageManager?.PackageMetadata == null) return lookup;
            
            foreach (var package in _packageManager.PackageMetadata.Values)
            {
                // Add various possible keys for the same package
                var filename = Path.GetFileNameWithoutExtension(package.Filename);
                var packageName = package.PackageName;
                var creatorPackage = $"{package.CreatorName}.{package.PackageName}";
                
                // Add all possible keys (using TryAdd to avoid duplicates)
                if (!string.IsNullOrEmpty(filename))
                {
                    lookup.TryAdd(filename, package);
                    lookup.TryAdd(filename.ToLowerInvariant(), package);
                }
                
                if (!string.IsNullOrEmpty(packageName))
                {
                    lookup.TryAdd(packageName, package);
                    lookup.TryAdd(packageName.ToLowerInvariant(), package);
                }
                
                if (!string.IsNullOrEmpty(package.CreatorName) && !string.IsNullOrEmpty(packageName))
                {
                    lookup.TryAdd(creatorPackage, package);
                    lookup.TryAdd(creatorPackage.ToLowerInvariant(), package);
                }
            }
            
            return lookup;
        }

        private void DisplayDependents(PackageItem packageItem)
        {
            // Use cached package status index - only refresh after actual file changes (downloads)
            _packageFileManager?.RefreshPackageStatusIndex(force: false);

            var view = CollectionViewSource.GetDefaultView(Dependencies);
            if (view != null)
            {
                view.Filter = null;
            }

            Dependencies.Clear();
            _originalDependencies.Clear();

            _packageManager.PackageMetadata.TryGetValue(packageItem.MetadataKey, out var packageMetadata);
            if (packageMetadata == null)
            {
                var noDepsItem = new DependencyItem
                {
                    Name = "No dependents",
                    Status = "N/A"
                };
                Dependencies.Add(noDepsItem);
                _originalDependencies.Add(noDepsItem);
                return;
            }

            // Use dependency graph for O(1) lookup instead of iterating all packages
            var packageFullName = $"{packageMetadata.CreatorName}.{packageMetadata.PackageName}.{packageMetadata.Version}";
            var dependentsList = _packageManager.GetPackageDependents(packageFullName);

            if (dependentsList.Count > 0)
            {
                foreach (var dependentName in dependentsList.OrderBy(d => d))
                {
                    // Parse the dependent name to extract base name and version
                    string baseName = dependentName;
                    string version = "";
                    
                    var lastDotIndex = dependentName.LastIndexOf('.');
                    if (lastDotIndex > 0)
                    {
                        var potentialVersion = dependentName.Substring(lastDotIndex + 1);
                        if (int.TryParse(potentialVersion, out _))
                        {
                            version = potentialVersion;
                            baseName = dependentName.Substring(0, lastDotIndex);
                        }
                    }
                    
                    // Check if dependent is in PackageMetadata (includes external packages)
                    string status = "Unknown";
                    if (_packageManager.PackageMetadata.TryGetValue(dependentName, out var dependentMetadata))
                    {
                        // For external packages, use their destination color; otherwise use file manager status
                        if (dependentMetadata.IsExternal && !string.IsNullOrEmpty(dependentMetadata.ExternalDestinationColorHex))
                        {
                            status = dependentMetadata.ExternalDestinationColorHex;
                        }
                        else
                        {
                            status = dependentMetadata.Status;
                        }
                    }
                    else
                    {
                        // Fallback to file manager status for non-metadata packages
                        status = _packageFileManager?.GetPackageStatus(baseName) ?? "Unknown";
                    }
                    
                    var dependentItem = new DependencyItem
                    {
                        Name = baseName,
                        Version = version,
                        Status = status
                    };
                    
                    Dependencies.Add(dependentItem);
                    _originalDependencies.Add(dependentItem);
                }
            }
            else
            {
                var noDepsItem = new DependencyItem
                {
                    Name = "No dependents",
                    Status = "N/A"
                };
                Dependencies.Add(noDepsItem);
                _originalDependencies.Add(noDepsItem);
            }

            var depsState = _sortingManager?.GetSortingState("Dependencies");
            if (depsState?.CurrentSortOption is DependencySortOption depsSort)
            {
                ReapplyDependenciesSortingInternal(depsSort, depsState.IsAscending);
            }

            UpdateToolbarButtons();
        }

        private void DisplayConsolidatedDependents(List<PackageItem> selectedPackages)
        {
            // Use cached package status index - only refresh after actual file changes (downloads)
            _packageFileManager?.RefreshPackageStatusIndex(force: false);

            var view = CollectionViewSource.GetDefaultView(Dependencies);
            if (view != null)
            {
                view.Filter = null;
            }

            Dependencies.Clear();
            _originalDependencies.Clear();

            // Use dependency graph for O(1) lookups instead of iterating all packages
            var allDependents = new Dictionary<string, DependencyItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var package in selectedPackages)
            {
                _packageManager.PackageMetadata.TryGetValue(package.MetadataKey, out var packageMetadata);
                if (packageMetadata == null)
                    continue;
                
                var packageFullName = $"{packageMetadata.CreatorName}.{packageMetadata.PackageName}.{packageMetadata.Version}";
                var dependentsList = _packageManager.GetPackageDependents(packageFullName);
                
                foreach (var dependentName in dependentsList)
                {
                    if (allDependents.ContainsKey(dependentName))
                        continue;
                    
                    // Parse the dependent name to extract base name and version
                    string baseName = dependentName;
                    string version = "";
                    
                    var lastDotIndex = dependentName.LastIndexOf('.');
                    if (lastDotIndex > 0)
                    {
                        var potentialVersion = dependentName.Substring(lastDotIndex + 1);
                        if (int.TryParse(potentialVersion, out _))
                        {
                            version = potentialVersion;
                            baseName = dependentName.Substring(0, lastDotIndex);
                        }
                    }
                    
                    // Check if dependent is in PackageMetadata (includes external packages)
                    string status = "Unknown";
                    if (_packageManager.PackageMetadata.TryGetValue(dependentName, out var dependentMetadata))
                    {
                        // For external packages, use their destination color; otherwise use file manager status
                        if (dependentMetadata.IsExternal && !string.IsNullOrEmpty(dependentMetadata.ExternalDestinationColorHex))
                        {
                            status = dependentMetadata.ExternalDestinationColorHex;
                        }
                        else
                        {
                            status = dependentMetadata.Status;
                        }
                    }
                    else
                    {
                        // Fallback to file manager status for non-metadata packages
                        status = _packageFileManager?.GetPackageStatus(baseName) ?? "Unknown";
                    }
                    
                    allDependents[dependentName] = new DependencyItem
                    {
                        Name = baseName,
                        Version = version,
                        Status = status
                    };
                }
            }

            if (allDependents.Count > 0)
            {
                foreach (var dependent in allDependents.Values.OrderBy(d => d.Name))
                {
                    Dependencies.Add(dependent);
                    _originalDependencies.Add(dependent);
                }
            }
            else
            {
                var noDepsItem = new DependencyItem
                {
                    Name = "No dependents found",
                    Status = "N/A"
                };
                Dependencies.Add(noDepsItem);
                _originalDependencies.Add(noDepsItem);
            }

            var depsState = _sortingManager?.GetSortingState("Dependencies");
            if (depsState?.CurrentSortOption is DependencySortOption depsSort)
            {
                ReapplyDependenciesSortingInternal(depsSort, depsState.IsAscending);
            }

            UpdateToolbarButtons();
        }

        private void RefreshDependenciesDisplay()
        {
            var selectedPackages = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList();
            
            // Clear any existing selection in the dependencies grid when refreshing
            // This ensures we show the parent package's images until a dependency/dependent is selected
            if (DependenciesDataGrid?.SelectedItems != null && DependenciesDataGrid.SelectedItems.Count > 0)
            {
                DependenciesDataGrid.SelectedItems.Clear();
            }
            
            if (selectedPackages == null || selectedPackages.Count == 0)
            {
                ClearDependenciesDisplay();
            }
            else if (selectedPackages.Count == 1)
            {
                if (_showingDependents)
                {
                    DisplayDependents(selectedPackages[0]);
                }
                else
                {
                    DisplayDependencies(selectedPackages[0]);
                }
            }
            else
            {
                if (_showingDependents)
                {
                    DisplayConsolidatedDependents(selectedPackages);
                }
                else
                {
                    DisplayConsolidatedDependencies(selectedPackages);
                }
            }
        }

        #endregion
    }
}

