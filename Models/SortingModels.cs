using System;
using System.ComponentModel;

namespace VPM.Models
{
    /// <summary>
    /// Sorting options for the main package table
    /// </summary>
    public enum PackageSortOption
    {
        [Description("Name")]
        Name,
        [Description("Date")]
        Date,
        [Description("Size")]
        Size,
        [Description("Dependencies")]
        Dependencies,
        [Description("Dependents")]
        Dependents,
        [Description("Status")]
        Status,
        [Description("Morphs")]
        Morphs,
        [Description("Hair")]
        Hair,
        [Description("Clothing")]
        Clothing,
        [Description("Scenes")]
        Scenes,
        [Description("Looks")]
        Looks,
        [Description("Poses")]
        Poses,
        [Description("Assets")]
        Assets,
        [Description("Scripts")]
        Scripts,
        [Description("Plugins")]
        Plugins,
        [Description("SubScenes")]
        SubScenes,
        [Description("Skins")]
        Skins
    }

    /// <summary>
    /// Sorting options for the scene table
    /// </summary>
    public enum SceneSortOption
    {
        [Description("Name")]
        Name,
        [Description("Date")]
        Date,
        [Description("Size")]
        Size,
        [Description("Dependencies")]
        Dependencies,
        [Description("Atoms")]
        Atoms
    }

    /// <summary>
    /// Sorting options for the dependencies table
    /// </summary>
    public enum DependencySortOption
    {
        [Description("Name")]
        Name,
        [Description("Status")]
        Status
    }

    /// <summary>
    /// Sorting options for filter lists
    /// </summary>
    public enum FilterSortOption
    {
        [Description("Name")]
        Name,
        [Description("Count")]
        Count
    }

    /// <summary>
    /// Current sorting state for a table
    /// </summary>
    public class SortingState
    {
        public object CurrentSortOption { get; set; }
        public bool IsAscending { get; set; } = true;
        public DateTime LastSortTime { get; set; } = DateTime.Now;

        public SortingState()
        {
        }

        public SortingState(object sortOption, bool isAscending = true)
        {
            CurrentSortOption = sortOption;
            IsAscending = isAscending;
            LastSortTime = DateTime.Now;
        }
    }

    /// <summary>
    /// Serializable sorting state for persistence
    /// </summary>
    public class SerializableSortingState
    {
        public string SortOptionType { get; set; }
        public string SortOptionValue { get; set; }
        public bool IsAscending { get; set; } = true;

        public SerializableSortingState()
        {
        }

        public SerializableSortingState(string sortOptionType, string sortOptionValue, bool isAscending)
        {
            SortOptionType = sortOptionType;
            SortOptionValue = sortOptionValue;
            IsAscending = isAscending;
        }
    }

    /// <summary>
    /// Extension methods for sorting enums
    /// </summary>
    public static class SortingExtensions
    {
        public static string GetDescription(this Enum value)
        {
            var field = value.GetType().GetField(value.ToString());
            var attribute = (DescriptionAttribute)Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute));
            return attribute?.Description ?? value.ToString();
        }

        public static string GetDisplayText(this Enum value, bool isAscending)
        {
            var baseDescription = value.GetDescription();
            var direction = isAscending ? "↑" : "↓";
            return $"{baseDescription} {direction}";
        }
    }
}

