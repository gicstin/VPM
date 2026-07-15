using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VPM.Models;
using SharpCompress.Archives.Zip;

namespace VPM.Services
{
    /// <summary>
    /// Unified scanner for custom atom presets, scenes, person appearances, and custom packages
    /// Scans:
    /// - Custom\Atom\Person folder for .vap preset files
    /// - Saves\scene folder for .json scene files
    /// - Saves\Person\appearance folder for .json person appearance files
    /// - Custom\Assets, Custom\Clothing, Custom\Hair, Custom\SubScene folders for .vam package files
    /// </summary>
    public class UnifiedCustomContentScanner
    {
        private readonly string _vamPath;
        private readonly CustomPackageScanner _customPackageScanner;

        public UnifiedCustomContentScanner(string vamPath)
        {
            _vamPath = vamPath;
            _customPackageScanner = new CustomPackageScanner(vamPath);
        }

        /// <summary>
        /// Scans all custom content: presets, scenes, and custom packages
        /// </summary>
        public List<CustomAtomItem> ScanAllCustomContent()
        {
            var allItems = new List<CustomAtomItem>();

            // Scan presets from Custom\Atom\Person
            var presets = ScanPresets();
            allItems.AddRange(presets);

            // Scan scenes from Saves\scene
            var scenes = ScanScenes();
            allItems.AddRange(scenes);

            // Scan custom packages from Custom folder (Hair, Clothing, Assets, SubScene, etc.)
            var packages = _customPackageScanner.ScanCustomPackages();
            allItems.AddRange(packages);

            // Scan person appearances from Saves\Person\appearance
            var appearances = ScanPersonAppearances();
            allItems.AddRange(appearances);

            return allItems;
        }

        /// <summary>
        /// Scans the Custom\Atom\Person folder for .vap preset files
        /// </summary>
        private List<CustomAtomItem> ScanPresets()
        {
            var items = new List<CustomAtomItem>();
            var customPersonDir = Path.Combine(_vamPath, "Custom", "Atom", "Person");

            if (!Directory.Exists(customPersonDir))
                return items;

            try
            {
                var vapFiles = SymlinkSafeFileSystem.EnumerateFilesSafe(customPersonDir, "*.vap", true);

                foreach (var vapPath in vapFiles)
                {
                    try
                    {
                        var item = CreatePresetItemFromFile(vapPath);
                        if (item != null)
                            items.Add(item);
                    }
                    catch (Exception)
                    {
                        // Error processing file - continue
                    }
                }
            }
            catch (Exception)
            {
                // Error scanning folder
            }

            return items;
        }

        /// <summary>
        /// Scans the Saves\scene folder for .json scene files
        /// </summary>
        private List<CustomAtomItem> ScanScenes()
        {
            var items = new List<CustomAtomItem>();
            var sceneDir = Path.Combine(_vamPath, "Saves", "scene");

            if (!Directory.Exists(sceneDir))
                return items;

            try
            {
                var jsonFiles = SymlinkSafeFileSystem.EnumerateFilesSafe(sceneDir, "*.json", true);

                foreach (var jsonPath in jsonFiles)
                {
                    try
                    {
                        var item = CreateSceneItemFromFile(jsonPath);
                        if (item != null)
                            items.Add(item);
                    }
                    catch (Exception)
                    {
                        // Error processing file - continue
                    }
                }
            }
            catch (Exception)
            {
                // Error scanning scenes
            }

            return items;
        }

        /// <summary>
        /// Scans the Saves\Person\appearance folder for .json person appearance files
        /// </summary>
        private List<CustomAtomItem> ScanPersonAppearances()
        {
            var items = new List<CustomAtomItem>();
            var appearanceDir = Path.Combine(_vamPath, "Saves", "Person", "appearance");

            if (!Directory.Exists(appearanceDir))
                return items;

            try
            {
                var appearanceDirInfo = new DirectoryInfo(appearanceDir);
                var jsonFiles = appearanceDirInfo.EnumerateFiles("*.json", SearchOption.AllDirectories)
                    .Where(f => !f.Name.EndsWith(".fav", StringComparison.OrdinalIgnoreCase) &&
                                !f.Name.EndsWith(".hide", StringComparison.OrdinalIgnoreCase));

                foreach (var fileInfo in jsonFiles)
                {
                    try
                    {
                        var item = CreateAppearanceItemFromFile(fileInfo);
                        if (item != null)
                            items.Add(item);
                    }
                    catch (Exception)
                    {
                        // Error processing file - continue
                    }
                }
            }
            catch (Exception)
            {
                // Error scanning folder
            }

            return items;
        }

