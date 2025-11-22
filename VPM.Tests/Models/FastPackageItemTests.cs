using Xunit;
using VPM.Models;
using System;

namespace VPM.Tests.Models
{
    public class FastPackageItemTests
    {
        [Fact]
        public void FastPackageItem_DefaultConstructor_InitializesWithDefaults()
        {
            var item = new FastPackageItem();

            Assert.Equal("", item.Name);
            Assert.Equal("", item.Status);
            Assert.Equal("", item.Creator);
            Assert.Equal(0, item.FileSize);
            Assert.Null(item.ModifiedDate);
            Assert.True(item.IsLatestVersion);
            Assert.Equal(0, item.DependencyCount);
            Assert.Equal(0, item.DependentsCount);
            Assert.False(item.IsOptimized);
        }

        [Fact]
        public void Create_ValidParameters_CreatesItemWithAllProperties()
        {
            var date = DateTime.Now;
            var item = FastPackageItem.Create("TestPackage", "Loaded", "Creator1", 1024000, date, true, 2, 1, true);

            Assert.Equal("TestPackage", item.Name);
            Assert.Equal("Loaded", item.Status);
            Assert.Equal("Creator1", item.Creator);
            Assert.Equal(1024000, item.FileSize);
            Assert.Equal(date, item.ModifiedDate);
            Assert.True(item.IsLatestVersion);
            Assert.Equal(2, item.DependencyCount);
            Assert.Equal(1, item.DependentsCount);
            Assert.True(item.IsOptimized);
        }

        [Fact]
        public void Create_PrecomputesDisplayName()
        {
            var item = FastPackageItem.Create("TestPackage", "Loaded", "Creator", 1024, null, true, 0);

            Assert.Equal("TestPackage", item.DisplayName);
        }

        [Fact]
        public void Create_PrecomputesFileSizeFormatted()
        {
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1024000, null, true, 0);

            Assert.NotNull(item.FileSizeFormatted);
            Assert.NotEmpty(item.FileSizeFormatted);
        }

        [Fact]
        public void Create_PrecomputesDateFormatted_WithDate()
        {
            var date = new DateTime(2024, 12, 25);
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1024, date, true, 0);

