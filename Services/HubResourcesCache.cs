using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace VPM.Services
{
    /// <summary>
    /// High-performance binary cache for Hub resources (packages.json).
    /// Implements HTTP conditional requests (ETag/Last-Modified) for efficient incremental updates.
    /// Stores cached data in the same folder as other VPM caches for consistency.
    /// 
    /// Features:
    /// - Binary serialization for fast load/save (5-10x faster than JSON)
    /// - HTTP conditional requests to minimize bandwidth
    /// - Automatic cache validation and refresh
    /// - Thread-safe operations
    /// - Statistics tracking for monitoring
    /// </summary>
    public class HubResourcesCache : IDisposable
    {
        private const int CACHE_VERSION = 1;
        private const string CACHE_MAGIC = "VPMH"; // VPM Hub cache
        
        private readonly string _cacheDirectory;
        private readonly string _cacheFilePath;
        private readonly string _metadataFilePath;
        private readonly ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim();
        private readonly HttpClient _httpClient;
        private bool _disposed;
        
        // Cached data
        private Dictionary<string, string> _packageIdToResourceId;
        private Dictionary<string, int> _packageGroupToLatestVersion;
        private List<string> _cachedCreators;
        private DateTime _lastLoadTime = DateTime.MinValue;
        private bool _isLoaded = false;
        
        // HTTP caching metadata
        private string _etag;
        private DateTime _lastModified = DateTime.MinValue;
        private DateTime _cacheFileTime = DateTime.MinValue;
        
        // Statistics
        private int _cacheHits = 0;
        private int _cacheMisses = 0;
        private int _conditionalHits = 0; // 304 Not Modified responses
        private int _fullRefreshes = 0;   // Full downloads
        private long _bytesDownloaded = 0;
        private long _bytesSaved = 0;     // Bytes saved by conditional requests
        
        // Image caching
        private readonly string _imagesCacheFilePath;
        private readonly string _imagesCacheMetadataFilePath;
        private Dictionary<string, byte[]> _imageCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, DateTime> _imageCacheTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private int _imageHits = 0;
        private int _imageMisses = 0;
        private long _imageBytesDownloaded = 0;
        
        // Configuration
        private readonly TimeSpan _staleCheckInterval = TimeSpan.FromMinutes(30);
        private readonly TimeSpan _maxCacheAge = TimeSpan.FromDays(7);
        private readonly TimeSpan _imageExpiration = TimeSpan.FromDays(30); // Images expire after 30 days
        private const long MAX_IMAGE_CACHE_SIZE = 500 * 1024 * 1024; // 500MB max cache size
        private long _currentImageCacheSize = 0;
        
        public HubResourcesCache(HttpClient httpClient = null)
        {
            // Use AppData for cache storage (same folder as metadata and image caches)
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheDirectory = Path.Combine(appDataPath, "VPM", "Cache");
            _cacheFilePath = Path.Combine(_cacheDirectory, "HubResources.cache");
            _metadataFilePath = Path.Combine(_cacheDirectory, "HubResources.meta");
            _imagesCacheFilePath = Path.Combine(_cacheDirectory, "HubResourcesImages.cache");
            _imagesCacheMetadataFilePath = Path.Combine(_cacheDirectory, "HubResourcesImages.meta");
            
            _httpClient = httpClient;
            
            try
            {
                if (!Directory.Exists(_cacheDirectory))
                {
                    Directory.CreateDirectory(_cacheDirectory);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubResourcesCache] Failed to create cache directory: {ex}");
            }
            
            _packageIdToResourceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _packageGroupToLatestVersion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        
        #region Public Properties
        
        /// <summary>
        /// Gets whether the cache has been loaded
        /// </summary>
        public bool IsLoaded => _isLoaded;
        
        /// <summary>
        /// Gets the number of packages in the cache
        /// </summary>
        public int PackageCount
        {
            get
            {
                _cacheLock.EnterReadLock();
                try
                {
                    return _packageIdToResourceId?.Count ?? 0;
                }
                finally
                {
                    _cacheLock.ExitReadLock();
                }
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
        /// Gets the last time the cache was loaded
        /// </summary>
        public DateTime LastLoadTime => _lastLoadTime;
        
        /// <summary>
        /// Gets the ETag from the last successful fetch
        /// </summary>
        public string ETag => _etag;
        
        /// <summary>
        /// Gets the Last-Modified date from the last successful fetch
        /// </summary>
        public DateTime LastModified => _lastModified;
        
        #endregion
        
        #region Cache Loading
        
        /// <summary>
        /// Loads the cache from disk. Returns true if cache was loaded successfully.
        /// </summary>
        public bool LoadFromDisk(bool ignoreExpiration = false)
        {
            if (!File.Exists(_cacheFilePath))
            {
                return false;
            }
            
            try
            {
                var sw = Stopwatch.StartNew();
                
                // Load metadata first
                LoadMetadata();
                
                using var stream = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream);
                
                // Read and validate header
                var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (magic != CACHE_MAGIC)
                {
                    return false;
                }
                
                var version = reader.ReadInt32();
                if (version != CACHE_VERSION)
                {
                    return false;
                }
                
                // Read cache timestamp
                var cacheTicks = reader.ReadInt64();
                _cacheFileTime = new DateTime(cacheTicks);
                
                // Check if cache is too old
                if (!ignoreExpiration && DateTime.Now - _cacheFileTime > _maxCacheAge)
                {
                    return false;
                }
                
                // Read package count
                var packageCount = reader.ReadInt32();
                if (packageCount < 0 || packageCount > 500000) // Sanity check
                {
                    return false;
                }
                
                // Build dictionaries locally first to minimize lock time
                var newPackageIdToResourceId = new Dictionary<string, string>(packageCount, StringComparer.OrdinalIgnoreCase);
                var newPackageGroupToLatestVersion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                
                for (int i = 0; i < packageCount; i++)
                {
                    var packageName = reader.ReadString();
                    var resourceId = StringPool.Intern(reader.ReadString());
                    
                    newPackageIdToResourceId[packageName] = resourceId;
                    
                    // Build version index
                    var version2 = ExtractVersion(packageName);
                    var groupName = GetPackageGroupName(packageName);
                    
                    if (version2 >= 0 && !string.IsNullOrEmpty(groupName))
                    {
                        if (!newPackageGroupToLatestVersion.TryGetValue(groupName, out var currentLatest) || version2 > currentLatest)
                        {
                            newPackageGroupToLatestVersion[groupName] = version2;
                        }
                    }
                }

                // Pre-compute creators list while we have the data
                var creatorsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in newPackageIdToResourceId.Keys)
                {
                    var firstDot = name.IndexOf('.');
                    if (firstDot > 0)
                    {
                        var creator = name.Substring(0, firstDot);
                        creatorsSet.Add(creator);
                    }
                }
                var newCreators = creatorsSet.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
                
                _cacheLock.EnterWriteLock();
                try
                {
                    _packageIdToResourceId = newPackageIdToResourceId;
                    _packageGroupToLatestVersion = newPackageGroupToLatestVersion;
                    _cachedCreators = newCreators;
                    
                    _isLoaded = true;
                    _lastLoadTime = DateTime.Now;
                    _cacheHits++;
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
                
                sw.Stop();
                return true;
            }
            catch (Exception ex)
            {
                _cacheMisses++;
                Debug.WriteLine($"[HubResourcesCache] Failed to load cache from disk: {ex}");
                return false;
            }
        }
        
        /// <summary>
        /// Loads the cache asynchronously from disk
        /// </summary>
        public Task<bool> LoadFromDiskAsync(bool ignoreExpiration = false)
        {
            return Task.Run(() => LoadFromDisk(ignoreExpiration));
        }
        
        /// <summary>
        /// Loads HTTP caching metadata (ETag, Last-Modified) from disk
        /// </summary>
        private void LoadMetadata()
        {
            if (!File.Exists(_metadataFilePath))
                return;
            
            try
            {
                using var reader = new BinaryReader(File.OpenRead(_metadataFilePath));
                
                var version = reader.ReadInt32();
                if (version != 1)
                    return;
                
                _etag = reader.ReadString();
                if (string.IsNullOrEmpty(_etag))
                    _etag = null;
                
                var lastModTicks = reader.ReadInt64();
                _lastModified = lastModTicks > 0 ? new DateTime(lastModTicks) : DateTime.MinValue;
            }
            catch (Exception ex)
            {
                _etag = null;
                _lastModified = DateTime.MinValue;
                Debug.WriteLine($"[HubResourcesCache] Failed to load metadata from disk: {ex}");
            }
        }
        
        /// <summary>
        /// Saves HTTP caching metadata to disk
        /// </summary>
        private void SaveMetadata()
        {
            try
            {
                var tempPath = _metadataFilePath + ".tmp";
                
                using (var writer = new BinaryWriter(File.Create(tempPath)))
                {
                    writer.Write(1); // Version
                    writer.Write(_etag ?? "");
                    writer.Write(_lastModified.Ticks);
                }
                
                // Atomic replace
                try
                {
                    if (File.Exists(_metadataFilePath))
                        File.Delete(_metadataFilePath);
                    File.Move(tempPath, _metadataFilePath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HubResourcesCache] Failed to finalize metadata cache file write: {ex}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubResourcesCache] Failed to save metadata to disk: {ex}");
            }
        }
        
        #endregion
        
        #region Cache Saving
        
        /// <summary>
        /// Saves the current cache to disk
        /// </summary>
        public bool SaveToDisk()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var tempPath = _cacheFilePath + ".tmp";
                
                _cacheLock.EnterReadLock();
                try
                {
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new BinaryWriter(stream))
                    {
                        // Write header
                        writer.Write(Encoding.ASCII.GetBytes(CACHE_MAGIC));
                        writer.Write(CACHE_VERSION);
                        writer.Write(DateTime.Now.Ticks);
                        
                        // Write package count
                        writer.Write(_packageIdToResourceId.Count);
                        
                        // Write packages
                        foreach (var kvp in _packageIdToResourceId)
                        {
                            writer.Write(kvp.Key);
                            writer.Write(kvp.Value);
                        }
                        
                        writer.Flush();
                    }
                }
                finally
                {
                    _cacheLock.ExitReadLock();
                }
                
                // Atomic replace
                try
                {
                    if (File.Exists(_cacheFilePath))
                        File.Delete(_cacheFilePath);
                    File.Move(tempPath, _cacheFilePath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HubResourcesCache] Failed to finalize cache file write: {ex}");
                }
                
                // Save metadata
                SaveMetadata();
                
                sw.Stop();
                return true;
            }
            catch (Exception ex)
            {
                // Clean up temp file
                var tempPath = _cacheFilePath + ".tmp";
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (Exception ex2)
                {
                    Debug.WriteLine($"[HubResourcesCache] Failed to delete temp file '{tempPath}': {ex2}");
                }
                
                Debug.WriteLine($"[HubResourcesCache] Failed to save cache to disk: {ex}");
                return false;
            }
        }
        
        /// <summary>
        /// Saves the cache asynchronously
        /// </summary>
        public Task<bool> SaveToDiskAsync()
        {
            return Task.Run(() => SaveToDisk());
        }
        
        #endregion
        
        #region HTTP Fetching with Conditional Requests
        
        /// <summary>
        /// Fetches the packages.json from Hub, using conditional requests if possible.
        /// Returns true if data was updated (either from network or cache is still valid).
        /// </summary>
        public async Task<bool> FetchFromHubAsync(string packagesJsonUrl, CancellationToken cancellationToken = default)
        {
            if (_httpClient == null)
            {
                return false;
            }
            
            try
            {
                var sw = Stopwatch.StartNew();
                
                // Create request with conditional headers
                var request = new HttpRequestMessage(HttpMethod.Get, packagesJsonUrl);
                
                // Add conditional request headers if we have cached data
                if (!string.IsNullOrEmpty(_etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
                }
                
                if (_lastModified > DateTime.MinValue)
                {
                    request.Headers.IfModifiedSince = new DateTimeOffset(_lastModified);
                }
                
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                
                // Handle 304 Not Modified - cache is still valid
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    _conditionalHits++;
                    _lastLoadTime = DateTime.Now;
                    
                    // Update cache file time in memory to prevent immediate refresh loop
                    // This treats the confirmed valid data as "fresh"
                    _cacheFileTime = DateTime.Now;

                    // If not loaded (e.g. because it was expired), load it now!
                    if (!_isLoaded)
                    {
                        LoadFromDisk(ignoreExpiration: true);
                    }
                    
                    // Estimate bytes saved (typical packages.json is ~2-5MB)
                    _bytesSaved += _packageIdToResourceId.Count * 50; // Rough estimate
                    
                    sw.Stop();
                    return true;
                }
                
                response.EnsureSuccessStatusCode();
                
                // Read the response stream instead of loading full string into memory
                using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                
                // Build dictionaries locally first
                var newPackageIdToResourceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var newPackageGroupToLatestVersion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                
                // Use Task.Run for CPU-bound JSON parsing to avoid blocking UI thread if called from UI context
                var result = await Task.Run(() => 
                {
                    using var doc = JsonDocument.Parse(jsonStream);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var packageName = prop.Name.Replace(".var", "");
                        
                        // Handle both string and number resource IDs
                        string resourceId;
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            resourceId = StringPool.Intern(prop.Value.GetString());
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Number)
                        {
                            resourceId = StringPool.Intern(prop.Value.GetRawText());
                        }
                        else
                        {
                            resourceId = StringPool.Intern(prop.Value.ToString());
                        }
                        
                        newPackageIdToResourceId[packageName] = resourceId;
                        
                        // Build version index
                        var version = ExtractVersion(packageName);
                        var groupName = GetPackageGroupName(packageName);
                        
                        if (version >= 0 && !string.IsNullOrEmpty(groupName))
                        {
                            if (!newPackageGroupToLatestVersion.TryGetValue(groupName, out var currentLatest) || version > currentLatest)
                            {
                                newPackageGroupToLatestVersion[groupName] = version;
                            }
                        }
                    }
                    
                    // Pre-compute creators list
                    var creatorsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in newPackageIdToResourceId.Keys)
                    {
                        var firstDot = name.IndexOf('.');
                        if (firstDot > 0)
                        {
                            var creator = name.Substring(0, firstDot);
                            creatorsSet.Add(creator);
                        }
                    }
                    return (newPackageIdToResourceId, newPackageGroupToLatestVersion, creatorsSet.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList());
                }, cancellationToken);

                var (newPackageIdMap, newVersionMap, newCreatorsList) = result;

                _cacheLock.EnterWriteLock();
                try
                {
                    _packageIdToResourceId = newPackageIdMap;
                    _packageGroupToLatestVersion = newVersionMap;
                    _cachedCreators = newCreatorsList;
                    
                    _isLoaded = true;
                    _lastLoadTime = DateTime.Now;
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
                
                // Update HTTP caching metadata
                if (response.Headers.ETag != null)
                {
                    _etag = response.Headers.ETag.Tag;
                }
                
                if (response.Content.Headers.LastModified.HasValue)
                {
                    _lastModified = response.Content.Headers.LastModified.Value.UtcDateTime;
                }
                
                _fullRefreshes++;
                
                // Save to disk asynchronously
                _ = SaveToDiskAsync();
                
                sw.Stop();
                return true;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubResourcesCache] Failed to fetch from Hub: {ex}");
                return false;
            }
        }
        
        /// <summary>
        /// Checks if the cache needs to be refreshed based on staleness
        /// </summary>
        public bool NeedsRefresh()
        {
            if (!_isLoaded)
                return true;
            
            // If we haven't checked with server/loaded for a while
            if (DateTime.Now - _lastLoadTime > _staleCheckInterval)
                return true;
            
            // If the cache data itself is old, we should verify it
            if (DateTime.Now - _cacheFileTime > _staleCheckInterval)
                return true;
            
            return false;
        }
        
        #endregion
        
        #region Data Access
        
        /// <summary>
        /// Gets the resource ID for a package name
        /// </summary>
        public string GetResourceId(string packageName)
        {
            _cacheLock.EnterReadLock();
            try
            {
                if (_packageIdToResourceId == null)
                    return null;
                
                _packageIdToResourceId.TryGetValue(packageName.Replace(".var", ""), out var resourceId);
                return resourceId;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Gets the latest version for a package group
        /// </summary>
        public int GetLatestVersion(string packageGroupName)
        {
            _cacheLock.EnterReadLock();
            try
            {
                if (_packageGroupToLatestVersion == null)
                    return -1;
                
                if (_packageGroupToLatestVersion.TryGetValue(packageGroupName, out var latestVersion))
                    return latestVersion;
                
                return -1;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Checks if a package has an update available
        /// </summary>
        public bool HasUpdate(string packageGroupName, int localVersion)
        {
            var hubVersion = GetLatestVersion(packageGroupName);
            return hubVersion > localVersion;
        }
        
        /// <summary>
        /// Gets all unique creator names from the packages index
        /// </summary>
        public List<string> GetAllCreators()
        {
            _cacheLock.EnterReadLock();
            try
            {
                if (_cachedCreators != null)
                {
                    // Return a copy to be safe, or just the list if we treat it as immutable
                    return new List<string>(_cachedCreators);
                }

                if (_packageIdToResourceId == null || _packageIdToResourceId.Count == 0)
                    return new List<string>();
                
                var creators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var packageName in _packageIdToResourceId.Keys)
                {
                    var firstDot = packageName.IndexOf('.');
                    if (firstDot > 0)
                    {
                        var creator = packageName.Substring(0, firstDot);
                        creators.Add(creator);
                    }
                }
                
                var result = new List<string>(creators);
                result.Sort(StringComparer.OrdinalIgnoreCase);
                return result;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Gets the package ID to resource ID dictionary (for direct access by HubService)
        /// </summary>
        public Dictionary<string, string> GetPackageIdToResourceId()
        {
            _cacheLock.EnterReadLock();
            try
            {
                return new Dictionary<string, string>(_packageIdToResourceId, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Gets the package group to latest version dictionary (for direct access by HubService)
        /// </summary>
        public Dictionary<string, int> GetPackageGroupToLatestVersion()
        {
            _cacheLock.EnterReadLock();
            try
            {
                return new Dictionary<string, int>(_packageGroupToLatestVersion, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }
        
        #endregion
        
        #region Image Caching
        
        /// <summary>
        /// Tries to get a cached image by URL
        /// </summary>
        public BitmapImage TryGetCachedImage(string imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl))
                return null;
            
            _cacheLock.EnterReadLock();
            try
            {
                if (_imageCache.TryGetValue(imageUrl, out var imageData))
                {
                    _imageHits++;
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
                        bitmap.StreamSource = new MemoryStream(imageData);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[HubResourcesCache] Failed to decode cached image: {ex}");
                        return null;
                    }
                }
                
                _imageMisses++;
                return null;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Caches an image downloaded from Hub
        /// </summary>
        public bool CacheImage(string imageUrl, byte[] imageData)
        {
            if (string.IsNullOrEmpty(imageUrl) || imageData == null || imageData.Length == 0)
            {
                return false;
            }
            
            _cacheLock.EnterWriteLock();
            try
            {
                // Check if image already exists and remove its size from total
                if (_imageCache.TryGetValue(imageUrl, out var existingData))
                {
                    _currentImageCacheSize -= existingData.Length;
                }
                
                // Check if adding this image would exceed cache size limit
                if (_currentImageCacheSize + imageData.Length > MAX_IMAGE_CACHE_SIZE)
                {
                    // Evict oldest images until we have space
                    EvictOldestImages(imageData.Length);
                }
                
                _imageCache[imageUrl] = imageData;
                _imageCacheTimestamps[imageUrl] = DateTime.Now;
                _currentImageCacheSize += imageData.Length;
                _imageBytesDownloaded += imageData.Length;
                return true;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Evicts oldest images from cache to make room for new ones
        /// </summary>
        private void EvictOldestImages(long requiredSpace)
        {
            // Sort by timestamp and remove oldest images
            var sortedByAge = _imageCacheTimestamps
                .OrderBy(x => x.Value)
                .ToList();
            
            long freedSpace = 0;
            foreach (var kvp in sortedByAge)
            {
                if (freedSpace >= requiredSpace)
                    break;
                
                if (_imageCache.TryGetValue(kvp.Key, out var data))
                {
                    _currentImageCacheSize -= data.Length;
                    freedSpace += data.Length;
                    _imageCache.Remove(kvp.Key);
                    _imageCacheTimestamps.Remove(kvp.Key);
                }
            }
        }
        
        /// <summary>
        /// Removes expired images from cache
        /// </summary>
        private void RemoveExpiredImages()
        {
            var now = DateTime.Now;
            var expiredUrls = _imageCacheTimestamps
                .Where(x => now - x.Value > _imageExpiration)
                .Select(x => x.Key)
                .ToList();
            
            foreach (var url in expiredUrls)
            {
                if (_imageCache.TryGetValue(url, out var data))
                {
                    _currentImageCacheSize -= data.Length;
                    _imageCache.Remove(url);
                    _imageCacheTimestamps.Remove(url);
                }
            }
        }
        
        /// <summary>
        /// Loads the image cache from disk
        /// </summary>
        public bool LoadImageCacheFromDisk()
        {
            if (!File.Exists(_imagesCacheFilePath))
            {
                return false;
            }
            
            try
            {
                _cacheLock.EnterWriteLock();
                try
                {
                    // Load metadata first to get timestamps
                    LoadImageCacheMetadata();
                    
                    using var stream = new FileStream(_imagesCacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var reader = new BinaryReader(stream);
                    
                    // Read and validate header
                    var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    if (magic != "VPHI") // VPM Hub Images
                    {
                        return false;
                    }
                    
                    var version = reader.ReadInt32();
                    if (version != 1)
                    {
                        return false;
                    }
                    
                    // Read image count
                    var imageCount = reader.ReadInt32();
                    if (imageCount < 0 || imageCount > 100000) // Sanity check
                    {
                        return false;
                    }
                    
                    _imageCache = new Dictionary<string, byte[]>(imageCount, StringComparer.OrdinalIgnoreCase);
                    _currentImageCacheSize = 0;
                    var now = DateTime.Now;
                    var expiredCount = 0;
                    var skippedDueToSizeLimit = 0;
                    
                    for (int i = 0; i < imageCount; i++)
                    {
                        var url = reader.ReadString();
                        var dataLength = reader.ReadInt32();
                        
                        if (dataLength > 0 && dataLength < 50 * 1024 * 1024) // Max 50MB per image
                        {
                            // Check if image has expired
                            if (_imageCacheTimestamps.TryGetValue(url, out var timestamp))
                            {
                                if (now - timestamp > _imageExpiration)
                                {
                                    // Skip expired image
                                    reader.ReadBytes(dataLength);
                                    expiredCount++;
                                    continue;
                                }
                            }
                            
                            // CRITICAL FIX: Enforce MAX_IMAGE_CACHE_SIZE during load
                            // This prevents loading GBs of cached images into memory
                            if (_currentImageCacheSize + dataLength > MAX_IMAGE_CACHE_SIZE)
                            {
                                // Skip remaining images - we've hit the memory limit
                                reader.ReadBytes(dataLength);
                                skippedDueToSizeLimit++;
                                continue;
                            }
                            
                            var imageData = reader.ReadBytes(dataLength);
                            _imageCache[url] = imageData;
                            _currentImageCacheSize += imageData.Length;
                            
                            // Ensure timestamp exists
                            if (!_imageCacheTimestamps.ContainsKey(url))
                            {
                                _imageCacheTimestamps[url] = now;
                            }
                        }
                    }
                    
                    return true;
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubResourcesCache] Failed to load image cache from disk: {ex}");
                return false;
            }
        }
        
        /// <summary>
        /// Loads image cache metadata (timestamps)
        /// </summary>
        private void LoadImageCacheMetadata()
        {
            if (!File.Exists(_imagesCacheMetadataFilePath))
                return;
            
            try
            {
                using var reader = new BinaryReader(File.OpenRead(_imagesCacheMetadataFilePath));
                
                var version = reader.ReadInt32();
                if (version != 1)
                    return;
                
                var count = reader.ReadInt32();
                _imageCacheTimestamps = new Dictionary<string, DateTime>(count, StringComparer.OrdinalIgnoreCase);
                
                for (int i = 0; i < count; i++)
                {
                    var url = reader.ReadString();
                    var ticks = reader.ReadInt64();
                    _imageCacheTimestamps[url] = new DateTime(ticks);
                }
            }
            catch (Exception ex)
            {
                _imageCacheTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                Debug.WriteLine($"[HubResourcesCache] Failed to load image metadata from disk: {ex}");
            }
        }
        
        /// <summary>
        /// Saves image cache metadata (timestamps)
        /// </summary>
        private void SaveImageCacheMetadata()
        {
            try
            {
                var tempPath = _imagesCacheMetadataFilePath + ".tmp";
                
                // Copy timestamps under lock to avoid blocking during disk I/O
                List<KeyValuePair<string, DateTime>> timestampsToSave;
                _cacheLock.EnterReadLock();
                try
                {
                    timestampsToSave = _imageCacheTimestamps.ToList();
                }
                finally
                {
                    _cacheLock.ExitReadLock();
                }
                
                // Perform disk I/O outside the lock
                using (var writer = new BinaryWriter(File.Create(tempPath)))
                {
                    writer.Write(1); // Version
                    writer.Write(timestampsToSave.Count);
                    
                    foreach (var kvp in timestampsToSave)
                    {
                        writer.Write(kvp.Key);
                        writer.Write(kvp.Value.Ticks);
                    }
                }
                
                // Atomic replace
                try
                {
                    if (File.Exists(_imagesCacheMetadataFilePath))
                        File.Delete(_imagesCacheMetadataFilePath);
                    File.Move(tempPath, _imagesCacheMetadataFilePath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HubResourcesCache] Failed to finalize image metadata cache file write: {ex}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubResourcesCache] Failed to save image metadata to disk: {ex}");
            }
        }
        
        /// <summary>
        /// Saves the image cache to disk
        /// </summary>
        public bool SaveImageCacheToDisk()
        {
            try
            {
                var tempPath = _imagesCacheFilePath + ".tmp";
                
                // Copy data under lock to avoid blocking UI during disk I/O
                List<KeyValuePair<string, byte[]>> imagesToSave;
                _cacheLock.EnterReadLock();
                try
                {
                    // Create a snapshot of the cache data
                    imagesToSave = _imageCache.ToList();
                }
                finally
                {
                    _cacheLock.ExitReadLock();
                }
                
                // Perform disk I/O outside the lock to prevent UI blocking
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    // Write header
                    writer.Write(Encoding.ASCII.GetBytes("VPHI"));
                    writer.Write(1); // Version
                    writer.Write(imagesToSave.Count);
                    
                    // Write images
                    foreach (var kvp in imagesToSave)
                    {
                        writer.Write(kvp.Key);
                        writer.Write(kvp.Value.Length);
                        writer.Write(kvp.Value);
                    }
                    
                    writer.Flush();
                }
                
                // Atomic replace
                if (File.Exists(_imagesCacheFilePath))
                {
                    File.Delete(_imagesCacheFilePath);
                }
                
                File.Move(tempPath, _imagesCacheFilePath);
                
                // Save metadata
                SaveImageCacheMetadata();
                
                return true;
            }
            catch (Exception)
            {
                // Clean up temp file
                try
                {
                    var tempPath = _imagesCacheFilePath + ".tmp";
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HubResourcesCache] Failed to delete temp file '{_imagesCacheFilePath}.tmp': {ex}");
                }
                
                return false;
            }
        }
        
        /// <summary>
        /// Clears all cached images
        /// </summary>
        public void ClearImageCache()
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _imageCache.Clear();
                _imageCacheTimestamps.Clear();
                _currentImageCacheSize = 0;
                _imageHits = 0;
                _imageMisses = 0;
                _imageBytesDownloaded = 0;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
            
            // Delete image cache files
            try
            {
                if (File.Exists(_imagesCacheFilePath))
                    File.Delete(_imagesCacheFilePath);
                if (File.Exists(_imagesCacheMetadataFilePath))
                    File.Delete(_imagesCacheMetadataFilePath);
            }
            catch (Exception ex) { Debug.WriteLine($"[HubResourcesCache] Failed to clear image cache files: {ex}"); }
        }
        
        #endregion
        
        #region Statistics
        
        /// <summary>
        /// Gets cache statistics
        /// </summary>
        public HubResourcesCacheStats GetStatistics()
        {
            _cacheLock.EnterReadLock();
            try
            {
                var cacheSize = 0L;
                var imageCacheSize = 0L;
                try
                {
                    if (File.Exists(_cacheFilePath))
                        cacheSize = new FileInfo(_cacheFilePath).Length;
                    if (File.Exists(_imagesCacheFilePath))
                        imageCacheSize = new FileInfo(_imagesCacheFilePath).Length;
                }
                catch (Exception ex) { Debug.WriteLine($"[HubResourcesCache] Failed to compute cache statistics: {ex}"); }
                
                return new HubResourcesCacheStats
                {
                    PackageCount = _packageIdToResourceId?.Count ?? 0,
                    CacheHits = _cacheHits,
                    CacheMisses = _cacheMisses,
                    ConditionalHits = _conditionalHits,
                    FullRefreshes = _fullRefreshes,
                    BytesDownloaded = _bytesDownloaded,
                    BytesSaved = _bytesSaved,
                    CacheSizeBytes = cacheSize,
                    ImageCount = _imageCache?.Count ?? 0,
                    ImageHits = _imageHits,
                    ImageMisses = _imageMisses,
                    ImageBytesDownloaded = _imageBytesDownloaded,
                    ImageCacheSizeBytes = imageCacheSize,
                    LastLoadTime = _lastLoadTime,
                    LastModified = _lastModified,
                    ETag = _etag
                };
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Resets statistics counters
        /// </summary>
        public void ResetStatistics()
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _cacheHits = 0;
                _cacheMisses = 0;
                _conditionalHits = 0;
                _fullRefreshes = 0;
                _bytesDownloaded = 0;
                _bytesSaved = 0;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }
        
        #endregion
        
        #region Cache Management
        
        /// <summary>
        /// Clears the cache from memory and disk
        /// </summary>
        public bool ClearCache()
        {
            try
            {
                _cacheLock.EnterWriteLock();
                try
                {
                    _packageIdToResourceId.Clear();
                    _packageGroupToLatestVersion.Clear();
                    _cachedCreators = null;
                    _imageCache.Clear();
                    _isLoaded = false;
                    _lastLoadTime = DateTime.MinValue;
                    _etag = null;
                    _lastModified = DateTime.MinValue;
                    _cacheFileTime = DateTime.MinValue;
                    
                    ResetStatistics();
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
                
                // Delete cache files
                if (File.Exists(_cacheFilePath))
                    File.Delete(_cacheFilePath);
                
                if (File.Exists(_metadataFilePath))
                    File.Delete(_metadataFilePath);
                
                if (File.Exists(_imagesCacheFilePath))
                    File.Delete(_imagesCacheFilePath);
                
                if (File.Exists(_imagesCacheMetadataFilePath))
                    File.Delete(_imagesCacheMetadataFilePath);
                
                // Clean up temp files
                try
                {
                    var tempFiles = Directory.GetFiles(_cacheDirectory, "HubResources*.tmp");
                    foreach (var file in tempFiles)
                    {
                        try { File.Delete(file); } catch (Exception ex) { Debug.WriteLine($"[HubResourcesCache] Failed to delete temp file '{file}': {ex}"); }
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[HubResourcesCache] Failed to cleanup temp files: {ex}"); }
                
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        
        /// <summary>
        /// Gets the total size of the cache files in bytes
        /// </summary>
        public long GetCacheSize()
        {
            try
            {
                long size = 0;
                
                if (File.Exists(_cacheFilePath))
                    size += new FileInfo(_cacheFilePath).Length;
                
                if (File.Exists(_metadataFilePath))
                    size += new FileInfo(_metadataFilePath).Length;
                
                return size;
            }
            catch
            {
                return 0;
            }
        }
        
        #endregion
        
        #region Helper Methods
        
        private static int ExtractVersion(string packageName)
        {
            var nameSpan = packageName.AsSpan();
            
            if (nameSpan.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                nameSpan = nameSpan.Slice(0, nameSpan.Length - 4);
            
            if (nameSpan.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                return -1;
            
            var lastDot = nameSpan.LastIndexOf('.');
            if (lastDot > 0)
            {
                var afterDot = nameSpan.Slice(lastDot + 1);
                if (int.TryParse(afterDot, out var version))
                    return version;
            }
            
            return -1;
        }
        
        private static string GetPackageGroupName(string packageName)
        {
            var nameSpan = packageName.AsSpan();
            
            if (nameSpan.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                nameSpan = nameSpan.Slice(0, nameSpan.Length - 4);
            
            if (nameSpan.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                nameSpan = nameSpan.Slice(0, nameSpan.Length - 7);
            
            var lastDot = nameSpan.LastIndexOf('.');
            if (lastDot > 0)
            {
                var afterDot = nameSpan.Slice(lastDot + 1);
                if (int.TryParse(afterDot, out _))
                    return StringPool.Intern(nameSpan.Slice(0, lastDot).ToString());
            }
            
            return StringPool.Intern(nameSpan.ToString());
        }
        
        #endregion
        
        #region IDisposable
        
        public void Dispose()
        {
            if (!_disposed)
            {
                _cacheLock?.Dispose();
                _disposed = true;
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// Statistics for the Hub resources cache
    /// </summary>
    public class HubResourcesCacheStats
    {
        public int PackageCount { get; set; }
        public int CacheHits { get; set; }
        public int CacheMisses { get; set; }
        public int ConditionalHits { get; set; }  // 304 Not Modified responses
        public int FullRefreshes { get; set; }    // Full downloads
        public long BytesDownloaded { get; set; }
        public long BytesSaved { get; set; }      // Bytes saved by conditional requests
        public long CacheSizeBytes { get; set; }
        public int ImageCount { get; set; }
        public int ImageHits { get; set; }
        public int ImageMisses { get; set; }
        public long ImageBytesDownloaded { get; set; }
        public long ImageCacheSizeBytes { get; set; }
        public DateTime LastLoadTime { get; set; }
        public DateTime LastModified { get; set; }
        public string ETag { get; set; }
        
        /// <summary>
        /// Gets the hit rate percentage
        /// </summary>
        public double HitRate
        {
            get
            {
                var total = CacheHits + CacheMisses;
                return total > 0 ? (CacheHits * 100.0 / total) : 0;
            }
        }
        
        /// <summary>
        /// Gets the conditional hit rate (304 responses vs full downloads)
        /// </summary>
        public double ConditionalHitRate
        {
            get
            {
                var total = ConditionalHits + FullRefreshes;
                return total > 0 ? (ConditionalHits * 100.0 / total) : 0;
            }
        }
        
        /// <summary>
        /// Gets formatted cache size
        /// </summary>
        public string CacheSizeFormatted
        {
            get
            {
                var bytes = CacheSizeBytes;
                if (bytes <= 0) return "0 B";
                
                string[] sizes = { "B", "KB", "MB", "GB" };
                int order = 0;
                double size = bytes;
                while (size >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    size /= 1024;
                }
                return $"{size:0.##} {sizes[order]}";
            }
        }
        
        /// <summary>
        /// Gets formatted bytes downloaded
        /// </summary>
        public string BytesDownloadedFormatted
        {
            get
            {
                var bytes = BytesDownloaded;
                if (bytes <= 0) return "0 B";
                
                string[] sizes = { "B", "KB", "MB", "GB" };
                int order = 0;
                double size = bytes;
                while (size >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    size /= 1024;
                }
                return $"{size:0.##} {sizes[order]}";
            }
        }
        
        /// <summary>
        /// Gets formatted bytes saved
        /// </summary>
        public string BytesSavedFormatted
        {
            get
            {
                var bytes = BytesSaved;
                if (bytes <= 0) return "0 B";
                
                string[] sizes = { "B", "KB", "MB", "GB" };
                int order = 0;
                double size = bytes;
                while (size >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    size /= 1024;
                }
                return $"{size:0.##} {sizes[order]}";
            }
        }
    }
}
