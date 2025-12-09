using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;

namespace VPM.Services
{
    // NOTE: This class now integrates with FileAccessController for centralized lock management.
    // All file access goes through FileAccessController to prevent file lock conflicts
    // during optimization operations.
    /// <summary>
    /// Represents a cached entry from a ZIP archive.
    /// Data is loaded on-demand and can be released under memory pressure.
    /// </summary>
    public class VirtualArchiveEntry
    {
        public string Path { get; set; }
        public long CompressedSize { get; set; }
        public long UncompressedSize { get; set; }
        public bool IsDirectory { get; set; }
        
        // Strong reference for recently accessed data
        private byte[] _data;
        
        // Weak reference for memory-pressure-friendly caching
        private WeakReference<byte[]> _weakData;
        
        private readonly object _dataLock = new();
        
        /// <summary>
        /// Gets the cached data if available, or null if not loaded/collected.
        /// </summary>
        public byte[] GetData()
        {
            lock (_dataLock)
            {
                if (_data != null)
                    return _data;
                    
                if (_weakData != null && _weakData.TryGetTarget(out var data))
                    return data;
                    
                return null;
            }
        }
        
        /// <summary>
        /// Sets the cached data. Use demoteToWeak=true to allow GC collection.
        /// </summary>
        public void SetData(byte[] data, bool demoteToWeak = false)
        {
            lock (_dataLock)
            {
                if (demoteToWeak)
                {
                    _data = null;
                    _weakData = data != null ? new WeakReference<byte[]>(data) : null;
                }
                else
                {
                    _data = data;
                    _weakData = null;
                }
            }
        }
        
        /// <summary>
        /// Demotes strong reference to weak reference to allow GC collection.
        /// </summary>
        public void DemoteToWeakReference()
        {
            lock (_dataLock)
            {
                if (_data != null)
                {
                    _weakData = new WeakReference<byte[]>(_data);
                    _data = null;
                }
            }
        }
        
        /// <summary>
        /// Clears all cached data.
        /// </summary>
        public void ClearData()
        {
            lock (_dataLock)
            {
                _data = null;
                _weakData = null;
            }
        }
        
        /// <summary>
        /// Returns true if data is currently cached (strong or weak).
        /// </summary>
        public bool HasCachedData
        {
            get
            {
                lock (_dataLock)
                {
                    return _data != null || (_weakData != null && _weakData.TryGetTarget(out _));
                }
            }
        }
    }
    
    /// <summary>
    /// Represents a virtual (in-memory) view of a ZIP archive.
    /// The file is read once and closed immediately, eliminating file lock issues.
    /// </summary>
    public class VirtualArchive : IDisposable
    {
        public string FilePath { get; }
        public long FileSize { get; private set; }
        public long LastWriteTicks { get; private set; }
        public DateTime LastAccessed { get; private set; }
        public bool IsValid { get; private set; }
        
        private readonly ConcurrentDictionary<string, VirtualArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly ReaderWriterLockSlim _accessLock = new();
        private bool _disposed;
        
        // Memory management
        private long _totalCachedBytes;
        private const long MaxCachedBytesPerArchive = 50 * 1024 * 1024; // 50MB per archive
        
        public VirtualArchive(string filePath)
        {
            FilePath = filePath;
            LastAccessed = DateTime.UtcNow;
        }
        
