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
using VPM.Models;

namespace VPM.Services
{
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
        private readonly SemaphoreSlim _requestLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        // Cache for packages.json (packageId -> resourceId mapping)
        private Dictionary<string, string> _packageIdToResourceId;
        private Dictionary<string, int> _packageGroupToLatestVersion;
        private DateTime _packagesCacheTime = DateTime.MinValue;
        private readonly TimeSpan _packagesCacheExpiry = TimeSpan.FromHours(1);

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
        }

        #region Search & Browse

        /// <summary>
        /// Get available filter options from the Hub API
        /// </summary>
        public async Task<HubFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
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

                return options;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubService] Failed to get filter options: {ex.Message}");
                Debug.WriteLine($"[HubService] Exception details: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Search for resources on the Hub
        /// </summary>
        public async Task<HubSearchResponse> SearchResourcesAsync(HubSearchParams searchParams, CancellationToken cancellationToken = default)
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
            return response;
        }

        /// <summary>
        /// Get detailed information about a specific resource
        /// </summary>
        public async Task<HubResourceDetail> GetResourceDetailAsync(string resourceId, bool isPackageName = false, CancellationToken cancellationToken = default)
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

            return detail;
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
        /// Load the packages.json from Hub CDN for version checking
        /// </summary>
        public async Task<bool> LoadPackagesJsonAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            if (!forceRefresh && _packageIdToResourceId != null && DateTime.Now - _packagesCacheTime < _packagesCacheExpiry)
                return true;

            try
            {
                StatusChanged?.Invoke(this, "Loading Hub packages index...");

                var response = await _httpClient.GetStringAsync(PackagesJsonUrl, cancellationToken);
                var packagesJson = JsonDocument.Parse(response);

                _packageIdToResourceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _packageGroupToLatestVersion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var prop in packagesJson.RootElement.EnumerateObject())
                {
                    var packageName = prop.Name.Replace(".var", "");
                    
                    // Handle both string and number resource IDs
                    string resourceId;
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        resourceId = prop.Value.GetString();
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        resourceId = prop.Value.GetRawText();
                    }
                    else
                    {
                        resourceId = prop.Value.ToString();
                    }

                    _packageIdToResourceId[packageName] = resourceId;

                    // Extract version info
                    var version = ExtractVersion(packageName);
                    var groupName = GetPackageGroupName(packageName);

                    if (version >= 0 && !string.IsNullOrEmpty(groupName))
                    {
                        if (!_packageGroupToLatestVersion.TryGetValue(groupName, out var currentLatest) || version > currentLatest)
                        {
                            _packageGroupToLatestVersion[groupName] = version;
                        }
                    }
                }

                _packagesCacheTime = DateTime.Now;
                StatusChanged?.Invoke(this, $"Loaded {_packageIdToResourceId.Count} packages from Hub index");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubService] Failed to load Hub packages index: {ex.Message}");
                StatusChanged?.Invoke(this, $"Failed to load Hub packages index: {ex.Message}");
                return false;
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
            if (shouldStartProcessing)
            {
                Task.Run(() => ProcessDownloadQueueAsync());
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
                        Debug.WriteLine($"[HubService] Download error: {ex.Message}");
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
            catch (JsonException ex)
            {
                Debug.WriteLine($"[HubService] JSON deserialization error: {ex.Message}");
                Debug.WriteLine($"[HubService] Response was: {responseJson?.Substring(0, Math.Min(500, responseJson?.Length ?? 0))}...");
                throw;
            }
        }

        private async Task<string> PostRequestRawAsync(string jsonContent, CancellationToken cancellationToken)
        {
            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(ApiUrl, content, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            finally
            {
                _requestLock.Release();
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

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _requestLock?.Dispose();
                _disposed = true;
            }
        }
    }
}
