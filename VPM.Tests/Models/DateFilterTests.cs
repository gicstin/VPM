using Xunit;
using VPM.Models;
using System;

namespace VPM.Tests.Models
{
    public class DateFilterTests
    {
        [Fact]
        public void DateFilter_DefaultConstructor_InitializesWithAllTime()
        {
            var filter = new DateFilter();

            Assert.Equal(DateFilterType.AllTime, filter.FilterType);
            Assert.Null(filter.CustomStartDate);
            Assert.Null(filter.CustomEndDate);
        }

        [Fact]
        public void DisplayName_AllTime_ReturnsCorrectLabel()
        {
            var filter = new DateFilter { FilterType = DateFilterType.AllTime };

            Assert.Equal("All Time", filter.DisplayName);
        }

        [Fact]
        public void DisplayName_Today_ReturnsCorrectLabel()
        {
            var filter = new DateFilter { FilterType = DateFilterType.Today };

            Assert.Equal("Today", filter.DisplayName);
        }

        [Fact]
        public void DisplayName_PastWeek_ReturnsCorrectLabel()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastWeek };

            Assert.Equal("Past Week", filter.DisplayName);
        }

        [Fact]
        public void DisplayName_PastMonth_ReturnsCorrectLabel()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastMonth };

            Assert.Equal("Past Month", filter.DisplayName);
        }

        [Fact]
        public void DisplayName_Past3Months_ReturnsCorrectLabel()
        {
            var filter = new DateFilter { FilterType = DateFilterType.Past3Months };

            Assert.Equal("Past 3 Months", filter.DisplayName);
        }

        [Fact]
        public void DisplayName_PastYear_ReturnsCorrectLabel()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastYear };

            Assert.Equal("Past Year", filter.DisplayName);
        }

        [Fact]
        public void DisplayName_CustomRange_ReturnsCorrectLabel()
        {
            var filter = new DateFilter { FilterType = DateFilterType.CustomRange };

            Assert.Equal("Custom Range", filter.DisplayName);
        }

        [Fact]
        public void GetDateRange_AllTime_ReturnsNullRange()
        {
            var filter = new DateFilter { FilterType = DateFilterType.AllTime };

            var (startDate, endDate) = filter.GetDateRange();

            Assert.Null(startDate);
            Assert.Null(endDate);
        }

        [Fact]
        public void GetDateRange_Today_ReturnsTodayRange()
        {
            var filter = new DateFilter { FilterType = DateFilterType.Today };

            var (startDate, endDate) = filter.GetDateRange();

            var today = DateTime.Now.Date;
            Assert.NotNull(startDate);
            Assert.NotNull(endDate);
            Assert.Equal(today, startDate.Value.Date);
            Assert.Equal(today, endDate.Value.Date);
        }

        [Fact]
        public void GetDateRange_PastWeek_Returns7DayRange()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastWeek };

            var (startDate, endDate) = filter.GetDateRange();

            Assert.NotNull(startDate);
            Assert.NotNull(endDate);
            var daysDifference = (endDate.Value.Date - startDate.Value.Date).TotalDays;
            Assert.True(daysDifference >= 6);
        }

        [Fact]
        public void GetDateRange_PastMonth_Returns30DayRange()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastMonth };

            var (startDate, endDate) = filter.GetDateRange();

            Assert.NotNull(startDate);
            Assert.NotNull(endDate);
            var daysDifference = (endDate.Value.Date - startDate.Value.Date).TotalDays;
            Assert.True(daysDifference >= 29);
        }

        [Fact]
        public void GetDateRange_Past3Months_Returns90DayRange()
        {
            var filter = new DateFilter { FilterType = DateFilterType.Past3Months };

            var (startDate, endDate) = filter.GetDateRange();

            Assert.NotNull(startDate);
            Assert.NotNull(endDate);
            var daysDifference = (endDate.Value.Date - startDate.Value.Date).TotalDays;
            Assert.True(daysDifference >= 89);
        }

        [Fact]
        public void GetDateRange_PastYear_Returns365DayRange()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastYear };

            var (startDate, endDate) = filter.GetDateRange();

            Assert.NotNull(startDate);
            Assert.NotNull(endDate);
            var daysDifference = (endDate.Value.Date - startDate.Value.Date).TotalDays;
            Assert.True(daysDifference >= 364);
        }

        [Fact]
        public void GetDateRange_CustomRange_ReturnsCustomDates()
        {
            var startCustom = new DateTime(2024, 1, 1);
            var endCustom = new DateTime(2024, 6, 30);
            var filter = new DateFilter
            {
                FilterType = DateFilterType.CustomRange,
                CustomStartDate = startCustom,
                CustomEndDate = endCustom
            };

            var (startDate, endDate) = filter.GetDateRange();

            Assert.Equal(startCustom, startDate);
            Assert.Equal(endCustom, endDate);
        }

        [Fact]
        public void MatchesFilter_AllTime_ReturnsTrueForAnyDate()
        {
            var filter = new DateFilter { FilterType = DateFilterType.AllTime };

            Assert.True(filter.MatchesFilter(DateTime.Now));
            Assert.True(filter.MatchesFilter(DateTime.Now.AddYears(-10)));
            Assert.True(filter.MatchesFilter(null));
        }

        [Fact]
        public void MatchesFilter_Today_ReturnsTrueForTodayOnlyDate()
        {
            var filter = new DateFilter { FilterType = DateFilterType.Today };

            var today = DateTime.Now;
            var yesterday = DateTime.Now.AddDays(-1);
            var tomorrow = DateTime.Now.AddDays(1);

            Assert.True(filter.MatchesFilter(today));
            Assert.False(filter.MatchesFilter(yesterday));
            Assert.False(filter.MatchesFilter(tomorrow));
        }

        [Fact]
        public void MatchesFilter_PastWeek_ReturnsTrueForRecentDates()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastWeek };

            var today = DateTime.Now;
            var threeDaysAgo = DateTime.Now.AddDays(-3);
            var eightDaysAgo = DateTime.Now.AddDays(-8);

            Assert.True(filter.MatchesFilter(today));
            Assert.True(filter.MatchesFilter(threeDaysAgo));
            Assert.False(filter.MatchesFilter(eightDaysAgo));
        }

        [Fact]
        public void MatchesFilter_PastMonth_ReturnsTrueForLastMonthDates()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastMonth };

            var today = DateTime.Now;
            var fifteenDaysAgo = DateTime.Now.AddDays(-15);
            var sixtyDaysAgo = DateTime.Now.AddDays(-60);

            Assert.True(filter.MatchesFilter(today));
            Assert.True(filter.MatchesFilter(fifteenDaysAgo));
            Assert.False(filter.MatchesFilter(sixtyDaysAgo));
        }

        [Fact]
        public void MatchesFilter_CustomRange_ReturnsTrueOnlyInRange()
        {
            var startDate = new DateTime(2024, 1, 1);
            var endDate = new DateTime(2024, 12, 31);
            var filter = new DateFilter
            {
                FilterType = DateFilterType.CustomRange,
                CustomStartDate = startDate,
                CustomEndDate = endDate
            };

            Assert.True(filter.MatchesFilter(new DateTime(2024, 6, 15)));
            Assert.False(filter.MatchesFilter(new DateTime(2023, 12, 31)));
            Assert.False(filter.MatchesFilter(new DateTime(2025, 1, 1)));
        }

        [Fact]
        public void MatchesFilter_CustomRangeStartOnly_ReturnsTrueOnOrAfterStart()
        {
            var startDate = new DateTime(2024, 1, 1);
            var filter = new DateFilter
            {
                FilterType = DateFilterType.CustomRange,
                CustomStartDate = startDate,
                CustomEndDate = null
            };

            Assert.True(filter.MatchesFilter(new DateTime(2024, 6, 15)));
            Assert.True(filter.MatchesFilter(new DateTime(2025, 1, 1)));
            Assert.False(filter.MatchesFilter(new DateTime(2023, 12, 31)));
        }

        [Fact]
        public void MatchesFilter_CustomRangeEndOnly_ReturnsTrueOnOrBeforeEnd()
        {
            var endDate = new DateTime(2024, 12, 31);
            var filter = new DateFilter
            {
                FilterType = DateFilterType.CustomRange,
                CustomStartDate = null,
                CustomEndDate = endDate
            };

            Assert.True(filter.MatchesFilter(new DateTime(2024, 6, 15)));
            Assert.True(filter.MatchesFilter(new DateTime(2023, 1, 1)));
            Assert.False(filter.MatchesFilter(new DateTime(2025, 1, 1)));
        }

        [Fact]
        public void MatchesFilter_NullDate_ReturnsTrueOnlyForAllTime()
        {
            var filterAllTime = new DateFilter { FilterType = DateFilterType.AllTime };
            var filterToday = new DateFilter { FilterType = DateFilterType.Today };

            Assert.True(filterAllTime.MatchesFilter(null));
            Assert.False(filterToday.MatchesFilter(null));
        }

        [Fact]
        public void GetDescription_AllTime_ReturnsCorrectDescription()
        {
            var filter = new DateFilter { FilterType = DateFilterType.AllTime };

            var description = filter.GetDescription();

            Assert.Equal("Showing packages from all time periods", description);
        }

        [Fact]
        public void GetDescription_Today_ReturnsCorrectDescription()
        {
            var filter = new DateFilter { FilterType = DateFilterType.Today };

            var description = filter.GetDescription();

            Assert.Equal("Showing packages modified today", description);
        }

        [Fact]
        public void GetDescription_PastWeek_ReturnsCorrectDescription()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastWeek };

            var description = filter.GetDescription();

            Assert.Equal("Showing packages modified in the past 7 days", description);
        }

        [Fact]
        public void GetDescription_PastMonth_ReturnsCorrectDescription()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastMonth };

            var description = filter.GetDescription();

            Assert.Equal("Showing packages modified in the past 30 days", description);
        }

        [Fact]
        public void GetDescription_Past3Months_ReturnsCorrectDescription()
        {
            var filter = new DateFilter { FilterType = DateFilterType.Past3Months };

            var description = filter.GetDescription();

            Assert.Equal("Showing packages modified in the past 90 days", description);
        }

        [Fact]
        public void GetDescription_PastYear_ReturnsCorrectDescription()
        {
            var filter = new DateFilter { FilterType = DateFilterType.PastYear };

            var description = filter.GetDescription();

            Assert.Equal("Showing packages modified in the past year", description);
        }

        [Fact]
        public void GetDescription_CustomRangeWithBothDates_FormatsCorrectly()
        {
            var startDate = new DateTime(2024, 1, 15);
            var endDate = new DateTime(2024, 12, 25);
            var filter = new DateFilter
            {
                FilterType = DateFilterType.CustomRange,
                CustomStartDate = startDate,
                CustomEndDate = endDate
            };

            var description = filter.GetDescription();

            Assert.Contains("Jan", description);
            Assert.Contains("15", description);
            Assert.Contains("Dec", description);
            Assert.Contains("25", description);
            Assert.Contains("2024", description);
        }

        [Fact]
        public void GetDescription_CustomRangeWithStartOnly_FormatsCorrectly()
        {
            var startDate = new DateTime(2024, 1, 15);
            var filter = new DateFilter
            {
                FilterType = DateFilterType.CustomRange,
                CustomStartDate = startDate,
                CustomEndDate = null
            };

            var description = filter.GetDescription();

            Assert.Contains("Jan", description);
            Assert.Contains("15", description);
            Assert.Contains("onwards", description);
        }

        [Fact]
        public void GetDescription_CustomRangeWithEndOnly_FormatsCorrectly()
        {
            var endDate = new DateTime(2024, 12, 25);
            var filter = new DateFilter
            {
                FilterType = DateFilterType.CustomRange,
                CustomStartDate = null,
                CustomEndDate = endDate
            };

            var description = filter.GetDescription();

            Assert.Contains("Dec", description);
            Assert.Contains("25", description);
            Assert.Contains("up to", description);
        }

        [Fact]
        public void MatchesFilter_BoundaryDateStart_ReturnsTrueOnStartDate()
        {
            var startDate = new DateTime(2024, 1, 1);
            var filter = new DateFilter
            {
                FilterType = DateFilterType.CustomRange,
                CustomStartDate = startDate,
                CustomEndDate = new DateTime(2024, 12, 31)
            };

            Assert.True(filter.MatchesFilter(startDate));
        }

        [Fact]
        public void MatchesFilter_BoundaryDateEnd_ReturnsTrueOnEndDate()
        {
            var endDate = new DateTime(2024, 12, 31);
            var filter = new DateFilter
            {
                FilterType = DateFilterType.CustomRange,
                CustomStartDate = new DateTime(2024, 1, 1),
                CustomEndDate = endDate
            };

            Assert.True(filter.MatchesFilter(endDate));
        }

        [Fact]
        public void FilterType_CanBeChanged()
        {
            var filter = new DateFilter { FilterType = DateFilterType.AllTime };

            filter.FilterType = DateFilterType.Today;

            Assert.Equal(DateFilterType.Today, filter.FilterType);
        }

        [Fact]
        public void CustomDates_CanBeSetDirectly()
        {
            var startDate = new DateTime(2024, 1, 1);
            var endDate = new DateTime(2024, 12, 31);
            var filter = new DateFilter();

            filter.CustomStartDate = startDate;
            filter.CustomEndDate = endDate;

            Assert.Equal(startDate, filter.CustomStartDate);
            Assert.Equal(endDate, filter.CustomEndDate);
        }
    }
}
