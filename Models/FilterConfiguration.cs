using System;
using System.Collections.Generic;

namespace VPM.Models
{
    /// <summary>
    /// Centralized configuration for all filter types across different content modes.
    /// This prevents hardcoding filter names in multiple places and makes it easier to add new filters.
    /// </summary>
    public static class FilterConfiguration
    {
        /// <summary>
        /// Package mode filters in their default order
        /// </summary>
        public static readonly string[] PackageFilters = 
        { 
            "DateFilter",
            "StatusFilter",
            "ContentTypesFilter",
            "CreatorsFilter",
            "LicenseTypeFilter",
            "FileSizeFilter",
            "SubfoldersFilter",
            "DamagedFilter",
            "DestinationsFilter",
            "PlaylistsFilter",
            "VpbRatingFilter",
            "VpbTagFilter"
        };

        /// <summary>
        /// Scene mode filters in their default order
        /// </summary>
        public static readonly string[] SceneFilters = 
        { 
            "SceneTypeFilter",
            "SceneCreatorFilter",
            "SceneSourceFilter",
            "SceneDateFilter",
            "SceneFileSizeFilter",
            "SceneStatusFilter"
        };

        /// <summary>
        /// Preset mode filters in their default order
        /// </summary>
        public static readonly string[] PresetFilters = 
        { 
            "PresetCategoryFilter",
            "PresetSubfolderFilter",
            "PresetDateFilter",
            "PresetFileSizeFilter",
            "PresetStatusFilter"
        };

        /// <summary>
        /// Gets the required filters for a specific content mode
        /// </summary>
        public static string[] GetRequiredFiltersForMode(string contentMode)
        {
            return contentMode switch
            {
                "Packages" => PackageFilters,
                "Scenes" => SceneFilters,
                "Presets" => PresetFilters,
                "Custom" => PresetFilters,
                _ => new string[0]
            };
        }

        /// <summary>
        /// Ensures all required filters are present in the given filter order list
        /// </summary>
        public static void EnsureFiltersInOrder(List<string> filterOrder, string[] requiredFilters)
        {
            if (filterOrder == null)
                return;

            foreach (var filter in requiredFilters)
            {
                if (!filterOrder.Contains(filter))
                {
                    filterOrder.Add(filter);
                }
            }
        }

        /// <summary>
        /// Validates that all required filters are present in the order list
        /// </summary>
        public static bool ValidateFilterOrder(List<string> filterOrder, string[] requiredFilters)
        {
            if (filterOrder == null)
                return false;

            foreach (var filter in requiredFilters)
            {
                if (!filterOrder.Contains(filter))
                    return false;
            }

            return true;
        }
    }
}
