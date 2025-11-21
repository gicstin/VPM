using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Scanner for custom package files (.vam, .vab, .vaj) from Custom folder
    /// Scans multiple content folders: Assets, Atom\Person, Clothing, Hair, SubScene
    /// Packages are identified by .vam files with accompanying .jpg preview images
    /// </summary>
    public class CustomPackageScanner
    {
        private readonly string _vamPath;

        public CustomPackageScanner(string vamPath)
        {
            _vamPath = vamPath;
        }

        /// <summary>
        /// Scans all custom content folders for .vam package files
        /// </summary>
        public List<CustomAtomItem> ScanCustomPackages()
        {
            var items = new List<CustomAtomItem>();
            var customBasePath = Path.Combine(_vamPath, "Custom");

            if (!Directory.Exists(customBasePath))
                return items;

            // Define the folders to scan
            var foldersToScan = new[]
            {
                Path.Combine(customBasePath, "Assets"),
                Path.Combine(customBasePath, "Atom", "Person"),
                Path.Combine(customBasePath, "Clothing"),
                Path.Combine(customBasePath, "Hair"),
                Path.Combine(customBasePath, "SubScene")
            };

            foreach (var folderPath in foldersToScan)
            {
                if (Directory.Exists(folderPath))
                {
                    try
                    {
                        var folderItems = ScanFolderForPackages(folderPath, customBasePath);
                        items.AddRange(folderItems);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error scanning folder {folderPath}: {ex.Message}");
                    }
                }
            }

            return items;
        }

        /// <summary>
        /// Scans a specific folder for .vam package files
        /// </summary>
        private List<CustomAtomItem> ScanFolderForPackages(string folderPath, string customBasePath)
        {
            var items = new List<CustomAtomItem>();

            try
            {
                // Get all .vam files recursively from this folder and its subfolders
                var vamFiles = Directory.GetFiles(folderPath, "*.vam", SearchOption.AllDirectories);

                foreach (var vamPath in vamFiles)
                {
                    try
                    {
                        var item = CreateCustomPackageItemFromFile(vamPath, customBasePath);
                        if (item != null)
                        {
                            items.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error processing file {vamPath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning folder {folderPath}: {ex.Message}");
            }

            return items;
        }

        /// <summary>
        /// Creates a CustomAtomItem from a .vam package file
        /// </summary>
        private CustomAtomItem CreateCustomPackageItemFromFile(string vamPath, string customBasePath)
        {
            var fileInfo = new FileInfo(vamPath);
            var fileName = Path.GetFileNameWithoutExtension(vamPath);

            // Extract subfolder structure relative to Custom folder
            var relativePath = Path.GetDirectoryName(vamPath).Substring(customBasePath.Length).TrimStart(Path.DirectorySeparatorChar);

            // Extract category from the folder structure
            var category = ExtractCategoryFromPath(vamPath, customBasePath);

            var item = new CustomAtomItem
            {
                Name = fileInfo.Name,
                DisplayName = fileName,
                FilePath = vamPath,
                ThumbnailPath = FindThumbnail(vamPath),
                Category = category,
                Subfolder = relativePath,
                ModifiedDate = fileInfo.LastWriteTime,
                FileSize = fileInfo.Length,
                ContentType = "Package"
            };

            // Parse dependencies from the .vam file if it's a JSON-based package
            PresetScanner.ParsePresetDependencies(item);

            return item;
        }

        /// <summary>
        /// Extracts the category from the file path
        /// </summary>
        private string ExtractCategoryFromPath(string vamPath, string customBasePath)
        {
            var relativePath = Path.GetDirectoryName(vamPath).Substring(customBasePath.Length).ToLowerInvariant();

            if (relativePath.Contains("\\assets") || relativePath.Contains("/assets"))
                return "Assets";
            else if (relativePath.Contains("\\atom\\person") || relativePath.Contains("/atom/person"))
                return "Atom Person";
            else if (relativePath.Contains("\\clothing") || relativePath.Contains("/clothing"))
                return "Clothing";
            else if (relativePath.Contains("\\hair") || relativePath.Contains("/hair"))
                return "Hair";
            else if (relativePath.Contains("\\subscene") || relativePath.Contains("/subscene"))
                return "SubScene";

            return "Other";
        }

        /// <summary>
        /// Finds the thumbnail image for a .vam file
        /// Looks for .jpg file with the same base name
        /// </summary>
        private string FindThumbnail(string vamPath)
        {
            var basePath = Path.ChangeExtension(vamPath, null);
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".JPG", ".JPEG", ".PNG" };

            foreach (var ext in extensions)
            {
                var thumbPath = basePath + ext;
                if (File.Exists(thumbPath))
                {
                    return thumbPath;
                }
            }

            return "";
        }
    }
}
