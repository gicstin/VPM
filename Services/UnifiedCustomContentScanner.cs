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
    /// Unified scanner for custom atom presets, scenes, and custom packages
    /// Scans:
    /// - Custom\Atom\Person folder for .vap preset files
    /// - Saves\scene folder for .json scene files
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
                var vapFiles = Directory.GetFiles(customPersonDir, "*.vap", SearchOption.AllDirectories);

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
                var jsonFiles = Directory.GetFiles(sceneDir, "*.json", SearchOption.AllDirectories);

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
            item.IsOptimized = IsPresetOptimized(vapPath);

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
        /// Checks if a preset has been optimized
        /// </summary>
        private bool IsPresetOptimized(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return false;

                string jsonContent = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(jsonContent);

                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("_VPM_Optimized", out var optimizedProp))
                        return optimizedProp.ValueKind == JsonValueKind.True;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Parses scene metadata from JSON file
        /// </summary>
        private void ParseSceneMetadata(CustomAtomItem item, string jsonPath)
        {
            try
            {
                if (!File.Exists(jsonPath))
                    return;

                var jsonContent = File.ReadAllText(jsonPath);
                using (var doc = JsonDocument.Parse(jsonContent))
                {
                    var root = doc.RootElement;

                    // Extract dependencies
                    var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (root.TryGetProperty("atoms", out var atomsElement) && atomsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var atom in atomsElement.EnumerateArray())
                        {
                            if (atom.TryGetProperty("storables", out var storablesElement) && storablesElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var storable in storablesElement.EnumerateArray())
                                {
                                    ExtractDependenciesFromStorable(storable, dependencies);
                                }
                            }
                        }
                    }

                    item.Dependencies = dependencies.ToList();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing scene metadata: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts dependencies from a storable element
        /// </summary>
        private void ExtractDependenciesFromStorable(JsonElement storable, HashSet<string> dependencies)
        {
            try
            {
                foreach (var property in storable.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var stringValue = property.Value.GetString();
                        if (!string.IsNullOrEmpty(stringValue) && stringValue.Contains(":/"))
                        {
                            ExtractDependencyFromPath(stringValue, dependencies);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting dependencies from storable: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts package dependency from a path value
        /// </summary>
        private void ExtractDependencyFromPath(string path, HashSet<string> dependencies)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !path.Contains(":/"))
                    return;

                var parts = path.Split(new[] { ":/" }, StringSplitOptions.None);
                if (parts.Length > 1)
                {
                    var packageRef = parts[0].Trim();
                    if (IsValidPackageReference(packageRef))
                        dependencies.Add(packageRef);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting dependency: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates if a string is a valid package reference
        /// </summary>
        private bool IsValidPackageReference(string packageRef)
        {
            if (string.IsNullOrEmpty(packageRef))
                return false;

            if (packageRef.StartsWith("Custom/", StringComparison.OrdinalIgnoreCase) ||
                packageRef.StartsWith("Saves/", StringComparison.OrdinalIgnoreCase))
                return false;

            var dotCount = packageRef.Count(c => c == '.');
            if (dotCount < 2)
                return false;

            if (double.TryParse(packageRef, out _))
                return false;

            if (!packageRef.Any(char.IsLetter))
                return false;

            var parts = packageRef.Split('.');
            if (parts.Length < 3)
                return false;

            if (!parts[0].Any(char.IsLetter) || !parts[1].Any(char.IsLetter))
                return false;

            return true;
        }
    }
}
