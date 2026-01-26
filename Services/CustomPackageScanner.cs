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
                        // Determine extensions to scan based on folder
                        var extensions = new List<string> { "*.vam", "*.vab", "*.vaj" };
                        
                        // Add .vap for folders other than Atom\Person to avoid duplication with ScanPresets
                        // ScanPresets in UnifiedCustomContentScanner already handles .vap in Atom\Person
                        if (!folderPath.EndsWith(Path.Combine("Atom", "Person"), StringComparison.OrdinalIgnoreCase))
                        {
                            extensions.Add("*.vap");
                        }

                        var folderItems = ScanFolderForPackages(folderPath, customBasePath, extensions.ToArray());
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
        /// Scans a specific folder for package files
        /// </summary>
        private List<CustomAtomItem> ScanFolderForPackages(string folderPath, string customBasePath, string[] extensions)
        {
            var items = new List<CustomAtomItem>();
            var foundFiles = new List<string>();

            try
            {
                // 1. Collect all relevant files
                foreach (var ext in extensions)
                {
                    foundFiles.AddRange(SymlinkSafeFileSystem.EnumerateFilesSafe(folderPath, ext, true));
                }

                // 2. Group by Directory and FileName (without extension)
                var groupedFiles = foundFiles
                    .Select(f => new { Path = f, Dir = Path.GetDirectoryName(f), Name = Path.GetFileNameWithoutExtension(f) })
                    .GroupBy(x => new { x.Dir, x.Name });

                foreach (var group in groupedFiles)
                {
                    try
                    {
                        // 3. Determine primary file
                        string primaryFile = null;

                        // Priority: .vam > .vaj > .vab > .vap
                        var groupFiles = group.Select(x => x.Path).ToList();

                        if (groupFiles.Any(f => f.EndsWith(".vam", StringComparison.OrdinalIgnoreCase)))
                            primaryFile = groupFiles.First(f => f.EndsWith(".vam", StringComparison.OrdinalIgnoreCase));
                        else if (groupFiles.Any(f => f.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase)))
                            primaryFile = groupFiles.First(f => f.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase));
                        else if (groupFiles.Any(f => f.EndsWith(".vab", StringComparison.OrdinalIgnoreCase)))
                            primaryFile = groupFiles.First(f => f.EndsWith(".vab", StringComparison.OrdinalIgnoreCase));
                        else if (groupFiles.Any(f => f.EndsWith(".vap", StringComparison.OrdinalIgnoreCase)))
                            primaryFile = groupFiles.First(f => f.EndsWith(".vap", StringComparison.OrdinalIgnoreCase));

                        if (primaryFile == null) continue;

                        // 4. Calculate total size
                        // Get ALL files with this name in the directory (including .jpg, etc)
                        var dir = group.Key.Dir;
                        var name = group.Key.Name;
                        var allRelatedFiles = Directory.GetFiles(dir, name + ".*");

                        long totalSize = 0;
                        foreach (var f in allRelatedFiles)
                        {
                            // Ensure strict name matching (ignore case)
                            if (Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                            {
                                totalSize += new FileInfo(f).Length;
                            }
                        }

                        // 5. Create Item
                        var item = CreateCustomPackageItemFromFile(primaryFile, customBasePath);
                        if (item != null)
                        {
                            item.FileSize = totalSize;
                            items.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error processing group {group.Key.Name}: {ex.Message}");
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

            // Determine content type based on extension
            var extension = Path.GetExtension(vamPath).ToLowerInvariant();
            var contentType = "Package";
            if (extension == ".vap")
            {
                contentType = "Preset";
            }

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
                ContentType = contentType
            };

            // Parse dependencies from the .vam file if it's a JSON-based package
            // .vab is a binary asset bundle, so skip it
            if (extension != ".vab")
            {
                PresetScanner.ParsePresetDependencies(item);
            }

            // If it's a preset (.vap), try to find parent files
            if (extension == ".vap")
            {
                var parentName = PresetScanner.GetParentItemName(vamPath);
                if (!string.IsNullOrEmpty(parentName))
                {
                    var directory = Path.GetDirectoryName(vamPath);
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
            }

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
