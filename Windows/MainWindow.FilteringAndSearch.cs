using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VPM.Models;
using static VPM.Models.PackageItem;

namespace VPM
{
    /// <summary>
    /// Filtering and search functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        #region Filter Application

        private void ApplyFilters()
        {
            if (_filterManager == null || _packageManager == null) return;

            try
            {
                // Don't reload if no packages loaded yet
                if (_packageManager.PackageMetadata == null || _packageManager.PackageMetadata.Count == 0)
                {
                    return;
                }

                // Update FilterManager properties with current UI selections
                UpdateFilterManagerFromUI();
                
                // Invalidate reactive filter cache so counts will be recalculated
                if (_reactiveFilterManager != null)
                {
                    _reactiveFilterManager.InvalidateCounts();
                }
                
                // Apply cascade filtering if enabled
                if (_cascadeFiltering)
                {
                    var currentFilters = GetSelectedFilters();
                    UpdateCascadeFilteringLive(currentFilters);

                    // Update filter manager again after cascade filtering has restored selections
                    // This ensures the restored selections are properly captured
                    UpdateFilterManagerFromUI();
                }
                else
                {
                    // In non-linked mode, update filter counts live without full refresh
                    UpdateFilterCountsLive();
                }
                
                // Trigger package list reload with new filters applied in background
                // Don't refresh filter lists - they're already updated live above
                UpdatePackageListAsync(refreshFilterLists: false);
                
                // Reapply sorting after filtering to maintain sort order
                ReapplySorting();
            }
            catch (Exception)
            {
            }
        }

        private string SummarizeFilterValue(object value)
        {
            return value switch
            {
                List<string> stringList => stringList.Count == 0 ? "(empty list)" : string.Join(", ", stringList),
                DateFilter df => df.FilterType == DateFilterType.CustomRange
                    ? FormatCustomDateRange(df)
                    : df.FilterType.ToString(),
                null => "(null)",
                _ => value?.ToString() ?? "(null)"
            };
        }

        private string FormatCustomDateRange(DateFilter dateFilter)
        {
            var (start, end) = dateFilter.GetDateRange();

            if (start.HasValue && end.HasValue)
            {
                return $"CustomRange {start.Value:yyyy-MM-dd}..{end.Value:yyyy-MM-dd}";
            }

            if (start.HasValue)
            {
                return $"CustomRange from {start.Value:yyyy-MM-dd}";
            }

            if (end.HasValue)
            {
                return $"CustomRange until {end.Value:yyyy-MM-dd}";
            }

            return "CustomRange (no bounds)";
        }

        private void UpdateFilterManagerFromUI()
        {
            try
            {
                // Clear existing filters
                _filterManager.SelectedStatuses.Clear();
                _filterManager.SelectedFavoriteStatuses.Clear();
                _filterManager.SelectedAutoInstallStatuses.Clear();
                _filterManager.SelectedOptimizationStatuses.Clear();
                _filterManager.SelectedVersionStatuses.Clear();
                _filterManager.SelectedCreators.Clear();
                _filterManager.SelectedCategories.Clear();
                _filterManager.SelectedLicenseTypes.Clear();
                _filterManager.SelectedFileSizeRanges.Clear();
                _filterManager.SelectedSubfolders.Clear();
                _filterManager.SelectedDamagedFilter = null;
                
                // Update status filters (includes regular status, optimization status, version status, and favorites)
                _filterManager.FilterDuplicates = false;
                if (StatusFilterList?.SelectedItems != null && StatusFilterList.SelectedItems.Count > 0)
                {
                    var seenStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in StatusFilterList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            // Extract status from "Status (count)" format
                            var status = itemText.Split('(')[0].Trim();
                        
                        if (status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) || status.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                        {
                            _filterManager.FilterDuplicates = true;
                            continue;
                        }

                            if (seenStatuses.Contains(status))
                            {
                                continue;
                            }

                            // Check if this is a favorites status
                            if (status == "Favorites" || status == "Non-Favorites")
                            {
                                _filterManager.SelectedFavoriteStatuses.Add(status);
                            }
                            // Check if this is an autoinstall status
                            else if (status == "AutoInstall")
                            {
                                _filterManager.SelectedAutoInstallStatuses.Add(status);
                            }
                            // Check if this is an optimization status
                            else if (status == "Optimized" || status == "Unoptimized")
                            {
                                _filterManager.SelectedOptimizationStatuses.Add(status);
                            }
                            // Check if this is a version status
                            else if (status == "Latest" || status == "Old")
                            {
                                _filterManager.SelectedVersionStatuses.Add(status);
                            }
                            else
                            {
                                _filterManager.SelectedStatuses.Add(status);
                                seenStatuses.Add(status);
                            }
                        }
                    }
                }

