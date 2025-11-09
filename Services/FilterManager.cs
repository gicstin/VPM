using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPM.Models;

namespace VPM.Services
{
    public class FilterManager
    {
        // File size thresholds (in MB) - can be configured
        public double FileSizeTinyMax { get; set; } = 1;
        public double FileSizeSmallMax { get; set; } = 10;
        public double FileSizeMediumMax { get; set; } = 100;
        
        public FavoritesManager FavoritesManager { get; set; } = null;
        public AutoInstallManager AutoInstallManager { get; set; } = null;
        
        public string SelectedStatus { get; set; } = null;
        public List<string> SelectedStatuses { get; set; } = new List<string>();
        public List<string> SelectedFavoriteStatuses { get; set; } = new List<string>();
        public List<string> SelectedAutoInstallStatuses { get; set; } = new List<string>();
        public List<string> SelectedOptimizationStatuses { get; set; } = new List<string>();
        public List<string> SelectedVersionStatuses { get; set; } = new List<string>();
        public string SelectedCategory { get; set; } = null;
        public List<string> SelectedCategories { get; set; } = new List<string>();
        public string SelectedCreator { get; set; } = null;
        public List<string> SelectedCreators { get; set; } = new List<string>();
        public string SelectedLicenseType { get; set; } = null;
        public List<string> SelectedLicenseTypes { get; set; } = new List<string>();
        public List<string> SelectedFileSizeRanges { get; set; } = new List<string>();
        public List<string> SelectedSubfolders { get; set; } = new List<string>();
        public string SelectedDamagedFilter { get; set; } = null;
        public string SearchText { get; set; } = "";
        public HashSet<string> SelectedPackages { get; set; } = new HashSet<string>();
        public DateFilter DateFilter { get; set; } = new DateFilter();
        public bool FilterDuplicates { get; set; } = false;
        public bool HideArchivedPackages { get; set; } = true;

        public void ClearAllFilters()
        {
            SelectedStatus = null;
            SelectedStatuses.Clear();
            SelectedFavoriteStatuses.Clear();
            SelectedAutoInstallStatuses.Clear();
            SelectedOptimizationStatuses.Clear();
            SelectedVersionStatuses.Clear();
            SelectedCategory = null;
            SelectedCategories.Clear();
            SelectedCreator = null;
            SelectedCreators.Clear();
            SelectedLicenseType = null;
            SelectedLicenseTypes.Clear();
            SelectedFileSizeRanges.Clear();
            SelectedSubfolders.Clear();
            SelectedDamagedFilter = null;
            SearchText = "";
            SelectedPackages.Clear();
            DateFilter = new DateFilter();
            FilterDuplicates = false;
        }

        public void ClearCategoryFilter()
        {
            SelectedCategory = null;
            SelectedCategories.Clear();
        }

        public void ClearCreatorFilter()
        {
            SelectedCreator = null;
            SelectedCreators.Clear();
        }

        public void ClearStatusFilter()
        {
            SelectedStatus = null;
            SelectedStatuses.Clear();
            FilterDuplicates = false;
        }

        public void ClearLicenseFilter()
        {
            SelectedLicenseType = null;
            SelectedLicenseTypes.Clear();
        }

        public void ClearDateFilter()
        {
            DateFilter = new DateFilter();
        }

        public void ClearFileSizeFilter()
        {
            SelectedFileSizeRanges.Clear();
        }

        public void ClearSubfoldersFilter()
        {
            SelectedSubfolders.Clear();
        }

        public void SetSearchText(string text)
        {
            SearchText = SearchHelper.PrepareSearchText(text);
        }

        public bool MatchesSearch(string packageName, VarMetadata metadata = null)
        {
            // Simple, fast "starts with" search on package name only
            // No description/tags/categories search for maximum performance
            return SearchHelper.MatchesPackageSearch(packageName, SearchText);
        }

