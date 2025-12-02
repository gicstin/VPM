using System;
using System.Collections.Generic;

namespace VPM.Models
{
    [Serializable]
    public class VarMetadata
    {
        public string Filename { get; set; } = "";
        public string PackageName { get; set; } = "";
        public string CreatorName { get; set; } = "";
        public string Description { get; set; } = "";
        public int Version { get; set; } = 1;
        public string LicenseType { get; set; } = "";
        public List<string> Dependencies { get; set; } = new List<string>();
        public List<string> ContentList { get; set; } = new List<string>();
        public HashSet<string> ContentTypes { get; set; } = new HashSet<string>();
        public HashSet<string> Categories { get; set; } = new HashSet<string>();
        public int FileCount { get; set; } = 0;
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public List<string> UserTags { get; set; } = new List<string>();
        public bool IsCorrupted { get; set; } = false;
        public bool PreloadMorphs { get; set; } = false;
        public bool IsMorphAsset { get; set; } = false;
        public string Status { get; set; } = "Unknown"; // Loaded, Available, Missing, etc.
        public string FilePath { get; set; } = "";
        public long FileSize { get; set; } = 0;
        
        // Optimization tracking
        public bool IsOptimized { get; set; } = false;
        public bool HasTextureOptimization { get; set; } = false;
        public bool HasHairOptimization { get; set; } = false;
        public bool HasMirrorOptimization { get; set; } = false;
        public bool HasJsonMinification { get; set; } = false;

        // Snapshot helpers
        public string VariantRole { get; set; } = "Unknown"; // Loaded, Available, Archived, Duplicate

        // Duplicate tracking
        public bool IsDuplicate { get; set; } = false;
        public int DuplicateLocationCount { get; set; } = 1;

        // Version tracking
        public bool IsOldVersion { get; set; } = false;
        public int LatestVersionNumber { get; set; } = 1;
        public string PackageBaseName { get; set; } = "";

        // Integrity tracking
        public bool IsDamaged { get; set; } = false;
        public string DamageReason { get; set; } = "";

        public int MorphCount { get; set; } = 0;
        public int HairCount { get; set; } = 0;
        public int ClothingCount { get; set; } = 0;
        public int SceneCount { get; set; } = 0;
        public int LooksCount { get; set; } = 0;
        public int PosesCount { get; set; } = 0;
        public int AssetsCount { get; set; } = 0;
        public int ScriptsCount { get; set; } = 0;
        public int PluginsCount { get; set; } = 0;
        public int SubScenesCount { get; set; } = 0;
        public int SkinsCount { get; set; } = 0;
        
        // Complete file index from archive - used for UI display and expansion
        public List<string> AllFiles { get; set; } = new List<string>();
        
        // Missing dependencies tracking
        public List<string> MissingDependencies { get; set; } = new List<string>();
        public bool HasMissingDependencies => MissingDependencies?.Count > 0;
        public int MissingDependencyCount => MissingDependencies?.Count ?? 0;
    }
}

