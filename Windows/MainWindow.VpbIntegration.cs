using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VPM.Models;
using VPM.Services;
using VPM.Services.Vpb;
using VPM.Windows;

namespace VPM
{
    /// <summary>VPB ratings and tags in the package grid and filters. VPB is the sole writer; VPM mirrors it.</summary>
    public partial class MainWindow
    {
        private const string VpbUnratedLabel = "Unrated";
        private const string VpbUntaggedLabel = "Untagged";
        private const string VpbLookUnmatchedLabel = "Unmatched";
        private const string VpbHubUncategorizedLabel = "No hub category";
        private const string VpbHubUntaggedLabel = "No hub tags";

        private VpbLibraryData _vpbData = VpbLibraryData.Empty;
        private VpbLookData _vpbLookData = VpbLookData.Empty;
        private bool _vpbDataLoaded;
        private bool _vpbRatingWarningAccepted;
        private Dictionary<string, int> _vpbTagFilterCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _vpbUntaggedCount;
        private Dictionary<string, int> _vpbLookFilterCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _vpbLookUnmatchedCount;
        private Dictionary<string, int> _vpbHubCategoryFilterCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _vpbHubUncategorizedCount;
        private Dictionary<string, int> _vpbHubTagFilterCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _vpbHubUntaggedCount;
        private int _vpbToolbarCheckGen;

        /// <summary>Local VPB.dll presence sets gold Install VPB vs Patch VaM; GitHub check may flip to Update VPB.</summary>
        private void RefreshVpbToolbarChrome()
        {
            var installed = VpbPresence.IsPluginInstalled(_selectedFolder);

            var pinned = false;
            string pinnedVersion = null;
            if (installed && !string.IsNullOrEmpty(_selectedFolder))
            {
                try
                {
                    var config = VpbUpdateConfigFile.Load(_selectedFolder);
                    pinned = config.Pinned;
                    pinnedVersion = config.PinnedVersion;
                }
                catch
                {
                }
            }

            ApplyVpbToolbarState(installed, updateAvailable: false, pinned, pinnedVersion);

            var gen = Interlocked.Increment(ref _vpbToolbarCheckGen);
            _ = RefreshVpbToolbarUpdateStateAsync(gen);
        }

        internal void ApplyVpbToolbarFromCheck(VpbPluginCheckResult check)
        {
            if (check == null)
            {
                RefreshVpbToolbarChrome();
                return;
            }

            ApplyVpbToolbarState(check.IsInstalled, check.IsUpdateAvailable, check.IsPinned, check.PinnedVersion);
        }

        private void ApplyVpbToolbarState(bool installed, bool updateAvailable, bool pinned = false, string pinnedVersion = null)
        {
            if (VpbPatchToolbarButton == null) return;

            bool gold;
            string content;
            string tooltip;
            if (!installed)
            {
                gold = true;
                content = "🧩 Install VPB";
                tooltip = "Install VPB plugin for VaM";
            }
            else if (pinned)
            {
                gold = false;
                content = "🧩 VPB pinned";
                tooltip = string.IsNullOrWhiteSpace(pinnedVersion)
                    ? "Pinned to an earlier VPB build — open to manage versions"
                    : $"Pinned to VPB {pinnedVersion} — open to manage versions";
            }
            else if (updateAvailable)
            {
                gold = false;
                content = "🧩 Update VPB";
                tooltip = "Update VPB plugin";
            }
            else
            {
                gold = false;
                content = "🧩 Patch VaM";
                tooltip = "Open VPB patcher";
            }

            VpbPatchToolbarButton.Content = content;
            VpbPatchToolbarButton.ToolTip = tooltip;

            var styleKey = gold ? "VamHubShimmerButtonStyle" : "BlueHoverButtonStyle";
            try
            {
                VpbPatchToolbarButton.Style = (Style)FindResource(styleKey);
            }
            catch
            {
            }
        }

        private async Task RefreshVpbToolbarUpdateStateAsync(int gen)
        {
            if (string.IsNullOrEmpty(_selectedFolder)) return;
            if (!VpbPresence.IsPluginInstalled(_selectedFolder)) return;

            VpbPluginCheckResult check = null;
            try
            {
                var folder = _selectedFolder;
                var branch = _settingsManager?.Settings?.VpbPreferredBranch is { Length: > 0 } b ? b : "main";
                check = await Task.Run(async () =>
                {
                    using var checker = new VpbPluginChecker();
                    return await checker.CheckAsync(folder, branch).ConfigureAwait(false);
                }).ConfigureAwait(true);
            }
            catch
            {
                return;
            }

            if (gen != _vpbToolbarCheckGen) return;
            if (check == null) return;

            ApplyVpbToolbarFromCheck(check);
        }

        /// <summary>Loads VPB data once if a rebuild is reached without a rescan, so rows are never blank just because the user changed sort or filters instead of refreshing.</summary>
        private void EnsureVpbData()
        {
            if (_vpbDataLoaded) return;
            RefreshVpbData();
        }

        /// <summary>Re-reads VPB's ratings and tags and pushes them into cached rows and the filter manager.</summary>
        private void RefreshVpbData()
        {
            _vpbDataLoaded = true;
            try
            {
                _vpbData = VpbLibraryData.Load(_selectedFolder);
            }
            catch
            {
                _vpbData = VpbLibraryData.Empty;
            }

            try
            {
                _vpbLookData = VpbLookData.Load(
                    _selectedFolder,
                    _settingsManager?.Settings?.VpbHubTagsEnabled ?? true);
            }
            catch
            {
                _vpbLookData = VpbLookData.Empty;
            }

            if (_filterManager != null)
            {
                _filterManager.VpbData = _vpbData;
                _filterManager.VpbLookData = _vpbLookData;
                ApplyVpbLookSearchSettings();
            }

            if (_packageManager?.PackageMetadata != null)
            {
                foreach (var item in _packageItemCache.Values)
                {
                    if (item == null) continue;
                    if (_packageManager.PackageMetadata.TryGetValue(item.MetadataKey, out var metadata))
                        ApplyVpbToItem(item, metadata);
                }
            }

            if (CustomAtomItems != null)
            {
                foreach (var item in CustomAtomItems)
                    ApplyVpbToCustomItem(item);
            }
        }

        private void ApplyVpbToItem(PackageItem item, VarMetadata metadata)
        {
            if (item == null) return;
            var uid = VpbLibraryData.UidFor(metadata);
            item.VpbRating = _vpbData.RatingFor(uid);
            item.IsVpbRatingInherited = _vpbData.IsRatingInherited(uid);
            item.VpbTags = _vpbData.TagsDisplayFor(uid);
            item.VpbLookSubject = _vpbLookData.SubjectFor(uid);
            item.VpbLookDetails = _vpbLookData.DetailsFor(uid);
            item.VpbHubTags = _vpbLookData.HubTagsDisplayFor(uid);
        }

        private void ApplyVpbLookSearchSettings()
        {
            if (_filterManager == null) return;
            var settings = _settingsManager?.Settings;
            _filterManager.VpbLookSearchEnabled = settings?.VpbLookSearchEnabled ?? true;
            _filterManager.VpbLookTagSearchEnabled = settings?.VpbLookTagSearchEnabled ?? false;
            _filterManager.VpbHubCategoryOverrideEnabled = settings?.VpbHubCategoryOverrideEnabled ?? true;
            UpdateVpbLookSearchTooltip();
        }

