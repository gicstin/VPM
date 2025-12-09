using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using Microsoft.IO;

namespace VPM.Services
{
    /// <summary>
    /// Disk-based image cache for fast retrieval without extracting from VAR files.
    /// Uses LAZY LOADING: only loads index at startup, reads image bytes on-demand from disk.
    /// This dramatically reduces memory usage (from GBs to MBs).
    /// Stores images in encrypted binary container files for privacy.
    /// </summary>
    public class ImageDiskCache
    {
        private readonly string _cacheDirectory;
        private readonly object _cacheLock = new();
        private readonly byte[] _encryptionKey;
        
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
        
        // Track invalid cache entries to prevent reload loops
        // Key format: "packageKey::internalPath"
        private readonly HashSet<string> _invalidEntries = new();
        
        // Minimum image dimension to consider valid (rejects 80x80 EXIF thumbnails)
        private const int MinValidImageSize = 100;
        
        // Save throttling
        private bool _saveInProgress = false;
        private bool _savePending = false;
        
        // Cache file format version (2 = lazy loading with offsets)
        private const int CACHE_VERSION = 2;

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
            
            // Generate machine-specific encryption key (not stored, derived from machine ID)
            _encryptionKey = GenerateMachineKey();
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
        /// Generates a machine-specific encryption key
        /// </summary>
        private byte[] GenerateMachineKey()
        {
            // Use machine name + user name as seed for consistent key per machine/user
            var seed = $"{Environment.MachineName}|{Environment.UserName}|VPM_ImageCache_v1";
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(Encoding.UTF8.GetBytes(seed));
        }

        /// <summary>
        /// Generates an obfuscated cache key for a package
        /// </summary>
        private string GetPackageCacheKey(string varPath, long fileSize, long lastWriteTicks)
        {
            // Hash the entire signature to obfuscate package identity
            var signature = $"{varPath}|{fileSize}|{lastWriteTicks}";
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(signature));
            return BitConverter.ToString(hash).Replace("-", "");
        }

        /// <summary>
        /// Index entry for a single image - stores file offset and size, NOT the bytes
        /// </summary>
        private class ImageIndexEntry
        {
            public long FileOffset { get; set; }  // Position in cache file
            public int DataLength { get; set; }   // Length of encrypted data
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
                    
                    byte[] encryptedData = null;
                    
                    // 1. Check LRU memory cache first (fastest)
                    if (_memoryLruCache.TryGetValue(cacheKey, out encryptedData))
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
                             pendingCache.Images.TryGetValue(internalPath, out encryptedData))
                    {
                        // Found in pending writes, add to LRU cache
                        AddToMemoryLruCache(cacheKey, encryptedData);
                    }
                    // 3. Read from disk on-demand using index
                    else if (_indexCache.TryGetValue(packageKey, out var packageIndex) &&
                             packageIndex.ImageOffsets.TryGetValue(internalPath, out var indexEntry))
                    {
                        // Read from disk - release lock during I/O
                        encryptedData = ReadImageFromDisk(indexEntry.FileOffset, indexEntry.DataLength);
                        
                        if (encryptedData != null)
                        {
                            // Add to LRU cache for faster subsequent access
                            AddToMemoryLruCache(cacheKey, encryptedData);
                        }
                    }
                    
                    if (encryptedData == null)
                    {
                        _cacheMisses++;
                        return null;
                    }

                    // Decrypt and load image
                    var decryptedData = Decrypt(encryptedData);
                    
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    
                    // Use a non-pooled MemoryStream for BitmapImage to avoid disposal issues
                    var stream = new MemoryStream(decryptedData);
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
                    _totalBytesRead += decryptedData.Length;

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
        /// Reads encrypted image data from disk at the specified offset
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
        /// Adds encrypted image data to the LRU memory cache with eviction
        /// </summary>
        private void AddToMemoryLruCache(string cacheKey, byte[] encryptedData)
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
            _memoryLruCache[cacheKey] = encryptedData;
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
                        
