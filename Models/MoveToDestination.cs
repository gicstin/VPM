using System;

namespace VPM.Models
{
    /// <summary>
    /// Represents a configured destination path for the "Move To" feature.
    /// Allows users to quickly move packages to predefined locations.
    /// </summary>
    public class MoveToDestination
    {
        /// <summary>
        /// Display name for the destination (shown in context menu)
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Full path to the destination folder
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Optional description for the destination
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Whether this destination is enabled and should appear in the menu
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Order in which this destination appears in the menu (lower = higher)
        /// </summary>
        public int SortOrder { get; set; } = 0;

        /// <summary>
        /// Creates a new empty MoveToDestination
        /// </summary>
        public MoveToDestination()
        {
        }

        /// <summary>
        /// Creates a new MoveToDestination with the specified name and path
        /// </summary>
        public MoveToDestination(string name, string path)
        {
            Name = name ?? string.Empty;
            Path = path ?? string.Empty;
        }

        /// <summary>
        /// Validates that the destination has required fields
        /// </summary>
        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Path);
        }

        /// <summary>
        /// Checks if the destination path exists
        /// </summary>
        public bool PathExists()
        {
            try
            {
                return System.IO.Directory.Exists(Path);
            }
            catch
            {
                return false;
            }
        }

        public override string ToString()
        {
            return $"{Name} ({Path})";
        }
    }
}
