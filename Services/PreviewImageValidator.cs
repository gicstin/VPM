using System;
using System.Collections.Generic;
using System.Linq;

namespace VPM.Services
{
    /// <summary>
    /// Static helper for validating whether an image path should be considered a preview image.
    /// Uses a simple pairing rule: if there are files with the same stem (filename without extension)
    /// where one is an image (jpg/png) and the other has a different extension, the image is a preview.
    /// 
    /// Examples:
    /// - Amelie.jpg + Amelie.json → Amelie.jpg is a preview
    /// - nico_dmc5.jpg + nico_dmc5.json → nico_dmc5.jpg is a preview
    /// - nico_boot_L2.jpg + nico_boot_L2.vab + nico_boot_L2.vaj + nico_boot_L2.vam → nico_boot_L2.jpg is a preview
    /// </summary>
    public static class PreviewImageValidator
    {
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

        /// <summary>
        /// Determines if an image filename should be indexed as a preview image.
        /// Uses file pairing logic: if there are multiple files with the same stem where one is an image,
        /// the image is considered a preview.
        /// 
        /// This method expects a flattened list of filenames (just filenames, no paths).
        /// Both imageFilename and allFilesInArchive should be normalized: lowercase.
        /// </summary>
        public static bool IsPreviewImage(string imageFilename, IEnumerable<string> allFilesInArchive)
        {
            if (string.IsNullOrEmpty(imageFilename) || allFilesInArchive == null)
                return false;

            var ext = System.IO.Path.GetExtension(imageFilename).ToLowerInvariant();
            
            // Must be an image file
            if (!ImageExtensions.Contains(ext))
                return false;

            // Get the stem (filename without extension)
            var stem = System.IO.Path.GetFileNameWithoutExtension(imageFilename).ToLowerInvariant();
            
            if (string.IsNullOrEmpty(stem))
                return false;

            // Check if there are other files with the same stem but different extension
            var hasMatchingFile = false;
            foreach (var file in allFilesInArchive)
            {
                if (string.IsNullOrEmpty(file) || file.Equals(imageFilename, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Files should already be normalized (lowercase, just filenames)
                var fileStem = System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                var fileExt = System.IO.Path.GetExtension(file).ToLowerInvariant();

                // Check if this file has the same stem but different extension
                if (fileStem == stem && fileExt != ext && !ImageExtensions.Contains(fileExt))
                {
                    hasMatchingFile = true;
                    break;
                }
            }

            return hasMatchingFile;
        }

        /// <summary>
        /// Overload for backward compatibility: checks if an image is a preview based on path patterns alone.
        /// This is a fallback when the full file list is not available.
        /// Returns true if the path looks like it could be a preview (conservative approach).
        /// </summary>
        public static bool IsPreviewImage(string pathNorm)
        {
            if (string.IsNullOrEmpty(pathNorm))
                return false;

            var ext = System.IO.Path.GetExtension(pathNorm).ToLowerInvariant();
            
            // Must be an image file
            if (!ImageExtensions.Contains(ext))
                return false;

            var pathLower = pathNorm.ToLowerInvariant();
            
            // Exclude images in texture directories (these are typically not previews)
            if (pathLower.Contains("/textures/") || pathLower.Contains("/texture/"))
                return false;

            // Exclude images in scripts and sounds directories
            if (pathLower.Contains("custom/scripts/") || pathLower.Contains("custom/sounds/"))
                return false;

            // Conservative approach: assume it's a preview if it's an image file
            // The pairing check will be done when full file list is available
            return true;
        }
    }
}
