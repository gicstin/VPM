using Xunit;
using VPM.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace VPM.Tests.Models
{
    public class VarMetadataTests
    {
        [Fact]
        public void Constructor_CreatesDefaultMetadata()
        {
            var metadata = new VarMetadata();

            Assert.NotNull(metadata);
            Assert.Equal("", metadata.Filename);
            Assert.Equal("", metadata.PackageName);
            Assert.Equal("", metadata.CreatorName);
            Assert.Equal("", metadata.Description);
            Assert.Equal(1, metadata.Version);
        }

        [Fact]
        public void Dependencies_InitializedAsEmptyList()
        {
            var metadata = new VarMetadata();

            Assert.NotNull(metadata.Dependencies);
            Assert.Empty(metadata.Dependencies);
        }

        [Fact]
        public void Dependencies_CanAddItems()
        {
            var metadata = new VarMetadata();
            metadata.Dependencies.Add("Dependency1");
            metadata.Dependencies.Add("Dependency2");

            Assert.Equal(2, metadata.Dependencies.Count);
            Assert.Contains("Dependency1", metadata.Dependencies);
        }

        [Fact]
        public void ContentList_InitializedAsEmptyList()
        {
            var metadata = new VarMetadata();

            Assert.NotNull(metadata.ContentList);
            Assert.Empty(metadata.ContentList);
        }

        [Fact]
        public void ContentList_CanAddItems()
        {
            var metadata = new VarMetadata();
            metadata.ContentList.Add("Custom/Morphs");
            metadata.ContentList.Add("Saves/Looks");

            Assert.Equal(2, metadata.ContentList.Count);
        }

        [Fact]
        public void ContentTypes_InitializedAsEmptyHashSet()
        {
            var metadata = new VarMetadata();

            Assert.NotNull(metadata.ContentTypes);
            Assert.Empty(metadata.ContentTypes);
        }

        [Fact]
        public void ContentTypes_CanAddItems()
        {
            var metadata = new VarMetadata();
            metadata.ContentTypes.Add("Morph");
            metadata.ContentTypes.Add("Clothing");

            Assert.Equal(2, metadata.ContentTypes.Count);
            Assert.Contains("Morph", metadata.ContentTypes);
        }

        [Fact]
        public void Categories_InitializedAsEmptyHashSet()
        {
            var metadata = new VarMetadata();

            Assert.NotNull(metadata.Categories);
            Assert.Empty(metadata.Categories);
        }

        [Fact]
        public void Categories_CanAddItems()
        {
            var metadata = new VarMetadata();
            metadata.Categories.Add("Male");
            metadata.Categories.Add("Clothing");

            Assert.Equal(2, metadata.Categories.Count);
        }

        [Fact]
        public void UserTags_InitializedAsEmptyList()
        {
            var metadata = new VarMetadata();

            Assert.NotNull(metadata.UserTags);
            Assert.Empty(metadata.UserTags);
        }

        [Fact]
        public void FileCount_DefaultIsZero()
        {
            var metadata = new VarMetadata();

            Assert.Equal(0, metadata.FileCount);
        }

        [Fact]
        public void FileCount_CanBeSet()
        {
            var metadata = new VarMetadata();
            metadata.FileCount = 42;

            Assert.Equal(42, metadata.FileCount);
        }

        [Fact]
        public void CreatedDate_DefaultIsNull()
        {
            var metadata = new VarMetadata();

            Assert.Null(metadata.CreatedDate);
        }

        [Fact]
        public void CreatedDate_CanBeSet()
        {
            var metadata = new VarMetadata();
            var date = new DateTime(2023, 1, 15);
            metadata.CreatedDate = date;

            Assert.Equal(date, metadata.CreatedDate);
        }

        [Fact]
        public void ModifiedDate_DefaultIsNull()
        {
            var metadata = new VarMetadata();

            Assert.Null(metadata.ModifiedDate);
        }

        [Fact]
        public void IsOptimized_DefaultIsFalse()
        {
            var metadata = new VarMetadata();

            Assert.False(metadata.IsOptimized);
        }

        [Fact]
        public void OptimizationFlags_AllDefaultToFalse()
        {
            var metadata = new VarMetadata();

            Assert.False(metadata.HasTextureOptimization);
            Assert.False(metadata.HasHairOptimization);
            Assert.False(metadata.HasMirrorOptimization);
            Assert.False(metadata.HasJsonMinification);
        }

        [Fact]
        public void OptimizationFlags_CanBeSet()
        {
            var metadata = new VarMetadata();
            metadata.HasTextureOptimization = true;
            metadata.HasHairOptimization = true;

            Assert.True(metadata.HasTextureOptimization);
            Assert.True(metadata.HasHairOptimization);
            Assert.False(metadata.HasMirrorOptimization);
        }

        [Fact]
        public void DuplicateTracking_DefaultValues()
        {
            var metadata = new VarMetadata();

            Assert.False(metadata.IsDuplicate);
            Assert.Equal(1, metadata.DuplicateLocationCount);
        }

        [Fact]
        public void DuplicateTracking_CanBeModified()
        {
            var metadata = new VarMetadata();
            metadata.IsDuplicate = true;
            metadata.DuplicateLocationCount = 3;

            Assert.True(metadata.IsDuplicate);
            Assert.Equal(3, metadata.DuplicateLocationCount);
        }

        [Fact]
        public void VersionTracking_DefaultValues()
        {
            var metadata = new VarMetadata();

            Assert.False(metadata.IsOldVersion);
            Assert.Equal(1, metadata.LatestVersionNumber);
        }

        [Fact]
        public void VersionTracking_CanBeModified()
        {
            var metadata = new VarMetadata();
            metadata.IsOldVersion = true;
            metadata.LatestVersionNumber = 5;
            metadata.PackageBaseName = "MyPackage";

            Assert.True(metadata.IsOldVersion);
            Assert.Equal(5, metadata.LatestVersionNumber);
            Assert.Equal("MyPackage", metadata.PackageBaseName);
        }

        [Fact]
        public void IntegrityTracking_DefaultValues()
        {
            var metadata = new VarMetadata();

            Assert.False(metadata.IsDamaged);
            Assert.Equal("", metadata.DamageReason);
        }

        [Fact]
        public void IntegrityTracking_CanBeModified()
        {
            var metadata = new VarMetadata();
            metadata.IsDamaged = true;
            metadata.DamageReason = "Corrupted archive";

            Assert.True(metadata.IsDamaged);
            Assert.Equal("Corrupted archive", metadata.DamageReason);
        }

        [Fact]
        public void ContentCountProperties_DefaultToZero()
        {
            var metadata = new VarMetadata();

            Assert.Equal(0, metadata.MorphCount);
            Assert.Equal(0, metadata.HairCount);
            Assert.Equal(0, metadata.ClothingCount);
            Assert.Equal(0, metadata.SceneCount);
            Assert.Equal(0, metadata.LooksCount);
            Assert.Equal(0, metadata.PosesCount);
            Assert.Equal(0, metadata.AssetsCount);
            Assert.Equal(0, metadata.ScriptsCount);
            Assert.Equal(0, metadata.PluginsCount);
            Assert.Equal(0, metadata.SubScenesCount);
            Assert.Equal(0, metadata.SkinsCount);
        }

        [Fact]
        public void ContentCountProperties_CanBeSet()
        {
            var metadata = new VarMetadata();
            metadata.MorphCount = 5;
            metadata.ClothingCount = 3;
            metadata.SceneCount = 2;

            Assert.Equal(5, metadata.MorphCount);
            Assert.Equal(3, metadata.ClothingCount);
            Assert.Equal(2, metadata.SceneCount);
        }

        [Fact]
        public void AllFiles_InitializedAsEmptyList()
        {
            var metadata = new VarMetadata();

            Assert.NotNull(metadata.AllFiles);
            Assert.Empty(metadata.AllFiles);
        }

        [Fact]
        public void AllFiles_CanBePopulated()
        {
            var metadata = new VarMetadata();
            metadata.AllFiles.Add("Custom/Morphs/file1.vmb");
            metadata.AllFiles.Add("Custom/Morphs/file2.vmb");
            metadata.AllFiles.Add("Saves/Looks/look1.vam");

            Assert.Equal(3, metadata.AllFiles.Count);
        }

        [Fact]
        public void FilePath_DefaultIsEmpty()
        {
            var metadata = new VarMetadata();

            Assert.Equal("", metadata.FilePath);
        }

        [Fact]
        public void FileSize_DefaultIsZero()
        {
            var metadata = new VarMetadata();

            Assert.Equal(0, metadata.FileSize);
        }

        [Fact]
        public void FileSize_CanBeSet()
        {
            var metadata = new VarMetadata();
            metadata.FileSize = 1024 * 1024 * 50;

            Assert.Equal(1024 * 1024 * 50, metadata.FileSize);
        }

        [Fact]
        public void Status_DefaultIsUnknown()
        {
            var metadata = new VarMetadata();

            Assert.Equal("Unknown", metadata.Status);
        }

        [Fact]
        public void Status_CanBeSet()
        {
            var metadata = new VarMetadata();
            metadata.Status = "Loaded";

            Assert.Equal("Loaded", metadata.Status);
        }

        [Fact]
        public void LicenseType_DefaultIsEmpty()
        {
            var metadata = new VarMetadata();

            Assert.Equal("", metadata.LicenseType);
        }

        [Fact]
        public void IsCorrupted_DefaultIsFalse()
        {
            var metadata = new VarMetadata();

            Assert.False(metadata.IsCorrupted);
        }

        [Fact]
        public void PreloadMorphs_DefaultIsFalse()
        {
            var metadata = new VarMetadata();

            Assert.False(metadata.PreloadMorphs);
        }

        [Fact]
        public void IsMorphAsset_DefaultIsFalse()
        {
            var metadata = new VarMetadata();

            Assert.False(metadata.IsMorphAsset);
        }

        [Fact]
        public void VariantRole_DefaultIsUnknown()
        {
            var metadata = new VarMetadata();

            Assert.Equal("Unknown", metadata.VariantRole);
        }

        [Fact]
        public void MultipleInstances_AreIndependent()
        {
            var metadata1 = new VarMetadata { PackageName = "Package1" };
            var metadata2 = new VarMetadata { PackageName = "Package2" };

            metadata1.Dependencies.Add("Dep1");

            Assert.Equal("Package1", metadata1.PackageName);
            Assert.Equal("Package2", metadata2.PackageName);
            Assert.Single(metadata1.Dependencies);
            Assert.Empty(metadata2.Dependencies);
        }

        [Fact]
        public void Serializable_AttributePresent()
        {
            var metadataType = typeof(VarMetadata);
            var attributes = metadataType.GetCustomAttributes(typeof(SerializableAttribute), false);

            Assert.NotEmpty(attributes);
        }

        [Fact]
        public void CompleteMetadata_CanBePopulated()
        {
            var metadata = new VarMetadata
            {
                Filename = "package.var",
                PackageName = "TestPackage",
                CreatorName = "Creator",
                Description = "A test package",
                Version = 2,
                LicenseType = "CC0",
                FileCount = 25,
                MorphCount = 5,
                ClothingCount = 3,
                Status = "Loaded",
                FilePath = "C:\\VAM\\Packages\\package.var",
                FileSize = 50 * 1024 * 1024
            };

            metadata.Dependencies.AddRange(new[] { "Base", "Addon1" });
            metadata.ContentTypes.Add("Morph");
            metadata.Categories.Add("Male");

            Assert.Equal("TestPackage", metadata.PackageName);
            Assert.Equal(2, metadata.Version);
            Assert.Equal(5, metadata.MorphCount);
            Assert.Equal(2, metadata.Dependencies.Count);
            Assert.Single(metadata.ContentTypes);
            Assert.Single(metadata.Categories);
        }
    }
}