        /// <summary>
        /// Initializes the virtual archive by reading the ZIP directory structure.
        /// The file is opened, directory is read, and file is closed immediately.
        /// </summary>
        public bool Initialize()
        {
            if (_disposed) return false;
            
            // CRITICAL FIX: Acquire read lock from FileAccessController BEFORE opening the file
            IDisposable readLock = null;
            try
            {
                readLock = FileAccessController.Instance.TryAcquireReadAccessAsync(FilePath).GetAwaiter().GetResult();
                if (readLock == null)
                {
                    IsValid = false;
                    return false; // Writer is waiting or active
                }
            }
            catch (OperationCanceledException)
            {
                IsValid = false;
                return false;
            }
            
            _accessLock.EnterWriteLock();
            try
            {
                if (!File.Exists(FilePath))
                {
                    IsValid = false;
                    return false;
                }
                
                var fileInfo = new FileInfo(FilePath);
                FileSize = fileInfo.Length;
                LastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
                
                // Read directory structure only - no file data yet
                // File is opened and closed within this block
                using (var fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: false))
                using (var archive = ZipArchive.Open(fileStream))
                {
                    foreach (var entry in archive.Entries)
                    {
                        var virtualEntry = new VirtualArchiveEntry
                        {
                            Path = entry.Key,
                            CompressedSize = entry.CompressedSize,
                            UncompressedSize = entry.Size,
                            IsDirectory = entry.IsDirectory
                        };
                        
                        _entries[entry.Key] = virtualEntry;
                    }
                }
                
                IsValid = true;
                LastAccessed = DateTime.UtcNow;
                return true;
            }
            catch (Exception)
            {
                IsValid = false;
                return false;
            }
            finally
            {
                _accessLock.ExitWriteLock();
                readLock?.Dispose(); // Release the FileAccessController read lock
            }
        }
        
