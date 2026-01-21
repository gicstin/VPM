using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VPM.Models
{
    /// <summary>
    /// Represents the type of version reference in a dependency
    /// </summary>
    public enum DependencyVersionType
    {
        /// <summary>Exact version (e.g., Creator.Package.5)</summary>
        Exact,
        /// <summary>Latest version (e.g., Creator.Package.latest)</summary>
        Latest,
        /// <summary>Minimum version (e.g., Creator.Package.min32)</summary>
        Minimum
    }

    /// <summary>
    /// Parsed information about a dependency reference
    /// </summary>
    public class DependencyVersionInfo
    {
        /// <summary>The original dependency string</summary>
        public string OriginalDependency { get; set; } = string.Empty;
        
        /// <summary>The base package name (Creator.Package)</summary>
        public string BaseName { get; set; } = string.Empty;
        
        /// <summary>The type of version reference</summary>
        public DependencyVersionType VersionType { get; set; }
        
        /// <summary>The version number (for Exact or Minimum types)</summary>
        public int? VersionNumber { get; set; }

        // Regex patterns for parsing dependency strings
        private static readonly Regex MinVersionPattern = new(@"\.min(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ExactVersionPattern = new(@"\.([A-Za-z0-9_]+)$", RegexOptions.Compiled);

        /// <summary>
        /// Parses a dependency string into its components
        /// </summary>
        /// <param name="dependency">The dependency string (e.g., "Creator.Package.min32", "Creator.Package.latest", "Creator.Package.5")</param>
        /// <returns>Parsed dependency information</returns>
        public static DependencyVersionInfo Parse(string dependency)
        {
            if (string.IsNullOrEmpty(dependency))
            {
                return new DependencyVersionInfo
                {
                    OriginalDependency = dependency ?? string.Empty,
                    VersionType = DependencyVersionType.Exact
                };
            }

            var info = new DependencyVersionInfo
            {
                OriginalDependency = dependency
            };

            // Remove .var extension if present
            var name = dependency;
            if (name.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            // Check for .latest suffix
            if (name.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
            {
                info.BaseName = name[..^7]; // Remove ".latest"
                info.VersionType = DependencyVersionType.Latest;
                return info;
            }

            // Check for .min[NUMBER] suffix (e.g., .min32)
            var minMatch = MinVersionPattern.Match(name);
            if (minMatch.Success)
            {
                info.BaseName = name[..minMatch.Index];
                info.VersionType = DependencyVersionType.Minimum;
                info.VersionNumber = int.Parse(minMatch.Groups[1].Value);
                return info;
            }

            // Check for exact version number suffix
            var exactMatch = ExactVersionPattern.Match(name);
            if (exactMatch.Success)
            {
                info.BaseName = name[..exactMatch.Index];
                info.VersionType = DependencyVersionType.Exact;
                
                var versionText = exactMatch.Groups[1].Value;
                
                // Try to parse leading integer
                var firstSegment = versionText.Split('_')[0];
                var i = 0;
                while (i < firstSegment.Length && char.IsDigit(firstSegment[i]))
                    i++;

                if (i > 0 && int.TryParse(firstSegment.Substring(0, i), out var version))
                {
                    info.VersionNumber = version;
                }
                else
                {
                    // For purely alphanumeric versions like "latest" or "v1", 
                    // we treat it as an exact match for that string, but since VersionNumber is int,
                    // we default to 1 to match VarMetadata behavior.
                    info.VersionNumber = 1;
                }
                return info;
            }

            // No version suffix found - treat as base name
            info.BaseName = name;
            info.VersionType = DependencyVersionType.Latest; // Default to latest if no version specified
            return info;
        }

        /// <summary>
        /// Extracts just the base name from a dependency string, handling all version formats
        /// </summary>
        public static string GetBaseName(string dependency)
        {
            return Parse(dependency).BaseName;
        }

        /// <summary>
        /// Checks if a given version satisfies this dependency requirement
        /// </summary>
        /// <param name="availableVersion">The version number to check</param>
        /// <returns>True if the version satisfies the requirement</returns>
        public bool IsSatisfiedBy(int availableVersion)
        {
            return VersionType switch
            {
                DependencyVersionType.Latest => true, // Any version satisfies .latest
                DependencyVersionType.Minimum => availableVersion >= (VersionNumber ?? 0),
                DependencyVersionType.Exact => availableVersion == (VersionNumber ?? 0),
                _ => true
            };
        }

        /// <summary>
        /// Finds the best matching version from a list of available versions
        /// </summary>
        /// <param name="availableVersions">List of available version numbers (should be sorted ascending)</param>
        /// <param name="preferLatest">If true, prefer the latest version when multiple match</param>
        /// <returns>The best matching version, or null if none found</returns>
        public int? FindBestMatch(IEnumerable<int> availableVersions, bool preferLatest = true)
        {
            var versions = availableVersions.OrderBy(v => v).ToList();
            if (versions.Count == 0)
                return null;

            switch (VersionType)
            {
                case DependencyVersionType.Latest:
                    return versions.Last();

                case DependencyVersionType.Minimum:
                    // Find the smallest version >= minimum, or latest if none found
                    var minVersion = VersionNumber ?? 0;
                    var matching = versions.Where(v => v >= minVersion).ToList();
                    if (matching.Count > 0)
                    {
                        return preferLatest ? matching.Last() : matching.First();
                    }
                    // Fallback to latest available if no version meets minimum
                    return versions.Last();

                case DependencyVersionType.Exact:
                    // Return exact version if available, otherwise latest
                    var exactVersion = VersionNumber ?? 0;
                    if (versions.Contains(exactVersion))
                    {
                        return exactVersion;
                    }
                    // Fallback to latest available
                    return versions.Last();

                default:
                    return versions.Last();
            }
        }
    }
}
