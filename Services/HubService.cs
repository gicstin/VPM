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
        private readonly CookieContainer _cookieContainer;
        private readonly SemaphoreSlim _requestThrottle = new SemaphoreSlim(4, 4); // Allow up to 4 concurrent API requests
        private bool _disposed;
        
        // Performance monitoring
        public readonly PerformanceMonitor PerformanceMonitor = new PerformanceMonitor();
        
        // API Response Caching
        private HubFilterOptions _cachedFilterOptions;
        private DateTime _filterOptionsCacheTime = DateTime.MinValue;
        private readonly TimeSpan _filterOptionsCacheExpiry = TimeSpan.FromHours(1); // Filter options rarely change
        
        // Resource detail cache (Single binary file)
        private readonly HubResourceDetailCache _detailCache;
        
        // Search result cache
        private readonly Dictionary<string, (HubSearchResponse Response, DateTime CacheTime)> _searchCache = new();
        private readonly int _searchCacheMaxSize = 20;
        private readonly TimeSpan _searchCacheExpiry = TimeSpan.FromMinutes(5);
        private readonly object _searchCacheLock = new object();

        private readonly HubSearchCache _hubSearchCache;

        // Binary cache for packages.json with HTTP conditional request support
        private readonly HubResourcesCache _hubResourcesCache;
        private readonly RemotePackageInspector _remoteInspector;
        private bool _cacheInitialized = false;

        // Dependency resolution cache (on-demand, short-lived)
        private readonly Dictionary<string, (HubDependencyResolution Resolution, DateTime CacheTime)> _dependencyResolutionCache =
            new Dictionary<string, (HubDependencyResolution Resolution, DateTime CacheTime)>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _dependencyResolutionCacheExpiry = TimeSpan.FromMinutes(10);
        private readonly object _dependencyResolutionCacheLock = new object();
        
        // In-memory cache references (delegated to HubResourcesCache)
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
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Persistent Dependency Cache
        private Dictionary<string, HubDependencyResolution> _persistentDependencyCache = new Dictionary<string, HubDependencyResolution>(StringComparer.OrdinalIgnoreCase);
        private readonly string _persistentDependencyCachePath;
        private readonly object _persistentCacheLock = new object();
        private bool _persistentCacheDirty = false;

        public HubService()
        {
            _cookieContainer = new CookieContainer();
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = _cookieContainer
            };

            // Add the Hub consent cookie
            handler.CookieContainer.Add(new Uri($"https://{CookieHost}"), 
                new Cookie("vamhubconsent", "1", "/", CookieHost));

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30)
            };

            _httpClient.DefaultRequestHeaders.Add("User-Agent", "VPM/1.0");
            
            _remoteInspector = new RemotePackageInspector(_httpClient);

            // Initialize the binary cache for Hub resources
            _hubResourcesCache = new HubResourcesCache(_httpClient);

            _hubSearchCache = new HubSearchCache(ttl: TimeSpan.FromMinutes(10), maxEntries: 200);
            
            // Initialize detail cache
            _detailCache = new HubResourceDetailCache();
            Task.Run(() => _detailCache.LoadFromDisk());

            _cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPM", "Cache");
            Directory.CreateDirectory(_cacheDirectory);
            _persistentDependencyCachePath = Path.Combine(_cacheDirectory, "DependencyResolution.json");
            LoadPersistentDependencyCache();

            // Periodic save task
            Task.Run(async () =>
            {
                while (!_disposed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    SavePersistentDependencyCache();
                }
            });

            // Clean up legacy HubResourceDetails folder if it exists
            Task.Run(() =>
            {
                try
                {
                    var legacyPath = Path.Combine(_cacheDirectory, "HubResourceDetails");
                    if (Directory.Exists(legacyPath))
                    {
                        Directory.Delete(legacyPath, true);
                    }
                }
                catch (Exception) { /* ignore cleanup errors */ }
            });
        }

        private void LoadPersistentDependencyCache()
        {
            try
            {
                if (File.Exists(_persistentDependencyCachePath))
                {
                    var json = File.ReadAllText(_persistentDependencyCachePath);
                    var cache = JsonSerializer.Deserialize<Dictionary<string, HubDependencyResolution>>(json, _jsonOptions);
                    if (cache != null)
                    {
                        lock (_persistentCacheLock)
                        {
                            _persistentDependencyCache = new Dictionary<string, HubDependencyResolution>(cache, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubService] Failed to load persistent dependency cache: {ex}");
            }
        }

        private void SavePersistentDependencyCache()
        {
            if (!_persistentCacheDirty) return;
            
            try
            {
                Dictionary<string, HubDependencyResolution> cacheSnapshot;
                lock (_persistentCacheLock)
                {
                    cacheSnapshot = new Dictionary<string, HubDependencyResolution>(_persistentDependencyCache, StringComparer.OrdinalIgnoreCase);
                    _persistentCacheDirty = false;
                }

                var json = JsonSerializer.Serialize(cacheSnapshot, _jsonOptions);
                File.WriteAllText(_persistentDependencyCachePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubService] Failed to save persistent dependency cache: {ex}");
            }
        }

        public void UpdateCookies(IEnumerable<Cookie> cookies)
        {
            foreach (var cookie in cookies)
            {
                try
                {
                    _cookieContainer.Add(cookie);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HubService] Failed to add cookie {cookie.Name}: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            // Save caches
            SavePersistentDependencyCache();
            PerformImageCacheSave();
            
            // Dispose resources
            _imageCacheSaveTimer?.Dispose();
            _httpClient?.Dispose();
            _requestThrottle?.Dispose();
            _hubResourcesCache?.Dispose();
            _detailCache?.Dispose();
            
            _disposed = true;
        }


        public bool HasCachedIndirectDependencies(HubResourceDetail detail)
        {
            if (detail == null)
                return false;

            var downloadUrl = detail.HubFiles?.Count > 0 ? detail.HubFiles[0].EffectiveDownloadUrl : null;
            
            // If no download URL AND no API dependencies, we can't have cached indirects
            if ((string.IsNullOrEmpty(downloadUrl) || downloadUrl == "null") && 
                (detail.Dependencies == null || detail.Dependencies.Count == 0))
                return false;

            var cacheKey = !string.IsNullOrEmpty(detail.ResourceId)
                ? $"id:{detail.ResourceId}|indirect"
                : $"url:{downloadUrl}|indirect";

            lock (_persistentCacheLock)
            {
                return _persistentDependencyCache.ContainsKey(cacheKey);
            }
        }

        /// <summary>
        /// Inspect dependencies up to 2 levels deep and return direct + indirect lists.
        /// This is intended for on-demand use when showing detail pane.
        /// </summary>
        public async Task<HubDependencyResolution> InspectPackageDependenciesTwoLevelAsync(
            HubResourceDetail detail,
            bool includeIndirect,
            CancellationToken cancellationToken = default,
            IProgress<string> statusReporter = null)
        {
            if (detail == null)
            {
                return new HubDependencyResolution
                {
                    DirectDependencies = new Dictionary<string, List<HubFile>>(),
                    IndirectDependencies = new Dictionary<string, List<HubFile>>()
                };
            }

            var mainPackageName = detail.PackageName;
            var downloadUrl = detail.HubFiles?.Count > 0 ? detail.HubFiles[0].EffectiveDownloadUrl : null;
            
            // If no download URL AND no API dependencies, we can't do anything
            if ((string.IsNullOrEmpty(downloadUrl) || downloadUrl == "null") && 
                (detail.Dependencies == null || detail.Dependencies.Count == 0))
            {
                return new HubDependencyResolution
                {
                    DirectDependencies = new Dictionary<string, List<HubFile>>(),
                    IndirectDependencies = new Dictionary<string, List<HubFile>>()
                };
            }

            var cacheKey = !string.IsNullOrEmpty(detail.ResourceId)
                ? $"id:{detail.ResourceId}"
                : $"url:{downloadUrl}";
            cacheKey += includeIndirect ? "|indirect" : "|direct";

            // Check persistent cache first
            lock (_persistentCacheLock)
            {
                if (_persistentDependencyCache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            if (TryGetDependencyResolutionCache(cacheKey, out var cachedResolution))
            {
                return cachedResolution;
            }

            var directPackages = new Dictionary<string, HubPackageInfo>(StringComparer.OrdinalIgnoreCase);
            var indirectPackages = new Dictionary<string, HubPackageInfo>(StringComparer.OrdinalIgnoreCase);

            if (detail.Dependencies != null && detail.Dependencies.Count > 0)
            {
                var apiFilesTotal = 0;
                foreach (var v in detail.Dependencies.Values)
                    apiFilesTotal += v?.Count ?? 0;

                Trace.WriteLine($"[HubService] Using API dependencies for {detail.PackageName ?? detail.ResourceId}. Keys={detail.Dependencies.Count}, Files={apiFilesTotal}");

                foreach (var kvp in detail.Dependencies)
                {
                    if (kvp.Value == null || kvp.Value.Count == 0)
                        continue;

                    foreach (var file in kvp.Value)
                    {
                        if (file == null)
                            continue;

                        var packageName = file.PackageName;
                        if (string.IsNullOrWhiteSpace(packageName))
                            packageName = kvp.Key;

                        if (string.IsNullOrWhiteSpace(packageName))
                            continue;

                        directPackages[packageName] = new HubPackageInfo
                        {
                            PackageName = packageName,
                            DownloadUrl = file.EffectiveDownloadUrl,
                            LatestUrl = file.LatestUrl,
                            FileSize = file.FileSize,
                            LicenseType = file.LicenseType
                        };
                    }
                }
            }
            else if (!string.IsNullOrEmpty(downloadUrl) && downloadUrl != "null")
            {
                Trace.WriteLine($"[HubService] API dependencies missing for {detail.PackageName ?? detail.ResourceId}. Falling back to remote inspector.");
                statusReporter?.Report("Inspecting direct dependencies...");
                List<string> directNames = null;
                try
                {
                    directNames = await _remoteInspector.GetPackageDependenciesAsync(downloadUrl, cancellationToken);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[HubService] Failed to inspect direct dependencies: {ex.Message}");
                }

                Trace.WriteLine($"[HubService] Remote inspector returned {directNames?.Count ?? 0} direct dependencies for {detail.PackageName ?? detail.ResourceId}.");

                if (directNames == null || directNames.Count == 0)
                {
                    return new HubDependencyResolution
                    {
                        DirectDependencies = new Dictionary<string, List<HubFile>>(),
                        IndirectDependencies = new Dictionary<string, List<HubFile>>()
                    };
                }

                var resolvedDirect = await FindPackagesAsync(directNames, cancellationToken);
                Trace.WriteLine($"[HubService] Resolved {resolvedDirect?.Count ?? 0} direct packages for {detail.PackageName ?? detail.ResourceId}.");
                foreach (var kvp in resolvedDirect)
                {
                    directPackages[kvp.Key] = kvp.Value;
                }
            }

            if (includeIndirect)
            {
                var nextLevelNames = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                var checkedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Use slightly higher concurrency for checking many files
                using var throttle = new SemaphoreSlim(20, 20);
                var tasks = new List<Task>();
                
                int totalItems = 0;
                int processedItems = 0;

                // Create a linked CTS for internal cancellation (timeout)
                using var internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                foreach (var info in directPackages.Values)
                {
                    if (info.NotOnHub || string.IsNullOrEmpty(info.DownloadUrl) || info.DownloadUrl == "null")
                    {
                        continue;
                    }

                    if (!checkedUrls.Add(info.DownloadUrl))
                    {
                        continue;
                    }

                    totalItems++;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await throttle.WaitAsync(internalCts.Token);
                            try
                            {
                                var subDeps = await _remoteInspector.GetPackageDependenciesAsync(info.DownloadUrl, internalCts.Token);
                                if (subDeps == null)
                                {
                                    return;
                                }

                                foreach (var dep in subDeps)
                                {
                                    if (directPackages.ContainsKey(dep))
                                    {
                                        continue;
                                    }

                                    if (!string.IsNullOrEmpty(mainPackageName) && string.Equals(dep, mainPackageName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }

                                    nextLevelNames.TryAdd(dep, 0);
                                }
                            }
                            finally
                            {
                                try { throttle.Release(); } catch (ObjectDisposedException) { }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[HubService] Error inspecting {info.PackageName}: {ex.Message}");
                        }
                        finally
                        {
                            var current = Interlocked.Increment(ref processedItems);
                            statusReporter?.Report($"Scanning {current}/{totalItems} dependencies...");
                        }
                    }, internalCts.Token));
                }

                if (tasks.Count > 0)
                {
                    statusReporter?.Report($"Scanning 0/{totalItems} dependencies...");
                    // Dynamic timeout: 5s per item or minimum 45s, max 5 minutes
                    var timeoutMs = Math.Min(300000, Math.Max(45000, totalItems * 5000));
                    
                    var allTasks = Task.WhenAll(tasks);
                    var delayTask = Task.Delay(timeoutMs, internalCts.Token);
                    
                    var finishedTask = await Task.WhenAny(allTasks, delayTask);
                    
                    if (finishedTask == delayTask)
                    {
                        Trace.WriteLine("[HubService] Dependency inspection timed out.");
                        internalCts.Cancel();
                    }
                }

                if (!nextLevelNames.IsEmpty)
                {
                    statusReporter?.Report($"Resolving {nextLevelNames.Count} sub-dependencies...");
                    var resolvedIndirect = await FindPackagesAsync(nextLevelNames.Keys, cancellationToken);
                    foreach (var kvp in resolvedIndirect)
                    {
                        if (!directPackages.ContainsKey(kvp.Key))
                        {
                            indirectPackages[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }

            var result = new HubDependencyResolution
            {
                DirectDependencies = BuildDependencyFiles(directPackages),
                IndirectDependencies = BuildDependencyFiles(indirectPackages)
            };

            StoreDependencyResolutionCache(cacheKey, result);
            
            // Update persistent cache
            lock (_persistentCacheLock)
            {
                _persistentDependencyCache[cacheKey] = result;
                _persistentCacheDirty = true;
            }
            
            return result;
        }

        private bool TryGetDependencyResolutionCache(string cacheKey, out HubDependencyResolution resolution)
        {
            resolution = null;
            lock (_dependencyResolutionCacheLock)
            {
                if (_dependencyResolutionCache.TryGetValue(cacheKey, out var entry))
                {
                    if (DateTime.UtcNow - entry.CacheTime <= _dependencyResolutionCacheExpiry)
                    {
                        resolution = entry.Resolution;
                        return true;
                    }

                    _dependencyResolutionCache.Remove(cacheKey);
                }
            }

            return false;
        }

        private void StoreDependencyResolutionCache(string cacheKey, HubDependencyResolution resolution)
        {
            if (resolution == null)
            {
                return;
            }

            lock (_dependencyResolutionCacheLock)
            {
                _dependencyResolutionCache[cacheKey] = (resolution, DateTime.UtcNow);

                if (_dependencyResolutionCache.Count > 200)
                {
                    var expiredKeys = _dependencyResolutionCache
                        .Where(kvp => DateTime.UtcNow - kvp.Value.CacheTime > _dependencyResolutionCacheExpiry)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in expiredKeys)
                    {
                        _dependencyResolutionCache.Remove(key);
                    }
                }
            }
        }

        private static Dictionary<string, List<HubFile>> BuildDependencyFiles(Dictionary<string, HubPackageInfo> packages)
        {
            var deps = new Dictionary<string, List<HubFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in packages)
            {
                var info = kvp.Value;
                var packageName = info?.PackageName;
                if (string.IsNullOrWhiteSpace(packageName))
                {
                    packageName = kvp.Key;
                }

                if (string.IsNullOrWhiteSpace(packageName))
                {
                    continue;
                }

                var hubFile = new HubFile
                {
                    Filename = packageName + ".var",
                    FileSizeStr = info?.FileSize.ToString(),
                    DownloadUrl = info?.DownloadUrl,
                    LatestUrl = info?.LatestUrl,
                    LicenseType = info?.LicenseType,
                    Version = info?.Version > 0 ? info.Version.ToString() : null,
                    LatestVersion = info?.LatestVersion > 0 ? info.LatestVersion.ToString() : null,
                };

                deps[packageName] = new List<HubFile> { hubFile };
            }

            return deps;
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
                    var options = JsonSerializer.Deserialize<HubFilterOptions>(response, _jsonOptions);

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
        /// Try to get a cached search response for the given parameters, ignoring expiration if requested.
        /// </summary>
        public HubSearchResponse TryGetCachedSearch(HubSearchParams searchParams, bool ignoreExpiration)
        {
            var cacheKey = BuildSearchCacheKey(searchParams);

            // Check memory cache first
            lock (_searchCacheLock)
            {
                if (_searchCache.TryGetValue(cacheKey, out var cached))
                {
                    // If ignoring expiration or cache is fresh
                    if (ignoreExpiration || DateTime.Now - cached.CacheTime < _searchCacheExpiry)
                    {
                        return cached.Response;
                    }
                }
            }

            // Check disk cache
            if (_hubSearchCache != null && _hubSearchCache.TryGet(cacheKey, out var diskCached, ignoreExpiration) && diskCached != null)
            {
                lock (_searchCacheLock)
                {
                    _searchCache[cacheKey] = (diskCached, DateTime.Now);
                }
                return diskCached;
            }

            return null;
        }

        /// <summary>
        /// Search for resources on the Hub
        /// </summary>
        public async Task<HubSearchResponse> SearchResourcesAsync(HubSearchParams searchParams, CancellationToken cancellationToken = default)
        {
            // Build cache key from search params
            var cacheKey = BuildSearchCacheKey(searchParams);
            
            // Check cache first (fresh only)
            var cached = TryGetCachedSearch(searchParams, ignoreExpiration: false);
            if (cached != null)
            {
                PerformanceMonitor.RecordOperation("SearchResourcesAsync", 0, $"Cached - Page {searchParams.Page}");
                return cached;
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
            
            // Check cache
            var cached = _detailCache.TryGet(cacheKey);
            if (cached != null)
            {
                PerformanceMonitor.RecordOperation("GetResourceDetailAsync", 0, $"Cached - {cacheKey}");
                return cached;
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
                var detail = JsonSerializer.Deserialize<HubResourceDetail>(jsonResponse, _jsonOptions);

                // Note: dependency inspection is performed on-demand when the detail panel is shown.

                // Cache the result
                if (detail != null)
                {
                    _detailCache.Store(cacheKey, detail);
                    
                    // Also cache by resource ID if we looked up by package name
                    if (isPackageName && !string.IsNullOrEmpty(detail.ResourceId))
                    {
                        var idKey = $"id:{detail.ResourceId}";
                        _detailCache.Store(idKey, detail);
                    }
                }

                return detail;
            }
        }

        /// <summary>
        /// Perform recursive dependency inspection for a specific package details object.
        /// This should be called by the UI when displaying details.
        /// </summary>
        public async Task<HubResourceDetail> InspectPackageDependenciesAsync(HubResourceDetail detail, CancellationToken cancellationToken = default)
        {
            if (detail == null || !detail.HubDownloadable || detail.HubFiles == null || detail.HubFiles.Count == 0)
                return detail;

            try
            {
                var mainFile = detail.HubFiles[0];
                var downloadUrl = mainFile.EffectiveDownloadUrl;

                if (string.IsNullOrEmpty(downloadUrl) || downloadUrl == "null")
                    return detail;

                // Start with existing dependencies if we have them (from initial shallow check)
                var existingDeps = new Dictionary<string, HubPackageInfo>(StringComparer.OrdinalIgnoreCase);
                
                if (detail.Dependencies != null)
                {
                    foreach (var kvp in detail.Dependencies)
                    {
                        if (kvp.Value != null && kvp.Value.Count > 0)
                        {
                            var f = kvp.Value[0];
                            existingDeps[kvp.Key] = new HubPackageInfo 
                            { 
                                PackageName = kvp.Key,
                                DownloadUrl = f.EffectiveDownloadUrl,
                                LatestUrl = f.LatestUrl,
                                FileSize = f.FileSize,
                                LicenseType = f.LicenseType
                            };
                        }
                    }
                }

                // RECURSIVE CHECK (Multi-level): Check dependencies of dependencies up to a limit
                // API doesn't provide this, so we use RemotePackageInspector recursively.
                var allResolvedDeps = new System.Collections.Concurrent.ConcurrentDictionary<string, HubPackageInfo>(existingDeps, StringComparer.OrdinalIgnoreCase);
                var checkedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                Trace.WriteLine($"[HubService] Starting recursive dependency check for {detail.PackageName}. Initial deps: {existingDeps.Count}");

                // Start with Level 1 dependencies
                var currentBatch = existingDeps.Values.ToList();
                int depth = 0;
                const int MaxDepth = 3; // Go up to 3 levels deep (Main -> Dep -> Dep -> Dep)
                
                // Throttle concurrent remote inspections to avoid network congestion/timeouts
                using var throttle = new SemaphoreSlim(8, 8);

                while (depth < MaxDepth && currentBatch.Count > 0)
                {
                    Trace.WriteLine($"[HubService] Processing Depth {depth}, Batch Size: {currentBatch.Count}");
                    
                    var nextBatchNames = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                    var tasks = new List<Task>();
                    
                    // Process current batch in parallel with throttling
                    foreach (var info in currentBatch)
                    {
                        if (info.NotOnHub || string.IsNullOrEmpty(info.DownloadUrl) || info.DownloadUrl == "null")
                        {
                            continue;
                        }
                        if (checkedUrls.Contains(info.DownloadUrl))
                        {
                            continue;
                        }

                        checkedUrls.Add(info.DownloadUrl);

                        tasks.Add(Task.Run(async () =>
                        {
                            await throttle.WaitAsync(cancellationToken);
                            try
                            {
                                var subDeps = await _remoteInspector.GetPackageDependenciesAsync(info.DownloadUrl, cancellationToken);
                                if (subDeps != null)
                                {
                                    foreach (var dep in subDeps)
                                    {
                                        // Only add if not already in the main list and not the package itself
                                        if (!allResolvedDeps.ContainsKey(dep) && 
                                            !string.Equals(dep, detail.HubFiles[0].PackageName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            nextBatchNames.TryAdd(dep, 0);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Trace.WriteLine($"[HubService] Error inspecting {info.PackageName}: {ex.Message}");
                            }
                            finally
                            {
                                throttle.Release();
                            }
                        }));
                    }

                    // Wait for batch to complete (with timeout per level)
                    if (tasks.Count > 0)
                    {
                        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(45000, cancellationToken));
                    }

                    if (nextBatchNames.IsEmpty) 
                    {
                        break;
                    }

                    // Resolve newly found dependencies
                    var resolvedNext = await FindPackagesAsync(nextBatchNames.Keys, cancellationToken);
                    
                    // Update currentBatch for next iteration (only new valid packages)
                    currentBatch.Clear();
                    foreach (var kvp in resolvedNext)
                    {
                        if (!allResolvedDeps.ContainsKey(kvp.Key))
                        {
                            allResolvedDeps[kvp.Key] = kvp.Value;
                            currentBatch.Add(kvp.Value);
                        }
                    }

                    depth++;
                }
                
                Trace.WriteLine($"[HubService] Total resolved dependencies after recursive check: {allResolvedDeps.Count}");

                var newDeps = new Dictionary<string, List<HubFile>>();
                
                foreach (var kvp in allResolvedDeps)
                {
                    var info = kvp.Value;
                    var hubFile = new HubFile
                    {
                        Filename = info.PackageName + ".var",
                        FileSizeStr = info.FileSize.ToString(),
                        DownloadUrl = info.DownloadUrl,
                        LatestUrl = info.LatestUrl,
                        LicenseType = info.LicenseType,
                        Version = info.Version > 0 ? info.Version.ToString() : null,
                        LatestVersion = info.LatestVersion > 0 ? info.LatestVersion.ToString() : null,
                    };
                    
                    newDeps[kvp.Key] = new List<HubFile> { hubFile };
                }
                
                detail.Dependencies = newDeps;
                detail.DependencyCount = allResolvedDeps.Count;
                
                // Update cache with full dependencies
                if (!string.IsNullOrEmpty(detail.ResourceId))
                {
                    _detailCache.Store($"id:{detail.ResourceId}", detail);
                }
                
                return detail;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubService] Error in InspectPackageDependenciesAsync: {ex}");
                return detail;
            }
        
        }

        /// <summary>
        /// Find packages by name (for missing dependencies or updates)
        /// </summary>
        public async Task<Dictionary<string, HubPackageInfo>> FindPackagesAsync(IEnumerable<string> packageNames, CancellationToken cancellationToken = default)
        {
            var namesList = packageNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!namesList.Any())
                return new Dictionary<string, HubPackageInfo>();

            Trace.WriteLine($"[HubService] FindPackagesAsync called for {namesList.Count} packages");
            Debug.WriteLine($"[HubService] FindPackagesAsync called for {namesList.Count} packages");

            var result = new Dictionary<string, HubPackageInfo>(StringComparer.OrdinalIgnoreCase);
            
            // Batch requests to avoid API limits (e.g. 50 packages per request)
            const int BatchSize = 50;
            
            for (int i = 0; i < namesList.Count; i += BatchSize)
            {
                var batch = namesList.Skip(i).Take(BatchSize).ToList();
                
                try 
                {
                    Trace.WriteLine($"[HubService] Requesting batch {i/BatchSize + 1} ({batch.Count} items)...");
                    Debug.WriteLine($"[HubService] Requesting batch {i/BatchSize + 1} ({batch.Count} items)...");

                    var request = new JsonObject
                    {
                        ["source"] = "VaM",
                        ["action"] = "findPackages",
                        ["packages"] = string.Join(",", batch)
                    };

                    var response = await PostRequestAsync<HubFindPackagesResponse>(request.ToJsonString(), cancellationToken);

                    if (response?.Packages != null)
                    {
                        Trace.WriteLine($"[HubService] Batch {i/BatchSize + 1} received {response.Packages.Count} results");
                        Debug.WriteLine($"[HubService] Batch {i/BatchSize + 1} received {response.Packages.Count} results");

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
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HubService] FindPackagesAsync batch failed: {ex.Message}");
                    // Continue with next batch
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
            if (!forceRefresh && _cacheInitialized && _hubResourcesCache.IsLoaded && !_hubResourcesCache.NeedsRefresh())
                return true;

            try
            {
                var sw = Stopwatch.StartNew();
                
                // Try to load from disk cache first (if not already initialized)
                if (!_cacheInitialized)
                {
                    StatusChanged?.Invoke(this, "Loading Hub packages from cache...");
                    
                    if (await _hubResourcesCache.LoadFromDiskAsync())
                    {
                        // Cache loaded successfully
                        _cacheInitialized = true;
                        
                        sw.Stop();
                        StatusChanged?.Invoke(this, $"Loaded {_hubResourcesCache.PackageCount} packages from cache ({sw.ElapsedMilliseconds}ms)");
                        
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
                    sw.Stop();
                    var stats = _hubResourcesCache.GetStatistics();
                    var cacheInfo = stats.ConditionalHits > 0 ? $" (cached, {stats.ConditionalHitRate:F0}% conditional hits)" : "";
                    StatusChanged?.Invoke(this, $"Loaded {_hubResourcesCache.PackageCount} packages from Hub index{cacheInfo} ({sw.ElapsedMilliseconds}ms)");
                    
                    return true;
                }
                else
                {
                    // Fetch failed, but we might have stale cache data
                    if (_hubResourcesCache.PackageCount > 0)
                    {
                        StatusChanged?.Invoke(this, $"Using cached Hub index ({_hubResourcesCache.PackageCount} packages) - network unavailable");
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
                return _hubResourcesCache.PackageCount > 0;
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
                    _cacheInitialized = true;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Check if a package has an update available on Hub
        /// </summary>
        public bool HasUpdate(string packageGroupName, int localVersion)
        {
            return _hubResourcesCache.HasUpdate(packageGroupName, localVersion);
        }

        /// <summary>
        /// Get the Hub resource ID for a package
        /// </summary>
        public string GetResourceId(string packageName)
        {
            return _hubResourcesCache.GetResourceId(packageName);
        }
        
        /// <summary>
        /// Get the latest version number for a package group from Hub
        /// </summary>
        /// <param name="packageGroupName">Base package name without version</param>
        /// <returns>Latest version number, or -1 if not found</returns>
        public int GetLatestVersion(string packageGroupName)
        {
            return _hubResourcesCache.GetLatestVersion(packageGroupName);
        }
        
        /// <summary>
        /// Get the count of packages loaded from Hub
        /// </summary>
        /// <returns>Number of packages in the Hub index</returns>
        public int GetPackageCount()
        {
            return _hubResourcesCache.PackageCount;
        }
        
        /// <summary>
        /// Get all unique creator names from the packages index
        /// </summary>
        /// <returns>Sorted list of unique creator names</returns>
        public List<string> GetAllCreators()
        {
            return _hubResourcesCache.GetAllCreators();
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
                    catch (Exception)
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
                const int maxAttempts = 3;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                        using var response = await _httpClient.PostAsync(ApiUrl, content, cancellationToken);
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsStringAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (HttpRequestException ex) when (attempt < maxAttempts && IsTransientHubHttpFailure(ex) && !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(GetRetryDelay(attempt), cancellationToken);
                    }
                }

                // Should never get here due to return/throw paths.
                throw new HttpRequestException("Hub request failed after retries.");
            }
            finally
            {
                _requestThrottle.Release();
            }
        }

        private static bool IsTransientHubHttpFailure(HttpRequestException ex)
        {
            if (ex == null)
                return false;

            // Common transient case seen in logs:
            // System.Net.Http.HttpIOException: The response ended prematurely. (ResponseEnded)
            // Note: often the *outer* HttpRequestException message is generic and the detail is in InnerException.
            for (Exception cur = ex; cur != null; cur = cur.InnerException)
            {
                var msg = cur.Message ?? string.Empty;
                if (msg.IndexOf("prematurely", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("ResponseEnded", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                // Treat low-level IO failures as transient.
                if (cur is IOException)
                    return true;
            }

            // Retry on 5xx when HttpRequestException has StatusCode.
            if (ex.StatusCode.HasValue)
            {
                var code = (int)ex.StatusCode.Value;
                if (code >= 500 && code <= 599)
                    return true;
            }

            // Otherwise treat as non-transient.
            return false;
        }

        private static TimeSpan GetRetryDelay(int attempt)
        {
            // Small exponential backoff: 200ms, 500ms, 1s
            return attempt switch
            {
                1 => TimeSpan.FromMilliseconds(200),
                2 => TimeSpan.FromMilliseconds(500),
                _ => TimeSpan.FromMilliseconds(1000)
            };
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
        
    }
}
