using Xunit;
using VPM.Services;
using System.Collections.Generic;
using System.Linq;

namespace VPM.Tests.Services
{
    public class PreviewImageValidatorTests
    {
        [Fact]
        public void IsPreviewImage_WithJpgImageAndMatchingStemFile_ReturnsTrue()
        {
            var imageFilename = "amelie.jpg";
            var allFiles = new[] { "amelie.jpg", "amelie.json" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_WithJpegImageAndMatchingFiles_ReturnsTrue()
        {
            var imageFilename = "preview.jpeg";
            var allFiles = new[] { "preview.jpeg", "preview.vam" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_WithPngImageAndMatchingFiles_ReturnsTrue()
        {
            var imageFilename = "character.png";
            var allFiles = new[] { "character.png", "character.json", "character.fav" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_WithImageButNoMatchingFiles_ReturnsFalse()
        {
            var imageFilename = "standalone.jpg";
            var allFiles = new[] { "standalone.jpg", "other.json" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_WithNonImageFile_ReturnsFalse()
        {
            var nonImageFile = "document.json";
            var allFiles = new[] { "document.json", "other.txt" };

            var result = PreviewImageValidator.IsPreviewImage(nonImageFile, allFiles);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_WithNullFilename_ReturnsFalse()
        {
            var allFiles = new[] { "some.jpg", "some.json" };

            var result = PreviewImageValidator.IsPreviewImage(null, allFiles);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_WithEmptyFilename_ReturnsFalse()
        {
            var allFiles = new[] { "some.jpg", "some.json" };

            var result = PreviewImageValidator.IsPreviewImage("", allFiles);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_WithNullFilesList_ReturnsFalse()
        {
            var imageFilename = "image.jpg";

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, null);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_WithComplexStemAndMultipleFiles_ReturnsTrue()
        {
            var imageFilename = "nico_dmc5.jpg";
            var allFiles = new[] { 
                "nico_dmc5.jpg", 
                "nico_dmc5.json", 
                "nico_dmc5.fav", 
                "other_file.txt" 
            };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_WithMultipleStemMatches_ReturnsTrue()
        {
            var imageFilename = "nico_boot_L2.jpg";
            var allFiles = new[] {
                "nico_boot_L2.jpg",
                "nico_boot_L2.vab",
                "nico_boot_L2.vaj",
                "nico_boot_L2.vam"
            };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_CaseInsensitive_ReturnsTrue()
        {
            var imageFilename = "Image.JPG";
            var allFiles = new[] { "image.jpg", "image.json" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_WithImageExtensionAsFileStem_ReturnsFalse()
        {
            var imageFilename = ".jpg";
            var allFiles = new[] { ".jpg", ".json" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_IgnoresSelfInFilesList_ReturnsTrue()
        {
            var imageFilename = "preview.jpg";
            var allFiles = new[] { "preview.jpg", "preview.jpg", "preview.json" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_WithEmptyFilesList_ReturnsFalse()
        {
            var imageFilename = "image.jpg";
            var allFiles = new string[] { };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_WithOnlyImageFileInList_ReturnsFalse()
        {
            var imageFilename = "image.jpg";
            var allFiles = new[] { "image.jpg" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_WithImageAndImageExtensionMatchingFile_ReturnsFalse()
        {
            var imageFilename = "preview.jpg";
            var allFiles = new[] { "preview.jpg", "preview.png" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_PathOverload_WithImageInRegularPath_ReturnsTrue()
        {
            var pathNorm = "Custom/Image/preview.jpg";

            var result = PreviewImageValidator.IsPreviewImage(pathNorm);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_PathOverload_WithImageInTextureDirectory_ReturnsFalse()
        {
            var pathNorm = "/textures/normal_map.jpg";

            var result = PreviewImageValidator.IsPreviewImage(pathNorm);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_PathOverload_WithImageInScriptsDirectory_ReturnsFalse()
        {
            var pathNorm = "Custom/scripts/script_image.jpg";

            var result = PreviewImageValidator.IsPreviewImage(pathNorm);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_PathOverload_WithImageInSoundsDirectory_ReturnsFalse()
        {
            var pathNorm = "Custom/sounds/sound_image.png";

            var result = PreviewImageValidator.IsPreviewImage(pathNorm);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_PathOverload_WithNullPath_ReturnsFalse()
        {
            var result = PreviewImageValidator.IsPreviewImage((string)null);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_PathOverload_WithEmptyPath_ReturnsFalse()
        {
            var result = PreviewImageValidator.IsPreviewImage("");

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_PathOverload_WithNonImageFile_ReturnsFalse()
        {
            var pathNorm = "Custom/Scripts/config.json";

            var result = PreviewImageValidator.IsPreviewImage(pathNorm);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_PathOverload_WithTextureTexturePath_ReturnsFalse()
        {
            var pathNorm = "Custom/Atom/Person/Appearance/texture/albedo.jpg";

            var result = PreviewImageValidator.IsPreviewImage(pathNorm);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_MethodOverload_Disambiguation()
        {
            var imageFilename = "preview.jpg";
            var fileList = new List<string> { "preview.jpg", "preview.json" };

            var result1 = PreviewImageValidator.IsPreviewImage(imageFilename, fileList);
            var result2 = PreviewImageValidator.IsPreviewImage(imageFilename);

            Assert.True(result1);
            Assert.True(result2);
        }

        [Fact]
        public void IsPreviewImage_WithWhitespaceFilename_ReturnsFalse()
        {
            var imageFilename = "   .jpg";
            var allFiles = new[] { "   .jpg", "file.json" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.False(result);
        }

        [Fact]
        public void IsPreviewImage_WithLargeFileList_PerformsEfficiently()
        {
            var imageFilename = "preview.jpg";
            var largeFileList = Enumerable.Range(0, 1000)
                .Select(i => $"file{i}.vam")
                .Concat(new[] { "preview.jpg", "preview.json" })
                .ToList();

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, largeFileList);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_WithNullFilesInList_IgnoresNulls()
        {
            var imageFilename = "preview.jpg";
            var allFiles = new string[] { "preview.jpg", null, "preview.json" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_WithDifferentImageExtensions_RecognizesAll()
        {
            var jpgResult = PreviewImageValidator.IsPreviewImage("image.jpg", new[] { "image.jpg", "image.json" });
            var jpegResult = PreviewImageValidator.IsPreviewImage("image.jpeg", new[] { "image.jpeg", "image.json" });
            var pngResult = PreviewImageValidator.IsPreviewImage("image.png", new[] { "image.png", "image.json" });

            Assert.True(jpgResult);
            Assert.True(jpegResult);
            Assert.True(pngResult);
        }

        [Fact]
        public void IsPreviewImage_WithCompoundExtension_MatchesStemCorrectly()
        {
            var imageFilename = "data.preset.jpg";
            var allFiles = new[] { "data.preset.jpg", "data.preset.vap" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.True(result);
        }

        [Fact]
        public void IsPreviewImage_WithOnlyImageAndOtherImage_ReturnsFalse()
        {
            var imageFilename = "preview1.jpg";
            var allFiles = new[] { "preview1.jpg", "preview2.jpg" };

            var result = PreviewImageValidator.IsPreviewImage(imageFilename, allFiles);

            Assert.False(result);
        }
    }
}
