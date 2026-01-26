using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// High-performance binary cache for Hub Resource Details.
    /// Stores all resource details in a single binary file to avoid filesystem clutter (thousands of small JSON files).
    /// </summary>
    public class HubResourceDetailCache : IDisposable
    {
        private const int CACHE_VERSION = 1;
        private const string CACHE_MAGIC = "VPMD"; // VPM Details cache
        
        private readonly string _cacheFilePath;
        private readonly ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim();
        
        // Cache structure: Key -> (Detail, CacheTime)
        // Key is either "id:{resourceId}" or "pkg:{packageName}"
        private Dictionary<string, (HubResourceDetail Detail, DateTime CacheTime)> _cache;
        
        private bool _isDirty = false;
        private DateTime _lastSaveTime = DateTime.MinValue;
        private bool _disposed;

        private readonly TimeSpan _maxCacheAge = TimeSpan.FromDays(7);
        private readonly int _maxEntries = 2000; // Limit memory usage
        
        public string GamePath { get; set; }
        
        // JsonSerializer options for serializing details
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public HubResourceDetailCache()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cacheDir = Path.Combine(appDataPath, "VPM", "Cache");
            _cacheFilePath = Path.Combine(cacheDir, "HubResourceDetails.cache");

            _cache = new Dictionary<string, (HubResourceDetail, DateTime)>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubResourceDetailCache] Failed to create cache directory: {ex}");
            }
        }

        public int Count
        {
            get
            {
                _cacheLock.EnterReadLock();
                try
                {
                    return _cache.Count;
                }
                finally
                {
                    _cacheLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Try to get a resource detail from cache
        /// </summary>
        public HubResourceDetail TryGet(string key)
        {
            _cacheLock.EnterReadLock();
            try
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    // Check expiration
                    if (DateTime.Now - entry.CacheTime < _maxCacheAge)
                    {
                        return entry.Detail;
                    }
                }
                return null;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Store a resource detail in cache
        /// </summary>
        public void Store(string key, HubResourceDetail detail)
        {
            if (string.IsNullOrEmpty(key) || detail == null)
                return;

            _cacheLock.EnterWriteLock();
            try
            {
                // Eviction policy: if too many entries, remove oldest
                if (_cache.Count >= _maxEntries && !_cache.ContainsKey(key))
                {
                    var oldest = _cache.OrderBy(x => x.Value.CacheTime).FirstOrDefault().Key;
                    if (oldest != null)
                        _cache.Remove(oldest);
                }

                _cache[key] = (detail, DateTime.Now);
                _isDirty = true;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Loads the cache from disk
        /// </summary>
        public bool LoadFromDisk()
        {
            if (!File.Exists(_cacheFilePath))
                return false;

            _cacheLock.EnterWriteLock();
            try
            {
                using var stream = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream);

                // Read header
                var magicBytes = reader.ReadBytes(4);
                var magic = Encoding.ASCII.GetString(magicBytes);
                if (magic != CACHE_MAGIC)
                    return false;

                var version = reader.ReadInt32();
                if (version != CACHE_VERSION)
                    return false; // Version mismatch, discard cache

                var count = reader.ReadInt32();
                if (count < 0 || count > 100000) // Sanity check
                    return false;

                _cache.Clear();

                for (int i = 0; i < count; i++)
                {
                    var key = reader.ReadString();
                    var ticks = reader.ReadInt64();
                    var cacheTime = new DateTime(ticks);
                    var jsonLen = reader.ReadInt32();
                    var jsonBytes = reader.ReadBytes(jsonLen);
                    
                    // Lazy deserialization could be done here, but for now we deserialize immediately
                    // to ensure validity and readiness.
                    try
                    {
                        var json = Encoding.UTF8.GetString(jsonBytes);
                        var detail = JsonSerializer.Deserialize<HubResourceDetail>(json, _jsonOptions);
                        if (detail != null)
                        {
                            _cache[key] = (detail, cacheTime);
                        }
                    }
                    catch
                    {
                        // Ignore malformed entries
                    }
                }
                
                _isDirty = false;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubResourceDetailCache] Failed to load cache: {ex}");
                return false;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Saves the cache to disk if it has changed
        /// </summary>
        public void SaveToDisk()
        {
            if (!_isDirty)
                return;

            _cacheLock.EnterReadLock(); // Acquire read lock to copy data for saving
            Dictionary<string, (HubResourceDetail, DateTime)> snapshot;
            try
            {
                snapshot = new Dictionary<string, (HubResourceDetail, DateTime)>(_cache);
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }

            try
            {
                var tempPath = _cacheFilePath + ".tmp";
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Encoding.ASCII.GetBytes(CACHE_MAGIC));
                    writer.Write(CACHE_VERSION);
                    writer.Write(snapshot.Count);

                    foreach (var kvp in snapshot)
                    {
                        writer.Write(kvp.Key);
                        writer.Write(kvp.Value.Item2.Ticks); // CacheTime

                        var json = JsonSerializer.Serialize(kvp.Value.Item1, _jsonOptions);
                        var jsonBytes = Encoding.UTF8.GetBytes(json);
                        writer.Write(jsonBytes.Length);
                        writer.Write(jsonBytes);
                    }
                }

                if (File.Exists(_cacheFilePath))
                    File.Delete(_cacheFilePath);
                File.Move(tempPath, _cacheFilePath);

                _isDirty = false;
                _lastSaveTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HubResourceDetailCache] Failed to save cache: {ex}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                SaveToDisk();
                _cacheLock.Dispose();
                _disposed = true;
            }
        }
    }
}
