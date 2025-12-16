using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// High-performance binary serialization cache for VarMetadata
    /// Provides 5-10x faster loading compared to JSON deserialization
    /// Based on _VB project's VarPackageMgr binary caching strategy
    /// </summary>
    public class BinaryMetadataCache : IDisposable
    {
        private const int CACHE_VERSION = 14; // Removed ContentList and AllFiles from cache payload
        private readonly string _cacheFilePath;
        private readonly string _cacheDirectory;
        private readonly Dictionary<string, CachedMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim();
        
        // Statistics
        private int _cacheHits = 0;
        private int _cacheMisses = 0;

        public BinaryMetadataCache()
        {
            // Use AppData for cache storage (survives app updates, per-user isolation)
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheDirectory = Path.Combine(appDataPath, "VPM", "Cache");
            _cacheFilePath = Path.Combine(_cacheDirectory, "PackageMetadata.cache");
            
            try
            {
                if (!Directory.Exists(_cacheDirectory))
                {
                    Directory.CreateDirectory(_cacheDirectory);
                }
            }
            catch (Exception)
            {
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

                _cacheLock.EnterWriteLock();
                try
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
                finally
                {
                    _cacheLock.ExitWriteLock();
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

                    _cacheLock.EnterReadLock();
                    try
                    {
                        // Write entry count
                        writer.Write(_cache.Count);

                        foreach (var kvp in _cache)
                        {
                            writer.Write(kvp.Key);
                            WriteCachedMetadata(writer, kvp.Value);
                        }
                    }
                    finally
                    {
                        _cacheLock.ExitReadLock();
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
            catch (Exception)
            {
                // Clean up temp file if it exists
                try
                {
                    var tempPath = _cacheFilePath + ".tmp";
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
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
            _cacheLock.EnterReadLock();
            try
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
            finally
            {
                _cacheLock.ExitReadLock();
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

            _cacheLock.EnterWriteLock();
            try
            {
                _cache[packageNameOrFilename] = new CachedMetadata
                {
                    Metadata = CloneMetadata(metadata),
                    FileSize = fileSize,
                    LastWriteTicks = lastWriteTicks
                };
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Removes a package from the cache
        /// </summary>
        public void Remove(string packageName)
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _cache.Remove(packageName);
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Clears the in-memory cache dictionary to free memory.
        /// Call this after the PackageManager has fully loaded the metadata into its own structures.
        /// The cache file remains on disk.
        /// </summary>
        public void ClearMemory()
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _cache.Clear();
                // We don't reset stats here as they might be interesting
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Updates the cache from the provided metadata dictionary before saving.
        /// This is necessary if ClearMemory() was called, as the internal cache would be empty.
        /// </summary>
        public void UpdateFrom(Dictionary<string, VarMetadata> currentMetadata)
        {
            if (currentMetadata == null) return;

            _cacheLock.EnterWriteLock();
            try
            {
                _cache.Clear();
                foreach (var kvp in currentMetadata)
                {
                    // Only cache if we have valid file info
                    // We might need to look up file info if it's not in VarMetadata
                    // But VarMetadata has FileSize and ModifiedDate
                    
                    if (kvp.Value != null)
                    {
                        long lastWriteTicks = kvp.Value.ModifiedDate?.Ticks ?? 0;
                        
                        _cache[kvp.Key] = new CachedMetadata
                        {
                            Metadata = CloneMetadata(kvp.Value),
                            FileSize = kvp.Value.FileSize,
                            LastWriteTicks = lastWriteTicks
                        };
                    }
                }
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Gets the number of cached entries
        /// </summary>
        public int Count
        {
            get
            {
                _cacheLock.EnterReadLock();
                try
                {
                    return _cache.Count;
                }
                finally
                {
                    _cacheLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Clears all cached entries
        /// </summary>
        public void Clear()
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _cache.Clear();
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Gets cache statistics
        /// </summary>
        public (int hits, int misses, double hitRate) GetStatistics()
        {
            _cacheLock.EnterReadLock();
            try
            {
                var total = _cacheHits + _cacheMisses;
                var hitRate = total > 0 ? (_cacheHits * 100.0 / total) : 0;
                return (_cacheHits, _cacheMisses, hitRate);
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Updates content counters for a cached package
        /// </summary>
        public void UpdateContentCounters(string packageName, int morphCount, int hairCount, int clothingCount, int sceneCount, 
            int looksCount = 0, int posesCount = 0, int assetsCount = 0, int scriptsCount = 0, 
            int pluginsCount = 0, int subScenesCount = 0, int skinsCount = 0)
        {
            _cacheLock.EnterWriteLock();
            try
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
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Resets cache statistics
        /// </summary>
        public void ResetStatistics()
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _cacheHits = 0;
                _cacheMisses = 0;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
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
                _cacheLock.EnterWriteLock();
                try
                {
                    _cache.Clear();
                    _cacheHits = 0;
                    _cacheMisses = 0;
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
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
            // Use StringPool.Intern for frequently duplicated strings to reduce memory
            // Memory profiler showed 219MB wasted on duplicate strings
            var metadata = new VarMetadata
            {
                Filename = StringPool.Intern(reader.ReadString()),
                PackageName = StringPool.Intern(reader.ReadString()),
                CreatorName = StringPool.Intern(reader.ReadString()),
                Description = StringPool.Intern(reader.ReadString()),
                Version = reader.ReadInt32(),
                LicenseType = StringPool.InternIgnoreCase(reader.ReadString()),
                FileCount = reader.ReadInt32(),
                IsCorrupted = reader.ReadBoolean(),
                PreloadMorphs = reader.ReadBoolean(),
                Status = StringPool.InternIgnoreCase(reader.ReadString()),
                FilePath = StringPool.InternPath(reader.ReadString()),
                FileSize = reader.ReadInt64(),
                IsOptimized = reader.ReadBoolean(),
                HasTextureOptimization = reader.ReadBoolean(),
                HasHairOptimization = reader.ReadBoolean(),
                HasMirrorOptimization = reader.ReadBoolean(),
                VariantRole = StringPool.InternIgnoreCase(reader.ReadString()),
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

            // Read Dependencies list - intern package names (highly duplicated)
            var depCount = reader.ReadInt32();
            if (depCount > 0)
            {
                metadata.Dependencies = new string[depCount];
                for (int i = 0; i < depCount; i++)
                {
                    metadata.Dependencies[i] = StringPool.Intern(reader.ReadString());
                }
            }

            // ContentList is not stored in cache (loaded on-demand)
            // Still consume serialized payload to keep the reader aligned.
            try
            {
                var contentCount = reader.ReadInt32();
                for (int i = 0; i < contentCount; i++)
                {
                    reader.ReadString();
                }
            }
            catch
            {
                // Leave as null
            }

            // Read ContentTypes List - intern (small set of known values)
            var contentTypesCount = reader.ReadInt32();
            if (contentTypesCount > 0)
            {
                metadata.ContentTypes = new string[contentTypesCount];
                for (int i = 0; i < contentTypesCount; i++)
                {
                    metadata.ContentTypes[i] = StringPool.InternIgnoreCase(reader.ReadString());
                }
            }

            // Read Categories List - intern (small set of known values)
            var categoriesCount = reader.ReadInt32();
            if (categoriesCount > 0)
            {
                metadata.Categories = new string[categoriesCount];
                for (int i = 0; i < categoriesCount; i++)
                {
                    metadata.Categories[i] = StringPool.InternIgnoreCase(reader.ReadString());
                }
            }

            // Read UserTags list - intern (tags are often repeated)
            var userTagsCount = reader.ReadInt32();
            if (userTagsCount > 0)
            {
                metadata.UserTags = new string[userTagsCount];
                for (int i = 0; i < userTagsCount; i++)
                {
                    metadata.UserTags[i] = StringPool.Intern(reader.ReadString());
                }
            }

            // AllFiles is not stored in cache (loaded on-demand)
            try
            {
                var allFilesCount = reader.ReadInt32();
                for (int i = 0; i < allFilesCount; i++)
                {
                    reader.ReadString();
                }
            }
            catch
            {
                // Leave as null
            }

            // Read MissingDependencies list - intern package names
            try
            {
                var missingDepsCount = reader.ReadInt32();
                if (missingDepsCount > 0)
                {
                    metadata.MissingDependencies = new string[missingDepsCount];
                    for (int i = 0; i < missingDepsCount; i++)
                    {
                        metadata.MissingDependencies[i] = StringPool.Intern(reader.ReadString());
                    }
                }
            }
            catch
            {
                // Leave as null (lazy init)
            }

            // Read ClothingTags List - intern tags
            try
            {
                var clothingTagsCount = reader.ReadInt32();
                if (clothingTagsCount > 0)
                {
                    metadata.ClothingTags = new string[clothingTagsCount];
                    for (int i = 0; i < clothingTagsCount; i++)
                    {
                        metadata.ClothingTags[i] = StringPool.InternIgnoreCase(reader.ReadString());
                    }
                }
            }
            catch
            {
                // Leave as null (lazy init)
            }

            // Read HairTags List - intern tags
            try
            {
                var hairTagsCount = reader.ReadInt32();
                if (hairTagsCount > 0)
                {
                    metadata.HairTags = new string[hairTagsCount];
                    for (int i = 0; i < hairTagsCount; i++)
                    {
                        metadata.HairTags[i] = StringPool.InternIgnoreCase(reader.ReadString());
                    }
                }
            }
            catch
            {
                // Leave as null (lazy init)
            }
            
            // Trim excess capacity to reduce sparse array waste
            metadata.TrimExcess();

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
            var dependencies = metadata.Dependencies ?? Array.Empty<string>();
            writer.Write(dependencies.Length);
            foreach (var dep in dependencies)
            {
                writer.Write(dep ?? "");
            }

            // ContentList not stored in cache (loaded on-demand)
            writer.Write(0);

            // Write ContentTypes List
            var contentTypes = metadata.ContentTypes ?? Array.Empty<string>();
            writer.Write(contentTypes.Length);
            foreach (var type in contentTypes)
            {
                writer.Write(type ?? "");
            }

            // Write Categories List
            var categories = metadata.Categories ?? Array.Empty<string>();
            writer.Write(categories.Length);
            foreach (var category in categories)
            {
                writer.Write(category ?? "");
            }

            // Write UserTags list
            var userTags = metadata.UserTags ?? Array.Empty<string>();
            writer.Write(userTags.Length);
            foreach (var tag in userTags)
            {
                writer.Write(tag ?? "");
            }

            // AllFiles not stored in cache (loaded on-demand)
            writer.Write(0);

            // Write MissingDependencies list
            var missingDeps = metadata.MissingDependencies ?? Array.Empty<string>();
            writer.Write(missingDeps.Length);
            foreach (var dep in missingDeps)
            {
                writer.Write(dep ?? "");
            }

            // Write ClothingTags List
            var clothingTags = metadata.ClothingTags ?? Array.Empty<string>();
            writer.Write(clothingTags.Length);
            foreach (var tag in clothingTags)
            {
                writer.Write(tag ?? "");
            }

            // Write HairTags List
            var hairTags = metadata.HairTags ?? Array.Empty<string>();
            writer.Write(hairTags.Length);
            foreach (var tag in hairTags)
            {
                writer.Write(tag ?? "");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a deep clone of VarMetadata to prevent reference sharing.
        /// Optimized to avoid allocating empty collections (lazy init handles that).
        /// </summary>
        private VarMetadata CloneMetadata(VarMetadata source)
        {
            if (source == null) return null;

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
                SkinsCount = source.SkinsCount
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

        #endregion
        
        #region IDisposable
        
        private bool _disposed;
        
        /// <summary>
        /// Dispose resources.
        /// FIXED: ReaderWriterLockSlim was not being disposed.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _cacheLock?.Dispose();
        }
        
        #endregion
    }
}

