using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SharpCompress.Archives;

namespace VPM.Services
{
    /// <summary>
    /// Extracts content from .VAR archives to the game folder, maintaining directory structure.
    /// Supports extracting all files with the same base name but different extensions.
    /// </summary>
    public class VarContentExtractor
    {
        /// <summary>
        /// Extracts all files with the same base name as the image to the game folder.
        /// Maintains the directory structure from the archive.
        /// </summary>
        /// <param name="varFilePath">Full path to the .var archive</param>
        /// <param name="internalImagePath">Internal path to the image within the archive (e.g., "\Custom\Hair\Female\...\image.jpg")</param>
        /// <param name="gameFolder">Target game folder path</param>
        /// <returns>Number of files extracted</returns>
        public static async Task<int> ExtractRelatedFilesAsync(string varFilePath, string internalImagePath, string gameFolder)
        {
            return await Task.Run(() => ExtractRelatedFiles(varFilePath, internalImagePath, gameFolder));
        }

        /// <summary>
        /// Extracts all files with the same base name as the image and returns paths of extracted parent items.
        /// This allows the UI to update parent item extraction status.
        /// </summary>
        public static async Task<(int count, List<string> extractedParentPaths)> ExtractRelatedFilesWithParentsAsync(string varFilePath, string internalImagePath, string gameFolder)
        {
            return await Task.Run(() => ExtractRelatedFilesWithParents(varFilePath, internalImagePath, gameFolder));
        }

