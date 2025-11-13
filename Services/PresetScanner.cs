using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Service for scanning and parsing VAM preset files (.vap) to extract dependencies
    /// </summary>
    public class PresetScanner
    {
        /// <summary>
        /// Parses a .vap file and extracts dependencies and metadata
        /// </summary>
        public static void ParsePresetDependencies(CustomAtomItem preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.FilePath) || !File.Exists(preset.FilePath))
                return;

            try
            {
                var jsonContent = File.ReadAllText(preset.FilePath);
                ParsePresetDependenciesFromJson(preset, jsonContent);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing preset dependencies for {preset.FilePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses preset dependencies from JSON content
        /// </summary>
        private static void ParsePresetDependenciesFromJson(CustomAtomItem preset, string jsonContent)
        {
            var dependencySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hairItems = new List<string>();
            var clothingItems = new List<string>();
            var morphItems = new List<string>();
            var textureItems = new List<string>();

            try
            {
                using (var doc = JsonDocument.Parse(jsonContent))
                {
                    var root = doc.RootElement;

                    // Parse storables array - this is where most preset data is stored
                    if (root.TryGetProperty("storables", out var storablesElement) && storablesElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var storable in storablesElement.EnumerateArray())
                        {
                            if (storable.TryGetProperty("id", out var storableId))
                            {
                                var id = storableId.GetString();
                                
                                // Parse geometry storable for hair, clothing, and morphs
                                if (id == "geometry")
                                {
                                    ParseGeometryStorable(storable, dependencySet, hairItems, clothingItems, morphItems);
                                }
                                // Parse texture storables
                                else if (id == "textures")
                                {
                                    ParseTextureStorable(storable, dependencySet, textureItems);
                                }
                                // Parse other storables that might have dependencies
                                else
                                {
                                    ParseGenericStorable(storable, dependencySet);
                                }
                            }
                        }
                    }

                    // Apply parsed data to preset
                    preset.Dependencies = dependencySet.ToList();
                    preset.HairItems = hairItems;
                    preset.ClothingItems = clothingItems;
                    preset.MorphItems = morphItems;
                    preset.TextureItems = textureItems;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing preset JSON: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses the geometry storable for hair, clothing, and morphs
        /// </summary>
        private static void ParseGeometryStorable(JsonElement storable, HashSet<string> dependencies, 
            List<string> hairItems, List<string> clothingItems, List<string> morphItems)
        {
            try
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
                                hairItems.Add(hairId);
                                ExtractDependencyFromPath(hairId, dependencies);
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
                                clothingItems.Add(clothingId);
                                ExtractDependencyFromPath(clothingId, dependencies);
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
                                morphItems.Add(morphUid);
                                ExtractDependencyFromPath(morphUid, dependencies);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing geometry storable: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses the textures storable for texture dependencies
        /// </summary>
        private static void ParseTextureStorable(JsonElement storable, HashSet<string> dependencies, List<string> textureItems)
        {
            try
            {
                // Parse various texture URL properties
                var textureProperties = new[]
                {
                    "faceDiffuseUrl", "torsoDiffuseUrl", "limbsDiffuseUrl", "genitalsDiffuseUrl",
                    "faceSpecularUrl", "torsoSpecularUrl", "limbsSpecularUrl", "genitalsSpecularUrl",
                    "faceGlossUrl", "torsoGlossUrl", "limbsGlossUrl", "genitalsGlossUrl",
                    "faceNormalUrl", "torsoNormalUrl", "limbsNormalUrl", "genitalsNormalUrl",
                    "faceDetailUrl", "torsoDetailUrl", "limbsDetailUrl", "genitalsDetailUrl",
                    "faceDecalUrl", "torsoDecalUrl", "limbsDecalUrl", "genitalsDecalUrl"
                };

                foreach (var prop in textureProperties)
                {
                    if (storable.TryGetProperty(prop, out var textureElement))
                    {
                        var textureUrl = textureElement.GetString();
                        if (!string.IsNullOrEmpty(textureUrl))
                        {
                            textureItems.Add($"{prop}: {textureUrl}");
                            ExtractDependencyFromPath(textureUrl, dependencies);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing texture storable: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses generic storables for specific properties that might contain dependencies
        /// </summary>
        private static void ParseGenericStorable(JsonElement storable, HashSet<string> dependencies)
        {
            try
            {
                // Only look for specific properties that are likely to contain package references
                var pathProperties = new[]
                {
                    "storePath", "url", "path", "filePath", "assetUrl", "pluginUrl",
                    "customTexture_MainTex", "customTexture_SpecTex", "customTexture_GlossTex",
                    "customTexture_BumpMap", "customTexture_AlphaTex", "customTexture_DecalTex",
                    "simTexture"
                };

                foreach (var property in storable.EnumerateObject())
                {
                    var propertyName = property.Name.ToLowerInvariant();
                    
                    // Check if this property is likely to contain a path
                    if (pathProperties.Any(p => propertyName.Contains(p.ToLowerInvariant())) ||
                        propertyName.Contains("texture") ||
                        propertyName.Contains("url") ||
                        propertyName.Contains("path"))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var stringValue = property.Value.GetString();
                            if (!string.IsNullOrEmpty(stringValue))
                            {
                                ExtractDependencyFromPath(stringValue, dependencies);
                            }
                        }
                    }
                    // Also check arrays that might contain path objects
                    else if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var arrayItem in property.Value.EnumerateArray())
                        {
                            if (arrayItem.ValueKind == JsonValueKind.Object)
                            {
                                ParseGenericStorable(arrayItem, dependencies);
                            }
                        }
                    }
                    // Check nested objects
                    else if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        ParseGenericStorable(property.Value, dependencies);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing generic storable: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts package dependency from a path value
        /// Format: "creator.packagename.version:/path/to/file"
        /// Ignores local file paths like "Custom/..." and numeric values
        /// </summary>
        private static void ExtractDependencyFromPath(string path, HashSet<string> dependencies)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    return;

                // Skip if it's a numeric value (coordinates, settings, etc.)
                if (double.TryParse(path, out _))
                    return;

                // Skip if it doesn't contain :/ (not a package reference)
                if (!path.Contains(":/"))
                    return;

                // Split by :/ to separate package reference from path
                var parts = path.Split(new[] { ":/" }, StringSplitOptions.None);
                if (parts.Length > 1) // Must have both package reference and path
                {
                    var packageRef = parts[0].Trim();
                    
                    // Only add if it looks like a valid package reference
                    if (IsValidPackageReference(packageRef))
                    {
                        dependencies.Add(packageRef);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting dependency from path: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates if a string is a valid package reference
        /// </summary>
        private static bool IsValidPackageReference(string packageRef)
        {
            if (string.IsNullOrEmpty(packageRef))
                return false;

            // Skip local file paths
            if (packageRef.StartsWith("Custom/", StringComparison.OrdinalIgnoreCase) ||
                packageRef.StartsWith("Saves/", StringComparison.OrdinalIgnoreCase))
                return false;

            // Skip built-in references
            if (IsBuiltInReference(packageRef))
                return false;

            // Must contain at least 2 dots for creator.package.version format
            var dotCount = packageRef.Count(c => c == '.');
            if (dotCount < 2)
                return false;

            // Must not be purely numeric
            if (double.TryParse(packageRef, out _))
                return false;

            // Should have alphabetic characters (not just numbers and dots)
            if (!packageRef.Any(char.IsLetter))
                return false;

            // Should follow creator.package.version pattern
            var parts = packageRef.Split('.');
            if (parts.Length < 3)
                return false;

            // Creator and package name should have letters
            if (!parts[0].Any(char.IsLetter) || !parts[1].Any(char.IsLetter))
                return false;

            return true;
        }

        /// <summary>
        /// Checks if a reference is a built-in VAM reference that shouldn't be considered a dependency
        /// </summary>
        private static bool IsBuiltInReference(string reference)
        {
            if (string.IsNullOrEmpty(reference))
                return true;

            var builtInPrefixes = new[]
            {
                "Hair Creator",
                "SimV2 Hair",
                "Tank Top",
                "Shorts",
                "Lashes",
                "Sclera",
                "Color",
                "Brows"
            };

            return builtInPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }
}
