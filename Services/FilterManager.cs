using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public HashSet<string> SelectedStatuses { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectedFavoriteStatuses { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectedAutoInstallStatuses { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectedOptimizationStatuses { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectedVersionStatuses { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string SelectedCategory { get; set; } = null;
        public HashSet<string> SelectedCategories { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string SelectedCreator { get; set; } = null;
        public HashSet<string> SelectedCreators { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string SelectedLicenseType { get; set; } = null;
        public HashSet<string> SelectedLicenseTypes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectedFileSizeRanges { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectedSubfolders { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string SelectedDamagedFilter { get; set; } = null;
        public string SearchText { get; set; } = "";
        private string[] _searchTerms = Array.Empty<string>();
        public HashSet<string> SelectedPackages { get; set; } = new HashSet<string>();
        public DateFilter DateFilter { get; set; } = new DateFilter();
        public bool FilterDuplicates { get; set; } = false;
        public bool FilterNoDependents { get; set; } = false;
        public bool FilterNoDependencies { get; set; } = false;
        public bool HideArchivedPackages { get; set; } = true;

        // Content tag filtering (clothing and hair)
        public HashSet<string> SelectedClothingTags { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectedHairTags { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool RequireAllTags { get; set; } = false;

        // External destination filtering
        public HashSet<string> SelectedDestinations { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            FilterNoDependents = false;
            FilterNoDependencies = false;
            SelectedClothingTags.Clear();
            SelectedHairTags.Clear();
            RequireAllTags = false;
            SelectedDestinations.Clear();
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
            FilterNoDependents = false;
            FilterNoDependencies = false;
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

        public void ClearTagFilters()
        {
            SelectedClothingTags.Clear();
        }

        public void ClearDestinationFilter()
        {
            SelectedDestinations.Clear();
        }

        public void ClearClothingTagFilter()
        {
            SelectedClothingTags.Clear();
        }

        public void ClearHairTagFilter()
        {
            SelectedHairTags.Clear();
        }

        public void SetSearchText(string text)
        {
            SearchText = SearchHelper.PrepareSearchText(text);
            _searchTerms = SearchHelper.PrepareSearchTerms(SearchText);
        }

        public bool MatchesSearch(string packageName, VarMetadata metadata = null)
        {
            // Simple, fast "starts with" search on package name only
            // No description/tags/categories search for maximum performance
            return SearchHelper.MatchesPackageSearch(packageName, _searchTerms);
        }

        /// <summary>
        /// Matches filters using current instance state. Delegates to the FilterState-based overload.
        /// </summary>
        public bool MatchesFilters(VarMetadata metadata, string packageName = null)
        {
            return MatchesFilters(metadata, GetSnapshot(), packageName);
        }

        public FilterState GetSnapshot()
        {
            return new FilterState
            {
                SearchText = SearchText,
                SearchTerms = _searchTerms,
                HideArchivedPackages = HideArchivedPackages,
                SelectedStatus = SelectedStatus,
                SelectedStatuses = new HashSet<string>(SelectedStatuses, StringComparer.OrdinalIgnoreCase),
                SelectedFavoriteStatuses = new HashSet<string>(SelectedFavoriteStatuses, StringComparer.OrdinalIgnoreCase),
                SelectedAutoInstallStatuses = new HashSet<string>(SelectedAutoInstallStatuses, StringComparer.OrdinalIgnoreCase),
                SelectedOptimizationStatuses = new HashSet<string>(SelectedOptimizationStatuses, StringComparer.OrdinalIgnoreCase),
                SelectedVersionStatuses = new HashSet<string>(SelectedVersionStatuses, StringComparer.OrdinalIgnoreCase),
                SelectedCategory = SelectedCategory,
                SelectedCategories = new HashSet<string>(SelectedCategories, StringComparer.OrdinalIgnoreCase),
                SelectedCreator = SelectedCreator,
                SelectedCreators = new HashSet<string>(SelectedCreators, StringComparer.OrdinalIgnoreCase),
                SelectedLicenseType = SelectedLicenseType,
                SelectedLicenseTypes = new HashSet<string>(SelectedLicenseTypes, StringComparer.OrdinalIgnoreCase),
                SelectedFileSizeRanges = new HashSet<string>(SelectedFileSizeRanges, StringComparer.OrdinalIgnoreCase),
                SelectedSubfolders = new HashSet<string>(SelectedSubfolders, StringComparer.OrdinalIgnoreCase),
                SelectedDamagedFilter = SelectedDamagedFilter,
                FilterDuplicates = FilterDuplicates,
                FilterNoDependents = FilterNoDependents,
                FilterNoDependencies = FilterNoDependencies,
                DateFilter = new DateFilter 
                { 
                    FilterType = DateFilter.FilterType,
                    CustomStartDate = DateFilter.CustomStartDate,
                    CustomEndDate = DateFilter.CustomEndDate
                },
                FavoritesManager = FavoritesManager,
                AutoInstallManager = AutoInstallManager,
                FileSizeTinyMax = FileSizeTinyMax,
                FileSizeSmallMax = FileSizeSmallMax,
                FileSizeMediumMax = FileSizeMediumMax,
                SelectedClothingTags = new HashSet<string>(SelectedClothingTags, StringComparer.OrdinalIgnoreCase),
                SelectedHairTags = new HashSet<string>(SelectedHairTags, StringComparer.OrdinalIgnoreCase),
                RequireAllTags = RequireAllTags,
                SelectedDestinations = new HashSet<string>(SelectedDestinations, StringComparer.OrdinalIgnoreCase)
            };
        }

        /// <summary>
        /// Resolves the package name from metadata, using provided name if available
        /// </summary>
        private static string ResolvePackageName(VarMetadata metadata, string providedName = null)
        {
            if (!string.IsNullOrEmpty(providedName))
                return providedName;
            if (!string.IsNullOrEmpty(metadata.PackageName))
                return metadata.PackageName;
            return Path.GetFileNameWithoutExtension(metadata.Filename);
        }

        public bool MatchesFilters(VarMetadata metadata, FilterState state, string packageName = null)
        {
            if (metadata == null)
                return false;

            // Resolve package name once for all filters that need it
            packageName = ResolvePackageName(metadata, packageName);

            // 1. Search text filter (most restrictive, check first)
            if (!string.IsNullOrEmpty(state.SearchText))
            {
                if (!SearchHelper.MatchesPackageSearch(packageName, state.SearchTerms))
                    return false;
            }

            // 2. Hide archived packages (fast boolean check)
            if (state.HideArchivedPackages)
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
                string pathToCheck = !string.IsNullOrEmpty(metadata.FilePath) ? metadata.FilePath : metadata.Filename;
                if (!string.IsNullOrEmpty(pathToCheck))
                {
                    if (pathToCheck.IndexOf("\\archive\\", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                    
                    if (pathToCheck.IndexOf("\\ArchivedPackages\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (metadata.Filename != null && metadata.Filename.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                }
            }

            // 3. Status filter (HashSet lookup O(1))
            // IMPORTANT: External packages have Status set to their destination name (e.g., "Backup"),
            // not standard statuses like "Loaded"/"Available". Skip status filtering for external packages
            // unless a destination filter is explicitly selected.
            bool isExternalPackage = metadata.IsExternal && !string.IsNullOrEmpty(metadata.ExternalDestinationName);
            
            if (!isExternalPackage)
            {
                if (!string.IsNullOrEmpty(state.SelectedStatus) && metadata.Status != state.SelectedStatus)
                    return false;
                
                if (state.SelectedStatuses.Count > 0 && !state.SelectedStatuses.Contains(metadata.Status))
                    return false;
            }

            // 4. Optimization status filter
            if (state.SelectedOptimizationStatuses.Count > 0)
            {
                if (metadata.Status == "Archived")
                    return false;
                
                bool matchesOptimization = false;
                foreach (var optStatus in state.SelectedOptimizationStatuses)
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

            // 5. Version status filter
            if (state.SelectedVersionStatuses.Count > 0)
            {
                bool matchesVersion = false;
                foreach (var verStatus in state.SelectedVersionStatuses)
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

            // 6. Favorites filter
            if (state.SelectedFavoriteStatuses.Count > 0 && state.FavoritesManager != null)
            {
                if (!state.FavoritesManager.IsFavorite(packageName))
                    return false;
            }

            // 7. AutoInstall filter
            if (state.SelectedAutoInstallStatuses.Count > 0 && state.AutoInstallManager != null)
            {
                if (!state.AutoInstallManager.IsAutoInstall(packageName))
                    return false;
            }

            // 8. Category filter
            if (!string.IsNullOrEmpty(state.SelectedCategory))
            {
                if (metadata.Categories == null || !metadata.Categories.Contains(state.SelectedCategory))
                    return false;
            }
            
            if (state.SelectedCategories.Count > 0)
            {
                // Use Any + Contains to leverage SelectedCategories' case-insensitive comparer
                if (metadata.Categories == null || !metadata.Categories.Any(c => state.SelectedCategories.Contains(c)))
                    return false;
            }

            // 9. Creator filter
            if (!string.IsNullOrEmpty(state.SelectedCreator) && 
                !string.Equals(metadata.CreatorName, state.SelectedCreator, StringComparison.OrdinalIgnoreCase))
                return false;
                
            if (state.SelectedCreators.Count > 0 && 
                !state.SelectedCreators.Contains(metadata.CreatorName))
                return false;

            // 10. License filter
            if (!string.IsNullOrEmpty(state.SelectedLicenseType) && metadata.LicenseType != state.SelectedLicenseType)
                return false;
            
            if (state.SelectedLicenseTypes.Count > 0)
            {
                var license = string.IsNullOrEmpty(metadata.LicenseType) ? "Unknown" : metadata.LicenseType;
                if (!state.SelectedLicenseTypes.Contains(license))
                    return false;
            }

            // 11. Duplicate filter
            if (state.FilterDuplicates && metadata.DuplicateLocationCount <= 1)
                return false;

            // 12. No Dependents filter (packages that nothing depends on)
            if (state.FilterNoDependents && metadata.DependentsCount > 0)
                return false;

            // 13. No Dependencies filter (packages that don't require other packages)
            if (state.FilterNoDependencies && metadata.DependencyCount > 0)
                return false;

            // 14. Date filter
            if (state.DateFilter.FilterType != DateFilterType.AllTime)
            {
                var dateToCheck = metadata.ModifiedDate ?? metadata.CreatedDate;
                if (!state.DateFilter.MatchesFilter(dateToCheck))
                    return false;
            }

            // 15. File size filter
            if (state.SelectedFileSizeRanges.Count > 0)
            {
                if (!MatchesFileSizeFilter(metadata.FileSize, state))
                    return false;
            }

            // 16. Subfolders filter
            if (state.SelectedSubfolders.Count > 0)
            {
                var subfolder = ExtractSubfolderFromMetadata(metadata);
                if (string.IsNullOrEmpty(subfolder) || !state.SelectedSubfolders.Contains(subfolder))
                    return false;
            }

            // 17. Damaged filter
            if (!string.IsNullOrEmpty(state.SelectedDamagedFilter))
            {
                if (state.SelectedDamagedFilter.Contains("Damaged"))
                {
                    if (!metadata.IsDamaged)
                        return false;
                }
                else if (state.SelectedDamagedFilter.Contains("Valid"))
                {
                    if (metadata.IsDamaged)
                        return false;
                }
            }

            // 18. Clothing tag filter
            if (state.SelectedClothingTags.Count > 0)
            {
                if (!MatchesTagFilter(metadata.ClothingTags, state.SelectedClothingTags, state.RequireAllTags))
                    return false;
            }

            // 19. Hair tag filter
            if (state.SelectedHairTags.Count > 0)
            {
                if (!MatchesTagFilter(metadata.HairTags, state.SelectedHairTags, state.RequireAllTags))
                    return false;
            }

            // 20. External destination filter
            if (state.SelectedDestinations.Count > 0)
            {
                if (!metadata.IsExternal || string.IsNullOrEmpty(metadata.ExternalDestinationName))
                    return false;
                
                // Build the full destination key including subfolder if present
                string packageDestKey = metadata.ExternalDestinationName;
                if (!string.IsNullOrEmpty(metadata.ExternalDestinationSubfolder))
                {
                    packageDestKey = $"{metadata.ExternalDestinationName}/{metadata.ExternalDestinationSubfolder}";
                }
                
                // Check if this package's destination matches any selected destination
                if (!state.SelectedDestinations.Contains(packageDestKey))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if item tags match the filter tags
        /// </summary>
        /// <param name="itemTags">Tags on the item (from metadata)</param>
        /// <param name="filterTags">Tags to filter by</param>
        /// <param name="requireAll">If true, all filter tags must match (AND). If false, any tag matches (OR).</param>
        /// <returns>True if the item matches the filter</returns>
        private static bool MatchesTagFilter(HashSet<string> itemTags, HashSet<string> filterTags, bool requireAll)
        {
            if (filterTags == null || filterTags.Count == 0)
                return true;
            
            if (itemTags == null || itemTags.Count == 0)
                return false;
            
            if (requireAll)
            {
                // AND logic: all filter tags must be present
                foreach (var filterTag in filterTags)
                {
                    if (!itemTags.Contains(filterTag))
                        return false;
                }
                return true;
            }
            else
            {
                // OR logic: any filter tag matches
                foreach (var filterTag in filterTags)
                {
                    if (itemTags.Contains(filterTag))
                        return true;
                }
                return false;
            }
        }

        private bool MatchesFileSizeFilter(long fileSizeBytes, FilterState state)
        {
            if (state.SelectedFileSizeRanges.Count == 0)
                return true;

            // Convert bytes to MB for comparison
            double fileSizeMB = fileSizeBytes / (1024.0 * 1024.0);

            foreach (var range in state.SelectedFileSizeRanges)
            {
                // Extract the range name without the count (e.g., "Tiny (5)" -> "Tiny")
                var rangeName = range.Split('(')[0].Trim();
                
                if (rangeName == "Tiny" && fileSizeMB < state.FileSizeTinyMax)
                    return true;
                if (rangeName == "Small" && fileSizeMB >= state.FileSizeTinyMax && fileSizeMB < state.FileSizeSmallMax)
                    return true;
                if (rangeName == "Medium" && fileSizeMB >= state.FileSizeSmallMax && fileSizeMB < state.FileSizeMediumMax)
                    return true;
                if (rangeName == "Large" && fileSizeMB >= state.FileSizeMediumMax)
                    return true;
            }

            return false;
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

            // Count unique duplicate packages by tracking base package names
            // A package is a duplicate if DuplicateLocationCount > 1
            // We only count each unique package once, not each instance
            var uniqueDuplicatePackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var kvp in packages)
            {
                var package = kvp.Value;
                if (package != null && package.DuplicateLocationCount > 1)
                {
                    // Extract base package name (without version/variant suffixes)
                    string basePackageName = kvp.Key;
                    
                    // Remove archived suffix if present
                    if (basePackageName.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
                    {
                        basePackageName = basePackageName.Substring(0, basePackageName.Length - 9);
                    }
                    
                    // Remove variant suffixes (e.g., #1, #2, etc.)
                    int hashIndex = basePackageName.LastIndexOf('#');
                    if (hashIndex > 0)
                    {
                        basePackageName = basePackageName.Substring(0, hashIndex);
                    }
                    
                    uniqueDuplicatePackages.Add(basePackageName);
                }
            }

            return uniqueDuplicatePackages.Count;
        }

        public Dictionary<string, int> GetCategoryCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
            // The packages dictionary is not modified during iteration
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
            
            // Add duplicate count
            int duplicateCount = GetDuplicateCount(packages);
            if (duplicateCount > 0)
            {
                counts["Duplicate"] = duplicateCount;
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
            
            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
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
            
            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
            foreach (var package in packages.Values)
            {
                if (package.IsOldVersion)
                    counts["Old"]++;
                else
                    counts["Latest"]++;
            }
            
            return counts;
        }

        /// <summary>
        /// Get dependency status counts (No Dependents / No Dependencies)
        /// </summary>
        public Dictionary<string, int> GetDependencyStatusCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>
            {
                ["No Dependents"] = 0,
                ["No Dependencies"] = 0
            };
            
            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
            foreach (var package in packages.Values)
            {
                if (package.DependentsCount == 0)
                    counts["No Dependents"]++;
                if (package.DependencyCount == 0)
                    counts["No Dependencies"]++;
            }
            
            return counts;
        }

        public Dictionary<string, int> GetLicenseCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
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
        /// Check if a file size matches any of the selected size ranges.
        /// Delegates to the FilterState-based overload for consistency.
        /// </summary>
        private bool MatchesFileSizeFilter(long fileSizeBytes) 
            => MatchesFileSizeFilter(fileSizeBytes, GetSnapshot());

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

            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
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

            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
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
        public string GetPackageSubfolder(VarMetadata metadata)
        {
            return ExtractSubfolderFromMetadata(metadata);
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

            // Status filter - O(1) using HashSet instead of O(n) List.Contains()
            if (filters.TryGetValue("Status", out var statusFilter) && statusFilter is List<string> selectedStatuses)
            {
                if (selectedStatuses.Count > 0)
                {
                    var statusSet = new HashSet<string>(selectedStatuses, StringComparer.OrdinalIgnoreCase);
                    if (!statusSet.Contains(package.Status))
                    {
                        return false;
                    }
                }
            }

            if (filters.TryGetValue("Duplicate", out var duplicatesFilter) && duplicatesFilter is bool filterDuplicates && filterDuplicates)
            {
                if (!package.IsDuplicate)
                {
                    return false;
                }
            }

            // Creator filter - O(1) using HashSet instead of O(n) Any()
            if (filters.TryGetValue("Creator", out var creatorFilter) && creatorFilter is List<string> selectedCreators)
            {
                if (selectedCreators.Count > 0)
                {
                    var creatorSet = new HashSet<string>(selectedCreators, StringComparer.OrdinalIgnoreCase);
                    if (!creatorSet.Contains(package.Creator))
                    {
                        return false;
                    }
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

            // Status filter - O(1) using HashSet instead of O(n) List.Contains()
            if (filters.TryGetValue("Status", out var statusFilter) && statusFilter is List<string> selectedStatuses)
            {
                if (selectedStatuses.Count > 0)
                {
                    var statusSet = new HashSet<string>(selectedStatuses, StringComparer.OrdinalIgnoreCase);
                    if (!statusSet.Contains(metadata.Status))
                    {
                        return false;
                    }
                }
            }

            if (filters.TryGetValue("Duplicate", out var duplicatesFilter) && duplicatesFilter is bool filterDuplicates && filterDuplicates)
            {
                if (!metadata.IsDuplicate)
                {
                    return false;
                }
            }

            // Creator filter - O(1) using HashSet instead of O(n) Any()
            if (filters.TryGetValue("Creator", out var creatorFilter) && creatorFilter is List<string> selectedCreators)
            {
                if (selectedCreators.Count > 0)
                {
                    var creatorSet = new HashSet<string>(selectedCreators, StringComparer.OrdinalIgnoreCase);
                    if (!creatorSet.Contains(creator))
                    {
                        return false;
                    }
                }
            }

            // Content type filter - O(n) using HashSet instead of O(n*m) nested Any()
            if (filters.TryGetValue("ContentType", out var contentTypeFilter) && contentTypeFilter is List<string> selectedTypes)
            {
                if (selectedTypes.Count > 0)
                {
                    var packageCategories = metadata.Categories ?? new HashSet<string>();
                    // Convert to HashSet for O(1) lookups instead of O(n) Any() calls
                    var selectedTypesSet = new HashSet<string>(selectedTypes, StringComparer.OrdinalIgnoreCase);
                    bool hasMatchingCategory = packageCategories.Any(category => selectedTypesSet.Contains(category));
                    
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

        /// <summary>
        /// Get clothing tag counts from packages
        /// Returns a dictionary of tag -> count of packages with that tag
        /// </summary>
        public Dictionary<string, int> GetClothingTagCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
            foreach (var package in packages.Values)
            {
                if (package.ClothingTags == null || package.ClothingTags.Count == 0)
                    continue;
                
                foreach (var tag in package.ClothingTags)
                {
                    if (string.IsNullOrEmpty(tag))
                        continue;
                    
                    if (counts.TryGetValue(tag, out var count))
                        counts[tag] = count + 1;
                    else
                        counts[tag] = 1;
                }
            }
            
            return counts;
        }

        /// <summary>
        /// Get hair tag counts from packages
        /// Returns a dictionary of tag -> count of packages with that tag
        /// </summary>
        public Dictionary<string, int> GetHairTagCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
            foreach (var package in packages.Values)
            {
                if (package.HairTags == null || package.HairTags.Count == 0)
                    continue;
                
                foreach (var tag in package.HairTags)
                {
                    if (string.IsNullOrEmpty(tag))
                        continue;
                    
                    if (counts.TryGetValue(tag, out var count))
                        counts[tag] = count + 1;
                    else
                        counts[tag] = 1;
                }
            }
            
            return counts;
        }

        /// <summary>
        /// Get count of packages that have any clothing tags
        /// </summary>
        public int GetPackagesWithClothingTagsCount(Dictionary<string, VarMetadata> packages)
        {
            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
            int count = 0;
            foreach (var package in packages.Values)
            {
                if (package.ClothingTags != null && package.ClothingTags.Count > 0)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Get count of packages that have any hair tags
        /// </summary>
        public int GetPackagesWithHairTagsCount(Dictionary<string, VarMetadata> packages)
        {
            // MEMORY FIX: Iterate directly instead of creating a copy with ToList()
            int count = 0;
            foreach (var package in packages.Values)
            {
                if (package.HairTags != null && package.HairTags.Count > 0)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Check if any tag filters are active
        /// </summary>
        public bool HasActiveTagFilters()
        {
            return SelectedClothingTags.Count > 0 || SelectedHairTags.Count > 0;
        }

        /// <summary>
        /// Get external destination counts from packages.
        /// If a package has a subfolder, it's counted under "DestinationName/Subfolder".
        /// If a package is in the root of the destination, it's counted under "DestinationName".
        /// </summary>
        public Dictionary<string, int> GetDestinationCounts(Dictionary<string, VarMetadata> packages)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var package in packages.Values)
            {
                if (!package.IsExternal || string.IsNullOrEmpty(package.ExternalDestinationName))
                    continue;
                
                // Create a key that includes the subfolder if present
                string key = package.ExternalDestinationName;
                if (!string.IsNullOrEmpty(package.ExternalDestinationSubfolder))
                {
                    key = $"{package.ExternalDestinationName}/{package.ExternalDestinationSubfolder}";
                }
                
                if (counts.TryGetValue(key, out var count))
                    counts[key] = count + 1;
                else
                    counts[key] = 1;
            }
            
            return counts;
        }

        /// <summary>
        /// Check if any destination filters are active
        /// </summary>
        public bool HasActiveDestinationFilters()
        {
            return SelectedDestinations.Count > 0;
        }
    }
}
