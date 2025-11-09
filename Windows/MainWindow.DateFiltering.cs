using System;
using System.Windows;
using System.Windows.Controls;
using VPM.Models;

namespace VPM
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Handles date filter list selection changes
        /// </summary>
        private void DateFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Prevent recursion during programmatic updates
            if (_suppressSelectionEvents) return;
            
            if (DateFilterList.SelectedItem is ListBoxItem selectedItem)
            {
                var filterTypeString = selectedItem.Tag?.ToString();
                if (Enum.TryParse<DateFilterType>(filterTypeString, out var filterType))
                {
                    // Update the filter manager
                    if (_filterManager != null)
                    {
                        _filterManager.DateFilter.FilterType = filterType;
                        
                        // Show/hide custom date range panel
                        if (filterType == DateFilterType.CustomRange)
                        {
                            CustomDateRangePanel.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            CustomDateRangePanel.Visibility = Visibility.Collapsed;
                            // Apply filter immediately for predefined ranges
                            ApplyDateFilter();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Handles custom date range changes
        /// </summary>
        private void CustomDateRange_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Update the filter manager with custom dates
            if (_filterManager != null && _filterManager.DateFilter.FilterType == DateFilterType.CustomRange)
            {
                _filterManager.DateFilter.CustomStartDate = StartDatePicker.SelectedDate;
                _filterManager.DateFilter.CustomEndDate = EndDatePicker.SelectedDate;
            }
        }

        /// <summary>
        /// Applies custom date range filter
        /// </summary>
        private void ApplyCustomDateRange_Click(object sender, RoutedEventArgs e)
        {
            if (_filterManager != null && _filterManager.DateFilter.FilterType == DateFilterType.CustomRange)
            {
                _filterManager.DateFilter.CustomStartDate = StartDatePicker.SelectedDate;
                _filterManager.DateFilter.CustomEndDate = EndDatePicker.SelectedDate;
                ApplyDateFilter();
            }
        }

        /// <summary>
        /// Clears the date filter
        /// </summary>
        private void ClearDateFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_filterManager != null)
            {
                _filterManager.ClearDateFilter();
                
                // Reset UI
                DateFilterList.SelectedIndex = 0; // "All Time"
                CustomDateRangePanel.Visibility = Visibility.Collapsed;
                StartDatePicker.SelectedDate = null;
                EndDatePicker.SelectedDate = null;
                
                // Apply the cleared filter
                ApplyDateFilter();
            }
        }

        /// <summary>
        /// Applies the current date filter and refreshes the package list
        /// </summary>
        private void ApplyDateFilter()
        {
            try
            {
                // Update status with filter description
                if (_filterManager?.DateFilter != null)
                {
                    var description = _filterManager.DateFilter.GetDescription();
                }

                // Refresh the filtered package list
                ApplyFilters();
            }
            catch (Exception ex)
            {
                SetStatus($"Error applying date filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Initializes the date filter UI components
        /// </summary>
        private void InitializeDateFilter()
        {
            try
            {
                // Initialize filter manager if not already done
                if (_filterManager == null)
                {
                    _filterManager = new Services.FilterManager();
                }
                
                // Populate date filter list with counts (will be called again when packages are loaded)
                if (_packageManager?.PackageMetadata != null)
                {
                    // Call the method from FilteringAndSearch.cs
                    PopulateDateFilterList();
                }
                else
                {
                    // Set default selection if no packages loaded yet
                    DateFilterList.SelectedIndex = 0; // "All Time"
                }
                
                CustomDateRangePanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception)
            {
                // Initialization error - silently handled
            }
        }

        /// <summary>
        /// Initializes the file size filter UI components with predefined size ranges
        /// </summary>
        private void InitializeFileSizeFilter()
        {
            try
            {
                if (FileSizeFilterList == null) return;

                // File size filter will be populated when packages are loaded
                // via PopulateFileSizeFilterList() in FilteringAndSearch.cs
                FileSizeFilterList.Items.Clear();
            }
            catch (Exception)
            {
                // Initialization error - silently handled
            }
        }

        /// <summary>
        /// Gets the count of packages matching the current date filter
        /// </summary>
        /// <returns>Number of packages matching the date filter</returns>
        private int GetDateFilteredPackageCount()
        {
            if (_filterManager?.DateFilter == null || _packageManager?.PackageMetadata == null)
                return 0;

            int count = 0;
            foreach (var package in _packageManager.PackageMetadata.Values)
            {
                if (_filterManager.DateFilter.FilterType == DateFilterType.AllTime)
                {
                    count++;
                }
                else
                {
                    var dateToCheck = package.ModifiedDate ?? package.CreatedDate;
                    if (_filterManager.DateFilter.MatchesFilter(dateToCheck))
                    {
                        count++;
                    }
                }
            }
            return count;
        }
    }
}

