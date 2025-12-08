using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SharpCompress.Archives;
using VPM.Models;

namespace VPM.Services
{
    public class DependencyScanner
    {
        public class DependencyScanResult
        {
            public List<DependencyItemModel> Dependencies { get; set; } = new List<DependencyItemModel>();
            public string ErrorMessage { get; set; } = "";
            public bool Success => string.IsNullOrEmpty(ErrorMessage);
        }

        public DependencyScanResult ScanPackageDependencies(string packagePath)
        {
            var result = new DependencyScanResult();

            try
            {
                if (string.IsNullOrEmpty(packagePath))
                {
                    result.ErrorMessage = "Package path is empty";
                    return result;
                }

                if (File.Exists(packagePath))
                {
                    result = ScanVarFile(packagePath);
                }
                else if (Directory.Exists(packagePath))
                {
                    result = ScanUnpackedFolder(packagePath);
                }
                else
                {
                    result.ErrorMessage = "Package path does not exist";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error scanning dependencies: {ex.Message}";
            }

            return result;
        }

        private DependencyScanResult ScanVarFile(string varPath)
        {
            var result = new DependencyScanResult();

            try
            {
                using var archive = SharpCompressHelper.OpenForRead(varPath);
                var metaEntry = archive.Entries.FirstOrDefault(e => 
                    e.Key.Equals("meta.json", StringComparison.OrdinalIgnoreCase));

                if (metaEntry == null)
                {
                    result.ErrorMessage = "No meta.json found in package";
                    return result;
                }

                using var stream = metaEntry.OpenEntryStream();
                using var reader = new StreamReader(stream);
                var metaJson = reader.ReadToEnd();

                // Single pass: ParseDependenciesFromJson now also handles disabled dependencies
                ParseDependenciesFromJson(metaJson, result);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error reading VAR file: {ex.Message}";
            }

            return result;
        }

        private DependencyScanResult ScanUnpackedFolder(string folderPath)
        {
            var result = new DependencyScanResult();

            try
            {
                var metaPath = Path.Combine(folderPath, "meta.json");
                if (!File.Exists(metaPath))
                {
                    result.ErrorMessage = "No meta.json found in unpacked folder";
                    return result;
                }

                var metaJson = File.ReadAllText(metaPath);
                // Single pass: ParseDependenciesFromJson now also handles disabled dependencies
                ParseDependenciesFromJson(metaJson, result);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error reading unpacked folder: {ex.Message}";
            }

            return result;
        }

        private void ParseDependenciesFromJson(string metaJson, DependencyScanResult result)
        {
            try
            {
                using var jsonDoc = JsonDocument.Parse(metaJson);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("dependencies", out var depsElement))
                {
                    ParseDependenciesRecursive(depsElement, result.Dependencies, 0);
                }
                else
                {
                    result.ErrorMessage = "No dependencies found in meta.json";
                }
                
                // Parse disabled dependencies from description in the same pass (avoid re-parsing JSON)
                ParseDisabledDependenciesFromElement(root, result);
            }
            catch (JsonException ex)
            {
                result.ErrorMessage = $"Invalid JSON in meta.json: {ex.Message}";
            }
        }

