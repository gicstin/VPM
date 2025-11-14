using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VPM.Models
{
    /// <summary>
    /// Root structure for the JSON package database
    /// Key: Creator name (e.g., "Oeshii")
    /// Value: Dictionary of packages by that creator
    /// </summary>
    public class JsonPackageDatabase
    {
        [JsonExtensionData]
        public Dictionary<string, object> ExtensionData { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Information about a single package
    /// </summary>
    public class JsonPackageInfo
    {
        /// <summary>
        /// Full filename of the package (e.g., "Oeshii.LatteHair.1.var")
        /// </summary>
        [JsonPropertyName("filename")]
        public string Filename { get; set; }

        /// <summary>
        /// Download sources for this package
        /// </summary>
        [JsonPropertyName("sources")]
        public JsonPackageSources Sources { get; set; }
    }

    /// <summary>
    /// Download sources for a package
    /// </summary>
    public class JsonPackageSources
    {
        /// <summary>
        /// Hub.virtamate.com download URLs (relative paths)
        /// Base URL: https://hub.virtamate.com
        /// </summary>
        [JsonPropertyName("hub")]
        public List<string> Hub { get; set; }

        /// <summary>
        /// Pixeldrain download URLs (relative paths)
        /// Base URL: https://pixeldrain.com
        /// </summary>
        [JsonPropertyName("pdr")]
        public List<string> Pdr { get; set; }
    }

    /// <summary>
    /// Flattened package entry for easier processing
    /// </summary>
    public class FlatPackageEntry
    {
        /// <summary>
        /// Creator name (e.g., "Oeshii")
        /// </summary>
        public string Creator { get; set; }

        /// <summary>
        /// Package key (e.g., "LatteHair.1")
        /// </summary>
        public string PackageKey { get; set; }

        /// <summary>
        /// Full package name without .var extension (e.g., "Oeshii.LatteHair.1")
        /// </summary>
        public string FullPackageName { get; set; }

        /// <summary>
        /// Full filename with .var extension (e.g., "Oeshii.LatteHair.1.var")
        /// </summary>
        public string Filename { get; set; }

        /// <summary>
        /// Complete Hub URLs (e.g., "https://hub.virtamate.com/resources/54783/version/73458/download?file=458468")
        /// </summary>
        public List<string> HubUrls { get; set; }

        /// <summary>
        /// Complete Pixeldrain URLs (e.g., "https://pixeldrain.com/api/file/fcHT7AAH/info/zip/Oeshii.LatteHair.1.var")
        /// </summary>
        public List<string> PdrUrls { get; set; }

        /// <summary>
        /// All download URLs combined (Hub + Pixeldrain)
        /// </summary>
        public List<string> AllUrls { get; set; }

        /// <summary>
        /// Primary download URL (first available URL)
        /// </summary>
        public string PrimaryUrl { get; set; }
    }
}

