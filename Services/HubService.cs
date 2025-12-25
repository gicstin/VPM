using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using VPM.Models;

namespace VPM.Services
{
    public sealed class ServiceResult<T>
    {
        public bool Success { get; }
        public T Value { get; }
        public string ErrorMessage { get; }
        public Exception Exception { get; }

        private ServiceResult(bool success, T value, string errorMessage, Exception exception)
        {
            Success = success;
            Value = value;
            ErrorMessage = errorMessage;
            Exception = exception;
        }

        public static ServiceResult<T> Ok(T value) => new ServiceResult<T>(true, value, null, null);

        public static ServiceResult<T> Fail(string errorMessage, Exception exception = null) =>
            new ServiceResult<T>(false, default, errorMessage, exception);
    }

    /// <summary>
    /// Service for interacting with the VaM Hub API
    /// Adapted from var_browser's HubBrowse implementation
    /// </summary>
    public class HubService : IDisposable
    {
        private const string ApiUrl = "https://hub.virtamate.com/citizenx/api.php";
        private const string PackagesJsonUrl = "https://s3cdn.virtamate.com/data/packages.json";
        private const string CookieHost = "hub.virtamate.com";

        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _requestThrottle = new SemaphoreSlim(4, 4); // Allow up to 4 concurrent API requests
        private bool _disposed;
        
        // Performance monitoring
        public readonly PerformanceMonitor PerformanceMonitor = new PerformanceMonitor();
        
        // API Response Caching
        private HubFilterOptions _cachedFilterOptions;
        private DateTime _filterOptionsCacheTime = DateTime.MinValue;
        private readonly TimeSpan _filterOptionsCacheExpiry = TimeSpan.FromHours(1); // Filter options rarely change
        
        // Resource detail cache (LRU-style with max size)
        private readonly Dictionary<string, (HubResourceDetail Detail, DateTime CacheTime)> _resourceDetailCache = new();
        private readonly int _resourceDetailCacheMaxSize = 100;
        private readonly TimeSpan _resourceDetailCacheExpiry = TimeSpan.FromMinutes(10);
        
        // Search result cache
        private readonly Dictionary<string, (HubSearchResponse Response, DateTime CacheTime)> _searchCache = new();
        private readonly int _searchCacheMaxSize = 20;
        private readonly TimeSpan _searchCacheExpiry = TimeSpan.FromMinutes(5);
        private readonly object _searchCacheLock = new object();

        private readonly HubSearchCache _hubSearchCache;

        // Binary cache for packages.json with HTTP conditional request support
        private readonly HubResourcesCache _hubResourcesCache;
        private bool _cacheInitialized = false;
        
        // In-memory cache references (populated from HubResourcesCache)
        private Dictionary<string, string> _packageIdToResourceId;
        private Dictionary<string, int> _packageGroupToLatestVersion;
        private DateTime _packagesCacheTime = DateTime.MinValue;
        private readonly TimeSpan _packagesCacheExpiry = TimeSpan.FromMinutes(30); // Reduced since we use conditional requests

        // Download queue management
        private readonly Queue<QueuedDownload> _downloadQueue = new Queue<QueuedDownload>();
        private readonly object _downloadQueueLock = new object();
        private bool _isDownloading = false;

        // Events
        public event EventHandler<HubDownloadProgress> DownloadProgressChanged;
        public event EventHandler<string> StatusChanged;
        public event EventHandler<QueuedDownload> DownloadQueued;
        public event EventHandler<QueuedDownload> DownloadStarted;
        public event EventHandler<QueuedDownload> DownloadCompleted;
        /// <summary>
        /// Fired when all queued downloads have been processed (queue is empty)
        /// </summary>
        public event EventHandler AllDownloadsCompleted;

        private readonly string _cacheDirectory;
        private readonly string _resourceDetailCacheDirectory;
        private readonly object _resourceDetailDiskCacheLock = new object();
        private readonly TimeSpan _resourceDetailDiskCacheTtl = TimeSpan.FromDays(7);

        public HubService()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };

