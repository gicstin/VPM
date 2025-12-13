using System;
using System.Collections.Generic;
using System.Linq;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Builds and maintains a dependency graph for all packages.
    /// Enables efficient reverse dependency lookups, orphan detection, and dependency chain analysis.
    /// </summary>
    public class DependencyGraph
    {
        // Forward dependencies: Package -> List of packages it depends on
        private readonly Dictionary<string, HashSet<string>> _dependencies = new(StringComparer.OrdinalIgnoreCase);
        
        // Reverse dependencies: Package -> List of packages that depend on it
        private readonly Dictionary<string, HashSet<string>> _dependents = new(StringComparer.OrdinalIgnoreCase);
        
        // Package base name lookup for .latest resolution
        private readonly Dictionary<string, HashSet<string>> _packagesByBaseName = new(StringComparer.OrdinalIgnoreCase);
        
        // All known packages
        private readonly HashSet<string> _allPackages = new(StringComparer.OrdinalIgnoreCase);
        
        // Statistics
        public int TotalPackages => _allPackages.Count;
        public int TotalDependencyLinks { get; private set; }
        
        /// <summary>
        /// Builds the dependency graph from package metadata
        /// Includes external packages in the graph so they're properly indexed as dependents
        /// </summary>
        public void Build(IEnumerable<KeyValuePair<string, VarMetadata>> packageMetadata)
        {
            Clear();
            
            // First pass: collect all packages and build base name index
            // This includes external packages so they can be resolved as dependents
            foreach (var kvp in packageMetadata)
            {
                var metadata = kvp.Value;
                if (metadata.IsCorrupted)
                    continue;
                
                var packageName = $"{metadata.CreatorName}.{metadata.PackageName}.{metadata.Version}";
                _allPackages.Add(packageName);
                
                // Index by base name for .latest resolution
                var baseName = $"{metadata.CreatorName}.{metadata.PackageName}";
                if (!_packagesByBaseName.TryGetValue(baseName, out var versions))
                {
                    versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _packagesByBaseName[baseName] = versions;
                }
                versions.Add(packageName);
                
                // Initialize empty dependency sets for all packages, including external ones
                if (!_dependencies.ContainsKey(packageName))
                    _dependencies[packageName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            
            // Second pass: build dependency relationships
            foreach (var kvp in packageMetadata)
            {
                var metadata = kvp.Value;
                if (metadata.IsCorrupted || metadata.Dependencies == null)
                    continue;
                
                var packageName = $"{metadata.CreatorName}.{metadata.PackageName}.{metadata.Version}";
                
                foreach (var dep in metadata.Dependencies)
                {
                    if (string.IsNullOrEmpty(dep))
                        continue;
                    
                    // Resolve the dependency to actual package name(s)
                    var resolvedDeps = ResolveDependency(dep);
                    
                    foreach (var resolvedDep in resolvedDeps)
                    {
                        // Add forward dependency
                        _dependencies[packageName].Add(resolvedDep);
                        
                        // Add reverse dependency
                        if (!_dependents.TryGetValue(resolvedDep, out var dependentSet))
                        {
                            dependentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _dependents[resolvedDep] = dependentSet;
                        }
                        dependentSet.Add(packageName);
                        
                        TotalDependencyLinks++;
                    }
                }
            }
        }
        
        /// <summary>
        /// Resolves a dependency string to actual package name(s)
        /// Handles .latest, .min[NUMBER], and exact version references
        /// </summary>
        private IEnumerable<string> ResolveDependency(string dep)
        {
            var depInfo = DependencyVersionInfo.Parse(dep);
            
            // Try to find packages matching the base name
            if (_packagesByBaseName.TryGetValue(depInfo.BaseName, out var versions))
            {
                switch (depInfo.VersionType)
                {
                    case DependencyVersionType.Latest:
                        // Return all versions for .latest (any version satisfies)
                        return versions;
                    
                    case DependencyVersionType.Minimum:
                        // Return versions that meet the minimum requirement
                        var minVersion = depInfo.VersionNumber ?? 0;
                        var matchingVersions = versions.Where(v =>
                        {
                            var versionNum = ExtractVersionNumber(v);
                            return versionNum >= minVersion;
                        }).ToList();
                        
                        // If no versions meet minimum, return all versions (fallback)
                        return matchingVersions.Count > 0 ? matchingVersions : versions;
                    
                    case DependencyVersionType.Exact:
                        // Check if exact version exists
                        if (_allPackages.Contains(dep))
                        {
                            return new[] { dep };
                        }
                        // Fallback to all versions if exact not found
                        return versions;
                }
            }
            
            // Check if exact package exists (for exact version references)
            if (_allPackages.Contains(dep))
            {
                return new[] { dep };
            }
            
            // Return as-is (missing dependency)
            return new[] { dep };
        }
        
        /// <summary>
        /// Extracts the version number from a full package name (Creator.Package.Version)
        /// </summary>
        private static int ExtractVersionNumber(string packageName)
        {
            var lastDot = packageName.LastIndexOf('.');
            if (lastDot > 0)
            {
                var versionStr = packageName.Substring(lastDot + 1);
                if (int.TryParse(versionStr, out var version))
                {
                    return version;
                }
            }
            return 0;
        }
        
        /// <summary>
        /// Gets packages that depend on the specified package (reverse dependencies)
        /// </summary>
        public List<string> GetDependents(string packageName)
        {
            // Try exact match first
            if (_dependents.TryGetValue(packageName, out var dependents))
            {
                return dependents.ToList();
            }
            
            // Try matching by base name
            var baseName = GetBaseName(packageName);
            if (_packagesByBaseName.TryGetValue(baseName, out var versions))
            {
                var allDependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var version in versions)
                {
                    if (_dependents.TryGetValue(version, out var versionDependents))
                    {
                        foreach (var dep in versionDependents)
                            allDependents.Add(dep);
                    }
                }
                return allDependents.ToList();
            }
            
            return new List<string>();
        }
        
        /// <summary>
        /// Gets the count of packages that depend on the specified package
        /// </summary>
        public int GetDependentsCount(string packageName)
        {
            return GetDependents(packageName).Count;
        }
        
        /// <summary>
        /// Gets packages that the specified package depends on
        /// </summary>
        public List<string> GetDependencies(string packageName)
        {
            if (_dependencies.TryGetValue(packageName, out var deps))
            {
                return deps.ToList();
            }
            return new List<string>();
        }
        
        /// <summary>
        /// Gets orphan packages (packages that no other package depends on)
        /// </summary>
        public List<string> GetOrphanPackages()
        {
            var orphans = new List<string>();
            
            foreach (var package in _allPackages)
            {
                if (!_dependents.TryGetValue(package, out var dependents) || dependents.Count == 0)
                {
                    orphans.Add(package);
                }
            }
            
            return orphans;
        }
        
        /// <summary>
        /// Gets critical packages (packages that many others depend on)
        /// </summary>
        /// <param name="minDependents">Minimum number of dependents to be considered critical</param>
        public List<(string Package, int DependentCount)> GetCriticalPackages(int minDependents = 5)
        {
            var critical = new List<(string Package, int DependentCount)>();
            
            foreach (var kvp in _dependents)
            {
                if (kvp.Value.Count >= minDependents)
                {
                    critical.Add((kvp.Key, kvp.Value.Count));
                }
            }
            
            return critical.OrderByDescending(x => x.DependentCount).ToList();
        }
        
        /// <summary>
        /// Gets the full dependency chain for a package (all transitive dependencies)
        /// </summary>
        public HashSet<string> GetFullDependencyChain(string packageName, int maxDepth = 10)
        {
            var chain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            CollectDependenciesRecursive(packageName, chain, visited, 0, maxDepth);
            
            return chain;
        }
        
        private void CollectDependenciesRecursive(string packageName, HashSet<string> chain, HashSet<string> visited, int depth, int maxDepth)
        {
            if (depth >= maxDepth || visited.Contains(packageName))
                return;
            
            visited.Add(packageName);
            
            if (_dependencies.TryGetValue(packageName, out var deps))
            {
                foreach (var dep in deps)
                {
                    chain.Add(dep);
                    CollectDependenciesRecursive(dep, chain, visited, depth + 1, maxDepth);
                }
            }
        }
        
        /// <summary>
        /// Gets the full reverse dependency chain (all packages that transitively depend on this one)
        /// </summary>
        public HashSet<string> GetFullDependentChain(string packageName, int maxDepth = 10)
        {
            var chain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            CollectDependentsRecursive(packageName, chain, visited, 0, maxDepth);
            
            return chain;
        }
        
        private void CollectDependentsRecursive(string packageName, HashSet<string> chain, HashSet<string> visited, int depth, int maxDepth)
        {
            if (depth >= maxDepth || visited.Contains(packageName))
                return;
            
            visited.Add(packageName);
            
            // Check all versions of this package
            var baseName = GetBaseName(packageName);
            var packagesToCheck = new List<string> { packageName };
            
            if (_packagesByBaseName.TryGetValue(baseName, out var versions))
            {
                packagesToCheck.AddRange(versions);
            }
            
            foreach (var pkg in packagesToCheck)
            {
                if (_dependents.TryGetValue(pkg, out var deps))
                {
                    foreach (var dep in deps)
                    {
                        chain.Add(dep);
                        CollectDependentsRecursive(dep, chain, visited, depth + 1, maxDepth);
                    }
                }
            }
        }
        
        /// <summary>
        /// Checks if removing a package would break any other packages
        /// </summary>
        public List<string> GetPackagesThatWouldBreak(string packageName)
        {
            return GetDependents(packageName);
        }
        
        /// <summary>
        /// Gets dependency statistics for a package
        /// </summary>
        public PackageDependencyStats GetPackageStats(string packageName)
        {
            var stats = new PackageDependencyStats
            {
                PackageName = packageName,
                DirectDependencies = GetDependencies(packageName).Count,
                DirectDependents = GetDependentsCount(packageName),
                TransitiveDependencies = GetFullDependencyChain(packageName).Count,
                TransitiveDependents = GetFullDependentChain(packageName).Count
            };
            
            return stats;
        }
        
        /// <summary>
        /// Gets overall graph statistics
        /// </summary>
        public GraphStatistics GetGraphStatistics()
        {
            var stats = new GraphStatistics
            {
                TotalPackages = _allPackages.Count,
                TotalDependencyLinks = TotalDependencyLinks,
                OrphanPackages = GetOrphanPackages().Count,
                PackagesWithDependencies = _dependencies.Count(kvp => kvp.Value.Count > 0),
                PackagesWithDependents = _dependents.Count,
                MaxDependents = _dependents.Count > 0 ? _dependents.Max(kvp => kvp.Value.Count) : 0,
                MaxDependencies = _dependencies.Count > 0 ? _dependencies.Max(kvp => kvp.Value.Count) : 0
            };
            
            // Find most depended-on package
            if (_dependents.Count > 0)
            {
                var mostDepended = _dependents.OrderByDescending(kvp => kvp.Value.Count).First();
                stats.MostDependedPackage = mostDepended.Key;
                stats.MostDependedCount = mostDepended.Value.Count;
            }
            
            return stats;
        }
        
        /// <summary>
        /// Clears the graph
        /// </summary>
        public void Clear()
        {
            _dependencies.Clear();
            _dependents.Clear();
            _packagesByBaseName.Clear();
            _allPackages.Clear();
            TotalDependencyLinks = 0;
        }
        
        /// <summary>
        /// Checks if a package exists in the graph
        /// </summary>
        public bool PackageExists(string packageName)
        {
            if (_allPackages.Contains(packageName))
                return true;
            
            // Check by base name
            var baseName = GetBaseName(packageName);
            return _packagesByBaseName.ContainsKey(baseName);
        }
        
        private static string GetBaseName(string packageName)
        {
            // Use the centralized DependencyVersionInfo parser
            return DependencyVersionInfo.GetBaseName(packageName);
        }
    }
    
    /// <summary>
    /// Statistics for a single package's dependencies
    /// </summary>
    public class PackageDependencyStats
    {
        public string PackageName { get; set; } = "";
        public int DirectDependencies { get; set; }
        public int DirectDependents { get; set; }
        public int TransitiveDependencies { get; set; }
        public int TransitiveDependents { get; set; }
    }
    
    /// <summary>
    /// Overall graph statistics
    /// </summary>
    public class GraphStatistics
    {
        public int TotalPackages { get; set; }
        public int TotalDependencyLinks { get; set; }
        public int OrphanPackages { get; set; }
        public int PackagesWithDependencies { get; set; }
        public int PackagesWithDependents { get; set; }
        public int MaxDependents { get; set; }
        public int MaxDependencies { get; set; }
        public string MostDependedPackage { get; set; } = "";
        public int MostDependedCount { get; set; }
    }
}
