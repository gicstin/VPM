using Xunit;
using VPM.Models;
using VPM.Services;
using System;

namespace VPM.Tests.Services
{
    public class SortingExtensionsTests
    {
        [Fact]
        public void GetDescription_PackageSortOptionName_ReturnsCorrectDescription()
        {
            var option = PackageSortOption.Name;

            var description = option.GetDescription();

            Assert.Equal("Name", description);
        }

        [Fact]
        public void GetDescription_PackageSortOptionDate_ReturnsCorrectDescription()
        {
            var option = PackageSortOption.Date;

            var description = option.GetDescription();

            Assert.Equal("Date", description);
        }

        [Fact]
        public void GetDescription_PackageSortOptionSize_ReturnsCorrectDescription()
        {
            var option = PackageSortOption.Size;

            var description = option.GetDescription();

            Assert.Equal("Size", description);
        }

        [Fact]
        public void GetDescription_PackageSortOptionDependencies_ReturnsCorrectDescription()
        {
            var option = PackageSortOption.Dependencies;

            var description = option.GetDescription();

            Assert.Equal("Dependencies", description);
        }

        [Fact]
        public void GetDescription_PackageSortOptionStatus_ReturnsCorrectDescription()
        {
            var option = PackageSortOption.Status;

            var description = option.GetDescription();

            Assert.Equal("Status", description);
        }

        [Fact]
        public void GetDescription_SceneSortOptionName_ReturnsCorrectDescription()
        {
            var option = SceneSortOption.Name;

            var description = option.GetDescription();

            Assert.Equal("Name", description);
        }

        [Fact]
        public void GetDescription_SceneSortOptionDate_ReturnsCorrectDescription()
        {
            var option = SceneSortOption.Date;

            var description = option.GetDescription();

            Assert.Equal("Date", description);
        }

        [Fact]
        public void GetDescription_DependencySortOptionName_ReturnsCorrectDescription()
        {
            var option = DependencySortOption.Name;

            var description = option.GetDescription();

            Assert.Equal("Name", description);
        }

        [Fact]
        public void GetDescription_FilterSortOptionName_ReturnsCorrectDescription()
        {
            var option = FilterSortOption.Name;

            var description = option.GetDescription();

            Assert.Equal("Name", description);
        }

        [Fact]
        public void GetDescription_FilterSortOptionCount_ReturnsCorrectDescription()
        {
            var option = FilterSortOption.Count;

            var description = option.GetDescription();

            Assert.Equal("Count", description);
        }

        [Fact]
        public void GetDisplayText_Ascending_IncludesAscendingArrow()
        {
            var option = PackageSortOption.Name;

            var displayText = option.GetDisplayText(true);

            Assert.Contains("Name", displayText);
            Assert.Contains("↑", displayText);
        }

        [Fact]
        public void GetDisplayText_Descending_IncludesDescendingArrow()
        {
            var option = PackageSortOption.Name;

            var displayText = option.GetDisplayText(false);

            Assert.Contains("Name", displayText);
            Assert.Contains("↓", displayText);
        }

        [Fact]
        public void GetDisplayText_AllPackageSortOptions_IncludesDescriptionAndArrow()
        {
            var options = SortingManager.GetPackageSortOptions();

            foreach (var option in options)
            {
                var displayTextAsc = option.GetDisplayText(true);
                var displayTextDesc = option.GetDisplayText(false);

                Assert.NotNull(displayTextAsc);
                Assert.NotNull(displayTextDesc);
                Assert.Contains("↑", displayTextAsc);
                Assert.Contains("↓", displayTextDesc);
            }
        }

        [Fact]
        public void GetDisplayText_AllSceneSortOptions_IncludesDescriptionAndArrow()
        {
            var options = SortingManager.GetSceneSortOptions();

            foreach (var option in options)
            {
                var displayTextAsc = option.GetDisplayText(true);
                var displayTextDesc = option.GetDisplayText(false);

                Assert.NotNull(displayTextAsc);
                Assert.NotNull(displayTextDesc);
                Assert.Contains("↑", displayTextAsc);
                Assert.Contains("↓", displayTextDesc);
            }
        }

        [Fact]
        public void GetDisplayText_AllDependencySortOptions_IncludesDescriptionAndArrow()
        {
            var options = SortingManager.GetDependencySortOptions();

            foreach (var option in options)
            {
                var displayTextAsc = option.GetDisplayText(true);
                var displayTextDesc = option.GetDisplayText(false);

                Assert.NotNull(displayTextAsc);
                Assert.NotNull(displayTextDesc);
                Assert.Contains("↑", displayTextAsc);
                Assert.Contains("↓", displayTextDesc);
            }
        }