            // Add the Hub consent cookie
            handler.CookieContainer.Add(new Uri($"https://{CookieHost}"), 
                new Cookie("vamhubconsent", "1", "/", CookieHost));

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30)
            };

            _httpClient.DefaultRequestHeaders.Add("User-Agent", "VPM/1.0");
            
            // Initialize the binary cache for Hub resources
            _hubResourcesCache = new HubResourcesCache(_httpClient);

            _hubSearchCache = new HubSearchCache(ttl: TimeSpan.FromMinutes(10), maxEntries: 200);

            _cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPM", "Cache");
            _resourceDetailCacheDirectory = Path.Combine(_cacheDirectory, "HubResourceDetails");
            try
            {
                if (!Directory.Exists(_resourceDetailCacheDirectory))
                    Directory.CreateDirectory(_resourceDetailCacheDirectory);
            }
            catch (Exception ex)
            {
            }
        }

        private string GetResourceDetailCachePath(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
                return null;

            var safe = new string(cacheKey.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
            return Path.Combine(_resourceDetailCacheDirectory, safe + ".json");
        }

        private bool TryLoadResourceDetailFromDisk(string cacheKey, out string json)
        {
            json = null;
            try
            {
                var path = GetResourceDetailCachePath(cacheKey);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;

                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (DateTime.UtcNow - lastWrite > _resourceDetailDiskCacheTtl)
                    return false;

                lock (_resourceDetailDiskCacheLock)
                {
                    json = File.ReadAllText(path, Encoding.UTF8);
                }
                return !string.IsNullOrWhiteSpace(json);
            }
            catch
            {
                return false;
            }
        }

        private void SaveResourceDetailToDisk(string cacheKey, string json)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                var path = GetResourceDetailCachePath(cacheKey);
                if (string.IsNullOrEmpty(path))
                    return;

                lock (_resourceDetailDiskCacheLock)
                {
                    File.WriteAllText(path, json, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
            }
        }

        #region Search & Browse

        /// <summary>
        /// Get available filter options from the Hub API
        /// </summary>
        public async Task<HubFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
        {
            var result = await GetFilterOptionsResultAsync(cancellationToken);
            return result.Success ? result.Value : _cachedFilterOptions;
        }

        public async Task<ServiceResult<HubFilterOptions>> GetFilterOptionsResultAsync(CancellationToken cancellationToken = default)
        {
            // Check cache first
            if (_cachedFilterOptions != null && DateTime.Now - _filterOptionsCacheTime < _filterOptionsCacheExpiry)
            {
                PerformanceMonitor.RecordOperation("GetFilterOptionsAsync", 0, "Cached");
                return ServiceResult<HubFilterOptions>.Ok(_cachedFilterOptions);
            }

            using (var timer = PerformanceMonitor.StartOperation("GetFilterOptionsAsync"))
            {
                try
                {
                    var request = new JsonObject
                    {
                        ["source"] = "VaM",
                        ["action"] = "getInfo"
                    };

                    var requestJson = request.ToJsonString();

                    var response = await PostRequestRawAsync(requestJson, cancellationToken);
                    var options = JsonSerializer.Deserialize<HubFilterOptions>(response, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (options == null)
                        return ServiceResult<HubFilterOptions>.Fail("Hub returned empty filter options.");

                    // Cache the result
                    _cachedFilterOptions = options;
                    _filterOptionsCacheTime = DateTime.Now;

                    return ServiceResult<HubFilterOptions>.Ok(options);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Return cached version if available, even if expired, but still report failure
                    if (_cachedFilterOptions != null)
                        return ServiceResult<HubFilterOptions>.Fail("Failed to refresh Hub filter options; using cached options.", ex);

                    return ServiceResult<HubFilterOptions>.Fail("Failed to load Hub filter options.", ex);
                }
            }
        }

        /// <summary>
        /// Search for resources on the Hub
        /// </summary>
        public async Task<HubSearchResponse> SearchResourcesAsync(HubSearchParams searchParams, CancellationToken cancellationToken = default)
        {
            // Build cache key from search params
            var cacheKey = BuildSearchCacheKey(searchParams);
            
            // Check cache first
            lock (_searchCacheLock)
            {
                if (_searchCache.TryGetValue(cacheKey, out var cached) &&
                    DateTime.Now - cached.CacheTime < _searchCacheExpiry)
                {
                    var ageMs = (DateTime.Now - cached.CacheTime).TotalMilliseconds;
                    PerformanceMonitor.RecordOperation("SearchResourcesAsync", 0, $"Cached - Page {searchParams.Page}");
                    return cached.Response;
                }
            }

            try
            {
                if (_hubSearchCache != null && _hubSearchCache.TryGet(cacheKey, out var diskCached) && diskCached != null)
                {
                    lock (_searchCacheLock)
                    {
                        _searchCache[cacheKey] = (diskCached, DateTime.Now);
                    }
                    PerformanceMonitor.RecordOperation("SearchResourcesAsync", 0, $"DiskCached - Page {searchParams.Page}");
                    return diskCached;
                }
            }
            catch (Exception ex)
            {
            }
            
            using (var timer = PerformanceMonitor.StartOperation("SearchResourcesAsync")
                .WithDetails($"Page {searchParams.Page}, PerPage {searchParams.PerPage}"))
            {
                var request = new JsonObject
                {
                    ["source"] = "VaM",
                    ["action"] = "getResources",
                    ["latest_image"] = "Y",
                    ["perpage"] = searchParams.PerPage.ToString(),
                    ["page"] = searchParams.Page.ToString()
                };

                if (searchParams.Location != "All")
                    request["location"] = searchParams.Location;

                if (!string.IsNullOrEmpty(searchParams.Search))
                {
                    request["search"] = searchParams.Search;
                    request["searchall"] = "true";
                }

                // Set category based on PayType filter
                if (searchParams.PayType != "All")
                {
                    request["category"] = searchParams.PayType;
                }

                if (searchParams.Category != "All")
                    request["type"] = searchParams.Category;

                if (searchParams.Creator != "All")
                    request["username"] = searchParams.Creator;

                if (searchParams.Tags != "All")
                    request["tags"] = searchParams.Tags;

                request["sort"] = searchParams.Sort;
                
                if (!string.IsNullOrEmpty(searchParams.SortSecondary) && searchParams.SortSecondary != "None")
                    request["sort_secondary"] = searchParams.SortSecondary;

                var requestJson = request.ToJsonString();
                
                var response = await PostRequestAsync<HubSearchResponse>(requestJson, cancellationToken);
                
                // Cache the result
                if (response != null)
                {
                    lock (_searchCacheLock)
                    {
                        // Evict oldest entries if cache is full
                        if (_searchCache.Count >= _searchCacheMaxSize)
                        {
                            var oldest = _searchCache.OrderBy(x => x.Value.CacheTime).First().Key;
                            _searchCache.Remove(oldest);
                        }
                        _searchCache[cacheKey] = (response, DateTime.Now);
                    }

                    try
                    {
                        _hubSearchCache?.Store(cacheKey, response);
                    }
                    catch
                    {
                    }
                }
                
                return response;
            }
        }
        
        /// <summary>
        /// Build a cache key from search parameters
        /// </summary>
        private static string BuildSearchCacheKey(HubSearchParams p)
        {
            return $"{p.Page}|{p.PerPage}|{p.Location}|{p.Search}|{p.PayType}|{p.Category}|{p.Creator}|{p.Tags}|{p.Sort}|{p.SortSecondary}|{p.OnlyDownloadable}";
        }
        
        /// <summary>
        /// Clear search cache (call when user wants fresh results)
        /// </summary>
        public void ClearSearchCache()
        {
            lock (_searchCacheLock)
            {
                _searchCache.Clear();
            }

            try
            {
                _hubSearchCache?.Clear();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Get detailed information about a specific resource
        /// </summary>
        public async Task<HubResourceDetail> GetResourceDetailAsync(string resourceId, bool isPackageName = false, CancellationToken cancellationToken = default)
        {
            // Create cache key
            var cacheKey = isPackageName ? $"pkg:{resourceId}" : $"id:{resourceId}";
            
            // Check cache first
            if (_resourceDetailCache.TryGetValue(cacheKey, out var cached) && 
                DateTime.Now - cached.CacheTime < _resourceDetailCacheExpiry)
            {
                PerformanceMonitor.RecordOperation("GetResourceDetailAsync", 0, $"Cached - {cacheKey}");
                return cached.Detail;
            }

            try
            {
                if (TryLoadResourceDetailFromDisk(cacheKey, out var cachedJson))
                {
                    var detailFromDisk = JsonSerializer.Deserialize<HubResourceDetail>(cachedJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (detailFromDisk != null)
                    {
                        _resourceDetailCache[cacheKey] = (detailFromDisk, DateTime.Now);
                        PerformanceMonitor.RecordOperation("GetResourceDetailAsync", 0, $"DiskCached - {cacheKey}");
                        return detailFromDisk;
                    }
                }
            }
            catch
            {
            }
            
            using (var timer = PerformanceMonitor.StartOperation("GetResourceDetailAsync")
                .WithDetails(isPackageName ? $"Package: {resourceId}" : $"ResourceId: {resourceId}"))
            {
                var request = new JsonObject
                {
                    ["source"] = "VaM",
                    ["action"] = "getResourceDetail",
                    ["latest_image"] = "Y"
                };

                if (isPackageName)
                    request["package_name"] = resourceId;
                else
                    request["resource_id"] = resourceId;

                var jsonResponse = await PostRequestRawAsync(request.ToJsonString(), cancellationToken);
                
                // Parse the response - the detail fields are at root level
                var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "error")
                {
                    var error = root.TryGetProperty("error", out var errorProp) ? errorProp.GetString() : "Unknown error";
                    throw new Exception($"Hub API error: {error}");
                }

                // Deserialize directly as HubResourceDetail since fields are at root
                var detail = JsonSerializer.Deserialize<HubResourceDetail>(jsonResponse, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                // Cache the result
                if (detail != null)
                {
                    SaveResourceDetailToDisk(cacheKey, jsonResponse);

                    // Evict oldest entries if cache is full
                    if (_resourceDetailCache.Count >= _resourceDetailCacheMaxSize)
                    {
                        var oldest = _resourceDetailCache.OrderBy(x => x.Value.CacheTime).First().Key;
                        _resourceDetailCache.Remove(oldest);
                    }
                    _resourceDetailCache[cacheKey] = (detail, DateTime.Now);
                    
                    // Also cache by resource ID if we looked up by package name
                    if (isPackageName && !string.IsNullOrEmpty(detail.ResourceId))
                    {
                        var idKey = $"id:{detail.ResourceId}";
                        if (!_resourceDetailCache.ContainsKey(idKey))
                        {
                            _resourceDetailCache[idKey] = (detail, DateTime.Now);
                        }
                    }
                }

                return detail;
            }
        }

        /// <summary>
        /// Find packages by name (for missing dependencies or updates)
        /// </summary>
        public async Task<Dictionary<string, HubPackageInfo>> FindPackagesAsync(IEnumerable<string> packageNames, CancellationToken cancellationToken = default)
        {
            var namesList = packageNames.ToList();
            if (!namesList.Any())
                return new Dictionary<string, HubPackageInfo>();

            var request = new JsonObject
            {
                ["source"] = "VaM",
                ["action"] = "findPackages",
                ["packages"] = string.Join(",", namesList)
            };

            var response = await PostRequestAsync<HubFindPackagesResponse>(request.ToJsonString(), cancellationToken);

            var result = new Dictionary<string, HubPackageInfo>(StringComparer.OrdinalIgnoreCase);

            if (response?.Packages != null)
            {
                foreach (var kvp in response.Packages)
                {
                    var file = kvp.Value;
                    var info = new HubPackageInfo
                    {
                        PackageName = file.PackageName,
                        DownloadUrl = file.EffectiveDownloadUrl,
                        LatestUrl = file.LatestUrl,
                        FileSize = file.FileSize,
                        LicenseType = file.LicenseType,
                        NotOnHub = string.IsNullOrEmpty(file.EffectiveDownloadUrl) || file.EffectiveDownloadUrl == "null"
                    };

                    if (int.TryParse(file.Version, out var ver))
                        info.Version = ver;
                    if (int.TryParse(file.LatestVersion, out var latestVer))
                        info.LatestVersion = latestVer;

                    result[kvp.Key] = info;
                }
            }

            return result;
        }

        #endregion

        #region Package Version Checking

        /// <summary>
        /// Load the packages.json from Hub CDN for version checking.
        /// Uses binary caching with HTTP conditional requests (ETag/Last-Modified) for optimal performance.
        /// </summary>
        public async Task<bool> LoadPackagesJsonAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            // Check if in-memory cache is still valid
            if (!forceRefresh && _packageIdToResourceId != null && DateTime.Now - _packagesCacheTime < _packagesCacheExpiry)
                return true;

            try
            {
                var sw = Stopwatch.StartNew();
                
                // Try to load from disk cache first (if not already initialized)
                if (!_cacheInitialized)
                {
                    StatusChanged?.Invoke(this, "Loading Hub packages from cache...");
                    
                    if (_hubResourcesCache.LoadFromDisk())
                    {
                        // Cache loaded successfully, populate in-memory dictionaries
                        _packageIdToResourceId = _hubResourcesCache.GetPackageIdToResourceId();
                        _packageGroupToLatestVersion = _hubResourcesCache.GetPackageGroupToLatestVersion();
                        _packagesCacheTime = DateTime.Now;
                        _cacheInitialized = true;
                        
                        sw.Stop();
                        StatusChanged?.Invoke(this, $"Loaded {_packageIdToResourceId.Count} packages from cache ({sw.ElapsedMilliseconds}ms)");
                        
                        // Check if cache needs refresh in background (non-blocking)
                        if (_hubResourcesCache.NeedsRefresh() || forceRefresh)
                        {
                            _ = RefreshCacheInBackgroundAsync(cancellationToken);
                        }
                        
                        return true;
                    }
                    
                    _cacheInitialized = true; // Mark as initialized even if load failed
                }
                
                // Fetch from Hub (with conditional request if we have cached data)
                StatusChanged?.Invoke(this, "Fetching Hub packages index...");
                
                var success = await _hubResourcesCache.FetchFromHubAsync(PackagesJsonUrl, cancellationToken);
                
                if (success)
                {
                    // Update in-memory dictionaries from cache
                    _packageIdToResourceId = _hubResourcesCache.GetPackageIdToResourceId();
                    _packageGroupToLatestVersion = _hubResourcesCache.GetPackageGroupToLatestVersion();
                    _packagesCacheTime = DateTime.Now;
                    
                    sw.Stop();
                    var stats = _hubResourcesCache.GetStatistics();
                    var cacheInfo = stats.ConditionalHits > 0 ? $" (cached, {stats.ConditionalHitRate:F0}% conditional hits)" : "";
                    StatusChanged?.Invoke(this, $"Loaded {_packageIdToResourceId.Count} packages from Hub index{cacheInfo} ({sw.ElapsedMilliseconds}ms)");
                    
                    return true;
                }
                else
                {
                    // Fetch failed, but we might have stale cache data
                    if (_packageIdToResourceId != null && _packageIdToResourceId.Count > 0)
                    {
                        StatusChanged?.Invoke(this, $"Using cached Hub index ({_packageIdToResourceId.Count} packages) - network unavailable");
                        return true;
                    }
                    
                    StatusChanged?.Invoke(this, "Failed to load Hub packages index");
                    return false;
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Failed to load Hub packages index: {ex.Message}");
                
                // Return true if we have any cached data
                return _packageIdToResourceId != null && _packageIdToResourceId.Count > 0;
            }
        }
        
        /// <summary>
        /// Refreshes the cache in the background without blocking the caller
        /// </summary>
        private async Task RefreshCacheInBackgroundAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var success = await _hubResourcesCache.FetchFromHubAsync(PackagesJsonUrl, cancellationToken);
                
                if (success)
                {
                    _packageIdToResourceId = _hubResourcesCache.GetPackageIdToResourceId();
                    _packageGroupToLatestVersion = _hubResourcesCache.GetPackageGroupToLatestVersion();
                    _cacheInitialized = true;
                    _packagesCacheTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
            }
        }

        /// <summary>
        /// Check if a package has an update available on Hub
        /// </summary>
        public bool HasUpdate(string packageGroupName, int localVersion)
        {
            if (_packageGroupToLatestVersion == null)
                return false;

            if (_packageGroupToLatestVersion.TryGetValue(packageGroupName, out var hubVersion))
            {
                return hubVersion > localVersion;
            }

            return false;
        }

        /// <summary>
        /// Get the Hub resource ID for a package
        /// </summary>
        public string GetResourceId(string packageName)
        {
            if (_packageIdToResourceId == null)
                return null;

            _packageIdToResourceId.TryGetValue(packageName.Replace(".var", ""), out var resourceId);
            return resourceId;
        }
        
        /// <summary>
        /// Get the latest version number for a package group from Hub
        /// </summary>
        /// <param name="packageGroupName">Base package name without version</param>
        /// <returns>Latest version number, or -1 if not found</returns>
        public int GetLatestVersion(string packageGroupName)
        {
            if (_packageGroupToLatestVersion == null)
                return -1;

            if (_packageGroupToLatestVersion.TryGetValue(packageGroupName, out var latestVersion))
            {
                return latestVersion;
            }

            return -1;
        }
        
        /// <summary>
        /// Get the count of packages loaded from Hub
        /// </summary>
        /// <returns>Number of packages in the Hub index</returns>
        public int GetPackageCount()
        {
            return _packageIdToResourceId?.Count ?? 0;
        }
        
        /// <summary>
        /// Get all unique creator names from the packages index
        /// </summary>
        /// <returns>Sorted list of unique creator names</returns>
        public List<string> GetAllCreators()
        {
            if (_packageIdToResourceId == null || _packageIdToResourceId.Count == 0)
                return new List<string>();
            
            var creators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var packageName in _packageIdToResourceId.Keys)
            {
                // Package format: Creator.PackageName.Version
                var firstDot = packageName.IndexOf('.');
                if (firstDot > 0)
                {
                    var creator = packageName.Substring(0, firstDot);
                    creators.Add(creator);
                }
            }
            
            return creators.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        }

        #endregion

        #region Download

        /// <summary>
        /// Download a package from Hub
        /// </summary>
        public async Task<bool> DownloadPackageAsync(
            string downloadUrl, 
            string destinationPath, 
            string packageName,
            IProgress<HubDownloadProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(downloadUrl) || downloadUrl == "null")
            {
                progress?.Report(new HubDownloadProgress
                {
                    PackageName = packageName,
                    HasError = true,
                    ErrorMessage = "No download URL available"
                });
                return false;
            }

            try
            {
                progress?.Report(new HubDownloadProgress
                {
                    PackageName = packageName,
                    IsDownloading = true,
                    Progress = 0
                });

                // Use HttpCompletionOption.ResponseHeadersRead to stream the response
                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                
                
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                var downloadedBytes = 0L;

                // Get filename from Content-Disposition header if available
                var fileName = packageName + ".var";
                if (response.Content.Headers.ContentDisposition?.FileName != null)
                {
                    fileName = response.Content.Headers.ContentDisposition.FileName.Trim('"');
                }

                var fullPath = Path.Combine(destinationPath, fileName);

                // Ensure directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                int bytesRead;
                var lastProgressReport = DateTime.Now;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    downloadedBytes += bytesRead;

                    // Report progress every 100ms
                    if ((DateTime.Now - lastProgressReport).TotalMilliseconds > 100)
                    {
                        var progressValue = totalBytes > 0 ? (float)downloadedBytes / totalBytes : 0;
                        progress?.Report(new HubDownloadProgress
                        {
                            PackageName = packageName,
                            IsDownloading = true,
                            Progress = progressValue,
                            DownloadedBytes = downloadedBytes,
                            TotalBytes = totalBytes
                        });
                        DownloadProgressChanged?.Invoke(this, new HubDownloadProgress
                        {
                            PackageName = packageName,
                            IsDownloading = true,
                            Progress = progressValue,
                            DownloadedBytes = downloadedBytes,
                            TotalBytes = totalBytes
                        });
                        lastProgressReport = DateTime.Now;
                    }
                }


                progress?.Report(new HubDownloadProgress
                {
                    PackageName = packageName,
                    IsCompleted = true,
                    Progress = 1,
                    DownloadedBytes = downloadedBytes,
                    TotalBytes = totalBytes
                });

                return true;
            }
            catch (Exception ex)
            {
                progress?.Report(new HubDownloadProgress
                {
                    PackageName = packageName,
                    HasError = true,
                    ErrorMessage = ex.Message
                });
                return false;
            }
        }

        /// <summary>
        /// Queue a download to be processed sequentially
        /// </summary>
        public QueuedDownload QueueDownload(string downloadUrl, string destinationPath, string packageName, long fileSize = 0)
        {
            var queuedDownload = new QueuedDownload
            {
                PackageName = packageName,
                DownloadUrl = downloadUrl,
                DestinationPath = destinationPath,
                Status = DownloadStatus.Queued,
                TotalBytes = fileSize,
                CancellationTokenSource = new CancellationTokenSource(),
                QueuedTime = DateTime.Now
            };

            bool shouldStartProcessing = false;
            
            // Use lock for thread-safe queue access
            lock (_downloadQueueLock)
            {
                _downloadQueue.Enqueue(queuedDownload);
                
                // Check if we need to start processing (inside same lock to prevent race)
                if (!_isDownloading)
                {
                    _isDownloading = true;
                    shouldStartProcessing = true;
                }
            }
            
            // Fire event (outside lock to prevent deadlocks)
            DownloadQueued?.Invoke(this, queuedDownload);
            
            // Start processing queue on background thread if needed
            // FIXED: Wrap in try-catch to prevent unobserved task exceptions
            if (shouldStartProcessing)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessDownloadQueueAsync();
                    }
                    catch (Exception ex)
                    {
                    }
                });
            }

            return queuedDownload;
        }

        /// <summary>
        /// Cancel a queued or active download
        /// </summary>
        public void CancelDownload(QueuedDownload download)
        {
            if (download?.CancellationTokenSource != null && download.CanCancel)
            {
                download.CancellationTokenSource.Cancel();
                download.Status = DownloadStatus.Cancelled;
            }
        }

        /// <summary>
        /// Cancel all queued and active downloads
        /// </summary>
        public void CancelAllDownloads()
        {
            lock (_downloadQueueLock)
            {
                foreach (var download in _downloadQueue)
                {
                    if (download.CanCancel)
                    {
                        download.CancellationTokenSource?.Cancel();
                        download.Status = DownloadStatus.Cancelled;
                    }
                }
            }
        }

        /// <summary>
        /// Get the current download queue count
        /// </summary>
        public int GetQueueCount()
        {
            lock (_downloadQueueLock)
            {
                return _downloadQueue.Count;
            }
        }

        /// <summary>
        /// Check if currently downloading
        /// </summary>
        public bool IsDownloading => _isDownloading;

        /// <summary>
        /// Process the download queue sequentially (runs on background thread)
        /// </summary>
        private async Task ProcessDownloadQueueAsync()
        {
            try
            {
                while (true)
                {
                    QueuedDownload download;
                    
                    lock (_downloadQueueLock)
                    {
                        if (_downloadQueue.Count == 0)
                            break;

                        download = _downloadQueue.Dequeue();
                    }

                    if (download == null)
                        break;

                    // Skip cancelled downloads
                    if (download.Status == DownloadStatus.Cancelled)
                        continue;

                    download.Status = DownloadStatus.Downloading;
                    download.StartTime = DateTime.Now;
                    download.ProgressPercentage = 0;
                    DownloadStarted?.Invoke(this, download);

                    var progress = new Progress<HubDownloadProgress>(p =>
                    {
                        if (p.IsDownloading)
                        {
                            download.DownloadedBytes = p.DownloadedBytes;
                            if (p.TotalBytes > 0)
                            {
                                download.TotalBytes = p.TotalBytes;
                                download.ProgressPercentage = (int)(p.Progress * 100);
                            }
                        }
                    });

                    bool success = false;
                    try
                    {
                        success = await DownloadPackageAsync(
                            download.DownloadUrl,
                            download.DestinationPath,
                            download.PackageName,
                            progress,
                            download.CancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Download was cancelled
                        success = false;
                    }
                    catch (Exception ex)
                    {
                        download.ErrorMessage = ex.Message;
                        success = false;
                    }

                    download.EndTime = DateTime.Now;

                    if (download.CancellationTokenSource.Token.IsCancellationRequested)
                    {
                        download.Status = DownloadStatus.Cancelled;
                    }
                    else if (success)
                    {
                        download.Status = DownloadStatus.Completed;
                        download.ProgressPercentage = 100;
                    }
                    else
                    {
                        download.Status = DownloadStatus.Failed;
                        if (string.IsNullOrEmpty(download.ErrorMessage))
                            download.ErrorMessage = "Download failed";
                    }

                    // Dispose the CancellationTokenSource to prevent memory leak
                    try
                    {
                        download.CancellationTokenSource?.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors
                    }

                    DownloadCompleted?.Invoke(this, download);
                }
            }
            finally
            {
                lock (_downloadQueueLock)
                {
                    _isDownloading = false;
                }
                
                // Notify that all downloads are complete
                AllDownloadsCompleted?.Invoke(this, EventArgs.Empty);
            }
        }

        #endregion

        #region Helper Methods

        private async Task<T> PostRequestAsync<T>(string jsonContent, CancellationToken cancellationToken) where T : class
        {
            var responseJson = await PostRequestRawAsync(jsonContent, cancellationToken);
            try
            {
                return JsonSerializer.Deserialize<T>(responseJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException)
            {
                throw;
            }
        }

        private async Task<string> PostRequestRawAsync(string jsonContent, CancellationToken cancellationToken)
        {
            await _requestThrottle.WaitAsync(cancellationToken);
            try
            {
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(ApiUrl, content, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            finally
            {
                _requestThrottle.Release();
            }
        }

        private static int ExtractVersion(string packageName)
        {
            var name = packageName;
            
            // Remove .var extension
            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            
            // Handle .latest - return -1 as there's no numeric version
            if (name.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                return -1;

            // Find version number at the end
            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
            {
                var afterDot = name.Substring(lastDot + 1);
                if (int.TryParse(afterDot, out var version))
                {
                    return version;
                }
            }

            return -1;
        }

        private static string GetPackageGroupName(string packageName)
        {
            var name = packageName;
            
            // Remove .var extension
            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            
            // Remove .latest suffix
            if (name.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 7);

            // Remove version number (digits at the end)
            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
            {
                var afterDot = name.Substring(lastDot + 1);
                if (int.TryParse(afterDot, out _))
                {
                    return name.Substring(0, lastDot);
                }
            }

            return name;
        }

        public static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }

        #endregion

        /// <summary>
        /// Gets the Hub resources cache for statistics and management
        /// </summary>
        public HubResourcesCache ResourcesCache => _hubResourcesCache;
        
        /// <summary>
        /// Gets cache statistics
        /// </summary>
        public HubResourcesCacheStats GetCacheStatistics()
        {
            return _hubResourcesCache?.GetStatistics();
        }
        
        /// <summary>
        /// Clears the Hub resources cache
        /// </summary>
        public bool ClearResourcesCache()
        {
            var result = _hubResourcesCache?.ClearCache() ?? false;
            if (result)
            {
                _packageIdToResourceId = null;
                _packageGroupToLatestVersion = null;
                _packagesCacheTime = DateTime.MinValue;
                _cacheInitialized = false;
            }
            return result;
        }
        
        private System.Threading.Timer _imageCacheSaveTimer;
        private readonly object _imageCacheSaveLock = new object();
        private bool _imageCacheDirty = false;
        private const int IMAGE_CACHE_SAVE_DELAY_MS = 3000; // Save 3 seconds after last change
        
        /// <summary>
        /// Downloads and caches an image from Hub
        /// Returns cached image if available, otherwise downloads from URL
        /// </summary>
        public async Task<BitmapImage> GetCachedImageAsync(string imageUrl, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(imageUrl))
            {
                return null;
            }
            
            // Try to get from cache first
            var cachedImage = _hubResourcesCache?.TryGetCachedImage(imageUrl);
            if (cachedImage != null)
            {
                return cachedImage;
            }
            
            // Download from URL
            try
            {
                using var response = await _httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseContentRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                
                var imageData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                
                // Cache the image data
                var cacheResult = _hubResourcesCache?.CacheImage(imageUrl, imageData) ?? false;
                
                // Schedule a batched save (debounced)
                if (cacheResult)
                {
                    ScheduleImageCacheSave();
                }
                
                // Convert to BitmapImage
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = new MemoryStream(imageData);
                bitmap.EndInit();
                bitmap.Freeze();
                
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }
        
        /// <summary>
        /// Schedules a debounced save of the image cache
        /// Multiple rapid changes will only result in one save after 3 seconds of inactivity
        /// </summary>
        private void ScheduleImageCacheSave()
        {
            lock (_imageCacheSaveLock)
            {
                _imageCacheDirty = true;
                
                if (_imageCacheSaveTimer == null)
                {
                    _imageCacheSaveTimer = new System.Threading.Timer(
                        _ => PerformImageCacheSave(),
                        null,
                        IMAGE_CACHE_SAVE_DELAY_MS,
                        System.Threading.Timeout.Infinite);
                }
                else
                {
                    // Reset the timer
                    _imageCacheSaveTimer.Change(IMAGE_CACHE_SAVE_DELAY_MS, System.Threading.Timeout.Infinite);
                }
            }
        }
        
        /// <summary>
        /// Performs the actual save of the image cache
        /// </summary>
        private void PerformImageCacheSave()
        {
            lock (_imageCacheSaveLock)
            {
                if (_imageCacheDirty)
                {
                    _imageCacheDirty = false;
                    SaveImageCache();
                }
            }
        }
        
        /// <summary>
        /// Loads the image cache from disk
        /// Call this during app startup to restore cached images
        /// </summary>
        public bool LoadImageCache()
        {
            var result = _hubResourcesCache?.LoadImageCacheFromDisk() ?? false;
            return result;
        }
        
        /// <summary>
        /// Saves the image cache to disk
        /// Call this during app shutdown to persist cached images
        /// </summary>
        public bool SaveImageCache()
        {
            var result = _hubResourcesCache?.SaveImageCacheToDisk() ?? false;
            return result;
        }
        
        public void Dispose()
        {
            if (!_disposed)
            {
                // Perform final image cache save before disposing
                PerformImageCacheSave();
                
                _imageCacheSaveTimer?.Dispose();
                _httpClient?.Dispose();
                _requestThrottle?.Dispose();
                _hubResourcesCache?.Dispose();
                _disposed = true;
            }
        }
    }
}