        /// <summary>
        /// Creates a CustomAtomItem from a person appearance .json file
        /// </summary>
        private CustomAtomItem CreateAppearanceItemFromFile(FileInfo fileInfo)
        {
            var fileName = Path.GetFileNameWithoutExtension(fileInfo.Name);

            var appearanceDir = Path.Combine(_vamPath, "Saves", "Person", "appearance");
            var relativePath = Path.GetDirectoryName(fileInfo.FullName).Substring(appearanceDir.Length).TrimStart(Path.DirectorySeparatorChar);

            var item = new CustomAtomItem
            {
                Name = fileInfo.Name,
                DisplayName = fileName,
                FilePath = fileInfo.FullName,
                ThumbnailPath = FindAppearanceThumbnail(fileInfo.FullName),
                Category = "Appearance",
                Subfolder = relativePath,
                ModifiedDate = fileInfo.LastWriteTime,
                FileSize = fileInfo.Length,
                ContentType = "Appearance"
            };

            PresetScanner.ParsePresetDependencies(item);

            return item;
        }

        /// <summary>
        /// Finds thumbnail for a person appearance file
        /// </summary>
        private string FindAppearanceThumbnail(string jsonPath)
        {
            var basePath = Path.ChangeExtension(jsonPath, null);
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".JPG", ".JPEG", ".PNG" };

            foreach (var ext in extensions)
            {
                var thumbPath = basePath + ext;
                if (File.Exists(thumbPath))
                    return thumbPath;
            }

            return "";
        }

        /// <summary>
        /// Creates a CustomAtomItem from a .vap preset file
        /// </summary>
        private CustomAtomItem CreatePresetItemFromFile(string vapPath)
        {
            var fileInfo = new FileInfo(vapPath);
            var fileName = Path.GetFileNameWithoutExtension(vapPath);

            var customPersonDir = Path.Combine(_vamPath, "Custom", "Atom", "Person");
            var relativePath = Path.GetDirectoryName(vapPath).Substring(customPersonDir.Length).TrimStart(Path.DirectorySeparatorChar);

            var category = ExtractCategoryFromPath(vapPath, customPersonDir);

            var displayName = fileName;
            if (fileName.StartsWith("Preset_", StringComparison.OrdinalIgnoreCase))
                displayName = fileName.Substring(7);

            var item = new CustomAtomItem
            {
                Name = fileInfo.Name,
                DisplayName = displayName,
                FilePath = vapPath,
                ThumbnailPath = FindThumbnail(vapPath),
                Category = category,
                Subfolder = relativePath,
                ModifiedDate = fileInfo.LastWriteTime,
                FileSize = fileInfo.Length,
                ContentType = "Preset"
            };

            PresetScanner.ParsePresetDependencies(item);

            // Try to find parent files
            var parentName = PresetScanner.GetParentItemName(vapPath);
            if (!string.IsNullOrEmpty(parentName))
            {
                var directory = Path.GetDirectoryName(vapPath);
                var parentFiles = new List<string>();
                var parentExtensions = new[] { ".vaj", ".vam", ".jpg", ".vab" };
                
                foreach (var ext in parentExtensions)
                {
                    var parentPath = Path.Combine(directory, parentName + ext);
                    if (File.Exists(parentPath))
                    {
                        parentFiles.Add(parentPath);
                    }
                }
                
                item.ParentFiles = parentFiles;
            }

            return item;
        }

        /// <summary>
        /// Creates a CustomAtomItem from a scene .json file
        /// </summary>
        private CustomAtomItem CreateSceneItemFromFile(string jsonPath)
        {
            var fileInfo = new FileInfo(jsonPath);
            var fileName = Path.GetFileNameWithoutExtension(jsonPath);

            var sceneDir = Path.Combine(_vamPath, "Saves", "scene");
            var relativePath = Path.GetDirectoryName(jsonPath).Substring(sceneDir.Length).TrimStart(Path.DirectorySeparatorChar);

            var item = new CustomAtomItem
            {
                Name = fileInfo.Name,
                DisplayName = fileName,
                FilePath = jsonPath,
                ThumbnailPath = FindSceneThumbnail(jsonPath),
                Category = "Scene",
                Subfolder = relativePath,
                ModifiedDate = fileInfo.LastWriteTime,
                FileSize = fileInfo.Length,
                ContentType = "Scene"
            };

            // Parse scene metadata
            ParseSceneMetadata(item, jsonPath);

            return item;
        }

        /// <summary>
        /// Extracts category from preset file path
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
        /// Finds thumbnail for a preset file
        /// </summary>
        private string FindThumbnail(string vapPath)
        {
            var basePath = Path.ChangeExtension(vapPath, null);
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".JPG", ".JPEG", ".PNG" };

            foreach (var ext in extensions)
            {
                var thumbPath = basePath + ext;
                if (File.Exists(thumbPath))
                    return thumbPath;
            }

            return "";
        }

        /// <summary>
        /// Finds thumbnail for a scene file
        /// </summary>
        private string FindSceneThumbnail(string jsonPath)
        {
            var basePath = Path.ChangeExtension(jsonPath, null);
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".JPG", ".JPEG", ".PNG" };

            foreach (var ext in extensions)
            {
                var thumbPath = basePath + ext;
                if (File.Exists(thumbPath))
                    return thumbPath;
            }

            return "";
        }

        /// <summary>
        /// Parses scene metadata from JSON file using the full-tree regex scan
        /// </summary>
        private void ParseSceneMetadata(CustomAtomItem item, string jsonPath)
        {
            PresetScanner.ParsePresetDependencies(item);
        }
    }
}
