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
        private List<string> _dependencies;
        private List<string> _contentList;
        private HashSet<string> _contentTypes;
        private HashSet<string> _categories;
        private List<string> _userTags;
        private List<string> _allFiles;
        private List<string> _missingDependencies;
        private HashSet<string> _clothingTags;
        private HashSet<string> _hairTags;
        
        public string Filename { get; set; } = "";
        public string PackageName { get; set; } = "";
        public string CreatorName { get; set; } = "";
        public string Description { get; set; } = "";
        public int Version { get; set; } = 1;
        public string LicenseType { get; set; } = "";
        
        // Lazy-initialized collections - only allocate when needed
        public List<string> Dependencies 
        { 
            get => _dependencies ??= new List<string>();
            set => _dependencies = value;
        }
        
        public List<string> ContentList 
        { 
            get => _contentList ??= new List<string>();
            set => _contentList = value;
        }
        
        public HashSet<string> ContentTypes 
        { 
            get => _contentTypes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set => _contentTypes = value;
        }
        
        public HashSet<string> Categories 
        { 
            // Initialize with capacity 2 since most packages have 1-2 categories
            get => _categories ??= new HashSet<string>(2, StringComparer.OrdinalIgnoreCase);
            set => _categories = value;
        }
        
        public int FileCount { get; set; } = 0;
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        
        public List<string> UserTags 
        { 
            get => _userTags ??= new List<string>();
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
        public List<string> AllFiles 
        { 
            get => _allFiles ??= new List<string>();
            set => _allFiles = value;
        }
        
        // Missing dependencies tracking
        public List<string> MissingDependencies 
        { 
            get => _missingDependencies ??= new List<string>();
            set => _missingDependencies = value;
        }
        
        public bool HasMissingDependencies => _missingDependencies?.Count > 0;
        public int MissingDependencyCount => _missingDependencies?.Count ?? 0;

        // Dependency and Dependents tracking
        public int DependencyCount { get; set; } = 0;  // Number of packages this one depends on
        public int DependentsCount { get; set; } = 0;  // Number of packages that depend on this one

        // Content tags extracted from .vam files (clothing and hair)
        // Tags are comma-separated strings like "head,torso,dress,formal"
        public HashSet<string> ClothingTags 
        { 
            get => _clothingTags ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set => _clothingTags = value;
        }
        
        public HashSet<string> HairTags 
        { 
            get => _hairTags ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set => _hairTags = value;
        }
        
        /// <summary>
        /// Returns true if this package has any clothing tags
        /// </summary>
        public bool HasClothingTags => _clothingTags?.Count > 0;
        
        /// <summary>
        /// Returns true if this package has any hair tags
        /// </summary>
        public bool HasHairTags => _hairTags?.Count > 0;
        
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
            _dependencies?.TrimExcess();
            _contentList?.TrimExcess();
            _contentTypes?.TrimExcess();
            _userTags?.TrimExcess();
            _allFiles?.TrimExcess();
            _missingDependencies?.TrimExcess();
            _clothingTags?.TrimExcess();
            _hairTags?.TrimExcess();
        }
        
        /// <summary>
        /// Clears collections that are no longer needed after initial processing.
        /// Call this to free memory for data that's been processed.
        /// </summary>
        public void ClearTransientData()
        {
            // ContentList is typically only needed during parsing
            _contentList?.Clear();
            _contentList = null;
        }
    }
}

