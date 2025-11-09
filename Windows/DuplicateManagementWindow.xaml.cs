using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using VPM.Models;

namespace VPM
{
    public partial class DuplicateManagementWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private ObservableCollection<DuplicatePackageItem> _duplicatePackages;
        private string _addonPackagesPath;
        private string _allPackagesPath;

        // Drag selection fields
        private bool _duplicateDragging = false;
        private Point _duplicateDragStartPoint;
        private System.Windows.Controls.DataGridRow _duplicateDragStartRow;
        private System.Windows.Controls.CheckBox _duplicateDragStartCheckbox;
        private bool? _duplicateDragCheckState;

        public DuplicateManagementWindow(List<PackageItem> duplicatePackages, string addonPackagesPath, string allPackagesPath)
        {
            InitializeComponent();
            
            _addonPackagesPath = addonPackagesPath;
            _allPackagesPath = allPackagesPath;
            
            // Apply dark theme to window chrome
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
                int useImmersiveDarkMode = 1;
                // Try Windows 11/10 20H1+ attribute first, then fall back to older Windows 10 attribute
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
                }
            }
            catch
            {
                // Dark mode not available on this system
            }
            
            LoadDuplicatePackages(duplicatePackages);
        }

        private void LoadDuplicatePackages(List<PackageItem> packages)
        {
            _duplicatePackages = new ObservableCollection<DuplicatePackageItem>();

            
            // Debug: Log all received packages
            foreach (var pkg in packages)
            {
            }

            // Group incoming metadata by base package name first
            // Note: We don't filter by Status or DuplicateLocationCount here because the metadata
            // might be stale after a refresh. We'll rely on the filesystem scan to determine actual duplicates.
            var baseNameGroups = new Dictionary<string, List<PackageItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pkg in packages)
            {
                string baseName = ExtractBasePackageName(pkg.DisplayName);
                if (!baseNameGroups.TryGetValue(baseName, out var list))
                {
                    list = new List<PackageItem>();
                    baseNameGroups[baseName] = list;
                }
                list.Add(pkg);
            }

            foreach (var baseEntry in baseNameGroups)
            {
                var baseName = baseEntry.Key;
                var metadataItems = baseEntry.Value;


                // Gather actual file instances from disk for this base package
                var fileInstances = new List<FileInstance>();
                var addonInstances = GetPackageInstancesInAddonPackages(baseName);
                var allInstances = GetPackageInstancesInAllPackages(baseName);
                
                
                // Log all found files
                foreach (var path in addonInstances)
                {
                }
                foreach (var path in allInstances)
                {
                }
                
                AppendFileInstances(fileInstances, addonInstances, FileLocation.AddonPackages);
                AppendFileInstances(fileInstances, allInstances, FileLocation.AllPackages);

                if (fileInstances.Count == 0)
                {
                    continue;
                }

                // Group by filename (which contains version information)
                // Duplicates are identified by same name and version, regardless of file size or date
                var fileGroups = fileInstances.GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase);

                foreach (var fileGroup in fileGroups)
                {
                    var instances = fileGroup.ToList();
                    bool hasAddon = instances.Any(f => f.Location == FileLocation.AddonPackages);
                    bool hasAll = instances.Any(f => f.Location == FileLocation.AllPackages);
                    
                    // Count how many instances in each location
                    int addonCount = instances.Count(f => f.Location == FileLocation.AddonPackages);
                    int allCount = instances.Count(f => f.Location == FileLocation.AllPackages);
                    
                    // Get file sizes for logging
                    var fileSizes = instances.Select(f => f.FileSize).Distinct().ToList();
                    var sizeInfo = fileSizes.Count > 1 ? $"sizes: {string.Join(", ", fileSizes)}" : $"size: {fileSizes.FirstOrDefault()}";
                    

                    // We care about duplicates that exist in more than one location OR multiple times in the same location
                    bool isDuplicate = (hasAddon && hasAll) || addonCount > 1 || allCount > 1;
                    
                    if (!isDuplicate)
                    {
                        continue;
                    }
                    

                    var displayName = Path.GetFileNameWithoutExtension(fileGroup.Key);

                    // Match metadata items for display/helpers
                    var addonMetadata = metadataItems.FirstOrDefault(p =>
                        IsInAddonPackages(p) && string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
                    var allMetadata = metadataItems.FirstOrDefault(p =>
                        IsInAllPackages(p) && string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

                    // Use the largest file size for display (likely the most recent/complete version)
                    var maxFileSize = instances.Max(f => f.FileSize);

                    var duplicateItem = new DuplicatePackageItem
                    {
                        PackageName = displayName,
                        // Enable both checkboxes - user can choose to move to the other location
                        ExistsInAddonPackages = true,
                        ExistsInAllPackages = true,
                        KeepInAddonPackages = hasAddon, // default to keeping addon copy if it exists there
                        KeepInAllPackages = !hasAddon && hasAll,
                        LoadedPackageItem = addonMetadata ?? allMetadata ?? metadataItems.FirstOrDefault(),
                        AvailablePackageItem = allMetadata ?? addonMetadata ?? metadataItems.FirstOrDefault(),
                        FileSizeBytes = maxFileSize
                    };

                    if (hasAddon && hasAll)
                    {
                        duplicateItem.KeepInAddonPackages = true;
                        duplicateItem.KeepInAllPackages = false;
                    }

                    duplicateItem.PropertyChanged += DuplicateItem_PropertyChanged;
                    _duplicatePackages.Add(duplicateItem);
                }
            }

            DuplicatesDataGrid.ItemsSource = _duplicatePackages;
            
            // Attach drag selection event handlers
            DuplicatesDataGrid.PreviewMouseDown += DuplicatesDataGrid_PreviewMouseDown;
            DuplicatesDataGrid.PreviewMouseUp += DuplicatesDataGrid_PreviewMouseUp;
            DuplicatesDataGrid.PreviewMouseMove += DuplicatesDataGrid_PreviewMouseMove;
            
            UpdateFixButtonCounter();
            UpdateStatusText();
        }

        private enum FileLocation
        {
            AddonPackages,
            AllPackages
        }

        private readonly struct FileInstance
        {
            public FileInstance(string fullPath, FileLocation location)
            {
                FullPath = fullPath;
                Location = location;
                FileName = Path.GetFileName(fullPath);
                FileSize = 0;
                try
                {
                    var info = new FileInfo(fullPath);
                    if (info.Exists)
                    {
                        FileSize = info.Length;
                    }
                }
                catch
                {
                    FileSize = 0;
                }
            }

            public string FullPath { get; }
            public string FileName { get; }
            public long FileSize { get; }
            public FileLocation Location { get; }
        }

        private void AppendFileInstances(List<FileInstance> list, List<string> paths, FileLocation location)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                try
                {
                    if (File.Exists(path))
                    {
                        list.Add(new FileInstance(path, location));
                    }
                }
                catch { }
            }
        }
        
        private bool IsInAddonPackages(PackageItem package)
        {
            // First check metadata key for role indicators
            if (!string.IsNullOrEmpty(package.MetadataKey))
            {
                if (package.MetadataKey.EndsWith("#loaded", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            
            // Check by status
            if (package.Status == "Loaded")
            {
                return true;
            }
            
            return false;
        }
        
        private bool IsInAllPackages(PackageItem package)
        {
            // First check metadata key for role indicators
            if (!string.IsNullOrEmpty(package.MetadataKey))
            {
                if (package.MetadataKey.EndsWith("#available", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            
            // Check by status
            if (package.Status == "Available")
            {
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Check if a package with the given base name exists in AddonPackages folder
        /// </summary>
        private bool PackageExistsInAddonPackages(string baseName)
        {
            var instances = GetPackageInstancesInAddonPackages(baseName);
            return instances.Count > 0;
        }
        
        /// <summary>
        /// Check if a package with the given base name exists in AllPackages folder
        /// </summary>
        private bool PackageExistsInAllPackages(string baseName)
        {
            var instances = GetPackageInstancesInAllPackages(baseName);
            return instances.Count > 0;
        }
        
        /// <summary>
        /// Get all instances of a package in AddonPackages folder (including subfolders)
        /// Excludes ArchivedPackages folder which holds original backups
        /// </summary>
        private List<string> GetPackageInstancesInAddonPackages(string baseName)
        {
            var instances = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(_addonPackagesPath) || !Directory.Exists(_addonPackagesPath))
                    return instances;
                    
                // Look for any version of this package in AddonPackages
                var pattern = $"{baseName}.*.var";
                var files = Directory.GetFiles(_addonPackagesPath, pattern, SearchOption.AllDirectories);
                
                // Exclude ArchivedPackages folder
                foreach (var file in files)
                {
                    if (!file.Contains("ArchivedPackages", StringComparison.OrdinalIgnoreCase))
                    {
                        instances.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetPackageInstancesInAddonPackages] Error: {ex.Message}");
            }
            return instances;
        }
        
        /// <summary>
        /// Get all instances of a package in AllPackages folder (including subfolders)
        /// Excludes ArchivedPackages folder which holds original backups
        /// </summary>
        private List<string> GetPackageInstancesInAllPackages(string baseName)
        {
            var instances = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(_allPackagesPath) || !Directory.Exists(_allPackagesPath))
                    return instances;
                    
                // Look for any version of this package in AllPackages
                var pattern = $"{baseName}.*.var";
                var files = Directory.GetFiles(_allPackagesPath, pattern, SearchOption.AllDirectories);
                
                // Exclude ArchivedPackages folder
                foreach (var file in files)
                {
                    if (!file.Contains("ArchivedPackages", StringComparison.OrdinalIgnoreCase))
                    {
                        instances.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetPackageInstancesInAllPackages] Error: {ex.Message}");
            }
            return instances;
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
        /// Get a user-friendly relative path for display purposes
        /// </summary>
        private string GetRelativeDisplayPath(string fullPath)
        {
            try
            {
                // Find the main folder (AddonPackages or AllPackages) in the path
                var pathParts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                
                int mainFolderIndex = -1;
                for (int i = 0; i < pathParts.Length; i++)
                {
                    if (pathParts[i].Equals("AddonPackages", StringComparison.OrdinalIgnoreCase) ||
                        pathParts[i].Equals("AllPackages", StringComparison.OrdinalIgnoreCase))
                    {
                        mainFolderIndex = i;
                        break;
                    }
                }
                
                if (mainFolderIndex >= 0 && mainFolderIndex < pathParts.Length - 1)
                {
                    // Return path relative to the main folder with forward slashes for consistency
                    var relativePath = string.Join("/", pathParts.Skip(mainFolderIndex));
                    return relativePath;
                }
                
                // Fallback: show last 3 parts of the path if we can't find the main folder
                if (pathParts.Length >= 3)
                {
                    return string.Join("/", pathParts.Skip(pathParts.Length - 3));
                }
                
                return Path.GetFileName(fullPath);
            }
            catch
            {
                return Path.GetFileName(fullPath);
            }
        }

        private string GetCleanFileName(PackageItem package)
        {
            var fileName = package.Name;
            if (fileName.EndsWith("#loaded", StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - 7);
            if (fileName.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - 9);
            
            if (!fileName.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                fileName += ".var";
                
            return fileName;
        }

        private void DuplicateItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DuplicatePackageItem.KeepInAddonPackages) || 
                e.PropertyName == nameof(DuplicatePackageItem.KeepInAllPackages))
            {
                UpdateFixButtonCounter();
            }
        }

        private void UpdateFixButtonCounter()
        {
            int deleteCount = 0;
            
            foreach (var item in _duplicatePackages)
            {
                // If both options are unchecked, this duplicate is excluded from processing
                if (!item.KeepInAddonPackages && !item.KeepInAllPackages)
                    continue;

                if (item.ExistsInAddonPackages && !item.KeepInAddonPackages)
                    deleteCount++;
                if (item.ExistsInAllPackages && !item.KeepInAllPackages)
                    deleteCount++;
            }
            
            FixDuplicatesButton.Content = $"Fix Duplicates ({deleteCount})";
            FixDuplicatesButton.IsEnabled = deleteCount > 0;
        }

        private void UpdateStatusText()
        {
            StatusText.Text = $"Found {_duplicatePackages.Count} duplicate package(s)";
        }

        private void KeepAllPackages_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _duplicatePackages)
            {
                if (item.ExistsInAllPackages)
                {
                    item.KeepInAllPackages = true;
                    item.KeepInAddonPackages = false;
                }
            }
        }

        private void KeepAddonPackages_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _duplicatePackages)
            {
                if (item.ExistsInAddonPackages)
                {
                    item.KeepInAddonPackages = true;
                    item.KeepInAllPackages = false;
                }
            }
        }

        private async void FixDuplicates_Click(object sender, RoutedEventArgs e)
        {
            var packagesToDelete = new List<string>();
            var packagesToMove = new Dictionary<string, string>(); // source -> destination
            var packagesRequiringSelection = new List<DuplicatePackageItem>();
            
            // First pass: identify packages that need subfolder selection
            foreach (var item in _duplicatePackages)
            {
                if (item.KeepInAddonPackages && !item.KeepInAllPackages)
                {
                    // Check if there are multiple instances in AddonPackages that need selection
                    var baseName = ExtractBasePackageName(item.PackageName);
                    var addonInstances = GetPackageInstancesInAddonPackages(baseName);
                    if (addonInstances.Count > 1)
                    {
                        packagesRequiringSelection.Add(item);
                    }
                }
                else if (item.KeepInAllPackages && !item.KeepInAddonPackages)
                {
                    // Check if there are multiple instances in AllPackages that need selection
                    var baseName = ExtractBasePackageName(item.PackageName);
                    var allInstances = GetPackageInstancesInAllPackages(baseName);
                    if (allInstances.Count > 1)
                    {
                        packagesRequiringSelection.Add(item);
                    }
                }
            }
            
            // Handle packages that require subfolder selection FIRST
            var userCancelledSelection = false;
            var selectedFilesToKeep = new Dictionary<string, string>(); // packageName -> selectedFilePath
            
            foreach (var item in packagesRequiringSelection)
            {
                List<string> instances;
                var baseName = ExtractBasePackageName(item.PackageName);
                
                if (item.KeepInAddonPackages)
                {
                    instances = GetPackageInstancesInAddonPackages(baseName);
                }
                else
                {
                    instances = GetPackageInstancesInAllPackages(baseName);
                }
                
                // Filter to only the specific version the user selected
                var expectedFileName = item.PackageName + ".var";
                instances = instances.Where(path => 
                    Path.GetFileName(path).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                
                if (instances.Count > 1)
                {
                    var selectionWindow = new SubfolderSelectionWindow(item.PackageName, instances)
                    {
                        Owner = this
                    };
                    
                    var selectionResult = selectionWindow.ShowDialog();
                    if (selectionResult == true && !string.IsNullOrEmpty(selectionWindow.SelectedFilePath))
                    {
                        selectedFilesToKeep[item.PackageName] = selectionWindow.SelectedFilePath;
                    }
                    else
                    {
                        // User cancelled selection - abort entire operation
                        userCancelledSelection = true;
                        break;
                    }
                }
            }
            
            // If user cancelled any selection, abort the entire operation
            if (userCancelledSelection)
            {
                return;
            }
            
            // Now build the deletion/move list after all selections are confirmed
            foreach (var item in _duplicatePackages)
            {
                var baseName = ExtractBasePackageName(item.PackageName);
                
                if (item.KeepInAddonPackages && !item.KeepInAllPackages)
                {
                    // Want to keep in AddonPackages
                    var allPackageInstances = GetPackageInstancesInAllPackages(baseName);
                    var addonPackageInstances = GetPackageInstancesInAddonPackages(baseName);
                    
                    // If file doesn't exist in AddonPackages but exists in AllPackages, move it
                    if (addonPackageInstances.Count == 0 && allPackageInstances.Count > 0)
                    {
                        string sourceFile = selectedFilesToKeep.ContainsKey(item.PackageName) 
                            ? selectedFilesToKeep[item.PackageName] 
                            : allPackageInstances[0];
                        string destPath = BuildDestinationPath(sourceFile, _allPackagesPath, _addonPackagesPath);
                        packagesToMove[sourceFile] = destPath;
                        
                        // Delete other instances in AllPackages
                        var instancesToDelete = allPackageInstances.Where(path => 
                            !string.Equals(path, sourceFile, StringComparison.OrdinalIgnoreCase)).ToList();
                        packagesToDelete.AddRange(instancesToDelete);
                    }
                    else
                    {
                        // Delete all AllPackages instances
                        packagesToDelete.AddRange(allPackageInstances);
                        
                        // If multiple AddonPackages instances, delete the non-selected ones
                        if (selectedFilesToKeep.ContainsKey(item.PackageName))
                        {
                            var instancesToDelete = addonPackageInstances.Where(path => 
                                !string.Equals(path, selectedFilesToKeep[item.PackageName], StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            packagesToDelete.AddRange(instancesToDelete);
                        }
                    }
                }
                else if (item.KeepInAllPackages && !item.KeepInAddonPackages)
                {
                    // Want to keep in AllPackages
                    var allPackageInstances = GetPackageInstancesInAllPackages(baseName);
                    var addonPackageInstances = GetPackageInstancesInAddonPackages(baseName);
                    
                    // If file doesn't exist in AllPackages but exists in AddonPackages, move it
                    if (allPackageInstances.Count == 0 && addonPackageInstances.Count > 0)
                    {
                        string sourceFile = selectedFilesToKeep.ContainsKey(item.PackageName) 
                            ? selectedFilesToKeep[item.PackageName] 
                            : addonPackageInstances[0];
                        string destPath = BuildDestinationPath(sourceFile, _addonPackagesPath, _allPackagesPath);
                        packagesToMove[sourceFile] = destPath;
                        
                        // Delete other instances in AddonPackages
                        var instancesToDelete = addonPackageInstances.Where(path => 
                            !string.Equals(path, sourceFile, StringComparison.OrdinalIgnoreCase)).ToList();
                        packagesToDelete.AddRange(instancesToDelete);
                    }
                    else
                    {
                        // Delete all AddonPackages instances
                        packagesToDelete.AddRange(addonPackageInstances);
                        
                        // If multiple AllPackages instances, delete the non-selected ones
                        if (selectedFilesToKeep.ContainsKey(item.PackageName))
                        {
                            var instancesToDelete = allPackageInstances.Where(path => 
                                !string.Equals(path, selectedFilesToKeep[item.PackageName], StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            packagesToDelete.AddRange(instancesToDelete);
                        }
                    }
                }
            }
            
            if (packagesToDelete.Count == 0 && packagesToMove.Count == 0)
            {
                DarkMessageBox.Show("No packages selected for deletion or moving.", "Fix Duplicates", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            // Show confirmation window with detailed information grouped by package
            var confirmationWindow = new DuplicateFixConfirmationWindow(packagesToMove, packagesToDelete)
            {
                Owner = this
            };
            
            var result = confirmationWindow.ShowDialog();
            
            if (result != true || !confirmationWindow.Confirmed)
                return;
            
            // Perform safe move and deletion
            await PerformSafeOperations(packagesToMove, packagesToDelete);
        }
        
        private async System.Threading.Tasks.Task PerformSafeDeletion(List<string> filesToDelete)
        {
            int successCount = 0;
            int failCount = 0;
            var errors = new List<string>();
            
            // Process deletions with a small delay to allow UI updates
            foreach (var filePath in filesToDelete)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        // Verify it's a .var file before deletion (safety check)
                        if (Path.GetExtension(filePath).Equals(".var", StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(filePath);
                            successCount++;
                            
                            // Small delay to prevent UI freezing on large operations
                            if (successCount % 10 == 0)
                            {
                                await System.Threading.Tasks.Task.Delay(1);
                            }
                        }
                        else
                        {
                            errors.Add($"{Path.GetFileName(filePath)}: Not a .var file - skipped for safety");
                        }
                    }
                    else
                    {
                    }
                }
                catch (Exception ex)
                {
                    failCount++;
                    errors.Add($"{Path.GetFileName(filePath)}: {ex.Message}");
                }
            }
            
            // Show results - only show message if there were errors
            if (failCount > 0 || errors.Count > 0)
            {
                var errorMessage = $"Deleted {successCount} package(s) successfully.";
                errorMessage += $"\nEncountered {failCount + errors.Count} issue(s):\n\n";
                errorMessage += string.Join("\n", errors.Take(10));
                if (errors.Count > 10)
                    errorMessage += $"\n... and {errors.Count - 10} more";
                
                DarkMessageBox.Show(errorMessage, "Fix Duplicates Completed", 
                    MessageBoxButton.OK, successCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            
            // Close window if any files were successfully processed
            if (successCount > 0)
            {
                DialogResult = true;
                Close();
            }
        }

        private string GetPackageFilePath(PackageItem package, string basePath)
        {
            if (package == null || string.IsNullOrEmpty(basePath))
                return null;
            
            // Try to construct the file path
            var fileName = package.Name;
            if (fileName.EndsWith("#loaded", StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - 7);
            if (fileName.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - 9);
            
            fileName += ".var";
            
            var filePath = Path.Combine(basePath, fileName);
            if (File.Exists(filePath))
                return filePath;
            
            // Try searching in subdirectories
            try
            {
                var files = Directory.GetFiles(basePath, fileName, SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files[0];
            }
            catch
            {
                // Ignore search errors
            }
            
            return null;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
        
        /// <summary>
        /// Builds destination path preserving subfolder structure and handling conflicts
        /// </summary>
        private string BuildDestinationPath(string sourcePath, string sourceBasePath, string destBasePath)
        {
            try
            {
                // Get the relative path from source base (preserves subfolder structure)
                string relativePath = Path.GetRelativePath(sourceBasePath, sourcePath);
                string destPath = Path.Combine(destBasePath, relativePath);
                
                // Handle conflicts by appending a number
                if (File.Exists(destPath))
                {
                    string directory = Path.GetDirectoryName(destPath);
                    string fileNameWithoutExt = Path.GetFileNameWithoutExtension(destPath);
                    string extension = Path.GetExtension(destPath);
                    
                    int counter = 1;
                    do
                    {
                        destPath = Path.Combine(directory, $"{fileNameWithoutExt}_conflict{counter}{extension}");
                        counter++;
                    } while (File.Exists(destPath));
                    
                }
                
                return destPath;
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Performs safe move and deletion operations
        /// </summary>
        private async System.Threading.Tasks.Task PerformSafeOperations(Dictionary<string, string> filesToMove, List<string> filesToDelete)
        {
            int moveSuccessCount = 0;
            int moveFailCount = 0;
            int deleteSuccessCount = 0;
            int deleteFailCount = 0;
            var errors = new List<string>();
            
            // First, perform moves
            foreach (var moveOp in filesToMove)
            {
                string sourcePath = moveOp.Key;
                string destPath = moveOp.Value;
                
                try
                {
                    if (File.Exists(sourcePath))
                    {
                        // Verify it's a .var file
                        if (Path.GetExtension(sourcePath).Equals(".var", StringComparison.OrdinalIgnoreCase))
                        {
                            // Ensure destination directory exists
                            string destDir = Path.GetDirectoryName(destPath);
                            if (!Directory.Exists(destDir))
                            {
                                Directory.CreateDirectory(destDir);
                            }
                            
                            // Move the file
                            File.Move(sourcePath, destPath);
                            moveSuccessCount++;
                            
                            await System.Threading.Tasks.Task.Delay(1);
                        }
                        else
                        {
                            errors.Add($"{Path.GetFileName(sourcePath)}: Not a .var file - skipped for safety");
                        }
                    }
                    else
                    {
                    }
                }
                catch (Exception ex)
                {
                    moveFailCount++;
                    errors.Add($"{Path.GetFileName(sourcePath)}: Move failed - {ex.Message}");
                }
            }
            
            // Then, perform deletions
            foreach (var filePath in filesToDelete)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        // Verify it's a .var file before deletion (safety check)
                        if (Path.GetExtension(filePath).Equals(".var", StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(filePath);
                            deleteSuccessCount++;
                            
                            // Small delay to prevent UI freezing on large operations
                            if (deleteSuccessCount % 10 == 0)
                            {
                                await System.Threading.Tasks.Task.Delay(1);
                            }
                        }
                        else
                        {
                            errors.Add($"{Path.GetFileName(filePath)}: Not a .var file - skipped for safety");
                        }
                    }
                    else
                    {
                    }
                }
                catch (Exception ex)
                {
                    deleteFailCount++;
                    errors.Add($"{Path.GetFileName(filePath)}: {ex.Message}");
                }
            }
            
            // Show results - only show message if there were errors
            int totalSuccess = moveSuccessCount + deleteSuccessCount;
            int totalFail = moveFailCount + deleteFailCount;
            
            if (totalFail > 0 || errors.Count > 0)
            {
                var errorMessage = $"Operation completed:\n";
                if (moveSuccessCount > 0)
                    errorMessage += $"• Moved {moveSuccessCount} package(s)\n";
                if (deleteSuccessCount > 0)
                    errorMessage += $"• Deleted {deleteSuccessCount} package(s)\n";
                    
                errorMessage += $"\nEncountered {totalFail + errors.Count} issue(s):\n\n";
                errorMessage += string.Join("\n", errors.Take(10));
                if (errors.Count > 10)
                    errorMessage += $"\n... and {errors.Count - 10} more";
                
                DarkMessageBox.Show(errorMessage, "Fix Duplicates Completed", 
                    MessageBoxButton.OK, totalSuccess > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            
            // Close window if any files were successfully processed
            if (totalSuccess > 0)
            {
                DialogResult = true;
                Close();
            }
        }

        #region Drag Selection for Duplicate Checkboxes

        private void DuplicatesDataGrid_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                var dataGrid = sender as System.Windows.Controls.DataGrid;
                var hitTest = System.Windows.Media.VisualTreeHelper.HitTest(dataGrid, e.GetPosition(dataGrid));
                
                var checkbox = FindParent<System.Windows.Controls.CheckBox>(hitTest?.VisualHit as System.Windows.DependencyObject);
                if (checkbox != null && checkbox.IsEnabled)
                {
                    _duplicateDragStartCheckbox = checkbox;
                    bool currentCheckboxState = checkbox.IsChecked == true;
                    _duplicateDragCheckState = !currentCheckboxState;
                    
                    _duplicateDragStartPoint = e.GetPosition(dataGrid);
                    _duplicateDragStartRow = FindParent<System.Windows.Controls.DataGridRow>(hitTest?.VisualHit as System.Windows.DependencyObject);
                    _duplicateDragging = false;
                    return;
                }
                
                _duplicateDragStartCheckbox = null;
                _duplicateDragCheckState = null;
                _duplicateDragStartRow = null;
            }
        }

        private void DuplicatesDataGrid_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                _duplicateDragging = false;
                _duplicateDragStartRow = null;
                _duplicateDragStartCheckbox = null;
                _duplicateDragCheckState = null;
            }
        }

        private void DuplicatesDataGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var dataGrid = sender as System.Windows.Controls.DataGrid;
            var currentPoint = e.GetPosition(dataGrid);
            
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && _duplicateDragStartRow != null && 
                _duplicateDragStartCheckbox != null)
            {
                if (Math.Abs(currentPoint.X - _duplicateDragStartPoint.X) > 3 || 
                    Math.Abs(currentPoint.Y - _duplicateDragStartPoint.Y) > 3)
                {
                    if (!_duplicateDragging)
                    {
                        _duplicateDragging = true;
                        
                        // Apply state to the starting row when drag begins
                        var startCheckbox = FindCheckboxInRow(_duplicateDragStartRow);
                        
                        if (startCheckbox != null && startCheckbox.IsEnabled && 
                            startCheckbox.IsChecked != _duplicateDragCheckState)
                        {
                            startCheckbox.IsChecked = _duplicateDragCheckState;
                        }
                    }
                    
                    var hitTest = System.Windows.Media.VisualTreeHelper.HitTest(dataGrid, currentPoint);
                    var currentRow = FindParent<System.Windows.Controls.DataGridRow>(hitTest?.VisualHit as System.Windows.DependencyObject);
                    
                    if (currentRow != null && currentRow != _duplicateDragStartRow)
                    {
                        var currentCheckbox = FindCheckboxInRow(currentRow);
                        
                        if (currentCheckbox != null && currentCheckbox.IsEnabled && 
                            currentCheckbox.IsChecked != _duplicateDragCheckState)
                        {
                            currentCheckbox.IsChecked = _duplicateDragCheckState;
                        }
                    }
                }
            }
        }

        private System.Windows.Controls.CheckBox FindCheckboxInRow(System.Windows.Controls.DataGridRow row)
        {
            if (row == null) return null;
            
            try
            {
                var checkboxes = new List<System.Windows.Controls.CheckBox>();
                CollectCheckboxesInVisualTree(row, checkboxes);
                
                if (_duplicateDragStartCheckbox != null && checkboxes.Count > 0)
                {
                    var startCheckboxColumn = GetCheckboxColumnIndex(_duplicateDragStartCheckbox);
                    
                    foreach (var cb in checkboxes)
                    {
                        var cbColumn = GetCheckboxColumnIndex(cb);
                        if (cbColumn == startCheckboxColumn)
                        {
                            return cb;
                        }
                    }
                }
                
                return checkboxes.FirstOrDefault();
            }
            catch
            {
            }
            
            return null;
        }

        private int GetCheckboxColumnIndex(System.Windows.Controls.CheckBox checkbox)
        {
            try
            {
                var cell = FindParent<System.Windows.Controls.DataGridCell>(checkbox);
                if (cell != null && cell.Column != null)
                {
                    var dataGrid = FindParent<System.Windows.Controls.DataGrid>(cell);
                    if (dataGrid != null)
                    {
                        return dataGrid.Columns.IndexOf(cell.Column);
                    }
                }
            }
            catch
            {
            }
            return -1;
        }

        private void CollectCheckboxesInVisualTree(System.Windows.DependencyObject obj, List<System.Windows.Controls.CheckBox> checkboxes)
        {
            if (obj == null) return;
            
            if (obj is System.Windows.Controls.CheckBox checkbox)
            {
                checkboxes.Add(checkbox);
            }
            
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(obj, i);
                CollectCheckboxesInVisualTree(child, checkboxes);
            }
        }

        private T FindParent<T>(System.Windows.DependencyObject obj) where T : System.Windows.DependencyObject
        {
            if (obj == null) return null;
            
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(obj);
            if (parent is T typedParent)
                return typedParent;
            
            return FindParent<T>(parent);
        }

        #endregion
    }

    public class DuplicatePackageItem : INotifyPropertyChanged
    {
        private bool _keepInAddonPackages;
        private bool _keepInAllPackages;
        private bool _existsInAddonPackages;
        private bool _existsInAllPackages;
        private long _fileSizeBytes;
        private bool _isUpdating;

        public string PackageName { get; set; } = string.Empty;

        public bool ExistsInAddonPackages
        {
            get => _existsInAddonPackages;
            set => SetField(ref _existsInAddonPackages, value);
        }

        public bool ExistsInAllPackages
        {
            get => _existsInAllPackages;
            set => SetField(ref _existsInAllPackages, value);
        }

        public bool KeepInAddonPackages
        {
            get => _keepInAddonPackages;
            set
            {
                if (!SetField(ref _keepInAddonPackages, value))
                    return;

                if (_isUpdating)
                    return;

                if (value && ExistsInAddonPackages && ExistsInAllPackages)
                {
                    try
                    {
                        _isUpdating = true;
                        KeepInAllPackages = false;
                    }
                    finally
                    {
                        _isUpdating = false;
                    }
                }
            }
        }

        public bool KeepInAllPackages
        {
            get => _keepInAllPackages;
            set
            {
                if (!SetField(ref _keepInAllPackages, value))
                    return;

                if (_isUpdating)
                    return;

                if (value && ExistsInAddonPackages && ExistsInAllPackages)
                {
                    try
                    {
                        _isUpdating = true;
                        KeepInAddonPackages = false;
                    }
                    finally
                    {
                        _isUpdating = false;
                    }
                }
            }
        }

        public long FileSizeBytes
        {
            get => _fileSizeBytes;
            set => SetField(ref _fileSizeBytes, value);
        }

        public PackageItem LoadedPackageItem { get; set; }
        public PackageItem AvailablePackageItem { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

