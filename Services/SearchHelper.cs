using System;

namespace VPM.Services
{
    /// <summary>
    /// High-performance search helper for consistent, fast case-insensitive "starts with" matching across all searchboxes.
    /// Optimized for stability and performance with zero allocations.
    /// </summary>
    public static class SearchHelper
    {
        /// <summary>
        /// Performs a high-performance case-insensitive "starts with" match.
        /// Returns true if the text starts with the search term (case-insensitive).
        /// Returns true if search term is empty.
        /// Uses StringComparison.OrdinalIgnoreCase for best performance without allocations.
        /// </summary>
        /// <param name="text">The text to search in</param>
        /// <param name="searchTerm">The search term to look for</param>
        /// <returns>True if text starts with searchTerm (case-insensitive) or searchTerm is empty</returns>
        public static bool StartsWithSearch(string text, string searchTerm)
        {
            // Empty search matches everything
            if (string.IsNullOrEmpty(searchTerm))
                return true;

            // Null or empty text doesn't match non-empty search
            if (string.IsNullOrEmpty(text))
                return false;

            // Use StartsWith with OrdinalIgnoreCase for zero-allocation case-insensitive comparison
            return text.StartsWith(searchTerm, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates and prepares search text for use.
        /// Returns empty string for null/whitespace input.
        /// </summary>
        /// <param name="searchText">Raw search text from UI</param>
        /// <returns>Cleaned search text or empty string</returns>
        public static string PrepareSearchText(string searchText)
        {
            return string.IsNullOrWhiteSpace(searchText) ? "" : searchText.Trim();
        }

        /// <summary>
        /// Performs a simple case-insensitive "starts with" check on package name only.
        /// Designed for maximum performance on large package lists.
        /// </summary>
        /// <param name="packageName">Package name to check</param>
        /// <param name="searchTerm">Search term (should be pre-cleaned with PrepareSearchText)</param>
        /// <returns>True if package name starts with search term</returns>
        public static bool MatchesPackageSearch(string packageName, string searchTerm)
        {
            return StartsWithSearch(packageName, searchTerm);
        }
    }
}

