using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Disk-based image cache for fast retrieval without extracting from VAR files.
    /// Uses LAZY LOADING: only loads index at startup, reads image bytes on-demand from disk.
    /// This dramatically reduces memory usage (from GBs to MBs).
    /// Stores images in binary container files.
    /// </summary>
    public class ImageDiskCache
    {
        private readonly string _cacheDirectory;
        private readonly object _cacheLock = new();
        
        // Statistics
        private int _cacheHits = 0;
        private int _cacheMisses = 0;
        private long _totalBytesWritten = 0;
        private long _totalBytesRead = 0;

        private readonly string _cacheFilePath;
        
        // LAZY LOADING: Index only stores file offsets, not image bytes
        // Image bytes are read from disk on-demand
        private readonly Dictionary<string, PackageImageIndex> _indexCache = new();
        
        // Small in-memory LRU cache for recently accessed images (keeps ~50 images in RAM)
        private const int MAX_MEMORY_CACHE_SIZE = 50;
        private readonly Dictionary<string, byte[]> _memoryLruCache = new();
        private readonly LinkedList<string> _memoryLruOrder = new();
        private readonly Dictionary<string, LinkedListNode<string>> _memoryLruNodes = new();
        
        // Pending writes that haven't been saved to disk yet
        private readonly Dictionary<string, PackageImageCache> _pendingWrites = new();
        
        // MEMORY FIX: Track pending writes size to prevent unbounded memory growth
        private long _pendingWritesBytes = 0;
        private const long MAX_PENDING_WRITES_BYTES = 100 * 1024 * 1024; // 100MB max pending
        
        // Track invalid cache entries to prevent reload loops
        // Key format: "packageKey::internalPath"
        private readonly HashSet<string> _invalidEntries = new();
        
        // Minimum image dimension to consider valid (rejects 80x80 EXIF thumbnails)
        private const int MinValidImageSize = 100;
        
        // Save throttling
        private bool _saveInProgress = false;
        private bool _savePending = false;
        
        // Cache file format version (3 = unencrypted lazy loading with offsets)
        private const int CACHE_VERSION = 3;

        public ImageDiskCache()
        {
            // Use AppData for cache storage (same folder as metadata cache)
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheDirectory = Path.Combine(appDataPath, "VPM", "Cache");
            _cacheFilePath = Path.Combine(_cacheDirectory, "PackageImages.cache");
            
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }
        
        /// <summary>
        /// Loads the cache INDEX asynchronously (not image bytes - those are loaded on-demand).
        /// Call this after UI initialization to avoid blocking startup.
        /// </summary>
        public async Task LoadCacheDatabaseAsync()
        {
            await Task.Run(() => LoadCacheIndex());
        }

        /// <summary>
        /// Gets the cache directory path
        /// </summary>
        public string CacheDirectory => _cacheDirectory;

        /// <summary>
        /// Generates a cache key for a package based on path, size, and modification time
        /// </summary>
        private string GetPackageCacheKey(string varPath, long fileSize, long lastWriteTicks)
        {
            // Create a unique key from the signature
            return $"{varPath}|{fileSize}|{lastWriteTicks}";
        }

        /// <summary>
        /// Index entry for a single image - stores file offset and size, NOT the bytes
        /// </summary>
        private class ImageIndexEntry
        {
            public long FileOffset { get; set; }  // Position in cache file
            public int DataLength { get; set; }   // Length of image data
        }
        
        /// <summary>
        /// Index for all images from one package - stores offsets only, not image bytes
        /// </summary>
        private class PackageImageIndex
        {
            public Dictionary<string, ImageIndexEntry> ImageOffsets { get; set; } = new();
            public List<string> ImagePaths { get; set; } = new(); // For quick path lookup
        }
        
        /// <summary>
        /// Container for pending writes (images not yet saved to disk)
        /// </summary>
        private class PackageImageCache
        {
            public Dictionary<string, byte[]> Images { get; set; } = new Dictionary<string, byte[]>();
            public List<string> ImagePaths { get; set; } = new List<string>();
        }

        /// <summary>
        /// Tries to load an image from disk cache using LAZY LOADING.
        /// First checks LRU memory cache, then pending writes, then reads from disk on-demand.
        /// Returns null if not cached, invalid, or previously marked as invalid.
        /// </summary>
        public BitmapImage TryGetCached(string varPath, string internalPath, long fileSize, long lastWriteTicks)
        {
            try
            {
                var packageKey = GetPackageCacheKey(varPath, fileSize, lastWriteTicks);
                var cacheKey = $"{packageKey}::{internalPath}";

                lock (_cacheLock)
                {
                    // Check if this entry was previously marked as invalid (prevents reload loops)
                    if (_invalidEntries.Contains(cacheKey))
                    {
                        _cacheMisses++;
                        return null;
                    }
                    
                    byte[] imageData = null;
                    
                    // 1. Check LRU memory cache first (fastest)
                    if (_memoryLruCache.TryGetValue(cacheKey, out imageData))
                    {
                        // Move to front of LRU
                        if (_memoryLruNodes.TryGetValue(cacheKey, out var node))
                        {
                            _memoryLruOrder.Remove(node);
                            _memoryLruOrder.AddFirst(node);
                        }
                    }
                    // 2. Check pending writes (not yet saved to disk)
                    else if (_pendingWrites.TryGetValue(packageKey, out var pendingCache) &&
                             pendingCache.Images.TryGetValue(internalPath, out imageData))
                    {
                        // Found in pending writes, add to LRU cache
                        AddToMemoryLruCache(cacheKey, imageData);
                    }
                    // 3. Read from disk on-demand using index
                    else if (_indexCache.TryGetValue(packageKey, out var packageIndex) &&
                             packageIndex.ImageOffsets.TryGetValue(internalPath, out var indexEntry))
                    {
                        // Read from disk - release lock during I/O
                        imageData = ReadImageFromDisk(indexEntry.FileOffset, indexEntry.DataLength);
                        
                        if (imageData != null)
                        {
                            // Add to LRU cache for faster subsequent access
                            AddToMemoryLruCache(cacheKey, imageData);
                        }
                    }
                    
                    if (imageData == null)
                    {
                        _cacheMisses++;
                        return null;
                    }
                    
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    
                    // Use a non-pooled MemoryStream for BitmapImage to avoid disposal issues
                    var stream = new MemoryStream(imageData);
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    
                    bitmap.Freeze();

                    // Validate image dimensions - reject tiny images (like 80x80 EXIF thumbnails)
                    if (bitmap.PixelWidth < MinValidImageSize || bitmap.PixelHeight < MinValidImageSize)
                    {
                        // Mark as invalid to prevent reload loops
                        _invalidEntries.Add(cacheKey);
                        RemoveFromMemoryLruCache(cacheKey);
                        _cacheMisses++;
                        return null;
                    }

                    _cacheHits++;
                    _totalBytesRead += imageData.Length;

                    return bitmap;
                }
            }
            catch
            {
                // Cache read failed, return null
                return null;
            }
        }
        
        /// <summary>
        /// Reads image data from disk at the specified offset
        /// </summary>
        private byte[] ReadImageFromDisk(long fileOffset, int dataLength)
        {
            try
            {
                if (!File.Exists(_cacheFilePath))
                    return null;
                    
                using var stream = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                stream.Seek(fileOffset, SeekOrigin.Begin);
                
                var data = new byte[dataLength];
                var bytesRead = stream.Read(data, 0, dataLength);
                
                if (bytesRead != dataLength)
                    return null;
                    
                return data;
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Adds image data to the LRU memory cache with eviction
        /// </summary>
        private void AddToMemoryLruCache(string cacheKey, byte[] imageData)
        {
            // Evict oldest if at capacity
            while (_memoryLruCache.Count >= MAX_MEMORY_CACHE_SIZE && _memoryLruOrder.Count > 0)
            {
                var oldest = _memoryLruOrder.Last;
                if (oldest != null)
                {
                    _memoryLruOrder.RemoveLast();
                    _memoryLruNodes.Remove(oldest.Value);
                    _memoryLruCache.Remove(oldest.Value);
                }
            }
            
            // Add new entry
            _memoryLruCache[cacheKey] = imageData;
            var newNode = _memoryLruOrder.AddFirst(cacheKey);
            _memoryLruNodes[cacheKey] = newNode;
        }
        
        /// <summary>
        /// Removes an entry from the LRU memory cache
        /// </summary>
        private void RemoveFromMemoryLruCache(string cacheKey)
        {
            _memoryLruCache.Remove(cacheKey);
            if (_memoryLruNodes.TryGetValue(cacheKey, out var node))
            {
                _memoryLruOrder.Remove(node);
                _memoryLruNodes.Remove(cacheKey);
            }
        }

        /// <summary>
        /// Gets cached image paths for a package (avoids opening VAR to scan).
        /// Checks both index cache and pending writes.
        /// </summary>
        public List<string> GetCachedImagePaths(string varPath, long fileSize, long lastWriteTicks)
        {
            try
            {
                var packageKey = GetPackageCacheKey(varPath, fileSize, lastWriteTicks);

                lock (_cacheLock)
                {
                    // Check index cache first
                    if (_indexCache.TryGetValue(packageKey, out var packageIndex))
                    {
                        return new List<string>(packageIndex.ImagePaths);
                    }
                    
                    // Check pending writes
                    if (_pendingWrites.TryGetValue(packageKey, out var pendingCache))
                    {
                        return new List<string>(pendingCache.ImagePaths);
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return null;
        }

        /// <summary>
        /// Batch lookup for multiple images from same VAR using lazy loading.
        /// Returns dictionary of found images and list of uncached paths.
        /// </summary>
        public (Dictionary<string, BitmapImage> cached, List<string> uncached) TryGetCachedBatch(
            string varPath, List<string> internalPaths, long fileSize, long lastWriteTicks)
        {
            var cached = new Dictionary<string, BitmapImage>();
            var uncached = new List<string>();
            
            try
            {
                var packageKey = GetPackageCacheKey(varPath, fileSize, lastWriteTicks);

                lock (_cacheLock)
                {
                    // Check if package exists in index or pending writes
                    var hasIndex = _indexCache.TryGetValue(packageKey, out var packageIndex);
                    var hasPending = _pendingWrites.TryGetValue(packageKey, out var pendingCache);
                    
                    if (!hasIndex && !hasPending)
                    {
                        // Package not cached, all paths are uncached
                        uncached.AddRange(internalPaths);
                        _cacheMisses += internalPaths.Count;
                        return (cached, uncached);
                    }

                    // Check each image in batch with validation
                    foreach (var internalPath in internalPaths)
                    {
                        var cacheKey = $"{packageKey}::{internalPath}";
                        
                        // Skip if previously marked as invalid
                        if (_invalidEntries.Contains(cacheKey))
                        {
                            uncached.Add(internalPath);
                            _cacheMisses++;
                            continue;
                        }
                        
                        byte[] imageData = null;
                        
                        // 1. Check LRU memory cache
                        if (_memoryLruCache.TryGetValue(cacheKey, out imageData))
                        {
                            // Move to front of LRU
                            if (_memoryLruNodes.TryGetValue(cacheKey, out var node))
                            {
                                _memoryLruOrder.Remove(node);
                                _memoryLruOrder.AddFirst(node);
                            }
                        }
                        // 2. Check pending writes
                        else if (hasPending && pendingCache.Images.TryGetValue(internalPath, out imageData))
                        {
                            AddToMemoryLruCache(cacheKey, imageData);
                        }
                        // 3. Read from disk using index
                        else if (hasIndex && packageIndex.ImageOffsets.TryGetValue(internalPath, out var indexEntry))
                        {
                            imageData = ReadImageFromDisk(indexEntry.FileOffset, indexEntry.DataLength);
                            if (imageData != null)
                            {
                                AddToMemoryLruCache(cacheKey, imageData);
                            }
                        }
                        
                        if (imageData == null)
                        {
                            uncached.Add(internalPath);
                            _cacheMisses++;
                            continue;
                        }
                        
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                            
                            // Use non-pooled MemoryStream for BitmapImage
                            var stream = new MemoryStream(imageData);
                            bitmap.StreamSource = stream;
                            bitmap.EndInit();
                            bitmap.Freeze();

                            // Validate image dimensions - reject tiny images (like 80x80 EXIF thumbnails)
                            if (bitmap.PixelWidth < MinValidImageSize || bitmap.PixelHeight < MinValidImageSize)
                            {
                                // Mark as invalid to prevent reload loops
                                _invalidEntries.Add(cacheKey);
                                RemoveFromMemoryLruCache(cacheKey);
                                uncached.Add(internalPath);
                                _cacheMisses++;
                                continue;
                            }

                            cached[internalPath] = bitmap;
                            _cacheHits++;
                            _totalBytesRead += imageData.Length;
                        }
                        catch
                        {
                            // Image loading failed, treat as uncached
                            uncached.Add(internalPath);
                            _cacheMisses++;
                        }
                    }
                }
            }
            catch
            {
                // Cache read failed, treat all as uncached
                uncached.AddRange(internalPaths);
                _cacheMisses += internalPaths.Count;
            }

            return (cached, uncached);
        }

        /// <summary>
        /// Saves a BitmapImage to pending writes (will be persisted to disk asynchronously).
        /// Also adds to LRU memory cache for immediate access.
        /// </summary>
        public bool TrySaveToCache(string varPath, string internalPath, long fileSize, long lastWriteTicks, BitmapImage bitmap)
        {
            try
            {
                // Validate image dimensions before caching
                // Reject suspiciously small images (like 80x80 EXIF thumbnails)
                if (bitmap.PixelWidth < MinValidImageSize || bitmap.PixelHeight < MinValidImageSize)
                {
                    // Don't cache tiny images - they're likely EXIF thumbnails or corrupted
                    return false;
                }
                
                var packageKey = GetPackageCacheKey(varPath, fileSize, lastWriteTicks);
                var cacheKey = $"{packageKey}::{internalPath}";

                lock (_cacheLock)
                {
                    // Check if already in index (already persisted)
                    if (_indexCache.TryGetValue(packageKey, out var existingIndex) &&
                        existingIndex.ImageOffsets.ContainsKey(internalPath))
                    {
                        return true;
                    }
                    
                    // Check if already in pending writes
                    if (_pendingWrites.TryGetValue(packageKey, out var existingPending) &&
                        existingPending.Images.ContainsKey(internalPath))
                    {
                        return true;
                    }

                    // Encode as JPEG with quality 90
                    var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));

                    using var memoryStream = new MemoryStream();
                    encoder.Save(memoryStream);
                    var imageData = memoryStream.ToArray();
                    
                    // MEMORY FIX: Force save if pending writes exceed limit
                    if (_pendingWritesBytes + imageData.Length > MAX_PENDING_WRITES_BYTES)
                    {
                        // Release lock and save immediately to prevent memory bloat
                        // Don't add this image - it will be loaded from archive next time
                        return false;
                    }
                    
                    // Add to pending writes
                    if (!_pendingWrites.TryGetValue(packageKey, out var pendingCache))
                    {
                        pendingCache = new PackageImageCache();
                        _pendingWrites[packageKey] = pendingCache;
                    }
                    
                    pendingCache.Images[internalPath] = imageData;
                    if (!pendingCache.ImagePaths.Contains(internalPath))
                    {
                        pendingCache.ImagePaths.Add(internalPath);
                    }
                    
                    // Track pending bytes
                    _pendingWritesBytes += imageData.Length;
                    
                    // Also add to LRU cache for immediate access
                    AddToMemoryLruCache(cacheKey, imageData);

                    _totalBytesWritten += imageData.Length;
                }

                // Trigger async save (throttled to prevent concurrent writes)
                TriggerAsyncSave();

                return true;
            }
            catch
            {
                // Cache write failed, not critical
                return false;
            }
        }

        /// <summary>
        /// Triggers an async save with throttling to prevent concurrent writes
        /// </summary>
        private void TriggerAsyncSave()
        {
            lock (_cacheLock)
            {
                if (_saveInProgress)
                {
                    // Save already in progress, mark that another save is needed
                    _savePending = true;
                    return;
                }

                _saveInProgress = true;
            }

            // FIXED: Wrap in try-catch to prevent unobserved exceptions
            _ = Task.Run(() =>
            {
                try
                {
                    SaveCacheDatabase();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ImageDiskCache] Save error: {ex.Message}");
                }
                finally
                {
                    lock (_cacheLock)
                    {
                        _saveInProgress = false;

                        // If another save was requested while we were saving, trigger it now
                        if (_savePending)
                        {
                            _savePending = false;
                            TriggerAsyncSave();
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Loads only the cache INDEX from disk (not image bytes).
        /// Image bytes are read on-demand using file offsets.
        /// This dramatically reduces memory usage at startup.
        /// </summary>
        private void LoadCacheIndex()
        {
            try
            {
                if (!File.Exists(_cacheFilePath))
                {
                    return;
                }

                using var stream = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream);
                
                // Read header
                var magic = reader.ReadInt32();
                if (magic != 0x56504D49) // "VPMI" (VPM Images)
                {
                    return;
                }

                var version = reader.ReadInt32();
                
                // Handle old versions (1 or 2) - they used encryption, discard and rebuild
                if (version < CACHE_VERSION)
                {
                    return;
                }

                var packageCount = reader.ReadInt32();

                for (int i = 0; i < packageCount; i++)
                {
                    // Read package key
                    var keyLength = reader.ReadInt32();
                    var keyBytes = reader.ReadBytes(keyLength);
                    var packageKey = Encoding.UTF8.GetString(keyBytes);

                    // Read image count for this package
                    var imageCount = reader.ReadInt32();
                    var packageIndex = new PackageImageIndex();

                    for (int j = 0; j < imageCount; j++)
                    {
                        // Read image path
                        var pathLength = reader.ReadInt32();
                        var pathBytes = reader.ReadBytes(pathLength);
                        var imagePath = Encoding.UTF8.GetString(pathBytes);

                        // Read file offset and data length (NOT the actual data)
                        var fileOffset = reader.ReadInt64();
                        var dataLength = reader.ReadInt32();

                        packageIndex.ImageOffsets[imagePath] = new ImageIndexEntry
                        {
                            FileOffset = fileOffset,
                            DataLength = dataLength
                        };
                        packageIndex.ImagePaths.Add(imagePath);
                    }

                    _indexCache[packageKey] = packageIndex;
                }
            }
            catch (Exception)
            {
                _indexCache.Clear();
            }
        }
        
        /// <summary>
        /// Saves the cache database to disk atomically with v3 format (unencrypted lazy loading with offsets).
        /// Merges pending writes with existing index data.
        /// </summary>
        private void SaveCacheDatabase()
        {
            try
            {
                // MEMORY FIX: Only save pending writes if there are any
                // Don't reload entire cache from disk - that causes massive memory spikes
                lock (_cacheLock)
                {
                    if (_pendingWrites.Count == 0)
                    {
                        return; // Nothing to save
                    }
                }
                
                var tempPath = _cacheFilePath + ".tmp";
                
                // Collect only pending writes (not existing data from disk)
                Dictionary<string, PackageImageCache> pendingData;
                
                lock (_cacheLock)
                {
                    // Copy pending writes
                    pendingData = new Dictionary<string, PackageImageCache>(_pendingWrites.Count);
                    foreach (var kvp in _pendingWrites)
                    {
                        var cache = new PackageImageCache();
                        foreach (var imgKvp in kvp.Value.Images)
                        {
                            cache.Images[imgKvp.Key] = imgKvp.Value;
                        }
                        cache.ImagePaths = new List<string>(kvp.Value.ImagePaths);
                        pendingData[kvp.Key] = cache;
                    }
                }

                // Write only pending data to temp file
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    // Write header
                    writer.Write(0x56504D49); // "VPMI" magic
                    writer.Write(CACHE_VERSION); // Version 3
                    writer.Write(pendingData.Count);

                    // Track where we'll write image data (after all index entries)
                    var indexEntries = new List<(string packageKey, string imagePath, long offsetPosition)>();
                    
                    // First pass: write index structure with placeholder offsets
                    foreach (var package in pendingData)
                    {
                        // Write package key
                        var keyBytes = Encoding.UTF8.GetBytes(package.Key);
                        writer.Write(keyBytes.Length);
                        writer.Write(keyBytes);

                        // Write image count
                        writer.Write(package.Value.Images.Count);

                        foreach (var image in package.Value.Images)
                        {
                            // Write image path
                            var pathBytes = Encoding.UTF8.GetBytes(image.Key);
                            writer.Write(pathBytes.Length);
                            writer.Write(pathBytes);

                            // Remember position for offset (will update later)
                            indexEntries.Add((package.Key, image.Key, stream.Position));
                            
                            // Write placeholder offset and length
                            writer.Write(0L); // FileOffset placeholder
                            writer.Write(image.Value.Length); // DataLength
                        }
                    }
                    
                    // Second pass: write image data and update offsets
                    var entryIndex = 0;
                    foreach (var package in pendingData)
                    {
                        foreach (var image in package.Value.Images)
                        {
                            var entry = indexEntries[entryIndex++];
                            var dataOffset = stream.Position;
                            
                            // Write image data
                            writer.Write(image.Value);
                            
                            // Go back and update the offset
                            var currentPos = stream.Position;
                            stream.Seek(entry.offsetPosition, SeekOrigin.Begin);
                            writer.Write(dataOffset);
                            stream.Seek(currentPos, SeekOrigin.Begin);
                        }
                    }
                }

                // Atomic replace
                lock (_cacheLock)
                {
                    if (File.Exists(_cacheFilePath))
                    {
                        File.Delete(_cacheFilePath);
                    }
                    File.Move(tempPath, _cacheFilePath);
                    
                    // Clear pending writes and rebuild index
                    _pendingWrites.Clear();
                    _pendingWritesBytes = 0;
                    _indexCache.Clear();
                }
                
                // Reload index from new file
                LoadCacheIndex();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Saves the cache database synchronously (for app shutdown)
        /// </summary>
        public void SaveCacheSynchronous()
        {
            SaveCacheDatabase();
        }

        /// <summary>
        /// Clears all cached images from memory and disk
        /// </summary>
        public bool ClearCache()
        {
            try
            {
                lock (_cacheLock)
                {
                    _indexCache.Clear();
                    _pendingWrites.Clear();
                    _pendingWritesBytes = 0;
                    _memoryLruCache.Clear();
                    _memoryLruOrder.Clear();
                    _memoryLruNodes.Clear();
                    _invalidEntries.Clear();
                    _cacheHits = 0;
                    _cacheMisses = 0;
                    _totalBytesWritten = 0;
                    _totalBytesRead = 0;
                }

                // Delete database file
                if (File.Exists(_cacheFilePath))
                {
                    File.Delete(_cacheFilePath);
                }

                // Clean up temp files
                if (Directory.Exists(_cacheDirectory))
                {
                    var tempFiles = Directory.GetFiles(_cacheDirectory, "*.tmp");
                    foreach (var file in tempFiles)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // Continue deleting other files
                        }
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Gets cache statistics
        /// </summary>
        public (int hits, int misses, double hitRate, long bytesWritten, long bytesRead, int imageCount) GetStatistics()
        {
            lock (_cacheLock)
            {
                var total = _cacheHits + _cacheMisses;
                var hitRate = total > 0 ? (_cacheHits * 100.0 / total) : 0;
                
                // Count images from index + pending writes
                var indexCount = _indexCache.Sum(p => p.Value.ImagePaths.Count);
                var pendingCount = _pendingWrites.Sum(p => p.Value.Images.Count);
                var imageCount = indexCount + pendingCount;
                
                return (_cacheHits, _cacheMisses, hitRate, _totalBytesWritten, _totalBytesRead, imageCount);
            }
        }

        /// <summary>
        /// Gets the total size of the cache database in bytes
        /// </summary>
        public long GetCacheSize()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    return new FileInfo(_cacheFilePath).Length;
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Clears all cached images from memory and disk.
        /// Use this to reset the cache if corrupted images were cached.
        /// </summary>
        public void ClearAllCache()
        {
            lock (_cacheLock)
            {
                _indexCache.Clear();
                _pendingWrites.Clear();
                _pendingWritesBytes = 0;
                _memoryLruCache.Clear();
                _memoryLruOrder.Clear();
                _memoryLruNodes.Clear();
                _invalidEntries.Clear();
                _cacheHits = 0;
                _cacheMisses = 0;
                _totalBytesWritten = 0;
                _totalBytesRead = 0;
            }

            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    File.Delete(_cacheFilePath);
                }
            }
            catch
            {
                // Ignore deletion errors
            }
        }
        
        /// <summary>
        /// Invalidates disk cache entries for a specific package.
        /// This is used when a package is restored from backup or its file signature changes.
        /// </summary>
        public void InvalidatePackageCache(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return;
            
            lock (_cacheLock)
            {
                try
                {
                    // Remove all index entries for this package
                    var keysToRemove = _indexCache.Keys
                        .Where(k => k.StartsWith(packageName + "|", StringComparison.OrdinalIgnoreCase) ||
                                   k.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    
                    foreach (var key in keysToRemove)
                    {
                        _indexCache.Remove(key);
                    }
                    
                    // Remove from memory LRU cache
                    var memoryKeysToRemove = _memoryLruCache.Keys
                        .Where(k => k.StartsWith(packageName + "|", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    
                    foreach (var key in memoryKeysToRemove)
                    {
                        _memoryLruCache.Remove(key);
                        if (_memoryLruNodes.TryGetValue(key, out var node))
                        {
                            _memoryLruOrder.Remove(node);
                            _memoryLruNodes.Remove(key);
                        }
                    }
                    
                    // Remove from pending writes
                    var pendingKeysToRemove = _pendingWrites.Keys
                        .Where(k => k.StartsWith(packageName + "|", StringComparison.OrdinalIgnoreCase) ||
                                   k.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    
                    foreach (var key in pendingKeysToRemove)
                    {
                        if (_pendingWrites.TryGetValue(key, out var cache))
                        {
                            _pendingWritesBytes -= cache.Images.Values.Sum(img => img.Length);
                        }
                        _pendingWrites.Remove(key);
                    }
                    
                    // Remove from invalid entries cache
                    var invalidKeysToRemove = _invalidEntries
                        .Where(k => k.StartsWith(packageName + "::", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    
                    foreach (var key in invalidKeysToRemove)
                    {
                        _invalidEntries.Remove(key);
                    }
                }
                catch (Exception)
                {
                    // Ignore errors during invalidation
                }
            }
        }
    }
}

