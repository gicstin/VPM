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
        public HashSet<string> SelectedAutoInstallStatuses { get; set; }
        public HashSet<string> SelectedOptimizationStatuses { get; set; }
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
        public DateFilter DateFilter { get; set; }
        public FavoritesManager FavoritesManager { get; set; }
        public AutoInstallManager AutoInstallManager { get; set; }
        public double FileSizeTinyMax { get; set; }
        public double FileSizeSmallMax { get; set; }
        public double FileSizeMediumMax { get; set; }
    }
}
