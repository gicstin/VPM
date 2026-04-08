using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Service for scanning and parsing VAM preset files (.vap) to extract dependencies
    /// </summary>
    public class PresetScanner
    {
        // Matches AUTHOR.NAME.VERSION or AUTHOR.NAME.VERSION:/path
        // NAME allows hyphens (e.g. Plugin-AutoFlutterTongue)
        // VERSION: integer, "latest", or "minN"
        private static readonly Regex DepPattern = new Regex(
            @"\b([A-Za-z0-9_]+)\.([A-Za-z0-9_-]+)\.(latest|min\d+|[A-Za-z0-9_]+)(?:\.var)?(?::/[^\s""',\]\}]*)?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );
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

                    // Regex scan: walk every string value in the entire JSON
                    // to catch deps in fields the structured parser doesn't know about
                    ScanAllStringsForDependencies(root, dependencySet);

                    // Apply parsed data to preset, keeping only the highest version per package
                    preset.Dependencies = DeduplicateByHighestVersion(dependencySet);
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
        /// Recursively walks every string value in a JsonElement and extracts
        /// dependency references using the regex pattern.
        /// </summary>
        private static void ScanAllStringsForDependencies(JsonElement element, HashSet<string> dependencies)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var value = element.GetString();
                    if (!string.IsNullOrEmpty(value))
                        ExtractDependenciesWithRegex(value, dependencies);
                    break;
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                        ScanAllStringsForDependencies(prop.Value, dependencies);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        ScanAllStringsForDependencies(item, dependencies);
                    break;
            }
        }

        /// <summary>
        /// Applies the dep regex to a string and adds all valid matches to the set.
        /// </summary>
        private static void ExtractDependenciesWithRegex(string value, HashSet<string> dependencies)
        {
            foreach (Match m in DepPattern.Matches(value))
            {
                var author  = m.Groups[1].Value;
                var name    = m.Groups[2].Value;
                var version = m.Groups[3].Value;

                if (IsValidDepMatch(author, name, version))
                    dependencies.Add($"{author}.{name}.{version}");
            }
        }

        /// <summary>
        /// Validates a regex match: version must be a recognised token and both
        /// author and name must contain at least one letter (filters float-like noise).
        /// </summary>
        private static bool IsValidDepMatch(string author, string name, string version)
        {
            // Version must be "latest", "minN", or start with a digit
            var v = version.ToLowerInvariant();
            if (v != "latest" &&
                !Regex.IsMatch(v, @"^min\d+$") &&
                !char.IsDigit(version[0]))
                return false;

            // Author and name must each contain at least one letter
            if (!author.Any(char.IsLetter) || !name.Any(char.IsLetter))
                return false;

            return true;
        }

        /// <summary>
        /// Returns a deduplicated list keeping only the highest version per
        /// author.name base (case-sensitive). Ranking: latest > numeric > minN.
        /// </summary>
        private static List<string> DeduplicateByHighestVersion(HashSet<string> deps)
        {
            // key = "Author.Name", value = (fullRef, rank, versionNum)
            var best = new Dictionary<string, (string FullRef, int Rank, int VersionNum)>(StringComparer.Ordinal);

            foreach (var dep in deps)
            {
                var parts = dep.Split('.');
                if (parts.Length < 3)
                {
                    if (!best.ContainsKey(dep))
                        best[dep] = (dep, -1, 0);
                    continue;
                }

                // Base = everything except the last segment (the version)
                var baseName = string.Join(".", parts.Take(parts.Length - 1));
                var version  = parts[parts.Length - 1];
                var (rank, versionNum) = GetVersionRank(version);

                if (!best.TryGetValue(baseName, out var current) ||
                    rank > current.Rank ||
                    (rank == current.Rank && versionNum > current.VersionNum))
                {
                    best[baseName] = (dep, rank, versionNum);
                }
            }

            return best.Values.Select(v => v.FullRef).ToList();
        }

        private static (int Rank, int VersionNum) GetVersionRank(string version)
        {
            if (string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase))
                return (2, 0);

            var minMatch = Regex.Match(version, @"^min(\d+)$", RegexOptions.IgnoreCase);
            if (minMatch.Success)
                return (0, int.Parse(minMatch.Groups[1].Value));

            var numMatch = Regex.Match(version, @"^(\d+)");
            if (numMatch.Success)
                return (1, int.Parse(numMatch.Groups[1].Value));

            return (1, 0);
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

        /// <summary>
        /// Extracts the parent item name from a preset file by analyzing storable IDs
        /// </summary>
        public static string GetParentItemName(string vapPath)
        {
            try
            {
                if (!File.Exists(vapPath)) return null;

                var jsonContent = File.ReadAllText(vapPath);
                using (var doc = JsonDocument.Parse(jsonContent))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("storables", out var storablesElement) && storablesElement.ValueKind == JsonValueKind.Array)
                    {
                        // Count occurrences of potential parent names
                        var nameCounts = new Dictionary<string, int>();

                        foreach (var storable in storablesElement.EnumerateArray())
                        {
                            if (storable.TryGetProperty("id", out var idElement))
                            {
                                var id = idElement.GetString();
                                if (string.IsNullOrEmpty(id) || !id.Contains(":")) continue;

                                var parts = id.Split(':');
                                if (parts.Length < 2) continue;

                                var namePart = parts[1];
                                
                                // Heuristic: Remove common suffixes to find the base name
                                var suffixes = new[] { "Style", "WrapControl", "Sim", "Control", "Trigger", "Audio", "Light", "Physics", "Collider" };
                                foreach (var suffix in suffixes)
                                {
                                    if (namePart.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                                    {
                                        namePart = namePart.Substring(0, namePart.Length - suffix.Length);
                                        break;
                                    }
                                }

                                if (!string.IsNullOrEmpty(namePart))
                                {
                                    if (!nameCounts.ContainsKey(namePart))
                                        nameCounts[namePart] = 0;
                                    nameCounts[namePart]++;
                                }
                            }
                        }

                        // Return the most frequent name
                        return nameCounts.OrderByDescending(x => x.Value).FirstOrDefault().Key;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting parent item name: {ex.Message}");
            }

            return null;
        }
    }
}
