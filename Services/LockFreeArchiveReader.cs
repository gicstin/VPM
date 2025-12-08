using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Archives;

namespace VPM.Services
{
    /// <summary>
    /// Lock-free archive reader that uses VirtualArchiveCache to eliminate file locking issues.
    /// This class provides the same interface as SharpCompressHelper but without holding file handles.
    /// </summary>
    public class LockFreeArchiveReader : IDisposable
    {
        private readonly VirtualArchiveCache _cache;
        private bool _disposed;
        
        // Singleton instance for global access
        private static readonly Lazy<LockFreeArchiveReader> _instance = new(() => new LockFreeArchiveReader());
        public static LockFreeArchiveReader Instance => _instance.Value;
        
        public LockFreeArchiveReader(long maxCacheBytes = 500 * 1024 * 1024)
        {
            _cache = new VirtualArchiveCache(maxCacheBytes);
        }
        
        /// <summary>
        /// Reads entry data from an archive without holding file locks.
        /// Returns null if the file is locked for writing (optimization in progress).
        /// </summary>
        public byte[] ReadEntryData(string archivePath, string entryPath)
        {
            if (_disposed) return null;
            
            // CRITICAL: Check FileAccessController FIRST - if file is locked for writing, fail fast
            if (FileAccessController.Instance.IsFileLockedForWriting(archivePath))
            {
                return null; // File is being optimized
            }
            
            return _cache.ReadEntryData(archivePath, entryPath);
        }
        
        /// <summary>
        /// Reads entry data asynchronously.
        /// Returns null if the file is locked for writing (optimization in progress).
        /// </summary>
        public Task<byte[]> ReadEntryDataAsync(string archivePath, string entryPath)
        {
            if (_disposed) return Task.FromResult<byte[]>(null);
            
            // CRITICAL: Check FileAccessController FIRST - if file is locked for writing, fail fast
            if (FileAccessController.Instance.IsFileLockedForWriting(archivePath))
            {
                return Task.FromResult<byte[]>(null); // File is being optimized
            }
            
            return _cache.ReadEntryDataAsync(archivePath, entryPath);
        }
        
        /// <summary>
        /// Reads only the header of an entry (for image dimension detection).
        /// Returns null if the file is locked for writing (optimization in progress).
        /// </summary>
        public byte[] ReadEntryHeader(string archivePath, string entryPath, int headerSize = 65536)
        {
            if (_disposed) return null;
            
            // CRITICAL: Check FileAccessController FIRST - if file is locked for writing, fail fast
            if (FileAccessController.Instance.IsFileLockedForWriting(archivePath))
            {
                return null; // File is being optimized
            }
            
            var archive = _cache.GetOrCreateArchive(archivePath);
            return archive?.ReadEntryHeader(entryPath, headerSize);
        }
        
        /// <summary>
        /// Reads multiple entries from an archive in a single file open operation.
        /// This is the most efficient method for batch loading (e.g., multiple preview images).
        /// 
        /// Key benefits:
        /// - Opens the VAR file ONCE for all entries
        /// - Only reads the specific entries requested (not the whole archive)
        /// - Closes file immediately after reading
        /// - No file locks held after return
        /// 
        /// Example: For a 500MB package with 100 textures but only 3 preview images,
        /// this reads ~150KB (the 3 previews) not 500MB.
        /// 
        /// Returns empty dictionary if the file is locked for writing (optimization in progress).
        /// </summary>
        public Dictionary<string, byte[]> ReadEntriesBatch(string archivePath, IEnumerable<string> entryPaths)
        {
            if (_disposed) return new Dictionary<string, byte[]>();
            
            // CRITICAL: Check FileAccessController FIRST - if file is locked for writing, fail fast
            if (FileAccessController.Instance.IsFileLockedForWriting(archivePath))
            {
                return new Dictionary<string, byte[]>(); // File is being optimized
            }
            
            return _cache.ReadEntriesBatch(archivePath, entryPaths);
        }
        
