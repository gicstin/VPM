using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Efficiently checks for package updates by comparing local packages with online database
    /// </summary>
    public class PackageUpdateChecker
    {
        private readonly PackageDownloader _packageDownloader;
        private Dictionary<string, int> _localPackageVersions;
        private List<PackageUpdateInfo> _availableUpdates;
        
        public PackageUpdateChecker(PackageDownloader packageDownloader)
        {
            _packageDownloader = packageDownloader ?? throw new ArgumentNullException(nameof(packageDownloader));
            _localPackageVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _availableUpdates = new List<PackageUpdateInfo>();
        }
        
        /// <summary>
        /// Checks for updates by comparing local packages with online database
        /// This is optimized for speed by using dictionary lookups
        /// </summary>
        /// <param name="localPackages">List of local package names with versions</param>
        /// <returns>List of packages that have updates available</returns>
        public async Task<List<PackageUpdateInfo>> CheckForUpdatesAsync(IEnumerable<PackageItem> localPackages)
        {
            _availableUpdates.Clear();
            _localPackageVersions.Clear();
            
            // Build a fast lookup dictionary of local packages with their HIGHEST versions
            // This ensures we compare against the latest version the user has, not just "Loaded" ones
            foreach (var package in localPackages)
            {
                var baseName = GetBaseName(package.Name);
                var version = ExtractVersion(package.Name);
                
                // Skip invalid versions
                if (version < 0)
                    continue;
                
                // Keep track of the HIGHEST version we have locally for each base package
                if (!_localPackageVersions.ContainsKey(baseName) || _localPackageVersions[baseName] < version)
                {
                    _localPackageVersions[baseName] = version;
                    
                    // Debug logging for specific package
                    if (baseName.Contains("Naturalis", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[UpdateChecker] Found local: {package.Name} -> base: {baseName}, version: {version}");
                    }
                }
            }
            
            // Check online database for higher versions (optimized - single pass)
            await Task.Run(() =>
            {
                // Get all online packages once (avoid repeated calls)
                var allOnlinePackages = _packageDownloader.GetAllPackageNames();
                
                // Build a fast lookup: baseName -> list of versions
                var onlineVersionsLookup = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var packageName in allOnlinePackages)
                {
                    var baseName = GetBaseName(packageName);
                    var version = ExtractVersion(packageName);
                    
                    if (version >= 0)
                    {
                        if (!onlineVersionsLookup.ContainsKey(baseName))
                            onlineVersionsLookup[baseName] = new List<int>();
                        
                        onlineVersionsLookup[baseName].Add(version);
                    }
                }
                
                // Now check each local package against online versions
                foreach (var kvp in _localPackageVersions)
                {
                    var baseName = kvp.Key;
                    var localVersion = kvp.Value;
                    
                    // Fast lookup - no repeated scanning
                    if (onlineVersionsLookup.TryGetValue(baseName, out var onlineVersions))
                    {
                        var highestOnlineVersion = onlineVersions.Max();
                        
                        if (highestOnlineVersion > localVersion)
                        {
                            // Update available!
                            _availableUpdates.Add(new PackageUpdateInfo
                            {
                                PackageName = $"{baseName}.{localVersion}",
                                OnlinePackageName = $"{baseName}.{highestOnlineVersion}",
                                LocalVersion = localVersion,
                                OnlineVersion = highestOnlineVersion,
                                BaseName = baseName
                            });
                        }
                    }
                }
            });
            
            return _availableUpdates;
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
}