                // Update creator filters
                if (CreatorsList?.SelectedItems != null && CreatorsList.SelectedItems.Count > 0)
                {
                    foreach (var item in CreatorsList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var creator = itemText.Split('(')[0].Trim();
                            _filterManager.SelectedCreators.Add(creator);
                        }
                    }
                }

                // Update content type filters
                if (ContentTypesList?.SelectedItems != null && ContentTypesList.SelectedItems.Count > 0)
                {
                    foreach (var item in ContentTypesList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var contentType = itemText.Split('(')[0].Trim();
                            _filterManager.SelectedCategories.Add(contentType);
                        }
                    }
                }

                // Update license type filters
                if (LicenseTypeList?.SelectedItems != null && LicenseTypeList.SelectedItems.Count > 0)
                {
                    foreach (var item in LicenseTypeList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var licenseType = itemText.Split('(')[0].Trim();
                            _filterManager.SelectedLicenseTypes.Add(licenseType);
                        }
                    }
                }

                // Update file size filters
                if (FileSizeFilterList?.SelectedItems != null && FileSizeFilterList.SelectedItems.Count > 0)
                {
                    foreach (var item in FileSizeFilterList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            _filterManager.SelectedFileSizeRanges.Add(itemText);
                        }
                    }
                }

                // Update subfolders filters
                if (SubfoldersFilterList?.SelectedItems != null && SubfoldersFilterList.SelectedItems.Count > 0)
                {
                    foreach (var item in SubfoldersFilterList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            // Extract subfolder name from "Subfolder (count)" format
                            var subfolder = itemText.Split('(')[0].Trim();
                            _filterManager.SelectedSubfolders.Add(subfolder);
                        }
                    }
                }

                // Update damaged filter
                if (DamagedFilterList?.SelectedItem != null)
                {
                    var selectedItem = DamagedFilterList.SelectedItem.ToString();
                    if (!string.IsNullOrEmpty(selectedItem) && !selectedItem.StartsWith("All Packages"))
                    {
                        _filterManager.SelectedDamagedFilter = selectedItem;
                    }
                }

                // Update search text filter
                var searchText = GetSearchText(PackageSearchBox);
                _filterManager.SetSearchText(searchText);

                // Date filter is already handled by the FilterManager.DateFilter property
                // which is updated directly by the date filter UI events
            }
            catch (Exception)
            {
            }
        }

        private Dictionary<string, object> GetSelectedFilters()
        {
            var filters = new Dictionary<string, object>();

            try
            {
                // Status filters
                if (StatusFilterList?.SelectedItems != null && StatusFilterList.SelectedItems.Count > 0)
                {
                    var selectedStatuses = new List<string>();
                    bool duplicatesSelected = false;
                    foreach (var item in StatusFilterList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            // Extract status from "Status (count)" format - no emojis to handle
                            var status = itemText.Split('(')[0].Trim();
                            if (status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) || status.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                            {
                                duplicatesSelected = true;
                            }
                            else
                            {
                                selectedStatuses.Add(status);
                            }
                        }
                    }
                    
                    if (selectedStatuses.Count > 0)
                    {
                        filters["Status"] = selectedStatuses;
                    }

                    if (duplicatesSelected)
                    {
                        filters["Duplicate"] = true;
                    }
                }

                // Creator filters
                if (CreatorsList?.SelectedItems != null && CreatorsList.SelectedItems.Count > 0)
                {
                    var selectedCreators = new List<string>();
                    foreach (var item in CreatorsList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var creator = itemText.Split('(')[0].Trim();
                            selectedCreators.Add(creator);
                        }
                    }
                    
                    if (selectedCreators.Count > 0)
                    {
                        filters["Creator"] = selectedCreators;
                    }
                }

                // Content type filters
                if (ContentTypesList?.SelectedItems != null && ContentTypesList.SelectedItems.Count > 0)
                {
                    var selectedTypes = new List<string>();
                    foreach (var item in ContentTypesList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var contentType = itemText.Split('(')[0].Trim();
                            selectedTypes.Add(contentType);
                        }
                    }
                    
                    if (selectedTypes.Count > 0)
                    {
                        filters["ContentType"] = selectedTypes;
                    }
                }

                // License type filters
                if (LicenseTypeList?.SelectedItems != null && LicenseTypeList.SelectedItems.Count > 0)
                {
                    var selectedLicenseTypes = new List<string>();
                    foreach (var item in LicenseTypeList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var licenseType = itemText.Split('(')[0].Trim();
                            selectedLicenseTypes.Add(licenseType);
                        }
                    }
                    
                    if (selectedLicenseTypes.Count > 0)
                    {
                        filters["LicenseType"] = selectedLicenseTypes;
                    }
                }

                // File size filters
                if (FileSizeFilterList?.SelectedItems != null && FileSizeFilterList.SelectedItems.Count > 0)
                {
                    var selectedFileSizeRanges = new List<string>();
                    foreach (var item in FileSizeFilterList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            selectedFileSizeRanges.Add(itemText);
                        }
                    }
                    
                    if (selectedFileSizeRanges.Count > 0)
                    {
                        filters["FileSizeRange"] = selectedFileSizeRanges;
                    }
                }

                // Subfolders filters
                if (SubfoldersFilterList?.SelectedItems != null && SubfoldersFilterList.SelectedItems.Count > 0)
                {
                    var selectedSubfolders = new List<string>();
                    foreach (var item in SubfoldersFilterList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            // Extract subfolder name from "Subfolder (count)" format
                            var subfolder = itemText.Split('(')[0].Trim();
                            selectedSubfolders.Add(subfolder);
                        }
                    }
                    
                    if (selectedSubfolders.Count > 0)
                    {
                        filters["Subfolders"] = selectedSubfolders;
                    }
                }

                // Date filter
                if (_filterManager?.DateFilter != null && _filterManager.DateFilter.FilterType != DateFilterType.AllTime)
                {
                    filters["DateFilter"] = _filterManager.DateFilter;
                }
            }
            catch (Exception)
            {
            }

            return filters;
        }

        #endregion

        #region Filter Methods

        private void FilterPackages(string filterText = "")
        {
            ApplyFilters();
        }

        private void FilterDependencies(string filterText = "")
        {
            if (Dependencies == null || _originalDependencies == null) return;

            try
            {
                Dependencies.Clear();
                
                if (string.IsNullOrWhiteSpace(filterText))
                {
                    // Show all dependencies
                    foreach (var dep in _originalDependencies)
                    {
                        Dependencies.Add(dep);
                    }
                }
                else
                {
                    // Prepare search terms
                    var searchTerms = VPM.Services.SearchHelper.PrepareSearchTerms(filterText);

                    // Filter dependencies by text - using MatchesAllTerms for multi-term matching
                    foreach (var dep in _originalDependencies)
                    {
                        if (VPM.Services.SearchHelper.MatchesAllTerms(dep.Name, searchTerms))
                        {
                            Dependencies.Add(dep);
                        }
                    }
                }
                
                // Reapply dependencies sorting after filtering
                var depsState = _sortingManager?.GetSortingState("Dependencies");
                if (depsState?.CurrentSortOption is DependencySortOption depsSort)
                {
                    ReapplyDependenciesSortingInternal(depsSort, depsState.IsAscending);
                }
                
                // Update toolbar buttons after filtering dependencies
                UpdateToolbarButtons();
            }
            catch (Exception)
            {
            }
        }

        private void FilterCreators(string filterText = "")
        {
            // Filter the creators list by text
            FilterCreatorsList(filterText);
        }

        #endregion

        #region Clear Button Methods

        private void UpdateClearButtonVisibility()
        {
            UpdatePackageSearchClearButton();
            UpdateDepsSearchClearButton();
            UpdateCreatorsClearButton();
            UpdateContentTypesClearButton();
            UpdateLicenseTypeClearButton();
            UpdateSubfoldersClearButton();
        }

        private void UpdatePackageSearchClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (PackageSearchClearButton != null && PackageSearchBox != null && PackageDataGrid != null)
                {
                    bool hasText = !PackageSearchBox.Foreground.Equals(grayBrush) && !string.IsNullOrWhiteSpace(PackageSearchBox.Text);
                    bool hasSelection = PackageDataGrid.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    PackageSearchClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateDepsSearchClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (DepsSearchClearButton != null && DepsSearchBox != null && DependenciesDataGrid != null)
                {
                    bool hasText = !DepsSearchBox.Foreground.Equals(grayBrush) && !string.IsNullOrWhiteSpace(DepsSearchBox.Text);
                    bool hasSelection = DependenciesDataGrid.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    DepsSearchClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateCreatorsClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (CreatorsClearButton != null && CreatorsFilterBox != null && CreatorsList != null)
                {
                    bool hasText = !CreatorsFilterBox.Foreground.Equals(grayBrush) && !string.IsNullOrWhiteSpace(CreatorsFilterBox.Text);
                    bool hasSelection = CreatorsList.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    CreatorsClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateContentTypesClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (ContentTypesClearButton != null && ContentTypesFilterBox != null && ContentTypesList != null)
                {
                    bool hasText = !ContentTypesFilterBox.Foreground.Equals(grayBrush) && !string.IsNullOrWhiteSpace(ContentTypesFilterBox.Text);
                    bool hasSelection = ContentTypesList.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    ContentTypesClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateLicenseTypeClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (LicenseTypeClearButton != null && LicenseTypeFilterBox != null && LicenseTypeList != null)
                {
                    bool hasText = !LicenseTypeFilterBox.Foreground.Equals(grayBrush) && !string.IsNullOrWhiteSpace(LicenseTypeFilterBox.Text);
                    bool hasSelection = LicenseTypeList.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    LicenseTypeClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateSubfoldersClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (SubfoldersClearButton != null && SubfoldersFilterBox != null && SubfoldersFilterList != null)
                {
                    bool hasText = !SubfoldersFilterBox.Foreground.Equals(grayBrush) && !string.IsNullOrWhiteSpace(SubfoldersFilterBox.Text);
                    bool hasSelection = SubfoldersFilterList.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    SubfoldersClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Initialization Methods

        private void InitializeSearchBoxes()
        {
            if (!this.IsLoaded) return;

            try
            {
                // Initialize package search box
                if (PackageSearchBox != null)
                {
                    PackageSearchBox.Text = "Search packages...";
                    PackageSearchBox.Foreground = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                }

                // Initialize dependencies search box
                if (DepsSearchBox != null)
                {
                    DepsSearchBox.Text = "Search dependencies...";
                    DepsSearchBox.Foreground = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                }

                // Initialize creators filter box
                if (CreatorsFilterBox != null)
                {
                    CreatorsFilterBox.Text = "😣 Filter creators...";
                    CreatorsFilterBox.Foreground = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                }

            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Search Text Helpers

        private string GetSearchText(TextBox searchBox)
        {
            if (searchBox == null) return "";

            try
            {
                var text = searchBox.Text ?? "";
                
                // List of known placeholder texts to ignore
                var placeholders = new[] 
                { 
                    "search...", 
                    "📦 Filter packages, descriptions, tags...",
                    "📝 Filter creators...",
                    "😣 Filter creators...",
                    "📝 Filter content types...",
                    "–ï¸ Filter license types..."
                };
                
                // Check if the text is a placeholder
                if (placeholders.Any(p => text.Equals(p, StringComparison.OrdinalIgnoreCase)))
                {
                    return ""; // Treat placeholder text as empty
                }
                
                // Check if the text is placeholder text (gray color)
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                if (searchBox.Foreground.Equals(grayBrush))
                {
                    return ""; // Treat placeholder text as empty
                }
                
                // Has content if text is not null or whitespace
                return !string.IsNullOrWhiteSpace(text) ? text : "";
            }
            catch
            {
                return !string.IsNullOrWhiteSpace(searchBox?.Text) ? searchBox.Text : "";
            }
        }

        #endregion

        #region Dependencies Refresh

        private void RefreshDependenciesForFilteredPackages()
        {
            try
            {
                var selectedCount = PackageDataGrid?.SelectedItems.Count ?? 0;

                // Only refresh dependencies if no packages are selected
                // (when packages are selected, their specific dependencies are shown)
                if (selectedCount == 0)
                {
                    // Clear dependencies when no packages are selected to prevent loading all deps
                    ClearDependenciesDisplay();
                }
            }
            catch (Exception)
            {
            }
        }

        private async void RefreshDependenciesAfterCascade()
        {
            try
            {
                // Small delay to ensure main table view has been updated after cascade filtering
                await Task.Delay(50);

                var selectedCount = PackageDataGrid?.SelectedItems.Count ?? 0;

                // Only refresh dependencies if no packages are selected
                // (when packages are selected, their specific dependencies are shown)
                if (selectedCount == 0)
                {
                    // Clear dependencies when no packages are selected to prevent loading all deps
                    ClearDependenciesDisplay();
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Cascade Filtering

        private void UpdateStatusListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveStatusFilter)
        {
            if (StatusFilterList == null) return;

            // Prevent infinite recursion by suppressing selection events
            _suppressSelectionEvents = true;
            try
            {
                // Store selected status names (without counts) before clearing
                var selectedStatuses = new List<string>();
                foreach (var item in StatusFilterList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        // Extract status name without count and normalize
                        var statusName = itemText.Split('(')[0].Trim();
                        if (statusName.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                        {
                            statusName = "Duplicate";
                        }
                        selectedStatuses.Add(statusName);
                    }
                }
                // Show all statuses with updated counts from filtered packages
                StatusFilterList.Items.Clear();
                var statusCounts = _filterManager.GetStatusCounts(filteredPackages);

                foreach (var status in statusCounts.Where(s => s.Value > 0).OrderBy(s => s.Key))
                {
                    var displayName = status.Key.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) ? "Duplicates" : status.Key;
                    var displayText = $"{displayName} ({status.Value})";
                    StatusFilterList.Items.Add(displayText);

                    // Restore selection if this status was previously selected
                    if (selectedStatuses.Contains(status.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }
                
                // Add optimization status counts
                var optCounts = _filterManager.GetOptimizationStatusCounts(filteredPackages);

                foreach (var opt in optCounts.Where(s => s.Value > 0).OrderBy(s => s.Key))
                {
                    var displayText = $"{opt.Key} ({opt.Value:N0})";
                    StatusFilterList.Items.Add(displayText);

                    // Restore selection if this optimization status was previously selected
                    if (selectedStatuses.Contains(opt.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }
                
                // Add version status counts (always show, even if count is 0)
                var versionCounts = _filterManager.GetVersionStatusCounts(filteredPackages);

                foreach (var ver in versionCounts.OrderBy(s => s.Key))
                {
                    var displayText = $"{ver.Key} ({ver.Value:N0})";
                    StatusFilterList.Items.Add(displayText);

                    // Restore selection if this version status was previously selected
                    if (selectedStatuses.Contains(ver.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }
                
                // Add favorites option
                if (_favoritesManager != null && _packageManager?.PackageMetadata != null)
                {
                    var favorites = _favoritesManager.GetAllFavorites();
                    int favoriteCount = 0;
                    
                    // Count from ALL packages, not filtered packages
                    foreach (var pkg in _packageManager.PackageMetadata.Values)
                    {
                        var pkgName = System.IO.Path.GetFileNameWithoutExtension(pkg.Filename);
                        if (favorites.Contains(pkgName))
                            favoriteCount++;
                    }
                    
                    var favText = $"Favorites ({favoriteCount:N0})";
                    StatusFilterList.Items.Add(favText);
                    
                    if (selectedStatuses.Contains("Favorites"))
                    {
                        StatusFilterList.SelectedItems.Add(favText);
                    }
                }

                // Add autoinstall option
                if (_autoInstallManager != null && _packageManager?.PackageMetadata != null)
                {
                    var autoInstall = _autoInstallManager.GetAllAutoInstall();
                    int autoInstallCount = 0;
                    
                    // Count from ALL packages, not filtered packages
                    foreach (var pkg in _packageManager.PackageMetadata.Values)
                    {
                        var pkgName = System.IO.Path.GetFileNameWithoutExtension(pkg.Filename);
                        if (autoInstall.Contains(pkgName))
                            autoInstallCount++;
                    }
                    
                    var autoInstallText = $"AutoInstall ({autoInstallCount:N0})";
                    StatusFilterList.Items.Add(autoInstallText);
                    
                    if (selectedStatuses.Contains("AutoInstall"))
                    {
                        StatusFilterList.SelectedItems.Add(autoInstallText);
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        private void UpdateCreatorsListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveCreatorFilter)
        {
            if (CreatorsList == null) return;

            // Prevent infinite recursion by suppressing selection events
            _suppressSelectionEvents = true;
            try
            {
                // Store selected creator names (without counts) before clearing
                var selectedCreators = new List<string>();
                foreach (var item in CreatorsList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        // Extract creator name without count
                        var creatorName = itemText.Split('(')[0].Trim();
                        selectedCreators.Add(creatorName);
                    }
                }

                // In cascade mode, always show only creators that have packages in the filtered set
                // This provides the "hiding" behavior that users expect
                CreatorsList.Items.Clear();
                var creatorCounts = _filterManager.GetCreatorCounts(filteredPackages);

                foreach (var creator in creatorCounts.Where(c => c.Value > 0).OrderBy(c => c.Key))
                {
                    var displayText = $"{creator.Key} ({creator.Value})";
                    CreatorsList.Items.Add(displayText);

                    // Restore selection if this creator was previously selected
                    if (selectedCreators.Contains(creator.Key))
                    {
                        CreatorsList.SelectedItems.Add(displayText);
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        private void UpdateContentTypesListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveContentTypeFilter)
        {
            if (ContentTypesList == null) return;

            // Prevent infinite recursion by suppressing selection events
            _suppressSelectionEvents = true;
            try
            {
                // Store selected content type names (without counts) before clearing
                var selectedContentTypes = new List<string>();
                foreach (var item in ContentTypesList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        // Extract content type name without count
                        var contentTypeName = itemText.Split('(')[0].Trim();
                        selectedContentTypes.Add(contentTypeName);
                    }
                }

                // In cascade mode, always show only content types that have packages in the filtered set
                // This provides the "hiding" behavior that users expect
                ContentTypesList.Items.Clear();
                var categoryCounts = _filterManager.GetCategoryCounts(filteredPackages);

                foreach (var category in categoryCounts.Where(c => c.Value > 0).OrderBy(c => c.Key))
                {
                    var displayText = $"{category.Key} ({category.Value})";
                    ContentTypesList.Items.Add(displayText);

                    // Restore selection if this content type was previously selected
                    if (selectedContentTypes.Contains(category.Key))
                    {
                        ContentTypesList.SelectedItems.Add(displayText);
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        private void UpdateLicenseTypesListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveLicenseTypeFilter)
        {
            if (LicenseTypeList == null) return;

            // Prevent infinite recursion by suppressing selection events
            _suppressSelectionEvents = true;
            try
            {
                // Store selected license type names (without counts) before clearing
                var selectedLicenseTypes = new List<string>();
                foreach (var item in LicenseTypeList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        // Extract license type name without count
                        var licenseTypeName = itemText.Split('(')[0].Trim();
                        selectedLicenseTypes.Add(licenseTypeName);
                    }
                }

                // In cascade mode, always show only license types that have packages in the filtered set
                // This provides the "hiding" behavior that users expect
                LicenseTypeList.Items.Clear();
                var licenseCounts = _filterManager.GetLicenseCounts(filteredPackages);

                foreach (var license in licenseCounts.Where(l => l.Value > 0).OrderBy(l => l.Key))
                {
                    var displayText = $"{license.Key} ({license.Value})";
                    LicenseTypeList.Items.Add(displayText);

                    // Restore selection if this license type was previously selected
                    if (selectedLicenseTypes.Contains(license.Key))
                    {
                        LicenseTypeList.SelectedItems.Add(displayText);
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        private void UpdateFileSizeFilterListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveFileSizeFilter)
        {
            if (FileSizeFilterList == null) return;

            // Prevent infinite recursion by suppressing selection events
            _suppressSelectionEvents = true;
            try
            {
                // Store selected file size ranges (without counts) before clearing
                var selectedFileSizeRanges = new List<string>();
                foreach (var item in FileSizeFilterList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        // Extract range name without count
                        var rangeName = itemText.Split('(')[0].Trim();
                        selectedFileSizeRanges.Add(rangeName);
                    }
                }

                // In cascade mode, show file size ranges with updated counts from filtered packages
                FileSizeFilterList.Items.Clear();
                var fileSizeCounts = _filterManager.GetFileSizeCounts(filteredPackages);

                // Add in specific order
                var orderedRanges = new[] { "Tiny", "Small", "Medium", "Large" };
                foreach (var range in orderedRanges)
                {
                    if (fileSizeCounts.ContainsKey(range) && fileSizeCounts[range] > 0)
                    {
                        var displayText = $"{range} ({fileSizeCounts[range]})";
                        FileSizeFilterList.Items.Add(displayText);

                        // Restore selection if this range was previously selected
                        if (selectedFileSizeRanges.Contains(range))
                        {
                            FileSizeFilterList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        private void UpdateSubfoldersFilterListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveSubfoldersFilter)
        {
            if (SubfoldersFilterList == null) return;

            // Prevent infinite recursion by suppressing selection events
            _suppressSelectionEvents = true;
            try
            {
                // Store selected subfolders (without counts) before clearing
                var selectedSubfolders = new List<string>();
                foreach (var item in SubfoldersFilterList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        // Extract subfolder name without count
                        var subfolderName = itemText.Split('(')[0].Trim();
                        selectedSubfolders.Add(subfolderName);
                    }
                }

                // In cascade mode, show subfolders with updated counts from filtered packages
                SubfoldersFilterList.Items.Clear();
                var subfolderCounts = _filterManager.GetSubfolderCounts(filteredPackages);

                // Sort subfolders alphabetically
                var sortedSubfolders = subfolderCounts.Keys.OrderBy(k => k).ToList();
                foreach (var subfolder in sortedSubfolders)
                {
                    if (subfolderCounts[subfolder] > 0)
                    {
                        var displayText = $"{subfolder} ({subfolderCounts[subfolder]})";
                        SubfoldersFilterList.Items.Add(displayText);

                        // Restore selection if this subfolder was previously selected
                        if (selectedSubfolders.Contains(subfolder))
                        {
                            SubfoldersFilterList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        private void UpdateDateFilterListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveDateFilter)
        {
            if (DateFilterList == null) return;

            // Prevent infinite recursion by suppressing selection events
            _suppressSelectionEvents = true;
            try
            {
                var selectedItems = new List<string>();
                foreach (var item in DateFilterList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        selectedItems.Add(itemText);
                    }
                }
                
                if (hasActiveDateFilter)
                {
                    // Hide non-selected items when date filter is active
                    var itemsToRemove = new List<string>();
                    foreach (string item in DateFilterList.Items)
                    {
                        if (!selectedItems.Contains(item))
                        {
                            itemsToRemove.Add(item);
                        }
                    }
                    
                    foreach (var item in itemsToRemove)
                    {
                        DateFilterList.Items.Remove(item);
                    }
                }
                else
                {
                    // Show all date filter options with counts from filtered packages
                    DateFilterList.Items.Clear();
                    var dateCounts = GetDateFilterCounts(filteredPackages);
                    
                    // Store current selection tag
                    var selectedTag = "";
                    foreach (var item in selectedItems)
                    {
                        // Extract tag from display text or use the item directly
                        var parts = item.Split('(');
                        var baseText = parts[0].Trim();
                        
                        selectedTag = baseText switch
                        {
                            "All Time" => "AllTime",
                            "Today" => "Today", 
                            "Past Week" => "PastWeek",
                            "Past Month" => "PastMonth",
                            "Past 3 Months" => "Past3Months",
                            "Past Year" => "PastYear",
                            "Custom Range..." => "CustomRange",
                            _ => selectedTag
                        };
                        
                        if (!string.IsNullOrEmpty(selectedTag)) break;
                    }
                    
                    // Add all date filter options
                    var dateOptions = new[]
                    {
                        new { Text = "All Time", Tag = "AllTime", Count = dateCounts["AllTime"] },
                        new { Text = "Today", Tag = "Today", Count = dateCounts["Today"] },
                        new { Text = "Past Week", Tag = "PastWeek", Count = dateCounts["PastWeek"] },
                        new { Text = "Past Month", Tag = "PastMonth", Count = dateCounts["PastMonth"] },
                        new { Text = "Past 3 Months", Tag = "Past3Months", Count = dateCounts["Past3Months"] },
                        new { Text = "Past Year", Tag = "PastYear", Count = dateCounts["PastYear"] },
                        new { Text = "Custom Range...", Tag = "CustomRange", Count = 0 }
                    };
                    
                    foreach (var option in dateOptions)
                    {
                        var displayText = option.Tag == "CustomRange" ? option.Text : $"{option.Text} ({option.Count})";
                        DateFilterList.Items.Add(displayText);
                        
                        // Restore selection
                        if (option.Tag == selectedTag)
                        {
                            DateFilterList.SelectedItem = displayText;
                        }
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }


        #endregion

        #region Filter Textbox Methods

        private void FilterContentTypesList(string filterText)
        {
            if (ContentTypesList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var selectedItems = ContentTypesList.SelectedItems.Cast<string>().ToList();
                ContentTypesList.Items.Clear();
                
                var categoryCounts = _filterManager.GetCategoryCounts(_packageManager.PackageMetadata);
                var searchTerms = VPM.Services.SearchHelper.PrepareSearchTerms(filterText);
                
                foreach (var category in categoryCounts.OrderBy(c => c.Key))
                {
                    if (VPM.Services.SearchHelper.MatchesAllTerms(category.Key, searchTerms))
                    {
                        var displayText = $"{category.Key} ({category.Value})";
                        ContentTypesList.Items.Add(displayText);
                        
                        // Restore selection
                        if (selectedItems.Any(item => item.StartsWith(category.Key)))
                        {
                            ContentTypesList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void FilterCreatorsList(string filterText)
        {
            if (CreatorsList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var selectedItems = CreatorsList.SelectedItems.Cast<string>().ToList();
                CreatorsList.Items.Clear();
                
                var creatorCounts = _filterManager.GetCreatorCounts(_packageManager.PackageMetadata);
                var searchTerms = VPM.Services.SearchHelper.PrepareSearchTerms(filterText);
                
                foreach (var creator in creatorCounts.OrderBy(c => c.Key))
                {
                    if (VPM.Services.SearchHelper.MatchesAllTerms(creator.Key, searchTerms))
                    {
                        var displayText = $"{creator.Key} ({creator.Value})";
                        CreatorsList.Items.Add(displayText);
                        
                        // Restore selection
                        if (selectedItems.Any(item => item.StartsWith(creator.Key)))
                        {
                            CreatorsList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void FilterLicenseTypesList(string filterText)
        {
            if (LicenseTypeList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var selectedItems = LicenseTypeList.SelectedItems.Cast<string>().ToList();
                LicenseTypeList.Items.Clear();
                
                var licenseCounts = _filterManager.GetLicenseCounts(_packageManager.PackageMetadata);
                var searchTerms = VPM.Services.SearchHelper.PrepareSearchTerms(filterText);
                
                foreach (var license in licenseCounts.OrderBy(l => l.Key))
                {
                    if (VPM.Services.SearchHelper.MatchesAllTerms(license.Key, searchTerms))
                    {
                        var displayText = $"{license.Key} ({license.Value})";
                        LicenseTypeList.Items.Add(displayText);
                        
                        // Restore selection
                        if (selectedItems.Any(item => item.StartsWith(license.Key)))
                        {
                            LicenseTypeList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void FilterSubfoldersList(string filterText)
        {
            if (SubfoldersFilterList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var selectedItems = SubfoldersFilterList.SelectedItems.Cast<string>().ToList();
                SubfoldersFilterList.Items.Clear();
                
                var subfolderCounts = _filterManager.GetSubfolderCounts(_packageManager.PackageMetadata);
                var searchTerms = VPM.Services.SearchHelper.PrepareSearchTerms(filterText);
                
                foreach (var subfolder in subfolderCounts.OrderBy(s => s.Key))
                {
                    if (VPM.Services.SearchHelper.MatchesAllTerms(subfolder.Key, searchTerms))
                    {
                        var displayText = $"{subfolder.Key} ({subfolder.Value})";
                        SubfoldersFilterList.Items.Add(displayText);
                        
                        // Restore selection
                        if (selectedItems.Any(item => item.StartsWith(subfolder.Key)))
                        {
                            SubfoldersFilterList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Filter List Population

        private void PopulateFilterLists()
        {
            if (_filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                // Populate status filter list (includes optimization status)
                PopulateStatusFilterList();
                
                // Populate creators list
                PopulateCreatorsList();
                
                // Populate content types list
                PopulateContentTypesList();
                
                // Populate license types list
                PopulateLicenseTypesList();
                
                // Populate file size filter list
                PopulateFileSizeFilterList();
                
                // Populate subfolders filter list
                System.Diagnostics.Debug.WriteLine("[REFRESH_FILTERS] About to call PopulateSubfoldersFilterList");
                PopulateSubfoldersFilterList();
                System.Diagnostics.Debug.WriteLine("[REFRESH_FILTERS] PopulateSubfoldersFilterList completed");
                
                // Populate damaged filter list
                PopulateDamagedFilterList();
                
                // Populate date filter list
                PopulateDateFilterList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERROR] Exception in RefreshFilterLists: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void PopulateStatusFilterList()
        {
            if (StatusFilterList == null) return;

            try
            {
                StatusFilterList.Items.Clear();
                
                // Add regular status counts
                var statusCounts = _filterManager.GetStatusCounts(_packageManager.PackageMetadata);
                
                foreach (var status in statusCounts.OrderBy(s => s.Key))
                {
                    StatusFilterList.Items.Add($"{status.Key} ({status.Value})");
                }
                
                // Add optimization status counts
                var optCounts = _filterManager.GetOptimizationStatusCounts(_packageManager.PackageMetadata);
                
                foreach (var opt in optCounts.OrderBy(s => s.Key))
                {
                    StatusFilterList.Items.Add($"{opt.Key} ({opt.Value:N0})");
                }
                
                // Add version status counts
                var versionCounts = _filterManager.GetVersionStatusCounts(_packageManager.PackageMetadata);
                
                foreach (var ver in versionCounts.OrderBy(s => s.Key))
                {
                    StatusFilterList.Items.Add($"{ver.Key} ({ver.Value:N0})");
                }
            }
            catch (Exception)
            {
            }
        }

        private void PopulateCreatorsList()
        {
            if (CreatorsList == null) return;

            try
            {
                // Store current selections before clearing
                var selectedItems = new List<string>();
                foreach (var item in CreatorsList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        selectedItems.Add(itemText);
                    }
                }
                
                CreatorsList.Items.Clear();
                
                var creatorCounts = _filterManager.GetCreatorCounts(_packageManager.PackageMetadata);
                
                foreach (var creator in creatorCounts.OrderBy(c => c.Key))
                {
                    var displayText = $"{creator.Key} ({creator.Value})";
                    CreatorsList.Items.Add(displayText);
                    
                    // Restore selection if this creator was previously selected
                    if (selectedItems.Any(item => item.StartsWith(creator.Key)))
                    {
                        CreatorsList.SelectedItems.Add(displayText);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void PopulateContentTypesList()
        {
            if (ContentTypesList == null) return;

            try
            {
                Console.WriteLine("[DEBUG] PopulateContentTypesList: Starting population");
                
                // Store current selections before clearing
                var selectedItems = new List<string>();
                foreach (var item in ContentTypesList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        selectedItems.Add(itemText);
                    }
                }
                
                ContentTypesList.Items.Clear();
                
                var categoryCounts = _filterManager.GetCategoryCounts(_packageManager.PackageMetadata);
                Console.WriteLine($"[DEBUG] PopulateContentTypesList: Got {categoryCounts.Count} category types from FilterManager");
                
                foreach (var category in categoryCounts.OrderBy(c => c.Key))
                {
                    var displayText = $"{category.Key} ({category.Value})";
                    ContentTypesList.Items.Add(displayText);
                    Console.WriteLine($"[DEBUG] PopulateContentTypesList: Added '{displayText}' to ContentTypesList");
                    
                    // Restore selection if this content type was previously selected
                    if (selectedItems.Any(item => item.StartsWith(category.Key)))
                    {
                        ContentTypesList.SelectedItems.Add(displayText);
                    }
                }
                
                Console.WriteLine($"[DEBUG] PopulateContentTypesList: Finished with {ContentTypesList.Items.Count} items in list");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] PopulateContentTypesList: Exception occurred: {ex.Message}");
            }
        }

        private void PopulateLicenseTypesList()
        {
            if (LicenseTypeList == null) return;

            try
            {
                // Store current selections before clearing
                var selectedItems = new List<string>();
                foreach (var item in LicenseTypeList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        selectedItems.Add(itemText);
                    }
                }
                
                LicenseTypeList.Items.Clear();
                
                var licenseCounts = _filterManager.GetLicenseCounts(_packageManager.PackageMetadata);
                
                foreach (var license in licenseCounts.OrderBy(l => l.Key))
                {
                    var displayText = $"{license.Key} ({license.Value})";
                    LicenseTypeList.Items.Add(displayText);
                    
                    // Restore selection if this license type was previously selected
                    if (selectedItems.Any(item => item.StartsWith(license.Key)))
                    {
                        LicenseTypeList.SelectedItems.Add(displayText);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void PopulateFileSizeFilterList()
        {
            if (FileSizeFilterList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                // Store current selections before clearing
                var selectedItems = new List<string>();
                foreach (var item in FileSizeFilterList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        selectedItems.Add(itemText);
                    }
                }
                
                FileSizeFilterList.Items.Clear();
                
                var fileSizeCounts = _filterManager.GetFileSizeCounts(_packageManager.PackageMetadata);
                
                // Add in specific order
                var orderedRanges = new[] { "Tiny", "Small", "Medium", "Large" };
                foreach (var range in orderedRanges)
                {
                    if (fileSizeCounts.ContainsKey(range) && fileSizeCounts[range] > 0)
                    {
                        var displayText = $"{range} ({fileSizeCounts[range]})";
                        FileSizeFilterList.Items.Add(displayText);
                        
                        // Restore selection if this range was previously selected
                        if (selectedItems.Any(item => item.StartsWith(range)))
                        {
                            FileSizeFilterList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating file size filter: {ex.Message}");
            }
        }

        private void PopulateSubfoldersFilterList()
        {
            System.Diagnostics.Debug.WriteLine($"[POPULATE_SUBFOLDERS] Called. SubfoldersFilterList={SubfoldersFilterList != null}, FilterManager={_filterManager != null}, Metadata={_packageManager?.PackageMetadata != null}");
            
            if (SubfoldersFilterList == null || _filterManager == null || _packageManager?.PackageMetadata == null)
            {
                System.Diagnostics.Debug.WriteLine($"[POPULATE_SUBFOLDERS] Early return - one of the required objects is null");
                return;
            }

            try
            {
                // Store current selections before clearing
                var selectedItems = new List<string>();
                foreach (var item in SubfoldersFilterList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        selectedItems.Add(itemText);
                    }
                }
                
                SubfoldersFilterList.Items.Clear();
                
                var subfolderCounts = _filterManager.GetSubfolderCounts(_packageManager.PackageMetadata);
                
                System.Diagnostics.Debug.WriteLine($"[SUBFOLDERS] Found {subfolderCounts.Count} subfolders");
                foreach (var kvp in subfolderCounts)
                {
                    System.Diagnostics.Debug.WriteLine($"[SUBFOLDERS] {kvp.Key}: {kvp.Value} packages");
                }
                
                // Sort subfolders alphabetically
                var sortedSubfolders = subfolderCounts.Keys.OrderBy(k => k).ToList();
                foreach (var subfolder in sortedSubfolders)
                {
                    if (subfolderCounts[subfolder] > 0)
                    {
                        var displayText = $"{subfolder} ({subfolderCounts[subfolder]})";
                        SubfoldersFilterList.Items.Add(displayText);
                        
                        // Restore selection if this subfolder was previously selected
                        if (selectedItems.Any(item => item.StartsWith(subfolder)))
                        {
                            SubfoldersFilterList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating subfolders filter: {ex.Message}");
            }
        }

        private void PopulateDamagedFilterList()
        {
            if (DamagedFilterList == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var selectedItem = DamagedFilterList.SelectedItem?.ToString();
                
                DamagedFilterList.Items.Clear();
                
                int damagedCount = _packageManager.PackageMetadata.Values.Count(m => m.IsDamaged);
                int validCount = _packageManager.PackageMetadata.Count - damagedCount;
                
                DamagedFilterList.Items.Add($"All Packages ({_packageManager.PackageMetadata.Count})");
                
                if (damagedCount > 0)
                {
                    DamagedFilterList.Items.Add($"–ï¸ Damaged ({damagedCount})");
                }
                
                if (validCount > 0)
                {
                    DamagedFilterList.Items.Add($"✓ Valid ({validCount})");
                }
                
                if (!string.IsNullOrEmpty(selectedItem) && DamagedFilterList.Items.Contains(selectedItem))
                {
                    DamagedFilterList.SelectedItem = selectedItem;
                }
                else
                {
                    DamagedFilterList.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating damaged filter: {ex.Message}");
            }
        }

        #endregion

        #region Live Filter Updates

        /// <summary>
        /// Update filter counts live without rebuilding filter lists (non-cascade mode)
        /// This is much faster than RefreshFilterLists as it only updates counts
        /// </summary>
        private void UpdateFilterCountsLive()
        {
            if (_packageManager?.PackageMetadata == null || _reactiveFilterManager == null)
                return;

            try
            {
                _suppressSelectionEvents = true;

                // Get filtered packages based on current filter state
                var filteredPackages = _reactiveFilterManager.GetFilteredPackages();

                // Update each filter list with new counts while preserving selections
                UpdateStatusListCounts(filteredPackages);
                UpdateCreatorsListCounts(filteredPackages);
                UpdateContentTypesListCounts(filteredPackages);
                UpdateLicenseTypesListCounts(filteredPackages);
                UpdateFileSizeListCounts(filteredPackages);
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        /// <summary>
        /// Update cascade filtering live - optimized version
        /// </summary>
        private void UpdateCascadeFilteringLive(Dictionary<string, object> currentFilters)
        {
            if (_packageManager?.PackageMetadata == null || _reactiveFilterManager == null)
                return;

            try
            {
                // Get currently filtered packages based on active filters
                var filteredPackages = _reactiveFilterManager.GetFilteredPackages();

                // Check which filters are active
                var hasStatusFilter = currentFilters.ContainsKey("Status") &&
                                    currentFilters["Status"] is List<string> statusList && statusList.Count > 0;
                var hasCreatorFilter = currentFilters.ContainsKey("Creator") &&
                                     currentFilters["Creator"] is List<string> creatorList && creatorList.Count > 0;
                var hasContentTypeFilter = currentFilters.ContainsKey("ContentType") &&
                                          currentFilters["ContentType"] is List<string> contentList && contentList.Count > 0;
                var hasLicenseTypeFilter = currentFilters.ContainsKey("LicenseType") &&
                                          currentFilters["LicenseType"] is List<string> licenseList && licenseList.Count > 0;
                var hasFileSizeFilter = currentFilters.ContainsKey("FileSizeRange") &&
                                       currentFilters["FileSizeRange"] is List<string> fileSizeList && fileSizeList.Count > 0;
                var hasSubfoldersFilter = currentFilters.ContainsKey("Subfolders") &&
                                         currentFilters["Subfolders"] is List<string> subfoldersList && subfoldersList.Count > 0;
                var hasDateFilter = currentFilters.ContainsKey("DateFilter") &&
                                  currentFilters["DateFilter"] is DateFilter dateFilter && dateFilter.FilterType != DateFilterType.AllTime;

                // Update filter lists based on cascade rules
                UpdateStatusListWithCascade(filteredPackages, hasStatusFilter);
                UpdateCreatorsListWithCascade(filteredPackages, hasCreatorFilter);
                UpdateContentTypesListWithCascade(filteredPackages, hasContentTypeFilter);
                UpdateLicenseTypesListWithCascade(filteredPackages, hasLicenseTypeFilter);
                UpdateFileSizeFilterListWithCascade(filteredPackages, hasFileSizeFilter);
                UpdateSubfoldersFilterListWithCascade(filteredPackages, hasSubfoldersFilter);
                UpdateDateFilterListWithCascade(filteredPackages, hasDateFilter);

                // Also refresh dependencies to show only deps from filtered packages
                RefreshDependenciesAfterCascade();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Update status list counts without rebuilding the list
        /// </summary>
        private void UpdateStatusListCounts(Dictionary<string, VarMetadata> filteredPackages)
        {
            if (StatusFilterList == null) return;

            var selectedStatuses = GetSelectedItemNames(StatusFilterList);
            var statusCounts = _filterManager.GetStatusCounts(filteredPackages);
            var optCounts = _filterManager.GetOptimizationStatusCounts(filteredPackages);

            StatusFilterList.Items.Clear();

            // Add status items with updated counts
            foreach (var status in statusCounts.OrderBy(s => s.Key))
            {
                var displayName = status.Key.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) ? "Duplicates" : status.Key;
                var displayText = $"{displayName} ({status.Value:N0})";
                StatusFilterList.Items.Add(displayText);

                if (selectedStatuses.Contains(status.Key))
                {
                    StatusFilterList.SelectedItems.Add(displayText);
                }
            }

            // Add optimization status items
            foreach (var opt in optCounts.OrderBy(s => s.Key))
            {
                var displayText = $"{opt.Key} ({opt.Value:N0})";
                StatusFilterList.Items.Add(displayText);

                if (selectedStatuses.Contains(opt.Key))
                {
                    StatusFilterList.SelectedItems.Add(displayText);
                }
            }

            // Add version status items (always show, even if count is 0)
            var versionCounts = _filterManager.GetVersionStatusCounts(filteredPackages);
            foreach (var ver in versionCounts.OrderBy(s => s.Key))
            {
                var displayText = $"{ver.Key} ({ver.Value:N0})";
                StatusFilterList.Items.Add(displayText);

                if (selectedStatuses.Contains(ver.Key))
                {
                    StatusFilterList.SelectedItems.Add(displayText);
                }
            }
            
            // Add favorites option
            if (_favoritesManager != null && _packageManager?.PackageMetadata != null)
            {
                var favorites = _favoritesManager.GetAllFavorites();
                int favoriteCount = 0;
                
                // Count from ALL packages, not filtered packages
                foreach (var pkg in _packageManager.PackageMetadata.Values)
                {
                    var pkgName = System.IO.Path.GetFileNameWithoutExtension(pkg.Filename);
                    if (favorites.Contains(pkgName))
                        favoriteCount++;
                }
                
                var favText = $"Favorites ({favoriteCount:N0})";
                StatusFilterList.Items.Add(favText);
                
                if (selectedStatuses.Contains("Favorites"))
                {
                    StatusFilterList.SelectedItems.Add(favText);
                }
            }

            // Add autoinstall option
            if (_autoInstallManager != null && _packageManager?.PackageMetadata != null)
            {
                var autoInstall = _autoInstallManager.GetAllAutoInstall();
                int autoInstallCount = 0;
                
                // Count from ALL packages, not filtered packages
                foreach (var pkg in _packageManager.PackageMetadata.Values)
                {
                    var pkgName = System.IO.Path.GetFileNameWithoutExtension(pkg.Filename);
                    if (autoInstall.Contains(pkgName))
                        autoInstallCount++;
                }
                
                var autoInstallText = $"AutoInstall ({autoInstallCount:N0})";
                StatusFilterList.Items.Add(autoInstallText);
                
                if (selectedStatuses.Contains("AutoInstall"))
                {
                    StatusFilterList.SelectedItems.Add(autoInstallText);
                }
            }
        }

        /// <summary>
        /// Update creators list counts without rebuilding the list
        /// </summary>
        private void UpdateCreatorsListCounts(Dictionary<string, VarMetadata> filteredPackages)
        {
            if (CreatorsList == null) return;

            var selectedCreators = GetSelectedItemNames(CreatorsList);
            var creatorFilterText = GetSearchText(CreatorsFilterBox);
            var creatorCounts = _filterManager.GetCreatorCounts(filteredPackages);

            CreatorsList.Items.Clear();

            // Limit to top 500 creators to prevent UI slowdown
            var topCreators = creatorCounts
                .OrderByDescending(c => c.Value)
                .Take(500)
                .OrderBy(c => c.Key)
                .ToList();

            foreach (var creator in topCreators)
            {
                // Apply creator filter text if present
                if (!string.IsNullOrWhiteSpace(creatorFilterText) &&
                    creator.Key.IndexOf(creatorFilterText, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var displayText = $"{creator.Key} ({creator.Value})";
                CreatorsList.Items.Add(displayText);

                if (selectedCreators.Contains(creator.Key))
                {
                    CreatorsList.SelectedItems.Add(displayText);
                }
            }
        }

        /// <summary>
        /// Update content types list counts without rebuilding the list
        /// </summary>
        private void UpdateContentTypesListCounts(Dictionary<string, VarMetadata> filteredPackages)
        {
            if (ContentTypesList == null) return;

            var selectedContentTypes = GetSelectedItemNames(ContentTypesList);
            var categoryCounts = _filterManager.GetCategoryCounts(filteredPackages);

            ContentTypesList.Items.Clear();

            foreach (var category in categoryCounts.OrderBy(c => c.Key))
            {
                var displayText = $"{category.Key} ({category.Value:N0})";
                ContentTypesList.Items.Add(displayText);

                if (selectedContentTypes.Contains(category.Key))
                {
                    ContentTypesList.SelectedItems.Add(displayText);
                }
            }
        }

        /// <summary>
        /// Update license types list counts without rebuilding the list
        /// </summary>
        private void UpdateLicenseTypesListCounts(Dictionary<string, VarMetadata> filteredPackages)
        {
            if (LicenseTypeList == null) return;

            var selectedLicenseTypes = GetSelectedItemNames(LicenseTypeList);
            var licenseCounts = _filterManager.GetLicenseCounts(filteredPackages);

            LicenseTypeList.Items.Clear();

            foreach (var license in licenseCounts.OrderBy(l => l.Key))
            {
                var displayText = $"{license.Key} ({license.Value:N0})";
                LicenseTypeList.Items.Add(displayText);

                if (selectedLicenseTypes.Contains(license.Key))
                {
                    LicenseTypeList.SelectedItems.Add(displayText);
                }
            }
        }

        /// <summary>
        /// Update file size list counts without rebuilding the list
        /// </summary>
        private void UpdateFileSizeListCounts(Dictionary<string, VarMetadata> filteredPackages)
        {
            if (FileSizeFilterList == null) return;

            var selectedFileSizeRanges = GetSelectedItemNames(FileSizeFilterList);
            var fileSizeCounts = _filterManager.GetFileSizeCounts(filteredPackages);

            FileSizeFilterList.Items.Clear();

            var orderedRanges = new[] { "Tiny", "Small", "Medium", "Large" };
            foreach (var range in orderedRanges)
            {
                if (fileSizeCounts.ContainsKey(range) && fileSizeCounts[range] > 0)
                {
                    var displayText = $"{range} ({fileSizeCounts[range]:N0})";
                    FileSizeFilterList.Items.Add(displayText);

                    if (selectedFileSizeRanges.Contains(range))
                    {
                        FileSizeFilterList.SelectedItems.Add(displayText);
                    }
                }
            }
        }

        /// <summary>
        /// Helper method to get selected item names from a ListBox
        /// </summary>
        private List<string> GetSelectedItemNames(ListBox listBox)
        {
            var selectedNames = new List<string>();
            foreach (var item in listBox.SelectedItems)
            {
                string itemText = item?.ToString() ?? "";
                if (!string.IsNullOrEmpty(itemText))
                {
                    var name = itemText.Split('(')[0].Trim();
                    // Normalize "Duplicates" to "Duplicate"
                    if (name.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                    {
                        name = "Duplicate";
                    }
                    selectedNames.Add(name);
                }
            }
            return selectedNames;
        }

        #endregion
    }

    #region Value Converters

    /// <summary>
    /// Converter to format file sizes
    /// </summary>
    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long bytes)
            {
                return FormatFileSize(bytes);
            }
            return "0 B";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

    }

    /// <summary>
    /// Converter to convert status strings to colors
    /// </summary>
    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "Loaded" => new SolidColorBrush(Color.FromRgb(76, 175, 80)),     // Green
                    "Available" => new SolidColorBrush(Color.FromRgb(33, 150, 243)),  // Blue
                    "Missing" => new SolidColorBrush(Color.FromRgb(244, 67, 54)),     // Red
                    "Outdated" => new SolidColorBrush(Color.FromRgb(255, 152, 0)),    // Orange
                    "Updating" => new SolidColorBrush(Color.FromRgb(156, 39, 176)),   // Purple
                    _ => new SolidColorBrush(Color.FromRgb(158, 158, 158))            // Gray
                };
            }
            return new SolidColorBrush(Color.FromRgb(158, 158, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter to convert status strings to icons
    /// </summary>
    public class StatusIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "Loaded" => "✓",
                    "Available" => "○",
                    "Missing" => "✗",
                    "Outdated" => "⚠",
                    "Updating" => "↻",
                    _ => "?"
                };
            }
            return "?";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    #endregion
}

