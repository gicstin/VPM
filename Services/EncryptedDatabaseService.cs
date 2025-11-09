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
    /// Handles downloading, decrypting, and parsing encrypted database files
    /// </summary>
    public class EncryptedDatabaseService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _localDbPath;
        private bool _disposed;
        private byte[] _cachedEncryptedData; // In-memory cache instead of disk
        
        // AES encryption key and IV (static keys that match encrypt_database.ps1)
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

        public EncryptedDatabaseService()
        {
            // Check for offline database file in exe directory
            _localDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VAMPackageDatabase.bin");
            
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
        /// Loads and decrypts the package database from GitHub or local fallback
        /// </summary>
        /// <param name="githubUrl">URL to the encrypted database file on GitHub</param>
        /// <param name="forceRefresh">Force download even if local file exists</param>
        /// <returns>Dictionary of package names to download URLs</returns>
        public async Task<Dictionary<string, string>> LoadEncryptedDatabaseAsync(string githubUrl, bool forceRefresh = false)
        {
            
            byte[] encryptedData = null;

            // Check for offline database file first (skip network permission if file exists)
            if (File.Exists(_localDbPath) && !forceRefresh)
            {
                try
                {
                    encryptedData = await File.ReadAllBytesAsync(_localDbPath);
                    Console.WriteLine($"[EncryptedDB] ✓ Loaded offline database from: {_localDbPath}");
                    Console.WriteLine($"[EncryptedDB] ✓ File size: {encryptedData.Length:N0} bytes (offline mode - no network required)");
                    _cachedEncryptedData = encryptedData;
                    goto ProcessData;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EncryptedDB] – Failed to load offline database: {ex.Message}");
                }
            }

            // No offline file or forceRefresh - need network access
            // Check network permission ONLY if we need to download
            if (_networkPermissionCheck != null)
            {
                bool hasPermission = await _networkPermissionCheck();
                if (!hasPermission)
                {
                    goto LoadFromCache;
                }
            }

            // Try to load from GitHub
            if (!string.IsNullOrWhiteSpace(githubUrl) && (forceRefresh || _cachedEncryptedData == null))
            {

                try
                {
                    Console.WriteLine($"[EncryptedDB] Downloading from: {githubUrl}");
                    encryptedData = await _httpClient.GetByteArrayAsync(githubUrl);
                    Console.WriteLine($"[EncryptedDB] Downloaded {encryptedData.Length:N0} bytes");
                    
                    // Save to in-memory cache
                    _cachedEncryptedData = encryptedData;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EncryptedDB] Download failed: {ex.Message}");
                }
            }

            LoadFromCache:
            // Fallback to in-memory cache
            if (encryptedData == null && _cachedEncryptedData != null)
            {
                encryptedData = _cachedEncryptedData;
                Console.WriteLine($"[EncryptedDB] Using in-memory cache");
            }

            ProcessData:
            if (encryptedData == null)
            {
                Console.WriteLine($"[EncryptedDB] No data available (offline file not found and download failed)");
                return null;
            }

            // Decrypt and decompress
            try
            {
                byte[] decryptedData = DecryptAES(encryptedData);
                byte[] decompressedData = DecompressGzip(decryptedData);
                string content = Encoding.UTF8.GetString(decompressedData);
                
                var packageDict = ParsePackageData(content);
                
                return packageDict;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EncryptedDB] Error during decrypt/parse: {ex.Message}");
                
                // If local file is corrupted, delete it
                if (File.Exists(_localDbPath))
                {
                    try
                    {
                        File.Delete(_localDbPath);
                    }
                    catch { }
                }
                
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
        /// Parses the decrypted package data (tab-separated or CSV format)
        /// </summary>
        private Dictionary<string, string> ParsePackageData(string content)
        {
            var packageDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReadOnlySpan<char> remaining = content.AsSpan();

            int lineNumber = 0;
            int parsedCount = 0;
            int skippedCount = 0;
            int failedCount = 0;

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
                        if (packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                        {
                            packageName = packageName.Substring(0, packageName.Length - 4);
                        }

                        if (!packageDict.ContainsKey(packageName))
                        {
                            packageDict[packageName] = downloadUrl;
                            parsedCount++;
                        }
                    }
                    else
                    {
                        failedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EncryptedDB] Parse error on line {lineNumber}: {ex.Message}");
                    failedCount++;
                }
            }

            return packageDict;
        }

        /// <summary>
        /// Gets the local database file path
        /// </summary>
        public string GetLocalDatabasePath()
        {
            return _localDbPath;
        }

        /// <summary>
        /// Checks if local database exists
        /// </summary>
        public bool HasLocalDatabase()
        {
            return File.Exists(_localDbPath);
        }

        /// <summary>
        /// Deletes the local database cache
        /// </summary>
        public void ClearLocalCache()
        {
            try
            {
                if (File.Exists(_localDbPath))
                {
                    File.Delete(_localDbPath);
                    Console.WriteLine("[EncryptedDB] Local cache cleared");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EncryptedDB] Failed to clear cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads and decrypts a JSON-formatted package database
        /// </summary>
        /// <param name="githubUrl">URL to the encrypted JSON database file</param>
        /// <param name="forceRefresh">Force download even if cache exists</param>
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
                    Console.WriteLine($"[EncryptedDB-JSON] ✓ Loaded offline database from: {_localDbPath}");
                    Console.WriteLine($"[EncryptedDB-JSON] ✓ File size: {encryptedData.Length:N0} bytes (offline mode - no network required)");
                    _cachedEncryptedData = encryptedData;
                    goto ProcessData;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EncryptedDB-JSON] – Failed to load offline database: {ex.Message}");
                }
            }

            // No offline file or forceRefresh - need network access
            // Check network permission ONLY if we need to download
            if (_networkPermissionCheck != null)
            {
                bool hasPermission = await _networkPermissionCheck();
                if (!hasPermission)
                {
                    goto LoadFromCache;
                }
            }

            // Try to load from GitHub
            if (!string.IsNullOrWhiteSpace(githubUrl) && (forceRefresh || _cachedEncryptedData == null))
            {

                try
                {
                    Console.WriteLine($"[EncryptedDB-JSON] Downloading from: {githubUrl}");
                    encryptedData = await _httpClient.GetByteArrayAsync(githubUrl);
                    
                    // Save to in-memory cache
                    _cachedEncryptedData = encryptedData;
                    Console.WriteLine($"[EncryptedDB-JSON] Downloaded {encryptedData.Length:N0} bytes");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EncryptedDB-JSON] Download failed: {ex.Message}");
                }
            }

            LoadFromCache:
            // Fallback to in-memory cache
            if (encryptedData == null && _cachedEncryptedData != null)
            {
                encryptedData = _cachedEncryptedData;
                Console.WriteLine($"[EncryptedDB-JSON] Using in-memory cache");
            }

            ProcessData:
            if (encryptedData == null)
            {
                Console.WriteLine($"[EncryptedDB-JSON] No data available (offline file not found and download failed)");
                return null;
            }

            // Decrypt, decompress, and parse
            try
            {
                byte[] decryptedData = DecryptAES(encryptedData);
                Console.WriteLine($"[EncryptedDB-JSON] Decrypted {decryptedData.Length:N0} bytes");
                
                byte[] decompressedData = DecompressGzip(decryptedData);
                Console.WriteLine($"[EncryptedDB-JSON] Decompressed {decompressedData.Length:N0} bytes");
                
                string jsonContent = Encoding.UTF8.GetString(decompressedData);
                
                var packageList = ParseJsonDatabase(jsonContent);
                Console.WriteLine($"[EncryptedDB-JSON] Parsed {packageList?.Count ?? 0} packages");
                
                return packageList;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EncryptedDB-JSON] Error during decrypt/parse: {ex.Message}");
                
                // Clear corrupted cache
                _cachedEncryptedData = null;
                
                return null;
            }
        }

        /// <summary>
        /// Parses the JSON database and flattens it into a list of package entries
        /// </summary>
        private List<FlatPackageEntry> ParseJsonDatabase(string jsonContent)
        {
            const string HubBaseUrl = "https://hub.virtamate.com";
            const string PdrBaseUrl = "https://pixeldrain.com";
            
            var packageList = new List<FlatPackageEntry>();

            try
            {
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
                                Console.WriteLine($"[EncryptedDB-JSON] Error parsing package {creatorName}.{packageKey}: {ex.Message}");
                            }
                        }
                    }
                }

                Console.WriteLine($"[EncryptedDB-JSON] Successfully parsed {packageList.Count} packages");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EncryptedDB-JSON] Error parsing JSON: {ex.Message}");
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

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}

