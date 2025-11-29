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
    /// Disk-based image cache for fast retrieval without extracting from VAR files
    /// Stores images in encrypted binary container files (one per package) for privacy
    /// Based on _VB project's ImageCache strategy with enhanced security
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
        private readonly Dictionary<string, PackageImageCache> _memoryCache = new();
        
        // Save throttling
        private bool _saveInProgress = false;
        private bool _savePending = false;

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
            
            // Don't load cache database here - it will be loaded asynchronously
            // to avoid blocking the UI thread during startup
            
            // Console.WriteLine($"[ImageDiskCache] Cache location: {_cacheFilePath}");
        }
        
        /// <summary>
        /// Loads the cache database asynchronously
        /// Call this after UI initialization to avoid blocking startup
        /// </summary>
        public async Task LoadCacheDatabaseAsync()
        {
            await Task.Run(() => LoadCacheDatabase());
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
        /// Container for all images from one package
        /// </summary>
        private class PackageImageCache
        {
            public Dictionary<string, byte[]> Images { get; set; } = new Dictionary<string, byte[]>();
            public List<string> ImagePaths { get; set; } = new List<string>(); // For quick index lookup
        }

        /// <summary>
        /// Tries to load an image from disk cache
        /// Returns null if not cached or invalid
        /// </summary>
        public BitmapImage TryGetCached(string varPath, string internalPath, long fileSize, long lastWriteTicks)
        {
            try
            {
                var packageKey = GetPackageCacheKey(varPath, fileSize, lastWriteTicks);

                lock (_cacheLock)
                {
                    if (!_memoryCache.TryGetValue(packageKey, out var packageCache))
                    {
                        _cacheMisses++;
                        return null;
                    }

                    if (!packageCache.Images.TryGetValue(internalPath, out var encryptedData))
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
                    
                    // Use a non-pooled MemoryStream for BitmapImage to avoid disposal issues in .NET 10
                    // BitmapImage holds a reference to the stream, so we can't use a pooled stream
                    var stream = new MemoryStream(decryptedData);
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    
                    bitmap.Freeze();

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
        /// Gets cached image paths for a package (avoids opening VAR to scan)
        /// </summary>
        public List<string> GetCachedImagePaths(string varPath, long fileSize, long lastWriteTicks)
        {
            try
            {
                var packageKey = GetPackageCacheKey(varPath, fileSize, lastWriteTicks);

                lock (_cacheLock)
                {
                    if (_memoryCache.TryGetValue(packageKey, out var packageCache))
                    {
                        return new List<string>(packageCache.ImagePaths);
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
        /// MEDIUM PRIORITY FIX 5: Batch lookup for multiple images from same VAR
        /// Returns dictionary of found images and list of uncached paths
        /// Reduces I/O operations by batching lookups instead of sequential checks
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
                    if (!_memoryCache.TryGetValue(packageKey, out var packageCache))
                    {
                        // Package not cached, all paths are uncached
                        uncached.AddRange(internalPaths);
                        _cacheMisses += internalPaths.Count;
                        return (cached, uncached);
                    }

                    // Check each image in batch
                    foreach (var internalPath in internalPaths)
                    {
                        if (packageCache.Images.TryGetValue(internalPath, out var encryptedData))
                        {
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
                        else
                        {
                            // Image not in cache
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
        /// Saves a BitmapImage to disk cache (encrypted database)
        /// </summary>
        public bool TrySaveToCache(string varPath, string internalPath, long fileSize, long lastWriteTicks, BitmapImage bitmap)
        {
            try
            {
                var packageKey = GetPackageCacheKey(varPath, fileSize, lastWriteTicks);

                lock (_cacheLock)
                {
                    // Get or create package cache
                    if (!_memoryCache.TryGetValue(packageKey, out var packageCache))
                    {
                        packageCache = new PackageImageCache();
                        _memoryCache[packageKey] = packageCache;
                    }

                    // Check if image already cached
                    if (packageCache.Images.ContainsKey(internalPath))
                    {
                        return true;
                    }

                    // Encode as JPEG with quality 90
                    var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));

                    using var memoryStream = new MemoryStream();
                    encoder.Save(memoryStream);
                    var imageData = memoryStream.ToArray();

                    // Encrypt and add to cache
                    var encryptedData = Encrypt(imageData);
                    packageCache.Images[internalPath] = encryptedData;
                    
                    // Maintain image paths list for index
                    if (!packageCache.ImagePaths.Contains(internalPath))
                    {
                        packageCache.ImagePaths.Add(internalPath);
                    }

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

            Task.Run(() =>
            {
                SaveCacheDatabase();

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
        /// Loads the cache database from disk into memory
        /// </summary>
        private void LoadCacheDatabase()
        {
            try
            {
                if (!File.Exists(_cacheFilePath))
                {
                    return;
                }

                using var reader = new BinaryReader(File.OpenRead(_cacheFilePath));
                
                // Read header
                var magic = reader.ReadInt32();
                if (magic != 0x56504D49) // "VPMI" (VPM Images)
                {
                    return;
                }

                var version = reader.ReadInt32();
                if (version != 1)
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
                    var packageCache = new PackageImageCache();

                    for (int j = 0; j < imageCount; j++)
                    {
                        // Read image path
                        var pathLength = reader.ReadInt32();
                        var pathBytes = reader.ReadBytes(pathLength);
                        var imagePath = Encoding.UTF8.GetString(pathBytes);

                        // Read encrypted image data
                        var dataLength = reader.ReadInt32();
                        var imageData = reader.ReadBytes(dataLength);

                        packageCache.Images[imagePath] = imageData;
                        packageCache.ImagePaths.Add(imagePath); // Populate paths list
                    }

                    _memoryCache[packageKey] = packageCache;
                }

                Console.WriteLine($"[ImageDiskCache] Loaded {packageCount} packages with {_memoryCache.Sum(p => p.Value.Images.Count)} images");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImageDiskCache] Error loading database: {ex.Message}");
                _memoryCache.Clear();
            }
        }

        /// <summary>
        /// Saves the cache database to disk atomically
        /// </summary>
        private void SaveCacheDatabase()
        {
            try
            {
                var tempPath = _cacheFilePath + ".tmp";

                lock (_cacheLock)
                {
                    using (var writer = new BinaryWriter(File.Create(tempPath)))
                    {
                        // Write header
                        writer.Write(0x56504D49); // "VPMI" magic
                        writer.Write(1); // Version
                        writer.Write(_memoryCache.Count);

                        foreach (var package in _memoryCache)
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

                                // Write encrypted image data
                                writer.Write(image.Value.Length);
                                writer.Write(image.Value);
                            }
                        }
                    }
                }

                // Atomic replace
                if (File.Exists(_cacheFilePath))
                {
                    File.Delete(_cacheFilePath);
                }
                File.Move(tempPath, _cacheFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImageDiskCache] Error saving database: {ex.Message}");
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
                    _memoryCache.Clear();
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

                Console.WriteLine($"[ImageDiskCache] Cache cleared");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImageDiskCache] Error clearing cache: {ex.Message}");
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
                
                var imageCount = _memoryCache.Sum(p => p.Value.Images.Count);
                
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
    }
}