            Assert.NotNull(item.DateFormatted);
            Assert.Contains("Dec", item.DateFormatted);
            Assert.Contains("25", item.DateFormatted);
            Assert.Contains("2024", item.DateFormatted);
        }

        [Fact]
        public void Create_PrecomputesDateFormatted_WithoutDate()
        {
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1024, null, true, 0);

            Assert.Equal("Unknown", item.DateFormatted);
        }

        [Fact]
        public void Create_LoadedStatus_SetsCorrectStatusIcon()
        {
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1024, null, true, 0);

            Assert.Equal("✓", item.StatusIcon);
        }

        [Fact]
        public void Create_AvailableStatus_SetsCorrectStatusIcon()
        {
            var item = FastPackageItem.Create("Test", "Available", "Creator", 1024, null, true, 0);

            Assert.Equal("📦", item.StatusIcon);
        }

        [Fact]
        public void Create_MissingStatus_SetsCorrectStatusIcon()
        {
            var item = FastPackageItem.Create("Test", "Missing", "Creator", 1024, null, true, 0);

            Assert.Equal("✗", item.StatusIcon);
        }

        [Fact]
        public void Create_OutdatedStatus_SetsCorrectStatusIcon()
        {
            var item = FastPackageItem.Create("Test", "Outdated", "Creator", 1024, null, true, 0);

            Assert.Equal("⚠", item.StatusIcon);
        }

        [Fact]
        public void Create_UpdatingStatus_SetsCorrectStatusIcon()
        {
            var item = FastPackageItem.Create("Test", "Updating", "Creator", 1024, null, true, 0);

            Assert.Equal("↻", item.StatusIcon);
        }

        [Fact]
        public void Create_DuplicateStatus_SetsCorrectStatusIcon()
        {
            var item = FastPackageItem.Create("Test", "Duplicate", "Creator", 1024, null, true, 0);

            Assert.Equal("!", item.StatusIcon);
        }

        [Fact]
        public void Create_UnknownStatus_SetsDefaultStatusIcon()
        {
            var item = FastPackageItem.Create("Test", "Unknown", "Creator", 1024, null, true, 0);

            Assert.Equal("?", item.StatusIcon);
        }

        [Fact]
        public void Create_LoadedStatus_SetsCorrectColorHex()
        {
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1024, null, true, 0);

            Assert.Equal("#4CAF50", item.StatusColorHex);
        }

        [Fact]
        public void Create_AvailableStatus_SetsCorrectColorHex()
        {
            var item = FastPackageItem.Create("Test", "Available", "Creator", 1024, null, true, 0);

            Assert.Equal("#2196F3", item.StatusColorHex);
        }

        [Fact]
        public void Create_MissingStatus_SetsCorrectColorHex()
        {
            var item = FastPackageItem.Create("Test", "Missing", "Creator", 1024, null, true, 0);

            Assert.Equal("#F44336", item.StatusColorHex);
        }

        [Fact]
        public void Create_OutdatedStatus_SetsCorrectColorHex()
        {
            var item = FastPackageItem.Create("Test", "Outdated", "Creator", 1024, null, true, 0);

            Assert.Equal("#FF9800", item.StatusColorHex);
        }

        [Fact]
        public void Create_UpdatingStatus_SetsCorrectColorHex()
        {
            var item = FastPackageItem.Create("Test", "Updating", "Creator", 1024, null, true, 0);

            Assert.Equal("#9C27B0", item.StatusColorHex);
        }

        [Fact]
        public void Create_DuplicateStatus_SetsCorrectColorHex()
        {
            var item = FastPackageItem.Create("Test", "Duplicate", "Creator", 1024, null, true, 0);

            Assert.Equal("#FFEB3B", item.StatusColorHex);
        }

        [Fact]
        public void Create_UnknownStatus_SetsDefaultColorHex()
        {
            var item = FastPackageItem.Create("Test", "Unknown", "Creator", 1024, null, true, 0);

            Assert.Equal("#9E9E9E", item.StatusColorHex);
        }

        [Fact]
        public void Properties_CanBeSetDirectly()
        {
            var item = new FastPackageItem
            {
                Name = "DirectSet",
                Status = "Loaded",
                Creator = "Creator1",
                FileSize = 2048000,
                DependencyCount = 5
            };

            Assert.Equal("DirectSet", item.Name);
            Assert.Equal("Loaded", item.Status);
            Assert.Equal("Creator1", item.Creator);
            Assert.Equal(2048000, item.FileSize);
            Assert.Equal(5, item.DependencyCount);
        }

        [Fact]
        public void Create_WithoutOptimized_DefaultsFalse()
        {
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1024, null, true, 0);

            Assert.False(item.IsOptimized);
        }

        [Fact]
        public void Create_WithOptimized_SetsTrueWhenProvided()
        {
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1024, null, true, 0, 0, true);

            Assert.True(item.IsOptimized);
        }

        [Fact]
        public void Create_LargeFileSize_FormatsCorrectly()
        {
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1073741824, null, true, 0);

            Assert.NotNull(item.FileSizeFormatted);
            Assert.NotEmpty(item.FileSizeFormatted);
        }

        [Fact]
        public void Create_VerySmallFileSize_FormatsCorrectly()
        {
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1, null, true, 0);

            Assert.NotNull(item.FileSizeFormatted);
            Assert.NotEmpty(item.FileSizeFormatted);
        }

        [Fact]
        public void Create_NullDate_CorrectlyHandled()
        {
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1024, null, true, 0);

            Assert.Null(item.ModifiedDate);
            Assert.Equal("Unknown", item.DateFormatted);
        }

        [Fact]
        public void Create_WithDependents_SetsCorrectCount()
        {
            var item = FastPackageItem.Create("Test", "Loaded", "Creator", 1024, null, true, 3, 5);

            Assert.Equal(3, item.DependencyCount);
            Assert.Equal(5, item.DependentsCount);
        }
    }
}
