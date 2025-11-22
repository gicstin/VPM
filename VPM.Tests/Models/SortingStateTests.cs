using Xunit;
using VPM.Models;
using System;

namespace VPM.Tests.Models
{
    public class SortingStateTests
    {
        [Fact]
        public void SortingState_DefaultConstructor_InitializesProperties()
        {
            var state = new SortingState();

            Assert.Null(state.CurrentSortOption);
            Assert.True(state.IsAscending);
            Assert.NotNull(state.LastSortTime);
        }

        [Fact]
        public void SortingState_ConstructorWithOption_SetsProperties()
        {
            var option = PackageSortOption.Name;
            var state = new SortingState(option, true);

            Assert.Equal(option, state.CurrentSortOption);
            Assert.True(state.IsAscending);
            Assert.NotNull(state.LastSortTime);
        }

        [Fact]
        public void SortingState_ConstructorWithDescending_SetsIsAscendingFalse()
        {
            var option = PackageSortOption.Size;
            var state = new SortingState(option, false);

            Assert.Equal(option, state.CurrentSortOption);
            Assert.False(state.IsAscending);
        }

        [Fact]
        public void SortingState_LastSortTimeIsRecent()
        {
            var beforeCreation = DateTime.Now;
            var state = new SortingState();
            var afterCreation = DateTime.Now;

            Assert.True(state.LastSortTime >= beforeCreation);
            Assert.True(state.LastSortTime <= afterCreation);
        }

        [Fact]
        public void SortingState_CanModifyCurrentSortOption()
        {
            var state = new SortingState(PackageSortOption.Name, true);
            state.CurrentSortOption = PackageSortOption.Date;

            Assert.Equal(PackageSortOption.Date, state.CurrentSortOption);
        }

        [Fact]
        public void SortingState_CanToggleIsAscending()
        {
            var state = new SortingState(PackageSortOption.Name, true);
            Assert.True(state.IsAscending);

            state.IsAscending = false;
            Assert.False(state.IsAscending);

            state.IsAscending = true;
            Assert.True(state.IsAscending);
        }

        [Fact]
        public void SortingState_CanUpdateLastSortTime()
        {
            var state = new SortingState();
            var originalTime = state.LastSortTime;

            System.Threading.Thread.Sleep(10);
            var newTime = DateTime.Now;
            state.LastSortTime = newTime;

            Assert.NotEqual(originalTime, state.LastSortTime);
            Assert.Equal(newTime, state.LastSortTime);
        }

        [Fact]
        public void SortingState_WithNullOption_AllowsNullCurrentSortOption()
        {
            var state = new SortingState(null, true);

            Assert.Null(state.CurrentSortOption);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SortingState_WithDifferentDirections_SetsCorrectly(bool isAscending)
        {
            var state = new SortingState(PackageSortOption.Name, isAscending);

            Assert.Equal(isAscending, state.IsAscending);
        }

        [Fact]
        public void SortingState_DifferentInstances_HaveDifferentTimes()
        {
            var state1 = new SortingState();
            System.Threading.Thread.Sleep(5);
            var state2 = new SortingState();

            Assert.NotEqual(state1.LastSortTime, state2.LastSortTime);
        }
    }

    public class SerializableSortingStateTests
    {
        [Fact]
        public void SerializableSortingState_DefaultConstructor_InitializesProperties()
        {
            var state = new SerializableSortingState();

            Assert.Null(state.SortOptionType);
            Assert.Null(state.SortOptionValue);
            Assert.True(state.IsAscending);
        }

        [Fact]
        public void SerializableSortingState_ConstructorWithValues_SetsProperties()
        {
            var state = new SerializableSortingState("PackageSortOption", "Name", false);

            Assert.Equal("PackageSortOption", state.SortOptionType);
            Assert.Equal("Name", state.SortOptionValue);
            Assert.False(state.IsAscending);
        }

        [Fact]
        public void SerializableSortingState_CanModifyType()
        {
            var state = new SerializableSortingState("PackageSortOption", "Name", true);
            state.SortOptionType = "SceneSortOption";

            Assert.Equal("SceneSortOption", state.SortOptionType);
        }

        [Fact]
        public void SerializableSortingState_CanModifyValue()
        {
            var state = new SerializableSortingState("PackageSortOption", "Name", true);
            state.SortOptionValue = "Date";

            Assert.Equal("Date", state.SortOptionValue);
        }

        [Fact]
        public void SerializableSortingState_CanToggleDirection()
        {
            var state = new SerializableSortingState("PackageSortOption", "Name", true);
            Assert.True(state.IsAscending);

            state.IsAscending = false;
            Assert.False(state.IsAscending);
        }

        [Fact]
        public void SerializableSortingState_WithNullType_AllowsNull()
        {
            var state = new SerializableSortingState(null, "Name", true);

            Assert.Null(state.SortOptionType);
        }

        [Fact]
        public void SerializableSortingState_WithNullValue_AllowsNull()
        {
            var state = new SerializableSortingState("PackageSortOption", null, true);

            Assert.Null(state.SortOptionValue);
        }

        [Fact]
        public void SerializableSortingState_WithEmptyStrings_AllowsEmpty()
        {
            var state = new SerializableSortingState("", "", false);

            Assert.Equal("", state.SortOptionType);
            Assert.Equal("", state.SortOptionValue);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SerializableSortingState_WithDifferentDirections_SetsCorrectly(bool isAscending)
        {
            var state = new SerializableSortingState("PackageSortOption", "Name", isAscending);

            Assert.Equal(isAscending, state.IsAscending);
        }

        [Fact]
        public void SerializableSortingState_CanSerializeAndDeserialize()
        {
            var original = new SerializableSortingState("PackageSortOption", "Size", false);

            var copy = new SerializableSortingState(
                original.SortOptionType,
                original.SortOptionValue,
                original.IsAscending);

            Assert.Equal(original.SortOptionType, copy.SortOptionType);
            Assert.Equal(original.SortOptionValue, copy.SortOptionValue);
            Assert.Equal(original.IsAscending, copy.IsAscending);
        }
    }
}
