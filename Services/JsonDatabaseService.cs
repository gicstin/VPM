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
                    Console.WriteLine($"[JsonDB] ✓ Loaded offline database from: {_localDbPath}");
                    Console.WriteLine($"[JsonDB] ✓ File size: {encryptedData.Length:N0} bytes (offline mode - no network required)");
                    _cachedEncryptedData = encryptedData;
                    goto ProcessData;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JsonDB] – Failed to load offline database: {ex.Message}");
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
                    Console.WriteLine($"[JsonDB] Downloading from: {githubUrl}");
                    encryptedData = await _httpClient.GetByteArrayAsync(githubUrl);
                    
                    // Save to in-memory cache
                    _cachedEncryptedData = encryptedData;
                    Console.WriteLine($"[JsonDB] Downloaded {encryptedData.Length:N0} bytes");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JsonDB] Download failed: {ex.Message}");
                }
            }

            LoadFromCache:
            // Fallback to in-memory cache
            if (encryptedData == null && _cachedEncryptedData != null)
            {
                encryptedData = _cachedEncryptedData;
                Console.WriteLine($"[JsonDB] Using in-memory cache");
            }

            ProcessData:
            if (encryptedData == null)
            {
                Console.WriteLine($"[JsonDB] No data available (offline file not found and download failed)");
                return null;
            }

            // Decrypt, decompress, and parse
            try
            {
                byte[] decryptedData = DecryptAES(encryptedData);
                Console.WriteLine($"[JsonDB] Decrypted {decryptedData.Length:N0} bytes");
                
                byte[] decompressedData = DecompressGzip(decryptedData);
                Console.WriteLine($"[JsonDB] Decompressed {decompressedData.Length:N0} bytes");
                
                string jsonContent = Encoding.UTF8.GetString(decompressedData);
                
                var packageList = ParseJsonDatabase(jsonContent);
                Console.WriteLine($"[JsonDB] Parsed {packageList?.Count ?? 0} packages");
                
                return packageList;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JsonDB] Error during decrypt/parse: {ex.Message}");
                Console.WriteLine($"[JsonDB] Stack trace: {ex.StackTrace}");
                
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
                                Console.WriteLine($"[JsonDB] Error parsing package {creatorName}.{packageKey}: {ex.Message}");
                            }
                        }
                    }
                }

                Console.WriteLine($"[JsonDB] Successfully parsed {packageList.Count} packages");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JsonDB] Error parsing JSON: {ex.Message}");
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
            Console.WriteLine("[JsonDB] In-memory cache cleared");
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

