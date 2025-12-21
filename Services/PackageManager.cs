using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SharpCompress.Archives;
using VPM.Models;

namespace VPM.Services
{
    public class PackageManager : IDisposable
    {
        private bool _disposed;
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
        private readonly ContentTagScanner _contentTagScanner = new();
        
        public Dictionary<string, VarMetadata> PackageMetadata { get; private set; } = new Dictionary<string, VarMetadata>(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, List<ImageLocation>> _previewImageIndex;
        
        // Dependency graph for reverse dependency lookups and analysis
        private readonly DependencyGraph _dependencyGraph = new();

        /// <summary>
        /// Public accessor for the dependency graph (used by playlist manager and other services)
        /// </summary>
        public DependencyGraph DependencyGraph => _dependencyGraph;


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

        private PackageFileManager _packageFileManager;

        public PackageManager(string cacheFolder, ConcurrentDictionary<string, List<ImageLocation>> previewImageIndex = null, PackageFileManager packageFileManager = null)
        {
            _cacheFolder = cacheFolder;
            _binaryCache = new BinaryMetadataCache();
            _varScanner = new OptimizedVarScanner();
            _previewImageIndex = previewImageIndex;
            _packageFileManager = packageFileManager;

            // Parse standard VAR filenames: Creator.Package.Name.Version.var
            // Creator cannot contain dots; package name may contain dots; version commonly starts with digits but
            // may include suffixes like "1_1", "1a", "1_1b" etc.
            _varPattern = new Regex(@"^([^.]+)\.(.+?)\.([A-Za-z0-9_]+)\.var$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            
            // Don't load binary cache here - it will be loaded asynchronously
            // to avoid blocking the UI thread during startup
        }
        
        /// <summary>
        /// Loads the binary metadata cache from disk asynchronously
        /// Call this after UI initialization to avoid blocking startup
        /// </summary>
        public async Task LoadBinaryCacheAsync()
        {
            try
            {
                await Task.Run(() => _binaryCache.LoadCache());
            }
            catch (Exception)
            {
            }
        }
        
        /// <summary>
        /// Saves the binary metadata cache to disk asynchronously
        /// Call this after scanning packages to persist the cache
        /// </summary>
        public async Task SaveBinaryCacheAsync()
        {
            try
            {
                await Task.Run(() => _binaryCache.SaveCache());
            }
            catch (Exception)
            {
            }
        }
        
        /// <summary>
        /// Saves the binary metadata cache to disk synchronously (legacy, use SaveBinaryCacheAsync instead)
        /// </summary>
        public void SaveBinaryCache()
        {
            try
            {
                _binaryCache.SaveCache();
            }
            catch (Exception)
            {
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
                        metadata.Status = variant.Status; // Preserve actual status (Loaded/Available)
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
            if (string.IsNullOrWhiteSpace(filename))
                return (null, null, null);

            var fileOnly = Path.GetFileName(filename);
            var match = _varPattern.Match(fileOnly);
            if (match.Success)
            {
                return (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
            }

            // Fallback parsing for edge cases (should be rare)
            if (fileOnly.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
            {
                var parts = fileOnly[..^4].Split('.');
                if (parts.Length >= 3 && TryParseLeadingInt(parts[^1], out _))
                {
                    return (parts[0], string.Join(".", parts[1..^1]), parts[^1]);
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
                Filename = StringPool.Intern(filename),
                FilePath = StringPool.InternPath(varPath)
                // Collections are now lazy-initialized in VarMetadata
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
                    // Cache hit! Use cached metadata as-is for performance
                    // The cached metadata already has all necessary information including:
                    // - Categories (including morph pack detection)
                    // - Integrity validation results
                    // We skip re-validation on cached packages for speed
                    
                    cachedMetadata.FilePath = varPath;
                    cachedMetadata.Filename = filename;
                    
                    // IMPORTANT: cached metadata can become stale if filename parsing logic changes.
                    // Always re-apply authoritative creator/package/version values from the filename when possible.
                    var (cachedCreator, cachedPkgName, cachedVersion) = ParseFilename(filename);
                    if (!string.IsNullOrEmpty(cachedCreator))
                    {
                        cachedMetadata.CreatorName = StringPool.Intern(cachedCreator);
                    }
                    
                    if (!string.IsNullOrEmpty(cachedPkgName))
                    {
                        cachedMetadata.PackageName = StringPool.Intern(cachedPkgName);
                    }
                    
                    if (!string.IsNullOrEmpty(cachedVersion) && TryParseLeadingInt(cachedVersion, out var cachedVersionInt))
                    {
                        cachedMetadata.Version = cachedVersionInt;
                    }
                    
                    if (!string.IsNullOrEmpty(cachedCreator) && !string.IsNullOrEmpty(cachedPkgName))
                    {
                        cachedMetadata.PackageBaseName = $"{cachedCreator}.{cachedPkgName}";
                    }
                    
                    return (cachedMetadata, cachedMetadata.GetHashCode());
                }

                // Will try to get actual creation date from preview images inside the .var
                DateTime? previewImageDate = null;
                DateTime? latestArchiveFileDate = null;

            // Use SharpCompress for reliable reading
            using var archive = SharpCompressHelper.OpenForRead(varPath);
                
                string metaJsonContent = null;
                int metaJsonHash = 0;
                var contentList = new List<string>();
                IArchiveEntry metaEntry = null;
                
                // COMPLETE ARCHIVE SCAN: enumerate ALL entries and build comprehensive content list
                // This bypasses meta.json contentList to ensure accurate detection
                int entryCount = 0;
                foreach (var entry in archive.Entries)
                {
                    entryCount++;
                    
                    // Look for meta.json (case-insensitive)
                    if (metaEntry == null && 
                        entry.Key.Length == 9 && // "meta.json" length check (fast)
                        entry.Key.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                    {
                        metaEntry = entry;
                        // Don't break - we need to scan all entries
                    }
                    
                    // Track latest file date in archive (for non-optimized packages)
                    // Skip directories and meta.json itself
                    if (!entry.Key.EndsWith("/") && !entry.Key.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var entryDate = entry.LastModifiedTime ?? DateTime.Now;
                        if (!latestArchiveFileDate.HasValue || entryDate > latestArchiveFileDate.Value)
                        {
                            latestArchiveFileDate = entryDate;
                        }
                    }
                    
                    // Build comprehensive content list from ALL relevant files
                    // Skip directories and irrelevant files for performance
                    if (!entry.Key.EndsWith("/") && IsRelevantContent(entry.Key))
                    {
                        // Intern paths to reduce duplicate string memory
                        contentList.Add(StringPool.InternPath(entry.Key));
                    }
                }
                
                metadata.FileCount = entryCount;
                
                // Read meta.json if found (for metadata like creator, description, etc.)
                // But we'll use our scanned contentList instead of meta.json's contentList
                if (metaEntry != null)
                {
                    try
                    {
                        using var stream = metaEntry.OpenEntryStream();
                        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
                        metaJsonContent = reader.ReadToEnd();
                        
                        // Calculate hash of meta.json content for cache validation
                        metaJsonHash = metaJsonContent.GetHashCode();
                        
                        // Use meta.json LastModifiedTime as the creation date
                        previewImageDate = metaEntry.LastModifiedTime ?? DateTime.Now;
                        
                        // Keep our scanned contentList instead of using meta.json's contentList
                    }
                    catch
                    {
                        // Ignore meta.json read errors
                    }
                }
                
                metadata.ContentList = contentList.ToArray();


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
                    metadata.CreatorName = StringPool.Intern(creator ?? "Unknown");
                if (string.IsNullOrEmpty(metadata.PackageName))
                    metadata.PackageName = StringPool.Intern(pkgName ?? filename);
                
                // Always use filename version as it's the VAM standard (overrides meta.json packageVersion)
                if (!string.IsNullOrEmpty(version) && TryParseLeadingInt(version, out var versionInt))
                {
                    metadata.Version = versionInt;
                }

                // Detect categories and apply fallbacks
                ApplyCategoryDetectionAndFallbacks(metadata, filename);
                
                // Scan for clothing and hair tags from .vam files
                // Only scan if package has clothing or hair content
                if (metadata.Categories.Contains("Clothing", StringComparer.OrdinalIgnoreCase) || metadata.Categories.Contains("Hair", StringComparer.OrdinalIgnoreCase) ||
                    metadata.ClothingCount > 0 || metadata.HairCount > 0)
                {
                    try
                    {
                        // Convert array to list for scanning (scanner expects IEnumerable)
                        var tagResult = _contentTagScanner.ScanForTags(archive.Archive, contentList);
                        metadata.ClothingTags = tagResult.ClothingTags.ToArray();
                        metadata.HairTags = tagResult.HairTags.ToArray();
                    }
                    catch
                    {
                        // Ignore tag scanning errors - tags are optional
                    }
                }
                
                // Date priority:
                // 1. vpmOriginalDate (if package was optimized) - already set in ParseMetaJsonContent
                // 2. Latest file date inside archive (if package is NOT optimized) - use most recent file in archive
                // 3. meta.json LastWriteTime (if meta.json exists and package is optimized)
                // 4. File system modified date (fallback)
                
                // Only override if we have no vpmOriginalDate was set (package not optimized)
                if (metadata.ModifiedDate == fileInfo.LastWriteTime)
                {
                    // Package is not optimized (no vpmOriginalDate), use latest file date from archive
                    if (latestArchiveFileDate.HasValue)
                    {
                        metadata.ModifiedDate = latestArchiveFileDate.Value;
                        metadata.CreatedDate = latestArchiveFileDate.Value;
                    }
                    else if (previewImageDate.HasValue)
                    {
                        // Fallback to meta.json date if no other files in archive
                        metadata.ModifiedDate = previewImageDate.Value;
                        metadata.CreatedDate = previewImageDate.Value;
                    }
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
                
                // Trim excess capacity from collections to reduce sparse array waste
                metadata.TrimExcess();
                
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
                metadata.CreatorName = StringPool.Intern(creator ?? "Unknown");
                metadata.PackageName = StringPool.Intern(pkgName ?? filename);
                if (TryParseLeadingInt(version, out var versionInt))
                    metadata.Version = versionInt;
                metadata.Categories = new[] { "Unknown" };
                
                return (metadata, 0); // Return 0 hash for corrupted packages
            }
        }

        // Backward compatibility wrapper
        public VarMetadata ParseVarMetadataComplete(string varPath)
        {
            return ParseVarMetadata(varPath).metadata;
        }

        private string[] DetectCategoriesFromContent(string[] contentList)
        {
            var categories = new HashSet<string>();
            
            if (contentList == null || contentList.Length == 0)
            {
                categories.Add("Unknown");
                return categories.ToArray();
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
                return categories.ToArray();
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
                
            return categories.ToArray();
        }

        private (bool isMorphAsset, int morphCount) DetectMorphAsset(string[] contentList)
        {
            if (contentList == null || contentList.Length == 0)
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

        private (int morphs, int hair, int clothing, int scenes, int looks, int poses, int assets, int scripts, int plugins, int subScenes, int skins) CountContentItems(string[] contentList)
        {
            if (contentList == null || contentList.Length == 0)
            {
                return (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            }
            
            
            var processedAssetFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processedPluginFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
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
            
            return (morphCount, hairCount, clothingCount, sceneCount, looksCount, posesCount, assetsCount, scriptsCount, pluginsCount, subScenesCount, skinsCount);
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

        /// <summary>
        /// Scans VAR files including external destinations
        /// </summary>
        public async Task<(List<string> installed, List<string> available, Dictionary<string, List<string>> external)> ScanVarFilesWithExternalAsync(
            string installedFolder, 
            string allPackagesFolder, 
            List<MoveToDestination> externalDestinations)
        {
            var (installed, available) = await ScanVarFilesAsync(installedFolder, allPackagesFolder);
            var external = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (externalDestinations == null || externalDestinations.Count == 0)
                return (installed, available, external);

            // Scan external destinations in parallel
            var externalTasks = externalDestinations
                .Where(d => d.IsValid() && d.PathExists())
                .Select(async dest =>
                {
                    try
                    {
                        var files = await Task.Run(() =>
                            SafeFileEnumerator.EnumerateFiles(dest.Path, "*.var", recursive: true).ToList());
                        
                        lock (external)
                        {
                            external[dest.Name] = files;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"External scan FAILED for destination '{dest.Name}' ({dest.Path}): {ex.Message}");
                        lock (external)
                        {
                            external[dest.Name] = new List<string>();
                        }
                    }
                });

            await Task.WhenAll(externalTasks);

            return (installed, available, external);
        }

        /// <summary>
        /// Creates a clone of VarMetadata. Optimized to avoid allocating empty collections
        /// since VarMetadata now uses lazy initialization.
        /// </summary>
        private static VarMetadata CloneMetadata(VarMetadata source)
        {
            if (source == null)
            {
                return null;
            }

            var clone = new VarMetadata
            {
                Filename = source.Filename,
                PackageName = source.PackageName,
                CreatorName = source.CreatorName,
                Description = source.Description,
                Version = source.Version,
                LicenseType = source.LicenseType,
                FileCount = source.FileCount,
                CreatedDate = source.CreatedDate,
                ModifiedDate = source.ModifiedDate,
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
                IsMorphAsset = source.IsMorphAsset
            };
            
            // Only clone non-empty collections to avoid unnecessary allocations
            // VarMetadata uses lazy initialization, so null is fine for empty collections
            if (source.Dependencies?.Length > 0)
                clone.Dependencies = (string[])source.Dependencies.Clone();
            if (source.ContentTypes?.Length > 0)
                clone.ContentTypes = (string[])source.ContentTypes.Clone();
            if (source.Categories?.Length > 0)
                clone.Categories = (string[])source.Categories.Clone();
            if (source.UserTags?.Length > 0)
                clone.UserTags = (string[])source.UserTags.Clone();
            if (source.MissingDependencies?.Length > 0)
                clone.MissingDependencies = (string[])source.MissingDependencies.Clone();
            if (source.ClothingTags?.Length > 0)
                clone.ClothingTags = (string[])source.ClothingTags.Clone();
            if (source.HairTags?.Length > 0)
                clone.HairTags = (string[])source.HairTags.Clone();
            
            return clone;
        }

        /// <summary>
        /// Updates package metadata by scanning VAR files and using cached data when available.
        /// PERFORMANCE CRITICAL: Do not index preview images here - it opens every VAR file!
        /// Preview images are indexed on-demand when packages are displayed or during manual refresh.
        /// See: https://github.com/[repo]/issues/[issue] - Startup was slow due to VAR file scanning
        /// </summary>
        public void UpdatePackageMappingFast(List<string> installedFiles, List<string> availableFiles, IProgress<(int current, int total)> progress = null)
        {
            PackageMetadata.Clear();
            
            // .NET 10 GC will handle memory pressure automatically

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
                    // ParseVarMetadata checks binary cache internally, so if we got here it means:
                    // - Either it was in binary cache (cache hit)
                    // - Or it was freshly parsed (cache miss)
                    // We can't distinguish here, so we'll skip preview image indexing during initial load
                    // Preview images will be indexed on-demand when packages are displayed
                }

                snapshot.AddOrUpdateVariant(variant);
                activePackages.Add(descriptor.PackageBase);

                // ⚠️ CRITICAL PERFORMANCE NOTE (Nov 2025)
                // DO NOT call EnsurePreviewImagesIndexed() here!
                // 
                // Previous implementation opened every VAR file during startup to scan for preview images.
                // This caused startup to be extremely slow (multiple seconds per 1000 packages).
                // 
                // Root cause: EnsurePreviewImagesIndexed -> IndexPreviewImages opens the VAR archive
                // and scans all entries looking for preview image pairs. With thousands of packages,
                // this becomes a major bottleneck.
                //
                // Solution: Index preview images on-demand instead:
                // - When packages are displayed in UI (lazy loading)
                // - When packages are downloaded (IndexPreviewImagesForPackage)
                // - When user manually refreshes (RefreshPackages)
                //
                // This keeps startup fast while still providing preview images when needed.

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
                
                // .NET 10 GC handles memory pressure automatically
                materializedCount++;
            }
            
            // Remove inactive packages after iteration
            foreach (var packageBase in inactivePackages)
            {
                if (_snapshotCache.TryRemove(packageBase, out var snapshot))
                {
                    snapshot.RemoveMaterializedKeys(PackageMetadata);
                }
                _previewImageIndex.TryRemove(packageBase, out _);
            }
            
            // Trim excess capacity from all metadata collections to reduce sparse array waste
            foreach (var metadata in PackageMetadata.Values)
            {
                metadata.TrimExcess();
            }
            
            // Trim the StringPool to release any unused interned strings
            StringPool.TrimExcess();

            int optimizedCount = PackageMetadata.Values.Count(m => m.IsOptimized);
            
            // Detect old versions after all packages are loaded
            DetectOldVersions();
            
            // Detect missing dependencies for all packages
            DetectMissingDependencies();
            
            // Build dependency graph for reverse lookups and analysis
            _dependencyGraph.Build(PackageMetadata);
            
            // Populate dependency counts for each package
            PopulateDependencyCounts();

            // MEMORY FIX: Snapshot graph is large (PackageSnapshot/PackageVariant) and can retain
            // substantial metadata/string graphs. After materializing PackageMetadata, we can
            // discard snapshots to reduce steady-state RAM usage.
            _snapshotCache.Clear();
            
            // Save binary cache asynchronously after scanning completes (fire-and-forget)
            // Don't await to avoid blocking the UI
            _ = SaveBinaryCacheAsync();
        }

        /// <summary>
        /// Updates package mapping including external destination packages.
        /// External packages are marked with their destination name and color.
        /// Smart detection: if a destination path is nested inside another configured destination,
        /// it's excluded from being indexed as a separate destination.
        /// </summary>
        public void UpdatePackageMappingFast(
            List<string> installedFiles, 
            List<string> availableFiles, 
            Dictionary<string, List<string>> externalFiles,
            List<MoveToDestination> externalDestinations,
            IProgress<(int current, int total)> progress = null)
        {
            // First, process installed and available files normally
            UpdatePackageMappingFast(installedFiles, availableFiles, progress);

            // Then, add external packages
            if (externalFiles == null || externalFiles.Count == 0 || externalDestinations == null)
                return;

            // Build a lookup for destination info
            var destLookup = externalDestinations.ToDictionary(
                d => d.Name, 
                d => d, 
                StringComparer.OrdinalIgnoreCase);
            
            // Identify nested destinations to exclude from separate indexing
            var nestedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dest in externalDestinations)
            {
                if (dest == null || !dest.IsValid())
                    continue;
                
                var destPath = Path.GetFullPath(dest.Path).TrimEnd(Path.DirectorySeparatorChar);
                
                // Check if this destination is nested inside another configured destination
                foreach (var other in externalDestinations)
                {
                    if (other == null || !other.IsValid() || other.Name.Equals(dest.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    var otherPath = Path.GetFullPath(other.Path).TrimEnd(Path.DirectorySeparatorChar);
                    
                    // If destPath is inside otherPath, mark it as nested
                    if (destPath.StartsWith(otherPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        nestedDestinations.Add(dest.Name);
                        break;
                    }
                }
            }

            foreach (var kvp in externalFiles)
            {
                var destName = kvp.Key;
                var files = kvp.Value;

                if (!destLookup.TryGetValue(destName, out var destination))
                    continue;
                

                foreach (var filePath in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (!fileInfo.Exists)
                            continue;

                        var match = _varPattern.Match(Path.GetFileName(filePath));
                        if (!match.Success)
                        {
                            continue;
                        }

                        // Parse VAR filename using regex groups:
                        //   group1 = creator, group2 = package name, group3 = version (may include suffix like "1_1" or "1a")
                        var creator = match.Groups[1].Value;
                        var packageName = match.Groups[2].Value;
                        var versionText = match.Groups[3].Value;
                        if (!TryParseLeadingInt(versionText, out var version))
                            continue;

                        var packageKey = $"{creator}.{packageName}.{version}";

                        // Calculate subfolder path within the destination
                        var destPath = Path.GetFullPath(destination.Path).TrimEnd(Path.DirectorySeparatorChar);
                        var fullFilePath = Path.GetFullPath(filePath);
                        string subfolder = "";
                        
                        if (fullFilePath.StartsWith(destPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            var relativePath = fullFilePath.Substring(destPath.Length + 1);
                            var fileDir = Path.GetDirectoryName(relativePath);
                            subfolder = string.IsNullOrEmpty(fileDir) ? "" : fileDir.Replace(Path.DirectorySeparatorChar, '/');
                        }
                        
                        // SMART DETECTION: If this destination is nested inside another configured destination,
                        // store it under the parent destination with the subfolder path
                        string finalDestName = destName;
                        string finalSubfolder = subfolder;
                        string originalDestName = "";
                        string originalDestColor = "";
                        
                        if (nestedDestinations.Contains(destName))
                        {
                            // Preserve the original nested destination's name and color
                            originalDestName = destName;
                            originalDestColor = destination.StatusColor ?? "#808080";
                            
                            // Find the parent destination
                            foreach (var other in externalDestinations)
                            {
                                if (other == null || !other.IsValid() || other.Name.Equals(destName, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                
                                var otherPath = Path.GetFullPath(other.Path).TrimEnd(Path.DirectorySeparatorChar);
                                
                                // If destPath is inside otherPath, this is the parent
                                if (destPath.StartsWith(otherPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Store under parent destination with full subfolder path
                                    finalDestName = other.Name;
                                    
                                    // Calculate subfolder relative to parent
                                    var relativeToParent = destPath.Substring(otherPath.Length + 1);
                                    if (!string.IsNullOrEmpty(subfolder))
                                    {
                                        finalSubfolder = $"{relativeToParent}/{subfolder}";
                                    }
                                    else
                                    {
                                        finalSubfolder = relativeToParent;
                                    }
                                    break;
                                }
                            }
                        }

                        // Check if this package already exists (e.g., in AddonPackages or AllPackages)
                        if (PackageMetadata.ContainsKey(packageKey))
                        {
                            var existingMeta = PackageMetadata[packageKey];
                            
                            // Skip if it's an archived package - archived packages are backups and should not affect external packages
                            if (string.Equals(existingMeta.VariantRole, "Archived", StringComparison.OrdinalIgnoreCase))
                            {
                                // Don't skip - continue to add as new external package below
                            }
                            else
                            {
                                // If the existing key points to a different file path, always record this external copy.
                                // This is critical for showing external duplicates even when the package also exists
                                // in AddonPackages/AllPackages.
                                if (!string.IsNullOrEmpty(existingMeta.FilePath) &&
                                    !existingMeta.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                                {
                                    existingMeta.IsDuplicate = true;
                                    existingMeta.DuplicateLocationCount++;

                                    var subfolderKey = string.IsNullOrEmpty(finalSubfolder)
                                        ? "root"
                                        : finalSubfolder.Replace('/', '_').Replace('\\', '_');
                                    var uniqueKey = $"{packageKey}#external_{finalDestName}_{subfolderKey}_{Path.GetFileName(filePath)}";

                                    VarMetadata dupMetadata;
                                    try
                                    {
                                        var (parsed, dupMetaHash) = ParseVarMetadata(filePath);
                                        dupMetadata = parsed;
                                    }
                                    catch
                                    {
                                        dupMetadata = new VarMetadata
                                        {
                                            Filename = Path.GetFileName(filePath) ?? string.Empty,
                                            CreatorName = creator,
                                            PackageName = packageName,
                                            Version = version,
                                            IsCorrupted = true
                                        };
                                    }
                                    dupMetadata.IsDuplicate = true;
                                    dupMetadata.DuplicateLocationCount = existingMeta.DuplicateLocationCount;
                                    dupMetadata.VariantRole = "External";
                                    dupMetadata.FilePath = filePath;
                                    dupMetadata.FileSize = fileInfo.Length;
                                    dupMetadata.ExternalDestinationColorHex = StringPool.Intern(destination.StatusColor ?? "#808080");
                                    dupMetadata.Status = StringPool.Intern(finalDestName);
                                    dupMetadata.ExternalDestinationName = StringPool.Intern(finalDestName);
                                    dupMetadata.ExternalDestinationSubfolder = finalSubfolder;
                                    dupMetadata.OriginalExternalDestinationName = StringPool.Intern(originalDestName);
                                    dupMetadata.OriginalExternalDestinationColorHex = StringPool.Intern(originalDestColor);

                                    if (!dupMetadata.IsOptimized)
                                    {
                                        dupMetadata.ModifiedDate = fileInfo.LastWriteTime;
                                    }

                                    PackageMetadata[uniqueKey] = dupMetadata;
                                }

                                continue;
                            }
                        }

                        // Parse or get from cache
                        VarMetadata metadata;
                        try
                        {
                            var (parsed, metaHash) = ParseVarMetadata(filePath);
                            metadata = parsed;
                        }
                        catch
                        {
                            metadata = new VarMetadata
                            {
                                Filename = Path.GetFileName(filePath) ?? string.Empty,
                                CreatorName = creator,
                                PackageName = packageName,
                                Version = version,
                                IsCorrupted = true
                            };
                        }
                        metadata.VariantRole = "External";
                        metadata.FilePath = filePath;
                        metadata.FileSize = fileInfo.Length;
                        metadata.ExternalDestinationColorHex = StringPool.Intern(destination.StatusColor ?? "#808080");
                        metadata.Status = StringPool.Intern(finalDestName);
                        metadata.ExternalDestinationName = StringPool.Intern(finalDestName);
                        metadata.ExternalDestinationSubfolder = finalSubfolder;
                        metadata.OriginalExternalDestinationName = StringPool.Intern(originalDestName);
                        metadata.OriginalExternalDestinationColorHex = StringPool.Intern(originalDestColor);

                        if (!metadata.IsOptimized)
                        {
                            metadata.ModifiedDate = fileInfo.LastWriteTime;
                        }

                        PackageMetadata[packageKey] = metadata;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"External package mapping failed for '{filePath}': {ex.Message}");
                    }
                }
            }

            // Register external package statuses in PackageFileManager's index
            // This ensures external packages are properly recognized by GetPackageStatus()
            if (_packageFileManager != null)
            {
                var externalPackageStatuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var metadata in PackageMetadata.Values)
                {
                    if (string.Equals(metadata.VariantRole, "External", StringComparison.OrdinalIgnoreCase))
                    {
                        var creatorName = metadata.CreatorName ?? "";
                        var packageName = metadata.PackageName ?? "";
                        var status = metadata.ExternalDestinationName ?? "External";

                        if (!string.IsNullOrWhiteSpace(creatorName) && !string.IsNullOrWhiteSpace(packageName))
                        {
                            var fullPackageName = $"{creatorName}.{packageName}";
                            externalPackageStatuses[fullPackageName] = status;
                        }
                    }
                }

                if (externalPackageStatuses.Count > 0)
                {
                    _packageFileManager.RegisterExternalPackageStatuses(externalPackageStatuses);
                }
            }

            // Re-detect old versions and dependencies after adding external packages
            DetectOldVersions();
            DetectMissingDependencies();
            _dependencyGraph.Build(PackageMetadata);
            PopulateDependencyCounts();
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
        /// Detects missing dependencies for all packages.
        /// A dependency is considered missing if no package with that name (or any version for .latest/.min) exists.
        /// Handles .latest, .min[NUMBER], and exact version references.
        /// Includes external packages in the dependency resolution.
        /// </summary>
        public void DetectMissingDependencies()
        {
            // Build a set of all known package names for fast lookup
            // Include both exact names and base names (for .latest/.min resolution)
            var knownPackages = new HashSet<string>();
            var knownPackageBases = new HashSet<string>();
            // Track versions per base name for minimum version checking
            var packageVersions = new Dictionary<string, List<int>>();
            
            foreach (var kvp in PackageMetadata)
            {
                var metadata = kvp.Value;
                if (metadata.IsCorrupted)
                    continue;
                
                // Add the full package name (Creator.Package.Version)
                var fullName = $"{metadata.CreatorName}.{metadata.PackageName}.{metadata.Version}";
                knownPackages.Add(fullName);
                
                // Also add the base name for .latest/.min resolution
                var baseName = $"{metadata.CreatorName}.{metadata.PackageName}";
                knownPackageBases.Add(baseName);
                
                // Track versions for minimum version checking
                if (!packageVersions.TryGetValue(baseName, out var versions))
                {
                    versions = new List<int>();
                    packageVersions[baseName] = versions;
                }
                versions.Add(metadata.Version);
            }
            
            // Now check each package's dependencies
            int totalMissing = 0;
            foreach (var kvp in PackageMetadata)
            {
                var metadata = kvp.Value;
                var missingDeps = new List<string>();
                
                if (metadata.Dependencies == null || metadata.Dependencies.Length == 0)
                    continue;
                
                foreach (var dep in metadata.Dependencies)
                {
                    if (string.IsNullOrEmpty(dep))
                        continue;
                    
                    bool found = IsDependencySatisfied(dep, knownPackages, knownPackageBases, packageVersions);
                    
                    if (!found)
                    {
                        missingDeps.Add(dep);
                        totalMissing++;
                    }
                }
                metadata.MissingDependencies = missingDeps.ToArray();
            }
        }
        
        /// <summary>
        /// Checks if a dependency is satisfied by the available packages.
        /// Handles .latest, .min[NUMBER], and exact version references.
        /// </summary>
        private bool IsDependencySatisfied(
            string dep,
            HashSet<string> knownPackages,
            HashSet<string> knownPackageBases,
            Dictionary<string, List<int>> packageVersions)
        {
            var depInfo = DependencyVersionInfo.Parse(dep);
            
            switch (depInfo.VersionType)
            {
                case DependencyVersionType.Latest:
                    // Any version of this package satisfies .latest
                    return knownPackageBases.Contains(depInfo.BaseName);
                
                case DependencyVersionType.Minimum:
                    // Check if any version >= minimum exists
                    if (packageVersions.TryGetValue(depInfo.BaseName, out var versions))
                    {
                        var minVersion = depInfo.VersionNumber ?? 0;
                        // Any version >= minimum satisfies the dependency
                        return versions.Any(v => v >= minVersion);
                    }
                    return false;
                
                case DependencyVersionType.Exact:
                    // Check exact match first
                    if (knownPackages.Contains(dep))
                        return true;
                    
                    // Fallback: check if any version of this package exists
                    // (for flexibility when exact version is not available)
                    return knownPackageBases.Contains(depInfo.BaseName);
                
                default:
                    return knownPackages.Contains(dep);
            }
        }

        /// <summary>
        /// Removes a newly downloaded package from all MissingDependencies lists.
        /// This is more efficient than re-running DetectMissingDependencies() for the entire collection.
        /// Call this after downloading a package from Hub to update the missing dependencies state.
        /// Handles .latest, .min[NUMBER], and exact version references.
        /// </summary>
        /// <param name="packageName">The full package name (Creator.Package.Version) that was downloaded</param>
        public void RemoveFromMissingDependencies(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return;
            
            // Parse the downloaded package to get base name and version (done once)
            var downloadedInfo = DependencyVersionInfo.Parse(packageName);
            var baseName = downloadedInfo.BaseName;
            var downloadedVersion = downloadedInfo.VersionNumber ?? 0;
            
            // PERFORMANCE FIX: Pre-compute base name prefix for fast string matching
            // This avoids parsing every dependency - we only parse those that match the prefix
            var baseNamePrefix = !string.IsNullOrEmpty(baseName) ? baseName + "." : null;
            
            foreach (var kvp in PackageMetadata)
            {
                var metadata = kvp.Value;
                if (metadata.MissingDependencies == null || metadata.MissingDependencies.Length == 0)
                    continue;
                
                // Remove exact match
                if (metadata.MissingDependencies.Contains(packageName))
                {
                    metadata.MissingDependencies = metadata.MissingDependencies.Where(d => d != packageName).ToArray();
                }
                
                // Remove dependencies that this package satisfies
                if (!string.IsNullOrEmpty(baseNamePrefix))
                {
                    metadata.MissingDependencies = metadata.MissingDependencies.Where(dep =>
                    {
                        // PERFORMANCE FIX: Quick prefix check before expensive Parse
                        // Most dependencies won't match, so this short-circuits early
                        if (!dep.StartsWith(baseNamePrefix, StringComparison.OrdinalIgnoreCase) &&
                            !dep.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                            return true; // Keep it
                        
                        var depInfo = DependencyVersionInfo.Parse(dep);
                        
                        // Must be the same base package (double-check after parse)
                        if (!string.Equals(depInfo.BaseName, baseName, StringComparison.OrdinalIgnoreCase))
                            return true; // Keep it
                        
                        // Check if downloaded version satisfies the dependency
                        // If it satisfies, we remove it (return false)
                        return !depInfo.IsSatisfiedBy(downloadedVersion);
                    }).ToArray();
                }
            }
        }
        
        /// <summary>
        /// Populates DependencyCount and DependentsCount for all packages based on the dependency graph
        /// </summary>
        private void PopulateDependencyCounts()
        {
            int noDepsCount = 0;
            int noDependentsCount = 0;
            
            foreach (var kvp in PackageMetadata)
            {
                var metadata = kvp.Value;
                var packageName = kvp.Key;
                
                // Set dependency count (number of packages this one depends on)
                if (metadata.Dependencies != null)
                {
                    metadata.DependencyCount = metadata.Dependencies.Length;
                }
                else
                {
                    metadata.DependencyCount = 0;
                }
                
                // Set dependents count (number of packages that depend on this one)
                var dependents = _dependencyGraph.GetDependents(packageName);
                metadata.DependentsCount = dependents?.Count ?? 0;
                
                // Debug tracking
                if (metadata.DependencyCount == 0)
                    noDepsCount++;
                if (metadata.DependentsCount == 0)
                    noDependentsCount++;
            }
            
        }
        
        /// <summary>
        /// Extracts the base name (Creator.Package) from a full package name.
        /// Handles all version formats: exact (Creator.Package.5), latest (Creator.Package.latest), 
        /// and minimum (Creator.Package.min32).
        /// </summary>
        private string GetPackageBaseName(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return null;
            
            // Use the centralized DependencyVersionInfo parser
            return DependencyVersionInfo.GetBaseName(packageName);
        }

        #region Dependency Graph API
        
        /// <summary>
        /// Gets packages that depend on the specified package (reverse dependencies)
        /// </summary>
        public List<string> GetPackageDependents(string packageName)
        {
            return _dependencyGraph.GetDependents(packageName);
        }
        
        /// <summary>
        /// Gets the count of packages that depend on the specified package
        /// </summary>
        public int GetPackageDependentsCount(string packageName)
        {
            return _dependencyGraph.GetDependentsCount(packageName);
        }
        
        /// <summary>
        /// Gets orphan packages (packages that no other package depends on)
        /// </summary>
        public List<string> GetOrphanPackages()
        {
            return _dependencyGraph.GetOrphanPackages();
        }
        
        /// <summary>
        /// Gets critical packages (packages that many others depend on)
        /// </summary>
        public List<(string Package, int DependentCount)> GetCriticalPackages(int minDependents = 5)
        {
            return _dependencyGraph.GetCriticalPackages(minDependents);
        }
        
        /// <summary>
        /// Gets the full dependency chain for a package (all transitive dependencies)
        /// </summary>
        public HashSet<string> GetFullDependencyChain(string packageName)
        {
            return _dependencyGraph.GetFullDependencyChain(packageName);
        }
        
        /// <summary>
        /// Gets packages that would break if the specified package is removed
        /// </summary>
        public List<string> GetPackagesThatWouldBreak(string packageName)
        {
            return _dependencyGraph.GetPackagesThatWouldBreak(packageName);
        }
        
        /// <summary>
        /// Gets dependency statistics for a package
        /// </summary>
        public PackageDependencyStats GetPackageDependencyStats(string packageName)
        {
            return _dependencyGraph.GetPackageStats(packageName);
        }
        
        /// <summary>
        /// Gets overall dependency graph statistics
        /// </summary>
        public GraphStatistics GetDependencyGraphStatistics()
        {
            return _dependencyGraph.GetGraphStatistics();
        }
        
        /// <summary>
        /// Checks if a package exists in the dependency graph
        /// </summary>
        public bool PackageExistsInGraph(string packageName)
        {
            return _dependencyGraph.PackageExists(packageName);
        }
        
        /// <summary>
        /// Checks if any of the given packages have dependents and returns warning info
        /// </summary>
        /// <param name="packages">Packages to check</param>
        /// <returns>Dictionary of package name to list of dependents, only for packages with dependents</returns>
        public Dictionary<string, List<string>> CheckPackagesForDependents(IEnumerable<VarMetadata> packages)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var pkg in packages)
            {
                var packageFullName = $"{pkg.CreatorName}.{pkg.PackageName}.{pkg.Version}";
                var dependents = _dependencyGraph.GetDependents(packageFullName);
                
                if (dependents.Count > 0)
                {
                    result[packageFullName] = dependents;
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Gets a formatted warning message for packages that have dependents
        /// </summary>
        public string GetDependentsWarningMessage(Dictionary<string, List<string>> packagesWithDependents, int maxToShow = 5)
        {
            if (packagesWithDependents.Count == 0)
                return null;
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("⚠️ WARNING: The following packages have other packages that depend on them:\n");
            
            int shown = 0;
            foreach (var kvp in packagesWithDependents.OrderByDescending(x => x.Value.Count))
            {
                if (shown >= maxToShow)
                {
                    sb.AppendLine($"... and {packagesWithDependents.Count - maxToShow} more packages with dependents");
                    break;
                }
                
                sb.AppendLine($"• {kvp.Key} ({kvp.Value.Count} dependents)");
                foreach (var dep in kvp.Value.Take(3))
                {
                    sb.AppendLine($"    └─ {dep}");
                }
                if (kvp.Value.Count > 3)
                {
                    sb.AppendLine($"    └─ ... and {kvp.Value.Count - 3} more");
                }
                shown++;
            }
            
            sb.AppendLine("\nArchiving these packages may break the dependent packages.");
            
            return sb.ToString();
        }
        
        #endregion

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
        public void InvalidatePackageCache(string packageName, string filePath = null)
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

            _previewImageIndex.TryRemove(baseName, out _);
            
            // Remove from binary cache using the actual filename if provided
            // The binary cache uses full filename as key, not the base package name
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                string filename = Path.GetFileName(filePath);
                _binaryCache.Remove(filename);
            }
            else
            {
                // Fallback: try to remove by base name (for backwards compatibility)
                _binaryCache.Remove(baseName);
            }
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
            
            // Preserve external destination information if the input metadata has it
            if (metadata != null && !string.IsNullOrEmpty(metadata.ExternalDestinationName))
            {
                freshMetadata.ExternalDestinationName = metadata.ExternalDestinationName;
                freshMetadata.ExternalDestinationColorHex = metadata.ExternalDestinationColorHex;
                freshMetadata.ExternalDestinationSubfolder = metadata.ExternalDestinationSubfolder;
                freshMetadata.OriginalExternalDestinationName = metadata.OriginalExternalDestinationName;
                freshMetadata.OriginalExternalDestinationColorHex = metadata.OriginalExternalDestinationColorHex;
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
                if (_previewImageIndex.ContainsKey(metadataKey))
                {
                    return;
                }

                IndexPreviewImages(varPath, packageBase, metadataKey);
            }
            catch
            {
            }
        }

        private void DebugLog(string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var msg = $"[{timestamp}] {message}";
            System.Diagnostics.Debug.WriteLine(msg);
            
            var debugLogPath = Path.Combine(Path.GetTempPath(), "vpm_preview_debug.log");
            try
            {
                File.AppendAllText(debugLogPath, msg + "\n");
            }
            catch { }
        }

        private void IndexPreviewImages(string varPath, string packageBase, string metadataKey)
        {
            try
            {
                var imageLocations = new List<ImageLocation>();

                using var archive = SharpCompressHelper.OpenForRead(varPath);

                // Build a flattened list of all files in the archive for pairing detection
                var allFilesFlattened = new List<string>();
                foreach (var entry in archive.Entries)
                {
                    if (!entry.Key.EndsWith("/"))
                    {
                        var entryFilename = Path.GetFileName(entry.Key);
                        allFilesFlattened.Add(entryFilename.ToLower());
                    }
                }

                // Check each image file for pairing
                foreach (var entry in archive.Entries)
                {
                    if (entry.Key.EndsWith("/")) continue;

                    var ext = Path.GetExtension(entry.Key).ToLower();
                    if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;

                    var entryFilename = Path.GetFileName(entry.Key).ToLower();
                    
                    // Size filter: 1KB - 1MB
                    if (entry.Size < 1024 || entry.Size > 1024 * 1024)
                    {
                        continue;
                    }
                    
                    // Check if this image has a paired file with same stem
                    bool isPaired = PreviewImageValidator.IsPreviewImage(entryFilename, allFilesFlattened);
                    if (!isPaired)
                    {
                        continue;
                    }

                    // Get image dimensions from header
                    var (width, height) = SharpCompressHelper.GetImageDimensionsFromEntry(archive.Archive, entry);
                    
                    // Only index images with valid dimensions
                    if (width <= 0 || height <= 0)
                    {
                        continue;
                    }

                    imageLocations.Add(new ImageLocation
                    {
                        VarFilePath = varPath,
                        InternalPath = entry.Key,
                        FileSize = entry.Size,
                        Width = width,
                        Height = height
                    });
                }

                if (imageLocations.Count > 0)
                {
                    _previewImageIndex[metadataKey] = imageLocations;
                }
            }
            catch
            {
                // Silently fail if image indexing fails
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
                
                // Extract metadata from meta.json - intern strings to reduce duplicates
                if (metaData.TryGetProperty("packageName", out var pName))
                    metadata.PackageName = StringPool.Intern(pName.GetString() ?? "");
                if (metaData.TryGetProperty("creatorName", out var cName))
                    metadata.CreatorName = StringPool.Intern(cName.GetString() ?? "");
                if (metaData.TryGetProperty("description", out var desc))
                    metadata.Description = StringPool.Intern(desc.GetString() ?? "");
                if (metaData.TryGetProperty("packageVersion", out var ver))
                    metadata.Version = ver.GetInt32();
                if (metaData.TryGetProperty("licenseType", out var license))
                {
                    var licenseValue = license.GetString() ?? "";
                    metadata.LicenseType = StringPool.InternIgnoreCase(licenseValue);
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
                        .ToArray();
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

        private static bool TryParseLeadingInt(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
                return false;

            // Strip any underscore suffix first (common in VAR filenames)
            var firstSegment = text.Split('_')[0];

            // Extract leading digits (supports formats like "1a", "12b")
            var i = 0;
            while (i < firstSegment.Length && char.IsDigit(firstSegment[i]))
                i++;

            if (i == 0)
                return false;

            return int.TryParse(firstSegment.Substring(0, i), out value);
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
            
            // MEMORY FIX: Clear ContentList after processing.
            // Full per-file lists are loaded on-demand by the UI to avoid retaining huge lists for every package.
            metadata.ContentList = null;
            
            // Fallback category detection from filename if no categories found
            if (metadata.Categories.Length == 0)
            {
                var lowerName = filename.ToLower();
                var categories = new List<string>();
                
                if (lowerName.Contains("scene")) categories.Add("Scenes");
                else if (lowerName.Contains("look") || lowerName.Contains("appearance")) categories.Add("Looks");
                else if (lowerName.Contains("clothing")) categories.Add("Clothing");
                else if (lowerName.Contains("hair")) categories.Add("Hair");
                else if (lowerName.Contains("morph") || lowerName.Contains("morphpack")) categories.Add("Morph Pack");
                else if (lowerName.Contains("pose")) categories.Add("Poses");
                else categories.Add("Unknown");
                
                metadata.Categories = categories.ToArray();
            }
        }
        private string[] ParseDependencies(JsonElement deps)
        {
            // Use HashSet for O(1) duplicate detection instead of List with O(n) Contains checks
            var dependenciesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Recursively parse all dependencies including subdependencies
            ParseDependenciesRecursive(deps, dependenciesSet);

            // Convert back to Array for compatibility with existing code
            return dependenciesSet.ToArray();
        }

        /// <summary>
        /// Recursively parses dependencies at all nesting levels.
        /// Interns dependency strings to reduce memory from duplicates.
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
                                dependenciesSet.Add(StringPool.Intern(depStr));
                        }
                        else if (dep.ValueKind == JsonValueKind.Object)
                        {
                            // Extract from object properties with early exit optimization
                            foreach (var prop in dep.EnumerateObject())
                            {
                                // Check for known property names that contain dependency info
                                if (prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                                    prop.Name.Equals("packageName", StringComparison.OrdinalIgnoreCase) ||
                                    prop.Name.Equals("package", StringComparison.OrdinalIgnoreCase))
                                {
                                    var foundDependency = prop.Value.GetString();
                                    if (!string.IsNullOrEmpty(foundDependency))
                                    {
                                        dependenciesSet.Add(StringPool.Intern(foundDependency));
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
                        dependenciesSet.Add(StringPool.Intern(singleDep));
                    break;

                case JsonValueKind.Object:
                    // Property names are dependency names (common VAM format)
                    foreach (var prop in deps.EnumerateObject())
                    {
                        if (!string.IsNullOrEmpty(prop.Name))
                        {
                            dependenciesSet.Add(StringPool.Intern(prop.Name));
                            
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
        
        #region IDisposable
        
        /// <summary>
        /// Dispose resources.
        /// Releases all SemaphoreSlim instances to prevent handle leaks.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            // Dispose the main throttle semaphore
            _throttle?.Dispose();
            
            // Dispose all per-package locks
            foreach (var packageLock in _packageLocks.Values)
            {
                packageLock?.Dispose();
            }
            _packageLocks.Clear();
            
            // Dispose binary cache
            _binaryCache?.Dispose();
            
            // Dispose var scanner if it implements IDisposable
            (_varScanner as IDisposable)?.Dispose();
        }
        
        #endregion
    }
}

