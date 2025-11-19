using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Handles downloading, decrypting, and parsing encrypted JSON database files
    /// </summary>
    public class JsonDatabaseService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _localDbPath;
        private bool _disposed;
        private byte[] _cachedEncryptedData; // In-memory cache
        
        // Base URLs for download sources
        private const string HubBaseUrl = "https://hub.virtamate.com";
        private const string PdrBaseUrl = "https://pixeldrain.com";
        
        // AES encryption key and IV (static keys that match encrypt_json_database.ps1)
        // NOTE: These are embedded in the app for convenience, not for true security
        private static readonly byte[] EncryptionKey = new byte[]
        {
            0x56, 0x41, 0x4D, 0x50, 0x61, 0x63, 0x6B, 0x61,
            0x67, 0x65, 0x4D, 0x61, 0x6E, 0x61, 0x67, 0x65,
            0x72, 0x45, 0x6E, 0x63, 0x72, 0x79, 0x70, 0x74,
            0x69, 0x6F, 0x6E, 0x4B, 0x65, 0x79, 0x32, 0x30
        };

        private static readonly byte[] EncryptionIV = new byte[]
        {
            0x32, 0x30, 0x32, 0x35, 0x56, 0x41, 0x4D, 0x50,
            0x61, 0x63, 0x6B, 0x61, 0x67, 0x65, 0x4D, 0x67
        };

        // Network permission check callback
        private Func<Task<bool>> _networkPermissionCheck;

        public JsonDatabaseService()
        {
            // Check for VPM.bin first, then fall back to VAMPackageDatabase.bin
            string vpmBinPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VPM.bin");
            string legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VAMPackageDatabase.bin");
            
            _localDbPath = File.Exists(vpmBinPath) ? vpmBinPath : legacyPath;
            
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "VPM/1.0");
        }

        /// <summary>
        /// Sets the network permission check callback
        /// </summary>
        public void SetNetworkPermissionCheck(Func<Task<bool>> permissionCheck)
        {
            _networkPermissionCheck = permissionCheck;
        }

        /// <summary>
        /// Loads and decrypts the JSON package database from GitHub or local fallback
        /// </summary>
        /// <param name="githubUrl">URL to the encrypted database file on GitHub</param>
        /// <param name="forceRefresh">Force download even if local file exists</param>
        /// <returns>List of flattened package entries with complete URLs</returns>
        public async Task<List<FlatPackageEntry>> LoadEncryptedJsonDatabaseAsync(string githubUrl, bool forceRefresh = false)
        {
            byte[] encryptedData = null;

            // Check for offline database file first (skip network permission if file exists)
            if (File.Exists(_localDbPath) && !forceRefresh)
            {
                try
                {
                    encryptedData = await File.ReadAllBytesAsync(_localDbPath);
                    _cachedEncryptedData = encryptedData;
                    goto ProcessData;
                }
                catch (Exception)
                {
                    // Failed to load offline database
                }
            }

            // No offline file or forceRefresh - need network access
            // Always check network permission before attempting download
            bool hasNetworkPermission = false;
            if (_networkPermissionCheck != null)
            {
                hasNetworkPermission = await _networkPermissionCheck();
                if (!hasNetworkPermission)
                {
                    goto LoadFromCache;
                }
            }
            else
            {
                hasNetworkPermission = true;
            }

            // Try to load from GitHub only if we have permission
            if (hasNetworkPermission && !string.IsNullOrWhiteSpace(githubUrl) && (forceRefresh || _cachedEncryptedData == null))
            {
                try
                {
                    encryptedData = await _httpClient.GetByteArrayAsync(githubUrl);
                    
                    // Save to in-memory cache
                    _cachedEncryptedData = encryptedData;
                }
                catch (Exception)
                {
                    // Download failed
                }
            }

            LoadFromCache:
            // Fallback to in-memory cache
            if (encryptedData == null && _cachedEncryptedData != null)
            {
                encryptedData = _cachedEncryptedData;
            }

            ProcessData:
            if (encryptedData == null)
            {
                return null;
            }

            // Decrypt, decompress, and parse
            try
            {
                byte[] decryptedData = DecryptAES(encryptedData);
                byte[] decompressedData = DecompressGzip(decryptedData);
                string content = Encoding.UTF8.GetString(decompressedData);
                
                // Detect format: JSON or plain text
                List<FlatPackageEntry> packageList = null;
                string trimmedContent = content.Trim();
                
                if (trimmedContent.StartsWith("{"))
                {
                    // JSON format
                    packageList = ParseJsonDatabase(content);
                }
                else
                {
                    // Plain text format (tab/space-separated)
                    packageList = ParsePlainTextDatabase(content);
                }
                
                return packageList;
            }
            catch (Exception)
            {
                // Clear corrupted cache
                _cachedEncryptedData = null;
                
                return null;
            }
        }

        /// <summary>
        /// Decrypts data using AES-256
        /// </summary>
        private byte[] DecryptAES(byte[] encryptedData)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = EncryptionKey;
                aes.IV = EncryptionIV;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                using (var msEncrypted = new MemoryStream(encryptedData))
                using (var csDecrypt = new CryptoStream(msEncrypted, decryptor, CryptoStreamMode.Read))
                using (var msDecrypted = new MemoryStream())
                {
                    csDecrypt.CopyTo(msDecrypted);
                    return msDecrypted.ToArray();
                }
            }
        }

        /// <summary>
        /// Decompresses GZIP data
        /// </summary>
        private byte[] DecompressGzip(byte[] compressedData)
        {
            using (var msCompressed = new MemoryStream(compressedData))
            using (var gzip = new GZipStream(msCompressed, CompressionMode.Decompress))
            using (var msDecompressed = new MemoryStream())
            {
                gzip.CopyTo(msDecompressed);
                return msDecompressed.ToArray();
            }
        }

        /// <summary>
        /// Parses plain text database (tab/space-separated format) and converts to package entries
        /// </summary>
        private List<FlatPackageEntry> ParsePlainTextDatabase(string content)
        {
            var packageList = new List<FlatPackageEntry>();
            ReadOnlySpan<char> remaining = content.AsSpan();

            int lineNumber = 0;
            int parsedCount = 0;
            int skippedCount = 0;

            while (!remaining.IsEmpty)
            {
                int lineBreakIndex = remaining.IndexOfAny('\r', '\n');
                ReadOnlySpan<char> lineSpan;

                if (lineBreakIndex < 0)
                {
                    lineSpan = remaining;
                    remaining = ReadOnlySpan<char>.Empty;
                }
                else
                {
                    lineSpan = remaining.Slice(0, lineBreakIndex);
                    int skip = 1;
                    if (remaining[lineBreakIndex] == '\r' && lineBreakIndex + 1 < remaining.Length && remaining[lineBreakIndex + 1] == '\n')
                    {
                        skip = 2;
                    }

                    remaining = remaining.Slice(lineBreakIndex + skip);
                }

                lineNumber++;

                lineSpan = lineSpan.Trim();
                if (lineSpan.IsEmpty)
                {
                    skippedCount++;
                    continue;
                }

                if (lineSpan[0] == '#')
                {
                    skippedCount++;
                    continue;
                }

                string packageName = null;
                string downloadUrl = null;

                try
                {
                    // Try tab-separated first
                    int tabIndex = lineSpan.IndexOf('\t');
                    if (tabIndex >= 0)
                    {
                        var nameSpan = lineSpan.Slice(0, tabIndex).TrimEnd();
                        var urlSpan = lineSpan.Slice(tabIndex + 1).Trim();
                        if (!nameSpan.IsEmpty && !urlSpan.IsEmpty)
                        {
                            packageName = nameSpan.ToString();
                            downloadUrl = urlSpan.ToString();
                        }
                    }
                    else
                    {
                        // Try double-space separated
                        int multiSpaceIndex = -1;
                        for (int i = 0; i < lineSpan.Length - 1; i++)
                        {
                            if (lineSpan[i] == ' ' && lineSpan[i + 1] == ' ')
                            {
                                multiSpaceIndex = i;
                                break;
                            }
                        }

                        if (multiSpaceIndex >= 0)
                        {
                            int urlStart = multiSpaceIndex;
                            while (urlStart < lineSpan.Length && lineSpan[urlStart] == ' ')
                            {
                                urlStart++;
                            }

                            var nameSpan = lineSpan.Slice(0, multiSpaceIndex).TrimEnd();
                            var urlSpan = lineSpan.Slice(urlStart).Trim();
                            if (!nameSpan.IsEmpty && !urlSpan.IsEmpty)
                            {
                                packageName = nameSpan.ToString();
                                downloadUrl = urlSpan.ToString();
                            }
                        }
                        else
                        {
                            // Try comma-separated
                            int commaIndex = lineSpan.IndexOf(',');
                            if (commaIndex >= 0)
                            {
                                var nameSpan = lineSpan.Slice(0, commaIndex).TrimEnd();
                                var urlSpan = lineSpan.Slice(commaIndex + 1).Trim();
                                if (!nameSpan.IsEmpty && !urlSpan.IsEmpty)
                                {
                                    packageName = nameSpan.ToString();
                                    downloadUrl = urlSpan.ToString();
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(packageName) && !string.IsNullOrWhiteSpace(downloadUrl))
                    {
                        // Remove .var extension if present
                        if (packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                        {
                            packageName = packageName.Substring(0, packageName.Length - 4);
                        }

                        // Keep Pixeldrain URLs as-is - they point to specific files within a ZIP
                        string finalUrl = downloadUrl;
                        if (downloadUrl.Contains("pixeldrain.com", StringComparison.OrdinalIgnoreCase))
                        {
                            // Pixeldrain URLs in format: /api/file/{id}/info/zip/{filename}
                            // These are direct file downloads from within a ZIP, so keep them as-is
                            finalUrl = downloadUrl;
                        }

                        // Create entry with URL as primary
                        var entry = new FlatPackageEntry
                        {
                            Creator = "",
                            PackageKey = packageName,
                            FullPackageName = packageName,
                            Filename = packageName + ".var",
                            HubUrls = new List<string>(),
                            PdrUrls = new List<string> { finalUrl },
                            AllUrls = new List<string> { finalUrl },
                            PrimaryUrl = finalUrl
                        };

                        packageList.Add(entry);
                        parsedCount++;
                    }
                }
                catch (Exception)
                {
                    // Parse error on line
                }
            }

            return packageList;
        }

        /// <summary>
        /// Parses the JSON database and flattens it into a list of package entries
        /// </summary>
        private List<FlatPackageEntry> ParseJsonDatabase(string jsonContent)
        {
            var packageList = new List<FlatPackageEntry>();

            try
            {
                // Parse the JSON using System.Text.Json
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                using (JsonDocument document = JsonDocument.Parse(jsonContent, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }))
                {
                    var root = document.RootElement;

                    // Iterate through creators
                    foreach (var creatorProperty in root.EnumerateObject())
                    {
                        string creatorName = creatorProperty.Name;
                        var creatorPackages = creatorProperty.Value;

                        // Iterate through packages for this creator
                        foreach (var packageProperty in creatorPackages.EnumerateObject())
                        {
                            string packageKey = packageProperty.Name;
                            var packageInfo = packageProperty.Value;

                            try
                            {
                                // Extract filename
                                string filename = packageInfo.GetProperty("filename").GetString();
                                
                                // Extract full package name (without .var extension)
                                string fullPackageName = filename;
                                if (fullPackageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                                {
                                    fullPackageName = fullPackageName.Substring(0, fullPackageName.Length - 4);
                                }

                                // Extract sources
                                var sources = packageInfo.GetProperty("sources");
                                
                                var hubUrls = new List<string>();
                                var pdrUrls = new List<string>();

                                // Process Hub URLs
                                if (sources.TryGetProperty("hub", out var hubArray))
                                {
                                    foreach (var hubUrl in hubArray.EnumerateArray())
                                    {
                                        string relativePath = hubUrl.GetString();
                                        if (!string.IsNullOrWhiteSpace(relativePath))
                                        {
                                            // Ensure path starts with /
                                            if (!relativePath.StartsWith("/"))
                                            {
                                                relativePath = "/" + relativePath;
                                            }
                                            hubUrls.Add(HubBaseUrl + relativePath);
                                        }
                                    }
                                }

                                // Process Pixeldrain URLs
                                if (sources.TryGetProperty("pdr", out var pdrArray))
                                {
                                    foreach (var pdrUrl in pdrArray.EnumerateArray())
                                    {
                                        string relativePath = pdrUrl.GetString();
                                        if (!string.IsNullOrWhiteSpace(relativePath))
                                        {
                                            // Ensure path starts with /
                                            if (!relativePath.StartsWith("/"))
                                            {
                                                relativePath = "/" + relativePath;
                                            }
                                            
                                            // URL-encode the path to handle spaces and special characters
                                            // Split by '/' to encode each segment separately
                                            var pathSegments = relativePath.Split('/');
                                            var encodedSegments = pathSegments.Select(segment => 
                                                string.IsNullOrEmpty(segment) ? segment : Uri.EscapeDataString(segment));
                                            string encodedPath = string.Join("/", encodedSegments);
                                            
                                            // Build full URL - the database already contains complete paths
                                            string fullUrl = PdrBaseUrl + encodedPath;
                                            
                                            // Validate that the URL ends with .var (otherwise it's a folder, not a package)
                                            if (fullUrl.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                                            {
                                                pdrUrls.Add(fullUrl);
                                            }
                                            // Silently skip invalid URLs (folders, not packages)
                                        }
                                    }
                                }

                                // Combine all URLs
                                var allUrls = new List<string>();
                                allUrls.AddRange(hubUrls);
                                allUrls.AddRange(pdrUrls);

                                // Create flattened entry
                                var entry = new FlatPackageEntry
                                {
                                    Creator = creatorName,
                                    PackageKey = packageKey,
                                    FullPackageName = fullPackageName,
                                    Filename = filename,
                                    HubUrls = hubUrls,
                                    PdrUrls = pdrUrls,
                                    AllUrls = allUrls,
                                    PrimaryUrl = allUrls.FirstOrDefault()
                                };

                                packageList.Add(entry);
                            }
                            catch (Exception ex)
                            {
                                // Error parsing package
                            }
                        }
                    }
                }

                // Successfully parsed packages
            }
            catch (Exception)
            {
                throw;
            }

            return packageList;
        }

        /// <summary>
        /// Converts the flattened package list to a dictionary for quick lookup
        /// Key: Full package name (without .var)
        /// Value: Primary download URL
        /// </summary>
        public Dictionary<string, string> ConvertToUrlDictionary(List<FlatPackageEntry> packages)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (packages == null)
                return dict;

            foreach (var package in packages)
            {
                if (!string.IsNullOrWhiteSpace(package.FullPackageName) && 
                    !string.IsNullOrWhiteSpace(package.PrimaryUrl))
                {
                    // Use the full package name as key (without .var extension)
                    if (!dict.ContainsKey(package.FullPackageName))
                    {
                        dict[package.FullPackageName] = package.PrimaryUrl;
                    }
                }
            }

            return dict;
        }

        /// <summary>
        /// Gets the local database file path
        /// </summary>
        public string GetLocalDatabasePath()
        {
            return _localDbPath;
        }

        /// <summary>
        /// Checks if local database exists in memory cache
        /// </summary>
        public bool HasCachedDatabase()
        {
            return _cachedEncryptedData != null;
        }

        /// <summary>
        /// Clears the in-memory cache
        /// </summary>
        public void ClearCache()
        {
            _cachedEncryptedData = null;
            // In-memory cache cleared
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _cachedEncryptedData = null;
                _disposed = true;
            }
        }
    }
}

