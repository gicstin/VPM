using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// High-performance binary serialization cache for VarMetadata
    /// Provides 5-10x faster loading compared to JSON deserialization
    /// Based on _VB project's VarPackageMgr binary caching strategy
    /// </summary>
    public class BinaryMetadataCache
    {
        private const int CACHE_VERSION = 11; // Improved IsRelevantContent filter - more permissive, excludes non-content files
        private readonly string _cacheFilePath;
        private readonly string _cacheDirectory;
        private readonly Dictionary<string, CachedMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new();
        
        // Statistics
        private int _cacheHits = 0;
        private int _cacheMisses = 0;

        public BinaryMetadataCache()
        {
            // Use AppData for cache storage (survives app updates, per-user isolation)
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheDirectory = Path.Combine(appDataPath, "VPM", "Cache");
            _cacheFilePath = Path.Combine(_cacheDirectory, "PackageMetadata.cache");
            
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
            
        }

        /// <summary>
        /// Cached metadata with file signature for validation
        /// </summary>
        private class CachedMetadata
        {
            public VarMetadata Metadata { get; set; }
            public long FileSize { get; set; }
            public long LastWriteTicks { get; set; }
        }

        /// <summary>
        /// Loads the binary cache from disk
        /// Returns true if cache was successfully loaded
        /// </summary>
        public bool LoadCache()
        {
            if (!File.Exists(_cacheFilePath))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream);

                // Read and validate version
                var version = reader.ReadInt32();
                if (version != CACHE_VERSION)
                {
                    return false;
                }

                // Read entry count
                var count = reader.ReadInt32();
                if (count < 0 || count > 100000) // Sanity check
                {
                    return false;
                }

                lock (_cacheLock)
                {
                    _cache.Clear();

                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            var key = reader.ReadString();
                            var cached = ReadCachedMetadata(reader);
                            _cache[key] = cached;
                        }
                        catch { }
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Saves the current cache to disk
        /// </summary>
        public bool SaveCache()
        {
            try
            {
                var tempPath = _cacheFilePath + ".tmp";

                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    // Write version
                    writer.Write(CACHE_VERSION);

                    lock (_cacheLock)
                    {
                        // Write entry count
                        writer.Write(_cache.Count);

                        foreach (var kvp in _cache)
                        {
                            writer.Write(kvp.Key);
                            WriteCachedMetadata(writer, kvp.Value);
                        }
                    }

                    writer.Flush();
                }

                // Atomic replace
                if (File.Exists(_cacheFilePath))
                {
                    File.Delete(_cacheFilePath);
                }
                File.Move(tempPath, _cacheFilePath);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tries to get cached metadata for a package
        /// Returns null if not cached or signature doesn't match
        /// Uses full filename as cache key to handle multiple versions of the same package
        /// </summary>
        public VarMetadata TryGetCached(string packageNameOrFilename, long fileSize, long lastWriteTicks)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(packageNameOrFilename, out var cached))
                {
                    // Validate signature
                    if (cached.FileSize == fileSize && cached.LastWriteTicks == lastWriteTicks)
                    {
                        _cacheHits++;
                        return CloneMetadata(cached.Metadata);
                    }
                }
                
                _cacheMisses++;
            }

            return null;
        }

        /// <summary>
        /// Adds or updates metadata in the cache
        /// Uses full filename as cache key to handle multiple versions of the same package
        /// </summary>
        public void AddOrUpdate(string packageNameOrFilename, VarMetadata metadata, long fileSize, long lastWriteTicks)
        {
            if (metadata == null) return;

            lock (_cacheLock)
            {
                _cache[packageNameOrFilename] = new CachedMetadata
                {
                    Metadata = CloneMetadata(metadata),
                    FileSize = fileSize,
                    LastWriteTicks = lastWriteTicks
                };
            }
        }

        /// <summary>
        /// Removes a package from the cache
        /// </summary>
        public void Remove(string packageName)
        {
            lock (_cacheLock)
            {
                _cache.Remove(packageName);
            }
        }

        /// <summary>
        /// Gets the number of cached entries
        /// </summary>
        public int Count
        {
            get
            {
                lock (_cacheLock)
                {
                    return _cache.Count;
                }
            }
        }

        /// <summary>
        /// Clears all cached entries
        /// </summary>
        public void Clear()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
            }
        }
        
        /// <summary>
        /// Gets cache statistics
        /// </summary>
        public (int hits, int misses, double hitRate) GetStatistics()
        {
            lock (_cacheLock)
            {
                var total = _cacheHits + _cacheMisses;
                var hitRate = total > 0 ? (_cacheHits * 100.0 / total) : 0;
                return (_cacheHits, _cacheMisses, hitRate);
            }
        }
        
        /// <summary>
        /// Updates content counters for a cached package
        /// </summary>
        public void UpdateContentCounters(string packageName, int morphCount, int hairCount, int clothingCount, int sceneCount, 
            int looksCount = 0, int posesCount = 0, int assetsCount = 0, int scriptsCount = 0, 
            int pluginsCount = 0, int subScenesCount = 0, int skinsCount = 0)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(packageName, out var cached))
                {
                    cached.Metadata.MorphCount = morphCount;
                    cached.Metadata.HairCount = hairCount;
                    cached.Metadata.ClothingCount = clothingCount;
                    cached.Metadata.SceneCount = sceneCount;
                    cached.Metadata.LooksCount = looksCount;
                    cached.Metadata.PosesCount = posesCount;
                    cached.Metadata.AssetsCount = assetsCount;
                    cached.Metadata.ScriptsCount = scriptsCount;
                    cached.Metadata.PluginsCount = pluginsCount;
                    cached.Metadata.SubScenesCount = subScenesCount;
                    cached.Metadata.SkinsCount = skinsCount;
                }
            }
        }
        
        /// <summary>
        /// Resets cache statistics
        /// </summary>
        public void ResetStatistics()
        {
            lock (_cacheLock)
            {
                _cacheHits = 0;
                _cacheMisses = 0;
            }
        }
        
        /// <summary>
        /// Gets the cache directory path
        /// </summary>
        public string CacheDirectory => _cacheDirectory;
        
        /// <summary>
        /// Gets the cache file path
        /// </summary>
        public string CacheFilePath => _cacheFilePath;
        
        /// <summary>
        /// Clears the cache from memory and deletes the cache file
        /// </summary>
        public bool ClearCacheCompletely()
        {
            try
            {
                lock (_cacheLock)
                {
                    _cache.Clear();
                    _cacheHits = 0;
                    _cacheMisses = 0;
                }
                
                if (File.Exists(_cacheFilePath))
                {
                    File.Delete(_cacheFilePath);
                }
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        #region Binary Serialization

        private CachedMetadata ReadCachedMetadata(BinaryReader reader)
        {
            var cached = new CachedMetadata
            {
                FileSize = reader.ReadInt64(),
                LastWriteTicks = reader.ReadInt64(),
                Metadata = ReadVarMetadata(reader)
            };

            return cached;
        }

        private void WriteCachedMetadata(BinaryWriter writer, CachedMetadata cached)
        {
            writer.Write(cached.FileSize);
            writer.Write(cached.LastWriteTicks);
            WriteVarMetadata(writer, cached.Metadata);
        }

        private VarMetadata ReadVarMetadata(BinaryReader reader)
        {
            var metadata = new VarMetadata
            {
                Filename = reader.ReadString(),
                PackageName = reader.ReadString(),
                CreatorName = reader.ReadString(),
                Description = reader.ReadString(),
                Version = reader.ReadInt32(),
                LicenseType = reader.ReadString(),
                FileCount = reader.ReadInt32(),
                IsCorrupted = reader.ReadBoolean(),
                PreloadMorphs = reader.ReadBoolean(),
                Status = reader.ReadString(),
                FilePath = reader.ReadString(),
                FileSize = reader.ReadInt64(),
                IsOptimized = reader.ReadBoolean(),
                HasTextureOptimization = reader.ReadBoolean(),
                HasHairOptimization = reader.ReadBoolean(),
                HasMirrorOptimization = reader.ReadBoolean(),
                VariantRole = reader.ReadString(),
                IsDuplicate = reader.ReadBoolean(),
                DuplicateLocationCount = reader.ReadInt32(),
                MorphCount = reader.ReadInt32(),
                HairCount = reader.ReadInt32(),
                ClothingCount = reader.ReadInt32(),
                SceneCount = reader.ReadInt32(),
                LooksCount = reader.ReadInt32(),
                PosesCount = reader.ReadInt32(),
                AssetsCount = reader.ReadInt32(),
                ScriptsCount = reader.ReadInt32(),
                PluginsCount = reader.ReadInt32(),
                SubScenesCount = reader.ReadInt32(),
                SkinsCount = reader.ReadInt32()
            };

            // Read nullable DateTime fields
            metadata.CreatedDate = reader.ReadBoolean() ? new DateTime(reader.ReadInt64()) : null;
            metadata.ModifiedDate = reader.ReadBoolean() ? new DateTime(reader.ReadInt64()) : null;

            // Read Dependencies list
            var depCount = reader.ReadInt32();
            metadata.Dependencies = new List<string>(depCount);
            for (int i = 0; i < depCount; i++)
            {
                metadata.Dependencies.Add(reader.ReadString());
            }

            // Read ContentList
            var contentCount = reader.ReadInt32();
            metadata.ContentList = new List<string>(contentCount);
            for (int i = 0; i < contentCount; i++)
            {
                metadata.ContentList.Add(reader.ReadString());
            }

            // Read ContentTypes HashSet
            var contentTypesCount = reader.ReadInt32();
            metadata.ContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < contentTypesCount; i++)
            {
                metadata.ContentTypes.Add(reader.ReadString());
            }

            // Read Categories HashSet
            var categoriesCount = reader.ReadInt32();
            metadata.Categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < categoriesCount; i++)
            {
                metadata.Categories.Add(reader.ReadString());
            }

            // Read UserTags list
            var userTagsCount = reader.ReadInt32();
            metadata.UserTags = new List<string>(userTagsCount);
            for (int i = 0; i < userTagsCount; i++)
            {
                metadata.UserTags.Add(reader.ReadString());
            }

            // Read AllFiles list (complete file index from archive)
            try
            {
                var allFilesCount = reader.ReadInt32();
                metadata.AllFiles = new List<string>(allFilesCount);
                for (int i = 0; i < allFilesCount; i++)
                {
                    metadata.AllFiles.Add(reader.ReadString());
                }
            }
            catch
            {
                // If AllFiles can't be read (old cache format), initialize empty list
                metadata.AllFiles = new List<string>();
            }

            return metadata;
        }

        private void WriteVarMetadata(BinaryWriter writer, VarMetadata metadata)
        {
            writer.Write(metadata.Filename ?? "");
            writer.Write(metadata.PackageName ?? "");
            writer.Write(metadata.CreatorName ?? "");
            writer.Write(metadata.Description ?? "");
            writer.Write(metadata.Version);
            writer.Write(metadata.LicenseType ?? "");
            writer.Write(metadata.FileCount);
            writer.Write(metadata.IsCorrupted);
            writer.Write(metadata.PreloadMorphs);
            writer.Write(metadata.Status ?? "");
            writer.Write(metadata.FilePath ?? "");
            writer.Write(metadata.FileSize);
            writer.Write(metadata.IsOptimized);
            writer.Write(metadata.HasTextureOptimization);
            writer.Write(metadata.HasHairOptimization);
            writer.Write(metadata.HasMirrorOptimization);
            writer.Write(metadata.VariantRole ?? "");
            writer.Write(metadata.IsDuplicate);
            writer.Write(metadata.DuplicateLocationCount);
            writer.Write(metadata.MorphCount);
            writer.Write(metadata.HairCount);
            writer.Write(metadata.ClothingCount);
            writer.Write(metadata.SceneCount);
            writer.Write(metadata.LooksCount);
            writer.Write(metadata.PosesCount);
            writer.Write(metadata.AssetsCount);
            writer.Write(metadata.ScriptsCount);
            writer.Write(metadata.PluginsCount);
            writer.Write(metadata.SubScenesCount);
            writer.Write(metadata.SkinsCount);

            // Write nullable DateTime fields
            writer.Write(metadata.CreatedDate.HasValue);
            if (metadata.CreatedDate.HasValue)
            {
                writer.Write(metadata.CreatedDate.Value.Ticks);
            }

            writer.Write(metadata.ModifiedDate.HasValue);
            if (metadata.ModifiedDate.HasValue)
            {
                writer.Write(metadata.ModifiedDate.Value.Ticks);
            }

            // Write Dependencies list
            var dependencies = metadata.Dependencies ?? new List<string>();
            writer.Write(dependencies.Count);
            foreach (var dep in dependencies)
            {
                writer.Write(dep ?? "");
            }

            // Write ContentList
            var contentList = metadata.ContentList ?? new List<string>();
            writer.Write(contentList.Count);
            foreach (var content in contentList)
            {
                writer.Write(content ?? "");
            }

            // Write ContentTypes HashSet
            var contentTypes = metadata.ContentTypes ?? new HashSet<string>();
            writer.Write(contentTypes.Count);
            foreach (var type in contentTypes)
            {
                writer.Write(type ?? "");
            }

            // Write Categories HashSet
            var categories = metadata.Categories ?? new HashSet<string>();
            writer.Write(categories.Count);
            foreach (var category in categories)
            {
                writer.Write(category ?? "");
            }

            // Write UserTags list
            var userTags = metadata.UserTags ?? new List<string>();
            writer.Write(userTags.Count);
            foreach (var tag in userTags)
            {
                writer.Write(tag ?? "");
            }

            // Write AllFiles list (complete file index from archive)
            var allFiles = metadata.AllFiles ?? new List<string>();
            writer.Write(allFiles.Count);
            foreach (var file in allFiles)
            {
                writer.Write(file ?? "");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a deep clone of VarMetadata to prevent reference sharing
        /// </summary>
        private VarMetadata CloneMetadata(VarMetadata source)
        {
            if (source == null) return null;

            return new VarMetadata
            {
                Filename = source.Filename,
                PackageName = source.PackageName,
                CreatorName = source.CreatorName,
                Description = source.Description,
                Version = source.Version,
                LicenseType = source.LicenseType,
                Dependencies = new List<string>(source.Dependencies ?? new List<string>()),
                ContentList = new List<string>(source.ContentList ?? new List<string>()),
                ContentTypes = new HashSet<string>(source.ContentTypes ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase),
                Categories = new HashSet<string>(source.Categories ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase),
                FileCount = source.FileCount,
                CreatedDate = source.CreatedDate,
                ModifiedDate = source.ModifiedDate,
                UserTags = new List<string>(source.UserTags ?? new List<string>()),
                IsCorrupted = source.IsCorrupted,
                PreloadMorphs = source.PreloadMorphs,
                Status = source.Status,
                FilePath = source.FilePath,
                FileSize = source.FileSize,
                IsOptimized = source.IsOptimized,
                HasTextureOptimization = source.HasTextureOptimization,
                HasHairOptimization = source.HasHairOptimization,
                HasMirrorOptimization = source.HasMirrorOptimization,
                VariantRole = source.VariantRole,
                IsDuplicate = source.IsDuplicate,
                DuplicateLocationCount = source.DuplicateLocationCount,
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
                AllFiles = new List<string>(source.AllFiles ?? new List<string>())
            };
        }

        #endregion
    }
}

