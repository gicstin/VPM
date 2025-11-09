using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
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
                using var archive = ZipFile.OpenRead(varPath);
                var metaEntry = archive.Entries.FirstOrDefault(e => 
                    e.Name.Equals("meta.json", StringComparison.OrdinalIgnoreCase));

                if (metaEntry == null)
                {
                    result.ErrorMessage = "No meta.json found in package";
                    return result;
                }

                using var stream = metaEntry.Open();
                using var reader = new StreamReader(stream);
                var metaJson = reader.ReadToEnd();

                ParseDependenciesFromJson(metaJson, result);
                ParseDisabledDependenciesFromDescription(metaJson, result);
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
                ParseDependenciesFromJson(metaJson, result);
                ParseDisabledDependenciesFromDescription(metaJson, result);
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
                var jsonDoc = JsonDocument.Parse(metaJson);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("dependencies", out var depsElement))
                {
                    ParseDependenciesRecursive(depsElement, result.Dependencies, 0);
                }
                else
                {
                    result.ErrorMessage = "No dependencies found in meta.json";
                }
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
                        var subDepsCount = subDepsElement.EnumerateObject().Count();
                        depItem.HasSubDependencies = subDepsCount > 0;
                        depItem.SubDependencyCount = subDepsCount;
                        
                        dependencies.Add(depItem);
                        
                        // Recursively parse subdependencies
                        if (subDepsCount > 0)
                        {
                            ParseDependenciesRecursive(subDepsElement, dependencies, depth + 1, prop.Name);
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

        private void ParseDisabledDependenciesFromDescription(string metaJson, DependencyScanResult result)
        {
            try
            {
                var jsonDoc = JsonDocument.Parse(metaJson);
                var root = jsonDoc.RootElement;

                if (!root.TryGetProperty("description", out var descElement))
                    return;

                string description = descElement.GetString() ?? "";
                if (string.IsNullOrEmpty(description))
                    return;

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing disabled dependencies: {ex.Message}");
            }
        }
    }
}

