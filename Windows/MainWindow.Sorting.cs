using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// Sorting functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        #region Fields

        private SortingManager _sortingManager;

        #endregion

        #region Initialization

        private void InitializeSorting()
        {
            _sortingManager = new SortingManager(_settingsManager);
            
            // Restore sorting states from settings
            RestoreSortingStates();
        }
        
        /// <summary>
        /// Restore sorting states from persisted settings
        /// </summary>
        private void RestoreSortingStates()
        {
            if (_sortingManager == null) return;
            
            try
            {
                // Restore package sorting - use internal method to avoid toggle logic
                var packageState = _sortingManager.GetSortingState("Packages");
                if (packageState?.CurrentSortOption is PackageSortOption packageSort)
                {
                    Console.WriteLine($"[SORTING] Restoring package sorting: {packageSort} {(packageState.IsAscending ? "Ascending" : "Descending")}");
                    ReapplyPackageSortingInternal(packageSort, packageState.IsAscending);
                }
                else
                {
                    Console.WriteLine($"[SORTING] No package sorting state found to restore. CurrentSortOption: {packageState?.CurrentSortOption}");
                }
                
                // Restore dependencies sorting - use internal method to avoid toggle logic
                var depsState = _sortingManager.GetSortingState("Dependencies");
                if (depsState?.CurrentSortOption is DependencySortOption depsSort)
                {
                    Console.WriteLine($"[SORTING] Restoring dependencies sorting: {depsSort} {(depsState.IsAscending ? "Ascending" : "Descending")}");
                    ReapplyDependenciesSortingInternal(depsSort, depsState.IsAscending);
                }
                
                // NOTE: Filter list sorting restoration is deferred until after filter lists are populated
                // See RestoreFilterListsSorting() which is called from RefreshFilterLists()
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SORTING] Failed to restore sorting: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Restore filter list sorting - called AFTER filter lists are populated
        /// </summary>
        public void RestoreFilterListsSorting()
        {
            if (_sortingManager == null) return;
            
            try
            {
                // Restore filter list sorting
                RestoreFilterListSorting("Status", StatusFilterList, StatusSortButton);
                RestoreFilterListSorting("ContentTypes", ContentTypesList, ContentTypesSortButton);
                RestoreFilterListSorting("Creators", CreatorsList, CreatorsSortButton);
                RestoreFilterListSorting("LicenseTypes", LicenseTypeList, LicenseTypeSortButton);
                RestoreFilterListSorting("Subfolders", SubfoldersFilterList, SubfoldersSortButton);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SORTING] Failed to restore filter list sorting: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Restore sorting for a specific filter list
        /// </summary>
        private void RestoreFilterListSorting(string filterType, ListBox listBox, Button sortButton)
        {
            if (_sortingManager == null || listBox == null) return;
            
            try
            {
                var state = _sortingManager.GetSortingState($"FilterList_{filterType}");
                if (state?.CurrentSortOption is FilterSortOption sortOption)
                {
                    // Use internal method to avoid toggle logic during restoration
                    ReapplyFilterSortingInternal(filterType, listBox, sortButton, sortOption, state.IsAscending);
                }
            }
            catch (Exception)
            {
                // Failed to restore filter list sorting - non-critical
            }
        }
        
        /// <summary>
        /// Internal method to reapply filter list sorting without toggling direction
        /// </summary>
        private void ReapplyFilterSortingInternal(string filterType, ListBox listBox, Button sortButton, FilterSortOption sortOption, bool isAscending)
        {
            if (_sortingManager == null || listBox?.Items == null) return;

            try
            {
                // Check if ListBox has items
                if (listBox.Items.Count == 0) return;
                
                // Convert ListBox items to string list - safely handle non-string items
                var items = new List<string>();
                foreach (var item in listBox.Items)
                {
                    if (item is string str)
                    {
                        items.Add(str);
                    }
                }
                
                if (items.Count == 0) return;

                // Sort items directly without toggle logic
                if (sortOption == FilterSortOption.Name)
                {
                    if (isAscending)
                        items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(ParseFilterName(a), ParseFilterName(b)));
                    else
                        items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(ParseFilterName(b), ParseFilterName(a)));
                }
                else // FilterSortOption.Count
                {
                    if (isAscending)
                        items.Sort((a, b) => ParseFilterCount(a).CompareTo(ParseFilterCount(b)));
                    else
                        items.Sort((a, b) => ParseFilterCount(b).CompareTo(ParseFilterCount(a)));
                }

                // Update ListBox with sorted items
                var selectedItems = new List<string>();
                foreach (var item in listBox.SelectedItems)
                {
                    if (item is string str)
                    {
                        selectedItems.Add(str);
                    }
                }
                
                listBox.Items.Clear();
                foreach (var item in items)
                {
                    listBox.Items.Add(item);

                    // Restore selection
                    if (selectedItems.Contains(item))
                    {
                        listBox.SelectedItems.Add(item);
                    }
                }

                // Update button tooltip with current direction
                var displayText = sortOption.GetDisplayText(isAscending);
                sortButton.ToolTip = $"Sort {filterType.ToLower()} (Current: {displayText})";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SORTING] Error reapplying filter sorting for {filterType}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Parse filter item name from "Name (count)" format
        /// </summary>
        private string ParseFilterName(string item)
        {
            if (string.IsNullOrEmpty(item)) return string.Empty;
            int openParen = item.LastIndexOf('(');
            return openParen > 0 ? item.Substring(0, openParen).Trim() : item.Trim();
        }
        
        /// <summary>
        /// Parse filter item count from "Name (count)" format
        /// </summary>
        private int ParseFilterCount(string item)
        {
            if (string.IsNullOrEmpty(item)) return 0;
            
            int openParen = item.LastIndexOf('(');
            int closeParen = item.LastIndexOf(')');
            
            if (openParen < 0 || closeParen <= openParen || closeParen != item.Length - 1)
                return 0;
            
            int countStart = openParen + 1;
            int countLength = closeParen - countStart;
            
            if (countLength > 0 && countLength < 15)
            {
                string countStr = item.Substring(countStart, countLength).Replace(",", "").Replace(" ", "");
                if (int.TryParse(countStr, out int count))
                {
                    return count;
                }
            }
            
            return 0;
        }
        
        /// <summary>
        /// Reapply current sorting after data changes (filtering, operations, etc.)
        /// </summary>
        public void ReapplySorting()
        {
            if (_sortingManager == null) return;
            
            try
            {
                // Reapply package sorting if active
                var packageState = _sortingManager.GetSortingState("Packages");
                if (packageState?.CurrentSortOption is PackageSortOption packageSort)
                {
                    ReapplyPackageSortingInternal(packageSort, packageState.IsAscending);
                }
                
                // Reapply dependencies sorting if active
                var depsState = _sortingManager.GetSortingState("Dependencies");
                if (depsState?.CurrentSortOption is DependencySortOption depsSort)
                {
                    ReapplyDependenciesSortingInternal(depsSort, depsState.IsAscending);
                }
            }
            catch (Exception)
            {
                // Failed to reapply sorting - non-critical
            }
        }
        
        /// <summary>
        /// Internal method to reapply package sorting without toggling direction
        /// </summary>
        private void ReapplyPackageSortingInternal(PackageSortOption sortOption, bool isAscending)
        {
            if (PackagesView == null) return;
            
            try
            {
                string propertyName = sortOption switch
                {
                    PackageSortOption.Name => "DisplayName",
                    PackageSortOption.Date => "ModifiedDate",
                    PackageSortOption.Size => "FileSize",
                    PackageSortOption.Dependencies => "DependencyCount",
                    PackageSortOption.Dependents => "DependentsCount",
                    PackageSortOption.Status => "Status",
                    PackageSortOption.Morphs => "MorphCount",
                    PackageSortOption.Hair => "HairCount",
                    PackageSortOption.Clothing => "ClothingCount",
                    PackageSortOption.Scenes => "SceneCount",
                    PackageSortOption.Looks => "LooksCount",
                    PackageSortOption.Poses => "PosesCount",
                    PackageSortOption.Assets => "AssetsCount",
                    PackageSortOption.Scripts => "ScriptsCount",
                    PackageSortOption.Plugins => "PluginsCount",
                    PackageSortOption.SubScenes => "SubScenesCount",
                    PackageSortOption.Skins => "SkinsCount",
                    _ => "DisplayName"
                };
                
                using (PackagesView.DeferRefresh())
                {
                    PackagesView.SortDescriptions.Clear();
                    PackagesView.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                        propertyName,
                        isAscending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending));
                }
                
                // Update button tooltip
                var directionText = isAscending ? "†‘" : "†“";
                PackageSortButton.ToolTip = $"Sort packages (Current: {sortOption.GetDescription()} {directionText})";
            }
            catch (Exception)
            {
                // Error reapplying package sorting - silently handled
            }
        }
        
        /// <summary>
        /// Internal method to reapply dependencies sorting without toggling direction
        /// </summary>
        private void ReapplyDependenciesSortingInternal(DependencySortOption sortOption, bool isAscending)
        {
            if (DependenciesDataGrid == null) return;
            
            try
            {
                string propertyName = sortOption switch
                {
                    DependencySortOption.Name => "DisplayName",
                    DependencySortOption.Status => "Status",
                    _ => "DisplayName"
                };
                
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(DependenciesDataGrid.ItemsSource);
                if (view != null)
                {
                    using (view.DeferRefresh())
                    {
                        view.SortDescriptions.Clear();
                        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                            propertyName,
                            isAscending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending));
                    }
                }
                
                // Update button tooltip
                var directionText = isAscending ? "†‘" : "†“";
                DependenciesSortButton.ToolTip = $"Sort dependencies (Current: {sortOption.GetDescription()} {directionText})";
            }
            catch (Exception)
            {
                // Error reapplying dependencies sorting - silently handled
            }
        }

        #endregion

        #region Package Sorting

        private void PackageSortButton_Click(object sender, RoutedEventArgs e)
        {
            // Check current content mode and show appropriate sort menu
            if (_currentContentMode == "Scenes")
            {
                ShowSortMenu("Scenes", SortingManager.GetSceneSortOptions(), 
                            PackageSortButton, SceneSortMenuItem_Click);
            }
            else if (_currentContentMode == "Presets")
            {
                ShowSortMenu("Presets", SortingManager.GetPresetSortOptions(), 
                            PackageSortButton, PresetSortMenuItem_Click);
            }
            else
            {
                ShowSortMenu("Packages", SortingManager.GetPackageSortOptions(), 
                            PackageSortButton, PackageSortMenuItem_Click);
            }
        }

        private void PackageSortMenuItem_Click(object sender, RoutedEventArgs e)
        {
            HandleSortMenuClick(sender, ClearPackageSorting, 
                               tag => { if (tag is PackageSortOption sortOption) ApplyPackageSorting(sortOption); });
        }

        private void SceneSortMenuItem_Click(object sender, RoutedEventArgs e)
        {
            HandleSortMenuClick(sender, ClearSceneSorting, 
                               tag => { if (tag is SceneSortOption sortOption) ApplySceneSorting(sortOption); });
        }

        private void PresetSortMenuItem_Click(object sender, RoutedEventArgs e)
        {
            HandleSortMenuClick(sender, ClearPresetSorting, 
                               tag => { if (tag is PresetSortOption sortOption) ApplyPresetSorting(sortOption); });
        }

        private void ApplyPackageSorting(PackageSortOption sortOption)
        {
            if (_sortingManager == null || PackagesView == null) return;

            try
            {
                // Get property name for sorting
                string propertyName = sortOption switch
                {
                    PackageSortOption.Name => "DisplayName",
                    PackageSortOption.Date => "ModifiedDate",
                    PackageSortOption.Size => "FileSize",
                    PackageSortOption.Dependencies => "DependencyCount",
                    PackageSortOption.Dependents => "DependentsCount",
                    PackageSortOption.Status => "Status",
                    PackageSortOption.Morphs => "MorphCount",
                    PackageSortOption.Hair => "HairCount",
                    PackageSortOption.Clothing => "ClothingCount",
                    PackageSortOption.Scenes => "SceneCount",
                    PackageSortOption.Looks => "LooksCount",
                    PackageSortOption.Poses => "PosesCount",
                    PackageSortOption.Assets => "AssetsCount",
                    PackageSortOption.Scripts => "ScriptsCount",
                    PackageSortOption.Plugins => "PluginsCount",
                    PackageSortOption.SubScenes => "SubScenesCount",
                    PackageSortOption.Skins => "SkinsCount",
                    _ => "DisplayName"
                };

                // Update sorting manager state first to track direction
                _sortingManager.ApplyPackageSorting(Packages, sortOption);
                var direction = _sortingManager.GetSortingState("Packages")?.IsAscending == true;

                // Apply sorting to the CollectionView
                using (PackagesView.DeferRefresh())
                {
                    PackagesView.SortDescriptions.Clear();
                    PackagesView.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                        propertyName,
                        direction ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending));
                }

                // Update button tooltip to show current sort
                var directionText = direction ? "↑" : "↓";
                PackageSortButton.ToolTip = $"Sort (Current: {sortOption.GetDescription()} {directionText}) - Scroll to navigate";
            }
            catch (Exception)
            {
                // Error applying package sorting - silently handled
            }
        }

        private void ClearPackageSorting()
        {
            try
            {
                _sortingManager?.ClearSorting("Packages");
                PackageSortButton.ToolTip = "Sort (Scroll to navigate)";

                // Clear sorting from CollectionView
                if (PackagesView != null)
                {
                    using (PackagesView.DeferRefresh())
                    {
                        PackagesView.SortDescriptions.Clear();
                    }
                }
            }
            catch (Exception)
            {
                // Error clearing package sorting - silently handled
            }
        }

        #endregion

        #region Scene Sorting

        private void ApplySceneSorting(SceneSortOption sortOption)
        {
            if (_sortingManager == null || ScenesView == null) return;

            try
            {
                // Get property name for sorting
                string propertyName = sortOption switch
                {
                    SceneSortOption.Name => "DisplayName",
                    SceneSortOption.Date => "ModifiedDate",
                    SceneSortOption.Size => "FileSize",
                    SceneSortOption.Dependencies => "DependencyCount",
                    SceneSortOption.Atoms => "AtomCount",
                    _ => "DisplayName"
                };

                // Update sorting manager state first to track direction
                _sortingManager.ApplySceneSorting(Scenes, sortOption);
                var direction = _sortingManager.GetSortingState("Scenes")?.IsAscending == true;

                // Apply sorting to the CollectionView
                using (ScenesView.DeferRefresh())
                {
                    ScenesView.SortDescriptions.Clear();
                    ScenesView.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                        propertyName,
                        direction ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending));
                }

                // Update button tooltip to show current sort
                var directionText = direction ? "↑" : "↓";
                PackageSortButton.ToolTip = $"Sort (Current: {sortOption.GetDescription()} {directionText}) - Scroll to navigate";
            }
            catch (Exception)
            {
                // Error applying scene sorting - silently handled
            }
        }

        private void ClearSceneSorting()
        {
            try
            {
                _sortingManager?.ClearSorting("Scenes");
                PackageSortButton.ToolTip = "Sort (Scroll to navigate)";

                // Clear sorting from CollectionView
                if (ScenesView != null)
                {
                    using (ScenesView.DeferRefresh())
                    {
                        ScenesView.SortDescriptions.Clear();
                    }
                }
            }
            catch (Exception)
            {
                // Error clearing scene sorting - silently handled
            }
        }

        #endregion

        #region Preset Sorting

        private void ApplyPresetSorting(PresetSortOption sortOption)
        {
            if (_sortingManager == null || CustomAtomItemsView == null) return;

            try
            {
                // Get property name for sorting
                string propertyName = sortOption switch
                {
                    PresetSortOption.Name => "DisplayName",
                    PresetSortOption.Date => "ModifiedDate",
                    PresetSortOption.Size => "FileSize",
                    PresetSortOption.Category => "Category",
                    PresetSortOption.Subfolder => "Subfolder",
                    PresetSortOption.Status => "IsFavorite",
                    _ => "DisplayName"
                };

                // Update sorting manager state first to track direction
                _sortingManager.ApplyPresetSorting(CustomAtomItems, sortOption);
                var direction = _sortingManager.GetSortingState("Presets")?.IsAscending == true;

                // Apply sorting to the CollectionView
                using (CustomAtomItemsView.DeferRefresh())
                {
                    CustomAtomItemsView.SortDescriptions.Clear();
                    CustomAtomItemsView.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                        propertyName,
                        direction ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending));
                }

                // Update button tooltip to show current sort
                var directionText = direction ? "↑" : "↓";
                PackageSortButton.ToolTip = $"Sort (Current: {sortOption.GetDescription()} {directionText}) - Scroll to navigate";
            }
            catch (Exception)
            {
                // Error applying preset sorting - silently handled
            }
        }

        private void ClearPresetSorting()
        {
            try
            {
                _sortingManager?.ClearSorting("Presets");
                PackageSortButton.ToolTip = "Sort (Scroll to navigate)";

                // Clear sorting from CollectionView
                if (CustomAtomItemsView != null)
                {
                    using (CustomAtomItemsView.DeferRefresh())
                    {
                        CustomAtomItemsView.SortDescriptions.Clear();
                    }
                }
            }
            catch (Exception)
            {
                // Error clearing preset sorting - silently handled
            }
        }

        #endregion

        #region Dependencies Sorting

        private void DependenciesSortButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSortMenu("Dependencies", SortingManager.GetDependencySortOptions(), 
                        DependenciesSortButton, DependenciesSortMenuItem_Click);
        }

        private void DependenciesSortMenuItem_Click(object sender, RoutedEventArgs e)
        {
            HandleSortMenuClick(sender, ClearDependenciesSorting, 
                               tag => { if (tag is DependencySortOption sortOption) ApplyDependenciesSorting(sortOption); });
        }

        private void ApplyDependenciesSorting(DependencySortOption sortOption)
        {
            if (_sortingManager == null || DependenciesDataGrid == null) return;

            try
            {
                // Get property name for sorting
                string propertyName = sortOption switch
                {
                    DependencySortOption.Name => "DisplayName",
                    DependencySortOption.Status => "Status",
                    _ => "DisplayName"
                };

                // Update sorting manager state first to track direction
                _sortingManager.ApplyDependencySorting(Dependencies, sortOption);
                var direction = _sortingManager.GetSortingState("Dependencies")?.IsAscending == true;

                // Apply sorting to the DataGrid's CollectionView
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(DependenciesDataGrid.ItemsSource);
                if (view != null)
                {
                    using (view.DeferRefresh())
                    {
                        view.SortDescriptions.Clear();
                        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                            propertyName,
                            direction ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending));
                    }
                }

                // Update button tooltip to show current sort
                var directionText = direction ? "†‘" : "†“";
                DependenciesSortButton.ToolTip = $"Sort dependencies (Current: {sortOption.GetDescription()} {directionText})";
            }
            catch (Exception)
            {
                // Error applying dependencies sorting - silently handled
            }
        }

        private void ClearDependenciesSorting()
        {
            try
            {
                _sortingManager?.ClearSorting("Dependencies");
                DependenciesSortButton.ToolTip = "Sort dependencies";

                // Clear sorting from CollectionView
                if (DependenciesDataGrid != null)
                {
                    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(DependenciesDataGrid.ItemsSource);
                    if (view != null)
                    {
                        using (view.DeferRefresh())
                        {
                            view.SortDescriptions.Clear();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Error clearing dependencies sorting - silently handled
            }
        }

        #endregion

        #region Filter List Sorting

        private void StatusSortButton_Click(object sender, RoutedEventArgs e) =>
            ShowFilterSortMenu("Status", StatusFilterList, StatusSortButton);

        private void ContentTypesSortButton_Click(object sender, RoutedEventArgs e) =>
            ShowFilterSortMenu("ContentTypes", ContentTypesList, ContentTypesSortButton);

        private void CreatorsSortButton_Click(object sender, RoutedEventArgs e) =>
            ShowFilterSortMenu("Creators", CreatorsList, CreatorsSortButton);

        private void LicenseTypeSortButton_Click(object sender, RoutedEventArgs e) =>
            ShowFilterSortMenu("LicenseTypes", LicenseTypeList, LicenseTypeSortButton);

        private void SubfoldersSortButton_Click(object sender, RoutedEventArgs e) =>
            ShowFilterSortMenu("Subfolders", SubfoldersFilterList, SubfoldersSortButton);

        private void ShowFilterSortMenu(string filterType, ListBox listBox, Button sortButton)
        {
            try
            {
                var contextMenu = new ContextMenu();
                contextMenu.PlacementTarget = sortButton;
                contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                contextMenu.StaysOpen = false;

                // Add clear sorting option at the top
                var clearItem = new MenuItem
                {
                    Header = "Clear Sorting",
                    Tag = new { FilterType = filterType, ListBox = listBox, SortButton = sortButton, Option = "Clear" }
                };
                clearItem.Click += FilterSortMenuItem_Click;
                contextMenu.Items.Add(clearItem);
                contextMenu.Items.Add(new Separator());

                // Add sort options with current direction indicators
                var sortOptions = SortingManager.GetFilterSortOptions();
                var currentState = _sortingManager.GetSortingState($"FilterList_{filterType}");

                foreach (var option in sortOptions)
                {
                    string displayText = option.GetDescription();

                    // Add direction indicator if this is the currently active sort
                    if (currentState?.CurrentSortOption?.Equals(option) == true)
                    {
                        displayText = option.GetDisplayText(currentState.IsAscending);
                    }

                    var menuItem = new MenuItem
                    {
                        Header = displayText,
                        Tag = new { FilterType = filterType, ListBox = listBox, SortButton = sortButton, Option = option }
                    };
                    menuItem.Click += FilterSortMenuItem_Click;
                    contextMenu.Items.Add(menuItem);
                }

                // Apply theme compatible styling
                ApplyContextMenuStyling(contextMenu);

                contextMenu.IsOpen = true;
            }
            catch (Exception)
            {
                // Error showing filter sort menu - silently handled
            }
        }

        private void FilterSortMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is MenuItem menuItem && menuItem.Tag != null)
                {
                    dynamic tag = menuItem.Tag;
                    string filterType = tag.FilterType;
                    ListBox listBox = tag.ListBox;
                    Button sortButton = tag.SortButton;

                    if (tag.Option?.ToString() == "Clear")
                    {
                        ClearFilterSorting(filterType, sortButton);
                    }
                    else if (tag.Option is FilterSortOption sortOption)
                    {
                        ApplyFilterSorting(filterType, listBox, sortButton, sortOption);
                    }
                }
            }
            catch (Exception)
            {
                // Error in filter sort menu item click - silently handled
            }
        }

        private void ApplyFilterSorting(string filterType, ListBox listBox, Button sortButton, FilterSortOption sortOption)
        {
            if (_sortingManager == null || listBox?.Items == null) return;

            try
            {
                // Check if ListBox has items
                if (listBox.Items.Count == 0) return;
                
                // Convert ListBox items to string list - safely handle non-string items
                var items = new List<string>();
                foreach (var item in listBox.Items)
                {
                    if (item is string str)
                    {
                        items.Add(str);
                    }
                }
                
                if (items.Count == 0) return;

                // Apply sorting with toggle functionality
                _sortingManager.ApplyFilterListSorting(items, sortOption, filterType);

                // Update ListBox with sorted items
                var selectedItems = new List<string>();
                foreach (var item in listBox.SelectedItems)
                {
                    if (item is string str)
                    {
                        selectedItems.Add(str);
                    }
                }
                
                listBox.Items.Clear();
                foreach (var item in items)
                {
                    listBox.Items.Add(item);

                    // Restore selection
                    if (selectedItems.Contains(item))
                    {
                        listBox.SelectedItems.Add(item);
                    }
                }

                // Update button tooltip with current direction
                var currentState = _sortingManager.GetSortingState($"FilterList_{filterType}");
                var displayText = currentState != null ? sortOption.GetDisplayText(currentState.IsAscending) : sortOption.GetDescription();
                sortButton.ToolTip = $"Sort {filterType.ToLower()} (Current: {displayText})";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SORTING] Error applying filter sorting for {filterType}: {ex.Message}");
            }
        }

        private void ClearFilterSorting(string filterType, Button sortButton)
        {
            try
            {
                _sortingManager?.ClearSorting($"FilterList_{filterType}");
                sortButton.ToolTip = $"Sort {filterType.ToLower()}";

                // Refresh the filter list to original order
                if (filterType == "Status" || filterType == "ContentTypes" || filterType == "Creators" || filterType == "LicenseTypes" || filterType == "Subfolders")
                {
                    PopulateFilterLists();
                }
            }
            catch (Exception)
            {
                // Error clearing filter sorting - silently handled
            }
        }

        #endregion

        #region Theme Compatibility

        private void ApplyContextMenuStyling(ContextMenu contextMenu)
        {
            try
            {
                // Use the same colors as the existing UI theme
                contextMenu.Background = (System.Windows.Media.Brush)FindResource(SystemColors.ControlBrushKey);
                contextMenu.Foreground = (System.Windows.Media.Brush)FindResource(SystemColors.ControlTextBrushKey);
                contextMenu.BorderBrush = (System.Windows.Media.Brush)FindResource(SystemColors.ActiveBorderBrushKey);
                contextMenu.BorderThickness = new System.Windows.Thickness(1);

                // Use the same shadow effect as the package items
                contextMenu.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Gray,
                    BlurRadius = 2,
                    ShadowDepth = 1,
                    Opacity = 0.15
                };

                // Apply styling to all menu items
                foreach (var item in contextMenu.Items)
                {
                    if (item is MenuItem menuItem)
                    {
                        ApplyMenuItemStyling(menuItem);
                    }
                    else if (item is Separator separator)
                    {
                        ApplySeparatorStyling(separator);
                    }
                }
            }
            catch (Exception)
            {
                // Error applying context menu styling - silently handled
            }
        }

        private void ApplyMenuItemStyling(MenuItem menuItem)
        {
            try
            {
                // Simplified styling - just set colors without custom template
                menuItem.Background = (System.Windows.Media.Brush)FindResource(SystemColors.ControlBrushKey);
                menuItem.Foreground = (System.Windows.Media.Brush)FindResource(SystemColors.ControlTextBrushKey);
                menuItem.Padding = new System.Windows.Thickness(8, 4, 8, 4);
            }
            catch (Exception)
            {
                // Error applying menu item styling - silently handled
            }
        }

        private void ApplySeparatorStyling(Separator separator)
        {
            try
            {
                // Style the separator using existing UI theme colors
                separator.Background = (System.Windows.Media.Brush)FindResource(SystemColors.ActiveBorderBrushKey);
                separator.Foreground = (System.Windows.Media.Brush)FindResource(SystemColors.ActiveBorderBrushKey);
                separator.Margin = new System.Windows.Thickness(4, 2, 4, 2);
            }
            catch (Exception)
            {
                // Error applying separator styling - silently handled
            }
        }

        #endregion

        #region Consolidated Sort Helpers

        /// <summary>
        /// Generic sort menu builder for any sort type
        /// </summary>
        private void ShowSortMenu<T>(string contextName, IEnumerable<T> sortOptions,
                                     Button sortButton, RoutedEventHandler menuClickHandler)
        {
            try
            {
                var contextMenu = new ContextMenu();
                contextMenu.PlacementTarget = sortButton;
                contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                contextMenu.StaysOpen = false;

                // Add clear sorting option
                var clearItem = new MenuItem { Header = "Clear Sorting", Tag = "Clear" };
                clearItem.Click += menuClickHandler;
                contextMenu.Items.Add(clearItem);
                contextMenu.Items.Add(new Separator());

                // Add sort options with direction indicators
                var currentState = _sortingManager.GetSortingState(contextName);

                bool separatorAdded = false;
                foreach (var option in sortOptions)
                {
                    // Add separator before content type count options for Packages context
                    if (contextName == "Packages" && option is PackageSortOption pkgOption && 
                        pkgOption == PackageSortOption.Morphs && !separatorAdded)
                    {
                        contextMenu.Items.Add(new Separator());
                        separatorAdded = true;
                    }

                    // Cast to Enum to use extension methods
                    string displayText = option is Enum enumOption
                        ? enumOption.GetDescription()
                        : option.ToString();

                    if (currentState?.CurrentSortOption?.Equals(option) == true && option is Enum enumOpt)
                    {
                        displayText = enumOpt.GetDisplayText(currentState.IsAscending);
                    }

                    var menuItem = new MenuItem { Header = displayText, Tag = option };
                    menuItem.Click += menuClickHandler;
                    contextMenu.Items.Add(menuItem);
                }

                ApplyContextMenuStyling(contextMenu);
                contextMenu.IsOpen = true;
            }
            catch (Exception)
            {
                // Error showing sort menu - silently handled
            }
        }

        /// <summary>
        /// Generic sort menu click handler
        /// </summary>
        private void HandleSortMenuClick(object sender, Action clearAction, Action<object> applyAction)
        {
            try
            {
                if (sender is MenuItem menuItem)
                {
                    if (menuItem.Tag?.ToString() == "Clear")
                    {
                        clearAction();
                    }
                    else
                    {
                        applyAction(menuItem.Tag);
                    }
                }
            }
            catch (Exception)
            {
                // Error handling sort menu click - silently handled
            }
        }

        #endregion
    }
}

