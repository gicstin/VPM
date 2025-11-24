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
        /// Splits search text into individual terms for multi-term filtering.
        /// </summary>
        /// <param name="searchText">Cleaned search text</param>
        /// <returns>Array of search terms</returns>
        public static string[] PrepareSearchTerms(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return Array.Empty<string>();

            return searchText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Performs a simple case-insensitive partial match check on package name only.
        /// Designed for maximum performance on large package lists.
        /// </summary>
        /// <param name="packageName">Package name to check</param>
        /// <param name="searchTerm">Search term (should be pre-cleaned with PrepareSearchText)</param>
        /// <returns>True if package name contains search term</returns>
        public static bool MatchesPackageSearch(string packageName, string searchTerm)
        {
            return ContainsSearch(packageName, searchTerm);
        }

        /// <summary>
        /// Performs a multi-term AND match on package name.
        /// All terms must be present in the package name.
        /// </summary>
        /// <param name="packageName">Package name to check</param>
        /// <param name="searchTerms">Array of search terms</param>
        /// <returns>True if package name contains ALL search terms</returns>
        public static bool MatchesPackageSearch(string packageName, string[] searchTerms)
        {
            return MatchesAllTerms(packageName, searchTerms);
        }

        /// <summary>
        /// Performs a multi-term AND match on any text.
        /// All terms must be present in the text.
        /// </summary>
        /// <param name="text">Text to check</param>
        /// <param name="terms">Array of search terms</param>
        /// <returns>True if text contains ALL search terms</returns>
        public static bool MatchesAllTerms(string text, string[] terms)
        {
            if (terms == null || terms.Length == 0)
                return true;

            if (string.IsNullOrEmpty(text))
                return false;

            // Check if ALL terms are present (AND logic)
            for (int i = 0; i < terms.Length; i++)
            {
                if (text.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Performs a case-insensitive partial/contains match.
        /// Returns true if the text contains the search term (case-insensitive).
        /// Returns true if search term is empty.
        /// Uses IndexOf with OrdinalIgnoreCase for best performance without allocations.
        /// </summary>
        /// <param name="text">The text to search in</param>
        /// <param name="searchTerm">The search term to look for</param>
        /// <returns>True if text contains searchTerm (case-insensitive) or searchTerm is empty</returns>
        public static bool ContainsSearch(string text, string searchTerm)
        {
            // Empty search matches everything
            if (string.IsNullOrEmpty(searchTerm))
                return true;

            // Null or empty text doesn't match non-empty search
            if (string.IsNullOrEmpty(text))
                return false;

            // Use IndexOf with OrdinalIgnoreCase for zero-allocation case-insensitive comparison
            return text.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