                        byte[] encryptedData = null;
                        
                        // 1. Check LRU memory cache
                        if (_memoryLruCache.TryGetValue(cacheKey, out encryptedData))
                        {
                            // Move to front of LRU
                            if (_memoryLruNodes.TryGetValue(cacheKey, out var node))
                            {
                                _memoryLruOrder.Remove(node);
                                _memoryLruOrder.AddFirst(node);
                            }
                        }
                        // 2. Check pending writes
                        else if (hasPending && pendingCache.Images.TryGetValue(internalPath, out encryptedData))
                        {
                            AddToMemoryLruCache(cacheKey, encryptedData);
                        }
                        // 3. Read from disk using index
                        else if (hasIndex && packageIndex.ImageOffsets.TryGetValue(internalPath, out var indexEntry))
                        {
                            encryptedData = ReadImageFromDisk(indexEntry.FileOffset, indexEntry.DataLength);
                            if (encryptedData != null)
                            {
                                AddToMemoryLruCache(cacheKey, encryptedData);
                            }
                        }
                        
                        if (encryptedData == null)
                        {
                            uncached.Add(internalPath);
                            _cacheMisses++;
                            continue;
                        }
                        
                        try
                        {
                            // Decrypt and load image
                            var decryptedData = Decrypt(encryptedData);
                            
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                            
                            // Use non-pooled MemoryStream for BitmapImage
                            var stream = new MemoryStream(decryptedData);
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
                            _totalBytesRead += decryptedData.Length;
                        }
                        catch
                        {
                            // Decryption/loading failed, treat as uncached
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

                    // Encrypt the image data
                    var encryptedData = Encrypt(imageData);
                    
                    // Add to pending writes
                    if (!_pendingWrites.TryGetValue(packageKey, out var pendingCache))
                    {
                        pendingCache = new PackageImageCache();
                        _pendingWrites[packageKey] = pendingCache;
                    }
                    
                    pendingCache.Images[internalPath] = encryptedData;
                    if (!pendingCache.ImagePaths.Contains(internalPath))
                    {
                        pendingCache.ImagePaths.Add(internalPath);
                    }
                    
                    // Also add to LRU cache for immediate access
                    AddToMemoryLruCache(cacheKey, encryptedData);

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
        /// Encrypts data using AES
        /// </summary>
        private byte[] Encrypt(byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            using var msEncrypt = new MemoryStream();
            
            // Write IV first (needed for decryption)
            msEncrypt.Write(aes.IV, 0, aes.IV.Length);
            
            using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
            {
                csEncrypt.Write(data, 0, data.Length);
            }

            return msEncrypt.ToArray();
        }

        /// <summary>
        /// Decrypts data using AES
        /// </summary>
        private byte[] Decrypt(byte[] encryptedData)
        {
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;

            // Read IV from beginning
            var iv = new byte[aes.IV.Length];
            Array.Copy(encryptedData, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var msDecrypt = new MemoryStream(encryptedData, iv.Length, encryptedData.Length - iv.Length);
            using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
            using var msPlain = new MemoryStream();
            
            csDecrypt.CopyTo(msPlain);
            return msPlain.ToArray();
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
                
                // Handle version 1 (old format) - migrate by reading all data
                if (version == 1)
                {
                    LoadCacheIndexFromV1(reader);
                    // Trigger save to upgrade to v2 format
                    TriggerAsyncSave();
                    return;
                }
                
                // Version 2: Lazy loading format with offsets
                if (version != CACHE_VERSION)
                {
                    return;
                }

                var packageCount = reader.ReadInt32();

                for (int i = 0; i < packageCount; i++)
                {
                    // Read package key (hashed)
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
        /// Migrates from v1 format (all data in memory) to v2 format (lazy loading).
        /// Reads all image data and stores in pending writes for re-save.
        /// </summary>
        private void LoadCacheIndexFromV1(BinaryReader reader)
        {
            try
            {
                var packageCount = reader.ReadInt32();

                for (int i = 0; i < packageCount; i++)
                {
                    // Read package key (hashed)
                    var keyLength = reader.ReadInt32();
                    var keyBytes = reader.ReadBytes(keyLength);
                    var packageKey = Encoding.UTF8.GetString(keyBytes);

                    // Read image count for this package
                    var imageCount = reader.ReadInt32();
                    var pendingCache = new PackageImageCache();

                    for (int j = 0; j < imageCount; j++)
                    {
                        // Read image path
                        var pathLength = reader.ReadInt32();
                        var pathBytes = reader.ReadBytes(pathLength);
                        var imagePath = Encoding.UTF8.GetString(pathBytes);

                        // Read encrypted image data (v1 stores data inline)
                        var dataLength = reader.ReadInt32();
                        var imageData = reader.ReadBytes(dataLength);

                        pendingCache.Images[imagePath] = imageData;
                        pendingCache.ImagePaths.Add(imagePath);
                    }

                    // Store in pending writes for migration to v2
                    _pendingWrites[packageKey] = pendingCache;
                }
            }
            catch (Exception)
            {
                _pendingWrites.Clear();
            }
        }

        /// <summary>
        /// Saves the cache database to disk atomically with v2 format (lazy loading with offsets).
        /// Merges pending writes with existing index data.
        /// </summary>
        private void SaveCacheDatabase()
        {
            try
            {
                var tempPath = _cacheFilePath + ".tmp";
                
                // Collect all data to write (merge index + pending writes)
                Dictionary<string, PackageImageCache> allData;
                
                lock (_cacheLock)
                {
                    allData = new Dictionary<string, PackageImageCache>();
                    
                    // First, read existing data from disk for packages in index
                    foreach (var kvp in _indexCache)
                    {
                        var packageKey = kvp.Key;
                        var packageIndex = kvp.Value;
                        var cache = new PackageImageCache();
                        
                        foreach (var imgKvp in packageIndex.ImageOffsets)
                        {
                            var imagePath = imgKvp.Key;
                            var indexEntry = imgKvp.Value;
                            
                            // Read data from disk
                            var data = ReadImageFromDisk(indexEntry.FileOffset, indexEntry.DataLength);
                            if (data != null)
                            {
                                cache.Images[imagePath] = data;
                                cache.ImagePaths.Add(imagePath);
                            }
                        }
                        
                        allData[packageKey] = cache;
                    }
                    
                    // Merge pending writes (overwrites existing)
                    foreach (var kvp in _pendingWrites)
                    {
                        var packageKey = kvp.Key;
                        var pendingCache = kvp.Value;
                        
                        if (!allData.TryGetValue(packageKey, out var cache))
                        {
                            cache = new PackageImageCache();
                            allData[packageKey] = cache;
                        }
                        
                        foreach (var imgKvp in pendingCache.Images)
                        {
                            cache.Images[imgKvp.Key] = imgKvp.Value;
                            if (!cache.ImagePaths.Contains(imgKvp.Key))
                            {
                                cache.ImagePaths.Add(imgKvp.Key);
                            }
                        }
                    }
                }

                // Write to temp file with v2 format
                // First pass: write header and index with placeholder offsets
                // Second pass: write image data and update offsets
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    // Write header
                    writer.Write(0x56504D49); // "VPMI" magic
                    writer.Write(CACHE_VERSION); // Version 2
                    writer.Write(allData.Count);

                    // Track where we'll write image data (after all index entries)
                    var indexEntries = new List<(string packageKey, string imagePath, long offsetPosition)>();
                    
                    // First pass: write index structure with placeholder offsets
                    foreach (var package in allData)
                    {
                        // Write package key (hashed)
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
                    foreach (var package in allData)
                    {
                        foreach (var image in package.Value.Images)
                        {
                            var entry = indexEntries[entryIndex++];
                            var dataOffset = stream.Position;
                            
                            // Write encrypted image data
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
    }
}

