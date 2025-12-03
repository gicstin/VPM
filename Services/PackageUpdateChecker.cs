using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Efficiently checks for package updates using Hub resources or local links.txt file.
    /// Priority: local links.txt (if exists) -> online Hub resources
    /// </summary>
    public class PackageUpdateChecker
    {
        private readonly HubService _hubService;
        private Dictionary<string, int> _localPackageVersions;
        private List<PackageUpdateInfo> _availableUpdates;
        
        // Local links cache (from links.txt)
        private Dictionary<string, LocalLinkInfo> _localLinksCache;
        private Dictionary<string, int> _localLinksVersionLookup;
        private bool _usingLocalLinks;
        
        /// <summary>
        /// Path to the local links.txt file (same folder as executable)
        /// </summary>
        private static string LocalLinksFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "links.txt");
        
        /// <summary>
        /// Whether the checker is using local links.txt file
        /// </summary>
        public bool IsUsingLocalLinks => _usingLocalLinks;
        
        /// <summary>
        /// Event raised when status changes
        /// </summary>
        public event EventHandler<string> StatusChanged;
        
        public PackageUpdateChecker(HubService hubService)
        {
            _hubService = hubService ?? throw new ArgumentNullException(nameof(hubService));
            _localPackageVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _availableUpdates = new List<PackageUpdateInfo>();
            _localLinksCache = new Dictionary<string, LocalLinkInfo>(StringComparer.OrdinalIgnoreCase);
            _localLinksVersionLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// Loads the package source (local links.txt or Hub resources)
        /// </summary>
        /// <param name="forceRefresh">Force refresh even if already loaded</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if successfully loaded</returns>
        public async Task<bool> LoadPackageSourceAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            // Check for local links.txt file first
            if (File.Exists(LocalLinksFilePath))
            {
                try
                {
                    StatusChanged?.Invoke(this, "Loading local links.txt...");
                    var loaded = await LoadLocalLinksFileAsync(cancellationToken);
                    if (loaded)
                    {
                        _usingLocalLinks = true;
                        StatusChanged?.Invoke(this, $"Loaded {_localLinksCache.Count} packages from local links.txt");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PackageUpdateChecker] Failed to load local links.txt: {ex.Message}");
                    StatusChanged?.Invoke(this, $"Failed to load links.txt: {ex.Message}");
                }
            }
            
            // Fallback to online Hub resources
            _usingLocalLinks = false;
            StatusChanged?.Invoke(this, "Loading Hub package index...");
            var hubLoaded = await _hubService.LoadPackagesJsonAsync(forceRefresh, cancellationToken);
            
            if (hubLoaded)
            {
                StatusChanged?.Invoke(this, "Hub package index loaded");
            }
            else
            {
                StatusChanged?.Invoke(this, "Failed to load Hub package index");
            }
            
            return hubLoaded;
        }
        
        /// <summary>
        /// Loads the local links.txt file
        /// Supports multiple formats:
        /// - Tab-separated: package.name.version[TAB]URL
        /// - Comma-separated: package.name.version, URL
        /// - Space-separated: package.name.version  URL (2+ spaces)
        /// </summary>
        private async Task<bool> LoadLocalLinksFileAsync(CancellationToken cancellationToken)
        {
            _localLinksCache.Clear();
            _localLinksVersionLookup.Clear();
            
            var content = await File.ReadAllTextAsync(LocalLinksFilePath, cancellationToken);
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            int parsedCount = 0;
            int skippedCount = 0;
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    skippedCount++;
                    continue;
                }
                
                var trimmed = line.TrimStart();
                
                // Skip comments (lines starting with # or //)
                if (trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                {
                    skippedCount++;
                    continue;
                }
                
                var parsed = ParseLocalLinkLine(trimmed);
                if (parsed != null)
                {
                    _localLinksCache[parsed.PackageName] = parsed;
                    parsedCount++;
                    
                    // Build version lookup for update checking
                    var baseName = GetBaseName(parsed.PackageName);
                    var version = ExtractVersion(parsed.PackageName);
                    
                    if (version >= 0 && !string.IsNullOrEmpty(baseName))
                    {
                        if (!_localLinksVersionLookup.TryGetValue(baseName, out var currentLatest) || version > currentLatest)
                        {
                            _localLinksVersionLookup[baseName] = version;
                        }
                    }
                }
                else
                {
                    skippedCount++;
                    Debug.WriteLine($"[PackageUpdateChecker] Failed to parse line: {line.Substring(0, Math.Min(50, line.Length))}...");
                }
            }
            
            Debug.WriteLine($"[PackageUpdateChecker] Loaded {parsedCount} packages from links.txt, skipped {skippedCount} lines");
            return _localLinksCache.Count > 0;
        }
        
        /// <summary>
        /// Parses a single line from links.txt
        /// Supports multiple formats:
        /// - Tab-separated: package.name.version[TAB]URL
        /// - Comma-separated: package.name.version, URL  
        /// - Space-separated: package.name.version  URL (2+ spaces before URL)
        /// </summary>
        private LocalLinkInfo ParseLocalLinkLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;
            
            string packageName = null;
            string link = null;
            
            // Try tab-separated first (most reliable)
            int tabIndex = line.IndexOf('\t');
            if (tabIndex > 0)
            {
                packageName = line.Substring(0, tabIndex).Trim();
                link = line.Substring(tabIndex + 1).Trim();
            }
            else
            {
                // Try to find URL pattern (http:// or https://)
                int httpIndex = line.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
                if (httpIndex < 0)
                    httpIndex = line.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
                
                if (httpIndex > 0)
                {
                    // Everything before the URL is the package name
                    packageName = line.Substring(0, httpIndex).TrimEnd(' ', ',', '\t');
                    link = line.Substring(httpIndex).Trim();
                }
                else
                {
                    // Try comma-separated
                    int commaIndex = line.IndexOf(',');
                    if (commaIndex > 0)
                    {
                        packageName = line.Substring(0, commaIndex).Trim();
                        link = line.Substring(commaIndex + 1).Trim();
                    }
                    else
                    {
                        // Try double-space separated
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
                                packageName = line.Substring(0, spaceStart).Trim();
                                link = line.Substring(i).Trim();
                                break;
                            }
                            else
                            {
                                spaceStart = -1;
                                spaceCount = 0;
                            }
                        }
                    }
                }
            }
            
            // Validate we got both parts
            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(link))
                return null;
            
            // Remove .var extension if present
            if (packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                packageName = packageName.Substring(0, packageName.Length - 4);
            
            // Validate URL looks reasonable
            if (!link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                !link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            
            return new LocalLinkInfo
            {
                PackageName = packageName,
                DownloadUrl = link
            };
        }
        
        /// <summary>
        /// Checks for updates by comparing local packages with Hub resources or local links.txt
        /// </summary>
        /// <param name="localPackages">List of local package names with versions</param>
        /// <returns>List of packages that have updates available</returns>
        public async Task<List<PackageUpdateInfo>> CheckForUpdatesAsync(IEnumerable<PackageItem> localPackages)
        {
            _availableUpdates.Clear();
            _localPackageVersions.Clear();
            
            // Build a fast lookup dictionary of local packages with their HIGHEST versions
            foreach (var package in localPackages)
            {
                var baseName = GetBaseName(package.Name);
                var version = ExtractVersion(package.Name);
                
                if (version < 0)
                    continue;
                
                if (!_localPackageVersions.ContainsKey(baseName) || _localPackageVersions[baseName] < version)
                {
                    _localPackageVersions[baseName] = version;
                }
            }
            
            // Ensure package source is loaded
            if (_usingLocalLinks && _localLinksCache.Count == 0)
            {
                await LoadPackageSourceAsync();
            }
            else if (!_usingLocalLinks)
            {
                await _hubService.LoadPackagesJsonAsync();
            }
            
            // Check for updates
            await Task.Run(() =>
            {
                if (_usingLocalLinks)
                {
                    CheckUpdatesFromLocalLinks();
                }
                else
                {
                    CheckUpdatesFromHub();
                }
            });
            
            return _availableUpdates;
        }
        
        /// <summary>
        /// Checks for updates using local links.txt data
        /// </summary>
        private void CheckUpdatesFromLocalLinks()
        {
            foreach (var kvp in _localPackageVersions)
            {
                var baseName = kvp.Key;
                var localVersion = kvp.Value;
                
                if (_localLinksVersionLookup.TryGetValue(baseName, out var latestVersion))
                {
                    if (latestVersion > localVersion)
                    {
                        _availableUpdates.Add(new PackageUpdateInfo
                        {
                            PackageName = $"{baseName}.{localVersion}",
                            OnlinePackageName = $"{baseName}.{latestVersion}",
                            LocalVersion = localVersion,
                            OnlineVersion = latestVersion,
                            BaseName = baseName
                        });
                    }
                }
            }
        }
        
        /// <summary>
        /// Checks for updates using Hub resources
        /// </summary>
        private void CheckUpdatesFromHub()
        {
            foreach (var kvp in _localPackageVersions)
            {
                var baseName = kvp.Key;
                var localVersion = kvp.Value;
                
                if (_hubService.HasUpdate(baseName, localVersion))
                {
                    // Get the latest version from Hub
                    // We need to find the actual latest version number
                    var latestVersion = GetHubLatestVersion(baseName);
                    
                    if (latestVersion > localVersion)
                    {
                        _availableUpdates.Add(new PackageUpdateInfo
                        {
                            PackageName = $"{baseName}.{localVersion}",
                            OnlinePackageName = $"{baseName}.{latestVersion}",
                            LocalVersion = localVersion,
                            OnlineVersion = latestVersion,
                            BaseName = baseName
                        });
                    }
                }
            }
        }
        
        /// <summary>
        /// Gets the latest version number for a package from Hub
        /// </summary>
        private int GetHubLatestVersion(string baseName)
        {
            return _hubService.GetLatestVersion(baseName);
        }
        
        /// <summary>
        /// Gets download info for a package from local links or Hub
        /// </summary>
        /// <param name="packageName">Package name to get download info for</param>
        /// <returns>Download URL or null if not found</returns>
        public string GetDownloadUrl(string packageName)
        {
            if (_usingLocalLinks)
            {
                // Remove .var extension if present
                var cleanName = packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase)
                    ? packageName.Substring(0, packageName.Length - 4)
                    : packageName;
                
                if (_localLinksCache.TryGetValue(cleanName, out var linkInfo))
                {
                    return linkInfo.DownloadUrl;
                }
            }
            
            // For Hub, return null - caller should use HubService directly
            return null;
        }
        
        /// <summary>
        /// Gets all package names from the current source
        /// </summary>
        public List<string> GetAllPackageNames()
        {
            if (_usingLocalLinks)
            {
                return _localLinksCache.Keys.ToList();
            }
            
            // For Hub, this would require accessing internal Hub data
            // Return empty - caller should use HubService directly
            return new List<string>();
        }
        
        /// <summary>
        /// Gets the count of available updates
        /// </summary>
        public int GetUpdateCount()
        {
            return _availableUpdates?.Count ?? 0;
        }
        
        /// <summary>
        /// Gets the list of package names that have updates
        /// </summary>
        public List<string> GetUpdatePackageNames()
        {
            return _availableUpdates?.Select(u => u.PackageName).ToList() ?? new List<string>();
        }
        
        /// <summary>
        /// Gets base package name without version number
        /// Handles special characters like brackets in package names
        /// </summary>
        private string GetBaseName(string packageName)
        {
            // Remove .var extension if present
            var name = packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase)
                ? packageName.Substring(0, packageName.Length - 4)
                : packageName;

            // Find the last dot that's followed by a number
            // This handles cases like "Creator.[Special]_Name.5"
            int lastDotIndex = -1;
            for (int i = name.Length - 1; i >= 0; i--)
            {
                if (name[i] == '.')
                {
                    // Check if what follows is a number
                    if (i + 1 < name.Length)
                    {
                        var afterDot = name.Substring(i + 1);
                        if (int.TryParse(afterDot, out _))
                        {
                            lastDotIndex = i;
                            break;
                        }
                    }
                }
            }

            if (lastDotIndex > 0)
            {
                return name.Substring(0, lastDotIndex);
            }

            return name;
        }
        
        /// <summary>
        /// Extracts version number from package name
        /// Handles special characters like brackets in package names
        /// </summary>
        public int ExtractVersion(string packageName)
        {
            // Remove .var extension if present
            var name = packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase)
                ? packageName.Substring(0, packageName.Length - 4)
                : packageName;

            // Find the last dot that's followed by a number
            for (int i = name.Length - 1; i >= 0; i--)
            {
                if (name[i] == '.')
                {
                    if (i + 1 < name.Length)
                    {
                        var afterDot = name.Substring(i + 1);
                        if (int.TryParse(afterDot, out var version))
                        {
                            return version;
                        }
                    }
                }
            }

            return -1;
        }
    }
    
    /// <summary>
    /// Information about a package update
    /// </summary>
    public class PackageUpdateInfo
    {
        public string PackageName { get; set; }  // Local version name (what user has)
        public string OnlinePackageName { get; set; }  // Online version name (what's available)
        public string BaseName { get; set; }
        public int LocalVersion { get; set; }
        public int OnlineVersion { get; set; }
    }
    
    /// <summary>
    /// Information about a package from local links.txt file
    /// </summary>
    public class LocalLinkInfo
    {
        public string PackageName { get; set; }
        public string DownloadUrl { get; set; }
    }
}

