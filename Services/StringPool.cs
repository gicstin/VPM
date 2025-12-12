using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace VPM.Services
{
    /// <summary>
    /// Thread-safe string interning pool to reduce memory usage from duplicate strings.
    /// Uses a concurrent dictionary for O(1) lookups and automatic deduplication.
    /// 
    /// Memory profiler showed 219MB wasted on duplicate strings, primarily:
    /// - File paths like "Custom/Clothing/Female/..."
    /// - Creator names, package names, categories
    /// - Content type strings
    /// </summary>
    public static class StringPool
    {
        // Main pool for general strings (paths, names, etc.)
        private static readonly ConcurrentDictionary<string, string> _pool = 
            new(StringComparer.Ordinal);
        
        // Separate pool for case-insensitive strings (categories, statuses, etc.)
        private static readonly ConcurrentDictionary<string, string> _poolIgnoreCase = 
            new(StringComparer.OrdinalIgnoreCase);
        
        // Pre-interned common strings to avoid dictionary lookups
        private static readonly string[] CommonStrings = new[]
        {
            // Statuses
            "Loaded", "Available", "Archived", "Missing", "Unknown",
            // Categories
            "Clothing", "Hair", "Morphs", "Scenes", "Looks", "Poses", "Assets", 
            "Scripts", "Plugins", "SubScenes", "Skins", "Morph Pack", "Unknown",
            // Common path prefixes
            "Custom/", "Custom/Atom/", "Custom/Clothing/", "Custom/Hair/",
            "Custom/Assets/", "Custom/Scripts/", "Custom/SubScene/",
            "Custom/Clothing/Female/", "Custom/Clothing/Male/",
            "Custom/Hair/Female/", "Custom/Hair/Male/",
            "Saves/", "Saves/scene/", "Saves/Person/", "Saves/Person/appearance/",
            // File extensions
            ".vam", ".vap", ".vaj", ".json", ".jpg", ".png", ".cs", ".dll",
            // License types
            "FC", "CC BY", "CC BY-SA", "CC BY-NC", "CC BY-NC-SA", "PC", "Paid",
            // Common creators (will be populated dynamically)
            // Empty strings
            "", " "
        };
        
        // Pre-intern common strings on static initialization
        static StringPool()
        {
            foreach (var s in CommonStrings)
            {
                _pool[s] = s;
                _poolIgnoreCase[s] = s;
            }
        }
        
        /// <summary>
        /// Interns a string, returning a shared instance if one exists.
        /// Returns null if input is null, empty string if input is empty.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string Intern(string value)
        {
            if (value == null) return null;
            if (value.Length == 0) return string.Empty;
            
            return _pool.GetOrAdd(value, value);
        }
        
        /// <summary>
        /// Interns a string using case-insensitive comparison.
        /// Useful for categories, statuses, and other normalized strings.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string InternIgnoreCase(string value)
        {
            if (value == null) return null;
            if (value.Length == 0) return string.Empty;
            
            return _poolIgnoreCase.GetOrAdd(value, value);
        }
        
        /// <summary>
        /// Interns a path string, normalizing separators first.
        /// Paths are the biggest source of duplicate strings (219MB wasted).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string InternPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            
            // Normalize path separators to forward slash (common in VAR archives)
            // Only create new string if needed
            if (path.IndexOf('\\') >= 0)
            {
                path = path.Replace('\\', '/');
            }
            
            return _pool.GetOrAdd(path, path);
        }
        
        /// <summary>
        /// Gets statistics about the string pool for diagnostics.
        /// </summary>
        public static (int poolCount, int poolIgnoreCaseCount, long estimatedBytes) GetStatistics()
        {
            long estimatedBytes = 0;
            
            foreach (var kvp in _pool)
            {
                // String object overhead (~26 bytes) + 2 bytes per char
                estimatedBytes += 26 + (kvp.Key.Length * 2);
            }
            
            return (_pool.Count, _poolIgnoreCase.Count, estimatedBytes);
        }
        
        /// <summary>
        /// Clears the string pools. Use with caution - only call during shutdown
        /// or when you're certain no references to pooled strings exist.
        /// </summary>
        public static void Clear()
        {
            _pool.Clear();
            _poolIgnoreCase.Clear();
            
            // Re-add common strings
            foreach (var s in CommonStrings)
            {
                _pool[s] = s;
                _poolIgnoreCase[s] = s;
            }
        }
        
        /// <summary>
        /// Trims the pools by removing strings that are likely no longer needed.
        /// This is a heuristic-based cleanup - it removes very long strings
        /// that are unlikely to be reused.
        /// </summary>
        public static int TrimExcess(int maxStringLength = 500)
        {
            int removed = 0;
            
            foreach (var kvp in _pool)
            {
                if (kvp.Key.Length > maxStringLength)
                {
                    if (_pool.TryRemove(kvp.Key, out _))
                        removed++;
                }
            }
            
            return removed;
        }
    }
}