        [Fact]
        public void SortingState_DefaultValues_InitializedCorrectly()
        {
            var state = new SortingState();

            Assert.True(state.IsAscending);
            Assert.NotNull(state.LastSortTime);
        }

        [Fact]
        public void SortingState_ConstructorWithValues_SetsPropertiesCorrectly()
        {
            var option = PackageSortOption.Name;
            var state = new SortingState(option, false);

            Assert.Equal(option, state.CurrentSortOption);
            Assert.False(state.IsAscending);
            Assert.NotNull(state.LastSortTime);
        }

        [Fact]
        public void SortingState_PropertiesCanBeModified()
        {
            var state = new SortingState();
            state.CurrentSortOption = PackageSortOption.Size;
            state.IsAscending = false;

            Assert.Equal(PackageSortOption.Size, state.CurrentSortOption);
            Assert.False(state.IsAscending);
        }

        [Fact]
        public void SerializableSortingState_DefaultValues_InitializedCorrectly()
        {
            var state = new SerializableSortingState();

            Assert.True(state.IsAscending);
        }

        [Fact]
        public void SerializableSortingState_ConstructorWithValues_SetsPropertiesCorrectly()
        {
            var state = new SerializableSortingState("PackageSortOption", "Name", false);

            Assert.Equal("PackageSortOption", state.SortOptionType);
            Assert.Equal("Name", state.SortOptionValue);
            Assert.False(state.IsAscending);
        }

        [Fact]
        public void GetPackageSortOptions_ReturnsAllOptions()
        {
            var options = SortingManager.GetPackageSortOptions();

            Assert.NotEmpty(options);
            Assert.Contains(PackageSortOption.Name, options);
            Assert.Contains(PackageSortOption.Date, options);
            Assert.Contains(PackageSortOption.Size, options);
        }

        [Fact]
        public void GetSceneSortOptions_ReturnsAllOptions()
        {
            var options = SortingManager.GetSceneSortOptions();

            Assert.NotEmpty(options);
            Assert.Contains(SceneSortOption.Name, options);
            Assert.Contains(SceneSortOption.Date, options);
        }

        [Fact]
        public void GetPresetSortOptions_ReturnsAllOptions()
        {
            var options = SortingManager.GetPresetSortOptions();

            Assert.NotEmpty(options);
            Assert.Contains(PresetSortOption.Name, options);
        }

        [Fact]
        public void GetDependencySortOptions_ReturnsAllOptions()
        {
            var options = SortingManager.GetDependencySortOptions();

            Assert.NotEmpty(options);
            Assert.Contains(DependencySortOption.Name, options);
        }

        [Fact]
        public void GetFilterSortOptions_ReturnsAllOptions()
        {
            var options = SortingManager.GetFilterSortOptions();

            Assert.NotEmpty(options);
            Assert.Contains(FilterSortOption.Name, options);
            Assert.Contains(FilterSortOption.Count, options);
        }

        [Fact]
        public void PackageSortOption_AllValuesHaveDescriptions()
        {
            var options = SortingManager.GetPackageSortOptions();

            foreach (var option in options)
            {
                var description = option.GetDescription();
                Assert.NotEmpty(description);
            }
        }

        [Fact]
        public void GetDisplayText_AscentAndDescent_ContainOppositeArrows()
        {
            var option = PackageSortOption.Name;
            var asc = option.GetDisplayText(true);
            var desc = option.GetDisplayText(false);

            Assert.NotEqual(asc, desc);
            Assert.Contains("↑", asc);
            Assert.Contains("↓", desc);
        }

        [Theory]
        [InlineData(PackageSortOption.Name)]
        [InlineData(PackageSortOption.Date)]
        [InlineData(PackageSortOption.Size)]
        [InlineData(PackageSortOption.Dependencies)]
        [InlineData(PackageSortOption.Status)]
        public void GetDescription_PackageSortOptions_ReturnNonEmptyStrings(PackageSortOption option)
        {
            var description = option.GetDescription();

            Assert.NotNull(description);
            Assert.NotEmpty(description);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void GetDisplayText_BothDirections_ReturnValidStrings(bool isAscending)
        {
            var option = PackageSortOption.Name;

            var displayText = option.GetDisplayText(isAscending);

            Assert.NotNull(displayText);
            Assert.NotEmpty(displayText);
        }
    }
}
