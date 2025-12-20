using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VPM.Models;
using VPM.Services;
using static VPM.Models.PackageItem;

namespace VPM
{
    /// <summary>
    /// Filtering and search functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        private sealed class ActiveFilterToken
        {
            public string Kind { get; init; }
            public string Label { get; init; }
            public string Value { get; init; }
        }

        private static bool IsTextBoxActiveFilter(TextBox textBox, Brush placeholderBrush)
        {
            if (textBox == null)
                return false;

            return !string.IsNullOrWhiteSpace(textBox.Text);
        }

        private bool HasAnyPackageFiltersActive()
        {
            if (!IsLoaded)
                return false;

            try
            {
                if (StatusFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (CreatorsList?.SelectedItems?.Count > 0)
                    return true;
                if (ContentTypesList?.SelectedItems?.Count > 0)
                    return true;
                if (LicenseTypeList?.SelectedItems?.Count > 0)
                    return true;
                if (FileSizeFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (SubfoldersFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (DamagedFilterList?.SelectedItem != null)
                {
                    var selected = DamagedFilterList.SelectedItem.ToString();
                    if (!string.IsNullOrEmpty(selected) && !selected.StartsWith("All Packages", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                if (DestinationsFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (PlaylistsFilterList?.SelectedItems?.Count > 0)
                    return true;

                if (DateFilterList?.SelectedIndex > 0)
                    return true;
                if (StartDatePicker?.SelectedDate != null)
                    return true;
                if (EndDatePicker?.SelectedDate != null)
                    return true;

                if (IsTextBoxActiveFilter(PackageSearchBox, null))
                    return true;
                if (IsTextBoxActiveFilter(CreatorsFilterBox, null))
                    return true;
                if (IsTextBoxActiveFilter(ContentTypesFilterBox, null))
                    return true;
                if (IsTextBoxActiveFilter(LicenseTypeFilterBox, null))
                    return true;
                if (IsTextBoxActiveFilter(SubfoldersFilterBox, null))
                    return true;

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool HasAnySceneFiltersActive()
        {
            if (!IsLoaded)
                return false;

            try
            {
                if (SceneTypeFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (SceneCreatorFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (SceneStatusFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (SceneSourceFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (SceneDateFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (SceneFileSizeFilterList?.SelectedItems?.Count > 0)
                    return true;

                if (IsTextBoxActiveFilter(SceneSearchBox, null))
                    return true;
                if (IsTextBoxActiveFilter(SceneTypeFilterBox, null))
                    return true;
                if (IsTextBoxActiveFilter(SceneCreatorFilterBox, null))
                    return true;

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool HasAnyCustomFiltersActive()
        {
            if (!IsLoaded)
                return false;

            try
            {
                if (PresetCategoryFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (PresetSubfolderFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (PresetDateFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (PresetFileSizeFilterList?.SelectedItems?.Count > 0)
                    return true;
                if (PresetStatusFilterList?.SelectedItems?.Count > 0)
                    return true;

                if (IsTextBoxActiveFilter(CustomAtomSearchBox, null))
                    return true;
                if (IsTextBoxActiveFilter(PresetCategoryFilterBox, null))
                    return true;
                if (IsTextBoxActiveFilter(PresetSubfolderFilterBox, null))
                    return true;

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool HasAnyFiltersActive()
        {
            return _currentContentMode switch
            {
                "Scenes" => HasAnySceneFiltersActive(),
                "Custom" => HasAnyCustomFiltersActive(),
                _ => HasAnyPackageFiltersActive()
            };
        }

        private void UpdateClearAllFiltersButtonVisibility()
        {
            if (!IsLoaded)
                return;

            UpdateActiveFiltersPanel();
        }

        private void UpdateActiveFiltersPanel()
        {
            if (!IsLoaded)
                return;

            if (ActiveFiltersBorder == null || ActiveFiltersWrapPanel == null)
                return;

            try
            {
                ActiveFiltersWrapPanel.Children.Clear();

                var tokens = GetActiveFilterTokens();
                if (tokens.Count == 0)
                {
                    ActiveFiltersBorder.Visibility = Visibility.Collapsed;
                    return;
                }

                foreach (var token in tokens)
                {
                    ActiveFiltersWrapPanel.Children.Add(CreateActiveFilterChip(token));
                }

                ActiveFiltersBorder.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
            }
        }

        private List<ActiveFilterToken> GetActiveFilterTokens()
        {
            var tokens = new List<ActiveFilterToken>();

            try
            {
                if (!IsLoaded)
                    return tokens;

                switch (_currentContentMode)
                {
                    case "Scenes":
                    {
                        if (SceneTypeFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in SceneTypeFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "SceneType", Label = $"Type: {text}", Value = text });
                            }
                        }
                        if (SceneCreatorFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in SceneCreatorFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "SceneCreator", Label = $"Creator: {text}", Value = text });
                            }
                        }
                        if (SceneStatusFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in SceneStatusFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "SceneStatus", Label = $"Status: {text}", Value = text });
                            }
                        }
                        if (SceneSourceFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in SceneSourceFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "SceneSource", Label = $"Source: {text}", Value = text });
                            }
                        }
                        if (SceneDateFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in SceneDateFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "SceneDate", Label = $"Date: {text}", Value = text });
                            }
                        }
                        if (SceneFileSizeFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in SceneFileSizeFilterList.SelectedItems)
                            {
                                var text = GetListBoxItemText(item);
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "SceneFileSize", Label = $"Size: {text}", Value = text });
                            }
                        }

                        if (IsTextBoxActiveFilter(SceneSearchBox, null))
                        {
                            var text = GetSearchText(SceneSearchBox);
                            if (!string.IsNullOrEmpty(text))
                                tokens.Add(new ActiveFilterToken { Kind = "SceneSearch", Label = $"Search: {text}", Value = text });
                        }

                        break;
                    }
                    case "Custom":
                    {
                        if (PresetCategoryFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in PresetCategoryFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "PresetCategory", Label = $"Category: {text}", Value = text });
                            }
                        }
                        if (PresetSubfolderFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in PresetSubfolderFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "PresetSubfolder", Label = $"Subfolder: {text}", Value = text });
                            }
                        }
                        if (PresetDateFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in PresetDateFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "PresetDate", Label = $"Date: {text}", Value = text });
                            }
                        }
                        if (PresetFileSizeFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in PresetFileSizeFilterList.SelectedItems)
                            {
                                var text = GetListBoxItemText(item);
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "PresetFileSize", Label = $"Size: {text}", Value = text });
                            }
                        }
                        if (PresetStatusFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in PresetStatusFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "PresetStatus", Label = $"Status: {text}", Value = text });
                            }
                        }

                        if (IsTextBoxActiveFilter(CustomAtomSearchBox, null))
                        {
                            var text = GetSearchText(CustomAtomSearchBox);
                            if (!string.IsNullOrEmpty(text))
                                tokens.Add(new ActiveFilterToken { Kind = "PresetSearch", Label = $"Search: {text}", Value = text });
                        }

                        break;
                    }
                    default:
                    {
                        if (StatusFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in StatusFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (string.Equals(text, "Duplicates", StringComparison.OrdinalIgnoreCase))
                                    text = "Duplicate";

                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "Status", Label = $"Status: {text}", Value = text });
                            }
                        }

                        if (CreatorsList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in CreatorsList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "Creator", Label = $"Creator: {text}", Value = text });
                            }
                        }

                        if (ContentTypesList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in ContentTypesList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "ContentType", Label = $"Type: {text}", Value = text });
                            }
                        }

                        if (LicenseTypeList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in LicenseTypeList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "License", Label = $"License: {text}", Value = text });
                            }
                        }

                        if (FileSizeFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in FileSizeFilterList.SelectedItems)
                            {
                                var text = GetListBoxItemText(item);
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "FileSize", Label = $"Size: {text}", Value = text });
                            }
                        }

                        if (SubfoldersFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in SubfoldersFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "Subfolder", Label = $"Subfolder: {text}", Value = text });
                            }
                        }

                        if (DamagedFilterList?.SelectedItem != null)
                        {
                            var selected = DamagedFilterList.SelectedItem.ToString();
                            if (!string.IsNullOrEmpty(selected) && !selected.StartsWith("All Packages", StringComparison.OrdinalIgnoreCase))
                            {
                                tokens.Add(new ActiveFilterToken { Kind = "Damaged", Label = $"Damaged: {selected}", Value = selected });
                            }
                        }

                        if (DestinationsFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in DestinationsFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "Destination", Label = $"Destination: {text}", Value = text });
                            }
                        }

                        if (PlaylistsFilterList?.SelectedItems?.Count > 0)
                        {
                            foreach (var item in PlaylistsFilterList.SelectedItems)
                            {
                                var text = ExtractFilterValue(GetListBoxItemText(item));
                                if (!string.IsNullOrEmpty(text))
                                    tokens.Add(new ActiveFilterToken { Kind = "Playlist", Label = $"Playlist: {text}", Value = text });
                            }
                        }

                        if (DateFilterList?.SelectedIndex > 0 || StartDatePicker?.SelectedDate != null || EndDatePicker?.SelectedDate != null)
                        {
                            var description = _filterManager?.DateFilter != null ? _filterManager.DateFilter.GetDescription() : "Date";
                            if (!string.IsNullOrEmpty(description) && !string.Equals(description, "All Time", StringComparison.OrdinalIgnoreCase))
                                tokens.Add(new ActiveFilterToken { Kind = "Date", Label = $"Date: {description}", Value = description });
                        }

                        if (IsTextBoxActiveFilter(PackageSearchBox, null))
                        {
                            var text = GetSearchText(PackageSearchBox);
                            if (!string.IsNullOrEmpty(text))
                                tokens.Add(new ActiveFilterToken { Kind = "Search", Label = $"Search: {text}", Value = text });
                        }

                        break;
                    }
                }
            }
            catch (Exception)
            {
            }

            return tokens;
        }

        private UIElement CreateActiveFilterChip(ActiveFilterToken token)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 0),
                Padding = new Thickness(6, 2, 2, 2),
                Background = (Brush)FindResource(SystemColors.WindowBrushKey),
                BorderBrush = (Brush)FindResource(SystemColors.ActiveBorderBrushKey),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                ClipToBounds = false
            };

            var dock = new DockPanel { LastChildFill = false };

            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(SystemColors.ControlTextBrushKey),
                Text = token.Label
            };

            var button = new Button
            {
                Content = "X",
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = token,
                Style = (Style)FindResource("BlueHoverButtonStyle")
            };
            button.Click += ActiveFilterChipRemove_Click;

            DockPanel.SetDock(button, Dock.Right);
            dock.Children.Add(button);
            dock.Children.Add(text);

            border.Child = dock;
            return border;
        }

        private void ActiveFilterChipRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            if (button.Tag is not ActiveFilterToken token)
                return;

            try
            {
                _suppressSelectionEvents = true;
                RemoveActiveFilter(token);
            }
            catch (Exception)
            {
            }
            finally
            {
                _suppressSelectionEvents = false;
            }

            try
            {
                ApplyFilters();
                UpdateClearAllFiltersButtonVisibility();
            }
            catch (Exception)
            {
            }
        }

        private void RemoveActiveFilter(ActiveFilterToken token)
        {
            if (token == null)
                return;

            switch (token.Kind)
            {
                case "Search":
                    if (PackageSearchBox != null)
                        PackageSearchBox.Text = "";
                    break;
                case "Date":
                    if (DateFilterList != null)
                        DateFilterList.SelectedIndex = 0;
                    if (CustomDateRangePanel != null)
                        CustomDateRangePanel.Visibility = Visibility.Collapsed;
                    if (StartDatePicker != null)
                        StartDatePicker.SelectedDate = null;
                    if (EndDatePicker != null)
                        EndDatePicker.SelectedDate = null;
                    break;
                case "Status":
                    RemoveFromSelectedItems(StatusFilterList, token.Value, stripCount: true);
                    break;
                case "Creator":
                    RemoveFromSelectedItems(CreatorsList, token.Value, stripCount: true);
                    break;
                case "ContentType":
                    RemoveFromSelectedItems(ContentTypesList, token.Value, stripCount: true);
                    break;
                case "License":
                    RemoveFromSelectedItems(LicenseTypeList, token.Value, stripCount: true);
                    break;
                case "FileSize":
                    RemoveFromSelectedItems(FileSizeFilterList, token.Value, stripCount: false);
                    break;
                case "Subfolder":
                    RemoveFromSelectedItems(SubfoldersFilterList, token.Value, stripCount: true);
                    break;
                case "Damaged":
                    if (DamagedFilterList != null)
                        DamagedFilterList.SelectedItem = null;
                    break;
                case "Destination":
                    RemoveFromSelectedItems(DestinationsFilterList, token.Value, stripCount: true);
                    break;
                case "Playlist":
                    RemoveFromSelectedItems(PlaylistsFilterList, token.Value, stripCount: true);
                    break;
                case "SceneSearch":
                    if (SceneSearchBox != null)
                        SceneSearchBox.Text = "";
                    break;
                case "SceneType":
                    RemoveFromSelectedItems(SceneTypeFilterList, token.Value, stripCount: true);
                    break;
                case "SceneCreator":
                    RemoveFromSelectedItems(SceneCreatorFilterList, token.Value, stripCount: true);
                    break;
                case "SceneStatus":
                    RemoveFromSelectedItems(SceneStatusFilterList, token.Value, stripCount: true);
                    break;
                case "SceneSource":
                    RemoveFromSelectedItems(SceneSourceFilterList, token.Value, stripCount: true);
                    break;
                case "SceneDate":
                    RemoveFromSelectedItems(SceneDateFilterList, token.Value, stripCount: true);
                    break;
                case "SceneFileSize":
                    RemoveFromSelectedItems(SceneFileSizeFilterList, token.Value, stripCount: false);
                    break;
                case "PresetSearch":
                    if (CustomAtomSearchBox != null)
                        CustomAtomSearchBox.Text = "";
                    break;
                case "PresetCategory":
                    RemoveFromSelectedItems(PresetCategoryFilterList, token.Value, stripCount: true);
                    break;
                case "PresetSubfolder":
                    RemoveFromSelectedItems(PresetSubfolderFilterList, token.Value, stripCount: true);
                    break;
                case "PresetDate":
                    RemoveFromSelectedItems(PresetDateFilterList, token.Value, stripCount: true);
                    break;
                case "PresetFileSize":
                    RemoveFromSelectedItems(PresetFileSizeFilterList, token.Value, stripCount: false);
                    break;
                case "PresetStatus":
                    RemoveFromSelectedItems(PresetStatusFilterList, token.Value, stripCount: true);
                    break;
            }
        }

        private void RemoveFromSelectedItems(ListBox listBox, string value, bool stripCount)
        {
            if (listBox?.SelectedItems == null || listBox.SelectedItems.Count == 0)
                return;

            var toRemove = new List<object>();
            foreach (var item in listBox.SelectedItems)
            {
                var text = GetListBoxItemText(item);
                var compareText = stripCount ? ExtractFilterValue(text, stripCount: true) : text;
                if (string.Equals(compareText, value, StringComparison.OrdinalIgnoreCase))
                {
                    toRemove.Add(item);
                }
            }

            if (toRemove.Count == 0)
                return;

            foreach (var item in toRemove)
            {
                listBox.SelectedItems.Remove(item);
            }
        }

        #region Filter Application

        private void ApplyFilters()
        {
            if (_filterManager == null || _packageManager == null) return;

            try
            {
                // Don't reload if no packages loaded yet
                if (_packageManager.PackageMetadata == null || _packageManager.PackageMetadata.Count == 0)
                {
                    return;
                }

                // CRITICAL FIX: Skip ApplyFilters if we just completed a full refresh
                // This prevents view-based filtering from removing external packages that were just loaded
                if (_isLoadingPackages)
                {
                    return;
                }

                // Update FilterManager properties with current UI selections
                UpdateFilterManagerFromUI();
                
                // Invalidate reactive filter cache so counts will be recalculated
                if (_reactiveFilterManager != null)
                {
                    _reactiveFilterManager.InvalidateCounts();
                }
                
                // Apply cascade filtering if enabled
                if (_cascadeFiltering)
                {
                    var currentFilters = GetSelectedFilters();
                    UpdateCascadeFilteringLive(currentFilters);

                    // Update filter manager again after cascade filtering has restored selections
                    // This ensures the restored selections are properly captured
                    UpdateFilterManagerFromUI(applyFilters: false);
                }
                else
                {
                    // In non-linked mode, update filter counts live without full refresh
                    UpdateFilterCountsLive();
                }
                
                // Trigger package list reload with new filters applied in background
                // Don't refresh filter lists - they're already updated live above
                UpdatePackageListAsync(refreshFilterLists: false);
                
                // Reapply sorting after filtering to maintain sort order
                ReapplySorting();

                UpdateActiveFiltersPanel();
            }
            catch (Exception)
            {
            }
        }

        private string SummarizeFilterValue(object value)
        {
            return value switch
            {
                List<string> stringList => stringList.Count == 0 ? "(empty list)" : string.Join(", ", stringList),
                DateFilter df => df.FilterType == DateFilterType.CustomRange
                    ? FormatCustomDateRange(df)
                    : df.FilterType.ToString(),
                null => "(null)",
                _ => value?.ToString() ?? "(null)"
            };
        }

        private string FormatCustomDateRange(DateFilter dateFilter)
        {
            var (start, end) = dateFilter.GetDateRange();

            if (start.HasValue && end.HasValue)
            {
                return $"CustomRange {start.Value:yyyy-MM-dd}..{end.Value:yyyy-MM-dd}";
            }

            if (start.HasValue)
            {
                return $"CustomRange from {start.Value:yyyy-MM-dd}";
            }

            if (end.HasValue)
            {
                return $"CustomRange until {end.Value:yyyy-MM-dd}";
            }

            return "CustomRange (no bounds)";
        }

        /// <summary>
        /// Extracts text from a ListBox item (handles ListBoxItem, string, or other types)
        /// </summary>
        private static string GetListBoxItemText(object item) => item switch
        {
            ListBoxItem lbi => lbi.Content?.ToString() ?? "",
            string s => s,
            _ => item?.ToString() ?? ""
        };

        /// <summary>
        /// Extracts the filter value from text in "Value (count)" format
        /// </summary>
        private static string ExtractFilterValue(string itemText, bool stripCount = true)
        {
            if (string.IsNullOrEmpty(itemText)) return "";
            return stripCount ? itemText.Split('(')[0].Trim() : itemText;
        }

        /// <summary>
        /// Collects selected items from a ListBox into a collection, extracting filter values
        /// </summary>
        private static void CollectSelectedFilters(ListBox listBox, ICollection<string> collection, bool stripCount = true)
        {
            if (listBox?.SelectedItems == null || listBox.SelectedItems.Count == 0) return;
            
            foreach (var item in listBox.SelectedItems)
            {
                var text = GetListBoxItemText(item);
                var value = ExtractFilterValue(text, stripCount);
                if (!string.IsNullOrEmpty(value))
                    collection.Add(value);
            }
        }

        private void UpdateFilterManagerFromUI(bool applyFilters = true)
        {
            try
            {
                // Clear existing filters
                _filterManager.SelectedStatuses.Clear();
                _filterManager.SelectedFavoriteStatuses.Clear();
                _filterManager.SelectedAutoInstallStatuses.Clear();
                _filterManager.SelectedOptimizationStatuses.Clear();
                _filterManager.SelectedVersionStatuses.Clear();
                _filterManager.SelectedCreators.Clear();
                _filterManager.SelectedCategories.Clear();
                _filterManager.SelectedLicenseTypes.Clear();
                _filterManager.SelectedFileSizeRanges.Clear();
                _filterManager.SelectedSubfolders.Clear();
                _filterManager.SelectedDamagedFilter = null;
                _filterManager.SelectedPlaylistFilters.Clear();
                
                
                // Update status filters (includes regular status, optimization status, version status, and favorites)
                _filterManager.FilterDuplicates = false;
                _filterManager.FilterNoDependents = false;
                _filterManager.FilterNoDependencies = false;
                if (StatusFilterList?.SelectedItems != null && StatusFilterList.SelectedItems.Count > 0)
                {
                    var seenStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in StatusFilterList.SelectedItems)
                    {
                        var itemText = GetListBoxItemText(item);
                        var status = ExtractFilterValue(itemText);
                        
                        if (string.IsNullOrEmpty(status) || seenStatuses.Contains(status))
                        {
                            continue;
                        }

                        // Route status to appropriate collection based on type
                        if (status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) || 
                            status.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                        {
                            _filterManager.FilterDuplicates = true;
                        }
                        else if (status == "Favorites" || status == "Non-Favorites")
                        {
                            _filterManager.SelectedFavoriteStatuses.Add(status);
                        }
                        else if (status == "AutoInstall")
                        {
                            _filterManager.SelectedAutoInstallStatuses.Add(status);
                        }
                        else if (status == "Optimized" || status == "Unoptimized")
                        {
                            _filterManager.SelectedOptimizationStatuses.Add(status);
                        }
                        else if (status == "Latest" || status == "Old")
                        {
                            _filterManager.SelectedVersionStatuses.Add(status);
                        }
                        else if (status == "No Dependents")
                        {
                            _filterManager.FilterNoDependents = true;
                        }
                        else if (status == "No Dependencies")
                        {
                            _filterManager.FilterNoDependencies = true;
                        }
                        else
                        {
                            _filterManager.SelectedStatuses.Add(status);
                            seenStatuses.Add(status);
                        }
                    }
                }
                else
                {
                }

                // Update simple filter collections using helper
                CollectSelectedFilters(CreatorsList, _filterManager.SelectedCreators);
                CollectSelectedFilters(ContentTypesList, _filterManager.SelectedCategories);
                CollectSelectedFilters(LicenseTypeList, _filterManager.SelectedLicenseTypes);
                CollectSelectedFilters(FileSizeFilterList, _filterManager.SelectedFileSizeRanges, stripCount: false);
                CollectSelectedFilters(SubfoldersFilterList, _filterManager.SelectedSubfolders);
                
                // Update destinations filter
                _filterManager.SelectedDestinations.Clear();
                CollectSelectedFilters(DestinationsFilterList, _filterManager.SelectedDestinations);

                // Update playlists filter
                CollectSelectedFilters(PlaylistsFilterList, _filterManager.SelectedPlaylistFilters);

                // Update damaged filter
                if (DamagedFilterList?.SelectedItem != null)
                {
                    var selectedItem = DamagedFilterList.SelectedItem.ToString();
                    if (!string.IsNullOrEmpty(selectedItem) && !selectedItem.StartsWith("All Packages"))
                    {
                        _filterManager.SelectedDamagedFilter = selectedItem;
                    }
                }

                // Update search text filter
                var searchText = GetSearchText(PackageSearchBox);
                _filterManager.SetSearchText(searchText);

                // Date filter is already handled by the FilterManager.DateFilter property
                // which is updated directly by the date filter UI events
            }
            catch (Exception)
            {
            }
        }

        private Dictionary<string, object> GetSelectedFilters()
        {
            var filters = new Dictionary<string, object>();

            try
            {
                // Status filters
                if (StatusFilterList?.SelectedItems != null && StatusFilterList.SelectedItems.Count > 0)
                {
                    var selectedStatuses = new List<string>();
                    bool duplicatesSelected = false;
                    foreach (var item in StatusFilterList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            // Extract status from "Status (count)" format - no emojis to handle
                            var status = itemText.Split('(')[0].Trim();
                            if (status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) || status.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                            {
                                duplicatesSelected = true;
                            }
                            else
                            {
                                selectedStatuses.Add(status);
                            }
                        }
                    }
                    
                    if (selectedStatuses.Count > 0)
                    {
                        filters["Status"] = selectedStatuses;
                    }

                    if (duplicatesSelected)
                    {
                        filters["Duplicate"] = true;
                    }
                }

                // Creator filters
                if (CreatorsList?.SelectedItems != null && CreatorsList.SelectedItems.Count > 0)
                {
                    var selectedCreators = new List<string>();
                    foreach (var item in CreatorsList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var creator = itemText.Split('(')[0].Trim();
                            selectedCreators.Add(creator);
                        }
                    }
                    
                    if (selectedCreators.Count > 0)
                    {
                        filters["Creator"] = selectedCreators;
                    }
                }

                // Content type filters
                if (ContentTypesList?.SelectedItems != null && ContentTypesList.SelectedItems.Count > 0)
                {
                    var selectedTypes = new List<string>();
                    foreach (var item in ContentTypesList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var contentType = itemText.Split('(')[0].Trim();
                            selectedTypes.Add(contentType);
                        }
                    }
                    
                    if (selectedTypes.Count > 0)
                    {
                        filters["ContentType"] = selectedTypes;
                    }
                }

                // License type filters
                if (LicenseTypeList?.SelectedItems != null && LicenseTypeList.SelectedItems.Count > 0)
                {
                    var selectedLicenseTypes = new List<string>();
                    foreach (var item in LicenseTypeList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            var licenseType = itemText.Split('(')[0].Trim();
                            selectedLicenseTypes.Add(licenseType);
                        }
                    }
                    
                    if (selectedLicenseTypes.Count > 0)
                    {
                        filters["LicenseType"] = selectedLicenseTypes;
                    }
                }

                // File size filters
                if (FileSizeFilterList?.SelectedItems != null && FileSizeFilterList.SelectedItems.Count > 0)
                {
                    var selectedFileSizeRanges = new List<string>();
                    foreach (var item in FileSizeFilterList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            selectedFileSizeRanges.Add(itemText);
                        }
                    }
                    
                    if (selectedFileSizeRanges.Count > 0)
                    {
                        filters["FileSizeRange"] = selectedFileSizeRanges;
                    }
                }

                // Subfolders filters
                if (SubfoldersFilterList?.SelectedItems != null && SubfoldersFilterList.SelectedItems.Count > 0)
                {
                    var selectedSubfolders = new List<string>();
                    foreach (var item in SubfoldersFilterList.SelectedItems)
                    {
                        string itemText = "";
                        if (item is ListBoxItem listBoxItem)
                        {
                            itemText = listBoxItem.Content?.ToString() ?? "";
                        }
                        else if (item is string stringItem)
                        {
                            itemText = stringItem;
                        }
                        else
                        {
                            itemText = item?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(itemText))
                        {
                            // Extract subfolder name from "Subfolder (count)" format
                            var subfolder = itemText.Split('(')[0].Trim();
                            selectedSubfolders.Add(subfolder);
                        }
                    }
                    
                    if (selectedSubfolders.Count > 0)
                    {
                        filters["Subfolders"] = selectedSubfolders;
                    }
                }

                // Date filter
                if (_filterManager?.DateFilter != null && _filterManager.DateFilter.FilterType != DateFilterType.AllTime)
                {
                    filters["DateFilter"] = _filterManager.DateFilter;
                }
            }
            catch (Exception)
            {
            }

            return filters;
        }

        #endregion

        #region Filter Methods

        private void FilterPackages(string filterText = "")
        {
            ApplyFilters();
        }

        private void FilterDependencies(string filterText = "")
        {
            if (Dependencies == null || _originalDependencies == null) return;

            try
            {
                Dependencies.Clear();
                
                if (string.IsNullOrWhiteSpace(filterText))
                {
                    // Show all dependencies
                    foreach (var dep in _originalDependencies)
                    {
                        Dependencies.Add(dep);
                    }
                }
                else
                {
                    // Prepare search terms
                    var searchTerms = VPM.Services.SearchHelper.PrepareSearchTerms(filterText);

                    // Filter dependencies by text - using MatchesAllTerms for multi-term matching
                    foreach (var dep in _originalDependencies)
                    {
                        if (VPM.Services.SearchHelper.MatchesAllTerms(dep.Name, searchTerms))
                        {
                            Dependencies.Add(dep);
                        }
                    }
                }
                
                // Reapply dependencies sorting after filtering
                var depsState = _sortingManager?.GetSortingState("Dependencies");
                if (depsState?.CurrentSortOption is DependencySortOption depsSort)
                {
                    ReapplyDependenciesSortingInternal(depsSort, depsState.IsAscending);
                }
                
                // Update toolbar buttons after filtering dependencies
                UpdateToolbarButtons();
            }
            catch (Exception)
            {
            }
        }

        private void FilterCreators(string filterText = "")
        {
            // Filter the creators list by text
            FilterCreatorsList(filterText);
        }

        #endregion

        #region Clear Button Methods

        private void UpdateClearButtonVisibility()
        {
            UpdatePackageSearchClearButton();
            UpdateDepsSearchClearButton();
            UpdateCreatorsClearButton();
            UpdateContentTypesClearButton();
            UpdateLicenseTypeClearButton();
            UpdateSubfoldersClearButton();
            UpdateClearAllFiltersButtonVisibility();
        }

        private void UpdatePackageSearchClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                if (PackageSearchClearButton != null && PackageSearchBox != null && PackageDataGrid != null)
                {
                    bool hasText = !string.IsNullOrWhiteSpace(PackageSearchBox.Text);
                    bool hasSelection = PackageDataGrid.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    PackageSearchClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
                
                // Also update the dependency graph button visibility
                UpdatePackageDependencyGraphButton();
            }
            catch (Exception)
            {
            }
        }

        private void UpdatePackageDependencyGraphButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                if (PackageDependencyGraphButton != null && PackageDataGrid != null)
                {
                    // Only show when exactly one package is selected
                    bool shouldShow = PackageDataGrid.SelectedItems.Count == 1;
                    PackageDependencyGraphButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateDepsSearchClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (DepsSearchClearButton != null && DepsSearchBox != null && DependenciesDataGrid != null)
                {
                    bool hasText = !string.IsNullOrWhiteSpace(DepsSearchBox.Text);
                    bool hasSelection = DependenciesDataGrid.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    DepsSearchClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateCreatorsClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (CreatorsClearButton != null && CreatorsFilterBox != null && CreatorsList != null)
                {
                    bool hasText = !string.IsNullOrWhiteSpace(CreatorsFilterBox.Text);
                    bool hasSelection = CreatorsList.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    CreatorsClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateContentTypesClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (ContentTypesClearButton != null && ContentTypesFilterBox != null && ContentTypesList != null)
                {
                    bool hasText = !string.IsNullOrWhiteSpace(ContentTypesFilterBox.Text);
                    bool hasSelection = ContentTypesList.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    ContentTypesClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateLicenseTypeClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (LicenseTypeClearButton != null && LicenseTypeFilterBox != null && LicenseTypeList != null)
                {
                    bool hasText = !string.IsNullOrWhiteSpace(LicenseTypeFilterBox.Text);
                    bool hasSelection = LicenseTypeList.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    LicenseTypeClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateSubfoldersClearButton()
        {
            if (!this.IsLoaded) return;
            
            try
            {
                var grayBrush = (SolidColorBrush)FindResource(SystemColors.GrayTextBrushKey);
                
                if (SubfoldersClearButton != null && SubfoldersFilterBox != null && SubfoldersFilterList != null)
                {
                    bool hasText = !string.IsNullOrWhiteSpace(SubfoldersFilterBox.Text);
                    bool hasSelection = SubfoldersFilterList.SelectedItems.Count > 0;
                    bool shouldShow = hasText || hasSelection;
                    SubfoldersClearButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Initialization Methods

        private void InitializeSearchBoxes()
        {
            if (!this.IsLoaded) return;

            try
            {
                if (PackageSearchBox != null)
                {
                    PackageSearchBox.Text = "";
                }

                if (DepsSearchBox != null)
                {
                    DepsSearchBox.Text = "";
                }

                if (CreatorsFilterBox != null)
                {
                    CreatorsFilterBox.Text = "";
                }

            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Search Text Helpers

        private string GetSearchText(TextBox searchBox)
        {
            if (searchBox == null) return "";

            try
            {
                var text = searchBox.Text ?? "";
                return !string.IsNullOrWhiteSpace(text) ? text.Trim() : "";
            }
            catch
            {
                return !string.IsNullOrWhiteSpace(searchBox?.Text) ? searchBox.Text.Trim() : "";
            }
        }

        #endregion

        #region Dependencies Refresh

        private void RefreshDependenciesForFilteredPackages()
        {
            try
            {
                var selectedCount = PackageDataGrid?.SelectedItems.Count ?? 0;

                // Only refresh dependencies if no packages are selected
                // (when packages are selected, their specific dependencies are shown)
                if (selectedCount == 0)
                {
                    // Clear dependencies when no packages are selected to prevent loading all deps
                    ClearDependenciesDisplay();
                }
            }
            catch (Exception)
            {
            }
        }

        private async void RefreshDependenciesAfterCascade()
        {
            try
            {
                // Small delay to ensure main table view has been updated after cascade filtering
                await Task.Delay(50);

                var selectedCount = PackageDataGrid?.SelectedItems.Count ?? 0;

                // Only refresh dependencies if no packages are selected
                // (when packages are selected, their specific dependencies are shown)
                if (selectedCount == 0)
                {
                    // Clear dependencies when no packages are selected to prevent loading all deps
                    ClearDependenciesDisplay();
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Cascade Filtering

        private void UpdateStatusListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveStatusFilter)
        {
            if (StatusFilterList == null) return;

            // Prevent infinite recursion by suppressing selection events
            _suppressSelectionEvents = true;
            try
            {
                // Store selected status names (without counts) before clearing
                var selectedStatuses = new List<string>();
                foreach (var item in StatusFilterList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        // Extract status name without count and normalize
                        var statusName = itemText.Split('(')[0].Trim();
                        if (statusName.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                        {
                            statusName = "Duplicate";
                        }
                        selectedStatuses.Add(statusName);
                    }
                }
                // Show all statuses with updated counts from filtered packages
                StatusFilterList.Items.Clear();
                var statusCounts = _filterManager.GetStatusCounts(filteredPackages);

                foreach (var status in statusCounts.Where(s => s.Value > 0).OrderBy(s => s.Key))
                {
                    var displayName = status.Key.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) ? "Duplicates" : status.Key;
                    var displayText = $"{displayName} ({status.Value})";
                    StatusFilterList.Items.Add(displayText);

                    // Restore selection if this status was previously selected
                    if (selectedStatuses.Contains(status.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }
                
                // Add optimization status counts
                var optCounts = _filterManager.GetOptimizationStatusCounts(filteredPackages);

                foreach (var opt in optCounts.Where(s => s.Value > 0).OrderBy(s => s.Key))
                {
                    var displayText = $"{opt.Key} ({opt.Value:N0})";
                    StatusFilterList.Items.Add(displayText);

                    // Restore selection if this optimization status was previously selected
                    if (selectedStatuses.Contains(opt.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }
                
                // Add version status counts (always show, even if count is 0)
                var versionCounts = _filterManager.GetVersionStatusCounts(filteredPackages);

                foreach (var ver in versionCounts.OrderBy(s => s.Key))
                {
                    var displayText = $"{ver.Key} ({ver.Value:N0})";
                    StatusFilterList.Items.Add(displayText);

                    // Restore selection if this version status was previously selected
                    if (selectedStatuses.Contains(ver.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }
                
                // Add dependency status counts (No Dependents / No Dependencies)
                var depCounts = _filterManager.GetDependencyStatusCounts(filteredPackages);

                foreach (var dep in depCounts.OrderBy(s => s.Key))
                {
                    var displayText = $"{dep.Key} ({dep.Value:N0})";
                    StatusFilterList.Items.Add(displayText);

                    // Restore selection if this dependency status was previously selected
                    if (selectedStatuses.Contains(dep.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }
                
                // Add favorites option
                if (_favoritesManager != null && _packageManager?.PackageMetadata != null)
                {
                    var favorites = _favoritesManager.GetAllFavorites();
                    int favoriteCount = 0;
                    
                    // Count from ALL packages, not filtered packages
                    foreach (var pkg in _packageManager.PackageMetadata.Values)
                    {
                        var pkgName = System.IO.Path.GetFileNameWithoutExtension(pkg.Filename);
                        if (favorites.Contains(pkgName))
                            favoriteCount++;
                    }
                    
                    var favText = $"Favorites ({favoriteCount:N0})";
                    StatusFilterList.Items.Add(favText);
                    
                    if (selectedStatuses.Contains("Favorites"))
                    {
                        StatusFilterList.SelectedItems.Add(favText);
                    }
                }

                // Add autoinstall option
                if (_autoInstallManager != null && _packageManager?.PackageMetadata != null)
                {
                    var autoInstall = _autoInstallManager.GetAllAutoInstall();
                    int autoInstallCount = 0;
                    
                    // Count from ALL packages, not filtered packages
                    foreach (var pkg in _packageManager.PackageMetadata.Values)
                    {
                        var pkgName = System.IO.Path.GetFileNameWithoutExtension(pkg.Filename);
                        if (autoInstall.Contains(pkgName))
                            autoInstallCount++;
                    }
                    
                    var autoInstallText = $"AutoInstall ({autoInstallCount:N0})";
                    StatusFilterList.Items.Add(autoInstallText);
                    
                    if (selectedStatuses.Contains("AutoInstall"))
                    {
                        StatusFilterList.SelectedItems.Add(autoInstallText);
                    }
                }
                
                // Add external destination counts
                var destCounts = _filterManager.GetDestinationCounts(_packageManager.PackageMetadata);
                
                foreach (var dest in destCounts.OrderBy(s => s.Key))
                {
                    var displayText = $"{dest.Key} ({dest.Value:N0})";
                    StatusFilterList.Items.Add(displayText);
                    
                    // Restore selection if this destination was previously selected
                    if (selectedStatuses.Contains(dest.Key))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }

                // Add External/Local package type filters
                var externalCount = filteredPackages.Values.Count(p => p.IsExternal);
                var localCount = filteredPackages.Values.Count(p => !p.IsExternal);
                
                if (externalCount > 0)
                {
                    var displayText = $"External ({externalCount:N0})";
                    StatusFilterList.Items.Add(displayText);
                    if (selectedStatuses.Contains("External"))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }
                
                if (localCount > 0)
                {
                    var displayText = $"Local ({localCount:N0})";
                    StatusFilterList.Items.Add(displayText);
                    if (selectedStatuses.Contains("Local"))
                    {
                        StatusFilterList.SelectedItems.Add(displayText);
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        private void UpdateCreatorsListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveCreatorFilter)
        {
            var creatorCounts = _filterManager.GetCreatorCounts(filteredPackages);
            UpdateFilterListBox(CreatorsList, creatorCounts);
        }

        private void UpdateContentTypesListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveContentTypeFilter)
        {
            var categoryCounts = _filterManager.GetCategoryCounts(filteredPackages);
            UpdateFilterListBox(ContentTypesList, categoryCounts);
        }

        private void UpdateLicenseTypesListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveLicenseTypeFilter)
        {
            var licenseCounts = _filterManager.GetLicenseCounts(filteredPackages);
            UpdateFilterListBox(LicenseTypeList, licenseCounts);
        }

        private void UpdateFileSizeFilterListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveFileSizeFilter)
        {
            var fileSizeCounts = _filterManager.GetFileSizeCounts(filteredPackages);
            var orderedRanges = new[] { "Tiny", "Small", "Medium", "Large" };
            UpdateFilterListBox(FileSizeFilterList, fileSizeCounts, orderedKeys: orderedRanges);
        }

        private void UpdateSubfoldersFilterListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveSubfoldersFilter)
        {
            var subfolderCounts = _filterManager.GetSubfolderCounts(filteredPackages);
            UpdateFilterListBox(SubfoldersFilterList, subfolderCounts);
        }

        private void UpdateDateFilterListWithCascade(Dictionary<string, VarMetadata> filteredPackages, bool hasActiveDateFilter)
        {
            if (DateFilterList == null) return;

            // Prevent infinite recursion by suppressing selection events
            _suppressSelectionEvents = true;
            try
            {
                var selectedItems = new List<string>();
                foreach (var item in DateFilterList.SelectedItems)
                {
                    string itemText = "";
                    if (item is ListBoxItem listBoxItem)
                    {
                        itemText = listBoxItem.Content?.ToString() ?? "";
                    }
                    else if (item is string stringItem)
                    {
                        itemText = stringItem;
                    }
                    else
                    {
                        itemText = item?.ToString() ?? "";
                    }
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        selectedItems.Add(itemText);
                    }
                }
                
                if (hasActiveDateFilter)
                {
                    // Hide non-selected items when date filter is active
                    var itemsToRemove = new List<string>();
                    foreach (string item in DateFilterList.Items)
                    {
                        if (!selectedItems.Contains(item))
                        {
                            itemsToRemove.Add(item);
                        }
                    }
                    
                    foreach (var item in itemsToRemove)
                    {
                        DateFilterList.Items.Remove(item);
                    }
                }
                else
                {
                    // Show all date filter options with counts from filtered packages
                    DateFilterList.Items.Clear();
                    var dateCounts = GetDateFilterCounts(filteredPackages);
                    
                    // Store current selection tag
                    var selectedTag = "";
                    foreach (var item in selectedItems)
                    {
                        // Extract tag from display text or use the item directly
                        var parts = item.Split('(');
                        var baseText = parts[0].Trim();
                        
                        selectedTag = baseText switch
                        {
                            "All Time" => "AllTime",
                            "Today" => "Today", 
                            "Past Week" => "PastWeek",
                            "Past Month" => "PastMonth",
                            "Past 3 Months" => "Past3Months",
                            "Past Year" => "PastYear",
                            "Custom Range..." => "CustomRange",
                            _ => selectedTag
                        };
                        
                        if (!string.IsNullOrEmpty(selectedTag)) break;
                    }
                    
                    // Add all date filter options
                    var dateOptions = new[]
                    {
                        new { Text = "All Time", Tag = "AllTime", Count = dateCounts["AllTime"] },
                        new { Text = "Today", Tag = "Today", Count = dateCounts["Today"] },
                        new { Text = "Past Week", Tag = "PastWeek", Count = dateCounts["PastWeek"] },
                        new { Text = "Past Month", Tag = "PastMonth", Count = dateCounts["PastMonth"] },
                        new { Text = "Past 3 Months", Tag = "Past3Months", Count = dateCounts["Past3Months"] },
                        new { Text = "Past Year", Tag = "PastYear", Count = dateCounts["PastYear"] },
                        new { Text = "Custom Range...", Tag = "CustomRange", Count = 0 }
                    };
                    
                    foreach (var option in dateOptions)
                    {
                        var displayText = option.Tag == "CustomRange" ? option.Text : $"{option.Text} ({option.Count})";
                        DateFilterList.Items.Add(displayText);
                        
                        // Restore selection
                        if (option.Tag == selectedTag)
                        {
                            DateFilterList.SelectedItem = displayText;
                        }
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }


        #endregion

        #region Filter Textbox Methods

        private void FilterContentTypesList(string filterText)
        {
            if (ContentTypesList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var selectedItems = ContentTypesList.SelectedItems.Cast<string>().ToList();
                ContentTypesList.Items.Clear();
                
                var categoryCounts = _filterManager.GetCategoryCounts(_packageManager.PackageMetadata);
                var searchTerms = VPM.Services.SearchHelper.PrepareSearchTerms(filterText);
                
                foreach (var category in categoryCounts.OrderBy(c => c.Key))
                {
                    if (VPM.Services.SearchHelper.MatchesAllTerms(category.Key, searchTerms))
                    {
                        var displayText = $"{category.Key} ({category.Value})";
                        ContentTypesList.Items.Add(displayText);
                        
                        // Restore selection
                        if (selectedItems.Any(item => item.StartsWith(category.Key)))
                        {
                            ContentTypesList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void FilterCreatorsList(string filterText)
        {
            if (CreatorsList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var selectedItems = CreatorsList.SelectedItems.Cast<string>().ToList();
                CreatorsList.Items.Clear();
                
                var creatorCounts = _filterManager.GetCreatorCounts(_packageManager.PackageMetadata);
                var searchTerms = VPM.Services.SearchHelper.PrepareSearchTerms(filterText);
                
                foreach (var creator in creatorCounts.OrderBy(c => c.Key))
                {
                    if (VPM.Services.SearchHelper.MatchesAllTerms(creator.Key, searchTerms))
                    {
                        var displayText = $"{creator.Key} ({creator.Value})";
                        CreatorsList.Items.Add(displayText);
                        
                        // Restore selection
                        if (selectedItems.Any(item => item.StartsWith(creator.Key)))
                        {
                            CreatorsList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void FilterLicenseTypesList(string filterText)
        {
            if (LicenseTypeList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var selectedItems = LicenseTypeList.SelectedItems.Cast<string>().ToList();
                LicenseTypeList.Items.Clear();
                
                var licenseCounts = _filterManager.GetLicenseCounts(_packageManager.PackageMetadata);
                var searchTerms = VPM.Services.SearchHelper.PrepareSearchTerms(filterText);
                
                foreach (var license in licenseCounts.OrderBy(l => l.Key))
                {
                    if (VPM.Services.SearchHelper.MatchesAllTerms(license.Key, searchTerms))
                    {
                        var displayText = $"{license.Key} ({license.Value})";
                        LicenseTypeList.Items.Add(displayText);
                        
                        // Restore selection
                        if (selectedItems.Any(item => item.StartsWith(license.Key)))
                        {
                            LicenseTypeList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void FilterSubfoldersList(string filterText)
        {
            if (SubfoldersFilterList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var selectedItems = SubfoldersFilterList.SelectedItems.Cast<string>().ToList();
                SubfoldersFilterList.Items.Clear();
                
                var subfolderCounts = _filterManager.GetSubfolderCounts(_packageManager.PackageMetadata);
                var searchTerms = VPM.Services.SearchHelper.PrepareSearchTerms(filterText);
                
                foreach (var subfolder in subfolderCounts.OrderBy(s => s.Key))
                {
                    if (VPM.Services.SearchHelper.MatchesAllTerms(subfolder.Key, searchTerms))
                    {
                        var displayText = $"{subfolder.Key} ({subfolder.Value})";
                        SubfoldersFilterList.Items.Add(displayText);
                        
                        // Restore selection
                        if (selectedItems.Any(item => item.StartsWith(subfolder.Key)))
                        {
                            SubfoldersFilterList.SelectedItems.Add(displayText);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion

        #region Filter List Population

        private void PopulateFilterLists()
        {
            if (_filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                // Populate status filter list (includes optimization status)
                PopulateStatusFilterList();
                
                // Populate creators list
                PopulateCreatorsList();
                
                // Populate content types list
                PopulateContentTypesList();
                
                // Populate license types list
                PopulateLicenseTypesList();
                
                // Populate file size filter list
                PopulateFileSizeFilterList();
                
                // Populate subfolders filter list
                PopulateSubfoldersFilterList();
                
                // Populate destinations filter list
                PopulateDestinationsFilterList();
                
                // Populate damaged filter list
                PopulateDamagedFilterList();

                // Populate date filter list
                PopulateDateFilterList();
            }
            catch (Exception)
            {
            }
        }

        private void PopulateStatusFilterList()
        {
            if (StatusFilterList == null) return;

            try
            {
                // Store selected status names before clearing
                var selectedStatuses = new List<string>();
                
                foreach (var item in StatusFilterList.SelectedItems)
                {
                    string itemText = item?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        // Extract status name without count and normalize
                        var statusName = itemText.Split('(')[0].Trim();
                        if (statusName.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                        {
                            statusName = "Duplicate";
                        }
                        selectedStatuses.Add(statusName);
                    }
                }
                
                
                _suppressSelectionEvents = true;
                try
                {
                    StatusFilterList.Items.Clear();
                    
                    // Add regular status counts
                    var statusCounts = _filterManager.GetStatusCounts(_packageManager.PackageMetadata);
                    
                    foreach (var status in statusCounts.OrderBy(s => s.Key))
                    {
                        var displayName = status.Key.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) ? "Duplicates" : status.Key;
                        var displayText = $"{displayName} ({status.Value})";
                        StatusFilterList.Items.Add(displayText);
                        
                        // Restore selection if this status was previously selected
                        if (selectedStatuses.Contains(status.Key))
                        {
                            StatusFilterList.SelectedItems.Add(displayText);
                        }
                    }
                    
                    // Add optimization status counts
                    var optCounts = _filterManager.GetOptimizationStatusCounts(_packageManager.PackageMetadata);
                    
                    foreach (var opt in optCounts.OrderBy(s => s.Key))
                    {
                        var displayText = $"{opt.Key} ({opt.Value:N0})";
                        StatusFilterList.Items.Add(displayText);
                        
                        // Restore selection if this optimization status was previously selected
                        if (selectedStatuses.Contains(opt.Key))
                        {
                            StatusFilterList.SelectedItems.Add(displayText);
                        }
                    }
                    
                    // Add version status counts
                    var versionCounts = _filterManager.GetVersionStatusCounts(_packageManager.PackageMetadata);
                    
                    foreach (var ver in versionCounts.OrderBy(s => s.Key))
                    {
                        var displayText = $"{ver.Key} ({ver.Value:N0})";
                        StatusFilterList.Items.Add(displayText);
                        
                        // Restore selection if this version status was previously selected
                        if (selectedStatuses.Contains(ver.Key))
                        {
                            System.Diagnostics.Debug.WriteLine($"[PopulateStatusFilterList] Restoring version selection: '{displayText}'");
                            StatusFilterList.SelectedItems.Add(displayText);
                        }
                    }
                    
                    // Add dependency status counts (No Dependents / No Dependencies)
                    var depCounts = _filterManager.GetDependencyStatusCounts(_packageManager.PackageMetadata);
                    
                    foreach (var dep in depCounts.OrderBy(s => s.Key))
                    {
                        var displayText = $"{dep.Key} ({dep.Value:N0})";
                        StatusFilterList.Items.Add(displayText);
                        
                        // Restore selection if this dependency status was previously selected
                        if (selectedStatuses.Contains(dep.Key))
                        {
                            System.Diagnostics.Debug.WriteLine($"[PopulateStatusFilterList] Restoring dependency selection: '{displayText}'");
                            StatusFilterList.SelectedItems.Add(displayText);
                        }
                    }
                    
                    // Add External/Local package type filters
                    var externalCount = _packageManager.PackageMetadata.Values.Count(p => p.IsExternal);
                    var localCount = _packageManager.PackageMetadata.Values.Count(p => !p.IsExternal);
                    
                    if (externalCount > 0)
                    {
                        var displayText = $"External ({externalCount:N0})";
                        StatusFilterList.Items.Add(displayText);
                        if (selectedStatuses.Contains("External"))
                        {
                            StatusFilterList.SelectedItems.Add(displayText);
                        }
                    }
                    
                    if (localCount > 0)
                    {
                        var displayText = $"Local ({localCount:N0})";
                        StatusFilterList.Items.Add(displayText);
                        if (selectedStatuses.Contains("Local"))
                        {
                            StatusFilterList.SelectedItems.Add(displayText);
                        }
                    }
                }
                finally
                {
                    _suppressSelectionEvents = false;
                    System.Diagnostics.Debug.WriteLine($"[PopulateStatusFilterList] Completed - Final selected items count: {StatusFilterList.SelectedItems.Count}");
                }
            }
            catch (Exception)
            {
            }
        }

        private void PopulateCreatorsList()
        {
            if (CreatorsList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var creatorCounts = _filterManager.GetCreatorCounts(_packageManager.PackageMetadata);
                UpdateFilterListBox(CreatorsList, creatorCounts);
            }
            catch (Exception)
            {
            }
        }

        private void PopulateContentTypesList()
        {
            if (ContentTypesList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var categoryCounts = _filterManager.GetCategoryCounts(_packageManager.PackageMetadata);
                UpdateFilterListBox(ContentTypesList, categoryCounts);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PopulateContentTypesList] Error: {ex.Message}");
            }
        }

        private void PopulateLicenseTypesList()
        {
            if (LicenseTypeList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var licenseCounts = _filterManager.GetLicenseCounts(_packageManager.PackageMetadata);
                UpdateFilterListBox(LicenseTypeList, licenseCounts);
            }
            catch (Exception)
            {
            }
        }

        private void PopulateFileSizeFilterList()
        {
            if (FileSizeFilterList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var fileSizeCounts = _filterManager.GetFileSizeCounts(_packageManager.PackageMetadata);
                var orderedRanges = new[] { "Tiny", "Small", "Medium", "Large" };
                UpdateFilterListBox(FileSizeFilterList, fileSizeCounts, orderedKeys: orderedRanges);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PopulateFileSizeFilterList] Error: {ex.Message}");
            }
        }

        private void PopulateSubfoldersFilterList()
        {
            if (SubfoldersFilterList == null || _filterManager == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var subfolderCounts = _filterManager.GetSubfolderCounts(_packageManager.PackageMetadata);
                UpdateFilterListBox(SubfoldersFilterList, subfolderCounts);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PopulateSubfoldersFilterList] Error: {ex.Message}");
            }
        }

        private void PopulateDestinationsFilterList()
        {
            if (DestinationsFilterList == null || _filterManager == null || _packageManager?.PackageMetadata == null)
                return;

            try
            {
                var destCounts = _filterManager.GetDestinationCounts(_packageManager.PackageMetadata);
                
                // Build a lookup of destination visibility settings
                var destVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var dest in (_settingsManager?.Settings?.MoveToDestinations ?? new List<MoveToDestination>()))
                {
                    if (dest?.IsValid() == true && !string.IsNullOrWhiteSpace(dest.Name))
                    {
                        destVisibility[dest.Name] = dest.ShowInMainTable;
                        System.Diagnostics.Debug.WriteLine($"[PopulateDestinationsFilterList] {dest.Name}: ShowInMainTable={dest.ShowInMainTable}");
                    }
                }
                
                // Clear and rebuild the list with Hidden tag for hidden destinations
                var selectedNames = GetSelectedItemNames(DestinationsFilterList);
                DestinationsFilterList.Items.Clear();
                
                foreach (var key in destCounts.Keys.OrderBy(k => k))
                {
                    int count = destCounts[key];
                    if (count <= 0) continue;
                    
                    // Append "Hidden" tag if ShowInMainTable is false
                    bool isHidden = destVisibility.TryGetValue(key, out var showInTable) && !showInTable;
                    string displayText = isHidden
                        ? $"{key} ({count:N0}) Hidden"
                        : $"{key} ({count:N0})";
                    
                    System.Diagnostics.Debug.WriteLine($"[PopulateDestinationsFilterList] Adding: {displayText} (isHidden={isHidden})");
                    
                    DestinationsFilterList.Items.Add(displayText);
                    
                    // Restore selection if this item was previously selected
                    if (selectedNames.Contains(key))
                    {
                        DestinationsFilterList.SelectedItems.Add(displayText);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PopulateDestinationsFilterList] Error: {ex.Message}");
            }
        }

        private void PopulateDamagedFilterList()
        {
            if (DamagedFilterList == null || _packageManager?.PackageMetadata == null) return;

            try
            {
                var selectedItem = DamagedFilterList.SelectedItem?.ToString();
                
                DamagedFilterList.Items.Clear();
                
                int damagedCount = _packageManager.PackageMetadata.Values.Count(m => m.IsDamaged);
                int validCount = _packageManager.PackageMetadata.Count - damagedCount;
                
                DamagedFilterList.Items.Add($"All Packages ({_packageManager.PackageMetadata.Count})");
                
                if (damagedCount > 0)
                {
                    DamagedFilterList.Items.Add($"⚠️ Damaged ({damagedCount})");
                }
                
                if (validCount > 0)
                {
                    DamagedFilterList.Items.Add($"✓ Valid ({validCount})");
                }
                
                if (!string.IsNullOrEmpty(selectedItem) && DamagedFilterList.Items.Contains(selectedItem))
                {
                    DamagedFilterList.SelectedItem = selectedItem;
                }
                else
                {
                    DamagedFilterList.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating damaged filter: {ex.Message}");
            }
        }

        #endregion

        #region Live Filter Updates

        /// <summary>
        /// Update filter counts live without rebuilding filter lists (non-cascade mode)
        /// This is much faster than RefreshFilterLists as it only updates counts
        /// </summary>
        private void UpdateFilterCountsLive()
        {
            if (_packageManager?.PackageMetadata == null || _reactiveFilterManager == null)
                return;

            try
            {
                _suppressSelectionEvents = true;

                // Get filtered packages based on current filter state
                var filteredPackages = _reactiveFilterManager.GetFilteredPackages();

                // Update each filter list with new counts while preserving selections
                UpdateStatusListCounts(filteredPackages);
                UpdateCreatorsListCounts(filteredPackages);
                UpdateContentTypesListCounts(filteredPackages);
                UpdateLicenseTypesListCounts(filteredPackages);
                UpdateFileSizeListCounts(filteredPackages);
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        /// <summary>
        /// Update cascade filtering live - optimized version
        /// </summary>
        private void UpdateCascadeFilteringLive(Dictionary<string, object> currentFilters)
        {
            if (_packageManager?.PackageMetadata == null || _reactiveFilterManager == null)
                return;

            try
            {
                // Get currently filtered packages based on active filters
                var filteredPackages = _reactiveFilterManager.GetFilteredPackages();

                // Check which filters are active
                var hasStatusFilter = currentFilters.ContainsKey("Status") &&
                                    currentFilters["Status"] is List<string> statusList && statusList.Count > 0;
                var hasCreatorFilter = currentFilters.ContainsKey("Creator") &&
                                     currentFilters["Creator"] is List<string> creatorList && creatorList.Count > 0;
                var hasContentTypeFilter = currentFilters.ContainsKey("ContentType") &&
                                          currentFilters["ContentType"] is List<string> contentList && contentList.Count > 0;
                var hasLicenseTypeFilter = currentFilters.ContainsKey("LicenseType") &&
                                          currentFilters["LicenseType"] is List<string> licenseList && licenseList.Count > 0;
                var hasFileSizeFilter = currentFilters.ContainsKey("FileSizeRange") &&
                                       currentFilters["FileSizeRange"] is List<string> fileSizeList && fileSizeList.Count > 0;
                var hasSubfoldersFilter = currentFilters.ContainsKey("Subfolders") &&
                                         currentFilters["Subfolders"] is List<string> subfoldersList && subfoldersList.Count > 0;
                var hasDateFilter = currentFilters.ContainsKey("DateFilter") &&
                                  currentFilters["DateFilter"] is DateFilter dateFilter && dateFilter.FilterType != DateFilterType.AllTime;

                // Update filter lists based on cascade rules
                UpdateStatusListWithCascade(filteredPackages, hasStatusFilter);
                UpdateCreatorsListWithCascade(filteredPackages, hasCreatorFilter);
                UpdateContentTypesListWithCascade(filteredPackages, hasContentTypeFilter);
                UpdateLicenseTypesListWithCascade(filteredPackages, hasLicenseTypeFilter);
                UpdateFileSizeFilterListWithCascade(filteredPackages, hasFileSizeFilter);
                UpdateSubfoldersFilterListWithCascade(filteredPackages, hasSubfoldersFilter);
                UpdateDateFilterListWithCascade(filteredPackages, hasDateFilter);

                // Also refresh dependencies to show only deps from filtered packages
                RefreshDependenciesAfterCascade();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Update status list counts without rebuilding the list
        /// </summary>
        private void UpdateStatusListCounts(Dictionary<string, VarMetadata> filteredPackages)
        {
            if (StatusFilterList == null) return;

            var selectedStatuses = GetSelectedItemNames(StatusFilterList);
            var statusCounts = _filterManager.GetStatusCounts(filteredPackages);
            var optCounts = _filterManager.GetOptimizationStatusCounts(filteredPackages);

            StatusFilterList.Items.Clear();

            // Add status items with updated counts
            foreach (var status in statusCounts.OrderBy(s => s.Key))
            {
                var displayName = status.Key.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) ? "Duplicates" : status.Key;
                var displayText = $"{displayName} ({status.Value:N0})";
                StatusFilterList.Items.Add(displayText);

                if (selectedStatuses.Contains(status.Key))
                {
                    StatusFilterList.SelectedItems.Add(displayText);
                }
            }

            // Add optimization status items
            foreach (var opt in optCounts.OrderBy(s => s.Key))
            {
                var displayText = $"{opt.Key} ({opt.Value:N0})";
                StatusFilterList.Items.Add(displayText);

                if (selectedStatuses.Contains(opt.Key))
                {
                    StatusFilterList.SelectedItems.Add(displayText);
                }
            }

            // Add version status items (always show, even if count is 0)
            var versionCounts = _filterManager.GetVersionStatusCounts(filteredPackages);
            foreach (var ver in versionCounts.OrderBy(s => s.Key))
            {
                var displayText = $"{ver.Key} ({ver.Value:N0})";
                StatusFilterList.Items.Add(displayText);

                if (selectedStatuses.Contains(ver.Key))
                {
                    StatusFilterList.SelectedItems.Add(displayText);
                }
            }
            
            // Add dependency status counts (No Dependents / No Dependencies)
            var depCounts = _filterManager.GetDependencyStatusCounts(filteredPackages);

            foreach (var dep in depCounts.OrderBy(s => s.Key))
            {
                var displayText = $"{dep.Key} ({dep.Value:N0})";
                StatusFilterList.Items.Add(displayText);

                if (selectedStatuses.Contains(dep.Key))
                {
                    StatusFilterList.SelectedItems.Add(displayText);
                }
            }
            
            // Add External/Local package type filters
            var externalCount = filteredPackages.Values.Count(p => p.IsExternal);
            var localCount = filteredPackages.Values.Count(p => !p.IsExternal);
            
            if (externalCount > 0)
            {
                var displayText = $"External ({externalCount:N0})";
                StatusFilterList.Items.Add(displayText);
                if (selectedStatuses.Contains("External"))
                {
                    StatusFilterList.SelectedItems.Add(displayText);
                }
            }
            
            if (localCount > 0)
            {
                var displayText = $"Local ({localCount:N0})";
                StatusFilterList.Items.Add(displayText);
                if (selectedStatuses.Contains("Local"))
                {
                    StatusFilterList.SelectedItems.Add(displayText);
                }
            }
            
            // Add favorites option
            if (_favoritesManager != null && _packageManager?.PackageMetadata != null)
            {
                var favorites = _favoritesManager.GetAllFavorites();
                int favoriteCount = 0;
                
                // Count from filtered packages, excluding external packages
                foreach (var pkg in filteredPackages.Values)
                {
                    // Skip external packages - they should not appear in favorites filter
                    if (pkg.IsExternal)
                        continue;
                    
                    var pkgName = System.IO.Path.GetFileNameWithoutExtension(pkg.Filename);
                    if (favorites.Contains(pkgName))
                        favoriteCount++;
                }
                
                var favText = $"Favorites ({favoriteCount:N0})";
                StatusFilterList.Items.Add(favText);
                
                if (selectedStatuses.Contains("Favorites"))
                {
                    StatusFilterList.SelectedItems.Add(favText);
                }
            }

            // Add autoinstall option
            if (_autoInstallManager != null && _packageManager?.PackageMetadata != null)
            {
                var autoInstall = _autoInstallManager.GetAllAutoInstall();
                int autoInstallCount = 0;
                
                // Count from filtered packages, excluding external packages
                foreach (var pkg in filteredPackages.Values)
                {
                    // Skip external packages - they should not appear in autoinstall filter
                    if (pkg.IsExternal)
                        continue;
                    
                    var pkgName = System.IO.Path.GetFileNameWithoutExtension(pkg.Filename);
                    if (autoInstall.Contains(pkgName))
                        autoInstallCount++;
                }
                
                var autoInstallText = $"AutoInstall ({autoInstallCount:N0})";
                StatusFilterList.Items.Add(autoInstallText);
                
                if (selectedStatuses.Contains("AutoInstall"))
                {
                    StatusFilterList.SelectedItems.Add(autoInstallText);
                }
            }
        }

        /// <summary>
        /// Update creators list counts without rebuilding the list
        /// </summary>
        private void UpdateCreatorsListCounts(Dictionary<string, VarMetadata> filteredPackages)
        {
            var creatorCounts = _filterManager.GetCreatorCounts(filteredPackages);
            UpdateFilterListBox(CreatorsList, creatorCounts);
        }

        /// <summary>
        /// Update content types list counts without rebuilding the list
        /// </summary>
        private void UpdateContentTypesListCounts(Dictionary<string, VarMetadata> filteredPackages)
        {
            var categoryCounts = _filterManager.GetCategoryCounts(filteredPackages);
            UpdateFilterListBox(ContentTypesList, categoryCounts);
        }

        /// <summary>
        /// Update license types list counts without rebuilding the list
        /// </summary>
        private void UpdateLicenseTypesListCounts(Dictionary<string, VarMetadata> filteredPackages)
        {
            var licenseCounts = _filterManager.GetLicenseCounts(filteredPackages);
            UpdateFilterListBox(LicenseTypeList, licenseCounts);
        }

        /// <summary>
        /// Update file size list counts without rebuilding the list
        /// </summary>
        private void UpdateFileSizeListCounts(Dictionary<string, VarMetadata> filteredPackages)
        {
            var fileSizeCounts = _filterManager.GetFileSizeCounts(filteredPackages);
            var orderedRanges = new[] { "Tiny", "Small", "Medium", "Large" };
            UpdateFilterListBox(FileSizeFilterList, fileSizeCounts, orderedKeys: orderedRanges);
        }

        /// <summary>
        /// Helper method to get selected item names from a ListBox
        /// </summary>
        private List<string> GetSelectedItemNames(ListBox listBox)
        {
            var selectedNames = new List<string>();
            foreach (var item in listBox.SelectedItems)
            {
                var name = ExtractFilterValue(GetListBoxItemText(item));
                if (!string.IsNullOrEmpty(name))
                {
                    // Normalize "Duplicates" to "Duplicate"
                    if (name.Equals("Duplicates", StringComparison.OrdinalIgnoreCase))
                        name = "Duplicate";
                    selectedNames.Add(name);
                }
            }
            return selectedNames;
        }

        /// <summary>
        /// Generic helper to update a filter ListBox with counts, preserving selections.
        /// Reduces code duplication across all UpdateXxxListWithCascade and PopulateXxxList methods.
        /// </summary>
        private void UpdateFilterListBox(
            ListBox listBox,
            Dictionary<string, int> counts,
            Func<string, string> displayNameTransform = null,
            IEnumerable<string> orderedKeys = null,
            bool includeZeroCounts = false)
        {
            if (listBox == null) return;

            _suppressSelectionEvents = true;
            try
            {
                // Get currently selected item names
                var selectedNames = GetSelectedItemNames(listBox);

                listBox.Items.Clear();

                // Use ordered keys if provided, otherwise order by key
                var keysToProcess = orderedKeys ?? counts.Keys.OrderBy(k => k);

                foreach (var key in keysToProcess)
                {
                    if (!counts.TryGetValue(key, out int count)) continue;
                    if (!includeZeroCounts && count <= 0) continue;

                    var displayName = displayNameTransform?.Invoke(key) ?? key;
                    var displayText = $"{displayName} ({count:N0})";
                    listBox.Items.Add(displayText);

                    // Restore selection if this item was previously selected
                    if (selectedNames.Contains(key))
                    {
                        listBox.SelectedItems.Add(displayText);
                    }
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }

        #endregion
    }

    #region Value Converters

    /// <summary>
    /// Converter to format file sizes
    /// </summary>
    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long bytes)
            {
                return FormatHelper.FormatFileSize(bytes);
            }
            return "0 B";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

    }

    /// <summary>
    /// Converter to convert status strings to colors
    /// </summary>
    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "Loaded" => new SolidColorBrush(Color.FromRgb(76, 175, 80)),     // Green
                    "Available" => new SolidColorBrush(Color.FromRgb(33, 150, 243)),  // Blue
                    "Missing" => new SolidColorBrush(Color.FromRgb(244, 67, 54)),     // Red
                    "Outdated" => new SolidColorBrush(Color.FromRgb(255, 152, 0)),    // Orange
                    "Updating" => new SolidColorBrush(Color.FromRgb(156, 39, 176)),   // Purple
                    _ => new SolidColorBrush(Color.FromRgb(158, 158, 158))            // Gray
                };
            }
            return new SolidColorBrush(Color.FromRgb(158, 158, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter to convert status strings to icons
    /// </summary>
    public class StatusIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "Loaded" => "✓",
                    "Available" => "○",
                    "Missing" => "✗",
                    "Outdated" => "⚠",
                    "Updating" => "↻",
                    _ => "?"
                };
            }
            return "?";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    #endregion
}

