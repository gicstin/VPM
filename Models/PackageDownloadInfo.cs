using System;
using System.Collections.Generic;

namespace VPM.Models
{
    /// <summary>
    /// Represents download information for a package
    /// Simple format with just package name and download URL
    /// </summary>
    public class PackageDownloadInfo
    {
        /// <summary>
        /// Package name (e.g., "Creator.PackageName.Version")
        /// </summary>
        public string PackageName { get; set; } = "";

        /// <summary>
        /// Direct download URL for the package (primary URL)
        /// </summary>
        public string DownloadUrl { get; set; } = "";
        
        /// <summary>
        /// Hub download URLs (try these first)
        /// </summary>
        public List<string> HubUrls { get; set; } = new List<string>();
        
        /// <summary>
        /// Pixeldrain download URLs (fallback)
        /// </summary>
        public List<string> PdrUrls { get; set; } = new List<string>();
    }
}