        public bool MatchesFilters(VarMetadata metadata)
        {
            if (metadata == null)
                return false;

            // Hide archived packages if enabled
            if (HideArchivedPackages)
            {
                // Check if package is archived by status
                if (metadata.Status != null && metadata.Status.Equals("Archived", StringComparison.OrdinalIgnoreCase))
                    return false;
                
                // Check if package is archived by variant role
                if (metadata.VariantRole != null && metadata.VariantRole.Equals("Archived", StringComparison.OrdinalIgnoreCase))
                    return false;
                
                // Check if package is archived by filename suffix (backup marker)
                if (metadata.Filename != null && metadata.Filename.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                    return false;
                
                // Check if package is in archive folder (handle both "archive" and "ArchivedPackages")
                // BUT: Don't hide old version packages - they should be available for optimization
                // Only hide if it's a backup (has #archived suffix) or is explicitly marked as Archived
                // Old versions are legitimate packages that can be optimized
                string pathToCheck = !string.IsNullOrEmpty(metadata.FilePath) ? metadata.FilePath : metadata.Filename;
                if (!string.IsNullOrEmpty(pathToCheck))
                {
                    // Only hide if it's a backup (marked with #archived), not just because it's in ArchivedPackages
                    // Old version packages in ArchivedPackages should still be visible for optimization
                    if (pathToCheck.IndexOf("\\archive\\", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                    
                    // For ArchivedPackages folder, only hide if it's a backup (has #archived in filename)
                    if (pathToCheck.IndexOf("\\ArchivedPackages\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Check if this is a backup (has #archived suffix)
                        if (metadata.Filename != null && metadata.Filename.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                            return false;
                        // Otherwise, allow old version packages to be shown for optimization
                    }
                }
            }

            // Status filter - support both single and multiple selections
            if (!string.IsNullOrEmpty(SelectedStatus) && metadata.Status != SelectedStatus)
                return false;
            
            if (SelectedStatuses.Count > 0 && !SelectedStatuses.Contains(metadata.Status))
                return false;

            // Optimization status filter
            if (SelectedOptimizationStatuses.Count > 0)
            {
                // Exclude archived packages from optimization filter - they are backups, not active packages
                if (metadata.Status == "Archived")
                    return false;
                
                bool matchesOptimization = false;
                foreach (var optStatus in SelectedOptimizationStatuses)
                {
                    if (optStatus.StartsWith("Optimized") && metadata.IsOptimized)
                    {
                        matchesOptimization = true;
                        break;
                    }
                    else if (optStatus.StartsWith("Unoptimized") && !metadata.IsOptimized)
                    {
                        matchesOptimization = true;
                        break;
                    }
                }
                if (!matchesOptimization)
                    return false;
            }

            // Version status filter
            if (SelectedVersionStatuses.Count > 0)
            {
                bool matchesVersion = false;
                foreach (var verStatus in SelectedVersionStatuses)
                {
                    if (verStatus.StartsWith("Latest") && !metadata.IsOldVersion)
                    {
                        matchesVersion = true;
                        break;
                    }
                    else if (verStatus.StartsWith("Old") && metadata.IsOldVersion)
                    {
                        matchesVersion = true;
                        break;
                    }
                }
                if (!matchesVersion)
                    return false;
            }

            // Favorites filter
            if (SelectedFavoriteStatuses.Count > 0 && FavoritesManager != null)
            {
                var packageName = Path.GetFileNameWithoutExtension(metadata.Filename);
                bool isFavorite = FavoritesManager.IsFavorite(packageName);
                
                // Only show favorites when filter is active
                if (!isFavorite)
                    return false;
            }
            else if (SelectedFavoriteStatuses.Count > 0 && FavoritesManager == null)
            {
                Console.WriteLine($"[ERROR] Favorites filter active but FavoritesManager is NULL!");
            }

            // AutoInstall filter
            if (SelectedAutoInstallStatuses.Count > 0 && AutoInstallManager != null)
            {
                var packageName = Path.GetFileNameWithoutExtension(metadata.Filename);
                bool isAutoInstall = AutoInstallManager.IsAutoInstall(packageName);
                
                // Only show autoinstall packages when filter is active
                if (!isAutoInstall)
                    return false;
            }
            else if (SelectedAutoInstallStatuses.Count > 0 && AutoInstallManager == null)
            {
                Console.WriteLine($"[ERROR] AutoInstall filter active but AutoInstallManager is NULL!");
            }

            // Category filter - support both single and multiple selections
            if (!string.IsNullOrEmpty(SelectedCategory))
            {
                if (metadata.Categories == null || !metadata.Categories.Any(cat => 
                    string.Equals(SelectedCategory, cat, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            
            if (SelectedCategories.Count > 0)
            {
                if (metadata.Categories == null || !SelectedCategories.Any(selectedCat => 
                    metadata.Categories.Any(metaCat => 
                        string.Equals(selectedCat, metaCat, StringComparison.OrdinalIgnoreCase))))
                    return false;
            }

            // Creator filter - support both single and multiple selections (case-insensitive)
            if (!string.IsNullOrEmpty(SelectedCreator) && 
                !string.Equals(metadata.CreatorName, SelectedCreator, StringComparison.OrdinalIgnoreCase))
                return false;
                
            if (SelectedCreators.Count > 0 && 
                !SelectedCreators.Any(c => string.Equals(c, metadata.CreatorName, StringComparison.OrdinalIgnoreCase)))
                return false;

            // License filter - support both single and multiple selections
            if (!string.IsNullOrEmpty(SelectedLicenseType) && metadata.LicenseType != SelectedLicenseType)
                return false;
            
            if (SelectedLicenseTypes.Count > 0)
            {
                var license = string.IsNullOrEmpty(metadata.LicenseType) ? "Unknown" : metadata.LicenseType;
                if (!SelectedLicenseTypes.Contains(license))
                    return false;
            }

            // Handle duplicate filtering - when filtering duplicates, we want to show only unique duplicate packages
            // This is handled at the package loading level, not here, to avoid showing multiple instances
            if (FilterDuplicates && metadata.DuplicateLocationCount <= 1)
                return false;

            // Search text filter - only apply if search text is provided
            if (!string.IsNullOrEmpty(SearchText))
            {
                var packageName = Path.GetFileNameWithoutExtension(metadata.Filename);
                if (!MatchesSearch(packageName, metadata))
                    return false;
            }

            // Date filter - check both ModifiedDate and CreatedDate
            if (DateFilter.FilterType != DateFilterType.AllTime)
            {
                // Prefer ModifiedDate, fall back to CreatedDate
                var dateToCheck = metadata.ModifiedDate ?? metadata.CreatedDate;
                if (!DateFilter.MatchesFilter(dateToCheck))
                    return false;
            }

            // File size filter
            if (SelectedFileSizeRanges.Count > 0)
            {
                if (!MatchesFileSizeFilter(metadata.FileSize))
                    return false;
            }

            // Subfolders filter
            if (SelectedSubfolders.Count > 0)
            {
                var subfolder = ExtractSubfolderFromMetadata(metadata);
                if (string.IsNullOrEmpty(subfolder) || !SelectedSubfolders.Any(s => string.Equals(s, subfolder, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            // Damaged filter
            if (!string.IsNullOrEmpty(SelectedDamagedFilter))
            {
                if (SelectedDamagedFilter.Contains("Damaged"))
                {
                    if (!metadata.IsDamaged)
                        return false;
                }
                else if (SelectedDamagedFilter.Contains("Valid"))
                {
                    if (metadata.IsDamaged)
                        return false;
                }
                // "All Packages" option passes all packages through
            }

            return true;
        }

        public Dictionary<string, int> GetCreatorCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            // Track unique packages per creator (archived and optimized versions count as one)
            var uniquePackagesPerCreator = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var kvp in packages)
            {
                var packageKey = kvp.Key;
                var package = kvp.Value;
                
                if (!string.IsNullOrEmpty(package.CreatorName))
                {
                    // Get the base package name (without #archived suffix)
                    string basePackageName = packageKey.EndsWith("#archived", StringComparison.OrdinalIgnoreCase)
                        ? packageKey.Substring(0, packageKey.Length - 9)
                        : packageKey;
                    
                    // Initialize set if needed
                    if (!uniquePackagesPerCreator.ContainsKey(package.CreatorName))
                    {
                        uniquePackagesPerCreator[package.CreatorName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    
                    // Add to unique set
                    uniquePackagesPerCreator[package.CreatorName].Add(basePackageName);
                }
            }
            
            // Convert unique sets to counts
            foreach (var kvp in uniquePackagesPerCreator)
            {
                counts[kvp.Key] = kvp.Value.Count;
            }
            
            return counts;
        }

        public int GetDuplicateCount(Dictionary<string, VarMetadata> packages)
        {
            if (packages == null)
            {
                return 0;
            }

            // Simple fix: count all duplicates and divide by 2 to get real number
            int duplicateCount = 0;
            foreach (var package in packages.Values)
            {
                if (package != null && package.DuplicateLocationCount > 1)
                {
                    duplicateCount++;
                }
            }

            return duplicateCount / 2; // Halve the count to get real number of unique duplicates
        }

        public Dictionary<string, int> GetCategoryCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var package in packages.Values)
            {
                if (package.Categories != null)
                {
                    foreach (var category in package.Categories)
                    {
                        if (!string.IsNullOrEmpty(category))
                        {
                            if (counts.TryGetValue(category, out var count))
                            {
                                counts[category] = count + 1;
                            }
                            else
                            {
                                counts[category] = 1;
                            }
                        }
                    }
                }
            }
            
            return counts;
        }

        public Dictionary<string, int> GetStatusCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            // Track unique packages per status (archived and optimized versions count as one)
            var uniquePackagesPerStatus = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var kvp in packages)
            {
                var packageKey = kvp.Key;
                var package = kvp.Value;
                
                if (!string.IsNullOrEmpty(package.Status))
                {
                    // Get the base package name (without #archived suffix)
                    string basePackageName = packageKey.EndsWith("#archived", StringComparison.OrdinalIgnoreCase)
                        ? packageKey.Substring(0, packageKey.Length - 9)
                        : packageKey;
                    
                    // Initialize set if needed
                    if (!uniquePackagesPerStatus.ContainsKey(package.Status))
                    {
                        uniquePackagesPerStatus[package.Status] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    
                    // Add to unique set
                    uniquePackagesPerStatus[package.Status].Add(basePackageName);
                }
            }
            
            // Convert unique sets to counts
            foreach (var kvp in uniquePackagesPerStatus)
            {
                counts[kvp.Key] = kvp.Value.Count;
            }
            
            return counts;
        }

        public Dictionary<string, int> GetOptimizationStatusCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>
            {
                ["Optimized"] = 0,
                ["Unoptimized"] = 0
            };
            
            foreach (var package in packages.Values)
            {
                if (package.IsOptimized)
                    counts["Optimized"]++;
                else
                    counts["Unoptimized"]++;
            }
            
            return counts;
        }

        public Dictionary<string, int> GetVersionStatusCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>
            {
                ["Latest"] = 0,
                ["Old"] = 0
            };
            
            foreach (var package in packages.Values)
            {
                if (package.IsOldVersion)
                    counts["Old"]++;
                else
                    counts["Latest"]++;
            }
            
            return counts;
        }

        public Dictionary<string, int> GetLicenseCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var package in packages.Values)
            {
                var license = string.IsNullOrEmpty(package.LicenseType) ? "Unknown" : package.LicenseType;
                // Use TryGetValue for single lookup instead of ContainsKey + indexer (3x faster)
                if (counts.TryGetValue(license, out var count))
                {
                    counts[license] = count + 1;
                }
                else
                {
                    counts[license] = 1;
                }
            }
            
            return counts;
        }

        /// <summary>
        /// Get content type counts from packages (categories)
        /// </summary>
        public Dictionary<string, int> GetContentTypeCounts(Dictionary<string, VarMetadata> packages)
        {
            return GetCategoryCounts(packages); // Content types are essentially categories
        }

        /// <summary>
        /// Check if a file size matches any of the selected size ranges
        /// </summary>
        private bool MatchesFileSizeFilter(long fileSizeBytes)
        {
            if (SelectedFileSizeRanges.Count == 0)
                return true;

            // Convert bytes to MB for comparison
            double fileSizeMB = fileSizeBytes / (1024.0 * 1024.0);

            foreach (var range in SelectedFileSizeRanges)
            {
                // Extract the range name without the count (e.g., "Tiny (5)" -> "Tiny")
                var rangeName = range.Split('(')[0].Trim();
                
                if (rangeName == "Tiny" && fileSizeMB < FileSizeTinyMax)
                    return true;
                if (rangeName == "Small" && fileSizeMB >= FileSizeTinyMax && fileSizeMB < FileSizeSmallMax)
                    return true;
                if (rangeName == "Medium" && fileSizeMB >= FileSizeSmallMax && fileSizeMB < FileSizeMediumMax)
                    return true;
                if (rangeName == "Large" && fileSizeMB >= FileSizeMediumMax)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Get file size range counts from packages
        /// </summary>
        public Dictionary<string, int> GetFileSizeCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>
            {
                ["Tiny"] = 0,
                ["Small"] = 0,
                ["Medium"] = 0,
                ["Large"] = 0
            };

            foreach (var package in packages.Values)
            {
                double fileSizeMB = package.FileSize / (1024.0 * 1024.0);

                if (fileSizeMB < FileSizeTinyMax)
                    counts["Tiny"]++;
                else if (fileSizeMB < FileSizeSmallMax)
                    counts["Small"]++;
                else if (fileSizeMB < FileSizeMediumMax)
                    counts["Medium"]++;
                else
                    counts["Large"]++;
            }

            return counts;
        }

        /// <summary>
        /// Get subfolder counts from packages
        /// </summary>
        public Dictionary<string, int> GetSubfolderCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var package in packages.Values)
            {
                string subfolder = ExtractSubfolderFromMetadata(package);
                
                if (!string.IsNullOrEmpty(subfolder))
                {
                    if (counts.TryGetValue(subfolder, out var count))
                    {
                        counts[subfolder] = count + 1;
                    }
                    else
                    {
                        counts[subfolder] = 1;
                    }
                }
            }

            return counts;
        }

        /// <summary>
        /// Check if a file path matches any of the selected subfolders
        /// </summary>
        private bool MatchesSubfoldersFilter(string filePath)
        {
            if (SelectedSubfolders.Count == 0)
                return true;

            if (string.IsNullOrEmpty(filePath))
                return false;

            var pathParts = filePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            
            if (pathParts.Length >= 2)
            {
                var subfolder = pathParts[pathParts.Length - 2]; // Second to last is the folder
                
                foreach (var selectedSubfolder in SelectedSubfolders)
                {
                    if (string.Equals(subfolder, selectedSubfolder, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Extract full subfolder path from metadata using FilePath or Filename
        /// Returns the full relative path under AddonPackages or AllPackages
        /// Example: C:\VAM\AddonPackages\Folder1\SubFolder2\Package.var -> "Folder1/SubFolder2"
        /// Returns null if package is directly in AddonPackages/AllPackages root (not in a subfolder)
        /// </summary>
        private string ExtractSubfolderFromMetadata(VarMetadata metadata)
        {
            string pathToCheck = null;
            
            if (!string.IsNullOrEmpty(metadata.FilePath))
            {
                pathToCheck = metadata.FilePath;
            }
            else if (!string.IsNullOrEmpty(metadata.Filename))
            {
                pathToCheck = metadata.Filename;
            }
            
            if (string.IsNullOrEmpty(pathToCheck))
                return null;
            
            // Normalize path separators
            pathToCheck = pathToCheck.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            
            // Look for AddonPackages or AllPackages in the path
            string addonPackagesMarker = $"{Path.DirectorySeparatorChar}AddonPackages{Path.DirectorySeparatorChar}";
            string allPackagesMarker = $"{Path.DirectorySeparatorChar}AllPackages{Path.DirectorySeparatorChar}";
            
            int markerIndex = -1;
            
            if (pathToCheck.Contains(addonPackagesMarker, StringComparison.OrdinalIgnoreCase))
            {
                markerIndex = pathToCheck.IndexOf(addonPackagesMarker, StringComparison.OrdinalIgnoreCase);
                markerIndex += addonPackagesMarker.Length;
            }
            else if (pathToCheck.Contains(allPackagesMarker, StringComparison.OrdinalIgnoreCase))
            {
                markerIndex = pathToCheck.IndexOf(allPackagesMarker, StringComparison.OrdinalIgnoreCase);
                markerIndex += allPackagesMarker.Length;
            }
            
            if (markerIndex > 0)
            {
                // Get the path after AddonPackages or AllPackages
                string remainingPath = pathToCheck.Substring(markerIndex);
                var pathParts = remainingPath.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                
                // pathParts[last] is the filename
                // pathParts[0..last-1] are the folder levels
                if (pathParts.Length >= 2)
                {
                    // Package is in a subfolder - return the full relative path (all folders except filename)
                    var folderPath = string.Join("/", pathParts, 0, pathParts.Length - 1);
                    return folderPath;
                }
                else if (pathParts.Length == 1)
                {
                    // Package is directly in AddonPackages/AllPackages root - no subfolder
                    return null;
                }
            }
            
            return null;
        }

        /// <summary>
        /// Check if a package passes all current filters
        /// </summary>
        public bool PassesPackageFilter(PackageItem package, string searchText, Dictionary<string, object> filters)
        {
            if (package == null) return false;

            // Search text filter - optimized with early exit
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                bool foundInName = package.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
                bool foundInCreator = package.Creator != null && package.Creator.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
                
                if (!foundInName && !foundInCreator)
                {
                    return false;
                }
            }

            // Status filter
            if (filters.TryGetValue("Status", out var statusFilter) && statusFilter is List<string> selectedStatuses)
            {
                if (selectedStatuses.Count > 0 && !selectedStatuses.Contains(package.Status))
                {
                    return false;
                }
            }

            if (filters.TryGetValue("Duplicate", out var duplicatesFilter) && duplicatesFilter is bool filterDuplicates && filterDuplicates)
            {
                if (!package.IsDuplicate)
                {
                    return false;
                }
            }

            // Creator filter (case-insensitive)
            if (filters.TryGetValue("Creator", out var creatorFilter) && creatorFilter is List<string> selectedCreators)
            {
                if (selectedCreators.Count > 0 && 
                    !selectedCreators.Any(c => string.Equals(c, package.Creator, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            // Content type filter - need to check against metadata
            if (filters.TryGetValue("ContentType", out var contentTypeFilter) && contentTypeFilter is List<string> selectedTypes)
            {
                if (selectedTypes.Count > 0)
                {
                    // Content type filtering requires metadata which is not available in this method
                    // The calling code should use PassesPackageFilterWithMetadata instead
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Check if a package passes all current filters using full metadata
        /// </summary>
        public bool PassesPackageFilterWithMetadata(VarMetadata metadata, string searchText, Dictionary<string, object> filters)
        {
            if (metadata == null) return false;

            var packageName = Path.GetFileNameWithoutExtension(metadata.Filename);
            var creator = metadata.CreatorName;
            

            // Search text filter - optimized with early exit
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                bool foundInName = packageName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
                bool foundInCreator = creator != null && creator.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
                
                if (!foundInName && !foundInCreator)
                {
                    return false;
                }
            }

            // Status filter
            if (filters.TryGetValue("Status", out var statusFilter) && statusFilter is List<string> selectedStatuses)
            {
                if (selectedStatuses.Count > 0 && !selectedStatuses.Contains(metadata.Status))
                {
                    return false;
                }
            }

            if (filters.TryGetValue("Duplicate", out var duplicatesFilter) && duplicatesFilter is bool filterDuplicates && filterDuplicates)
            {
                if (!metadata.IsDuplicate)
                {
                    return false;
                }
            }

            // Creator filter (case-insensitive)
            if (filters.TryGetValue("Creator", out var creatorFilter) && creatorFilter is List<string> selectedCreators)
            {
                if (selectedCreators.Count > 0 && 
                    !selectedCreators.Any(c => string.Equals(c, creator, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            // Content type filter
            if (filters.TryGetValue("ContentType", out var contentTypeFilter) && contentTypeFilter is List<string> selectedTypes)
            {
                if (selectedTypes.Count > 0)
                {
                    var packageCategories = metadata.Categories ?? new HashSet<string>();
                    bool hasMatchingCategory = selectedTypes.Any(selectedType => 
                        packageCategories.Any(category => 
                            category.Equals(selectedType, StringComparison.OrdinalIgnoreCase)));
                    
                    
                    if (!hasMatchingCategory)
                    {
                        return false;
                    }
                }
            }

            // Date filter
            if (filters.TryGetValue("DateFilter", out var dateFilterObj) && dateFilterObj is DateFilter dateFilter)
            {
                if (dateFilter.FilterType != DateFilterType.AllTime)
                {
                    // Prefer ModifiedDate, fall back to CreatedDate
                    var dateToCheck = metadata.ModifiedDate ?? metadata.CreatedDate;
                    
                    if (!dateFilter.MatchesFilter(dateToCheck))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}