        private void ParseDependenciesRecursive(JsonElement depsElement, List<DependencyItemModel> dependencies, int depth, string parentName = "")
        {
            if (depsElement.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in depsElement.EnumerateObject())
            {
                var depItem = new DependencyItemModel
                {
                    Name = prop.Name,
                    Depth = depth,
                    IsEnabled = true,
                    ParentName = parentName
                };

                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("licenseType", out var licenseElement))
                    {
                        depItem.LicenseType = licenseElement.GetString() ?? "";
                    }

                    // Check for nested dependencies
                    if (prop.Value.TryGetProperty("dependencies", out var subDepsElement) &&
                        subDepsElement.ValueKind == JsonValueKind.Object)
                    {
                        // PERFORMANCE FIX: Enumerate once and materialize to avoid double enumeration
                        // Previously: EnumerateObject().Count() then EnumerateObject() again in recursion
                        var subDepsList = subDepsElement.EnumerateObject().ToList();
                        depItem.HasSubDependencies = subDepsList.Count > 0;
                        depItem.SubDependencyCount = subDepsList.Count;
                        
                        dependencies.Add(depItem);
                        
                        // Recursively parse subdependencies (uses already-enumerated list)
                        if (subDepsList.Count > 0)
                        {
                            ParseDependenciesRecursiveFromList(subDepsList, dependencies, depth + 1, prop.Name);
                        }
                    }
                    else
                    {
                        dependencies.Add(depItem);
                    }
                }
                else
                {
                    dependencies.Add(depItem);
                }
            }
        }

        /// <summary>
        /// PERFORMANCE FIX: Helper method that works with pre-enumerated list to avoid double enumeration
        /// </summary>
        private void ParseDependenciesRecursiveFromList(List<JsonProperty> properties, List<DependencyItemModel> dependencies, int depth, string parentName)
        {
            foreach (var prop in properties)
            {
                var depItem = new DependencyItemModel
                {
                    Name = prop.Name,
                    Depth = depth,
                    IsEnabled = true,
                    ParentName = parentName
                };

                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("licenseType", out var licenseElement))
                    {
                        depItem.LicenseType = licenseElement.GetString() ?? "";
                    }

                    // Check for nested dependencies
                    if (prop.Value.TryGetProperty("dependencies", out var subDepsElement) &&
                        subDepsElement.ValueKind == JsonValueKind.Object)
                    {
                        var subDepsList = subDepsElement.EnumerateObject().ToList();
                        depItem.HasSubDependencies = subDepsList.Count > 0;
                        depItem.SubDependencyCount = subDepsList.Count;
                        
                        dependencies.Add(depItem);
                        
                        if (subDepsList.Count > 0)
                        {
                            ParseDependenciesRecursiveFromList(subDepsList, dependencies, depth + 1, prop.Name);
                        }
                    }
                    else
                    {
                        dependencies.Add(depItem);
                    }
                }
                else
                {
                    dependencies.Add(depItem);
                }
            }
        }

        /// <summary>
        /// Parses disabled dependencies from an already-parsed JSON element (avoids re-parsing JSON).
        /// </summary>
        private void ParseDisabledDependenciesFromElement(JsonElement root, DependencyScanResult result)
        {
            try
            {
                if (!root.TryGetProperty("description", out var descElement))
                    return;

                string description = descElement.GetString() ?? "";
                if (string.IsNullOrEmpty(description))
                    return;

                ParseDisabledDependenciesFromDescriptionText(description, result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing disabled dependencies: {ex.Message}");
            }
        }

        /// <summary>
        /// Legacy method - kept for backward compatibility but prefer ParseDisabledDependenciesFromElement.
        /// </summary>
        private void ParseDisabledDependenciesFromDescription(string metaJson, DependencyScanResult result)
        {
            try
            {
                using var jsonDoc = JsonDocument.Parse(metaJson);
                var root = jsonDoc.RootElement;

                if (!root.TryGetProperty("description", out var descElement))
                    return;

                string description = descElement.GetString() ?? "";
                if (string.IsNullOrEmpty(description))
                    return;

                ParseDisabledDependenciesFromDescriptionText(description, result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing disabled dependencies: {ex.Message}");
            }
        }

        /// <summary>
        /// Core logic for parsing disabled dependencies from description text.
        /// </summary>
        private void ParseDisabledDependenciesFromDescriptionText(string description, DependencyScanResult result)
        {
            var startTag = "[VPM_DISABLED_DEPENDENCIES]";
            var endTag = "[/VPM_DISABLED_DEPENDENCIES]";

            int startIndex = description.IndexOf(startTag);
            if (startIndex == -1)
                return;

            startIndex += startTag.Length;
            int endIndex = description.IndexOf(endTag, startIndex);
            if (endIndex == -1)
                return;

            string disabledSection = description.Substring(startIndex, endIndex - startIndex).Trim();
            if (string.IsNullOrWhiteSpace(disabledSection))
                return;

            var lines = disabledSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("•") || trimmedLine.StartsWith("-"))
                {
                    var depEntry = trimmedLine.TrimStart('•', '-', ' ').Trim();
                    
                    // Parse format: depName or depName|PARENT:parentName
                    string depName = depEntry;
                    string parentName = "";
                    
                    if (depEntry.Contains("|PARENT:"))
                    {
                        var parts = depEntry.Split(new[] { "|PARENT:" }, StringSplitOptions.None);
                        depName = parts[0];
                        parentName = parts.Length > 1 ? parts[1] : "";
                    }
                    
                    var matchingDep = result.Dependencies.FirstOrDefault(d => d.Name.Equals(depName, StringComparison.OrdinalIgnoreCase));
                    if (matchingDep != null)
                    {
                        matchingDep.IsDisabledByUser = true;
                        matchingDep.IsEnabled = false;
                    }
                    else
                    {
                        result.Dependencies.Add(new DependencyItemModel
                        {
                            Name = depName,
                            IsDisabledByUser = true,
                            IsEnabled = false,
                            Depth = string.IsNullOrEmpty(parentName) ? 0 : 1,
                            ParentName = parentName
                        });
                    }
                }
            }
        }
    }
}

