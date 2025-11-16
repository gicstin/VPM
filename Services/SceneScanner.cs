using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VPM.Models;
using ICSharpCode.SharpZipLib.Zip;

namespace VPM.Services
{
    /// <summary>
    /// Service for scanning and parsing VAM scene files from local folders and VAR packages
    /// </summary>
    public class SceneScanner
    {
        private readonly string _vamPath;

        public SceneScanner(string vamPath)
        {
            _vamPath = vamPath;
        }

        /// <summary>
        /// Scans the local Saves\scene folder for scene files
        /// </summary>
        public List<SceneItem> ScanLocalScenes()
        {
            var scenes = new List<SceneItem>();
            var sceneDir = Path.Combine(_vamPath, "Saves", "scene");

            if (!Directory.Exists(sceneDir))
                return scenes;

            try
            {
                var jsonFiles = Directory.GetFiles(sceneDir, "*.json", SearchOption.AllDirectories);

                foreach (var jsonPath in jsonFiles)
                {
                    try
                    {
                        var scene = CreateSceneItemFromFile(jsonPath, "Local", "");
                        if (scene != null)
                            scenes.Add(scene);
                    }
                    catch
                    {
                        // Error scanning scene - continue
                    }
                }
            }
            catch
            {
                // Error scanning local scenes - silently handled
            }

            return scenes;
        }

