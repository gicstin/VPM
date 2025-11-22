using Xunit;
using VPM.Services;
using VPM.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VPM.Tests.Services
{
    public class VarIntegrityScannerTests
    {
        [Fact]
        public void Constructor_CreatesNewInstance()
        {
            var scanner = new VarIntegrityScanner();

            Assert.NotNull(scanner);
        }

        [Fact]
        public void ValidateMetadata_HealthyMetadata_ReturnsNotDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 25,
                LicenseType = "CC0",
                Description = "Valid package"
            };
            metadata.ContentList.Add("Custom/Morphs/file.vmb");

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
            Assert.Empty(result.DamageReason);
        }

        [Fact]
        public void ValidateMetadata_CorruptedFlag_ReturnsDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata { IsCorrupted = true };

            var result = scanner.ValidateMetadata(metadata);

            Assert.True(result.IsDamaged);
            Assert.Equal("Corrupted or unreadable archive", result.DamageReason);
        }

        [Fact]
        public void ValidateMetadata_EmptyPackage_ReturnsDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata { FileCount = 0 };

            var result = scanner.ValidateMetadata(metadata);

            Assert.True(result.IsDamaged);
            Assert.Equal("Empty package (no content files)", result.DamageReason);
        }

        [Fact]
        public void ValidateMetadata_OnlyMetaJson_ReturnsDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata { FileCount = 1 };

            var result = scanner.ValidateMetadata(metadata);

            Assert.True(result.IsDamaged);
            Assert.Equal("Empty package (no content files)", result.DamageReason);
        }

        [Fact]
        public void ValidateMetadata_MissingMetaJson_ReturnsDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 10,
                LicenseType = "",
                Description = "",
                ContentTypes = new HashSet<string>(),
                Dependencies = new List<string>()
            };

            var result = scanner.ValidateMetadata(metadata);

            Assert.True(result.IsDamaged);
            Assert.Equal("Missing or empty meta.json", result.DamageReason);
        }

        [Fact]
        public void ValidateMetadata_WithLicenseType_NotDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 10,
                LicenseType = "CC0"
            };
            metadata.ContentList.Add("Custom/Morphs/file.vmb");

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
        }

        [Fact]
        public void ValidateMetadata_WithDescription_NotDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 10,
                Description = "Valid description"
            };
            metadata.ContentList.Add("Custom/Morphs/file.vmb");

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
        }

        [Fact]
        public void ValidateMetadata_WithDependencies_NotDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 10
            };
            metadata.Dependencies.Add("BaseDependency");
            metadata.ContentList.Add("Custom/Morphs/file.vmb");

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
        }

        [Fact]
        public void ValidateMetadata_WithContentTypes_NotDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 10
            };
            metadata.ContentTypes.Add("Morph");
            metadata.ContentList.Add("Custom/Morphs/file.vmb");

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
        }

        [Fact]
        public void ValidateMetadata_NoCustomOrSavesFolders_ReturnsDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 5,
                LicenseType = "CC0"
            };
            metadata.ContentList.Add("SomeFolder/file.txt");
            metadata.ContentList.Add("AnotherFolder/file.txt");

            var result = scanner.ValidateMetadata(metadata);

            Assert.True(result.IsDamaged);
            Assert.Equal("No Custom/ or Saves/ folders found", result.DamageReason);
        }

        [Fact]
        public void ValidateMetadata_WithCustomFolder_NotDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 5,
                LicenseType = "CC0"
            };
            metadata.ContentList.Add("Custom/Morphs/file.vmb");

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
        }

        [Fact]
        public void ValidateMetadata_WithSavesFolder_NotDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 5,
                LicenseType = "CC0"
            };
            metadata.ContentList.Add("Saves/Looks/look1.vam");

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
        }

        [Fact]
        public void ValidateMetadata_CaseInsensitiveFolderCheck()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 5,
                LicenseType = "CC0"
            };
            metadata.ContentList.Add("CUSTOM/Morphs/file.vmb");

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
        }

        [Fact]
        public void ValidateMetadata_EmptyContentList_ReturnsDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 5,
                LicenseType = "CC0",
                ContentList = new List<string>()
            };

            var result = scanner.ValidateMetadata(metadata);

            Assert.True(result.IsDamaged);
            Assert.Equal("No Custom/ or Saves/ folders found", result.DamageReason);
        }

        [Fact]
        public void ValidateMetadata_NullContentList_ReturnsDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 5,
                LicenseType = "CC0",
                ContentList = null
            };

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
        }

        [Fact]
        public void ScanPackage_ReturnsNotDamagedResult()
        {
            var scanner = new VarIntegrityScanner();

            var result = scanner.ScanPackage("nonexistent.var");

            Assert.False(result.IsDamaged);
            Assert.Empty(result.DamageReason);
        }

        [Fact]
        public void ScanPackageAsync_ReturnsNotDamagedResult()
        {
            var scanner = new VarIntegrityScanner();

            var result = scanner.ScanPackageAsync("nonexistent.var").Result;

            Assert.False(result.IsDamaged);
            Assert.Empty(result.DamageReason);
        }

        [Fact]
        public async Task ScanPackageAsync_CanBeAwaitedAsTask()
        {
            var scanner = new VarIntegrityScanner();

            var result = await scanner.ScanPackageAsync("test.var");

            Assert.NotNull(result);
            Assert.IsType<VarIntegrityScanner.IntegrityResult>(result);
        }

        [Fact]
        public void IntegrityResult_HasDefaultIssuesList()
        {
            var result = new VarIntegrityScanner.IntegrityResult();

            Assert.NotNull(result.Issues);
            Assert.Empty(result.Issues);
        }

        [Fact]
        public void IntegrityResult_CanPopulateIssues()
        {
            var result = new VarIntegrityScanner.IntegrityResult();
            result.Issues.Add("Issue1");
            result.Issues.Add("Issue2");

            Assert.Equal(2, result.Issues.Count);
        }

        [Fact]
        public void ValidateMetadata_MultipleValidMetaJsonIndicators()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 10,
                LicenseType = "CC0",
                Description = "Valid",
                Dependencies = new List<string> { "Base" },
                ContentTypes = new HashSet<string> { "Morph" }
            };
            metadata.ContentList.Add("Custom/Morphs/file.vmb");

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
        }

        [Fact]
        public void ValidateMetadata_FileCountExactlyOne_ReturnsDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 1,
                LicenseType = "CC0"
            };
            metadata.ContentList.Add("Custom/Morphs/file.vmb");

            var result = scanner.ValidateMetadata(metadata);

            Assert.True(result.IsDamaged);
        }

        [Fact]
        public void ValidateMetadata_FileCountTwoOrMore_ReturnsNotDamaged()
        {
            var scanner = new VarIntegrityScanner();
            var metadata = new VarMetadata
            {
                IsCorrupted = false,
                FileCount = 2,
                LicenseType = "CC0"
            };
            metadata.ContentList.Add("Custom/Morphs/file.vmb");

            var result = scanner.ValidateMetadata(metadata);

            Assert.False(result.IsDamaged);
        }
    }
}