        private string VpbDataPackTooltip(string lead)
        {
            var look = _vpbLookData ?? VpbLookData.Empty;

            var sb = new System.Text.StringBuilder(lead);
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(look.HasAnything
                ? $"{look.CoveredPackageCount:N0} packages matched by VPB's data packs."
                : "No data packs applied yet — apply them in VPB's gallery settings.");

            var provenance = look.ProvenanceBlock();
            if (provenance.Length > 0) sb.Append('\n').Append(provenance);

            var attribution = look.AttributionLine();
            if (attribution.Length > 0) sb.Append('\n').Append(attribution);

            return sb.ToString();
        }

        private void UpdateVpbLookSearchTooltip()
        {
            if (VpbLookSearchMenuItem != null)
            {
                VpbLookSearchMenuItem.ToolTip = VpbDataPackTooltip(
                    "Search the \"this looks like\" subject and category VPB's Look-A-Pedia data pack matched to "
                    + "each package, so searching Jinx finds a look named Kinx.");
            }

            if (VpbLookFilterExpandedGrid != null)
            {
                VpbLookFilterExpandedGrid.ToolTip = VpbDataPackTooltip(
                    "Filter by the \"this looks like\" subject Look-A-Pedia matched to each package.");
            }

            if (VpbHubCategoryFilterExpandedGrid != null)
            {
                VpbHubCategoryFilterExpandedGrid.ToolTip = VpbDataPackTooltip(
                    "Filter by the category the creator uploaded the var under on the Hub — so Scenes means "
                    + "real scenes, and a clothing var shipped inside a scene still reads as Clothing.");
            }

            if (VpbHubTagFilterExpandedGrid != null)
            {
                VpbHubTagFilterExpandedGrid.ToolTip = VpbDataPackTooltip(
                    "Filter by the tags the creator put on the Hub resource this package came from.");
            }
        }

        private void ToggleVpbLookSearch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (_settingsManager?.Settings == null) return;

            _settingsManager.Settings.VpbLookSearchEnabled = menuItem.IsChecked;
            ApplyVpbLookSearchSettings();
            ReapplyFiltersForCurrentMode();

            if (menuItem.IsChecked && !(_vpbLookData?.HasAnything ?? false))
            {
                CustomMessageBox.Show(
                    "VPB has no Look-A-Pedia data applied yet, so searching by \"this looks like\" will not change "
                    + "results.\n\nIn VaM, open VPB's gallery settings and apply the Look-A-Pedia data pack, then "
                    + "refresh VPM.",
                    "Look-A-Pedia Data Not Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void ToggleVpbLookTagSearch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (_settingsManager?.Settings == null) return;

            _settingsManager.Settings.VpbLookTagSearchEnabled = menuItem.IsChecked;
            ApplyVpbLookSearchSettings();
            ReapplyFiltersForCurrentMode();
        }

        /// <summary>Hub tags are a whole tag category — off means VPM does not load them at all.</summary>
        private void ToggleVpbHubTags_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (_settingsManager?.Settings == null) return;

            _settingsManager.Settings.VpbHubTagsEnabled = menuItem.IsChecked;

            if (!menuItem.IsChecked && _filterManager != null)
            {
                _filterManager.SelectedVpbHubTags.Clear();
                _filterManager.VpbHubUntaggedOnly = false;
            }

            SetPackageFilterSectionsForMode();
            RefreshVpbDataAndLists();

            SetStatus(menuItem.IsChecked
                ? "Hub tags on — the creator's tags show as a read-only category"
                : "Hub tags off");
        }

        private void ToggleVpbHubCategoryOverride_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (_settingsManager?.Settings == null) return;

            _settingsManager.Settings.VpbHubCategoryOverrideEnabled = menuItem.IsChecked;
            ApplyVpbLookSearchSettings();
            ReapplyFiltersForCurrentMode();

            if (menuItem.IsChecked && !(_vpbLookData?.HasAnything ?? false))
            {
                CustomMessageBox.Show(
                    "VPB has no hub category data applied yet, so categories will keep coming from what each var "
                    + "contains.\n\nIn VaM, open VPB's gallery settings and apply the hub tags data pack, then refresh VPM.",
                    "Hub Category Data Not Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            SetStatus(menuItem.IsChecked
                ? "Categories now follow the creator's Hub category"
                : "Categories back to what VPM detects from file contents");
        }

        private void HideHubTagsGlobally_Click(object sender, RoutedEventArgs e)
        {
            var tags = SelectedHubTagFilterValues();
            if (tags.Count == 0)
            {
                SetStatus("Select one or more hub tags first");
                return;
            }

            var result = VpbHubTagPrefWriter.HideGlobally(_selectedFolder, tags);
            if (!result.Success)
            {
                DarkMessageBox.Show(result.Message, "Hub tags", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshVpbDataAndLists();
            SetStatus($"Hid {tags.Count} hub tag(s) everywhere");
        }

        private void ShowAllHiddenHubTags_Click(object sender, RoutedEventArgs e)
        {
            var prefs = _vpbLookData?.TagPrefs ?? VpbHubTagPrefs.Empty;
            if (!prefs.Any)
            {
                SetStatus("No hub tags are hidden");
                return;
            }

            var answer = DarkMessageBox.Show(
                $"Unhide every hidden hub tag?\n\n{prefs.GlobalCount} hidden everywhere, "
                + $"{prefs.PackageRuleCount} hidden on single packages.",
                "Hub tags",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            var result = VpbHubTagPrefWriter.ShowAll(_selectedFolder, globalOnly: false);
            if (!result.Success)
            {
                DarkMessageBox.Show(result.Message, "Hub tags", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshVpbDataAndLists();
            SetStatus("All hub tags are visible again");
        }

        private List<string> SelectedHubTagFilterValues()
        {
            var tags = new List<string>();
            if (VpbHubTagFilterList?.SelectedItems == null) return tags;

            foreach (var item in VpbHubTagFilterList.SelectedItems)
            {
                var value = ExtractFilterValue(GetListBoxItemText(item));
                if (string.IsNullOrEmpty(value)) continue;
                if (string.Equals(value, VpbHubUntaggedLabel, StringComparison.OrdinalIgnoreCase)) continue;
                tags.Add(value);
            }
            return tags;
        }

        private bool ApplyHubTagPrefChanges(VpbTagEditorWindow dialog, IReadOnlyList<string> uids, out string failure)
        {
            failure = "";
            if (dialog == null || !dialog.HasHubTagChanges) return false;

            var writes = new List<VpbWriteResult>(4);
            if (dialog.HubTagsToHideEverywhere.Count > 0)
                writes.Add(VpbHubTagPrefWriter.HideGlobally(_selectedFolder, dialog.HubTagsToHideEverywhere));
            if (dialog.HubTagsToShowEverywhere.Count > 0)
                writes.Add(VpbHubTagPrefWriter.ShowGlobally(_selectedFolder, dialog.HubTagsToShowEverywhere));
            if (dialog.HubTagsToHideHere.Count > 0 && uids != null && uids.Count > 0)
                writes.Add(VpbHubTagPrefWriter.HideOnPackages(_selectedFolder, uids, dialog.HubTagsToHideHere));
            if (dialog.HubTagsToShowHere.Count > 0 && uids != null && uids.Count > 0)
                writes.Add(VpbHubTagPrefWriter.ShowOnPackages(_selectedFolder, uids, dialog.HubTagsToShowHere));

            foreach (var write in writes)
            {
                if (!write.Success)
                {
                    failure = write.Message;
                    return false;
                }
            }

            return writes.Count > 0;
        }

        private void ReapplyFiltersForCurrentMode()
        {
            if (_currentContentMode == "Custom")
                ApplyPresetFilters();
            else
                ApplyFilters();
        }

        private void ApplyVpbToCustomItem(CustomAtomItem item)
        {
            if (item == null) return;
            var key = VpbUserFileKeys.RelativeUid(_selectedFolder, item.FilePath);
            item.VpbRating = _vpbData.RatingForFile(key);
            item.IsVpbRatingInherited = false;
            item.VpbTags = _vpbData.TagsDisplayForFile(key);
        }

        private void VpbRatingFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Prevent recursion during programmatic updates
            if (_suppressSelectionEvents) return;

            if (_currentContentMode == "Custom")
                ApplyPresetFilters();
            else
                ApplyFilters();
            UpdateClearAllFiltersButtonVisibility();
        }

        private void VpbTagFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents) return;

            if (_currentContentMode == "Custom")
                ApplyPresetFilters();
            else
                ApplyFilters();
            UpdateVpbTagsClearButton();
            UpdateClearAllFiltersButtonVisibility();
        }

        private void VpbLookFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents) return;

            ApplyFilters();
            UpdateVpbLookClearButton();
            UpdateClearAllFiltersButtonVisibility();
        }

