using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Handles downloading missing packages from a GitHub-hosted JSON list with local fallback
    /// </summary>
    public class PackageDownloader : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _destinationFolder;
        private readonly string _localUrlsFilePath;
        private Dictionary<string, PackageDownloadInfo> _packageUrlCache;
        private DateTime _cacheLastUpdated = DateTime.MinValue;
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromHours(1);
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private bool _disposed;
        private bool _lastLoadWasFromGitHub = false;
        private JsonDatabaseService _jsonDbService;
        
        // Network permission check callback
        private Func<Task<bool>> _networkPermissionCheck;

        // Events for progress reporting
        public event EventHandler<DownloadProgressEventArgs> DownloadProgress;
        public event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;
        public event EventHandler<DownloadErrorEventArgs> DownloadError;

        /// <summary>
        /// Initializes a new instance of the PackageDownloader
        /// </summary>
        /// <param name="destinationFolder">Folder where packages should be downloaded (typically AddonPackages)</param>
        public PackageDownloader(string destinationFolder)
        {
            if (string.IsNullOrWhiteSpace(destinationFolder))
                throw new ArgumentNullException(nameof(destinationFolder));

            _destinationFolder = destinationFolder;
            
            // Check for CSV first, then JSON as fallback
            var csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "urls.csv");
            var jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "urls.json");
            _localUrlsFilePath = File.Exists(csvPath) ? csvPath : jsonPath;
            
            // Use HttpClientHandler to enable automatic redirect following and cookie support
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = new System.Net.CookieContainer()
            };
            
            // Add the Hub consent cookie
            handler.CookieContainer.Add(new System.Net.Cookie("vamhubconsent", "yes", "/", "hub.virtamate.com"));
            
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30) // Allow for large file downloads
            };
            
            // Set a user agent to avoid potential blocks
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "VPM/1.0");
        }

        /// <summary>
        /// Sets the network permission check callback
        /// This callback will be invoked before any network operations
        /// </summary>
        /// <param name="permissionCheck">Async function that returns true if network access is allowed</param>
        public void SetNetworkPermissionCheck(Func<Task<bool>> permissionCheck)
        {
            _networkPermissionCheck = permissionCheck;
        }

        /// <summary>
        /// Loads the complete package URL list from an encrypted database file
        /// </summary>
        /// <param name="githubUrl">URL to the encrypted database file (VPM.db)</param>
        /// <param name="forceRefresh">Force refresh even if cache is valid</param>
        /// <returns>True if successfully loaded</returns>
        public async Task<bool> LoadEncryptedPackageListAsync(string githubUrl, bool forceRefresh = false)
        {
            
            await _cacheLock.WaitAsync();
            try
            {
                // Check if cache is still valid
                if (!forceRefresh && _packageUrlCache != null && 
                    DateTime.Now - _cacheLastUpdated < _cacheExpiration)
                {
                    return true;
                }

                // Initialize JSON database service if needed (for new JSON format)
                if (_jsonDbService == null)
                {
                    _jsonDbService = new JsonDatabaseService();
                    _jsonDbService.SetNetworkPermissionCheck(_networkPermissionCheck);
                }

                // Load and decrypt JSON database
                var packageList = await _jsonDbService.LoadEncryptedJsonDatabaseAsync(githubUrl, forceRefresh);
                
                if (packageList == null || packageList.Count == 0)
                {
                    Console.WriteLine($"[PackageDownloader] Failed to load package list (null or empty)");
                    return false;
                }

                Console.WriteLine($"[PackageDownloader] Loaded {packageList.Count} packages from JSON database");
                
                // Show first few packages for debugging
                var samplePackages = packageList.Take(3).ToList();
                foreach (var pkg in samplePackages)
                {
                    Console.WriteLine($"[PackageDownloader]   Sample: '{pkg.FullPackageName}' -> '{pkg.PrimaryUrl}'");
                }

                // Convert to PackageDownloadInfo dictionary
                _packageUrlCache = packageList
                    .Select(pkg => new PackageDownloadInfo
                    {
                        PackageName = pkg.FullPackageName,
                        DownloadUrl = pkg.PrimaryUrl,
                        HubUrls = pkg.HubUrls ?? new List<string>(),
                        PdrUrls = pkg.PdrUrls ?? new List<string>()
                    })
                    .ToDictionary(p => p.PackageName, p => p, StringComparer.OrdinalIgnoreCase);

                Console.WriteLine($"[PackageDownloader] Created cache with {_packageUrlCache.Count} entries");
                
                // Show first few cache entries for debugging
                var sampleCache = _packageUrlCache.Take(3).ToList();
                foreach (var kvp in sampleCache)
                {
                    Console.WriteLine($"[PackageDownloader]   Cache: '{kvp.Key}' -> '{kvp.Value.DownloadUrl}'");
                }
                
                // Show unique creators in cache
                var creators = _packageUrlCache.Keys
                    .Select(k => k.Split('.')[0])
                    .Distinct()
                    .OrderBy(c => c)
                    .Take(20)
                    .ToList();
                Console.WriteLine($"[PackageDownloader] First 20 creators in cache: {string.Join(", ", creators)}");
                
                // Check if caelryn packages exist
                var caelrynPackages = _packageUrlCache.Keys
                    .Where(k => k.StartsWith("caelryn.", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k)
                    .ToList();
                if (caelrynPackages.Any())
                {
                    Console.WriteLine($"[PackageDownloader] Found {caelrynPackages.Count} caelryn packages total:");
                    foreach (var pkg in caelrynPackages)
                    {
                        Console.WriteLine($"[PackageDownloader]   - {pkg}");
                    }
                }
                else
                {
                    Console.WriteLine($"[PackageDownloader] – No caelryn packages found in cache!");
                }

                _cacheLastUpdated = DateTime.Now;
                _lastLoadWasFromGitHub = true;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// Loads the complete package URL list from GitHub or local fallback into memory
        /// The entire list is cached in memory to avoid network requests during downloads
        /// Supports both CSV and JSON formats
        /// </summary>
        /// <param name="githubUrl">URL to the GitHub-hosted CSV or JSON file</param>
        /// <param name="forceRefresh">Force refresh even if cache is valid</param>
        /// <returns>True if successfully loaded</returns>
        public async Task<bool> LoadPackageUrlListAsync(string githubUrl, bool forceRefresh = false)
        {
            await _cacheLock.WaitAsync();
            try
            {
                // Check if cache is still valid
                if (!forceRefresh && _packageUrlCache != null && 
                    DateTime.Now - _cacheLastUpdated < _cacheExpiration)
                {
                    return true;
                }

                // Try to load from GitHub first
                if (!string.IsNullOrWhiteSpace(githubUrl))
                {
                    // Check network permission before attempting network request
                    if (_networkPermissionCheck != null)
                    {
                        bool hasPermission = await _networkPermissionCheck();
                        if (!hasPermission)
                        {
                            // Skip to local fallback
                            goto LoadFromLocal;
                        }
                    }
                    
                    // Try up to 5 times with 2 second delays (to allow firewall approval)
                    const int maxRetries = 5;
                    const int retryDelayMs = 2000;
                    
                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        try
                        {
                            var content = await _httpClient.GetStringAsync(githubUrl);
                            
                            // Detect format by URL or content
                            bool isCsv = githubUrl.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || 
                                        !content.TrimStart().StartsWith("[");
                            
                            if (ParsePackageList(content, isCsv))
                            {
                                _cacheLastUpdated = DateTime.Now;
                                _lastLoadWasFromGitHub = true;
                                return true;
                            }
                        }
                        catch (Exception)
                        {
                            if (attempt < maxRetries)
                            {
                                await Task.Delay(retryDelayMs);
                            }
                        }
                    }
                }

                LoadFromLocal:
                // Fallback to local file (silent fallback, user not aware)
                if (File.Exists(_localUrlsFilePath))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(_localUrlsFilePath);
                        
                        // Detect format by extension or content
                        bool isCsv = _localUrlsFilePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || 
                                    !content.TrimStart().StartsWith("[");
                        
                        if (ParsePackageList(content, isCsv))
                        {
                            _cacheLastUpdated = DateTime.Now;
                            _lastLoadWasFromGitHub = false;
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                return false;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// Parses the content (CSV or JSON) into the package URL cache
        /// Optimized for performance and stability
        /// </summary>
        private bool ParsePackageList(string content, bool isCsv)
        {
            try
            {
                List<PackageDownloadInfo> packageList;
                
                if (isCsv)
                {
                    // Parse text format: package_name [whitespace] download_url
                    // Supports tabs, multiple spaces, or comma separation
                    packageList = new List<PackageDownloadInfo>();
                    var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    
                    foreach (var line in lines)
                    {
                        // Skip empty lines and comments (optimized check)
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                            
                        var trimmedLine = line.TrimStart();
                        if (trimmedLine.Length == 0 || trimmedLine[0] == '#')
                            continue;
                        
                        // Parse line efficiently using single-pass algorithm
                        var result = ParseCsvLine(trimmedLine);
                        if (result != null)
                        {
                            packageList.Add(result);
                        }
                    }
                }
                else
                {
                    // Parse JSON format with better error handling using source generation
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        TypeInfoResolver = JsonSourceGenerationContext.Default
                    };

                    packageList = JsonSerializer.Deserialize(content, JsonSourceGenerationContext.Default.ListPackageDownloadInfo);
                }
                
                if (packageList == null || packageList.Count == 0)
                {
                    return false;
                }

                // Build dictionary for fast lookup by package name
                // Use capacity hint for better performance
                var capacity = packageList.Count;
                _packageUrlCache = new Dictionary<string, PackageDownloadInfo>(capacity, StringComparer.OrdinalIgnoreCase);
                
                foreach (var package in packageList)
                {
                    if (!string.IsNullOrWhiteSpace(package.PackageName) && 
                        !string.IsNullOrWhiteSpace(package.DownloadUrl))
                    {
                        // Use TryAdd to avoid exceptions on duplicates
                        _packageUrlCache.TryAdd(package.PackageName, package);
                    }
                }

                return _packageUrlCache.Count > 0;
            }
            catch (JsonException)
            {
                // JSON parsing failed - return false to allow fallback
                return false;
            }
            catch (Exception)
            {
                // Other errors - return false
                return false;
            }
        }
        
        /// <summary>
        /// Efficiently parses a single CSV line to extract package name and download URL
        /// Uses single-pass algorithm without regex for optimal performance
        /// </summary>
        private PackageDownloadInfo ParseCsvLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;
                
            string packageName = null;
            string downloadUrl = null;
            
            // Detect delimiter and split in one pass
            int tabIndex = line.IndexOf('\t');
            if (tabIndex > 0)
            {
                // Tab-separated (most reliable)
                packageName = line.Substring(0, tabIndex).Trim();
                downloadUrl = line.Substring(tabIndex + 1).Trim();
            }
            else
            {
                // Check for multiple consecutive spaces (2 or more)
                int spaceStart = -1;
                int spaceCount = 0;
                
                for (int i = 0; i < line.Length; i++)
                {
                    if (line[i] == ' ')
                    {
                        if (spaceStart == -1)
                            spaceStart = i;
                        spaceCount++;
                    }
                    else if (spaceCount >= 2)
                    {
                        // Found multiple spaces - use as delimiter
                        packageName = line.Substring(0, spaceStart).Trim();
                        downloadUrl = line.Substring(i).Trim();
                        break;
                    }
                    else
                    {
                        // Reset counter
                        spaceStart = -1;
                        spaceCount = 0;
                    }
                }
                
                // If no multiple spaces found, try comma
                if (packageName == null)
                {
                    int commaIndex = line.IndexOf(',');
                    if (commaIndex > 0)
                    {
                        packageName = line.Substring(0, commaIndex).Trim();
                        downloadUrl = line.Substring(commaIndex + 1).Trim();
                    }
                }
            }
            
            // Validate and clean up package name and URL
            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(downloadUrl))
                return null;
                
            // Strip .var extension from package name if present
            if (packageName.Length > 4 && 
                packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
            {
                packageName = packageName.Substring(0, packageName.Length - 4);
            }
            
            return new PackageDownloadInfo
            {
                PackageName = packageName,
                DownloadUrl = downloadUrl
            };
        }

        /// <summary>
        /// Checks if a package is available for download
        /// </summary>
        /// <param name="packageName">Name of the package to check</param>
        /// <returns>True if package is available</returns>
        public bool IsPackageAvailable(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName) || _packageUrlCache == null)
                return false;

            // Handle .latest dependencies
            var resolvedName = ResolveLatestVersion(packageName);
            bool isAvailable = _packageUrlCache.ContainsKey(resolvedName);
            
            // If not found and doesn't end with .latest, try treating it as .latest
            if (!isAvailable && !packageName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
            {
                var latestAttempt = ResolveLatestVersion(packageName + ".latest");
                if (_packageUrlCache.ContainsKey(latestAttempt))
                {
                    return true;
                }
            }
            
            return isAvailable;
        }

        /// <summary>
        /// Gets download info for a package
        /// </summary>
        /// <param name="packageName">Name of the package</param>
        /// <returns>Download info or null if not found</returns>
        public PackageDownloadInfo GetPackageInfo(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName) || _packageUrlCache == null)
            {
                Console.WriteLine($"[PackageDownloader] GetPackageInfo: '{packageName}' - cache is null or empty");
                return null;
            }

            // Handle .latest dependencies
            var resolvedName = ResolveLatestVersion(packageName);
            
            if (_packageUrlCache.TryGetValue(resolvedName, out var info))
            {
                return info;
            }
            
            // If not found and doesn't end with .latest, try treating it as .latest
            if (!packageName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
            {
                var latestAttempt = ResolveLatestVersion(packageName + ".latest");
                
                if (_packageUrlCache.TryGetValue(latestAttempt, out info))
                {
                    return info;
                }
            }
            
            return null;
        }

        /// <summary>
        /// Resolves .latest package names to the highest available version
        /// </summary>
        /// <param name="packageName">Package name (may contain .latest)</param>
        /// <returns>Resolved package name with actual version</returns>
        private string ResolveLatestVersion(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName) || _packageUrlCache == null)
                return packageName;

            // Check if package name ends with .latest
            if (!packageName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                return packageName;

            // Extract base name (everything before .latest)
            var baseName = packageName.Substring(0, packageName.Length - 7); // Remove ".latest"

            // Find all versions of this package
            var versions = _packageUrlCache.Keys
                .Where(k => k.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (versions.Count == 0)
                return packageName; // Return original if no versions found

            // Extract version numbers and find highest
            var highestVersion = versions
                .Select(v => new
                {
                    Name = v,
                    Version = ExtractVersion(v)
                })
                .Where(v => v.Version >= 0)
                .OrderByDescending(v => v.Version)
                .FirstOrDefault();

            if (highestVersion != null)
            {
                return highestVersion.Name;
            }

            return packageName;
        }

        /// <summary>
        /// Extracts version number from package name
        /// </summary>
        /// <param name="packageName">Package name (e.g., "creator.package.2")</param>
        /// <returns>Version number or -1 if not found</returns>
        private int ExtractVersion(string packageName)
        {
            // Remove .var extension if present
            var name = packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase)
                ? packageName.Substring(0, packageName.Length - 4)
                : packageName;

            // Get last segment after final dot
            var lastDotIndex = name.LastIndexOf('.');
            if (lastDotIndex < 0)
                return -1;

            var versionStr = name.Substring(lastDotIndex + 1);
            
            // Try to parse as integer
            if (int.TryParse(versionStr, out var version))
                return version;

            return -1;
        }

        /// <summary>
        /// Gets all available versions of a package in descending order
        /// </summary>
        /// <param name="baseName">Base package name without version</param>
        /// <param name="maxVersions">Maximum number of versions to return (default 4)</param>
        /// <returns>List of package names sorted by version (highest first)</returns>
        private List<string> GetAvailableVersions(string baseName, int maxVersions = 4)
        {
            if (string.IsNullOrWhiteSpace(baseName) || _packageUrlCache == null)
                return new List<string>();

            // Find all versions
            var versions = _packageUrlCache.Keys
                .Where(k => k.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                .Select(v => new
                {
                    Name = v,
                    Version = ExtractVersion(v)
                })
                .Where(v => v.Version >= 0)
                .OrderByDescending(v => v.Version)
                .Take(maxVersions)
                .Select(v => v.Name)
                .ToList();

            return versions;
        }

        /// <summary>
        /// Downloads a missing package with automatic version fallback
        /// </summary>
        /// <param name="packageName">Name of the package to download</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if download was successful</returns>
        public async Task<bool> DownloadPackageAsync(string packageName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                OnDownloadError(packageName, "Package name is empty");
                return false;
            }

            if (_packageUrlCache == null)
            {
                OnDownloadError(packageName, "Package list not loaded");
                return false;
            }

            // Resolve .latest to actual version
            var resolvedName = ResolveLatestVersion(packageName);
            
            // Get list of versions to try (up to 4)
            List<string> versionsToTry = new List<string>();
            
            if (resolvedName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
            {
                // If .latest couldn't be resolved, try to find any versions
                var baseName = resolvedName.Substring(0, resolvedName.Length - 7);
                versionsToTry = GetAvailableVersions(baseName, 4);
                
                if (versionsToTry.Count == 0)
                {
                    OnDownloadError(packageName, "No versions found in download list");
                    return false;
                }
            }
            else
            {
                // For specific versions, get fallback versions
                var baseName = GetBaseName(resolvedName);
                versionsToTry = GetAvailableVersions(baseName, 4);
                
                // If no versions found, try the resolved name directly
                if (versionsToTry.Count == 0)
                {
                    if (_packageUrlCache.ContainsKey(resolvedName))
                    {
                        versionsToTry.Add(resolvedName);
                    }
                    else
                    {
                        OnDownloadError(packageName, "Package not found in download list");
                        return false;
                    }
                }
            }

            // Try each version until one succeeds (max 4 tries)
            Exception lastException = null;
            foreach (var versionToTry in versionsToTry.Take(4))
            {
                if (!_packageUrlCache.TryGetValue(versionToTry, out var packageInfo))
                    continue;

                try
                {
                    bool success = await DownloadPackageFromUrlAsync(versionToTry, packageInfo, cancellationToken);
                    
                    if (success)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    
                    // Continue to next version
                }
            }

            // All attempts failed
            var errorMsg = lastException != null 
                ? $"All download attempts failed. Last error: {lastException.Message}"
                : "All download attempts failed";
            OnDownloadError(packageName, errorMsg);
            return false;
        }

        /// <summary>
        /// Extracts the source domain from a download URL
        /// </summary>
        private string ExtractSourceDomain(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Unknown";
            
            try
            {
                var uri = new Uri(url);
                var host = uri.Host;
                
                // Extract root domain (e.g., "pixeldrain.com" from "dl.pixeldrain.com")
                var parts = host.Split('.');
                if (parts.Length >= 2)
                {
                    // Return last two parts (domain.extension)
                    return $"{parts[parts.Length - 2]}.{parts[parts.Length - 1]}";
                }
                
                return host;
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Gets base package name without version number
        /// </summary>
        private string GetBaseName(string packageName)
        {
            // Remove .var extension if present
            var name = packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase)
                ? packageName.Substring(0, packageName.Length - 4)
                : packageName;

            // Remove version number (last segment after dot if it's a number)
            var lastDotIndex = name.LastIndexOf('.');
            if (lastDotIndex > 0)
            {
                var lastSegment = name.Substring(lastDotIndex + 1);
                if (int.TryParse(lastSegment, out _))
                {
                    return name.Substring(0, lastDotIndex);
                }
            }

            return name;
        }

        /// <summary>
        /// Downloads a package from a specific URL
        /// </summary>
        private async Task<bool> DownloadPackageFromUrlAsync(string packageName, PackageDownloadInfo packageInfo, CancellationToken cancellationToken)
        {
            try
            {
                // Check network permission before attempting download
                if (_networkPermissionCheck != null)
                {
                    bool hasPermission = await _networkPermissionCheck();
                    
                    if (!hasPermission)
                    {
                        OnDownloadError(packageName, "Network access denied by user");
                        return false;
                    }
                }
                
                // Ensure package name has .var extension
                var fileName = packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase) 
                    ? packageName 
                    : $"{packageName}.var";
                
                var destinationPath = Path.Combine(_destinationFolder, fileName);

                // Check if file already exists
                if (File.Exists(destinationPath))
                {
                    Console.WriteLine($"[PackageDownloader] File already exists: {fileName}");
                    OnDownloadCompleted(packageName, destinationPath, true);
                    return true;
                }

                // Ensure destination folder exists
                Directory.CreateDirectory(_destinationFolder);

                // Build list of URLs to try: Hub first (faster, no speed limit), then Pixeldrain as fallback
                var urlsToTry = new List<string>();
                
                // Add Hub URLs first (preferred - faster, no speed limit)
                if (packageInfo.HubUrls != null && packageInfo.HubUrls.Any())
                {
                    Console.WriteLine($"[PackageDownloader] Found {packageInfo.HubUrls.Count} Hub URL(s) for {packageName}");
                    urlsToTry.AddRange(packageInfo.HubUrls);
                }
                
                // Add Pixeldrain URLs as fallback
                if (packageInfo.PdrUrls != null && packageInfo.PdrUrls.Any())
                {
                    Console.WriteLine($"[PackageDownloader] Found {packageInfo.PdrUrls.Count} Pixeldrain URL(s) for {packageName} (fallback)");
                    urlsToTry.AddRange(packageInfo.PdrUrls);
                }
                
                // If no specific URLs, use the primary URL
                if (!urlsToTry.Any() && !string.IsNullOrEmpty(packageInfo.DownloadUrl))
                {
                    Console.WriteLine($"[PackageDownloader] Using primary URL for {packageName}");
                    urlsToTry.Add(packageInfo.DownloadUrl);
                }
                
                if (!urlsToTry.Any())
                {
                    Console.WriteLine($"[PackageDownloader] ✗ No download URLs available for {packageName}");
                    OnDownloadError(packageName, "No download URLs available");
                    return false;
                }
                
                // Try each URL until one succeeds
                Exception lastException = null;
                const int maxRetries = 3; // Retry each URL up to 3 times
                
                for (int i = 0; i < urlsToTry.Count; i++)
                {
                    var url = urlsToTry[i];
                    
                    // Detect URL type based on domain
                    var urlType = url.Contains("hub.virtamate.com", StringComparison.OrdinalIgnoreCase) ? "Hub" :
                                  url.Contains("pixeldrain.com", StringComparison.OrdinalIgnoreCase) ? "Pixeldrain" :
                                  "Unknown";
                    
                    // Try each URL with retries
                    for (int retry = 0; retry < maxRetries; retry++)
                    {
                        if (retry > 0)
                        {
                            // Wait before retrying (exponential backoff: 2s, 4s, 8s)
                            int delaySeconds = (int)Math.Pow(2, retry);
                            Console.WriteLine($"[PackageDownloader] Waiting {delaySeconds} seconds before retry {retry + 1}/{maxRetries}...");
                            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                        }
                        
                        Console.WriteLine($"[PackageDownloader] Attempt {i + 1}/{urlsToTry.Count} (Retry {retry + 1}/{maxRetries}): Trying {urlType} URL for {packageName}");
                        Console.WriteLine($"[PackageDownloader]   URL: {url}");
                        
                        try
                        {
                            bool success = await DownloadFromSingleUrlAsync(packageName, url, destinationPath, cancellationToken);
                            if (success)
                            {
                                Console.WriteLine($"[PackageDownloader] ✓ Successfully downloaded from {urlType} URL");
                                return true;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // User cancelled - don't retry
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                            Console.WriteLine($"[PackageDownloader] ✗ {urlType} URL failed: {ex.Message}");
                            
                            // Check if it's a rate limit or server error that might benefit from retry
                            bool shouldRetry = ex.Message.Contains("400") || 
                                             ex.Message.Contains("429") || 
                                             ex.Message.Contains("503") ||
                                             ex.Message.Contains("Too Many Requests") ||
                                             ex.Message.Contains("Service Unavailable");
                            
                            if (!shouldRetry || retry == maxRetries - 1)
                            {
                                // Don't retry this URL anymore, try next URL
                                break;
                            }
                        }
                    }
                    
                    // Continue to next URL if current one failed all retries
                    if (i < urlsToTry.Count - 1)
                    {
                        Console.WriteLine($"[PackageDownloader] Trying next URL...");
                    }
                }
                
                // All URLs failed
                var errorMsg = lastException != null 
                    ? $"All download URLs failed. Last error: {lastException.Message}"
                    : "All download URLs failed";
                Console.WriteLine($"[PackageDownloader] ✗ {errorMsg}");
                OnDownloadError(packageName, errorMsg);
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PackageDownloader] ✗ Unexpected error: {ex.Message}");
                OnDownloadError(packageName, $"Download error: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Downloads a package from a single URL
        /// </summary>
        private async Task<bool> DownloadFromSingleUrlAsync(string packageName, string url, string destinationPath, CancellationToken cancellationToken, int retryCount = 0)
        {
            try
            {
                // Download with progress reporting
                using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    // Log HTTP response details
                    Console.WriteLine($"[PackageDownloader]   HTTP Status: {(int)response.StatusCode} {response.StatusCode}");
                    Console.WriteLine($"[PackageDownloader]   Content-Type: {response.Content.Headers.ContentType?.MediaType ?? "unknown"}");
                    
                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    Console.WriteLine($"[PackageDownloader]   Content-Length: {totalBytes} bytes ({totalBytes / 1024.0:F1} KB)");
                    
                    // Check if content type suggests an error page (HTML instead of ZIP)
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    {
                        // Prevent infinite retry loop
                        if (retryCount >= 2)
                        {
                            Console.WriteLine($"[PackageDownloader]   ✗ Still getting HTML after {retryCount} retries - giving up");
                            throw new Exception("Hub returned HTML page even after retry attempts");
                        }
                        
                        Console.WriteLine($"[PackageDownloader]   – Warning: Server returned HTML instead of a file (likely Hub 'I agree' page)");
                        Console.WriteLine($"[PackageDownloader]   Note: vamhubconsent cookie should already be set, but server still returned HTML");
                        Console.WriteLine($"[PackageDownloader]   This may indicate the package requires login or has other restrictions");
                        
                        throw new Exception("Hub returned HTML page - package may require login or have access restrictions");
                    }
                    
                    response.EnsureSuccessStatusCode();

                    var downloadedBytes = 0L;

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        int bytesRead;
                        var lastProgressReport = DateTime.Now;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                            downloadedBytes += bytesRead;

                            // Report progress every 500ms to avoid UI spam
                            if (DateTime.Now - lastProgressReport > TimeSpan.FromMilliseconds(500))
                            {
                                var progressPercentage = totalBytes > 0 ? (int)((downloadedBytes * 100) / totalBytes) : 0;
                                var source = ExtractSourceDomain(url);
                                OnDownloadProgress(packageName, downloadedBytes, totalBytes, progressPercentage, source);
                                lastProgressReport = DateTime.Now;
                            }
                        }
                    }
                    
                    Console.WriteLine($"[PackageDownloader]   Downloaded: {downloadedBytes} bytes ({downloadedBytes / 1024.0:F1} KB)");
                }

                // Validate downloaded file
                var fileInfo = new FileInfo(destinationPath);
                Console.WriteLine($"[PackageDownloader] Validating downloaded file: {fileInfo.Length} bytes ({fileInfo.Length / 1024.0:F1} KB)");
                
                // Validate ZIP structure and check for meta.json
                bool isValidVarPackage = false;
                try
                {
                    using (var zip = System.IO.Compression.ZipFile.OpenRead(destinationPath))
                    {
                        // Check if ZIP can be read
                        var entryCount = zip.Entries.Count;
                        Console.WriteLine($"[PackageDownloader] ZIP contains {entryCount} entries");
                        
                        // Check for meta.json file (required for VAR packages)
                        var metaEntry = zip.Entries.FirstOrDefault(e => 
                            e.Name.Equals("meta.json", StringComparison.OrdinalIgnoreCase));
                        
                        if (metaEntry != null)
                        {
                            Console.WriteLine($"[PackageDownloader] ✓ Found meta.json in package");
                            isValidVarPackage = true;
                        }
                        else
                        {
                            Console.WriteLine($"[PackageDownloader] ✗ No meta.json found in package");
                        }
                    }
                }
                catch (Exception zipEx)
                {
                    Console.WriteLine($"[PackageDownloader] ✗ Downloaded file is not a valid ZIP/VAR: {zipEx.Message}");
                    
                    // Delete the corrupted file
                    try
                    {
                        File.Delete(destinationPath);
                        Console.WriteLine($"[PackageDownloader] Deleted invalid file");
                    }
                    catch (Exception deleteEx)
                    {
                        Console.WriteLine($"[PackageDownloader] Warning: Could not delete invalid file: {deleteEx.Message}");
                    }
                    
                    throw new Exception($"Downloaded file is not a valid VAR package: {zipEx.Message}");
                }
                
                // If no meta.json found, the package is invalid
                if (!isValidVarPackage)
                {
                    Console.WriteLine($"[PackageDownloader] ✗ Invalid VAR package: missing meta.json");
                    
                    // Delete the invalid file
                    try
                    {
                        File.Delete(destinationPath);
                        Console.WriteLine($"[PackageDownloader] Deleted invalid package");
                    }
                    catch (Exception deleteEx)
                    {
                        Console.WriteLine($"[PackageDownloader] Warning: Could not delete invalid file: {deleteEx.Message}");
                    }
                    
                    throw new Exception($"Invalid VAR package: meta.json not found");
                }
                
                Console.WriteLine($"[PackageDownloader] ✓ Valid VAR package: {fileInfo.Length} bytes ({fileInfo.Length / 1024.0:F1} KB)");

                OnDownloadCompleted(packageName, destinationPath, false);
                return true;
            }
            catch (OperationCanceledException)
            {
                // Delete partial download if it exists
                if (File.Exists(destinationPath))
                {
                    try
                    {
                        File.Delete(destinationPath);
                        Console.WriteLine($"[PackageDownloader] Deleted partial download: {destinationPath}");
                    }
                    catch (Exception deleteEx)
                    {
                        Console.WriteLine($"[PackageDownloader] Warning: Could not delete partial download: {deleteEx.Message}");
                    }
                }
                
                // Re-throw cancellation to stop trying other URLs
                throw;
            }
            catch (HttpRequestException ex)
            {
                // Don't call OnDownloadError here - let the caller handle it
                throw new Exception($"HTTP error: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                // Don't call OnDownloadError here - let the caller handle it
                throw new Exception($"Download error: {ex.Message}", ex);
            }
        }

        private Func<string, bool> _isPackageCancelledCheck;
        
        /// <summary>
        /// Sets a function to check if a package has been cancelled by the user
        /// </summary>
        public void SetPackageCancelledCheck(Func<string, bool> cancelledCheck)
        {
            _isPackageCancelledCheck = cancelledCheck;
        }
        
        /// <summary>
        /// Downloads multiple packages
        /// </summary>
        /// <param name="packageNames">List of package names to download</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Dictionary of package names and their download success status</returns>
        public async Task<Dictionary<string, bool>> DownloadPackagesAsync(IEnumerable<string> packageNames, CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, bool>();

            foreach (var packageName in packageNames)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Check if package was cancelled by user before starting download
                if (_isPackageCancelledCheck != null && _isPackageCancelledCheck(packageName))
                {
                    results[packageName] = false;
                    continue; // Skip this package and move to next
                }

                var success = await DownloadPackageAsync(packageName, cancellationToken);
                results[packageName] = success;
            }

            return results;
        }

        /// <summary>
        /// Gets a list of all available packages in the download list
        /// </summary>
        /// <returns>List of package names</returns>
        public List<string> GetAvailablePackages()
        {
            return _packageUrlCache?.Keys.ToList() ?? new List<string>();
        }
        
        /// <summary>
        /// Gets all package names from the online database cache
        /// Alias for GetAvailablePackages for clarity in update checking
        /// </summary>
        public List<string> GetAllPackageNames()
        {
            return GetAvailablePackages();
        }

        /// <summary>
        /// Gets the count of packages in the cache
        /// </summary>
        /// <returns>Number of packages loaded</returns>
        public int GetPackageCount()
        {
            return _packageUrlCache?.Count ?? 0;
        }

        /// <summary>
        /// Gets whether the last load was from GitHub (true) or local fallback (false)
        /// </summary>
        /// <returns>True if loaded from GitHub, false if from local or not loaded</returns>
        public bool WasLastLoadFromGitHub()
        {
            return _lastLoadWasFromGitHub && _packageUrlCache != null;
        }

        /// <summary>
        /// Clears the cached package list
        /// </summary>
        public void ClearCache()
        {
            _cacheLock.Wait();
            try
            {
                _packageUrlCache = null;
                _cacheLastUpdated = DateTime.MinValue;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        #region Event Handlers

        protected virtual void OnDownloadProgress(string packageName, long downloadedBytes, long totalBytes, int percentage, string downloadSource = null)
        {
            DownloadProgress?.Invoke(this, new DownloadProgressEventArgs
            {
                PackageName = packageName,
                DownloadedBytes = downloadedBytes,
                TotalBytes = totalBytes,
                ProgressPercentage = percentage,
                DownloadSource = downloadSource
            });
        }

        protected virtual void OnDownloadCompleted(string packageName, string filePath, bool alreadyExisted)
        {
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs
            {
                PackageName = packageName,
                FilePath = filePath,
                AlreadyExisted = alreadyExisted
            });
        }

        protected virtual void OnDownloadError(string packageName, string errorMessage)
        {
            DownloadError?.Invoke(this, new DownloadErrorEventArgs
            {
                PackageName = packageName,
                ErrorMessage = errorMessage
            });
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _httpClient?.Dispose();
                _cacheLock?.Dispose();
            }

            _disposed = true;
        }

        #endregion
    }

    #region Event Args Classes

    public class DownloadProgressEventArgs : EventArgs
    {
        public string PackageName { get; set; }
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public int ProgressPercentage { get; set; }
        public string DownloadSource { get; set; }
    }

    public class DownloadCompletedEventArgs : EventArgs
    {
        public string PackageName { get; set; }
        public string FilePath { get; set; }
        public bool AlreadyExisted { get; set; }
    }

    public class DownloadErrorEventArgs : EventArgs
    {
        public string PackageName { get; set; }
        public string ErrorMessage { get; set; }
    }

    #endregion
}

