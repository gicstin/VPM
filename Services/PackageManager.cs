using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    public class PackageManager
    {
        private const int BUFFER_SIZE = 81920; // 80KB buffer
        private static readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
        private readonly Regex _varPattern;

        private readonly ConcurrentDictionary<string, PackageSnapshot> _snapshotCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _throttle = new(Environment.ProcessorCount * 2); // Parallel processing throttle
        private readonly ResiliencyManager _resiliencyManager = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _packageLocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _packageStatusIndex = new(StringComparer.OrdinalIgnoreCase);
        private bool _statusIndexBuilt = false;
        private readonly object _statusIndexLock = new object();
        private readonly VarIntegrityScanner _integrityScanner = new();
        
        public Dictionary<string, VarMetadata> PackageMetadata { get; private set; } = new Dictionary<string, VarMetadata>(StringComparer.OrdinalIgnoreCase);
        // Buffer for preview image locations collected while processing VARs (key: packageBase)
        public ConcurrentDictionary<string, List<ImageLocation>> PreviewImageIndex { get; } = new ConcurrentDictionary<string, List<ImageLocation>>(StringComparer.OrdinalIgnoreCase);

        private readonly string _cacheFolder;
        private readonly BinaryMetadataCache _binaryCache;
        private readonly OptimizedVarScanner _varScanner;

        private static readonly string[] RolePriorityOrder = { PackageRoles.Loaded, PackageRoles.Available, PackageRoles.Archived };
        private static readonly Dictionary<string, int> RolePriorityMap = RolePriorityOrder
            .Select((role, index) => (role, index))
            .ToDictionary(pair => pair.role, pair => pair.index, StringComparer.OrdinalIgnoreCase);

        private static int GetRolePriority(string role)
        {
            return RolePriorityMap.TryGetValue(role ?? string.Empty, out var rank) ? rank : RolePriorityMap.Count;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }

        private static bool IsArchivedPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalized.IndexOf("" + Path.DirectorySeparatorChar + "ArchivedPackages" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetBasePackageName(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
            {
                return string.Empty;
            }

            var hashIndex = packageName.IndexOf('#');
            return hashIndex >= 0 ? packageName[..hashIndex] : packageName;
        }

        private static bool IsSameBasePackage(string packageKey, string baseName)
        {
            return string.Equals(GetBasePackageName(packageKey), baseName, StringComparison.OrdinalIgnoreCase);
        }

        private static string DetermineRoleFromKey(string packageKey)
        {
            if (string.IsNullOrEmpty(packageKey))
            {
                return PackageRoles.Loaded;
            }

            var hashIndex = packageKey.IndexOf('#');
            if (hashIndex < 0)
            {
                return PackageRoles.Loaded;
            }

            var suffix = packageKey[(hashIndex + 1)..].ToLowerInvariant();
            if (suffix.StartsWith("archived"))
            {
                return PackageRoles.Archived;
            }

            if (suffix.StartsWith("available"))
            {
                return PackageRoles.Available;
            }

            if (suffix.StartsWith("loaded"))
            {
                return PackageRoles.Loaded;
            }

            return PackageRoles.Loaded;
        }

        private static DateTime ConvertUtcTicksToLocal(long utcTicks)
        {
            var unspecified = new DateTime(utcTicks);
            var utc = DateTime.SpecifyKind(unspecified, DateTimeKind.Utc);
            return utc.ToLocalTime();
        }

        private async Task<T> RetryWithPolicyAsync<T>(Func<Task<T>> operation, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex) when (i < maxRetries - 1 && 
                    (ex is IOException || ex is UnauthorizedAccessException))
                {
                    await Task.Delay((i + 1) * 200); // Exponential backoff
                }
            }
            return await operation(); // Final try
        }

        public PackageManager(string cacheFolder)
        {
            _cacheFolder = cacheFolder;
            _binaryCache = new BinaryMetadataCache();
            _varScanner = new OptimizedVarScanner();

            _varPattern = new Regex(@"^([^.]+)\.(.+?)\.(\d+)\.var$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            
            // Load binary cache on initialization
            LoadBinaryCache();
        }
        
        /// <summary>
        /// Loads the binary metadata cache from disk
        /// </summary>
        private void LoadBinaryCache()
        {
            try
            {
                var loaded = _binaryCache.LoadCache();
                if (loaded)
                {
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Saves the binary metadata cache to disk
        /// Call this after scanning packages to persist the cache
        /// </summary>
        public void SaveBinaryCache()
        {
            try
            {
                var (hits, misses, hitRate) = _binaryCache.GetStatistics();
                
                _binaryCache.SaveCache();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BinaryCache] Error saving cache: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clears the binary metadata cache completely (memory + disk)
        /// </summary>
        public bool ClearBinaryCache()
        {
            return _binaryCache.ClearCacheCompletely();
        }
        
        /// <summary>
        /// Gets the cache directory path
        /// </summary>
        public string GetCacheDirectory()
        {
            return _binaryCache.CacheDirectory;
        }
        
        /// <summary>
        /// Gets cache statistics
        /// </summary>
        public (int hits, int misses, double hitRate, int count) GetCacheStatistics()
        {
            var (hits, misses, hitRate) = _binaryCache.GetStatistics();
            return (hits, misses, hitRate, _binaryCache.Count);
        }

        /// <summary>
        /// Clears all metadata cache (both disk and memory)
        /// </summary>
        public void ClearMetadataCache()
        {
            _binaryCache.ClearCacheCompletely();
        }

        private static class PackageRoles
        {
            public const string Loaded = "Loaded";
            public const string Available = "Available";
            public const string Archived = "Archived";
        }

        private readonly struct PackageVariantDescriptor
        {
            public PackageVariantDescriptor(string packageBase, string role, string status, string path, long fileSize, long lastWriteTicks)
            {
                PackageBase = packageBase;
                Role = role;
                Status = status;
                Path = path;
                FileSize = fileSize;
                LastWriteTicks = lastWriteTicks;
            }

            public string PackageBase { get; }
            public string Role { get; }
            public string Status { get; }
            public string Path { get; }
            public long FileSize { get; }
            public long LastWriteTicks { get; }
        }

        private sealed class PackageVariant
        {
            public PackageVariant(string role, string status, string path, long fileSize, long lastWriteTicks, VarMetadata metadata, int metaHash)
            {
                Role = role;
                Status = status;
                Path = path;
                FileSize = fileSize;
                LastWriteTicks = lastWriteTicks;
                Metadata = metadata;
                MetaHash = metaHash;
            }

            public string Role { get; }
            public string Status { get; }
            public string Path { get; }
            public long FileSize { get; }
            public long LastWriteTicks { get; }
            public VarMetadata Metadata { get; }
            public int MetaHash { get; }
        }

        private sealed class PackageSnapshot
        {
            private readonly Dictionary<string, PackageVariant> _variants = new(StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, PackageVariant> _previousVariants = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _materializedKeys = new();

            public PackageSnapshot(string packageBase)
            {
                PackageBase = packageBase;
            }

            public string PackageBase { get; }
            public PackageVariant PreferredVariant { get; private set; }

            private List<PackageVariant> _orderedVariants = new();

            public IEnumerable<PackageVariant> PreviousVariants => _previousVariants.Values;

            public void BeginRebuild(Dictionary<string, VarMetadata> metadataStore)
            {
                RemoveMaterializedKeys(metadataStore);
                _previousVariants = new Dictionary<string, PackageVariant>(_variants, StringComparer.OrdinalIgnoreCase);
                _variants.Clear();
                _orderedVariants.Clear();
                PreferredVariant = null;
            }

            public void AddOrUpdateVariant(PackageVariant variant)
            {
                _variants[PackageManager.NormalizePath(variant.Path)] = variant;
            }

            public bool TryGetPreviousVariant(string path, out PackageVariant variant)
            {
                return _previousVariants.TryGetValue(PackageManager.NormalizePath(path), out variant);
            }

            public bool RemoveVariantByPath(string path)
            {
                return _variants.Remove(PackageManager.NormalizePath(path));
            }

            public void FinalizeVariants()
            {
                _orderedVariants = _variants.Values
                    .OrderBy(v => v.Metadata.IsOptimized ? 0 : 1)
                    .ThenBy(v => PackageManager.GetRolePriority(v.Role))
                    .ThenBy(v => v.Path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(v => v.LastWriteTicks)
                    .ToList();

                var activeVariants = _orderedVariants
                    .Where(v => !string.Equals(v.Role, PackageRoles.Archived, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                int activeCount = activeVariants.Count;

                foreach (var variant in _orderedVariants)
                {
                    var metadata = variant.Metadata;
                    metadata.VariantRole = variant.Role;
                    metadata.FilePath = variant.Path;

                    if (string.Equals(variant.Role, PackageRoles.Archived, StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.IsDuplicate = false;
                        metadata.DuplicateLocationCount = Math.Max(1, activeCount);
                        metadata.Status = PackageRoles.Archived;
                        continue;
                    }

                    if (activeCount > 1)
                    {
                        metadata.IsDuplicate = true;
                        metadata.DuplicateLocationCount = activeCount;
                        metadata.Status = "Duplicate";
                    }
                    else
                    {
                        metadata.IsDuplicate = false;
                        metadata.DuplicateLocationCount = 1;
                        metadata.Status = variant.Status;
                    }
                }

                PreferredVariant = _orderedVariants.FirstOrDefault();
            }

            public void Materialize(Dictionary<string, VarMetadata> metadataStore)
            {
                RemoveMaterializedKeys(metadataStore);

                if (PreferredVariant == null)
                {
                    return;
                }

                var canonicalKey = PackageBase;
                metadataStore[canonicalKey] = CloneForStorage(PreferredVariant.Metadata);
                _materializedKeys.Add(canonicalKey);

                var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var variant in _orderedVariants)
                {
                    if (variant == PreferredVariant)
                        continue;

                    var key = BuildVariantKey(variant.Role, counters);
                    metadataStore[key] = CloneForStorage(variant.Metadata);
                    _materializedKeys.Add(key);
                }
            }

            public void RemoveMaterializedKeys(Dictionary<string, VarMetadata> metadataStore)
            {
                if (_materializedKeys.Count == 0)
                    return;

                foreach (var key in _materializedKeys)
                {
                    metadataStore.Remove(key);
                }

                _materializedKeys.Clear();
            }

            private VarMetadata CloneForStorage(VarMetadata source)
            {
                var clone = CloneMetadata(source);
                clone.VariantRole = source.VariantRole;
                clone.Status = source.Status;
                clone.FilePath = source.FilePath;
                clone.IsDuplicate = source.IsDuplicate;
                clone.DuplicateLocationCount = source.DuplicateLocationCount;
                clone.IsOptimized = source.IsOptimized;
                clone.HasTextureOptimization = source.HasTextureOptimization;
                clone.HasHairOptimization = source.HasHairOptimization;
                clone.HasMirrorOptimization = source.HasMirrorOptimization;
                clone.MorphCount = source.MorphCount;
                clone.HairCount = source.HairCount;
                clone.ClothingCount = source.ClothingCount;
                clone.SceneCount = source.SceneCount;
                clone.LooksCount = source.LooksCount;
                clone.PosesCount = source.PosesCount;
                clone.AssetsCount = source.AssetsCount;
                clone.ScriptsCount = source.ScriptsCount;
                clone.PluginsCount = source.PluginsCount;
                clone.SubScenesCount = source.SubScenesCount;
                clone.SkinsCount = source.SkinsCount;
                
                return clone;
            }

            private string BuildVariantKey(string role, Dictionary<string, int> counters)
            {
                var roleKey = role ?? string.Empty;
                var count = counters.TryGetValue(roleKey, out var existing) ? existing : 0;
                counters[roleKey] = count + 1;

                if (string.Equals(role, PackageRoles.Available, StringComparison.OrdinalIgnoreCase))
                {
                    return count == 0 ? $"{PackageBase}#available" : $"{PackageBase}#available{count + 1}";
                }

                if (string.Equals(role, PackageRoles.Archived, StringComparison.OrdinalIgnoreCase))
                {
                    return count == 0 ? $"{PackageBase}#archived" : $"{PackageBase}#archived{count + 1}";
                }

                if (string.Equals(role, PackageRoles.Loaded, StringComparison.OrdinalIgnoreCase))
                {
                    return count == 0 ? $"{PackageBase}#loaded" : $"{PackageBase}#loaded{count + 1}";
                }

                return count == 0 ? $"{PackageBase}#variant" : $"{PackageBase}#variant{count + 1}";
            }

        }

        public (string creator, string packageName, string version) ParseFilename(string filename)
        {
            var match = _varPattern.Match(filename);
            if (match.Success)
            {
                return (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
            }

            // Fallback for edge cases
            if (filename.ToLower().EndsWith(".var"))
            {
                var parts = filename[..^4].Split('.');
                if (parts.Length >= 3)
                {
                    if (int.TryParse(parts[^1], out _))
                    {
                        return (parts[0], string.Join(".", parts[1..^1]), parts[^1]);
                    }
                }
            }

            return (null, null, null);
        }

        private List<PackageVariantDescriptor> BuildVariantDescriptors(List<string> installedFiles, List<string> availableFiles)
        {
            var descriptors = new List<PackageVariantDescriptor>();

            void AddDescriptor(string filePath, string role, string status)
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return;
                }

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists)
                    {
                        return;
                    }

                    var packageBase = Path.GetFileNameWithoutExtension(fileInfo.Name);
                    var descriptor = new PackageVariantDescriptor(
                        packageBase,
                        role,
                        status,
                        filePath,
                        fileInfo.Length,
                        fileInfo.LastWriteTimeUtc.Ticks);

                    descriptors.Add(descriptor);
                }
                catch
                {
                    // Ignore inaccessible files
                }
            }

            if (availableFiles != null)
            {
                foreach (var file in availableFiles)
                {
                    var role = IsArchivedPath(file) ? PackageRoles.Archived : PackageRoles.Available;
                    var status = role == PackageRoles.Archived ? PackageRoles.Archived : "Available";
                    AddDescriptor(file, role, status);
                }
            }

            if (installedFiles != null)
            {
                foreach (var file in installedFiles)
                {
                    AddDescriptor(file, PackageRoles.Loaded, "Loaded");
                }
            }

            return descriptors
                .OrderBy(d => d.PackageBase, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => GetRolePriority(d.Role))
                .ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.LastWriteTicks)
                .ToList();
        }


        private (VarMetadata metadata, int metaHash) ParseVarMetadata(string varPath)
        {
            if (string.IsNullOrEmpty(varPath))
            {
                throw new ArgumentNullException(nameof(varPath));
            }

            var filename = Path.GetFileName(varPath);
            var packageName = Path.GetFileNameWithoutExtension(filename);
            
            var metadata = new VarMetadata
            {
                Filename = filename,
                FilePath = varPath,
                Dependencies = new List<string>(),
                ContentList = new List<string>(),
                Categories = new HashSet<string>(),
                UserTags = new List<string>()
            };

            try
            {
                if (!File.Exists(varPath))
                {
                    throw new FileNotFoundException($"Package file not found: {varPath}");
                }

                var fileInfo = new FileInfo(varPath);
                metadata.FileSize = fileInfo.Length;
                metadata.CreatedDate = fileInfo.CreationTime;
                metadata.ModifiedDate = fileInfo.LastWriteTime;
                
                // Try to get from binary cache first (5-10x faster than parsing)
                // Use full filename as cache key to handle multiple versions of the same package
                var cachedMetadata = _binaryCache.TryGetCached(filename, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
                if (cachedMetadata != null)
                {
                    // Cache hit! Only check for morph pack detection if this might be a morph pack
                    var isPotentialMorphPack = cachedMetadata.Categories.Contains("Morphs") || 
                                              filename.ToLower().Contains("morph") || 
                                              filename.ToLower().Contains("expression");
                    
                    if (isPotentialMorphPack)
                    {
                        // Re-run morph pack detection on cached packages that might be morph packs
                        var newCategories = DetectCategoriesFromContent(cachedMetadata.ContentList);
                        
                        // Only update if we detected a morph pack (not Unknown or unchanged)
                        if (newCategories.Count == 1 && newCategories.Contains("Morph Pack"))
                        {
                            cachedMetadata.Categories = newCategories;
                        }
                    }
                    
                    // Run integrity validation even on cached packages
                    try
                    {
                        var integrityResult = _integrityScanner.ValidateMetadata(cachedMetadata);
                        cachedMetadata.IsDamaged = integrityResult.IsDamaged;
                        cachedMetadata.DamageReason = integrityResult.DamageReason;
                    }
                    catch
                    {
                        // Don't fail if integrity check fails
                    }
                    
                    cachedMetadata.FilePath = varPath;
                    cachedMetadata.Filename = filename;
                    return (cachedMetadata, cachedMetadata.GetHashCode());
                }

                // Will try to get actual creation date from preview images inside the .var
                DateTime? previewImageDate = null;

            // Use standard .NET ZipFile for reliable reading
            using var archive = System.IO.Compression.ZipFile.OpenRead(varPath);
                
                string metaJsonContent = null;
                int metaJsonHash = 0;
                var contentList = new List<string>();
                ZipArchiveEntry metaEntry = null;
                
                // COMPLETE ARCHIVE SCAN: enumerate ALL entries and build comprehensive content list
                // This bypasses meta.json contentList to ensure accurate detection
                int entryCount = 0;
                foreach (var entry in archive.Entries)
                {
                    entryCount++;
                    
                    // Look for meta.json (case-insensitive)
                    if (metaEntry == null && 
                        entry.Name.Length == 9 && // "meta.json" length check (fast)
                        entry.Name.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                    {
                        metaEntry = entry;
                        // Don't break - we need to scan all entries
                    }
                    
                    // Build comprehensive content list from ALL relevant files
                    // Skip directories and irrelevant files for performance
                    if (!entry.FullName.EndsWith("/") && IsRelevantContent(entry.FullName))
                    {
                        contentList.Add(entry.FullName);
                    }
                }
                
                metadata.FileCount = entryCount;
                
                // Read meta.json if found (for metadata like creator, description, etc.)
                // But we'll use our scanned contentList instead of meta.json's contentList
                if (metaEntry != null)
                {
                    try
                    {
                        using var stream = metaEntry.Open();
                        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
                        metaJsonContent = reader.ReadToEnd();
                        
                        // Calculate hash of meta.json content for cache validation
                        metaJsonHash = metaJsonContent.GetHashCode();
                        
                        // Use meta.json LastWriteTime as the creation date
                        previewImageDate = metaEntry.LastWriteTime.DateTime;
                        
                        // Keep our scanned contentList instead of using meta.json's contentList
                    }
                    catch
                    {
                        // Ignore meta.json read errors
                    }
                }
                
                metadata.ContentList = contentList;


                // Parse meta.json if found
                if (!string.IsNullOrEmpty(metaJsonContent))
                {
                    ParseMetaJsonContent(metadata, metaJsonContent);
                }
                else
                {
                    // No meta.json found, using filename fallback
                }

                // Fallback to filename parsing if meta.json data is missing
                // ALWAYS parse version from filename as it's the authoritative source
                var (creator, pkgName, version) = ParseFilename(filename);
                
                if (string.IsNullOrEmpty(metadata.CreatorName))
                    metadata.CreatorName = creator ?? "Unknown";
                if (string.IsNullOrEmpty(metadata.PackageName))
                    metadata.PackageName = pkgName ?? filename;
                
                // Always use filename version as it's the VAM standard (overrides meta.json packageVersion)
                if (!string.IsNullOrEmpty(version) && int.TryParse(version, out var versionInt))
                {
                    metadata.Version = versionInt;
                }

                // Detect categories and apply fallbacks
                ApplyCategoryDetectionAndFallbacks(metadata, filename);
                
                // Date priority:
                // 1. vpmOriginalDate (if package was optimized) - already set in ParseMetaJsonContent
                // 2. meta.json LastWriteTime (if meta.json exists)
                // 3. File system modified date (fallback)
                
                // Only override if we have meta.json date AND no vpmOriginalDate was set
                if (previewImageDate.HasValue && metadata.ModifiedDate == fileInfo.LastWriteTime)
                {
                    // vpmOriginalDate wasn't set, so use meta.json date
                    metadata.ModifiedDate = previewImageDate.Value;
                    metadata.CreatedDate = previewImageDate.Value;
                }
                
                // Validate metadata for integrity issues (lightweight check using already-parsed data)
                try
                {
                    var integrityResult = _integrityScanner.ValidateMetadata(metadata);
                    metadata.IsDamaged = integrityResult.IsDamaged;
                    metadata.DamageReason = integrityResult.DamageReason;
                }
                catch
                {
                    // Don't fail the whole parse if integrity check fails
                }
                
                // Add to binary cache for faster future loads
                // Use full filename as cache key to handle multiple versions of the same package
                _binaryCache.AddOrUpdate(filename, metadata, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
                
                return (metadata, metaJsonHash);
            }
            catch (Exception)
            {
                metadata.IsCorrupted = true;
                metadata.IsDamaged = true;
                metadata.DamageReason = "Failed to read package file";
                
                // Try to extract basic info from filename even if corrupted
                var (creator, pkgName, version) = ParseFilename(filename);
                metadata.CreatorName = creator ?? "Unknown";
                metadata.PackageName = pkgName ?? filename;
                if (int.TryParse(version, out var versionInt))
                    metadata.Version = versionInt;
                metadata.Categories.Add("Unknown");
                
                return (metadata, 0); // Return 0 hash for corrupted packages
            }
        }

        // Backward compatibility wrapper
        public VarMetadata ParseVarMetadataComplete(string varPath)
        {
            return ParseVarMetadata(varPath).metadata;
        }

        private HashSet<string> DetectCategoriesFromContent(List<string> contentList)
        {
            var categories = new HashSet<string>();
            
            if (contentList == null || contentList.Count == 0)
            {
                categories.Add("Unknown");
                return categories;
            }
            
            // Check if this is a morph asset (only contains morphs) and count morphs
            var (isMorphAsset, morphCount) = DetectMorphAsset(contentList);
            if (isMorphAsset)
            {
                // Mark as "Morph Pack" only if it has 10 or more morphs
                if (morphCount >= 10)
                {
                    categories.Add("Morph Pack");
                }
                else
                {
                    categories.Add("Morphs");
                }
                return categories;
            }
            
            foreach (var content in contentList.Take(100))
            {
                var category = DetectCategoryFromPath(content);
                if (!string.IsNullOrEmpty(category) && category != "Unknown")
                {
                    categories.Add(category);
                }
            }
            
            if (categories.Count == 0)
                categories.Add("Unknown");
                
            return categories;
        }

        private (bool isMorphAsset, int morphCount) DetectMorphAsset(List<string> contentList)
        {
            if (contentList == null || contentList.Count == 0)
                return (false, 0);
            
            int morphCount = 0;
            bool hasNonMorphContent = false;
            var nonMorphFiles = new List<string>();
            
            foreach (var content in contentList)
            {
                if (string.IsNullOrEmpty(content))
                    continue;
                    
                var normalizedPath = content.Replace('\\', '/').ToLowerInvariant();
                
                // Skip directory entries
                if (normalizedPath.EndsWith("/"))
                    continue;
                
                // Check if this is a morph file
                if (normalizedPath.Contains("custom/atom/person/morphs") && 
                    (normalizedPath.EndsWith(".vmi") || normalizedPath.EndsWith(".vmb") || normalizedPath.EndsWith(".dsf")))
                {
                    morphCount++;
                }
                else if (!normalizedPath.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                {
                    // If it's not meta.json and not a morph file, it's not a morph-only asset
                    hasNonMorphContent = true;
                    nonMorphFiles.Add(content);
                }
            }
            
            // It's a morph asset only if it has morphs and no other content
            bool isMorphAsset = morphCount > 0 && !hasNonMorphContent;
            
            return (isMorphAsset, morphCount);
        }

        private string DetectCategoryFromPath(string path)
        {
            var normalizedPath = path.Replace('\\', '/').ToLowerInvariant();
            
            // More comprehensive pattern matching
            if (normalizedPath.Contains("saves/scene") || normalizedPath.Contains(".scene."))
                return "Scenes";
            if (normalizedPath.Contains("custom/atom/person/morphs") || normalizedPath.Contains(".morph."))
                return "Morphs";
            if (normalizedPath.Contains("custom/atom/person/pose") || normalizedPath.Contains(".pose."))
                return "Poses";
            if (normalizedPath.Contains("custom/clothing") || normalizedPath.Contains("custom/atom/person/clothing") || normalizedPath.Contains(".clothing."))
                return "Clothing";
            if (normalizedPath.Contains("custom/hair") || normalizedPath.Contains("custom/atom/person/hair") || normalizedPath.Contains(".hair."))
                return "Hair";
            if (normalizedPath.Contains("custom/atom/person/appearance") || normalizedPath.Contains(".look.") || normalizedPath.Contains("/looks/"))
                return "Looks";
            if (normalizedPath.Contains("custom/assets") || normalizedPath.Contains(".assetbundle"))
                return "Assets";
            if (normalizedPath.Contains("custom/scripts") || normalizedPath.Contains(".cs") || normalizedPath.Contains(".cslist"))
                return "Scripts";
            if (normalizedPath.Contains("custom/atom/person/plugins") || normalizedPath.Contains("/plugins/"))
                return "Plugins";
            if (normalizedPath.Contains("custom/subscene") || normalizedPath.Contains(".json") && normalizedPath.Contains("subscene"))
                return "SubScene";
            if (normalizedPath.Contains("custom/atom/person/skin") || normalizedPath.Contains("/skin/"))
                return "Skin";
            if (normalizedPath.Contains("custom/atom/person/textures") || normalizedPath.Contains("/textures/"))
                return "Textures";

            return "Unknown";
        }

        /// <summary>
        /// Checks if a file path is relevant content we want to track (clothing, hair, morphs, etc.)
        /// Filters out irrelevant files like readme, licenses, temp files, etc.
        /// </summary>
        private bool IsRelevantContent(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            
            var normalizedPath = path.Replace('\\', '/').ToLowerInvariant();
            
            // Exclude obvious non-content files for performance
            if (normalizedPath.EndsWith("meta.json") || 
                normalizedPath.Contains("readme") || 
                normalizedPath.Contains("license") ||
                normalizedPath.Contains("/_screenshots/") ||
                normalizedPath.Contains("/.git/"))
                return false;
            
            // Include files in VAM content directories
            // Using Contains instead of StartsWith to be more permissive
            return normalizedPath.Contains("custom/clothing/") ||
                   normalizedPath.Contains("custom/hair/") ||
                   normalizedPath.Contains("custom/atom/") ||
                   normalizedPath.Contains("custom/assets/") ||
                   normalizedPath.Contains("custom/scripts/") ||
                   normalizedPath.Contains("custom/subscenes/") ||
                   normalizedPath.Contains("saves/scene/") ||
                   normalizedPath.Contains("addonpackages/");
        }

        private (int morphs, int hair, int clothing, int scenes, int looks, int poses, int assets, int scripts, int plugins, int subScenes, int skins, List<string> expandedList) CountContentItems(List<string> contentList)
        {
            if (contentList == null || contentList.Count == 0)
            {
                return (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new List<string>());
            }
            
            
            var processedAssetFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processedPluginFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            // Since we now perform complete archive scanning, contentList already contains
            // all individual files (no directories). We can use it directly for counting and UI.
            var allFilesForUI = new List<string>(contentList);
            
            int morphCount = 0;
            int hairCount = 0;
            int clothingCount = 0;
            int sceneCount = 0;
            int looksCount = 0;
            int posesCount = 0;
            int assetsCount = 0;
            int scriptsCount = 0;
            int pluginsCount = 0;
            int subScenesCount = 0;
            int skinsCount = 0;
            
            foreach (var content in contentList)
            {
                if (string.IsNullOrEmpty(content))
                    continue;
                    
                var normalizedPath = content.Replace('\\', '/').ToLowerInvariant();
                
                if (normalizedPath.EndsWith("/"))
                {
                    continue;
                }
                
                if (normalizedPath.Contains("custom/atom/person/morphs") && 
                    (normalizedPath.EndsWith(".vmi") || normalizedPath.EndsWith(".vmb") || normalizedPath.EndsWith(".dsf")))
                {
                    morphCount++;
                }
                else if ((normalizedPath.Contains("custom/hair") || normalizedPath.Contains("custom/atom/person/hair")) && 
                         (normalizedPath.EndsWith(".vam") || normalizedPath.EndsWith(".vab")))
                {
                    // Count .vam (presets) and .vab (geometry) files as hair items
                    hairCount++;
                }
                else if ((normalizedPath.Contains("custom/clothing") || normalizedPath.Contains("custom/atom/person/clothing")) && 
                         (normalizedPath.EndsWith(".vap") || normalizedPath.EndsWith(".vab")))
                {
                    // Count .vap (presets) and .vab (geometry) files as clothing items
                    clothingCount++;
                }
                else if (normalizedPath.Contains("saves/scene") && normalizedPath.EndsWith(".json"))
                {
                    sceneCount++;
                }
                else if (normalizedPath.Contains("custom/atom/person/appearance") && 
                         (normalizedPath.EndsWith(".vap") || normalizedPath.EndsWith(".json")))
                {
                    looksCount++;
                }
                else if (normalizedPath.Contains("custom/atom/person/pose") && normalizedPath.EndsWith(".json"))
                {
                    posesCount++;
                }
                else if (normalizedPath.Contains("custom/assets") && normalizedPath.EndsWith(".assetbundle"))
                {
                    var folderPath = normalizedPath.Substring(0, normalizedPath.LastIndexOf('/'));
                    if (processedAssetFolders.Add(folderPath))
                    {
                        assetsCount++;
                    }
                }
                else if (normalizedPath.Contains("custom/scripts") && 
                         (normalizedPath.EndsWith(".cs") || normalizedPath.EndsWith(".cslist")))
                {
                    scriptsCount++;
                }
                else if (normalizedPath.Contains("custom/atom/person/plugins") && normalizedPath.EndsWith(".cs"))
                {
                    var folderPath = normalizedPath.Substring(0, normalizedPath.LastIndexOf('/'));
                    if (processedPluginFolders.Add(folderPath))
                    {
                        pluginsCount++;
                    }
                }
                else if (normalizedPath.Contains("custom/subscene") && normalizedPath.EndsWith(".json"))
                {
                    subScenesCount++;
                }
                else if ((normalizedPath.Contains("custom/atom/person/skin") || normalizedPath.Contains("custom/atom/person/textures")) && 
                         (normalizedPath.EndsWith(".jpg") || normalizedPath.EndsWith(".png") || normalizedPath.EndsWith(".vmi")))
                {
                    skinsCount++;
                }
            }
            
            // Return the expanded file list for UI display
            // This will be stored in metadata.AllFiles so the UI can show all individual files
            return (morphCount, hairCount, clothingCount, sceneCount, looksCount, posesCount, assetsCount, scriptsCount, pluginsCount, subScenesCount, skinsCount, allFilesForUI);
        }

        public VarScanResult ScanSingleVarOptimized(string varPath, bool indexAllFiles = false)
        {
            return _varScanner.ScanVarFile(varPath, indexAllFiles);
        }

        public LazyZipArchive OpenVarLazy(string varPath)
        {
            return _varScanner.OpenLazy(varPath);
        }

        public (long scanned, long skipped, long indexed, double skipPercentage) GetScannerStatistics()
        {
            return _varScanner.GetStatistics();
        }

        public void ResetScannerStatistics()
        {
            _varScanner.ResetStatistics();
        }

        public async Task<Dictionary<string, VarScanResult>> ScanVarsBatchOptimizedAsync(
            IEnumerable<string> varPaths, 
            bool indexAllFiles = false,
            IProgress<int> progress = null)
        {
            var results = new ConcurrentDictionary<string, VarScanResult>(StringComparer.OrdinalIgnoreCase);
            var pathsList = varPaths.ToList();
            var completed = 0;

            await Task.Run(() =>
            {
                Parallel.ForEach(pathsList, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    varPath =>
                    {
                        try
                        {
                            var scanResult = _varScanner.ScanVarFile(varPath, indexAllFiles);
                            results[varPath] = scanResult;
                        }
                        catch (Exception ex)
                        {
                            results[varPath] = new VarScanResult
                            {
                                VarPath = varPath,
                                Success = false,
                                ErrorMessage = ex.Message
                            };
                        }

                        var current = Interlocked.Increment(ref completed);
                        progress?.Report(current);
                    });
            });

            return results.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<(List<string> installed, List<string> available)> ScanVarFilesAsync(string installedFolder, string allPackagesFolder)
        {
            var installed = new List<string>();
            var available = new List<string>();

            // Use parallel scanning for better performance
            var scanTasks = new List<Task>();

            // Scan installed folder (AddonPackages) - including subfolders
            if (Directory.Exists(installedFolder))
            {
                scanTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var files = Directory.EnumerateFiles(installedFolder, "*.var", SearchOption.AllDirectories);
                        lock (installed)
                        {
                            installed.AddRange(files);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }));
            }

            // Scan AllPackages folder (available packages) - including subfolders
            if (Directory.Exists(allPackagesFolder))
            {
                scanTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var files = Directory.EnumerateFiles(allPackagesFolder, "*.var", SearchOption.AllDirectories);
                        lock (available)
                        {
                            available.AddRange(files);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }));
            }

            // Scan ArchivedPackages folder - including subfolders
            // Get root folder from installedFolder path
            string rootFolder = Path.GetDirectoryName(installedFolder);
            string archivedPackagesFolder = Path.Combine(rootFolder, "ArchivedPackages");
            if (Directory.Exists(archivedPackagesFolder))
            {
                scanTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var files = Directory.EnumerateFiles(archivedPackagesFolder, "*.var", SearchOption.AllDirectories);
                        // Add archived packages to available list with special marker
                        lock (available)
                        {
                            available.AddRange(files);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }));
            }

            // Wait for all scanning tasks to complete
            await Task.WhenAll(scanTasks);

            return (installed, available);
        }

        private static VarMetadata CloneMetadata(VarMetadata source)
        {
            if (source == null)
            {
                return null;
            }

            return new VarMetadata
            {
                Filename = source.Filename,
                PackageName = source.PackageName,
                CreatorName = source.CreatorName,
                Description = source.Description,
                Version = source.Version,
                LicenseType = source.LicenseType,
                Dependencies = source.Dependencies != null ? new List<string>(source.Dependencies) : new List<string>(),
                ContentList = source.ContentList != null ? new List<string>(source.ContentList) : new List<string>(),
                ContentTypes = source.ContentTypes != null ? new HashSet<string>(source.ContentTypes) : new HashSet<string>(),
                Categories = source.Categories != null ? new HashSet<string>(source.Categories) : new HashSet<string>(),
                FileCount = source.FileCount,
                CreatedDate = source.CreatedDate,
                ModifiedDate = source.ModifiedDate,
                UserTags = source.UserTags != null ? new List<string>(source.UserTags) : new List<string>(),
                IsCorrupted = source.IsCorrupted,
                PreloadMorphs = source.PreloadMorphs,
                Status = source.Status,
                FilePath = source.FilePath,
                FileSize = source.FileSize,
                IsOptimized = source.IsOptimized,
                HasTextureOptimization = source.HasTextureOptimization,
                HasHairOptimization = source.HasHairOptimization,
                HasMirrorOptimization = source.HasMirrorOptimization,
                IsDuplicate = source.IsDuplicate,
                DuplicateLocationCount = source.DuplicateLocationCount,
                IsOldVersion = source.IsOldVersion,
                LatestVersionNumber = source.LatestVersionNumber,
                PackageBaseName = source.PackageBaseName,
                MorphCount = source.MorphCount,
                HairCount = source.HairCount,
                ClothingCount = source.ClothingCount,
                SceneCount = source.SceneCount,
                LooksCount = source.LooksCount,
                PosesCount = source.PosesCount,
                AssetsCount = source.AssetsCount,
                ScriptsCount = source.ScriptsCount,
                PluginsCount = source.PluginsCount,
                SubScenesCount = source.SubScenesCount,
                SkinsCount = source.SkinsCount,
                IsMorphAsset = source.IsMorphAsset,
                AllFiles = source.AllFiles != null ? new List<string>(source.AllFiles) : new List<string>()
            };
        }

        public void UpdatePackageMappingFast(List<string> installedFiles, List<string> availableFiles, IProgress<(int current, int total)> progress = null)
        {
            PackageMetadata.Clear();
            
            // Force garbage collection to free memory before loading new packages
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var descriptors = BuildVariantDescriptors(installedFiles, availableFiles);
            var totalDescriptors = descriptors.Count;

            var processed = 0;
            var snapshotInitialized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activePackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var descriptor in descriptors)
            {
                var snapshot = _snapshotCache.GetOrAdd(descriptor.PackageBase, key => new PackageSnapshot(key));

                if (snapshotInitialized.Add(descriptor.PackageBase))
                {
                    snapshot.BeginRebuild(PackageMetadata);
                }

                var normalizedPath = NormalizePath(descriptor.Path);
                PackageVariant variant;

                if (snapshot.TryGetPreviousVariant(descriptor.Path, out var previousVariant) &&
                    previousVariant.FileSize == descriptor.FileSize &&
                    previousVariant.LastWriteTicks == descriptor.LastWriteTicks &&
                    previousVariant.MetaHash != 0)
                {
                    var metadataClone = CloneMetadata(previousVariant.Metadata);
                    metadataClone.Status = descriptor.Status;
                    metadataClone.VariantRole = descriptor.Role;
                    metadataClone.FilePath = descriptor.Path;
                    metadataClone.FileSize = descriptor.FileSize;
                    // Don't overwrite ModifiedDate if package is optimized (it has vpmOriginalDate)
                    if (!metadataClone.IsOptimized)
                    {
                        metadataClone.ModifiedDate = ConvertUtcTicksToLocal(descriptor.LastWriteTicks);
                    }

                    variant = new PackageVariant(descriptor.Role, descriptor.Status, descriptor.Path, descriptor.FileSize, descriptor.LastWriteTicks, metadataClone, previousVariant.MetaHash);
                }
                else
                {
                    var (metadata, metaHash) = ParseVarMetadata(descriptor.Path);
                    metadata.Status = descriptor.Status;
                    metadata.VariantRole = descriptor.Role;
                    metadata.FilePath = descriptor.Path;
                    metadata.FileSize = descriptor.FileSize;
                    // Don't overwrite ModifiedDate if package is optimized (it has vpmOriginalDate)
                    if (!metadata.IsOptimized)
                    {
                        metadata.ModifiedDate = ConvertUtcTicksToLocal(descriptor.LastWriteTicks);
                    }

                    variant = new PackageVariant(descriptor.Role, descriptor.Status, descriptor.Path, descriptor.FileSize, descriptor.LastWriteTicks, metadata, metaHash);
                }

                snapshot.AddOrUpdateVariant(variant);
                activePackages.Add(descriptor.PackageBase);

                processed++;
                if (progress != null && processed % 500 == 0)
                {
                    progress.Report((current: processed, total: totalDescriptors));
                }
            }

            int materializedCount = 0;
            var inactivePackages = new List<string>();
            
            foreach (var kvp in _snapshotCache)
            {
                var packageBase = kvp.Key;
                var snapshot = kvp.Value;

                if (!activePackages.Contains(packageBase))
                {
                    inactivePackages.Add(packageBase);
                    continue;
                }

                snapshot.FinalizeVariants();
                snapshot.Materialize(PackageMetadata);
                
                // Periodic garbage collection to prevent memory buildup during materialization
                materializedCount++;
                if (materializedCount % 1000 == 0)
                {
                    GC.Collect(0, GCCollectionMode.Optimized);
                }
            }
            
            // Remove inactive packages after iteration
            foreach (var packageBase in inactivePackages)
            {
                if (_snapshotCache.TryRemove(packageBase, out var snapshot))
                {
                    snapshot.RemoveMaterializedKeys(PackageMetadata);
                }
                PreviewImageIndex.TryRemove(packageBase, out _);
            }
            
            // Force garbage collection after materializing all packages
            GC.Collect();
            GC.WaitForPendingFinalizers();

            int optimizedCount = PackageMetadata.Values.Count(m => m.IsOptimized);
            
            // Detect old versions after all packages are loaded
            DetectOldVersions();
            
            // Save binary cache after scanning completes
            SaveBinaryCache();
            
        }

        /// <summary>
        /// Detects old versions of packages by comparing version numbers.
        /// A package is considered old if there's a newer version with the same creator and package name.
        /// </summary>
        public void DetectOldVersions()
        {
            var packageGroups = new Dictionary<string, List<VarMetadata>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var kvp in PackageMetadata)
            {
                var metadata = kvp.Value;
                
                if (metadata.IsCorrupted)
                    continue;
                
                var packageKey = $"{metadata.CreatorName}.{metadata.PackageName}";
                metadata.PackageBaseName = packageKey;
                
                if (!packageGroups.ContainsKey(packageKey))
                {
                    packageGroups[packageKey] = new List<VarMetadata>();
                }
                
                packageGroups[packageKey].Add(metadata);
            }
            
            int totalOldVersions = 0;
            foreach (var group in packageGroups.Values)
            {
                if (group.Count <= 1)
                {
                    foreach (var metadata in group)
                    {
                        metadata.IsOldVersion = false;
                        metadata.LatestVersionNumber = metadata.Version;
                    }
                    continue;
                }
                
                var latestVersion = group.Max(m => m.Version);
                var packageBaseName = group[0].PackageBaseName;
                
                // Mark old versions metadata in group
                foreach (var metadata in group)
                {
                    metadata.LatestVersionNumber = latestVersion;
                    metadata.IsOldVersion = metadata.Version < latestVersion;
                    if (metadata.IsOldVersion)
                        totalOldVersions++;
                }
            }
        }

        /// <summary>
        /// Gets all old version packages that can be archived.
        /// Returns packages that are not already in ArchivedPackages folder.
        /// </summary>
        public List<VarMetadata> GetOldVersionPackages()
        {
            return PackageMetadata.Values
                .Where(m => m.IsOldVersion && !IsArchivedPath(m.FilePath))
                .OrderBy(m => m.CreatorName)
                .ThenBy(m => m.PackageName)
                .ThenBy(m => m.Version)
                .ToList();
        }

        /// <summary>
        /// Public method to index preview images for a specific package.
        /// Used when adding newly downloaded packages.
        /// </summary>
        public void IndexPreviewImagesForPackage(string varPath, string packageBase)
        {
            var metadataKey = packageBase;
            EnsurePreviewImagesIndexed(varPath, packageBase, metadataKey);
        }
        
        /// <summary>
        /// Invalidates all caches for a specific package.
        /// Call this after modifying a package file (e.g., optimization).
        /// </summary>
        public void InvalidatePackageCache(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                return;
            }

            var baseName = GetBasePackageName(packageName);

            if (_snapshotCache.TryRemove(baseName, out var snapshot))
            {
                snapshot.RemoveMaterializedKeys(PackageMetadata);
            }

            var keysToRemove = PackageMetadata.Keys
                .Where(k => IsSameBasePackage(k, baseName))
                .ToList();

            foreach (var key in keysToRemove)
            {
                PackageMetadata.Remove(key);
            }

            PreviewImageIndex.TryRemove(baseName, out _);
            
            // Also remove from binary cache
            _binaryCache.Remove(baseName);
        }

        /// <summary>
        /// Updates the cache with fresh metadata for a specific package.
        /// Call this after re-parsing a modified package to ensure cache consistency.
        /// </summary>
        public void UpdatePackageCache(string packageName, VarMetadata metadata, string filePath)
        {
            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                return;
            }

            var baseName = GetBasePackageName(packageName);
            var role = DetermineRoleFromKey(packageName);
            var status = metadata?.Status ?? (role == PackageRoles.Archived ? PackageRoles.Archived : role);

            var snapshot = _snapshotCache.GetOrAdd(baseName, key => new PackageSnapshot(key));
            snapshot.BeginRebuild(PackageMetadata);

            var normalizedPath = NormalizePath(filePath);

            foreach (var previous in snapshot.PreviousVariants)
            {
                if (string.Equals(NormalizePath(previous.Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var previousClone = CloneMetadata(previous.Metadata);
                snapshot.AddOrUpdateVariant(new PackageVariant(
                    previous.Role,
                    previous.Status,
                    previous.Path,
                    previous.FileSize,
                    previous.LastWriteTicks,
                    previousClone,
                    previous.MetaHash));
            }

            var (freshMetadata, metaHash) = ParseVarMetadata(filePath);
            freshMetadata.Status = status;
            freshMetadata.VariantRole = role;
            freshMetadata.FilePath = filePath;
            freshMetadata.FileSize = fileInfo.Length;
            // Don't overwrite ModifiedDate if package is optimized (it has vpmOriginalDate)
            if (!freshMetadata.IsOptimized)
            {
                freshMetadata.ModifiedDate = fileInfo.LastWriteTime;
            }

            snapshot.AddOrUpdateVariant(new PackageVariant(
                role,
                status,
                filePath,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks,
                freshMetadata,
                metaHash));

            snapshot.FinalizeVariants();
            snapshot.Materialize(PackageMetadata);

            EnsurePreviewImagesIndexed(filePath, baseName, baseName);
        }

        /// <summary>
        /// Scans a VAR archive for preview-worthy images and stores lightweight references.
        /// </summary>
        private void EnsurePreviewImagesIndexed(string varPath, string packageBase, string metadataKey)
        {
            try
            {
                if (PreviewImageIndex.ContainsKey(metadataKey))
                {
                    return;
                }

                IndexPreviewImages(varPath, packageBase, metadataKey);
            }
            catch
            {
            }
        }

        private void IndexPreviewImages(string varPath, string packageBase, string metadataKey)
        {
            try
            {
                var imageLocations = new List<ImageLocation>();
                using var archive = System.IO.Compression.ZipFile.OpenRead(varPath);

                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith("/")) continue; // skip directories
                    var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
                    if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;

                    var pathNorm = entry.FullName.Replace('\\', '/').ToLowerInvariant();
                    if (pathNorm.Contains("/textures/") || pathNorm.Contains("/texture/")) continue;

                    if (entry.Length < 1024 || entry.Length > 1024 * 1024) continue;

                    imageLocations.Add(new ImageLocation
                    {
                        VarFilePath = varPath,
                        InternalPath = entry.FullName,
                        FileSize = entry.Length
                    });
                }

                if (imageLocations.Count > 0)
                {
                    PreviewImageIndex[metadataKey] = imageLocations;
                }
            }
            catch
            {
                // Ignore preview image indexing errors
            }
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
        {
            // Try exact match first (most common case)
            if (element.TryGetProperty(propertyName, out value))
                return true;
            
            // Try case-insensitive search
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            
            value = default;
            return false;
        }

        private void ParseMetaJsonContent(VarMetadata metadata, string metaJsonContent)
        {
            try
            {
                var metaData = JsonSerializer.Deserialize<JsonElement>(metaJsonContent);
                
                // Extract metadata from meta.json
                if (metaData.TryGetProperty("packageName", out var pName))
                    metadata.PackageName = pName.GetString() ?? "";
                if (metaData.TryGetProperty("creatorName", out var cName))
                    metadata.CreatorName = cName.GetString() ?? "";
                if (metaData.TryGetProperty("description", out var desc))
                    metadata.Description = desc.GetString() ?? "";
                if (metaData.TryGetProperty("packageVersion", out var ver))
                    metadata.Version = ver.GetInt32();
                if (metaData.TryGetProperty("licenseType", out var license))
                {
                    var licenseValue = license.GetString() ?? "";
                    metadata.LicenseType = licenseValue;
                }
                // Try to get preloadMorphs from root level or customOptions
                if (metaData.TryGetProperty("preloadMorphs", out var preload))
                {
                    metadata.PreloadMorphs = preload.ValueKind == JsonValueKind.String 
                        ? bool.Parse(preload.GetString() ?? "false") 
                        : preload.GetBoolean();
                }
                else if (metaData.TryGetProperty("customOptions", out var customOpts) && 
                         customOpts.TryGetProperty("preloadMorphs", out var preloadOpt))
                {
                    metadata.PreloadMorphs = preloadOpt.ValueKind == JsonValueKind.String 
                        ? bool.Parse(preloadOpt.GetString() ?? "false") 
                        : preloadOpt.GetBoolean();
                }
                
                // Extract dependencies
                if (metaData.TryGetProperty("dependencies", out var deps))
                {
                    metadata.Dependencies = ParseDependencies(deps);
                }
                
                // Extract tags
                if (metaData.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                {
                    metadata.UserTags = tags.EnumerateArray()
                        .Select(t => t.GetString())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();
                }
                
                // NOTE: We no longer use contentList from meta.json
                // Instead, we perform a complete archive scan to ensure accurate detection
                // The scanned contentList has already been set before this method is called
                
                // Parse VPM optimization flags from description field (VaM-compatible method)
                // VaM doesn't support custom fields in meta.json, so we store flags in description
                ParseVpmFlagsFromDescription(metadata);
                
                // Legacy: Also check for old-style VPM flags in meta.json (for backwards compatibility)
                // These will be removed on next optimization
                bool hasVpmOptimized = TryGetPropertyCaseInsensitive(metaData, "vpmOptimized", out var vpmOpt);
                bool hasVpmTexture = TryGetPropertyCaseInsensitive(metaData, "vpmTextureOptimized", out var vpmTexture);
                bool hasVpmHair = TryGetPropertyCaseInsensitive(metaData, "vpmHairOptimized", out var vpmHair);
                bool hasVpmMirror = TryGetPropertyCaseInsensitive(metaData, "vpmMirrorOptimized", out var vpmMirror);
                bool hasVpmOrigDate = metaData.TryGetProperty("vpmOriginalDate", out var vpmOrigDate);
                
                // Only use legacy flags if description-based flags weren't found
                if (!metadata.IsOptimized && hasVpmOptimized)
                {
                    metadata.IsOptimized = vpmOpt.GetBoolean();
                }
                if (!metadata.HasTextureOptimization && hasVpmTexture)
                {
                    metadata.HasTextureOptimization = vpmTexture.GetBoolean();
                }
                if (!metadata.HasHairOptimization && hasVpmHair)
                {
                    metadata.HasHairOptimization = vpmHair.GetBoolean();
                }
                if (!metadata.HasMirrorOptimization && hasVpmMirror)
                {
                    metadata.HasMirrorOptimization = vpmMirror.GetBoolean();
                }
                
                // Legacy: Extract VPM original date from meta.json if not found in description
                if (metadata.CreatedDate == DateTime.MinValue && hasVpmOrigDate)
                {
                    var dateStr = vpmOrigDate.GetString();
                    if (DateTime.TryParse(dateStr, out var parsedDate))
                    {
                        metadata.CreatedDate = parsedDate;
                        metadata.ModifiedDate = parsedDate;
                    }
                }
            }
            catch (JsonException)
            {
                // Invalid JSON in meta.json, continue with filename fallback
            }
        }

        private void ApplyCategoryDetectionAndFallbacks(VarMetadata metadata, string filename)
        {
            // Detect categories from content list
            metadata.Categories = DetectCategoriesFromContent(metadata.ContentList);
            
            // Detect if this is a morph asset (only contains morphs, including morph packs)
            var (isMorphAsset, morphCount) = DetectMorphAsset(metadata.ContentList);
            metadata.IsMorphAsset = isMorphAsset;
            
            // Count content items
            var contentCounts = CountContentItems(metadata.ContentList);
            metadata.MorphCount = contentCounts.morphs;
            metadata.HairCount = contentCounts.hair;
            metadata.ClothingCount = contentCounts.clothing;
            metadata.SceneCount = contentCounts.scenes;
            metadata.LooksCount = contentCounts.looks;
            metadata.PosesCount = contentCounts.poses;
            metadata.AssetsCount = contentCounts.assets;
            metadata.ScriptsCount = contentCounts.scripts;
            metadata.PluginsCount = contentCounts.plugins;
            metadata.SubScenesCount = contentCounts.subScenes;
            metadata.SkinsCount = contentCounts.skins;
            
            // Store the expanded file list for UI display
            // This contains all individual files from expanded directories
            metadata.AllFiles = contentCounts.expandedList;
            
            
            // Fallback category detection from filename if no categories found
            if (metadata.Categories.Count == 0)
            {
                var lowerName = filename.ToLower();
                
                if (lowerName.Contains("scene")) metadata.Categories.Add("Scenes");
                else if (lowerName.Contains("look") || lowerName.Contains("appearance")) metadata.Categories.Add("Looks");
                else if (lowerName.Contains("clothing")) metadata.Categories.Add("Clothing");
                else if (lowerName.Contains("hair")) metadata.Categories.Add("Hair");
                else if (lowerName.Contains("morph") || lowerName.Contains("morphpack")) metadata.Categories.Add("Morph Pack");
                else if (lowerName.Contains("pose")) metadata.Categories.Add("Poses");
                else metadata.Categories.Add("Unknown");
            }
        }
        private List<string> ParseDependencies(JsonElement deps)
        {
            // Use HashSet for O(1) duplicate detection instead of List with O(n) Contains checks
            var dependenciesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Recursively parse all dependencies including subdependencies
            ParseDependenciesRecursive(deps, dependenciesSet);

            // Convert back to List for compatibility with existing code
            return new List<string>(dependenciesSet);
        }

        /// <summary>
        /// Recursively parses dependencies at all nesting levels
        /// </summary>
        private void ParseDependenciesRecursive(JsonElement deps, HashSet<string> dependenciesSet)
        {
            switch (deps.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var dep in deps.EnumerateArray())
                    {
                        if (dep.ValueKind == JsonValueKind.String)
                        {
                            var depStr = dep.GetString();
                            if (!string.IsNullOrEmpty(depStr))
                                dependenciesSet.Add(depStr); // HashSet automatically handles duplicates
                        }
                        else if (dep.ValueKind == JsonValueKind.Object)
                        {
                            // Extract from object properties with early exit optimization
                            string foundDependency = null;
                            foreach (var prop in dep.EnumerateObject())
                            {
                                // Check for known property names that contain dependency info
                                if (prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                                    prop.Name.Equals("packageName", StringComparison.OrdinalIgnoreCase) ||
                                    prop.Name.Equals("package", StringComparison.OrdinalIgnoreCase))
                                {
                                    foundDependency = prop.Value.GetString();
                                    if (!string.IsNullOrEmpty(foundDependency))
                                    {
                                        dependenciesSet.Add(foundDependency);
                                        break; // Early exit - no need to check remaining properties
                                    }
                                }
                            }
                        }
                    }
                    break;

                case JsonValueKind.String:
                    var singleDep = deps.GetString();
                    if (!string.IsNullOrEmpty(singleDep))
                        dependenciesSet.Add(singleDep);
                    break;

                case JsonValueKind.Object:
                    // Property names are dependency names (common VAM format)
                    foreach (var prop in deps.EnumerateObject())
                    {
                        if (!string.IsNullOrEmpty(prop.Name))
                        {
                            dependenciesSet.Add(prop.Name);
                            
                            // Recursively parse subdependencies
                            if (prop.Value.ValueKind == JsonValueKind.Object &&
                                prop.Value.TryGetProperty("dependencies", out var subDeps))
                            {
                                ParseDependenciesRecursive(subDeps, dependenciesSet);
                            }
                        }
                    }
                    break;
            }
        }



        /// <summary>
        /// Builds the package status index by scanning available package locations
        /// </summary>
        private void BuildPackageStatusIndex()
        {
            lock (_statusIndexLock)
            {
                _packageStatusIndex.Clear();
                
                // Load package statuses from metadata cache
                foreach (var entry in PackageMetadata)
                {
                    if (!string.IsNullOrEmpty(entry.Key))
                    {
                        _packageStatusIndex[entry.Key] = entry.Value.Status ?? "Unknown";
                    }
                }
                
                _statusIndexBuilt = true;
            }
        }

        #region Async Package Status Operations
        public async Task<string> GetPackageStatusAsync(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return "Unknown";

            var packageLock = _packageLocks.GetOrAdd(packageName, _ => new SemaphoreSlim(1, 1));
            
            try
            {
                await packageLock.WaitAsync();
                return await _resiliencyManager.ExecuteWithResiliencyAsync(
                    $"get_status_{packageName}",
                    async () =>
                    {
                        if (!_statusIndexBuilt)
                        {
                            await Task.Run(() => BuildPackageStatusIndex());
                        }
                        return _packageStatusIndex.GetOrAdd(packageName, _ => "Not Found");
                    },
                    maxRetries: 3,
                    retryDelay: TimeSpan.FromMilliseconds(100));
            }
            finally
            {
                packageLock.Release();
            }
        }

        public async Task<Dictionary<string, string>> GetMultiplePackageStatusesAsync(IEnumerable<string> packageNames)
        {
            var tasks = packageNames.Select(async name =>
                new KeyValuePair<string, string>(name, await GetPackageStatusAsync(name)));
            
            var results = await Task.WhenAll(tasks);
            return new Dictionary<string, string>(results, StringComparer.OrdinalIgnoreCase);
        }
        #endregion

        /// <summary>
        /// Parses VPM optimization flags from the description field [VPM_FLAGS] section
        /// This is the VaM-compatible way to store optimization metadata
        /// </summary>
        private void ParseVpmFlagsFromDescription(VarMetadata metadata)
        {
            if (string.IsNullOrEmpty(metadata.Description))
                return;

            try
            {
                // Look for [VPM_FLAGS] section in description
                var startMarker = "[VPM_FLAGS]";
                var endMarker = "[/VPM_FLAGS]";
                
                int startIndex = metadata.Description.IndexOf(startMarker, StringComparison.Ordinal);
                if (startIndex == -1)
                    return; // No VPM flags section found
                
                int endIndex = metadata.Description.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
                if (endIndex == -1)
                    return; // Malformed section
                
                // Extract the flags section
                startIndex += startMarker.Length;
                string flagsSection = metadata.Description.Substring(startIndex, endIndex - startIndex);
                
                // Parse each line as key=value
                var lines = flagsSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    if (string.IsNullOrEmpty(trimmedLine))
                        continue;
                    
                    var parts = trimmedLine.Split(new[] { '=' }, 2);
                    if (parts.Length != 2)
                        continue;
                    
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();
                    
                    switch (key)
                    {
                        case "vpmOptimized":
                            metadata.IsOptimized = ParseBooleanValue(value);
                            break;
                        
                        case "vpmTextureOptimized":
                            metadata.HasTextureOptimization = ParseBooleanValue(value);
                            break;
                        
                        case "vpmHairOptimized":
                            metadata.HasHairOptimization = ParseBooleanValue(value);
                            break;
                        
                        case "vpmMirrorOptimized":
                            metadata.HasMirrorOptimization = ParseBooleanValue(value);
                            break;
                        
                        case "vpmJsonMinified":
                            metadata.HasJsonMinification = ParseBooleanValue(value);
                            break;
                        
                        case "vpmOriginalDate":
                            if (DateTime.TryParse(value, out var parsedDate))
                            {
                                metadata.CreatedDate = parsedDate;
                                metadata.ModifiedDate = parsedDate;
                            }
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // Failed to parse VPM flags from description
            }
        }

        /// <summary>
        /// Parses boolean values from string (handles "true", "True", "false", "False")
        /// </summary>
        private bool ParseBooleanValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || 
                   value.Equals("True", StringComparison.Ordinal);
        }
    }
}

