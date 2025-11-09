using System;

namespace VPM.Models
{
    /// <summary>
    /// Represents different date filter options for package filtering
    /// </summary>
    public enum DateFilterType
    {
        AllTime,
        Today,
        PastWeek,
        PastMonth,
        Past3Months,
        PastYear,
        CustomRange
    }

    /// <summary>
    /// Handles date filtering logic for packages
    /// </summary>
    public class DateFilter
    {
        public DateFilterType FilterType { get; set; } = DateFilterType.AllTime;
        public DateTime? CustomStartDate { get; set; }
        public DateTime? CustomEndDate { get; set; }

        /// <summary>
        /// Gets the display name for the current filter type
        /// </summary>
        public string DisplayName
        {
            get
            {
                return FilterType switch
                {
                    DateFilterType.AllTime => "All Time",
                    DateFilterType.Today => "Today",
                    DateFilterType.PastWeek => "Past Week",
                    DateFilterType.PastMonth => "Past Month",
                    DateFilterType.Past3Months => "Past 3 Months",
                    DateFilterType.PastYear => "Past Year",
                    DateFilterType.CustomRange => "Custom Range",
                    _ => "All Time"
                };
            }
        }

        /// <summary>
        /// Gets the date range for the current filter type
        /// </summary>
        /// <returns>Tuple of start and end dates, null if no filtering</returns>
        public (DateTime? StartDate, DateTime? EndDate) GetDateRange()
        {
            var now = DateTime.Now;
            var today = now.Date;

            return FilterType switch
            {
                DateFilterType.AllTime => (null, null),
                DateFilterType.Today => (today, today.AddDays(1).AddTicks(-1)),
                DateFilterType.PastWeek => (today.AddDays(-7), now),
                DateFilterType.PastMonth => (today.AddDays(-30), now),
                DateFilterType.Past3Months => (today.AddDays(-90), now),
                DateFilterType.PastYear => (today.AddDays(-365), now),
                DateFilterType.CustomRange => (CustomStartDate, CustomEndDate),
                _ => (null, null)
            };
        }

        /// <summary>
        /// Checks if a date falls within the current filter range
        /// </summary>
        /// <param name="date">Date to check</param>
        /// <returns>True if date matches filter, false otherwise</returns>
        public bool MatchesFilter(DateTime? date)
        {
            if (!date.HasValue)
                return FilterType == DateFilterType.AllTime;

            var (startDate, endDate) = GetDateRange();

            // No date filtering
            if (!startDate.HasValue && !endDate.HasValue)
                return true;

            // Check start date
            if (startDate.HasValue && date < startDate.Value)
                return false;

            // Check end date
            if (endDate.HasValue && date > endDate.Value)
                return false;

            return true;
        }

        /// <summary>
        /// Gets a user-friendly description of the current filter
        /// </summary>
        public string GetDescription()
        {
            var (startDate, endDate) = GetDateRange();

            return FilterType switch
            {
                DateFilterType.AllTime => "Showing packages from all time periods",
                DateFilterType.Today => "Showing packages modified today",
                DateFilterType.PastWeek => "Showing packages modified in the past 7 days",
                DateFilterType.PastMonth => "Showing packages modified in the past 30 days",
                DateFilterType.Past3Months => "Showing packages modified in the past 90 days",
                DateFilterType.PastYear => "Showing packages modified in the past year",
                DateFilterType.CustomRange when startDate.HasValue && endDate.HasValue => 
                    $"Showing packages from {startDate.Value:MMM dd, yyyy} to {endDate.Value:MMM dd, yyyy}",
                DateFilterType.CustomRange when startDate.HasValue => 
                    $"Showing packages from {startDate.Value:MMM dd, yyyy} onwards",
                DateFilterType.CustomRange when endDate.HasValue => 
                    $"Showing packages up to {endDate.Value:MMM dd, yyyy}",
                _ => "Showing packages from all time periods"
            };
        }
    }
}

