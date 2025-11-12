using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Manages sorting functionality for all tables in the application
    /// </summary>
    public class SortingManager
    {
        private readonly Dictionary<string, SortingState> _sortingStates;
        private readonly SettingsManager _settingsManager;

        public SortingManager(SettingsManager settingsManager = null)
        {
            _sortingStates = new Dictionary<string, SortingState>();
            _settingsManager = settingsManager;
            
            // Load persisted sorting states if available
            LoadSortingStatesFromSettings();
        }

        #region Public Methods

        /// <summary>
        /// Apply sorting to a package collection with toggle functionality
        /// </summary>
        public void ApplyPackageSorting(ObservableCollection<PackageItem> packages, PackageSortOption sortOption)
        {
            if (packages == null) return;

            var view = CollectionViewSource.GetDefaultView(packages);
            if (view == null) return;

            // Determine sort direction - toggle if same option, default based on field type for new option
            var currentState = GetSortingState("Packages");
            bool isAscending;
            
            if (currentState?.CurrentSortOption?.Equals(sortOption) == true)
            {
                // Same option clicked - toggle direction
                isAscending = !currentState.IsAscending;
            }
            else
            {
                // New option - default descending for numeric fields, ascending for text
                isAscending = sortOption == PackageSortOption.Name || sortOption == PackageSortOption.Status;
            }

            view.SortDescriptions.Clear();

            string propertyName = GetPropertyNameForPackageSort(sortOption);
            var direction = isAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
            view.SortDescriptions.Add(new SortDescription(propertyName, direction));

            view.Refresh();
            UpdateSortingState("Packages", sortOption, isAscending);
        }

        private string GetPropertyNameForPackageSort(PackageSortOption sortOption)
        {
            return sortOption switch
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
        }

        /// <summary>
        /// Apply sorting to a dependencies collection with toggle functionality
        /// </summary>
        public void ApplyDependencySorting(ObservableCollection<DependencyItem> dependencies, DependencySortOption sortOption)
        {
            if (dependencies == null) return;

            var view = CollectionViewSource.GetDefaultView(dependencies);
            if (view == null) return;

            // Determine sort direction - toggle if same option, default based on field type for new option
            var currentState = GetSortingState("Dependencies");
            bool isAscending;
            
            if (currentState?.CurrentSortOption?.Equals(sortOption) == true)
            {
                // Same option clicked - toggle direction
                isAscending = !currentState.IsAscending;
            }
            else
            {
                // New option - default ascending for text fields
                isAscending = sortOption == DependencySortOption.Name || sortOption == DependencySortOption.Status;
            }

            view.SortDescriptions.Clear();

            string propertyName = GetPropertyNameForDependencySort(sortOption);
            var direction = isAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
            view.SortDescriptions.Add(new SortDescription(propertyName, direction));

            view.Refresh();
            UpdateSortingState("Dependencies", sortOption, isAscending);
        }

        private string GetPropertyNameForDependencySort(DependencySortOption sortOption)
        {
            return sortOption switch
            {
                DependencySortOption.Name => "DisplayName",
                DependencySortOption.Status => "Status",
                _ => "DisplayName"
            };
        }

        /// <summary>
        /// Apply sorting to a scenes collection with toggle functionality
        /// </summary>
        public void ApplySceneSorting(ObservableCollection<SceneItem> scenes, SceneSortOption sortOption)
        {
            if (scenes == null) return;

            var view = CollectionViewSource.GetDefaultView(scenes);
            if (view == null) return;

            // Determine sort direction - toggle if same option, default based on field type for new option
            var currentState = GetSortingState("Scenes");
            bool isAscending;
            
            if (currentState?.CurrentSortOption?.Equals(sortOption) == true)
            {
                // Same option clicked - toggle direction
                isAscending = !currentState.IsAscending;
            }
            else
            {
                // New option - default ascending for text fields, descending for numeric
                isAscending = sortOption == SceneSortOption.Name;
            }

            view.SortDescriptions.Clear();

            string propertyName = GetPropertyNameForSceneSort(sortOption);
            var direction = isAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
            view.SortDescriptions.Add(new SortDescription(propertyName, direction));

            view.Refresh();
            UpdateSortingState("Scenes", sortOption, isAscending);
        }

        private string GetPropertyNameForSceneSort(SceneSortOption sortOption)
        {
            return sortOption switch
            {
                SceneSortOption.Name => "DisplayName",
                SceneSortOption.Date => "ModifiedDate",
                SceneSortOption.Size => "FileSize",
                SceneSortOption.Dependencies => "DependencyCount",
                SceneSortOption.Atoms => "AtomCount",
                _ => "DisplayName"
            };
        }

        /// <summary>
        /// Apply sorting to a presets collection with toggle functionality
        /// </summary>
        public void ApplyPresetSorting(ObservableCollection<CustomAtomItem> presets, PresetSortOption sortOption)
        {
            if (presets == null) return;

            var view = CollectionViewSource.GetDefaultView(presets);
            if (view == null) return;

            // Determine sort direction - toggle if same option, default based on field type for new option
            var currentState = GetSortingState("Presets");
            bool isAscending;
            
            if (currentState?.CurrentSortOption?.Equals(sortOption) == true)
            {
                // Same option clicked - toggle direction
                isAscending = !currentState.IsAscending;
            }
            else
            {
                // New option - default ascending for text fields, descending for numeric
                isAscending = sortOption == PresetSortOption.Name || sortOption == PresetSortOption.Category || 
                             sortOption == PresetSortOption.Subfolder || sortOption == PresetSortOption.Status;
            }

            view.SortDescriptions.Clear();

            string propertyName = GetPropertyNameForPresetSort(sortOption);
            var direction = isAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
            view.SortDescriptions.Add(new SortDescription(propertyName, direction));

            view.Refresh();
            UpdateSortingState("Presets", sortOption, isAscending);
        }

        private string GetPropertyNameForPresetSort(PresetSortOption sortOption)
        {
            return sortOption switch
            {
                PresetSortOption.Name => "DisplayName",
                PresetSortOption.Date => "ModifiedDate",
                PresetSortOption.Size => "FileSize",
                PresetSortOption.Category => "Category",
                PresetSortOption.Subfolder => "Subfolder",
                PresetSortOption.Status => "IsFavorite", // Could be enhanced to consider hidden status too
                _ => "DisplayName"
            };
        }

        /// <summary>
        /// Apply sorting to a filter list (generic string collection) with toggle functionality
        /// Optimized to parse items once upfront instead of on every comparison
        /// </summary>
        public void ApplyFilterListSorting(IList<string> filterItems, FilterSortOption sortOption, string filterType)
        {
            if (filterItems == null || filterItems.Count == 0) return;

            // Determine sort direction - toggle if same option, default based on field type for new option
            var currentState = GetSortingState($"FilterList_{filterType}");
            bool isAscending;
            
            if (currentState?.CurrentSortOption?.Equals(sortOption) == true)
            {
                // Same option clicked - toggle direction
                isAscending = !currentState.IsAscending;
            }
            else
            {
                // New option - default descending for Count, ascending for Name
                isAscending = sortOption == FilterSortOption.Name;
            }

            // OPTIMIZATION: Parse all items once upfront, then sort based on cached parsed data
            // This changes complexity from O(N log N * P) to O(N * P + N log N) where P is parse cost
            var parsedItems = new List<(string original, string name, int count)>(filterItems.Count);
            for (int i = 0; i < filterItems.Count; i++)
            {
                var item = filterItems[i];
                var (name, count) = ParseFilterItem(item);
                parsedItems.Add((item, name, count));
            }

            // Sort based on pre-parsed data
            if (sortOption == FilterSortOption.Name)
            {
                if (isAscending)
                    parsedItems.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.name, b.name));
                else
                    parsedItems.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(b.name, a.name));
            }
            else // FilterSortOption.Count
            {
                if (isAscending)
                    parsedItems.Sort((a, b) => a.count.CompareTo(b.count));
                else
                    parsedItems.Sort((a, b) => b.count.CompareTo(a.count));
            }

            // Update the original list in-place to avoid allocations
            for (int i = 0; i < parsedItems.Count; i++)
            {
                filterItems[i] = parsedItems[i].original;
            }

            UpdateSortingState($"FilterList_{filterType}", sortOption, isAscending);
        }

        /// <summary>
        /// Get current sorting state for a table
        /// </summary>
        public SortingState GetSortingState(string tableKey)
        {
            return _sortingStates.TryGetValue(tableKey, out var state) ? state : new SortingState();
        }

        /// <summary>
        /// Clear sorting for a specific table
        /// </summary>
        public void ClearSorting(string tableKey)
        {
            _sortingStates.Remove(tableKey);
            
            // Remove from settings
            if (_settingsManager?.Settings?.SortingStates != null)
            {
                _settingsManager.Settings.SortingStates.Remove(tableKey);
                
                // Trigger property change notification by reassigning the dictionary
                var currentStates = _settingsManager.Settings.SortingStates;
                _settingsManager.Settings.SortingStates = new Dictionary<string, SerializableSortingState>(currentStates);
            }
        }

        /// <summary>
        /// Clear all sorting states
        /// </summary>
        public void ClearAllSorting()
        {
            _sortingStates.Clear();
            
            // Clear from settings
            if (_settingsManager?.Settings != null)
            {
                _settingsManager.Settings.SortingStates = new Dictionary<string, SerializableSortingState>();
            }
        }

        #endregion

        #region Private Methods

        private void UpdateSortingState(string tableKey, object sortOption, bool isAscending)
        {
            _sortingStates[tableKey] = new SortingState(sortOption, isAscending);
            
            // Persist to settings
            SaveSortingStateToSettings(tableKey, sortOption, isAscending);
        }

        /// <summary>
        /// Parses filter item in "Name (count)" format
        /// Optimized for single-pass parsing with minimal allocations
        /// </summary>
        private static (string name, int count) ParseFilterItem(string item)
        {
            if (string.IsNullOrEmpty(item))
                return (item ?? string.Empty, 0);

            // Find last '(' and ')' for count extraction
            int openParen = item.LastIndexOf('(');
            int closeParen = item.LastIndexOf(')');

            // Validate format: must have both parentheses and ')' comes after '('
            if (openParen < 0 || closeParen <= openParen || closeParen != item.Length - 1)
            {
                // No count found, return full string as name
                return (item.Trim(), 0);
            }

            // Extract name (everything before '(')
            string name = openParen > 0 ? item.Substring(0, openParen).Trim() : string.Empty;

            // Extract and parse count (between parentheses)
            int countStart = openParen + 1;
            int countLength = closeParen - countStart;
            
            if (countLength > 0 && countLength < 15) // Sanity check: reasonable count length (increased for formatted numbers)
            {
                // Extract count string and remove any formatting (commas, spaces)
                string countStr = item.Substring(countStart, countLength).Replace(",", "").Replace(" ", "");
                if (int.TryParse(countStr, out int count))
                {
                    return (name, count);
                }
            }

            return (name, 0);
        }

        #endregion

        #region Persistence Methods

        /// <summary>
        /// Load sorting states from settings
        /// </summary>
        private void LoadSortingStatesFromSettings()
        {
            if (_settingsManager?.Settings?.SortingStates == null) return;

            try
            {
                foreach (var kvp in _settingsManager.Settings.SortingStates)
                {
                    var tableKey = kvp.Key;
                    var serializedState = kvp.Value;

                    if (serializedState == null || string.IsNullOrEmpty(serializedState.SortOptionType))
                        continue;

                    // Deserialize the sort option based on type
                    object sortOption = DeserializeSortOption(serializedState.SortOptionType, serializedState.SortOptionValue);
                    
                    if (sortOption != null)
                    {
                        _sortingStates[tableKey] = new SortingState(sortOption, serializedState.IsAscending);
                    }
                }
            }
            catch (Exception)
            {
                // Failed to load sorting states - continue with empty state
            }
        }

        /// <summary>
        /// Save a sorting state to settings
        /// </summary>
        private void SaveSortingStateToSettings(string tableKey, object sortOption, bool isAscending)
        {
            if (_settingsManager?.Settings == null) return;

            try
            {
                var serializedState = SerializeSortOption(sortOption, isAscending);
                if (serializedState != null)
                {
                    if (_settingsManager.Settings.SortingStates == null)
                    {
                        _settingsManager.Settings.SortingStates = new Dictionary<string, SerializableSortingState>();
                    }
                    _settingsManager.Settings.SortingStates[tableKey] = serializedState;
                    
                    Console.WriteLine($"[SORTING] Saved sorting state: {tableKey} = {sortOption} {(isAscending ? "Ascending" : "Descending")}");
                    
                    // Trigger property change notification by reassigning the dictionary
                    // This ensures the SettingsManager detects the change and saves
                    var currentStates = _settingsManager.Settings.SortingStates;
                    _settingsManager.Settings.SortingStates = new Dictionary<string, SerializableSortingState>(currentStates);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SORTING] Failed to save sorting state: {ex.Message}");
            }
        }

        /// <summary>
        /// Serialize a sort option to a persistable format
        /// </summary>
        private SerializableSortingState SerializeSortOption(object sortOption, bool isAscending)
        {
            if (sortOption == null) return null;

            string typeName = sortOption.GetType().Name;
            string value = sortOption.ToString();

            return new SerializableSortingState(typeName, value, isAscending);
        }

        /// <summary>
        /// Deserialize a sort option from settings
        /// </summary>
        private object DeserializeSortOption(string typeName, string value)
        {
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(value))
                return null;

            try
            {
                return typeName switch
                {
                    nameof(PackageSortOption) => Enum.Parse<PackageSortOption>(value),
                    nameof(SceneSortOption) => Enum.Parse<SceneSortOption>(value),
                    nameof(PresetSortOption) => Enum.Parse<PresetSortOption>(value),
                    nameof(DependencySortOption) => Enum.Parse<DependencySortOption>(value),
                    nameof(FilterSortOption) => Enum.Parse<FilterSortOption>(value),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Static Helper Methods

        /// <summary>
        /// Get all available package sort options
        /// </summary>
        public static List<PackageSortOption> GetPackageSortOptions()
        {
            return Enum.GetValues<PackageSortOption>().ToList();
        }

        /// <summary>
        /// Get all available scene sort options
        /// </summary>
        public static List<SceneSortOption> GetSceneSortOptions()
        {
            return Enum.GetValues<SceneSortOption>().ToList();
        }

        /// <summary>
        /// Get all available preset sort options
        /// </summary>
        public static List<PresetSortOption> GetPresetSortOptions()
        {
            return Enum.GetValues<PresetSortOption>().ToList();
        }

        /// <summary>
        /// Get all available dependency sort options
        /// </summary>
        public static List<DependencySortOption> GetDependencySortOptions()
        {
            return Enum.GetValues<DependencySortOption>().ToList();
        }

        /// <summary>
        /// Get all available filter sort options
        /// </summary>
        public static List<FilterSortOption> GetFilterSortOptions()
        {
            return Enum.GetValues<FilterSortOption>().ToList();
        }

        #endregion
    }
}