        /// <summary>
        /// Validates that the cached archive matches the current file on disk.
        /// </summary>
        public bool ValidateSignature()
        {
            if (_disposed || !IsValid) return false;
            
            try
            {
                if (!File.Exists(FilePath))
                    return false;
                    
                var fileInfo = new FileInfo(FilePath);
                return fileInfo.Length == FileSize && fileInfo.LastWriteTimeUtc.Ticks == LastWriteTicks;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Gets an entry by path, or null if not found.
        /// </summary>
        public VirtualArchiveEntry GetEntry(string path)
        {
            if (_disposed || !IsValid) return null;
            
            LastAccessed = DateTime.UtcNow;
            
            _entries.TryGetValue(path, out var entry);
            return entry;
        }
        
        /// <summary>
        /// Gets all entries in the archive.
        /// </summary>
        public IEnumerable<VirtualArchiveEntry> GetAllEntries()
        {
            if (_disposed || !IsValid) return Enumerable.Empty<VirtualArchiveEntry>();
            
            LastAccessed = DateTime.UtcNow;
            return _entries.Values;
        }
        
        /// <summary>
        /// Reads entry data from the archive file.
        /// File is opened, data is read, file is closed immediately.
        /// 
        /// IMPORTANT: This only reads the SPECIFIC entry requested, not the whole archive.
        /// For a 500MB package with one 50KB preview, only 50KB is read into memory.
        /// 
        /// THREAD SAFETY: Uses FileAccessController to coordinate with write operations.
        /// If a write operation (optimization) is pending, this will return null.
        /// 
        /// FIXED: Replaced UpgradeableReadLock pattern with simpler write lock acquisition
        /// to prevent potential lock corruption on I/O errors during the upgrade.
        /// </summary>
        public byte[] ReadEntryData(string path)
        {
            if (_disposed || !IsValid) return null;
            
            var entry = GetEntry(path);
            if (entry == null || entry.IsDirectory) return null;
            
            // Check if already cached in memory (no lock needed for this check)
            var cachedData = entry.GetData();
            if (cachedData != null)
            {
                LastAccessed = DateTime.UtcNow;
                return cachedData;
            }
            
            // CRITICAL FIX: Acquire read lock from FileAccessController BEFORE opening the file
            // This ensures proper coordination with writers (optimization/move operations)
            // If a writer is waiting, this returns null immediately (fail-fast)
            IDisposable readLock = null;
            try
            {
                readLock = FileAccessController.Instance.TryAcquireReadAccessAsync(FilePath).GetAwaiter().GetResult();
                if (readLock == null)
                {
                    return null; // Writer is waiting or active - fail fast
                }
            }
            catch (OperationCanceledException)
            {
                return null; // File is being optimized
            }
            
            // Use internal write lock for cache consistency
            _accessLock.EnterWriteLock();
            try
            {
                // Double-check after acquiring lock
                cachedData = entry.GetData();
                if (cachedData != null)
                    return cachedData;
                
                // Validate file hasn't changed
                if (!ValidateSignature())
                {
                    IsValid = false;
                    return null;
                }
                
                // Read ONLY the requested entry - file is opened and closed within this block
                // The archive is NOT fully loaded into memory - only the specific entry is decompressed
                using (var fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: false))
                using (var archive = ZipArchive.Open(fileStream))
                {
                    var archiveEntry = archive.Entries.FirstOrDefault(e => 
                        e.Key.Equals(path, StringComparison.OrdinalIgnoreCase));
                        
                    if (archiveEntry == null)
                        return null;
                    
                    // Only decompress this specific entry (e.g., 50KB preview from 500MB package)
                    using (var entryStream = archiveEntry.OpenEntryStream())
                    using (var memoryStream = new MemoryStream())
                    {
                        entryStream.CopyTo(memoryStream);
                        var data = memoryStream.ToArray();
                        
                        // Cache the data with memory management
                        // Small entries (< 1MB) use strong references
                        // Large entries use weak references to allow GC collection
                        bool demoteToWeak = data.Length > 1024 * 1024 || 
                                           Interlocked.Add(ref _totalCachedBytes, data.Length) > MaxCachedBytesPerArchive;
                        entry.SetData(data, demoteToWeak);
                        
                        LastAccessed = DateTime.UtcNow;
                        return data;
                    }
                }
            }
            catch (Exception)
            {
                // Return null on any error (I/O, file locked, etc.)
                return null;
            }
            finally
            {
                _accessLock.ExitWriteLock();
                readLock?.Dispose(); // Release the FileAccessController read lock
            }
        }
        
        /// <summary>
        /// Reads entry data asynchronously.
        /// </summary>
        public async Task<byte[]> ReadEntryDataAsync(string path)
        {
            // Run on thread pool to avoid blocking
            return await Task.Run(() => ReadEntryData(path)).ConfigureAwait(false);
        }
        
        /// <summary>
        /// Reads only the header of an entry (for image dimension detection).
        /// </summary>
        public byte[] ReadEntryHeader(string path, int headerSize = 65536)
        {
            var data = ReadEntryData(path);
            if (data == null) return null;
            
            if (data.Length <= headerSize)
                return data;
                
            var header = new byte[headerSize];
            Array.Copy(data, 0, header, 0, headerSize);
            return header;
        }
        
        /// <summary>
        /// Reads multiple entries in a single file open operation (batch optimization).
        /// Opens the archive ONCE, reads all requested entries, closes immediately.
        /// 
        /// This is the preferred method for loading multiple images from the same package.
        /// Example: Loading 5 preview images from a 500MB package opens the file once,
        /// reads only those 5 entries (~250KB total), and closes immediately.
        /// 
        /// THREAD SAFETY: Uses FileAccessController to coordinate with write operations.
        /// </summary>
        public Dictionary<string, byte[]> ReadEntriesBatch(IEnumerable<string> paths)
        {
            var results = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (_disposed || !IsValid) return results;
            
            var pathsList = paths.ToList();
            if (pathsList.Count == 0) return results;
            
            // First, check what's already cached
            var uncachedPaths = new List<string>();
            foreach (var path in pathsList)
            {
                var entry = GetEntry(path);
                if (entry == null) continue;
                
                var cachedData = entry.GetData();
                if (cachedData != null)
                {
                    results[path] = cachedData;
                }
                else
                {
                    uncachedPaths.Add(path);
                }
            }
            
            // If everything was cached, return early
            if (uncachedPaths.Count == 0)
            {
                LastAccessed = DateTime.UtcNow;
                return results;
            }
            
            // CRITICAL FIX: Acquire read lock from FileAccessController BEFORE opening the file
            IDisposable readLock = null;
            try
            {
                readLock = FileAccessController.Instance.TryAcquireReadAccessAsync(FilePath).GetAwaiter().GetResult();
                if (readLock == null)
                {
                    return results; // Writer is waiting or active - return cached results only
                }
            }
            catch (OperationCanceledException)
            {
                return results; // File is being optimized
            }
            
            _accessLock.EnterWriteLock();
            try
            {
                if (!ValidateSignature())
                {
                    IsValid = false;
                    return results;
                }
                
                // SINGLE file open for ALL requested entries
                using (var fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: false))
                using (var archive = ZipArchive.Open(fileStream))
                {
                    var pathSet = new HashSet<string>(uncachedPaths, StringComparer.OrdinalIgnoreCase);
                    
                    foreach (var archiveEntry in archive.Entries)
                    {
                        if (!pathSet.Contains(archiveEntry.Key)) continue;
                        
                        var entry = GetEntry(archiveEntry.Key);
                        if (entry == null) continue;
                        
                        try
                        {
                            using (var entryStream = archiveEntry.OpenEntryStream())
                            using (var memoryStream = new MemoryStream())
                            {
                                entryStream.CopyTo(memoryStream);
                                var data = memoryStream.ToArray();
                                
                                // Cache with smart memory management
                                bool demoteToWeak = data.Length > 1024 * 1024 || 
                                                   Interlocked.Add(ref _totalCachedBytes, data.Length) > MaxCachedBytesPerArchive;
                                entry.SetData(data, demoteToWeak);
                                
                                results[archiveEntry.Key] = data;
                            }
                        }
                        catch
                        {
                            // Skip entries that fail to read
                        }
                    }
                }
                
                LastAccessed = DateTime.UtcNow;
            }
            catch (OperationCanceledException)
            {
                // File is locked for writing - return what we have cached
            }
            finally
            {
                _accessLock.ExitWriteLock();
                readLock?.Dispose(); // Release the FileAccessController read lock
            }
            
            return results;
        }
        
