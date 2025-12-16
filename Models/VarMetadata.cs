using System;
using System.Collections.Generic;
using VPM.Services;

namespace VPM.Models
{
    [Serializable]
    public class VarMetadata
    {
        // Backing fields for lazy-initialized collections
        // This saves ~200 bytes per instance when collections are empty
        private string[] _dependencies;
        private string[] _contentList;
        private string[] _contentTypes;
        private string[] _categories;
        private string[] _userTags;
        private string[] _allFiles;
        private string[] _missingDependencies;
        private string[] _clothingTags;
        private string[] _hairTags;
        
        public string Filename { get; set; } = "";
        public string PackageName { get; set; } = "";
        public string CreatorName { get; set; } = "";
        public string Description { get; set; } = "";
        public int Version { get; set; } = 1;
        public string LicenseType { get; set; } = "";
        
        // Lazy-initialized collections - only allocate when needed
        public string[] Dependencies 
        { 
            get => _dependencies ??= Array.Empty<string>();
            set => _dependencies = value;
        }
        
        public string[] ContentList 
        { 
            get => _contentList ??= Array.Empty<string>();
            set => _contentList = value;
        }
        
        public string[] ContentTypes 
        { 
            get => _contentTypes ??= Array.Empty<string>();
            set => _contentTypes = value;
        }
        
        public string[] Categories 
        { 
            get => _categories ??= Array.Empty<string>();
            set => _categories = value;
        }
        
        public int FileCount { get; set; } = 0;
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        
        public string[] UserTags 
        { 
            get => _userTags ??= Array.Empty<string>();
            set => _userTags = value;
        }
        
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
        public string[] AllFiles 
        { 
            get => _allFiles ??= Array.Empty<string>();
            set => _allFiles = value;
        }
        
        // Missing dependencies tracking
        public string[] MissingDependencies 
        { 
            get => _missingDependencies ??= Array.Empty<string>();
            set => _missingDependencies = value;
        }
        
        public bool HasMissingDependencies => _missingDependencies?.Length > 0;
        public int MissingDependencyCount => _missingDependencies?.Length ?? 0;

        // Dependency and Dependents tracking
        public int DependencyCount { get; set; } = 0;  // Number of packages this one depends on
        public int DependentsCount { get; set; } = 0;  // Number of packages that depend on this one

        // External destination tracking
        public string ExternalDestinationName { get; set; } = "";  // Name of the external destination (e.g., "Backup")
        public string ExternalDestinationColorHex { get; set; } = "";  // Color hex for the external destination
        public string ExternalDestinationSubfolder { get; set; } = "";  // Subfolder within destination (e.g., "Backup/Archive")
        public string OriginalExternalDestinationName { get; set; } = "";  // Original destination name if remapped (for nested destinations)
        public string OriginalExternalDestinationColorHex { get; set; } = "";  // Original destination color if remapped (for nested destinations)
        public bool IsExternal => !string.IsNullOrEmpty(ExternalDestinationName);

        // Content tags extracted from .vam files (clothing and hair)
        // Tags are comma-separated strings like "head,torso,dress,formal"
        public string[] ClothingTags 
        { 
            get => _clothingTags ??= Array.Empty<string>();
            set => _clothingTags = value;
        }
        
        public string[] HairTags 
        { 
            get => _hairTags ??= Array.Empty<string>();
            set => _hairTags = value;
        }
        
        /// <summary>
        /// Returns true if this package has any clothing tags
        /// </summary>
        public bool HasClothingTags => _clothingTags?.Length > 0;
        
        /// <summary>
        /// Returns true if this package has any hair tags
        /// </summary>
        public bool HasHairTags => _hairTags?.Length > 0;
        
        /// <summary>
        /// Returns true if this package has any content tags (clothing or hair)
        /// </summary>
        public bool HasContentTags => HasClothingTags || HasHairTags;
        
        /// <summary>
        /// Trims excess capacity from all collections to reduce memory usage.
        /// Call after populating metadata to reclaim unused array space.
        /// </summary>
        public void TrimExcess()
        {
            // Arrays are fixed size, no need to trim
        }
        
        /// <summary>
        /// Clears collections that are no longer needed after initial processing.
        /// Call this to free memory for data that's been processed.
        /// </summary>
        public void ClearTransientData()
        {
            // ContentList is typically only needed during parsing
            _contentList = null;
        }
    }
}