        private void VpbHubCategoryFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents) return;

            ApplyFilters();
            UpdateVpbHubCategoryClearButton();
            UpdateClearAllFiltersButtonVisibility();
        }

        private void VpbHubTagFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents) return;

            ApplyFilters();
            UpdateVpbHubTagClearButton();
            UpdateClearAllFiltersButtonVisibility();
        }

        /// <summary>Star runs and "Unrated" map to the rating buckets the filter matches on.</summary>
        private void CollectVpbRatingFilterSelections()
        {
            if (_filterManager == null) return;
            _filterManager.SelectedVpbRatings.Clear();
            if (VpbRatingFilterList?.SelectedItems == null) return;

            foreach (var item in VpbRatingFilterList.SelectedItems)
            {
                var value = ExtractFilterValue(GetListBoxItemText(item));
                if (string.IsNullOrEmpty(value)) continue;

                if (string.Equals(value, VpbUnratedLabel, StringComparison.OrdinalIgnoreCase))
                {
                    _filterManager.SelectedVpbRatings.Add("0");
                    continue;
                }

                int stars = CountStars(value);
                if (stars > 0) _filterManager.SelectedVpbRatings.Add(stars.ToString());
            }
        }

        private void CollectVpbLookFilterSelections()
        {
            if (_filterManager == null) return;
            _filterManager.SelectedVpbLookSubjects.Clear();
            _filterManager.VpbLookUnmatchedOnly = false;
            if (VpbLookFilterList?.SelectedItems == null) return;

            foreach (var item in VpbLookFilterList.SelectedItems)
            {
                var value = ExtractFilterValue(GetListBoxItemText(item));
                if (string.IsNullOrEmpty(value)) continue;

                if (string.Equals(value, VpbLookUnmatchedLabel, StringComparison.OrdinalIgnoreCase))
                {
                    _filterManager.VpbLookUnmatchedOnly = true;
                    continue;
                }

                _filterManager.SelectedVpbLookSubjects.Add(value);
            }
        }

        private void CollectVpbHubCategoryFilterSelections()
        {
            if (_filterManager == null) return;
            _filterManager.SelectedVpbHubCategories.Clear();
            _filterManager.VpbHubUncategorizedOnly = false;
            if (VpbHubCategoryFilterList?.SelectedItems == null) return;

            foreach (var item in VpbHubCategoryFilterList.SelectedItems)
            {
                var value = ExtractFilterValue(GetListBoxItemText(item));
                if (string.IsNullOrEmpty(value)) continue;

                if (string.Equals(value, VpbHubUncategorizedLabel, StringComparison.OrdinalIgnoreCase))
                {
                    _filterManager.VpbHubUncategorizedOnly = true;
                    continue;
                }

                _filterManager.SelectedVpbHubCategories.Add(value);
            }
        }

        private void CollectVpbHubTagFilterSelections()
        {
            if (_filterManager == null) return;
            _filterManager.SelectedVpbHubTags.Clear();
            _filterManager.VpbHubUntaggedOnly = false;
            if (VpbHubTagFilterList?.SelectedItems == null) return;

            foreach (var item in VpbHubTagFilterList.SelectedItems)
            {
                var value = ExtractFilterValue(GetListBoxItemText(item));
                if (string.IsNullOrEmpty(value)) continue;

                if (string.Equals(value, VpbHubUntaggedLabel, StringComparison.OrdinalIgnoreCase))
                {
                    _filterManager.VpbHubUntaggedOnly = true;
                    continue;
                }

                _filterManager.SelectedVpbHubTags.Add(value);
            }
        }

        private void CollectVpbTagFilterSelections()
        {
            if (_filterManager == null) return;
            _filterManager.SelectedVpbTags.Clear();
            _filterManager.VpbUntaggedOnly = false;
            if (VpbTagFilterList?.SelectedItems == null) return;

            foreach (var item in VpbTagFilterList.SelectedItems)
            {
                var value = ExtractFilterValue(GetListBoxItemText(item));
                if (string.IsNullOrEmpty(value)) continue;

                if (string.Equals(value, VpbUntaggedLabel, StringComparison.OrdinalIgnoreCase))
                {
                    _filterManager.VpbUntaggedOnly = true;
                    continue;
                }

                _filterManager.SelectedVpbTags.Add(value);
            }
        }

        private static int CountStars(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            foreach (var c in value)
            {
                if (c != '\u2605') return 0;
            }
            return value.Length;
        }

        #region Rating and tagging

        /// <summary>Inline stars committed a value. Applies to the whole selection if the row is in it.</summary>
        private void VpbStarStrip_RatingCommitted(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not VpbStarStrip strip) return;
            e.Handled = true;

            if (strip.DataContext is CustomAtomItem clickedCustom)
            {
                var selection = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>().ToList() ?? new List<CustomAtomItem>();
                var targets = selection.Contains(clickedCustom) && selection.Count > 1
                    ? selection
                    : new List<CustomAtomItem> { clickedCustom };
                ApplyVpbFileRating(targets, strip.CommittedRating);
                return;
            }

            if (strip.DataContext is not PackageItem clicked) return;

            var packageSelection = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList() ?? new List<PackageItem>();
            var packageTargets = packageSelection.Contains(clicked) && packageSelection.Count > 1
                ? packageSelection
                : new List<PackageItem> { clicked };

            ApplyVpbRating(packageTargets, strip.CommittedRating);
        }

        private void VpbRate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not int stars) return;

            if (_currentContentMode == "Custom")
            {
                var customTargets = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>().ToList();
                if (customTargets == null || customTargets.Count == 0) return;
                ApplyVpbFileRating(customTargets, stars);
                return;
            }

            var targets = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList();
            if (targets == null || targets.Count == 0) return;
            ApplyVpbRating(targets, stars);
        }

        private void ApplyVpbRating(IReadOnlyList<PackageItem> targets, int stars)
        {
            if (targets == null || targets.Count == 0) return;
            if (!ConfirmVpbRatingWriteWhileVamRuns()) return;

            var changes = new List<PackageRatingChange>(targets.Count);
            foreach (var item in targets)
            {
                if (item == null) continue;
                if (_packageManager?.PackageMetadata == null) continue;
                if (!_packageManager.PackageMetadata.TryGetValue(item.MetadataKey, out var metadata)) continue;

                var uid = VpbLibraryData.UidFor(metadata);
                if (uid.Length == 0) continue;

                changes.Add(new PackageRatingChange
                {
                    Uid = uid,
                    VarPathRelative = RelativeVarPath(metadata),
                    Stars = stars
                });
            }

            if (changes.Count == 0)
            {
                SetStatus("Nothing to rate in the current selection");
                return;
            }

            var result = VpbRatingWriter.SetRatings(_selectedFolder, changes);
            if (!result.Success)
            {
                DarkMessageBox.Show(result.Message, "VPB rating", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshVpbData();
            SetStatus(stars > 0
                ? $"Rated {result.PackagesChanged} package(s) {stars}/5 in VPB"
                : $"Cleared the VPB rating on {result.PackagesChanged} package(s)");
        }

        private void ApplyVpbFileRating(IReadOnlyList<CustomAtomItem> targets, int stars)
        {
            if (targets == null || targets.Count == 0) return;
            if (!ConfirmVpbRatingWriteWhileVamRuns()) return;

            var changes = new List<FileRatingChange>(targets.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in targets)
            {
                if (item == null) continue;
                var relative = VpbUserFileKeys.RelativeUid(_selectedFolder, item.FilePath);
                if (relative.Length == 0 || !seen.Add(relative)) continue;
                changes.Add(new FileRatingChange { RelativePath = relative, Stars = stars });
            }

            if (changes.Count == 0)
            {
                SetStatus("Nothing to rate in the current selection");
                return;
            }

            var result = VpbRatingWriter.SetFileRatings(_selectedFolder, changes);
            if (!result.Success)
            {
                DarkMessageBox.Show(result.Message, "VPB rating", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshVpbData();
            SetStatus(stars > 0
                ? $"Rated {result.PackagesChanged} item(s) {stars}/5 in VPB"
                : $"Cleared the VPB rating on {result.PackagesChanged} item(s)");
        }

        /// <summary>Where the var sits relative to the VaM root, as VPB spells its rating keys.</summary>
        private string RelativeVarPath(VarMetadata metadata)
        {
            var full = metadata?.FilePath ?? "";
            if (full.Length == 0 || string.IsNullOrEmpty(_selectedFolder)) return "";
            try
            {
                var root = System.IO.Path.GetFullPath(_selectedFolder).TrimEnd('\\', '/');
                var file = System.IO.Path.GetFullPath(full);
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
                return file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
            }
            catch
            {
                return "";
            }
        }

        /// <summary>VPB keeps ratings in memory for the VaM session and rewrites the file wholesale, so a write now is lost if VPB saves before VaM exits. Tags are SQLite and need no warning.</summary>
        private bool ConfirmVpbRatingWriteWhileVamRuns()
        {
            if (_vpbRatingWarningAccepted) return true;
            if (!VpbRatingWriter.WritesAreVolatileRightNow()) return true;

            var answer = DarkMessageBox.Show(
                "VaM is running.\n\n" +
                "VPB loads ratings once when VaM starts and rewrites the whole file when it saves, so a " +
                "rating set here can be overwritten if you also rate something inside VPB before closing VaM.\n\n" +
                "Rate anyway?",
                "VaM is running",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return false;
            _vpbRatingWarningAccepted = true;
            return true;
        }

        private void PopulateVpbRateMenu(MenuItem rateMenuItem)
        {
            if (rateMenuItem == null) return;

            foreach (var existing in rateMenuItem.Items.OfType<MenuItem>().ToList())
                existing.Click -= VpbRate_Click;
            rateMenuItem.Items.Clear();

            if (_currentContentMode == "Custom")
            {
                var customTargets = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>().ToList() ?? new List<CustomAtomItem>();
                rateMenuItem.IsEnabled = customTargets.Count > 0 && !string.IsNullOrEmpty(_selectedFolder);
                if (!rateMenuItem.IsEnabled) return;

                rateMenuItem.Header = customTargets.Count > 1 ? $"⭐ Rate {customTargets.Count} items in VPB" : "⭐ Rate in VPB";
                FillVpbRateMenuItems(rateMenuItem, customTargets.Select(t => t.VpbRating).Distinct().ToList());
                return;
            }

            var targets = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList() ?? new List<PackageItem>();
            rateMenuItem.IsEnabled = targets.Count > 0 && !string.IsNullOrEmpty(_selectedFolder);
            if (!rateMenuItem.IsEnabled) return;

            rateMenuItem.Header = targets.Count > 1 ? $"⭐ Rate {targets.Count} packages in VPB" : "⭐ Rate in VPB";
            FillVpbRateMenuItems(rateMenuItem, targets.Select(t => t.VpbRating).Distinct().ToList());
        }

        private void FillVpbRateMenuItems(MenuItem rateMenuItem, List<int> distinctRatings)
        {
            int current = distinctRatings.Count == 1 ? distinctRatings[0] : -1;

            for (int stars = VpbStarStrip.MaxStars; stars >= 1; stars--)
            {
                var item = new MenuItem
                {
                    Header = new string('★', stars) + new string('☆', VpbStarStrip.MaxStars - stars),
                    Tag = stars,
                    IsChecked = stars == current
                };
                item.Click += VpbRate_Click;
                rateMenuItem.Items.Add(item);
            }

            rateMenuItem.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear rating", Tag = 0, IsChecked = current == 0 };
            clear.Click += VpbRate_Click;
            rateMenuItem.Items.Add(clear);
        }

        private void PopulateVpbTagsMenu(MenuItem tagsMenuItem)
        {
            if (tagsMenuItem == null) return;

            foreach (var existing in tagsMenuItem.Items.OfType<MenuItem>().ToList())
            {
                existing.Click -= VpbTagToggle_Click;
                existing.Click -= EditVpbTags_Click;
            }
            tagsMenuItem.Items.Clear();

            var keys = SelectedVpbIdentityKeys(out var count, out var noun);
            tagsMenuItem.IsEnabled = keys.Count > 0 && !string.IsNullOrEmpty(_selectedFolder);
            if (!tagsMenuItem.IsEnabled) return;

            tagsMenuItem.Header = count > 1 ? $"🏷 VPB Tags ({count} {noun})" : "🏷 VPB Tags";

            var editItem = new MenuItem { Header = "🏷 Edit tags…" };
            editItem.Click += EditVpbTags_Click;
            tagsMenuItem.Items.Add(editItem);

            if (keys.Count == 0 || _vpbData.AllTags.Count == 0) return;

            var usage = _vpbData.TagUsageCounts();
            var frequent = _vpbData.AllTags
                .OrderByDescending(t => usage.TryGetValue(t, out var n) ? n : 0)
                .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
            if (frequent.Count == 0) return;

            tagsMenuItem.Items.Add(new Separator());

            bool files = _currentContentMode == "Custom";
            foreach (var tag in frequent)
            {
                int carrying = files
                    ? keys.Count(u => _vpbData.HasTagOnFile(u, tag))
                    : keys.Count(u => _vpbData.HasTag(u, tag));
                var item = new MenuItem
                {
                    Header = carrying > 0 && carrying < keys.Count ? $"{tag}  ({carrying} of {keys.Count})" : tag,
                    Tag = tag,
                    IsCheckable = false,
                    IsChecked = carrying == keys.Count
                };
                item.Click += VpbTagToggle_Click;
                tagsMenuItem.Items.Add(item);
            }
        }

        private void VpbTagToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag) return;
            bool removing = item.IsChecked;

            if (_currentContentMode == "Custom")
            {
                var files = SelectedUserFileTargets();
                if (files.Count == 0) return;
                var result = removing
                    ? VpbTagWriter.RemoveTagsFromUserFiles(_selectedFolder, files, new[] { tag })
                    : VpbTagWriter.AddTagsToUserFiles(_selectedFolder, files, new[] { tag });
                ReportTagWrite(result, removing ? $"Removed “{tag}” from" : $"Tagged", files.Count, "item");
                return;
            }

            var targets = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList();
            var uids = SelectedPackageUids(targets);
            if (uids.Count == 0) return;

            var packageResult = removing
                ? VpbTagWriter.RemoveTags(_selectedFolder, uids, new[] { tag })
                : VpbTagWriter.AddTags(_selectedFolder, uids, new[] { tag });

            ReportTagWrite(packageResult, removing ? $"Removed “{tag}” from" : $"Tagged", uids.Count);
        }

        private void EditVpbTags_Click(object sender, RoutedEventArgs e)
        {
            if (_currentContentMode == "Custom")
            {
                var files = SelectedUserFileTargets();
                if (files.Count == 0) return;
                var keys = files.Select(f => f.RelativePath).ToList();
                var customItems = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>().ToList();
                var scope = keys.Count == 1
                    ? customItems?[0]?.DisplayName ?? keys[0]
                    : $"{keys.Count} selected items";

                var dialog = new VpbTagEditorWindow(keys, scope, _vpbData, _vpbLookData) { Owner = this };
                if (dialog.ShowDialog() != true) return;

                var changed = 0;
                var failure = "";

                if (dialog.TagsToAdd.Count > 0)
                {
                    var result = VpbTagWriter.AddTagsToUserFiles(_selectedFolder, files, dialog.TagsToAdd);
                    if (result.Success) changed += result.PackagesChanged; else failure = result.Message;
                }
                if (failure.Length == 0 && dialog.TagsToRemove.Count > 0)
                {
                    var result = VpbTagWriter.RemoveTagsFromUserFiles(_selectedFolder, files, dialog.TagsToRemove);
                    if (result.Success) changed = Math.Max(changed, result.PackagesChanged); else failure = result.Message;
                }

                if (failure.Length > 0)
                {
                    DarkMessageBox.Show(failure, "VPB tags", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                RefreshVpbDataAndLists();
                SetStatus($"Updated VPB tags on {changed} item(s)");
                return;
            }

            var targets = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList();
            var uids = SelectedPackageUids(targets);
            if (uids.Count == 0) return;

            var packageScope = uids.Count == 1
                ? targets[0].DisplayName
                : $"{uids.Count} selected packages";

            var packageDialog = new VpbTagEditorWindow(uids, packageScope, _vpbData, _vpbLookData) { Owner = this };
            if (packageDialog.ShowDialog() != true) return;

            var packageChanged = 0;
            var packageFailure = "";

            if (packageDialog.TagsToAdd.Count > 0)
            {
                var result = VpbTagWriter.AddTags(_selectedFolder, uids, packageDialog.TagsToAdd);
                if (result.Success) packageChanged += result.PackagesChanged; else packageFailure = result.Message;
            }
            if (packageFailure.Length == 0 && packageDialog.TagsToRemove.Count > 0)
            {
                var result = VpbTagWriter.RemoveTags(_selectedFolder, uids, packageDialog.TagsToRemove);
                if (result.Success) packageChanged = Math.Max(packageChanged, result.PackagesChanged); else packageFailure = result.Message;
            }

            var hubChanged = ApplyHubTagPrefChanges(packageDialog, uids, out var hubFailure);
            if (hubFailure.Length > 0 && packageFailure.Length == 0) packageFailure = hubFailure;

            if (packageFailure.Length > 0)
            {
                DarkMessageBox.Show(packageFailure, "VPB tags", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshVpbDataAndLists();
            SetStatus(hubChanged && packageChanged == 0
                ? "Updated hub tag visibility"
                : $"Updated VPB tags on {packageChanged} package(s)");
        }

        private void ReportTagWrite(VpbWriteResult result, string verb, int selectionSize, string noun = "package")
        {
            if (!result.Success)
            {
                DarkMessageBox.Show(result.Message, "VPB tags", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshVpbDataAndLists();
            var suffix = string.IsNullOrEmpty(result.Message) ? "" : " — " + result.Message;
            SetStatus($"{verb} {result.PackagesChanged} of {selectionSize} {noun}(s){suffix}");
        }

        /// <summary>Package uids for the current selection, skipping rows with no matching metadata.</summary>
        private List<string> SelectedPackageUids(IReadOnlyList<PackageItem> targets)
        {
            var uids = new List<string>();
            if (targets == null || _packageManager?.PackageMetadata == null) return uids;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in targets)
            {
                if (item == null) continue;
                if (!_packageManager.PackageMetadata.TryGetValue(item.MetadataKey, out var metadata)) continue;
                var uid = VpbLibraryData.UidFor(metadata);
                if (uid.Length > 0 && seen.Add(uid)) uids.Add(uid);
            }
            return uids;
        }

        private List<UserFileTagTarget> SelectedUserFileTargets()
        {
            var list = new List<UserFileTagTarget>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selected = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>();
            if (selected == null) return list;

            foreach (var item in selected)
            {
                if (item == null) continue;
                var path = VpbUserFileKeys.RelativeUid(_selectedFolder, item.FilePath);
                if (path.Length == 0 || !seen.Add(path)) continue;
                list.Add(new UserFileTagTarget
                {
                    RelativePath = path,
                    Category = VpbUserFileKeys.GalleryCategory(item)
                });
            }
            return list;
        }

        private List<string> SelectedVpbIdentityKeys(out int count, out string noun)
        {
            if (_currentContentMode == "Custom")
            {
                var files = SelectedUserFileTargets();
                count = files.Count;
                noun = "items";
                return files.Select(f => f.RelativePath).ToList();
            }

            var uids = SelectedPackageUids(PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList());
            count = uids.Count;
            noun = "packages";
            return uids;
        }

        /// <summary>Re-reads VPB and refreshes the filter list, whose tag rows and counts just moved.</summary>
        private void RefreshVpbDataAndLists()
        {
            RefreshVpbData();
            PopulateVpbFilterListsForCurrentMode();
            RefreshVpbTagsTab();
        }

        private void PopulateVpbFilterListsForCurrentMode()
        {
            if (_currentContentMode == "Custom")
                PopulateVpbFilterListsFromCustomItems();
            else
                PopulateVpbFilterLists(_packageManager?.PackageMetadata);
        }

        #endregion

        #region Tags tab

        /// <summary>A tag already on the selection, shown as a removable chip.</summary>
        public sealed class VpbTagChip
        {
            public string Name { get; init; } = "";
            public string Label { get; init; } = "";
            public string ScopeTooltip { get; init; } = "";

            /// <summary>Tags only some of the selection carries are dimmed so the difference reads at a glance.</summary>
            public double LabelOpacity { get; init; } = 1.0;
        }

        /// <summary>A tag in the library vocabulary, tickable to apply across the whole selection.</summary>
        public sealed class VpbTagOption
        {
            public string Name { get; init; } = "";
            public bool IsChecked { get; init; }
            public bool IsPartial { get; init; }
            public string CountLabel { get; init; } = "";
        }

        /// <summary>Rebuild Tags tab: chips remove applied tags; the list below is the vocabulary to add. Split so one checkbox is not add/remove/partial.</summary>
        private void RefreshVpbTagsTab()
        {
            if (VpbTagsScopeText == null) return;

            var keys = SelectedVpbIdentityKeys(out _, out var noun);
            string displayName = "";
            if (_currentContentMode == "Custom")
            {
                var custom = CustomAtomDataGrid?.SelectedItems?.Cast<CustomAtomItem>().FirstOrDefault();
                displayName = custom?.DisplayName ?? "";
            }
            else
            {
                var pkg = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().FirstOrDefault();
                displayName = pkg?.DisplayName ?? "";
            }

            bool usable = keys.Count > 0 && !string.IsNullOrEmpty(_selectedFolder);
            if (VpbTagInputBox != null) VpbTagInputBox.IsEnabled = usable;
            if (VpbTagAddButton != null) VpbTagAddButton.IsEnabled = usable;
            if (VpbTagSuggestionList != null) VpbTagSuggestionList.IsEnabled = usable;

            if (!usable)
            {
                VpbTagsScopeText.Text = _currentContentMode == "Custom"
                    ? "Select an item to manage its VPB tags"
                    : "Select a package to manage its VPB tags";
                if (VpbCurrentTagsPanel != null) VpbCurrentTagsPanel.ItemsSource = null;
                if (VpbTagSuggestionList != null) VpbTagSuggestionList.ItemsSource = null;
                if (VpbHubTagsPanel != null) VpbHubTagsPanel.ItemsSource = null;
                if (VpbHubTagsSection != null) VpbHubTagsSection.Visibility = Visibility.Collapsed;
                return;
            }

            VpbTagsScopeText.Text = keys.Count == 1
                ? $"Tags on {displayName}"
                : $"Tags across {keys.Count} selected {noun}";

            var carryingByTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                foreach (var tag in _vpbData.TagsFor(key))
                {
                    carryingByTag.TryGetValue(tag, out var n);
                    carryingByTag[tag] = n + 1;
                }
            }

            var chips = carryingByTag
                .OrderByDescending(t => t.Value)
                .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .Select(t => new VpbTagChip
                {
                    Name = t.Key,
                    Label = t.Value == keys.Count ? t.Key : $"{t.Key} ({t.Value}/{keys.Count})",
                    LabelOpacity = t.Value == keys.Count ? 1.0 : 0.65,
                    ScopeTooltip = t.Value == keys.Count
                        ? $"On every selected {noun.TrimEnd('s')}"
                        : $"On {t.Value} of {keys.Count} selected {noun}"
                })
                .ToList();

            if (VpbCurrentTagsPanel != null) VpbCurrentTagsPanel.ItemsSource = chips;
            RefreshVpbHubTagChips(keys, noun);
            PopulateVpbTagSuggestions(keys, carryingByTag);
        }

        /// <summary>The creator's tags for the selection. Not editable — the only action is to stop looking at one.</summary>
        private void RefreshVpbHubTagChips(List<string> keys, string noun)
        {
            if (VpbHubTagsPanel == null || VpbHubTagsSection == null) return;

            var look = _vpbLookData ?? VpbLookData.Empty;
            if (!look.HubTagsEnabled || keys == null || keys.Count == 0)
            {
                VpbHubTagsPanel.ItemsSource = null;
                VpbHubTagsSection.Visibility = Visibility.Collapsed;
                return;
            }

            var carryingByTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                foreach (var tag in look.HubTagsFor(key))
                {
                    carryingByTag.TryGetValue(tag, out var n);
                    carryingByTag[tag] = n + 1;
                }
            }

            if (carryingByTag.Count == 0)
            {
                VpbHubTagsPanel.ItemsSource = null;
                VpbHubTagsSection.Visibility = Visibility.Collapsed;
                return;
            }

            var chips = carryingByTag
                .OrderByDescending(t => t.Value)
                .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .Select(t => new VpbTagChip
                {
                    Name = t.Key,
                    Label = t.Value == keys.Count ? t.Key : $"{t.Key} ({t.Value}/{keys.Count})",
                    LabelOpacity = t.Value == keys.Count ? 1.0 : 0.65,
                    ScopeTooltip = "From the creator's Hub resource listing. Read-only — type the same word above "
                        + "to add it as your own tag."
                })
                .ToList();

            VpbHubTagsPanel.ItemsSource = chips;
            VpbHubTagsSection.Visibility = Visibility.Visible;

            if (VpbHubTagsHeader != null)
            {
                var hiddenHere = 0;
                foreach (var key in keys) hiddenHere += look.HiddenHubTagsFor(key).Count;
                VpbHubTagsHeader.Text = hiddenHere > 0
                    ? $"Hub tags (read-only) — {hiddenHere} hidden"
                    : "Hub tags (read-only)";
            }
        }

        /// <summary>✕ on a hub chip hides that tag on the selected packages, not everywhere.</summary>
        private void VpbHideHubTagChip_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string tag || tag.Length == 0) return;

            var keys = SelectedVpbIdentityKeys(out _, out var noun);
            if (keys.Count == 0) return;

            var result = VpbHubTagPrefWriter.HideOnPackages(_selectedFolder, keys, new[] { tag });
            if (!result.Success)
            {
                DarkMessageBox.Show(result.Message, "Hub tags", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshVpbDataAndLists();
            SetStatus($"Hid hub tag \"{tag}\" on {keys.Count} {noun}");
        }

        private void VpbHideHubTagEverywhere_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string tag || tag.Length == 0) return;

            var result = VpbHubTagPrefWriter.HideGlobally(_selectedFolder, new[] { tag });
            if (!result.Success)
            {
                DarkMessageBox.Show(result.Message, "Hub tags", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshVpbDataAndLists();
            SetStatus($"Hid hub tag \"{tag}\" everywhere");
        }

        private void PopulateVpbTagSuggestions(List<string> uids, Dictionary<string, int> carryingByTag)
        {
            if (VpbTagSuggestionList == null) return;

            var query = VpbTagInputBox?.Text?.Trim() ?? "";
            var options = new List<VpbTagOption>();

            foreach (var tag in _vpbData.AllTags)
            {
                if (query.Length > 0 && tag.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                carryingByTag.TryGetValue(tag, out var carrying);

                options.Add(new VpbTagOption
                {
                    Name = tag,
                    IsChecked = carrying == uids.Count,
                    IsPartial = carrying > 0 && carrying < uids.Count,
                    CountLabel = carrying > 0 && carrying < uids.Count ? $"({carrying} of {uids.Count})" : ""
                });
            }

            // Prefix matches first while filtering; otherwise applied tags lead.
            VpbTagSuggestionList.ItemsSource = query.Length > 0
                ? options
                    .OrderByDescending(o => o.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : options
                    .OrderByDescending(o => o.IsChecked || o.IsPartial)
                    .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        private void VpbTagInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (VpbTagSuggestionList == null || !VpbTagSuggestionList.IsEnabled) return;
            RefreshVpbTagsTab();
        }

        private void VpbTagInputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            ApplyTypedVpbTag();
        }

        private void VpbTagAddButton_Click(object sender, RoutedEventArgs e) => ApplyTypedVpbTag();

        private void ApplyTypedVpbTag()
        {
            var typed = VpbTagInputBox?.Text ?? "";
            var normalized = VpbTagWriter.NormalizeTagName(typed);
            if (normalized.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(typed))
                    SetStatus("That name cannot be used as a tag");
                return;
            }

            if (!ApplyVpbTagToSelection(normalized, adding: true)) return;
            if (VpbTagInputBox != null) VpbTagInputBox.Text = "";
        }

        private void VpbRemoveTagChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tag) return;
            ApplyVpbTagToSelection(tag, adding: false);
        }

        private void VpbTagCheck_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox check || check.Tag is not string tag) return;
            // The box has already flipped to the state the user asked for.
            ApplyVpbTagToSelection(tag, adding: check.IsChecked == true);
        }

        /// <summary>Adds or removes one tag across every selected package or custom item and refreshes what changed.</summary>
        private bool ApplyVpbTagToSelection(string tag, bool adding)
        {
            if (_currentContentMode == "Custom")
            {
                var files = SelectedUserFileTargets();
                if (files.Count == 0)
                {
                    SetStatus("Select an item first");
                    return false;
                }

                var fileResult = adding
                    ? VpbTagWriter.AddTagsToUserFiles(_selectedFolder, files, new[] { tag })
                    : VpbTagWriter.RemoveTagsFromUserFiles(_selectedFolder, files, new[] { tag });

                if (!fileResult.Success)
                {
                    DarkMessageBox.Show(fileResult.Message, "VPB tags", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                RefreshVpbDataAndLists();
                var fileVerb = adding ? "Tagged" : "Untagged";
                SetStatus($"{fileVerb} {fileResult.PackagesChanged} of {files.Count} item(s) with “{tag}”");
                return true;
            }

            var targets = PackageDataGrid?.SelectedItems?.Cast<PackageItem>().ToList();
            var uids = SelectedPackageUids(targets);
            if (uids.Count == 0)
            {
                SetStatus("Select a package first");
                return false;
            }

            var result = adding
                ? VpbTagWriter.AddTags(_selectedFolder, uids, new[] { tag })
                : VpbTagWriter.RemoveTags(_selectedFolder, uids, new[] { tag });

            if (!result.Success)
            {
                DarkMessageBox.Show(result.Message, "VPB tags", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            RefreshVpbDataAndLists();
            var verb = adding ? "Tagged" : "Untagged";
            SetStatus($"{verb} {result.PackagesChanged} of {uids.Count} package(s) with “{tag}”");
            return true;
        }

        #endregion

        /// <summary>Rebuild VPB rating and tag filter lists with live counts. Separate sections — mixing stars and tag names made every entry ambiguous.</summary>
        private void PopulateVpbFilterLists(Dictionary<string, VarMetadata> packagesToCount)
        {
            try
            {
                var ratingCounts = new int[6];
                var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var subjectCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var hubCategoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var hubTagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int untagged = 0;
                int unmatched = 0;
                int uncategorized = 0;
                int hubUntagged = 0;

                if (packagesToCount != null)
                {
                    foreach (var kvp in packagesToCount)
                    {
                        var uid = VpbLibraryData.UidFor(kvp.Value);

                        var rating = _vpbData.RatingFor(uid);
                        if (rating >= 0 && rating <= 5) ratingCounts[rating]++;

                        var tags = _vpbData.TagsFor(uid);
                        if (tags.Count == 0) untagged++;
                        foreach (var tag in tags)
                        {
                            tagCounts.TryGetValue(tag, out var n);
                            tagCounts[tag] = n + 1;
                        }

                        var subject = _vpbLookData.SubjectFor(uid);
                        if (subject.Length == 0)
                        {
                            unmatched++;
                        }
                        else
                        {
                            subjectCounts.TryGetValue(subject, out var s);
                            subjectCounts[subject] = s + 1;
                        }

                        var hubCategory = _vpbLookData.HubCategoryFor(uid);
                        if (hubCategory.Length == 0)
                        {
                            uncategorized++;
                        }
                        else
                        {
                            hubCategoryCounts.TryGetValue(hubCategory, out var c);
                            hubCategoryCounts[hubCategory] = c + 1;
                        }

                        var hubTags = _vpbLookData.HubTagsFor(uid);
                        if (hubTags.Count == 0) hubUntagged++;
                        foreach (var hubTag in hubTags)
                        {
                            hubTagCounts.TryGetValue(hubTag, out var t);
                            hubTagCounts[hubTag] = t + 1;
                        }
                    }
                }

                var ratingEntries = new List<string>(6);
                for (int stars = 5; stars >= 1; stars--)
                    ratingEntries.Add($"{new string('\u2605', stars)} ({ratingCounts[stars]:N0})");
                ratingEntries.Add($"{VpbUnratedLabel} ({ratingCounts[0]:N0})");
                RepopulatePreservingSelection(VpbRatingFilterList, ratingEntries);

                _vpbTagFilterCounts = tagCounts;
                _vpbUntaggedCount = untagged;
                FilterVpbTagsList(VpbTagFilterBox?.Text ?? "");

                _vpbLookFilterCounts = subjectCounts;
                _vpbLookUnmatchedCount = unmatched;
                FilterVpbLookList(VpbLookFilterBox?.Text ?? "");

                _vpbHubCategoryFilterCounts = hubCategoryCounts;
                _vpbHubUncategorizedCount = uncategorized;
                FilterVpbHubCategoryList(VpbHubCategoryFilterBox?.Text ?? "");

                _vpbHubTagFilterCounts = hubTagCounts;
                _vpbHubUntaggedCount = hubUntagged;
                FilterVpbHubTagList(VpbHubTagFilterBox?.Text ?? "");
            }
            catch (Exception)
            {
                // A filter list that fails to populate must not take the refresh down with it.
            }
        }

        private void PopulateVpbFilterListsFromCustomItems()
        {
            try
            {
                var ratingCounts = new int[6];
                var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int untagged = 0;

                if (CustomAtomItems != null)
                {
                    foreach (var item in CustomAtomItems)
                    {
                        if (item == null) continue;
                        var key = VpbUserFileKeys.RelativeUid(_selectedFolder, item.FilePath);

                        var rating = _vpbData.RatingForFile(key);
                        if (rating >= 0 && rating <= 5) ratingCounts[rating]++;

                        var tags = _vpbData.TagsForFile(key);
                        if (tags.Count == 0) untagged++;
                        foreach (var tag in tags)
                        {
                            tagCounts.TryGetValue(tag, out var n);
                            tagCounts[tag] = n + 1;
                        }
                    }
                }

                var ratingEntries = new List<string>(6);
                for (int stars = 5; stars >= 1; stars--)
                    ratingEntries.Add($"{new string('\u2605', stars)} ({ratingCounts[stars]:N0})");
                ratingEntries.Add($"{VpbUnratedLabel} ({ratingCounts[0]:N0})");
                RepopulatePreservingSelection(VpbRatingFilterList, ratingEntries);

                _vpbTagFilterCounts = tagCounts;
                _vpbUntaggedCount = untagged;
                FilterVpbTagsList(VpbTagFilterBox?.Text ?? "");
            }
            catch (Exception)
            {
            }
        }

        private void VpbLookFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            FilterVpbLookList(VpbLookFilterBox?.Text ?? "");
            UpdateVpbLookClearButton();
        }

        private void FilterVpbLookList(string filterText)
        {
            if (VpbLookFilterList == null) return;

            var searchTerms = SearchHelper.PrepareSearchTerms(filterText);
            var entries = new List<string>(_vpbLookFilterCounts.Count + 1);
            foreach (var subject in _vpbLookFilterCounts.OrderByDescending(s => s.Value)
                                                        .ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (SearchHelper.MatchesAllTerms(subject.Key, searchTerms))
                    entries.Add($"{subject.Key} ({subject.Value:N0})");
            }

            if (_vpbLookFilterCounts.Count > 0 && SearchHelper.MatchesAllTerms(VpbLookUnmatchedLabel, searchTerms))
                entries.Add($"{VpbLookUnmatchedLabel} ({_vpbLookUnmatchedCount:N0})");

            RepopulatePreservingSelection(VpbLookFilterList, entries);
            if (VpbLookSortButton != null)
                RestoreFilterListSorting("VpbLook", VpbLookFilterList, VpbLookSortButton);
            UpdateVpbLookClearButton();
        }

        private void VpbHubCategoryFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            FilterVpbHubCategoryList(VpbHubCategoryFilterBox?.Text ?? "");
            UpdateVpbHubCategoryClearButton();
        }

        private void FilterVpbHubCategoryList(string filterText)
        {
            if (VpbHubCategoryFilterList == null) return;

            var searchTerms = SearchHelper.PrepareSearchTerms(filterText);
            var entries = new List<string>(_vpbHubCategoryFilterCounts.Count + 1);
            foreach (var category in _vpbHubCategoryFilterCounts.OrderByDescending(c => c.Value)
                                                                .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (SearchHelper.MatchesAllTerms(category.Key, searchTerms))
                    entries.Add($"{category.Key} ({category.Value:N0})");
            }

            if (_vpbHubCategoryFilterCounts.Count > 0 && SearchHelper.MatchesAllTerms(VpbHubUncategorizedLabel, searchTerms))
                entries.Add($"{VpbHubUncategorizedLabel} ({_vpbHubUncategorizedCount:N0})");

            RepopulatePreservingSelection(VpbHubCategoryFilterList, entries);
            if (VpbHubCategorySortButton != null)
                RestoreFilterListSorting("VpbHubCategory", VpbHubCategoryFilterList, VpbHubCategorySortButton);
            UpdateVpbHubCategoryClearButton();
        }

        private void VpbHubTagFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            FilterVpbHubTagList(VpbHubTagFilterBox?.Text ?? "");
            UpdateVpbHubTagClearButton();
        }

        private void FilterVpbHubTagList(string filterText)
        {
            if (VpbHubTagFilterList == null) return;

            var searchTerms = SearchHelper.PrepareSearchTerms(filterText);
            var entries = new List<string>(_vpbHubTagFilterCounts.Count + 1);
            foreach (var tag in _vpbHubTagFilterCounts.OrderByDescending(t => t.Value)
                                                      .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (SearchHelper.MatchesAllTerms(tag.Key, searchTerms))
                    entries.Add($"{tag.Key} ({tag.Value:N0})");
            }

            if (_vpbHubTagFilterCounts.Count > 0 && SearchHelper.MatchesAllTerms(VpbHubUntaggedLabel, searchTerms))
                entries.Add($"{VpbHubUntaggedLabel} ({_vpbHubUntaggedCount:N0})");

            RepopulatePreservingSelection(VpbHubTagFilterList, entries);
            if (VpbHubTagSortButton != null)
                RestoreFilterListSorting("VpbHubTag", VpbHubTagFilterList, VpbHubTagSortButton);
            UpdateVpbHubTagClearButton();
        }

        private void VpbTagFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            FilterVpbTagsList(VpbTagFilterBox?.Text ?? "");
            UpdateVpbTagsClearButton();
        }

        private void FilterVpbTagsList(string filterText)
        {
            if (VpbTagFilterList == null) return;

            var searchTerms = SearchHelper.PrepareSearchTerms(filterText);
            var tagEntries = new List<string>(_vpbTagFilterCounts.Count + 1);
            foreach (var tag in _vpbTagFilterCounts.OrderByDescending(t => t.Value)
                                                   .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (SearchHelper.MatchesAllTerms(tag.Key, searchTerms))
                    tagEntries.Add($"{tag.Key} ({tag.Value:N0})");
            }

            if (SearchHelper.MatchesAllTerms(VpbUntaggedLabel, searchTerms))
                tagEntries.Add($"{VpbUntaggedLabel} ({_vpbUntaggedCount:N0})");

            RepopulatePreservingSelection(VpbTagFilterList, tagEntries);
            if (VpbTagsSortButton != null)
                RestoreFilterListSorting("VpbTags", VpbTagFilterList, VpbTagsSortButton);
            UpdateVpbTagsClearButton();
        }

        /// <summary>Refills a filter list, keeping whatever the user already had selected.</summary>
        private void RepopulatePreservingSelection(ListBox list, List<string> entries)
        {
            if (list == null) return;

            var selected = new List<string>();
            foreach (var item in list.SelectedItems)
            {
                var value = ExtractFilterValue(GetListBoxItemText(item));
                if (!string.IsNullOrEmpty(value)) selected.Add(value);
            }

            list.Items.Clear();
            foreach (var entry in entries) list.Items.Add(entry);

            if (selected.Count == 0) return;
            foreach (var listItem in list.Items)
            {
                var value = ExtractFilterValue(listItem?.ToString() ?? "");
                if (selected.Contains(value, StringComparer.OrdinalIgnoreCase))
                    list.SelectedItems.Add(listItem);
            }
        }
    }
}
