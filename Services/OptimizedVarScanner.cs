using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPM.Models;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;

namespace VPM.Services
{
    public class OptimizedVarScanner
    {
        private long _totalFilesScanned = 0;
        private long _totalFilesSkipped = 0;
        private long _totalFilesIndexed = 0;

        public (long scanned, long skipped, long indexed, double skipPercentage) GetStatistics()
        {
            var total = _totalFilesScanned;
            var skipPercentage = total > 0 ? (_totalFilesSkipped * 100.0 / total) : 0;
            return (_totalFilesScanned, _totalFilesSkipped, _totalFilesIndexed, skipPercentage);
        }

        public void ResetStatistics()
        {
            _totalFilesScanned = 0;
            _totalFilesSkipped = 0;
            _totalFilesIndexed = 0;
        }

        private static readonly HashSet<string> IndexedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".json",
            ".jpg",
            ".jpeg",
            ".vam",
            ".vap",
            ".assetbundle",
            ".cs",
            ".cslist"
        };

        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".vmi"
        };

        private static readonly HashSet<string> PairedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".json",
            ".vam",
            ".vap"
        };

        public VarScanResult ScanVarFile(string varPath, bool indexAllFiles = false)
        {
            var result = new VarScanResult
            {
                VarPath = varPath,
                Success = false
            };

            try
            {
                var fileInfo = new FileInfo(varPath);
                if (!fileInfo.Exists)
                {
                    result.ErrorMessage = "File does not exist";
                    return result;
                }

                result.FileSize = fileInfo.Length;
                result.LastWriteTime = fileInfo.LastWriteTimeUtc;

                using (var zipFile = ZipArchive.Open(varPath))
                {
                    var indexedEntries = new List<VarFileEntry>();
                    var pairedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var contentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int totalFileCount = 0;
                    string metaJsonContent = null;

                    // First pass: collect all entries and metadata
                    var allEntries = SharpCompressHelper.GetAllEntries(zipFile);
                    totalFileCount = allEntries.Count;

                    // Extract meta.json content
                    var metaEntry = SharpCompressHelper.FindEntryByPath(zipFile, "meta.json");
                    if (metaEntry != null)
                    {
                        result.HasMetaJson = true;
                        metaJsonContent = SharpCompressHelper.ReadEntryAsString(zipFile, metaEntry);
                    }

                    // Process entries
                    foreach (var entry in allEntries)
                    {
                        if (entry.IsDirectory) continue;

                        // Skip if already added as paired file (before counting as scanned)
                        if (pairedFiles.Contains(entry.Key))
                        {
                            continue;
                        }

                        System.Threading.Interlocked.Increment(ref _totalFilesScanned);

                        var ext = Path.GetExtension(entry.Key);
                        
                        if (SkippedExtensions.Contains(ext))
                        {
                            result.SkippedFileCount++;
                            System.Threading.Interlocked.Increment(ref _totalFilesSkipped);
                            continue;
                        }

                        if (!indexAllFiles && !IndexedExtensions.Contains(ext))
                        {
                            result.SkippedFileCount++;
                            System.Threading.Interlocked.Increment(ref _totalFilesSkipped);
                            continue;
                        }

                        // Add paired JPG if this file has a paired extension
                        if (PairedExtensions.Contains(ext))
                        {
                            var baseName = Path.ChangeExtension(entry.Key, null);
                            
                            // Try both .jpg and .JPG (case variations)
                            IArchiveEntry jpgEntry = null;
                            string jpgPath = null;
                            
                            foreach (var jpgExt in new[] { ".jpg", ".JPG", ".Jpg" })
                            {
                                var testPath = baseName + jpgExt;
                                if (!pairedFiles.Contains(testPath))
                                {
                                    jpgEntry = SharpCompressHelper.FindEntryByPath(zipFile, testPath);
                                    if (jpgEntry != null)
                                    {
                                        jpgPath = testPath;
                                        break;
                                    }
                                }
                            }

                            if (jpgEntry != null && jpgPath != null)
                            {
                                indexedEntries.Add(new VarFileEntry
                                {
                                    InternalPath = StringPool.InternPath(jpgEntry.Key),
                                    Size = jpgEntry.Size,
                                    LastWriteTime = (DateTime)(jpgEntry.LastModifiedTime ?? DateTime.UtcNow)
                                });
                                pairedFiles.Add(jpgPath);
                                System.Threading.Interlocked.Increment(ref _totalFilesIndexed);
                            }
                        }

                        indexedEntries.Add(new VarFileEntry
                        {
                            InternalPath = StringPool.InternPath(entry.Key),
                            Size = entry.Size,
                            LastWriteTime = (DateTime)(entry.LastModifiedTime ?? DateTime.UtcNow)
                        });

                        System.Threading.Interlocked.Increment(ref _totalFilesIndexed);
                        AnalyzeFileForMetadata(entry.Key, contentTypes, categories);
                    }

                    result.IndexedEntries = indexedEntries;
                    result.TotalFileCount = totalFileCount;
                    result.IndexedFileCount = indexedEntries.Count;
                    result.ContentTypes = contentTypes;
                    result.Categories = categories;
                    result.MetaJsonContent = metaJsonContent;
                    result.Success = true;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private void AnalyzeFileForMetadata(string path, HashSet<string> contentTypes, HashSet<string> categories)
        {
            // Use OrdinalIgnoreCase comparison instead of ToLowerInvariant() to avoid string allocations
            // This is a hot path called for every file in every package
            
            if (path.Contains("/saves/scene/", StringComparison.OrdinalIgnoreCase))
            {
                contentTypes.Add("Scenes");
                categories.Add("Scene");
            }
            else if (path.Contains("/custom/atom/person/appearance/", StringComparison.OrdinalIgnoreCase))
            {
                contentTypes.Add("Looks");
                categories.Add("Appearance");
            }
            else if (path.Contains("/custom/atom/person/pose/", StringComparison.OrdinalIgnoreCase))
            {
                contentTypes.Add("Poses");
                categories.Add("Pose");
            }
            else if (path.Contains("/custom/clothing/", StringComparison.OrdinalIgnoreCase))
            {
                contentTypes.Add("Clothing");
                categories.Add("Clothing");
            }
            else if (path.Contains("/custom/hair/", StringComparison.OrdinalIgnoreCase))
            {
                contentTypes.Add("Hair");
                categories.Add("Hair");
            }
            else if (path.Contains("/custom/assets/", StringComparison.OrdinalIgnoreCase))
            {
                contentTypes.Add("Assets");
                categories.Add("Asset");
            }
            else if (path.Contains("/custom/subscene/", StringComparison.OrdinalIgnoreCase))
            {
                contentTypes.Add("SubScenes");
                categories.Add("SubScene");
            }
            else if (path.Contains("/custom/scripts/", StringComparison.OrdinalIgnoreCase))
            {
                contentTypes.Add("Scripts");
                categories.Add("Script");
            }
            else if (path.EndsWith(".assetbundle", StringComparison.OrdinalIgnoreCase))
            {
                contentTypes.Add("AssetBundles");
            }
        }

        public LazyZipArchive OpenLazy(string varPath)
        {
            return new LazyZipArchive(varPath);
        }
    }

    public class VarScanResult
    {
        public string VarPath { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public long FileSize { get; set; }
        public DateTime LastWriteTime { get; set; }
        public int TotalFileCount { get; set; }
        public int IndexedFileCount { get; set; }
        public int SkippedFileCount { get; set; }
        public List<VarFileEntry> IndexedEntries { get; set; } = new();
        public HashSet<string> ContentTypes { get; set; } = new();
        public HashSet<string> Categories { get; set; } = new();
        public bool HasMetaJson { get; set; }
        public string MetaJsonContent { get; set; }
    }

    public class VarFileEntry
    {
        public string InternalPath { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
    }

    public class LazyZipArchive : IDisposable
    {
        private readonly string _varPath;
        private IArchive _archive;
        private bool _disposed;
        private readonly object _lock = new object();

        public LazyZipArchive(string varPath)
        {
            _varPath = varPath;
        }

        private void EnsureOpen()
        {
            if (_archive == null && !_disposed)
            {
                lock (_lock)
                {
                    if (_archive == null && !_disposed)
                    {
                        _archive = ZipArchive.Open(_varPath);
                    }
                }
            }
        }

        public IArchiveEntry GetEntry(string entryName)
        {
            EnsureOpen();
            return _archive != null ? SharpCompressHelper.FindEntryByPath(_archive, entryName) : null;
        }

        public List<IArchiveEntry> Entries
        {
            get
            {
                EnsureOpen();
                return _archive != null ? SharpCompressHelper.GetAllEntries(_archive) : new List<IArchiveEntry>();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _archive?.Dispose();
                    _disposed = true;
                }
            }
        }
    }
}

