using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Service for scanning and parsing custom atom person files (.vap) from local folders
    /// </summary>
    public class CustomAtomPersonScanner
    {
        private readonly string _vamPath;

        public CustomAtomPersonScanner(string vamPath)
        {
            _vamPath = vamPath;
        }

        /// <summary>
        /// Scans the Custom\Atom\Person folder for .vap files
        /// </summary>
        public List<CustomAtomItem> ScanCustomAtomPerson()
        {
            var items = new List<CustomAtomItem>();
            var customPersonDir = Path.Combine(_vamPath, "Custom", "Atom", "Person");

            System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] Scanning: {customPersonDir}");

            if (!Directory.Exists(customPersonDir))
            {
                System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] Directory does not exist: {customPersonDir}");
                return items;
            }

            try
            {
                // Get all .vap files recursively from subfolders
                var vapFiles = Directory.GetFiles(customPersonDir, "*.vap", SearchOption.AllDirectories);
                System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] Found {vapFiles.Length} .vap files");

                foreach (var vapPath in vapFiles)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] Processing: {vapPath}");
                        var item = CreateCustomAtomItemFromFile(vapPath);
                        if (item != null)
                        {
                            items.Add(item);
                            System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] Added item: {item.DisplayName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] Error processing {vapPath}: {ex.Message}");
                    }
                }
                System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] Total items loaded: {items.Count}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CUSTOM ATOM] Error scanning folder: {ex.Message}");
            }

            return items;
        }

        /// <summary>
        /// Creates a CustomAtomItem from a .vap file
        /// </summary>
        private CustomAtomItem CreateCustomAtomItemFromFile(string vapPath)
        {
            var fileInfo = new FileInfo(vapPath);
            var fileName = Path.GetFileNameWithoutExtension(vapPath);
            
            // Extract subfolder structure relative to Custom\Atom\Person
            var customPersonDir = Path.Combine(_vamPath, "Custom", "Atom", "Person");
            var relativePath = Path.GetDirectoryName(vapPath).Substring(customPersonDir.Length).TrimStart(Path.DirectorySeparatorChar);
            
            // Extract category from subfolder (e.g., "Hair", "Clothing", "Morphs")
            var category = ExtractCategoryFromPath(vapPath, customPersonDir);

            var item = new CustomAtomItem
            {
                Name = fileInfo.Name,
                DisplayName = fileName,
                FilePath = vapPath,
                ThumbnailPath = FindThumbnail(vapPath),
                Category = category,
                Subfolder = relativePath,
                ModifiedDate = fileInfo.LastWriteTime,
                FileSize = fileInfo.Length
            };

            return item;
        }

        /// <summary>
        /// Extracts the category (Hair, Clothing, etc.) from the file path
        /// </summary>
        private string ExtractCategoryFromPath(string vapPath, string customPersonDir)
        {
            var relativePath = Path.GetDirectoryName(vapPath).Substring(customPersonDir.Length).ToLowerInvariant();
            
            if (relativePath.Contains("hair"))
                return "Hair";
            else if (relativePath.Contains("clothing"))
                return "Clothing";
            else if (relativePath.Contains("morphs"))
                return "Morphs";
            else if (relativePath.Contains("appearance"))
                return "Appearance";
            else if (relativePath.Contains("pose"))
                return "Pose";
            else if (relativePath.Contains("skin"))
                return "Skin";
            else if (relativePath.Contains("plugin"))
                return "Plugin";
            else if (relativePath.Contains("general"))
                return "General";
            else if (relativePath.Contains("breastphysics"))
                return "Breast Physics";
            else if (relativePath.Contains("glutephysics"))
                return "Glute Physics";
            else if (relativePath.Contains("animationpresets"))
                return "Animation Presets";
            
            return "Other";
        }

        /// <summary>
        /// Finds the thumbnail image for a .vap file
        /// </summary>
        private string FindThumbnail(string vapPath)
        {
            var basePath = Path.ChangeExtension(vapPath, null);
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