        /// <summary>
        /// Gets entry metadata without reading data.
        /// </summary>
        public VirtualArchiveEntry GetEntry(string archivePath, string entryPath)
        {
            if (_disposed) return null;
            return _cache.GetEntry(archivePath, entryPath);
        }
        
        /// <summary>
        /// Gets all entries from an archive.
        /// </summary>
        public IEnumerable<VirtualArchiveEntry> GetAllEntries(string archivePath)
        {
            if (_disposed) return Enumerable.Empty<VirtualArchiveEntry>();
            return _cache.GetAllEntries(archivePath);
        }
        
        /// <summary>
        /// Finds an entry by path (case-insensitive).
        /// </summary>
        public VirtualArchiveEntry FindEntryByPath(string archivePath, string fullPath)
        {
            if (_disposed) return null;
            
            var entries = _cache.GetAllEntries(archivePath);
            return entries.FirstOrDefault(e => 
                e.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// Checks if an entry exists in the archive.
        /// </summary>
        public bool EntryExists(string archivePath, string entryPath)
        {
            return GetEntry(archivePath, entryPath) != null;
        }
        
        /// <summary>
        /// Gets image dimensions from an entry using header-only reading.
        /// </summary>
        public (int width, int height) GetImageDimensions(string archivePath, string entryPath)
        {
            var headerData = ReadEntryHeader(archivePath, entryPath, 65536);
            if (headerData == null || headerData.Length < 2)
                return (0, 0);
                
            return ParseImageDimensions(headerData);
        }
        
        /// <summary>
        /// Validates an entry as a valid image using header-only reading.
        /// </summary>
        public bool IsValidImageEntry(string archivePath, string entryPath)
        {
            var headerData = ReadEntryHeader(archivePath, entryPath, 8);
            if (headerData == null || headerData.Length < 4)
                return false;
                
            // Check PNG signature (89 50 4E 47)
            if (headerData[0] == 0x89 && headerData[1] == 0x50 && 
                headerData[2] == 0x4E && headerData[3] == 0x47)
                return true;
                
            // Check JPEG signature (FF D8 FF)
            if (headerData.Length >= 3 && 
                headerData[0] == 0xFF && headerData[1] == 0xD8 && headerData[2] == 0xFF)
                return true;
                
            return false;
        }
        
        /// <summary>
        /// Preloads multiple entries for batch operations.
        /// </summary>
        public void PreloadEntries(string archivePath, IEnumerable<string> entryPaths)
        {
            if (_disposed) return;
            
            var archive = _cache.GetOrCreateArchive(archivePath);
            archive?.PreloadEntries(entryPaths);
        }
        
        /// <summary>
        /// Invalidates cache for a specific archive.
        /// MUST be called before moving/deleting the file.
        /// </summary>
        public void InvalidateArchive(string archivePath)
        {
            if (_disposed) return;
            _cache.InvalidateArchive(archivePath);
        }
        
        /// <summary>
        /// Invalidates all cached archives.
        /// Call before bulk file operations.
        /// </summary>
        public void InvalidateAll()
        {
            if (_disposed) return;
            _cache.InvalidateAll();
        }
        
        /// <summary>
        /// Releases memory by demoting cached data to weak references.
        /// </summary>
        public void ReleaseMemory()
        {
            if (_disposed) return;
            _cache.ReleaseMemory();
        }
        
        /// <summary>
        /// Gets cache statistics.
        /// </summary>
        public (long hits, long misses, double hitRate, long bytesRead, int archiveCount, long totalCachedBytes) GetStatistics()
        {
            if (_disposed) return (0, 0, 0, 0, 0, 0);
            return _cache.GetStatistics();
        }
        
        /// <summary>
        /// Resets statistics.
        /// </summary>
        public void ResetStatistics()
        {
            if (_disposed) return;
            _cache.ResetStatistics();
        }
        
        /// <summary>
        /// Parses image dimensions from header data.
        /// </summary>
        private (int width, int height) ParseImageDimensions(byte[] headerData)
        {
            try
            {
                // Check for PNG signature (89 50 4E 47)
                if (headerData.Length >= 24 && 
                    headerData[0] == 0x89 && headerData[1] == 0x50 && 
                    headerData[2] == 0x4E && headerData[3] == 0x47)
                {
                    // PNG dimensions are at bytes 16-23 (big-endian)
                    int width = (headerData[16] << 24) | (headerData[17] << 16) | (headerData[18] << 8) | headerData[19];
                    int height = (headerData[20] << 24) | (headerData[21] << 16) | (headerData[22] << 8) | headerData[23];
                    
                    if (width > 0 && height > 0 && width < 100000 && height < 100000)
                        return (width, height);
                }
                
                // Check for JPEG signature (FF D8)
                if (headerData[0] == 0xFF && headerData[1] == 0xD8)
                {
                    // Parse JPEG markers to find SOF (Start of Frame)
                    int pos = 2;
                    while (pos + 2 < headerData.Length)
                    {
                        // Find next marker
                        while (pos < headerData.Length && headerData[pos] != 0xFF) pos++;
                        if (pos >= headerData.Length - 1) break;
                        
                        byte marker = headerData[pos + 1];
                        
                        // Skip padding bytes
                        if (marker == 0x00 || marker == 0xFF)
                        {
                            pos++;
                            continue;
                        }
                        
                        pos += 2;
                        if (pos + 2 > headerData.Length) break;
                        
                        int length = (headerData[pos] << 8) | headerData[pos + 1];
                        
                        // SOF markers (all variants)
                        if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7) || 
                            (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
                        {
                            if (pos + 7 <= headerData.Length)
                            {
                                int height = (headerData[pos + 3] << 8) | headerData[pos + 4];
                                int width = (headerData[pos + 5] << 8) | headerData[pos + 6];
                                
                                if (width > 0 && height > 0 && width < 100000 && height < 100000)
                                    return (width, height);
                            }
                        }
                        
                        pos += length;
                        if (pos > headerData.Length) break;
                    }
                }
            }
            catch
            {
                // If any error occurs, return invalid dimensions
            }
            
            return (0, 0);
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Dispose();
        }
    }
    
    /// <summary>
    /// Extension methods for seamless integration with existing code.
    /// </summary>
    public static class LockFreeArchiveExtensions
    {
        /// <summary>
        /// Reads entry as string using lock-free reader.
        /// </summary>
        public static string ReadEntryAsString(this LockFreeArchiveReader reader, string archivePath, string entryPath)
        {
            var data = reader.ReadEntryData(archivePath, entryPath);
            if (data == null) return null;
            return System.Text.Encoding.UTF8.GetString(data);
        }
        
        /// <summary>
        /// Reads entry as string asynchronously.
        /// </summary>
        public static async Task<string> ReadEntryAsStringAsync(this LockFreeArchiveReader reader, string archivePath, string entryPath)
        {
            var data = await reader.ReadEntryDataAsync(archivePath, entryPath).ConfigureAwait(false);
            if (data == null) return null;
            return System.Text.Encoding.UTF8.GetString(data);
        }
        
        /// <summary>
        /// Filters entries by extension.
        /// </summary>
        public static IEnumerable<VirtualArchiveEntry> FilterByExtension(
            this IEnumerable<VirtualArchiveEntry> entries, params string[] extensions)
        {
            var extensionSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            return entries.Where(e => !e.IsDirectory && extensionSet.Contains(Path.GetExtension(e.Path)));
        }
        
        /// <summary>
        /// Filters entries by path prefix.
        /// </summary>
        public static IEnumerable<VirtualArchiveEntry> FilterByPath(
            this IEnumerable<VirtualArchiveEntry> entries, string pathPrefix)
        {
            return entries.Where(e => e.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) && !e.IsDirectory);
        }
    }
}