        /// <summary>
        /// Scans a VAR package for scene files
        /// </summary>
        public List<SceneItem> ScanVarScenes(string varPath)
        {
            var scenes = new List<SceneItem>();

            if (!File.Exists(varPath))
            {
                return scenes;
            }

            try
            {
                var varName = Path.GetFileNameWithoutExtension(varPath);

                using (var zipFile = new ZipFile(varPath))
                {
                    var allEntries = SharpCompressHelper.GetAllEntries(zipFile);
                    // Look for scene files in Saves/scene/ path
                    var sceneEntries = allEntries
                        .Where(e => !e.IsDirectory && e.Name.StartsWith("Saves/scene/", StringComparison.OrdinalIgnoreCase) &&
                                    e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var entry in sceneEntries)
                    {
                        try
                        {
                            var scene = CreateSceneItemFromVarEntry(entry, varPath, varName);
                            if (scene != null)
                            {
                                scenes.Add(scene);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error scanning scene in VAR {varName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning VAR {varPath}: {ex.Message}");
            }

            return scenes;
        }

        /// <summary>
        /// Creates a SceneItem from a local file
        /// </summary>
        private SceneItem CreateSceneItemFromFile(string jsonPath, string source, string sourcePackage)
        {
            var fileInfo = new FileInfo(jsonPath);
            var fileName = Path.GetFileNameWithoutExtension(jsonPath);
            
            // Extract creator from filename (format: Creator.SceneName.version)
            var creator = ExtractCreatorFromFilename(fileName);

            var scene = new SceneItem
            {
                Name = fileInfo.Name,
                DisplayName = fileName,
                FilePath = jsonPath,
                ThumbnailPath = FindThumbnail(jsonPath),
                Creator = creator,
                ModifiedDate = fileInfo.LastWriteTime,
                FileSize = fileInfo.Length,
                Source = source,
                SourcePackage = sourcePackage
            };

            // Try to parse metadata (lightweight)
            try
            {
                var metadata = ParseSceneMetadata(jsonPath);
                ApplyMetadataToScene(scene, metadata);
            }
            catch
            {
                // If parsing fails, just use basic file info
            }

            return scene;
        }

        /// <summary>
        /// Creates a SceneItem from a VAR archive entry
        /// </summary>
        private SceneItem CreateSceneItemFromVarEntry(ZipEntry entry, string varPath, string varName)
        {
            var fileName = Path.GetFileName(entry.Name);
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            var creator = ExtractCreatorFromFilename(fileNameWithoutExt);

            var scene = new SceneItem
            {
                Name = fileName,
                DisplayName = fileNameWithoutExt,
                FilePath = $"{varPath}::{entry.Name}", // Virtual path
                Creator = creator,
                ModifiedDate = entry.DateTime,
                FileSize = entry.Size,
                Source = "VAR",
                SourcePackage = varName
            };

            // Try to find thumbnail in VAR
            scene.ThumbnailPath = FindThumbnailInVar(varPath, entry.Name);

            // Try to parse metadata from VAR entry
            try
            {
                using (var zipFile = new ZipFile(varPath))
                {
                    var jsonContent = SharpCompressHelper.ReadEntryAsString(zipFile, entry);
                    var metadata = ParseSceneMetadataFromJson(jsonContent);
                    ApplyMetadataToScene(scene, metadata);
                }
            }
            catch
            {
                // If parsing fails, just use basic info
            }

            return scene;
        }

        /// <summary>
        /// Finds the thumbnail image for a scene file
        /// </summary>
        private string FindThumbnail(string jsonPath)
        {
            var basePath = Path.ChangeExtension(jsonPath, null);
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

        /// <summary>
        /// Finds the thumbnail image for a scene inside a VAR
        /// </summary>
        private string FindThumbnailInVar(string varPath, string sceneEntryPath)
        {
            try
            {
                using (var zipFile = new ZipFile(varPath))
                {
                    var basePath = Path.ChangeExtension(sceneEntryPath, null);
                    var extensions = new[] { ".jpg", ".jpeg", ".png", ".JPG", ".JPEG", ".PNG" };

                    foreach (var ext in extensions)
                    {
                        var thumbPath = basePath + ext;
                        var thumbEntry = SharpCompressHelper.FindEntryByPath(zipFile, thumbPath);
                        if (thumbEntry != null)
                        {
                            // Return virtual path to thumbnail in VAR
                            return $"{varPath}::{thumbPath}";
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return "";
        }

        /// <summary>
        /// Parses scene metadata from a JSON file
        /// </summary>
        private SceneMetadata ParseSceneMetadata(string jsonPath)
        {
            var jsonContent = File.ReadAllText(jsonPath);
            return ParseSceneMetadataFromJson(jsonContent);
        }

        /// <summary>
        /// Parses scene metadata from JSON content (lightweight parsing)
        /// </summary>
        private SceneMetadata ParseSceneMetadataFromJson(string jsonContent)
        {
            var metadata = new SceneMetadata();
            var dependencySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var doc = JsonDocument.Parse(jsonContent))
                {
                    var root = doc.RootElement;

                    // Parse atoms array
                    if (root.TryGetProperty("atoms", out var atomsElement) && atomsElement.ValueKind == JsonValueKind.Array)
                    {
                        metadata.AtomCount = atomsElement.GetArrayLength();
                        
                        foreach (var atom in atomsElement.EnumerateArray())
                        {
                            if (atom.TryGetProperty("type", out var typeElement))
                            {
                                var atomType = typeElement.GetString() ?? "Unknown";
                                
                                if (!metadata.AtomTypes.Contains(atomType))
                                {
                                    metadata.AtomTypes.Add(atomType);
                                }

                                if (!metadata.AtomTypeCounts.ContainsKey(atomType))
                                {
                                    metadata.AtomTypeCounts[atomType] = 0;
                                }
                                metadata.AtomTypeCounts[atomType]++;

                                // Track special atom types
                                if (atomType == "Person")
                                    metadata.HasPerson = true;
                                else if (atomType == "CustomUnityAsset")
                                    metadata.HasCustomAssets = true;
                            }

                            // Parse dependencies from storables
                            if (atom.TryGetProperty("storables", out var storables) && storables.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var storable in storables.EnumerateArray())
                                {
                                    if (storable.TryGetProperty("storePath", out var storePathElement))
                                    {
                                        var storePath = storePathElement.GetString();
                                        if (!string.IsNullOrEmpty(storePath))
                                        {
                                            ExtractDependencyFromStorePath(storePath, dependencySet);
                                        }
                                    }

                                    // Parse plugin dependencies
                                    if (storable.TryGetProperty("plugins", out var pluginsElement) && pluginsElement.ValueKind == JsonValueKind.Object)
                                    {
                                        foreach (var plugin in pluginsElement.EnumerateObject())
                                        {
                                            var pluginPath = plugin.Value.GetString();
                                            if (!string.IsNullOrEmpty(pluginPath))
                                            {
                                                ExtractDependencyFromStorePath(pluginPath, dependencySet);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Parse geometry section for hair, clothing, and morphs
                    if (root.TryGetProperty("atoms", out var atomsForGeometry) && atomsForGeometry.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var atom in atomsForGeometry.EnumerateArray())
                        {
                            // Look for geometry storable within atoms
                            if (atom.TryGetProperty("storables", out var storables) && storables.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var storable in storables.EnumerateArray())
                                {
                                    if (storable.TryGetProperty("id", out var storableId) && 
                                        storableId.GetString() == "geometry")
                                    {
                                        // Parse hair items
                                        if (storable.TryGetProperty("hair", out var hairElement) && hairElement.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var hairItem in hairElement.EnumerateArray())
                                            {
                                                if (hairItem.TryGetProperty("id", out var hairIdElement))
                                                {
                                                    var hairId = hairIdElement.GetString();
                                                    if (!string.IsNullOrEmpty(hairId))
                                                    {
                                                        metadata.HairItems.Add(hairId);
                                                        ExtractDependencyFromStorePath(hairId, dependencySet);
                                                    }
                                                }
                                            }
                                        }

                                        // Parse clothing items
                                        if (storable.TryGetProperty("clothing", out var clothingElement) && clothingElement.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var clothingItem in clothingElement.EnumerateArray())
                                            {
                                                if (clothingItem.TryGetProperty("id", out var clothingIdElement))
                                                {
                                                    var clothingId = clothingIdElement.GetString();
                                                    if (!string.IsNullOrEmpty(clothingId))
                                                    {
                                                        metadata.ClothingItems.Add(clothingId);
                                                        ExtractDependencyFromStorePath(clothingId, dependencySet);
                                                    }
                                                }
                                            }
                                        }

                                        // Parse morphs
                                        if (storable.TryGetProperty("morphs", out var morphsElement) && morphsElement.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var morphItem in morphsElement.EnumerateArray())
                                            {
                                                if (morphItem.TryGetProperty("uid", out var morphUidElement))
                                                {
                                                    var morphUid = morphUidElement.GetString();
                                                    if (!string.IsNullOrEmpty(morphUid))
                                                    {
                                                        metadata.MorphItems.Add(morphUid);
                                                        ExtractDependencyFromStorePath(morphUid, dependencySet);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Convert dependency set to list
                    metadata.Dependencies = dependencySet.ToList();

                    // Determine scene type based on atoms
                    metadata.SceneType = DetermineSceneType(metadata);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing scene metadata: {ex.Message}");
            }

            return metadata;
        }

        /// <summary>
        /// Extracts package dependency from a storePath value
        /// Format: "creator.packagename.version:/path/to/file"
        /// Ignores local file paths like "Custom/SubScene/..."
        /// </summary>
        private void ExtractDependencyFromStorePath(string storePath, HashSet<string> dependencies)
        {
            try
            {
                // Split by :/ to separate package reference from path
                var parts = storePath.Split(new[] { ":/" }, StringSplitOptions.None);
                if (parts.Length > 0)
                {
                    var packageRef = parts[0].Trim();
                    
                    // Only add if it looks like a package reference (contains dots for creator.package format)
                    // Skip local file paths like "Custom/SubScene/..."
                    if (!string.IsNullOrEmpty(packageRef) && packageRef.Contains(".") && !packageRef.StartsWith("Custom/"))
                    {
                                dependencies.Add(packageRef);
                    }
                    else if (!string.IsNullOrEmpty(packageRef))
                    {
                    }
                }
            }
            catch
            {
                // Error extracting dependency - silently handled
            }
        }

        /// <summary>
        /// Applies parsed metadata to a SceneItem
        /// </summary>
        private void ApplyMetadataToScene(SceneItem scene, SceneMetadata metadata)
        {
            scene.AtomCount = metadata.AtomCount;
            scene.Dependencies = metadata.Dependencies;
            scene.AtomTypes = metadata.AtomTypes;
            scene.SceneType = metadata.SceneType;
            scene.HairCount = metadata.HairItems.Count;
            scene.ClothingCount = metadata.ClothingItems.Count;
            scene.MorphCount = metadata.MorphItems.Count;
            scene.HairItems = metadata.HairItems;
            scene.ClothingItems = metadata.ClothingItems;
            scene.MorphItems = metadata.MorphItems;
        }

        /// <summary>
        /// Determines the scene type based on atom composition
        /// </summary>
        private string DetermineSceneType(SceneMetadata metadata)
        {
            if (metadata.HasPerson && metadata.HasCustomAssets)
                return "Character Scene";
            else if (metadata.HasPerson)
                return "Person Scene";
            else if (metadata.HasCustomAssets)
                return "Environment";
            else if (metadata.AtomCount > 0)
                return "Scene";
            else
                return "Unknown";
        }

        /// <summary>
        /// Extracts creator name from filename (format: Creator.SceneName.version)
        /// </summary>
        private string ExtractCreatorFromFilename(string fileName)
        {
            var parts = fileName.Split('.');
            if (parts.Length >= 2)
            {
                return parts[0];
            }
            return "Unknown";
        }

        /// <summary>
        /// Extracts thumbnail from VAR to a temporary location for display
        /// </summary>
        public string ExtractThumbnailFromVar(string virtualPath, string cacheDir)
        {
            try
            {
                var parts = virtualPath.Split(new[] { "::" }, StringSplitOptions.None);
                if (parts.Length != 2)
                    return "";

                var varPath = parts[0];
                var entryPath = parts[1];

                if (!File.Exists(varPath))
                    return "";

                // Create cache directory if needed
                if (!Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }

                // Generate cache filename
                var cacheFileName = $"{Path.GetFileNameWithoutExtension(varPath)}_{Path.GetFileName(entryPath)}";
                var cachePath = Path.Combine(cacheDir, cacheFileName);

                // Return cached file if exists
                if (File.Exists(cachePath))
                {
                    return cachePath;
                }

                // Extract thumbnail from VAR using streaming
                // Benefit: Efficient memory usage for thumbnail extraction
                using (var zipFile = new ZipFile(varPath))
                {
                    var entry = SharpCompressHelper.FindEntryByPath(zipFile, entryPath);
                    if (entry != null)
                    {
                        // Use streaming to write directly to file without loading entire image into memory
                        using (var fileStream = File.Create(cachePath))
                        {
                            SharpCompressHelper.ReadEntryToStream(zipFile, entry, fileStream);
                        }
                        return cachePath;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting thumbnail: {ex.Message}");
            }

            return "";
        }
    }
}