        /// <summary>
        /// Synchronous version of ExtractRelatedFilesAsync
        /// </summary>
        public static int ExtractRelatedFiles(string varFilePath, string internalImagePath, string gameFolder)
        {
            if (!File.Exists(varFilePath))
                throw new FileNotFoundException($"VAR file not found: {varFilePath}");

            if (string.IsNullOrWhiteSpace(internalImagePath))
                throw new ArgumentException("Internal image path cannot be empty");

            if (string.IsNullOrWhiteSpace(gameFolder))
                throw new ArgumentException("Game folder cannot be empty");

            int extractedCount = 0;

            try
            {
                using var archive = SharpCompressHelper.OpenForRead(varFilePath);

                // Get the base name without extension (e.g., "Scalp mid base (by REN)")
                var baseName = Path.GetFileNameWithoutExtension(internalImagePath);
                var directoryPath = Path.GetDirectoryName(internalImagePath);

                // Normalize paths to use forward slashes for consistency
                directoryPath = directoryPath?.Replace('\\', '/') ?? "";

                // Find all files in the archive with the same base name
                List<IArchiveEntry> relatedEntries;
                try
                {
                    relatedEntries = archive.Entries
                        .Where(e => !e.Key.EndsWith("/"))
                        .Where(e =>
                        {
                            var entryBaseName = Path.GetFileNameWithoutExtension(e.Key);
                            // Use forward slash for archive paths (they always use forward slashes)
                            var entryDir = e.Key.Contains('/') 
                                ? e.Key.Substring(0, e.Key.LastIndexOf('/'))
                                : "";

                            // Match if same base name and same directory
                            return entryBaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase) &&
                                   entryDir.Equals(directoryPath, StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                }
                catch (SharpCompress.Common.ArchiveException archiveEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Archive is corrupted or invalid: {archiveEx.Message}");
                    // Log the error but don't throw - return 0 to indicate no files were extracted
                    System.Diagnostics.Debug.WriteLine($"Failed to extract from corrupted VAR file: {Path.GetFileName(varFilePath)}");
                    return 0;
                }

                if (relatedEntries.Count == 0)
                    return 0;

                // Extract each related file
                foreach (var entry in relatedEntries)
                {
                    try
                    {
                        // Construct the target path in the game folder
                        var targetPath = Path.Combine(gameFolder, entry.Key.Replace('/', Path.DirectorySeparatorChar));

                        // Ensure target directory exists
                        var targetDirectory = Path.GetDirectoryName(targetPath);
                        if (!Directory.Exists(targetDirectory))
                        {
                            Directory.CreateDirectory(targetDirectory);
                        }

                        // Extract the file
                        using (var entryStream = entry.OpenEntryStream())
                        using (var fileStream = File.Create(targetPath))
                        {
                            entryStream.CopyTo(fileStream);
                        }

                        extractedCount++;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract {entry.Key}: {ex.Message}");
                    }
                }

                // Now check for .vaj files and extract their dependencies
                var vajEntry = relatedEntries.FirstOrDefault(e => e.Key.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase));
                if (vajEntry != null)
                {
                    extractedCount += ExtractVajDependencies(archive.Archive, vajEntry, gameFolder, directoryPath);
                }

                // Check for .vap files and extract their dependencies
                var vapEntry = relatedEntries.FirstOrDefault(e => e.Key.EndsWith(".vap", StringComparison.OrdinalIgnoreCase));
                if (vapEntry != null)
                {
                    extractedCount += ExtractVapDependencies(archive.Archive, vapEntry, gameFolder, directoryPath);
                }

                return extractedCount;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during extraction: {ex.Message}");
                return 0; // Return 0 on any error to indicate no files were extracted
            }
        }

        /// <summary>
        /// Synchronous version of ExtractRelatedFilesWithParentsAsync
        /// Tracks which parent items (.vaj files) were extracted as dependencies
        /// </summary>
        public static (int count, List<string> extractedParentPaths) ExtractRelatedFilesWithParents(string varFilePath, string internalImagePath, string gameFolder)
        {
            var extractedParents = new List<string>();
            
            if (!File.Exists(varFilePath))
                throw new FileNotFoundException($"VAR file not found: {varFilePath}");

            if (string.IsNullOrWhiteSpace(internalImagePath))
                throw new ArgumentException("Internal image path cannot be empty");

            if (string.IsNullOrWhiteSpace(gameFolder))
                throw new ArgumentException("Game folder cannot be empty");

            int extractedCount = 0;

            try
            {
                using var archive = SharpCompressHelper.OpenForRead(varFilePath);

                var baseName = Path.GetFileNameWithoutExtension(internalImagePath);
                var directoryPath = Path.GetDirectoryName(internalImagePath);
                directoryPath = directoryPath?.Replace('\\', '/') ?? "";

                List<IArchiveEntry> relatedEntries;
                try
                {
                    relatedEntries = archive.Entries
                        .Where(e => !e.Key.EndsWith("/"))
                        .Where(e =>
                        {
                            var entryBaseName = Path.GetFileNameWithoutExtension(e.Key);
                            // Use forward slash for archive paths (they always use forward slashes)
                            var entryDir = e.Key.Contains('/') 
                                ? e.Key.Substring(0, e.Key.LastIndexOf('/'))
                                : "";
                            return entryBaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase) &&
                                   entryDir.Equals(directoryPath, StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                }
                catch (SharpCompress.Common.ArchiveException archiveEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Archive is corrupted or invalid: {archiveEx.Message}");
                    return (0, extractedParents);
                }

                if (relatedEntries.Count == 0)
                    return (0, extractedParents);

                // Extract each related file
                foreach (var entry in relatedEntries)
                {
                    try
                    {
                        var targetPath = Path.Combine(gameFolder, entry.Key.Replace('/', Path.DirectorySeparatorChar));
                        var targetDirectory = Path.GetDirectoryName(targetPath);
                        if (!Directory.Exists(targetDirectory))
                        {
                            Directory.CreateDirectory(targetDirectory);
                        }

                        using (var entryStream = entry.OpenEntryStream())
                        using (var fileStream = File.Create(targetPath))
                        {
                            entryStream.CopyTo(fileStream);
                        }

                        extractedCount++;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract {entry.Key}: {ex.Message}");
                    }
                }

                // Track .vaj files and extract their dependencies
                var vajEntry = relatedEntries.FirstOrDefault(e => e.Key.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase));
                if (vajEntry != null)
                {
                    extractedCount += ExtractVajDependencies(archive.Archive, vajEntry, gameFolder, directoryPath);
                    
                    // Find and track the parent image preview
                    var vajBaseName = Path.GetFileNameWithoutExtension(vajEntry.Key);
                    var vajDir = Path.GetDirectoryName(vajEntry.Key)?.Replace('\\', '/') ?? "";
                    
                    var parentImageEntry = archive.Entries.FirstOrDefault(e => 
                    {
                        if (e.Key.EndsWith("/")) return false;
                        var eBase = Path.GetFileNameWithoutExtension(e.Key);
                        var eDir = Path.GetDirectoryName(e.Key)?.Replace('\\', '/') ?? "";
                        var ext = Path.GetExtension(e.Key).ToLowerInvariant();
                        
                        return eBase.Equals(vajBaseName, StringComparison.OrdinalIgnoreCase) &&
                               eDir.Equals(vajDir, StringComparison.OrdinalIgnoreCase) &&
                               (ext == ".jpg" || ext == ".jpeg" || ext == ".png");
                    });
                    
                    if (parentImageEntry != null)
                    {
                        extractedParents.Add(parentImageEntry.Key);
                    }
                }

                // Track .vap files and extract their dependencies (which may include parent .vaj files)
                var vapEntry = relatedEntries.FirstOrDefault(e => e.Key.EndsWith(".vap", StringComparison.OrdinalIgnoreCase));
                if (vapEntry != null)
                {
                    var (vapCount, vapParents) = ExtractVapDependenciesWithTracking(archive.Archive, vapEntry, gameFolder, directoryPath);
                    extractedCount += vapCount;
                    extractedParents.AddRange(vapParents);
                }

                return (extractedCount, extractedParents);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during extraction: {ex.Message}");
                return (0, extractedParents);
            }
        }

        /// <summary>
        /// Removes all files related to the image from the game folder.
        /// </summary>
        public static async Task<int> RemoveRelatedFilesAsync(string varFilePath, string internalImagePath, string gameFolder)
        {
            return await Task.Run(() => RemoveRelatedFiles(varFilePath, internalImagePath, gameFolder));
        }

        /// <summary>
        /// Synchronous version of RemoveRelatedFilesAsync
        /// </summary>
        public static int RemoveRelatedFiles(string varFilePath, string internalImagePath, string gameFolder)
        {
            if (!File.Exists(varFilePath))
                throw new FileNotFoundException($"VAR file not found: {varFilePath}");

            if (string.IsNullOrWhiteSpace(internalImagePath) || string.IsNullOrWhiteSpace(gameFolder))
                return 0;

            int removedCount = 0;

            try
            {
                using var archive = SharpCompressHelper.OpenForRead(varFilePath);

                // Get the base name without extension
                var baseName = Path.GetFileNameWithoutExtension(internalImagePath);
                // For archive paths, use forward slash extraction
                var directoryPath = internalImagePath.Contains('/') 
                    ? internalImagePath.Substring(0, internalImagePath.LastIndexOf('/'))
                    : "";

                // Find all files in the archive with the same base name
                List<IArchiveEntry> relatedEntries;
                try
                {
                    relatedEntries = archive.Entries
                        .Where(e => !e.Key.EndsWith("/"))
                        .Where(e =>
                        {
                            var entryBaseName = Path.GetFileNameWithoutExtension(e.Key);
                            // Use forward slash for archive paths (they always use forward slashes)
                            var entryDir = e.Key.Contains('/') 
                                ? e.Key.Substring(0, e.Key.LastIndexOf('/'))
                                : "";

                            return entryBaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase) &&
                                   entryDir.Equals(directoryPath, StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                }
                catch (SharpCompress.Common.ArchiveException archiveEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Archive is corrupted or invalid: {archiveEx.Message}");
                    return 0; // Return 0 removed items for corrupted archives
                }

                if (relatedEntries.Count == 0)
                    return 0;

                // Remove each related file
                foreach (var entry in relatedEntries)
                {
                    var targetPath = Path.Combine(gameFolder, entry.Key.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                        removedCount++;
                    }
                }

                // Check for .vaj files and remove their dependencies
                var vajEntry = relatedEntries.FirstOrDefault(e => e.Key.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase));
                if (vajEntry != null)
                {
                    removedCount += RemoveVajDependencies(archive.Archive, vajEntry, gameFolder, directoryPath);
                }

                // Check for .vap files and remove their dependencies
                var vapEntry = relatedEntries.FirstOrDefault(e => e.Key.EndsWith(".vap", StringComparison.OrdinalIgnoreCase));
                if (vapEntry != null)
                {
                    removedCount += RemoveVapDependencies(archive.Archive, vapEntry, gameFolder, directoryPath);
                }

                return removedCount;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error removing files: {ex.Message}");
                return removedCount;
            }
        }

        private static int RemoveVajDependencies(IArchive archive, IArchiveEntry vajEntry, string gameFolder, string directoryPath)
        {
            int removedCount = 0;
            try
            {
                List<string> dependencies;
                using (var stream = vajEntry.OpenEntryStream())
                {
                    dependencies = ExtractTextureDependenciesFromVaj(stream);
                }
                foreach (var dependency in dependencies)
                {
                    var dependencyPath = string.IsNullOrEmpty(directoryPath) 
                        ? dependency 
                        : $"{directoryPath}/{dependency}";
                    
                    var depEntry = archive.Entries.FirstOrDefault(e => e.Key.Equals(dependencyPath, StringComparison.OrdinalIgnoreCase) ||
                                         e.Key.Equals(dependencyPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));

                    if (depEntry != null)
                    {
                        var targetPath = Path.Combine(gameFolder, depEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(targetPath))
                        {
                            // Only remove if it's not used by other things (simplified check: just remove for now as requested)
                            // Ideally we'd check if other installed items need this, but for now we follow instructions
                            // However, user said "if only preset is removed then dont remove parent item"
                            // This function is for .vaj dependencies (textures), not parent items themselves.
                            File.Delete(targetPath);
                            removedCount++;
                        }
                    }
                }
            }
            catch (Exception) { }
            return removedCount;
        }

        private static int RemoveVapDependencies(IArchive archive, IArchiveEntry vapEntry, string gameFolder, string directoryPath)
        {
            int removedCount = 0;
            try
            {
                string vapContent;
                using (var stream = vapEntry.OpenEntryStream())
                {
                    using var reader = new StreamReader(stream);
                    vapContent = reader.ReadToEnd();
                }

                var dependencies = ExtractFileDependenciesFromVap(vapContent);
                
                foreach (var dependency in dependencies)
                {
                    // Skip removing parent items (.vaj, .vab, .vam) if we are just removing a preset
                    // The user requirement: "if only preset is removed then dont remove parent item"
                    // We can identify parent items by extension
                    // NOTE: Do NOT skip .vap files - they should be removed as dependencies
                    if (dependency.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase) ||
                        dependency.EndsWith(".vab", StringComparison.OrdinalIgnoreCase) ||
                        dependency.EndsWith(".vam", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Handle relative paths logic similar to extraction
                    var dependencyPath = dependency;
                    if (dependency.StartsWith("./"))
                    {
                        dependencyPath = Path.Combine(directoryPath, dependency.Substring(2)).Replace('\\', '/');
                    }
                    else if (!dependency.Contains("/"))
                    {
                         dependencyPath = string.IsNullOrEmpty(directoryPath) 
                            ? dependency 
                            : $"{directoryPath}/{dependency}";
                    }
                    dependencyPath = dependencyPath.Replace('\\', '/');

                    var depEntry = archive.Entries.FirstOrDefault(e => e.Key.Equals(dependencyPath, StringComparison.OrdinalIgnoreCase) ||
                                         e.Key.EndsWith("/" + dependency, StringComparison.OrdinalIgnoreCase) ||
                                         e.Key.Equals(dependencyPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));

                    if (depEntry != null)
                    {
                        var targetPath = Path.Combine(gameFolder, depEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(targetPath))
                        {
                            File.Delete(targetPath);
                            removedCount++;
                        }
                    }
                }
            }
            catch (Exception) { }
            return removedCount;
        }

        private static string ResolveDependencyPath(string dependency, string directoryPath)
        {
            // Handle relative paths starting with ./
            if (dependency.StartsWith("./"))
            {
                return Path.Combine(directoryPath, dependency.Substring(2)).Replace('\\', '/');
            }

            // Handle absolute paths (starting with Custom/ or Saves/)
            if (dependency.StartsWith("Custom/", StringComparison.OrdinalIgnoreCase) ||
                dependency.StartsWith("Saves/", StringComparison.OrdinalIgnoreCase))
            {
                return dependency;
            }

            // Handle simple filenames (assume relative)
            if (!dependency.Contains("/") && !dependency.Contains("\\"))
            {
                return string.IsNullOrEmpty(directoryPath)
                   ? dependency
                   : $"{directoryPath}/{dependency}";
            }

            // If it has slashes but doesn't start with ./ or Custom/ or Saves/
            // It's ambiguous. It could be relative "Textures/foo.jpg" or absolute "Assets/foo.jpg"
            // In VAM, usually non-absolute paths are relative.
            return string.IsNullOrEmpty(directoryPath)
                ? dependency
                : $"{directoryPath}/{dependency}";
        }

        /// <summary>
        /// Extracts texture dependencies found in a .vaj file
        /// </summary>
        /// <param name="archive">The open archive</param>
        /// <param name="vajEntry">The .vaj file entry</param>
        /// <param name="gameFolder">Target game folder path</param>
        /// <param name="directoryPath">Directory path where the .vaj file is located</param>
        /// <returns>Number of dependency files extracted</returns>
        private static int ExtractVajDependencies(IArchive archive, IArchiveEntry vajEntry, string gameFolder, string directoryPath)
        {
            int extractedCount = 0;

            try
            {
                // Parse the JSON to find texture dependencies
                List<string> dependencies;
                using (var stream = vajEntry.OpenEntryStream())
                {
                    dependencies = ExtractTextureDependenciesFromVaj(stream);
                }

                if (dependencies.Count == 0)
                    return 0;

                // Extract each dependency file from the same directory
                foreach (var dependency in dependencies)
                {
                    try
                    {
                        // Construct the full path within the archive
                        var dependencyPath = ResolveDependencyPath(dependency, directoryPath);

                        // Find the dependency file in the archive
                        var depEntry = archive.Entries
                            .FirstOrDefault(e => e.Key.Equals(dependencyPath, StringComparison.OrdinalIgnoreCase) ||
                                                 e.Key.Equals(dependencyPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));

                        if (depEntry != null)
                        {
                            var targetPath = Path.Combine(gameFolder, depEntry.Key.Replace('/', Path.DirectorySeparatorChar));

                            // Skip if file already exists
                            if (File.Exists(targetPath))
                                continue;

                            // Ensure target directory exists
                            var targetDirectory = Path.GetDirectoryName(targetPath);
                            if (!Directory.Exists(targetDirectory))
                            {
                                Directory.CreateDirectory(targetDirectory);
                            }

                            // Extract the dependency file
                            using (var entryStream = depEntry.OpenEntryStream())
                            using (var fileStream = File.Create(targetPath))
                            {
                                entryStream.CopyTo(fileStream);
                            }

                            extractedCount++;
                        }
                        else
                        {
                            // Dependency not found - this is normal for optional texture files
                            // Don't log as it creates noise for missing optional dependencies
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract dependency {dependency}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting .vaj dependencies: {ex.Message}");
            }

            return extractedCount;
        }

        private static readonly HashSet<string> DependencyExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".vam", ".vmi", ".vaj", ".vab", ".vac",
            ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".bmp", ".tga", ".dds",
            ".mp3", ".wav", ".ogg"
        };

        private static void RecursivelyFindDependencies(JsonElement element, HashSet<string> dependencies)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        RecursivelyFindDependencies(property.Value, dependencies);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        RecursivelyFindDependencies(item, dependencies);
                    }
                    break;
                case JsonValueKind.String:
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        try
                        {
                            // Quick check for extension separator
                            if (value.Contains('.'))
                            {
                                var ext = Path.GetExtension(value);
                                if (!string.IsNullOrEmpty(ext) && DependencyExtensions.Contains(ext))
                                {
                                    // Handle "Package:Path" format
                                    var path = value;
                                    int colonIndex = value.IndexOf(':');
                                    if (colonIndex >= 0)
                                    {
                                        path = value.Substring(colonIndex + 1);
                                    }

                                    // Trim leading slashes to match archive entry keys
                                    path = path.TrimStart('/', '\\');

                                    if (!string.IsNullOrWhiteSpace(path))
                                    {
                                        dependencies.Add(path);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    break;
            }
        }

        /// <summary>
        /// Extracts texture file names from .vaj JSON content
        /// </summary>
        /// <param name="vajStream">Stream of the .vaj file content</param>
        /// <returns>List of texture file names</returns>
        private static List<string> ExtractTextureDependenciesFromVaj(Stream vajStream)
        {
            var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var document = JsonDocument.Parse(vajStream);
                RecursivelyFindDependencies(document.RootElement, dependencies);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing .vaj file: {ex.Message}");
            }

            return dependencies.ToList();
        }

        /// <summary>
        /// Checks if all related files already exist in the game folder
        /// </summary>
        /// <param name="varFilePath">Full path to the .var archive</param>
        /// <param name="internalImagePath">Internal path to the image within the archive</param>
        /// <param name="gameFolder">Target game folder path</param>
        /// <returns>True if all related files exist, false otherwise</returns>
        public static async Task<bool> AreRelatedFilesExtractedAsync(string varFilePath, string internalImagePath, string gameFolder)
        {
            return await Task.Run(() => AreRelatedFilesExtracted(varFilePath, internalImagePath, gameFolder));
        }

        /// <summary>
        /// Synchronous version of AreRelatedFilesExtractedAsync
        /// </summary>
        public static bool AreRelatedFilesExtracted(string varFilePath, string internalImagePath, string gameFolder)
        {
            if (!File.Exists(varFilePath))
                return false;

            if (string.IsNullOrWhiteSpace(internalImagePath) || string.IsNullOrWhiteSpace(gameFolder))
                return false;

            try
            {
                using var archive = SharpCompressHelper.OpenForRead(varFilePath);

                // Get the base name without extension
                var baseName = Path.GetFileNameWithoutExtension(internalImagePath);
                // For archive paths, use forward slash extraction
                var directoryPath = internalImagePath.Contains('/') 
                    ? internalImagePath.Substring(0, internalImagePath.LastIndexOf('/'))
                    : "";

                // Find all files in the archive with the same base name
                List<IArchiveEntry> relatedEntries;
                try
                {
                    relatedEntries = archive.Entries
                        .Where(e => !e.Key.EndsWith("/"))
                        .Where(e =>
                        {
                            var entryBaseName = Path.GetFileNameWithoutExtension(e.Key);
                            // Use forward slash for archive paths (they always use forward slashes)
                            var entryDir = e.Key.Contains('/') 
                                ? e.Key.Substring(0, e.Key.LastIndexOf('/'))
                                : "";

                            return entryBaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase) &&
                                   entryDir.Equals(directoryPath, StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                }
                catch (SharpCompress.Common.ArchiveException archiveEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Archive is corrupted or invalid: {archiveEx.Message}");
                    return false; // Corrupted archive means files are not extracted
                }

                if (relatedEntries.Count == 0)
                    return false;

                // For extraction status, we only need to check if the main content file exists
                // (not all related files, since previews might not be extracted)
                // Priority: .json (Scene) > .vap (Appearance) > .vaj (Hair/Clothing) > .jpg (Preview)
                
                // Check for .json (Scene files)
                var jsonEntry = relatedEntries.FirstOrDefault(e => e.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                if (jsonEntry != null)
                {
                    var targetPath = Path.Combine(gameFolder, jsonEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(targetPath);
                }

                // Check for .vap (Appearance files)
                var vapEntry = relatedEntries.FirstOrDefault(e => e.Key.EndsWith(".vap", StringComparison.OrdinalIgnoreCase));
                if (vapEntry != null)
                {
                    var targetPath = Path.Combine(gameFolder, vapEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(targetPath);
                }

                // Check for .vaj (Hair/Clothing files)
                var vajEntry = relatedEntries.FirstOrDefault(e => e.Key.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase));
                if (vajEntry != null)
                {
                    var targetPath = Path.Combine(gameFolder, vajEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(targetPath);
                }

                // Check for .jpg (Preview image)
                var jpgEntry = relatedEntries.FirstOrDefault(e => 
                    e.Key.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    e.Key.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    e.Key.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                if (jpgEntry != null)
                {
                    var targetPath = Path.Combine(gameFolder, jpgEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(targetPath);
                }

                return false;
            }
            catch (SharpCompress.Common.ArchiveException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Archive error checking extracted files: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking extracted files: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if all dependency files from a .vaj file exist in the game folder
        /// </summary>
        /// <param name="archive">The open archive</param>
        /// <param name="vajEntry">The .vaj file entry</param>
        /// <param name="gameFolder">Target game folder path</param>
        /// <param name="directoryPath">Directory path where the .vaj file is located</param>
        /// <returns>True if all dependencies exist, false otherwise</returns>
        private static bool AreDependenciesExtracted(IArchive archive, IArchiveEntry vajEntry, string gameFolder, string directoryPath)
        {
            try
            {
                // Parse the JSON to find texture dependencies
                List<string> dependencies;
                using (var stream = vajEntry.OpenEntryStream())
                {
                    dependencies = ExtractTextureDependenciesFromVaj(stream);
                }

                if (dependencies.Count == 0)
                    return true; // No dependencies, so they're all "extracted"

                // Check each dependency file
                foreach (var dependency in dependencies)
                {
                    // Construct the full path within the archive
                    var dependencyPath = ResolveDependencyPath(dependency, directoryPath);

                    // Find the dependency file in the archive
                    var depEntry = archive.Entries
                        .FirstOrDefault(e => e.Key.Equals(dependencyPath, StringComparison.OrdinalIgnoreCase) ||
                                             e.Key.Equals(dependencyPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));

                    if (depEntry != null)
                    {
                        var targetPath = Path.Combine(gameFolder, depEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(targetPath))
                            return false; // Dependency file doesn't exist
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking .vaj dependencies: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the category from the internal path (e.g., "Hair" from "\Custom\Hair\Female\...")
        /// </summary>
        public static string GetCategoryFromPath(string internalPath)
        {
            if (string.IsNullOrWhiteSpace(internalPath))
                return "";

            var parts = internalPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            // Expected structure: Custom/[Category]/[Subcategory]/...
            // or Scene/[Category]/...
            if (parts.Length >= 2)
            {
                // Special-case: VaM previews under Custom/Atom/Person/<Category>
                // Handle common categories like Skin, Clothing, Hair, Appearance so labels are specific and not just "Atom"
                // Examples:
                //   "\\Custom\\Atom\\Person\\Skin\\..."     -> "Skin"
                //   "\\Custom\\Atom\\Person\\Clothing\\..." -> "Clothing"
                //   "\\Custom\\Atom\\Person\\Hair\\..."     -> "Hair"
                //   "\\Custom\\Atom\\Person\\Appearance\\..." -> "Appearance"
                if (parts.Length >= 4 &&
                    parts[0].Equals("Custom", StringComparison.OrdinalIgnoreCase) &&
                    parts[1].Equals("Atom", StringComparison.OrdinalIgnoreCase) &&
                    parts[2].Equals("Person", StringComparison.OrdinalIgnoreCase))
                {
                    var subCategory = parts[3];
                    if (subCategory.Equals("Skin", StringComparison.OrdinalIgnoreCase) ||
                        subCategory.Equals("Clothing", StringComparison.OrdinalIgnoreCase) ||
                        subCategory.Equals("Hair", StringComparison.OrdinalIgnoreCase) ||
                        subCategory.Equals("Appearance", StringComparison.OrdinalIgnoreCase))
                    {
                        return subCategory;
                    }
                }

                // Return the second part (category) if first part is Custom or Scene
                if ((parts[0].Equals("Custom", StringComparison.OrdinalIgnoreCase) ||
                     parts[0].Equals("Scene", StringComparison.OrdinalIgnoreCase)) &&
                    parts.Length > 1)
                {
                    return parts[1];
                }

                // Handle VaM typical scene preview path: Saves/scene/... -> label should be "Scene"
                if (parts[0].Equals("Saves", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 &&
                    parts[1].Equals("scene", StringComparison.OrdinalIgnoreCase))
                {
                    return "Scene";
                }

                // Handle VaM pose preview path: Saves/Person/pose/... -> label should be "Pose"
                if (parts[0].Equals("Saves", StringComparison.OrdinalIgnoreCase) && parts.Length > 2 &&
                    parts[1].Equals("Person", StringComparison.OrdinalIgnoreCase) &&
                    parts[2].Equals("pose", StringComparison.OrdinalIgnoreCase))
                {
                    return "Pose";
                }
            }

            return "";
        }

        /// <summary>
        /// Gets a single character representation of the category for display
        /// </summary>
        public static string GetCategoryLetter(string internalPath)
        {
            var category = GetCategoryFromPath(internalPath);
            if (string.IsNullOrWhiteSpace(category))
                return "?";

            return category.Substring(0, 1).ToUpper();
        }
        /// <summary>
        /// Extracts dependencies found in a .vap file (textures, .vab, sim files)
        /// </summary>
        private static int ExtractVapDependencies(IArchive archive, IArchiveEntry vapEntry, string gameFolder, string directoryPath)
        {
            int extractedCount = 0;

            try
            {
                // Parse the JSON to find dependencies
                List<string> dependencies;
                using (var stream = vapEntry.OpenEntryStream())
                {
                    dependencies = ExtractFileDependenciesFromVap(stream);
                }

                if (dependencies.Count == 0)
                    return 0;

                // Extract each dependency file
                foreach (var dependency in dependencies)
                {
                    try
                    {
                        // Handle relative paths
                        var dependencyPath = ResolveDependencyPath(dependency, directoryPath);

                        // Find the dependency file in the archive
                        var depEntry = archive.Entries
                            .FirstOrDefault(e => e.Key.Equals(dependencyPath, StringComparison.OrdinalIgnoreCase) ||
                                                 e.Key.EndsWith("/" + dependency, StringComparison.OrdinalIgnoreCase) ||
                                                 e.Key.Equals(dependencyPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
                        
                        if (depEntry != null)
                        {
                            var targetPath = Path.Combine(gameFolder, depEntry.Key.Replace('/', Path.DirectorySeparatorChar));

                            // Skip if file already exists
                            if (File.Exists(targetPath))
                                continue;

                            // Ensure target directory exists
                            var targetDirectory = Path.GetDirectoryName(targetPath);
                            if (!Directory.Exists(targetDirectory))
                            {
                                Directory.CreateDirectory(targetDirectory);
                            }

                            // Extract the file
                            using (var entryStream = depEntry.OpenEntryStream())
                            using (var fileStream = File.Create(targetPath))
                            {
                                entryStream.CopyTo(fileStream);
                            }

                            extractedCount++;

                            // Recursively extract dependencies for .vaj files (parent items)
                            if (dependency.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase))
                            {
                                var vajDirectory = Path.GetDirectoryName(depEntry.Key)?.Replace('\\', '/');
                                extractedCount += ExtractVajDependencies(archive, depEntry, gameFolder, vajDirectory);

                                // Extract parent image preview
                                try
                                {
                                    var vajBaseName = Path.GetFileNameWithoutExtension(depEntry.Key);
                                    var vajDir = Path.GetDirectoryName(depEntry.Key)?.Replace('\\', '/') ?? "";
                                    
                                    var imageEntry = archive.Entries.FirstOrDefault(e => 
                                    {
                                        if (e.Key.EndsWith("/")) return false;
                                        var eBase = Path.GetFileNameWithoutExtension(e.Key);
                                        var eDir = Path.GetDirectoryName(e.Key)?.Replace('\\', '/') ?? "";
                                        var ext = Path.GetExtension(e.Key).ToLowerInvariant();
                                        
                                        return eBase.Equals(vajBaseName, StringComparison.OrdinalIgnoreCase) &&
                                               eDir.Equals(vajDir, StringComparison.OrdinalIgnoreCase) &&
                                               (ext == ".jpg" || ext == ".jpeg" || ext == ".png");
                                    });

                                    if (imageEntry != null)
                                    {
                                        var imageTargetPath = Path.Combine(gameFolder, imageEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                                        if (!File.Exists(imageTargetPath))
                                        {
                                            var imageTargetDirectory = Path.GetDirectoryName(imageTargetPath);
                                            if (!Directory.Exists(imageTargetDirectory))
                                            {
                                                Directory.CreateDirectory(imageTargetDirectory);
                                            }
                                            using (var entryStream = imageEntry.OpenEntryStream())
                                            using (var fileStream = File.Create(imageTargetPath))
                                            {
                                                entryStream.CopyTo(fileStream);
                                            }
                                            extractedCount++;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Failed to extract parent image preview: {ex.Message}");
                                }
                            }
                            // Recursively extract dependencies for .vam files (presets)
                            else if (dependency.EndsWith(".vam", StringComparison.OrdinalIgnoreCase))
                            {
                                var vamDirectory = Path.GetDirectoryName(depEntry.Key)?.Replace('\\', '/');
                                extractedCount += ExtractVapDependencies(archive, depEntry, gameFolder, vamDirectory);
                            }
                        }
                        else
                        {
                            // Try to find it in the same directory if we haven't already
                            if (!dependencyPath.StartsWith(directoryPath))
                            {
                                var siblingPath = string.IsNullOrEmpty(directoryPath) 
                                    ? Path.GetFileName(dependency)
                                    : $"{directoryPath}/{Path.GetFileName(dependency)}";
                                
                                depEntry = archive.Entries.FirstOrDefault(e => e.Key.Equals(siblingPath, StringComparison.OrdinalIgnoreCase));
                                
                                if (depEntry != null)
                                {
                                     var targetPath = Path.Combine(gameFolder, depEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                                     if (!File.Exists(targetPath))
                                     {
                                         var targetDirectory = Path.GetDirectoryName(targetPath);
                                         if (!Directory.Exists(targetDirectory)) Directory.CreateDirectory(targetDirectory);
                                         using (var entryStream = depEntry.OpenEntryStream())
                                         using (var fileStream = File.Create(targetPath)) entryStream.CopyTo(fileStream);
                                         extractedCount++;
                                     }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract dependency {dependency}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting .vap dependencies: {ex.Message}");
            }

            return extractedCount;
        }

        /// <summary>
        /// Extracts dependencies from a .vap file and tracks which parent items (.vaj files) were extracted
        /// </summary>
        private static (int count, List<string> parentPaths) ExtractVapDependenciesWithTracking(IArchive archive, IArchiveEntry vapEntry, string gameFolder, string directoryPath)
        {
            var parentPaths = new List<string>();
            int extractedCount = 0;

            try
            {
                // Parse the JSON to find dependencies
                List<string> dependencies;
                using (var stream = vapEntry.OpenEntryStream())
                {
                    dependencies = ExtractFileDependenciesFromVap(stream);
                }

                if (dependencies.Count == 0)
                    return (0, parentPaths);

                // Extract each dependency file
                foreach (var dependency in dependencies)
                {
                    try
                    {
                        // Handle relative paths
                        var dependencyPath = ResolveDependencyPath(dependency, directoryPath);

                        // Find the dependency file in the archive
                        var depEntry = archive.Entries
                            .FirstOrDefault(e => e.Key.Equals(dependencyPath, StringComparison.OrdinalIgnoreCase) ||
                                                 e.Key.EndsWith("/" + dependency, StringComparison.OrdinalIgnoreCase) ||
                                                 e.Key.Equals(dependencyPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
                        
                        if (depEntry != null)
                        {
                            var targetPath = Path.Combine(gameFolder, depEntry.Key.Replace('/', Path.DirectorySeparatorChar));

                            // Skip if file already exists
                            if (File.Exists(targetPath))
                                continue;

                            // Ensure target directory exists
                            var targetDirectory = Path.GetDirectoryName(targetPath);
                            if (!Directory.Exists(targetDirectory))
                            {
                                Directory.CreateDirectory(targetDirectory);
                            }

                            // Extract the file
                            using (var entryStream = depEntry.OpenEntryStream())
                            using (var fileStream = File.Create(targetPath))
                            {
                                entryStream.CopyTo(fileStream);
                            }

                            extractedCount++;

                            // Track and recursively extract dependencies for .vaj files (parent items)
                            if (dependency.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase))
                            {
                                var vajDirectory = Path.GetDirectoryName(depEntry.Key)?.Replace('\\', '/');
                                extractedCount += ExtractVajDependencies(archive, depEntry, gameFolder, vajDirectory);
                                parentPaths.Add(depEntry.Key); // Track this parent item

                                // Extract parent image preview
                                try
                                {
                                    var vajBaseName = Path.GetFileNameWithoutExtension(depEntry.Key);
                                    var vajDir = Path.GetDirectoryName(depEntry.Key)?.Replace('\\', '/') ?? "";
                                    
                                    var imageEntry = archive.Entries.FirstOrDefault(e => 
                                    {
                                        if (e.Key.EndsWith("/")) return false;
                                        var eBase = Path.GetFileNameWithoutExtension(e.Key);
                                        var eDir = Path.GetDirectoryName(e.Key)?.Replace('\\', '/') ?? "";
                                        var ext = Path.GetExtension(e.Key).ToLowerInvariant();
                                        
                                        return eBase.Equals(vajBaseName, StringComparison.OrdinalIgnoreCase) &&
                                               eDir.Equals(vajDir, StringComparison.OrdinalIgnoreCase) &&
                                               (ext == ".jpg" || ext == ".jpeg" || ext == ".png");
                                    });

                                    if (imageEntry != null)
                                    {
                                        var imageTargetPath = Path.Combine(gameFolder, imageEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                                        if (!File.Exists(imageTargetPath))
                                        {
                                            var imageTargetDirectory = Path.GetDirectoryName(imageTargetPath);
                                            if (!Directory.Exists(imageTargetDirectory))
                                            {
                                                Directory.CreateDirectory(imageTargetDirectory);
                                            }
                                            using (var entryStream = imageEntry.OpenEntryStream())
                                            using (var fileStream = File.Create(imageTargetPath))
                                            {
                                                entryStream.CopyTo(fileStream);
                                            }
                                            extractedCount++;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Failed to extract parent image preview: {ex.Message}");
                                }
                            }
                            // Recursively extract dependencies for .vam files (presets)
                            else if (dependency.EndsWith(".vam", StringComparison.OrdinalIgnoreCase))
                            {
                                var vamDirectory = Path.GetDirectoryName(depEntry.Key)?.Replace('\\', '/');
                                var (vamCount, vamParents) = ExtractVapDependenciesWithTracking(archive, depEntry, gameFolder, vamDirectory);
                                extractedCount += vamCount;
                                parentPaths.AddRange(vamParents);
                            }
                        }
                        else
                        {
                            // Try to find it in the same directory if we haven't already
                            if (!dependencyPath.StartsWith(directoryPath))
                            {
                                var siblingPath = string.IsNullOrEmpty(directoryPath) 
                                    ? Path.GetFileName(dependency)
                                    : $"{directoryPath}/{Path.GetFileName(dependency)}";
                                
                                depEntry = archive.Entries.FirstOrDefault(e => e.Key.Equals(siblingPath, StringComparison.OrdinalIgnoreCase));
                                
                                if (depEntry != null)
                                {
                                     var targetPath = Path.Combine(gameFolder, depEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                                     if (!File.Exists(targetPath))
                                     {
                                         var targetDirectory = Path.GetDirectoryName(targetPath);
                                         if (!Directory.Exists(targetDirectory)) Directory.CreateDirectory(targetDirectory);
                                         using (var entryStream = depEntry.OpenEntryStream())
                                         using (var fileStream = File.Create(targetPath)) entryStream.CopyTo(fileStream);
                                         extractedCount++;
                                     }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to extract dependency {dependency}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting .vap dependencies with tracking: {ex.Message}");
            }

            return (extractedCount, parentPaths);
        }

        /// <summary>
        /// Checks if all dependency files from a .vap file exist in the game folder
        /// </summary>
        private static bool AreVapDependenciesExtracted(IArchive archive, IArchiveEntry vapEntry, string gameFolder, string directoryPath)
        {
            try
            {
                // Read the .vap file content
                string vapContent;
                using (var stream = vapEntry.OpenEntryStream())
                using (var reader = new StreamReader(stream))
                {
                    vapContent = reader.ReadToEnd();
                }

                // Parse the JSON to find dependencies
                var dependencies = ExtractFileDependenciesFromVap(vapContent);

                if (dependencies.Count == 0)
                    return true;

                // Check each dependency file
                foreach (var dependency in dependencies)
                {
                    // Handle relative paths
                    var dependencyPath = dependency;
                    if (dependency.StartsWith("./"))
                    {
                        dependencyPath = Path.Combine(directoryPath, dependency.Substring(2)).Replace('\\', '/');
                    }
                    else if (!dependency.Contains("/"))
                    {
                         dependencyPath = string.IsNullOrEmpty(directoryPath) 
                            ? dependency 
                            : $"{directoryPath}/{dependency}";
                    }

                    dependencyPath = dependencyPath.Replace('\\', '/');

                    // Find the dependency file in the archive
                    var depEntry = archive.Entries
                        .FirstOrDefault(e => e.Key.Equals(dependencyPath, StringComparison.OrdinalIgnoreCase) ||
                                             e.Key.EndsWith("/" + dependency, StringComparison.OrdinalIgnoreCase) ||
                                             e.Key.Equals(dependencyPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));

                    if (depEntry != null)
                    {
                        var targetPath = Path.Combine(gameFolder, depEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(targetPath))
                            return false;

                        // Recursively check dependencies for .vaj files
                        if (dependency.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase))
                        {
                            var vajDirectory = Path.GetDirectoryName(depEntry.Key)?.Replace('\\', '/');
                            if (!AreDependenciesExtracted(archive, depEntry, gameFolder, vajDirectory))
                                return false;
                        }
                        // Recursively check dependencies for .vam files
                        else if (dependency.EndsWith(".vam", StringComparison.OrdinalIgnoreCase))
                        {
                            var vamDirectory = Path.GetDirectoryName(depEntry.Key)?.Replace('\\', '/');
                            if (!AreVapDependenciesExtracted(archive, depEntry, gameFolder, vamDirectory))
                                return false;
                        }
                    }
                    else
                    {
                         // Try sibling check
                         if (!dependencyPath.StartsWith(directoryPath))
                         {
                            var siblingPath = string.IsNullOrEmpty(directoryPath) 
                                ? Path.GetFileName(dependency)
                                : $"{directoryPath}/{Path.GetFileName(dependency)}";
                            
                            depEntry = archive.Entries.FirstOrDefault(e => e.Key.Equals(siblingPath, StringComparison.OrdinalIgnoreCase));
                            if (depEntry != null)
                            {
                                var targetPath = Path.Combine(gameFolder, depEntry.Key.Replace('/', Path.DirectorySeparatorChar));
                                if (!File.Exists(targetPath)) return false;
                            }
                         }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking .vap dependencies: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Extracts file dependencies from .vap JSON content
        /// </summary>
        private static List<string> ExtractFileDependenciesFromVap(Stream vapStream)
        {
            try
            {
                using var document = JsonDocument.Parse(vapStream);
                return ExtractFileDependenciesFromVap(document);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing .vap file: {ex.Message}");
                return new List<string>();
            }
        }

        private static List<string> ExtractFileDependenciesFromVap(string vapContent)
        {
            try
            {
                using var document = JsonDocument.Parse(vapContent);
                return ExtractFileDependenciesFromVap(document);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing .vap file: {ex.Message}");
                return new List<string>();
            }
        }

        private static List<string> ExtractFileDependenciesFromVap(JsonDocument document)
        {
            var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var root = document.RootElement;

            // 1. General recursive scan for any file paths
            RecursivelyFindDependencies(root, dependencies);

            // 2. Keep existing heuristic for IDs without extensions
            if (root.TryGetProperty("storables", out var storables) && storables.ValueKind == JsonValueKind.Array)
            {
                foreach (var storable in storables.EnumerateArray())
                {
                    // Check for ID to infer .vab and sim files
                    if (storable.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrEmpty(id) && !id.Contains('.')) // Only if no extension found
                        {
                            // Format: "package:AssetStyle" or "package:AssetSim"
                            var parts = id.Split(':');
                            if (parts.Length > 1)
                            {
                                var assetName = parts[1];
                                
                                // Heuristic: Strip common suffixes to find the base asset name
                                var suffixes = new[] { "Style", "Sim", "WrapControl", "ItemControl", "ItemDeleter", "ItemReloader", "MaterialCombined" };
                                foreach (var suffix in suffixes)
                                {
                                    if (assetName.EndsWith(suffix))
                                    {
                                        assetName = assetName.Substring(0, assetName.Length - suffix.Length);
                                        break; // Only strip one suffix
                                    }
                                }
                                
                                if (!string.IsNullOrEmpty(assetName))
                                {
                                    dependencies.Add($"{assetName}.vab");
                                    dependencies.Add($"{assetName}.vaj");
                                    dependencies.Add($"{assetName}.vam");
                                    dependencies.Add($"{assetName}.jpg");
                                    dependencies.Add($"{assetName} sim.jpg");
                                }
                            }
                        }
                    }
                }
            }

            return dependencies.ToList();
        }
    }
}
