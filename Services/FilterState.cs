using System;
using System.Collections.Generic;
using VPM.Models;

namespace VPM.Services
{
    public class FilterState
    {
        public string SearchText { get; set; }
        public string[] SearchTerms { get; set; }
        public bool HideArchivedPackages { get; set; }
        public string SelectedStatus { get; set; }
        public HashSet<string> SelectedStatuses { get; set; }
        public HashSet<string> SelectedFavoriteStatuses { get; set; }
        public HashSet<string> SelectedVersionStatuses { get; set; }
        public string SelectedCategory { get; set; }
        public HashSet<string> SelectedCategories { get; set; }
        public string SelectedCreator { get; set; }
        public HashSet<string> SelectedCreators { get; set; }
        public string SelectedLicenseType { get; set; }
        public HashSet<string> SelectedLicenseTypes { get; set; }
        public HashSet<string> SelectedFileSizeRanges { get; set; }
        public HashSet<string> SelectedSubfolders { get; set; }
        public string SelectedDamagedFilter { get; set; }
        public bool FilterDuplicates { get; set; }
        public bool FilterNoDependents { get; set; }
        public bool FilterNoDependencies { get; set; }
        public bool FilterCustomDependents { get; set; }
        public DateFilter DateFilter { get; set; }
        public FavoritesManager FavoritesManager { get; set; }
        public Func<VarMetadata, bool> HasCustomDependentsFunc { get; set; }
        public double FileSizeTinyMax { get; set; }
        public double FileSizeSmallMax { get; set; }
        public double FileSizeMediumMax { get; set; }
        
        // Content tag filtering (clothing and hair)
        public HashSet<string> SelectedClothingTags { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectedHairTags { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// If true, package must match ALL selected tags (AND logic).
        /// If false, package must match ANY selected tag (OR logic).
        /// </summary>
        public bool RequireAllTags { get; set; } = false;

        // External destination filtering
        public HashSet<string> SelectedDestinations { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Playlist filtering
        public HashSet<string> SelectedPlaylistFilters { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Lookup for base package key => playlist tags (e.g. "P1 P2")
        public IReadOnlyDictionary<string, string> PlaylistTagsCache { get; set; }

        // VPB ratings ("5"…"0") and tags: OR inside each group, AND between them.
        public HashSet<string> SelectedVpbRatings { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectedVpbTags { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"Untagged" was picked in the tag filter: match packages carrying no VPB tag at all.</summary>
        public bool VpbUntaggedOnly { get; set; }

        /// <summary>Ratings and tags VPB currently knows about, keyed by package uid.</summary>
        public VPM.Services.Vpb.VpbLibraryData VpbData { get; set; }

        public VPM.Services.Vpb.VpbLookData VpbLookData { get; set; }

        public HashSet<string> SelectedVpbLookSubjects { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool VpbLookUnmatchedOnly { get; set; }

        public bool VpbLookSearchEnabled { get; set; }

        public bool VpbLookTagSearchEnabled { get; set; }

        public HashSet<string> SelectedVpbHubCategories { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool VpbHubUncategorizedOnly { get; set; }

        public HashSet<string> SelectedVpbHubTags { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool VpbHubUntaggedOnly { get; set; }

        public bool VpbHubCategoryOverrideEnabled { get; set; }
    }
}
