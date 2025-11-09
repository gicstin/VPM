using System;

namespace VPM.Models
{
    /// <summary>
    /// Lightweight, optimized PackageItem for fast DataGrid loading
    /// Removes INotifyPropertyChanged and computed properties for maximum performance
    /// </summary>
    public class FastPackageItem
    {
        // Simple properties - no change notifications, no computed properties
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Creator { get; set; } = "";
        public long FileSize { get; set; } = 0;
        public DateTime? ModifiedDate { get; set; } = null;
        public bool IsLatestVersion { get; set; } = true;
        public int DependencyCount { get; set; } = 0;
        public int DependentsCount { get; set; } = 0;
        public bool IsOptimized { get; set; } = false;

        // Pre-computed display strings (set once, never change)
        public string DisplayName { get; set; } = "";
        public string FileSizeFormatted { get; set; } = "";
        public string DateFormatted { get; set; } = "";
        public string StatusIcon { get; set; } = "";
        
        // Simple status color as string (faster than Color objects)
        public string StatusColorHex { get; set; } = "#9E9E9E";

        /// <summary>
        /// Creates a FastPackageItem with pre-computed display values
        /// </summary>
        public static FastPackageItem Create(string name, string status, string creator, 
            long fileSize, DateTime? modifiedDate, bool isLatestVersion, int dependencyCount, int dependentsCount = 0, bool isOptimized = false)
        {
            return new FastPackageItem
            {
                Name = name,
                Status = status,
                Creator = creator,
                FileSize = fileSize,
                ModifiedDate = modifiedDate,
                IsLatestVersion = isLatestVersion,
                DependencyCount = dependencyCount,
                DependentsCount = dependentsCount,
                IsOptimized = isOptimized,
                
                // Pre-compute all display values
                DisplayName = name,
                FileSizeFormatted = PackageItem.FormatFileSize(fileSize),
                DateFormatted = modifiedDate?.ToString("MMM dd, yyyy") ?? "Unknown",
                StatusIcon = GetStatusIcon(status),
                StatusColorHex = GetStatusColorHex(status)
            };
        }



        private static string GetStatusIcon(string status)
        {
            return status switch
            {
                "Loaded" => "✓",
                "Available" => "📦",
                "Missing" => "✗",
                "Outdated" => "⚠",
                "Updating" => "↻",
                "Duplicate" => "!",
                _ => "?"
            };
        }

        private static string GetStatusColorHex(string status)
        {
            return status switch
            {
                "Loaded" => "#4CAF50",     // Green
                "Available" => "#2196F3",  // Blue
                "Missing" => "#F44336",    // Red
                "Outdated" => "#FF9800",   // Orange
                "Updating" => "#9C27B0",   // Purple
                "Duplicate" => "#FFEB3B",  // Yellow
                _ => "#9E9E9E"             // Gray
            };
        }
    }
}