        /// <summary>
        /// Preloads data for multiple entries (batch operation).
        /// Same as ReadEntriesBatch but doesn't return the data.
        /// </summary>
        public void PreloadEntries(IEnumerable<string> paths)
        {
            ReadEntriesBatch(paths); // Just call batch read and discard return value
        }
        
        /// <summary>
        /// Releases all cached entry data to free memory.
        /// </summary>
        public void ReleaseAllData()
        {
            _accessLock.EnterWriteLock();
            try
            {
                foreach (var entry in _entries.Values)
                {
                    entry.ClearData();
                }
                Interlocked.Exchange(ref _totalCachedBytes, 0);
            }
            finally
            {
                _accessLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Demotes all strong references to weak references.
        /// </summary>
        public void DemoteAllToWeakReferences()
        {
            _accessLock.EnterWriteLock();
            try
            {
                foreach (var entry in _entries.Values)
                {
                    entry.DemoteToWeakReference();
                }
            }
            finally
            {
                _accessLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Gets the total bytes currently cached.
        /// </summary>
        public long GetCachedBytes() => Interlocked.Read(ref _totalCachedBytes);
        
        /// <summary>
        /// Gets the number of entries in the archive.
        /// </summary>
        public int EntryCount => _entries.Count;
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            ReleaseAllData();
            _entries.Clear();
            _accessLock.Dispose();
        }
    }
    
    /// <summary>
    /// Global cache for virtual archives.
    /// Provides lock-free access to archive contents without holding file handles.
    /// </summary>
    public class VirtualArchiveCache : IDisposable
    {
        private readonly ConcurrentDictionary<string, VirtualArchive> _archives = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _cleanupTimer;
        private readonly TimeSpan _archiveTimeout = TimeSpan.FromMinutes(5);
        private readonly long _maxTotalCacheBytes;
        private bool _disposed;
        
        // Statistics
        private long _cacheHits;
        private long _cacheMisses;
        private long _bytesRead;
        
        // Track consecutive cleanup failures for diagnostics
        private int _consecutiveCleanupFailures = 0;
        private const int MaxConsecutiveCleanupFailures = 5;
        
        public VirtualArchiveCache(long maxTotalCacheBytes = 500 * 1024 * 1024) // 500MB default
        {
            _maxTotalCacheBytes = maxTotalCacheBytes;
            
            // Cleanup timer runs every 30 seconds
            _cleanupTimer = new Timer(CleanupCallback, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
        
        /// <summary>
        /// Gets or creates a virtual archive for the specified file path.
        /// </summary>
        public VirtualArchive GetOrCreateArchive(string filePath)
        {
            if (_disposed) return null;
            if (string.IsNullOrEmpty(filePath)) return null;
            
            // Try to get existing archive
            if (_archives.TryGetValue(filePath, out var existing))
            {
                // Validate it's still valid
                if (existing.IsValid && existing.ValidateSignature())
                {
                    Interlocked.Increment(ref _cacheHits);
                    return existing;
                }
                
                // Invalid - remove and recreate
                _archives.TryRemove(filePath, out _);
                existing.Dispose();
            }
            
            Interlocked.Increment(ref _cacheMisses);
            
            // Create new archive
            var archive = new VirtualArchive(filePath);
            if (!archive.Initialize())
            {
                archive.Dispose();
                return null;
            }
            
            // Add to cache (may race with another thread, that's OK)
            if (_archives.TryAdd(filePath, archive))
            {
                return archive;
            }
            
            // Another thread added it first, use theirs
            archive.Dispose();
            _archives.TryGetValue(filePath, out existing);
            return existing;
        }
        
        /// <summary>
        /// Reads entry data from an archive, using cache when possible.
        /// </summary>
        public byte[] ReadEntryData(string archivePath, string entryPath)
        {
            var archive = GetOrCreateArchive(archivePath);
            if (archive == null) return null;
            
            var data = archive.ReadEntryData(entryPath);
            if (data != null)
            {
                Interlocked.Add(ref _bytesRead, data.Length);
            }
            return data;
        }
        
        /// <summary>
        /// Reads entry data asynchronously.
        /// </summary>
        public async Task<byte[]> ReadEntryDataAsync(string archivePath, string entryPath)
        {
            var archive = GetOrCreateArchive(archivePath);
            if (archive == null) return null;
            
            var data = await archive.ReadEntryDataAsync(entryPath).ConfigureAwait(false);
            if (data != null)
            {
                Interlocked.Add(ref _bytesRead, data.Length);
            }
            return data;
        }
        
        /// <summary>
        /// Reads multiple entries from an archive in a single file open operation.
        /// This is the most efficient method for loading multiple images from the same package.
        /// 
        /// Example: Loading 5 preview images from a 500MB package:
        /// - Opens file ONCE
        /// - Reads only the 5 requested entries (~250KB total)
        /// - Closes file immediately
        /// - No file locks held after return
        /// </summary>
        public Dictionary<string, byte[]> ReadEntriesBatch(string archivePath, IEnumerable<string> entryPaths)
        {
            var archive = GetOrCreateArchive(archivePath);
            if (archive == null) return new Dictionary<string, byte[]>();
            
            var results = archive.ReadEntriesBatch(entryPaths);
            
            // Track bytes read
            foreach (var data in results.Values)
            {
                Interlocked.Add(ref _bytesRead, data.Length);
            }
            
            return results;
        }
        
        /// <summary>
        /// Gets an entry from an archive without reading its data.
        /// </summary>
        public VirtualArchiveEntry GetEntry(string archivePath, string entryPath)
        {
            var archive = GetOrCreateArchive(archivePath);
            return archive?.GetEntry(entryPath);
        }
        
        /// <summary>
        /// Gets all entries from an archive.
        /// </summary>
        public IEnumerable<VirtualArchiveEntry> GetAllEntries(string archivePath)
        {
            var archive = GetOrCreateArchive(archivePath);
            return archive?.GetAllEntries() ?? Enumerable.Empty<VirtualArchiveEntry>();
        }
        
        /// <summary>
        /// Invalidates and removes a specific archive from cache.
        /// Call this before moving/deleting the file.
        /// </summary>
        public void InvalidateArchive(string filePath)
        {
            if (_archives.TryRemove(filePath, out var archive))
            {
                archive.Dispose();
            }
            
            // Also check by filename (different paths may reference same file)
            var fileName = Path.GetFileName(filePath);
            var keysToRemove = _archives.Keys
                .Where(k => Path.GetFileName(k).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
                
            foreach (var key in keysToRemove)
            {
                if (_archives.TryRemove(key, out archive))
                {
                    archive.Dispose();
                }
            }
        }
        
        /// <summary>
        /// Invalidates all archives. Call before bulk file operations.
        /// </summary>
        public void InvalidateAll()
        {
            var keys = _archives.Keys.ToList();
            foreach (var key in keys)
            {
                if (_archives.TryRemove(key, out var archive))
                {
                    archive.Dispose();
                }
            }
        }
        
        /// <summary>
        /// Releases memory by demoting all cached data to weak references.
        /// </summary>
        public void ReleaseMemory()
        {
            foreach (var archive in _archives.Values)
            {
                archive.DemoteAllToWeakReferences();
            }
        }
        
        /// <summary>
        /// Gets cache statistics.
        /// </summary>
        public (long hits, long misses, double hitRate, long bytesRead, int archiveCount, long totalCachedBytes) GetStatistics()
        {
            var totalHits = Interlocked.Read(ref _cacheHits);
            var totalMisses = Interlocked.Read(ref _cacheMisses);
            var totalRequests = totalHits + totalMisses;
            var hitRate = totalRequests > 0 ? (totalHits * 100.0 / totalRequests) : 0;
            var bytesRead = Interlocked.Read(ref _bytesRead);
            var archiveCount = _archives.Count;
            var totalCachedBytes = _archives.Values.Sum(a => a.GetCachedBytes());
            
            return (totalHits, totalMisses, hitRate, bytesRead, archiveCount, totalCachedBytes);
        }
        
        /// <summary>
        /// Resets statistics.
        /// </summary>
        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _cacheHits, 0);
            Interlocked.Exchange(ref _cacheMisses, 0);
            Interlocked.Exchange(ref _bytesRead, 0);
        }
        
        private void CleanupCallback(object state)
        {
            if (_disposed) return;
            
            try
            {
                var now = DateTime.UtcNow;
                var keysToRemove = new List<string>();
                
                // Find stale archives
                foreach (var kvp in _archives)
                {
                    if (now - kvp.Value.LastAccessed > _archiveTimeout)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                
                // Remove stale archives
                foreach (var key in keysToRemove)
                {
                    if (_archives.TryRemove(key, out var archive))
                    {
                        archive.Dispose();
                    }
                }
                
                // Check total memory usage
                var totalCachedBytes = _archives.Values.Sum(a => a.GetCachedBytes());
                if (totalCachedBytes > _maxTotalCacheBytes)
                {
                    // Release memory from oldest archives first
                    var archivesByAge = _archives.Values
                        .OrderBy(a => a.LastAccessed)
                        .ToList();
                        
                    foreach (var archive in archivesByAge)
                    {
                        archive.DemoteAllToWeakReferences();
                        totalCachedBytes = _archives.Values.Sum(a => a.GetCachedBytes());
                        if (totalCachedBytes <= _maxTotalCacheBytes * 0.7) // Target 70% of max
                            break;
                    }
                }
                
                // Reset failure counter on success
                _consecutiveCleanupFailures = 0;
            }
            catch (Exception ex)
            {
                // FIXED: Track consecutive failures instead of silently ignoring
                _consecutiveCleanupFailures++;
                
                if (_consecutiveCleanupFailures >= MaxConsecutiveCleanupFailures)
                {
                    System.Diagnostics.Debug.WriteLine($"[VirtualArchiveCache] Cleanup failed {_consecutiveCleanupFailures} times. Last error: {ex.Message}");
                }
            }
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _cleanupTimer.Dispose();
            InvalidateAll();
        }
    }
}
