using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using VPM.Models;

namespace VPM.Services
{
    public sealed class HubSearchCache
    {
        private const int CACHE_VERSION = 1;
        private const string CACHE_MAGIC = "VPMS"; // VPM Search cache

        private readonly string _cacheDirectory;
        private readonly string _cacheFilePath;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        private readonly TimeSpan _ttl;
        private readonly int _maxEntries;

        private Dictionary<string, CacheEntry> _entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        private sealed class CacheEntry
        {
            public DateTime CachedAtUtc { get; set; }
            public byte[] JsonUtf8 { get; set; }
        }

        public HubSearchCache(TimeSpan ttl, int maxEntries)
        {
            _ttl = ttl;
            _maxEntries = Math.Max(1, maxEntries);

            _cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPM", "Cache");
            _cacheFilePath = Path.Combine(_cacheDirectory, "HubSearch.cache");

            try
            {
                if (!Directory.Exists(_cacheDirectory))
                    Directory.CreateDirectory(_cacheDirectory);
            }
            catch (Exception)
            {
            }

            LoadFromDisk();
        }

        public bool TryGet(string key, out HubSearchResponse response)
        {
            response = null;
            if (string.IsNullOrEmpty(key))
                return false;

            _lock.EnterUpgradeableReadLock();
            try
            {
                if (!_entries.TryGetValue(key, out var entry) || entry?.JsonUtf8 == null)
                    return false;

                if (DateTime.UtcNow - entry.CachedAtUtc > _ttl)
                {
                    _lock.EnterWriteLock();
                    try
                    {
                        _entries.Remove(key);
                    }
                    finally
                    {
                        _lock.ExitWriteLock();
                    }
                    return false;
                }

                try
                {
                    response = JsonSerializer.Deserialize<HubSearchResponse>(entry.JsonUtf8, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    return response != null;
                }
                catch
                {
                    _lock.EnterWriteLock();
                    try
                    {
                        _entries.Remove(key);
                    }
                    finally
                    {
                        _lock.ExitWriteLock();
                    }
                    return false;
                }
            }
            finally
            {
                _lock.ExitUpgradeableReadLock();
            }
        }

        public void Store(string key, HubSearchResponse response)
        {
            if (string.IsNullOrEmpty(key) || response == null)
                return;

            byte[] json;
            try
            {
                json = JsonSerializer.SerializeToUtf8Bytes(response);
            }
            catch
            {
                return;
            }

            _lock.EnterWriteLock();
            try
            {
                _entries[key] = new CacheEntry
                {
                    CachedAtUtc = DateTime.UtcNow,
                    JsonUtf8 = json
                };

                PruneIfNeeded_NoLock();
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            SaveToDisk();
        }

        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _entries.Clear();
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            try
            {
                if (File.Exists(_cacheFilePath))
                    File.Delete(_cacheFilePath);
            }
            catch
            {
            }
        }

        private void PruneIfNeeded_NoLock()
        {
            var nowUtc = DateTime.UtcNow;

            // TTL prune
            var expiredKeys = _entries
                .Where(kvp => nowUtc - kvp.Value.CachedAtUtc > _ttl)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var k in expiredKeys)
                _entries.Remove(k);

            // Size prune
            if (_entries.Count <= _maxEntries)
                return;

            var ordered = _entries.OrderBy(kvp => kvp.Value.CachedAtUtc).Select(kvp => kvp.Key).ToList();
            var removeCount = _entries.Count - _maxEntries;
            for (int i = 0; i < removeCount && i < ordered.Count; i++)
                _entries.Remove(ordered[i]);
        }

        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_cacheFilePath))
                    return;

                using var fs = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

                var magic = new string(br.ReadChars(4));
                if (!string.Equals(magic, CACHE_MAGIC, StringComparison.Ordinal))
                    return;

                var version = br.ReadInt32();
                if (version != CACHE_VERSION)
                    return;

                var count = br.ReadInt32();
                if (count < 0 || count > 100000)
                    return;

                var tmp = new Dictionary<string, CacheEntry>(Math.Min(count, _maxEntries), StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    var key = br.ReadString();
                    var ticks = br.ReadInt64();
                    var len = br.ReadInt32();

                    if (len <= 0 || len > 50_000_000)
                    {
                        br.ReadBytes(Math.Max(0, len));
                        continue;
                    }

                    var data = br.ReadBytes(len);
                    tmp[key] = new CacheEntry
                    {
                        CachedAtUtc = new DateTime(ticks, DateTimeKind.Utc),
                        JsonUtf8 = data
                    };
                }

                _lock.EnterWriteLock();
                try
                {
                    _entries = tmp;
                    PruneIfNeeded_NoLock();
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }
            catch (Exception)
            {
            }
        }

        private void SaveToDisk()
        {
            Dictionary<string, CacheEntry> snapshot;
            _lock.EnterReadLock();
            try
            {
                snapshot = new Dictionary<string, CacheEntry>(_entries.Count, StringComparer.Ordinal);
                foreach (var kvp in _entries)
                {
                    snapshot[kvp.Key] = new CacheEntry
                    {
                        CachedAtUtc = kvp.Value.CachedAtUtc,
                        JsonUtf8 = kvp.Value.JsonUtf8
                    };
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            try
            {
                var tmpPath = _cacheFilePath + ".tmp";
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
                {
                    bw.Write(CACHE_MAGIC.ToCharArray());
                    bw.Write(CACHE_VERSION);
                    bw.Write(snapshot.Count);

                    foreach (var kvp in snapshot)
                    {
                        bw.Write(kvp.Key);
                        bw.Write(kvp.Value.CachedAtUtc.Ticks);
                        var data = kvp.Value.JsonUtf8 ?? Array.Empty<byte>();
                        bw.Write(data.Length);
                        bw.Write(data);
                    }
                }

                File.Copy(tmpPath, _cacheFilePath, overwrite: true);
                try { File.Delete(tmpPath); } catch { }
            }
            catch (Exception)
            {
            }
        }
    }
}
