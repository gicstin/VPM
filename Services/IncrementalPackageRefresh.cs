using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Provides incremental package refresh functionality using file system monitoring.
    /// Instead of rescanning all packages, only processes changed/new/deleted packages.
    /// </summary>
    public class IncrementalPackageRefresh : IDisposable
    {
        private readonly PackageManager _packageManager;
        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly ConcurrentQueue<FileChangeEvent> _pendingChanges = new();
        private readonly object _processLock = new();
        private readonly Timer _debounceTimer;
        private readonly int _debounceDelayMs;
        private bool _disposed;
        private string _rootFolder;
        
        // Track file states for change detection
        private readonly ConcurrentDictionary<string, FileState> _fileStates = new(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Event raised when packages have been incrementally updated
        /// </summary>
        public event EventHandler<IncrementalRefreshResult> PackagesUpdated;
        
        /// <summary>
        /// Event raised when a full refresh is recommended (too many changes)
        /// </summary>
        public event EventHandler FullRefreshRecommended;

        public IncrementalPackageRefresh(PackageManager packageManager, int debounceDelayMs = 500)
        {
            _packageManager = packageManager ?? throw new ArgumentNullException(nameof(packageManager));
            _debounceDelayMs = debounceDelayMs;
            _debounceTimer = new Timer(ProcessPendingChanges, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Starts monitoring the specified VAM root folder for package changes
        /// </summary>
        public void StartMonitoring(string vamRootFolder)
        {
            if (string.IsNullOrEmpty(vamRootFolder) || !Directory.Exists(vamRootFolder))
                return;

            // Stop any existing monitoring first
            StopMonitoring();
            
            lock (_processLock)
            {
                _rootFolder = vamRootFolder;

                // Initialize file states from current packages
                InitializeFileStates();

                // Monitor AddonPackages, AllPackages, and ArchivedPackages folders
                var foldersToWatch = new[]
                {
                    Path.Combine(vamRootFolder, "AddonPackages"),
                    Path.Combine(vamRootFolder, "AllPackages"),
                    Path.Combine(vamRootFolder, "ArchivedPackages")
                };

                foreach (var folder in foldersToWatch)
                {
                    if (Directory.Exists(folder))
                    {
                        try
                        {
                            var watcher = new FileSystemWatcher(folder)
                            {
                                Filter = "*.var",
                                IncludeSubdirectories = true,
                                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                                // Increase buffer size to reduce chance of overflow
                                InternalBufferSize = 65536,
                                EnableRaisingEvents = true
                            };

                            watcher.Created += OnFileCreated;
                            watcher.Deleted += OnFileDeleted;
                            watcher.Changed += OnFileChanged;
                            watcher.Renamed += OnFileRenamed;
                            watcher.Error += OnWatcherError;

                            _watchers.Add(watcher);
                        }
                        catch (Exception)
                        {
                            // Failed to create watcher for this folder, continue with others
                        }
                    }
                }
            } // end lock
        }

        /// <summary>
        /// Stops monitoring for package changes
        /// </summary>
        public void StopMonitoring()
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch { }
            }
            _watchers.Clear();
            
            // Clear pending changes
            while (_pendingChanges.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Manually triggers an incremental refresh by comparing current file system state
        /// </summary>
        public async Task<IncrementalRefreshResult> RefreshIncrementallyAsync()
        {
            if (string.IsNullOrEmpty(_rootFolder))
                return new IncrementalRefreshResult();

            var result = new IncrementalRefreshResult();
            var currentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Scan current files
            var foldersToScan = new[]
            {
                Path.Combine(_rootFolder, "AddonPackages"),
                Path.Combine(_rootFolder, "AllPackages"),
                Path.Combine(_rootFolder, "ArchivedPackages")
            };

            foreach (var folder in foldersToScan)
            {
                if (Directory.Exists(folder))
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(folder, "*.var", SearchOption.AllDirectories))
                        {
                            // Skip temp files created during optimization
                            var filename = Path.GetFileName(file);
                            if (filename.StartsWith("~temp_", StringComparison.OrdinalIgnoreCase))
                                continue;
                            
                            currentFiles.Add(file);
                        }
                    }
                    catch { }
                }
            }

            // Find new files
            foreach (var file in currentFiles)
            {
                if (!_fileStates.ContainsKey(file))
                {
                    result.AddedFiles.Add(file);
                }
                else
                {
                    // Check if file was modified
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var currentState = _fileStates[file];
                        
                        if (fileInfo.Length != currentState.Size || 
                            fileInfo.LastWriteTimeUtc.Ticks != currentState.LastWriteTicks)
                        {
                            result.ModifiedFiles.Add(file);
                        }
                    }
                    catch { }
                }
            }

            // Find deleted files
            // CRITICAL: Skip external packages - they are in external destinations not scanned here
            foreach (var trackedFile in _fileStates.Keys.ToList())
            {
                if (!currentFiles.Contains(trackedFile))
                {
                    // Check if this is an external package by looking at its metadata
                    // External packages have paths outside the standard VAM folders
                    bool isExternal = false;
                    if (_packageManager?.PackageMetadata != null)
                    {
                        foreach (var kvp in _packageManager.PackageMetadata)
                        {
                            if (kvp.Value.FilePath?.Equals(trackedFile, StringComparison.OrdinalIgnoreCase) == true)
                            {
                                isExternal = kvp.Value.IsExternal;
                                break;
                            }
                        }
                    }
                    
                    // Only mark as removed if it's NOT an external package
                    if (!isExternal)
                    {
                        result.RemovedFiles.Add(trackedFile);
                    }
                }
            }

            // If too many changes, recommend full refresh
            int totalChanges = result.AddedFiles.Count + result.ModifiedFiles.Count + result.RemovedFiles.Count;
            if (totalChanges > 100)
            {
                result.RecommendFullRefresh = true;
                FullRefreshRecommended?.Invoke(this, EventArgs.Empty);
                // Don't process changes, but reinitialize file states after full refresh
                return result;
            }

            // Process changes
            if (totalChanges > 0)
            {
                await ProcessChangesAsync(result);
                
                // Reinitialize file states after processing to stay in sync
                lock (_processLock)
                {
                    InitializeFileStates();
                }
            }

            return result;
        }

        private void InitializeFileStates()
        {
            _fileStates.Clear();
            
            if (_packageManager?.PackageMetadata == null)
                return;

            foreach (var metadata in _packageManager.PackageMetadata.Values)
            {
                if (!string.IsNullOrEmpty(metadata.FilePath) && File.Exists(metadata.FilePath))
                {
                    try
                    {
                        var fileInfo = new FileInfo(metadata.FilePath);
                        _fileStates[metadata.FilePath] = new FileState
                        {
                            Path = metadata.FilePath,
                            Size = fileInfo.Length,
                            LastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks
                        };
                    }
                    catch { }
                }
            }
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            if (!e.FullPath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                return;
            
            // Skip temp files created during optimization
            var filename = Path.GetFileName(e.FullPath);
            if (filename.StartsWith("~temp_", StringComparison.OrdinalIgnoreCase))
                return;

            _pendingChanges.Enqueue(new FileChangeEvent
            {
                ChangeType = WatcherChangeTypes.Created,
                FullPath = e.FullPath,
                Timestamp = DateTime.UtcNow
            });
            
            RestartDebounceTimer();
        }

        private void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            if (!e.FullPath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                return;
            
            // Skip temp files created during optimization
            var filename = Path.GetFileName(e.FullPath);
            if (filename.StartsWith("~temp_", StringComparison.OrdinalIgnoreCase))
                return;

            _pendingChanges.Enqueue(new FileChangeEvent
            {
                ChangeType = WatcherChangeTypes.Deleted,
                FullPath = e.FullPath,
                Timestamp = DateTime.UtcNow
            });
            
            RestartDebounceTimer();
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!e.FullPath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                return;
            
            // Skip temp files created during optimization
            var filename = Path.GetFileName(e.FullPath);
            if (filename.StartsWith("~temp_", StringComparison.OrdinalIgnoreCase))
                return;

            _pendingChanges.Enqueue(new FileChangeEvent
            {
                ChangeType = WatcherChangeTypes.Changed,
                FullPath = e.FullPath,
                Timestamp = DateTime.UtcNow
            });
            
            RestartDebounceTimer();
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            // Treat rename as delete old + create new
            // Skip temp files created during optimization
            var oldFilename = Path.GetFileName(e.OldFullPath);
            var newFilename = Path.GetFileName(e.FullPath);
            
            if (e.OldFullPath.EndsWith(".var", StringComparison.OrdinalIgnoreCase) &&
                !oldFilename.StartsWith("~temp_", StringComparison.OrdinalIgnoreCase))
            {
                _pendingChanges.Enqueue(new FileChangeEvent
                {
                    ChangeType = WatcherChangeTypes.Deleted,
                    FullPath = e.OldFullPath,
                    Timestamp = DateTime.UtcNow
                });
            }

            if (e.FullPath.EndsWith(".var", StringComparison.OrdinalIgnoreCase) &&
                !newFilename.StartsWith("~temp_", StringComparison.OrdinalIgnoreCase))
            {
                _pendingChanges.Enqueue(new FileChangeEvent
                {
                    ChangeType = WatcherChangeTypes.Created,
                    FullPath = e.FullPath,
                    Timestamp = DateTime.UtcNow
                });
            }
            
            RestartDebounceTimer();
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // Watcher encountered an error, recommend full refresh
            FullRefreshRecommended?.Invoke(this, EventArgs.Empty);
        }

        private void RestartDebounceTimer()
        {
            _debounceTimer.Change(_debounceDelayMs, Timeout.Infinite);
        }

        private void ProcessPendingChanges(object state)
        {
            if (_disposed)
                return;

            lock (_processLock)
            {
                var result = new IncrementalRefreshResult();
                
                // Track final state per path (handles multiple events for same file)
                var pathStates = new Dictionary<string, WatcherChangeTypes>(StringComparer.OrdinalIgnoreCase);

                // Collect all pending changes, keeping only the final state per path
                while (_pendingChanges.TryDequeue(out var change))
                {
                    if (pathStates.TryGetValue(change.FullPath, out var existingState))
                    {
                        // Merge events: Delete + Create = Modified, Created + Changed = Created
                        if (existingState == WatcherChangeTypes.Deleted && change.ChangeType == WatcherChangeTypes.Created)
                        {
                            pathStates[change.FullPath] = WatcherChangeTypes.Changed; // Treat as modified
                        }
                        else if (existingState == WatcherChangeTypes.Created && change.ChangeType == WatcherChangeTypes.Changed)
                        {
                            // Keep as Created (new file that was also modified)
                        }
                        else
                        {
                            // Use the latest event
                            pathStates[change.FullPath] = change.ChangeType;
                        }
                    }
                    else
                    {
                        pathStates[change.FullPath] = change.ChangeType;
                    }
                }

                // Build result from final states
                foreach (var kvp in pathStates)
                {
                    switch (kvp.Value)
                    {
                        case WatcherChangeTypes.Created:
                            if (File.Exists(kvp.Key))
                                result.AddedFiles.Add(kvp.Key);
                            break;
                        case WatcherChangeTypes.Deleted:
                            result.RemovedFiles.Add(kvp.Key);
                            break;
                        case WatcherChangeTypes.Changed:
                            if (File.Exists(kvp.Key))
                                result.ModifiedFiles.Add(kvp.Key);
                            break;
                    }
                }

                // If too many changes, recommend full refresh
                int totalChanges = result.AddedFiles.Count + result.ModifiedFiles.Count + result.RemovedFiles.Count;
                if (totalChanges > 50)
                {
                    result.RecommendFullRefresh = true;
                    FullRefreshRecommended?.Invoke(this, EventArgs.Empty);
                    return;
                }

                if (totalChanges > 0)
                {
                    // Process changes asynchronously
                    _ = ProcessChangesAsync(result).ContinueWith(t =>
                    {
                        if (!t.IsFaulted)
                        {
                            PackagesUpdated?.Invoke(this, result);
                        }
                    });
                }
            }
        }

        private async Task ProcessChangesAsync(IncrementalRefreshResult result)
        {
            await Task.Run(() =>
            {
                // Process removed files
                foreach (var removedFile in result.RemovedFiles)
                {
                    RemovePackageFromMetadata(removedFile);
                    _fileStates.TryRemove(removedFile, out _);
                }

                // Process added and modified files
                var filesToProcess = result.AddedFiles.Concat(result.ModifiedFiles).ToList();
                foreach (var file in filesToProcess)
                {
                    try
                    {
                        AddOrUpdatePackageMetadata(file);
                        
                        // Update file state
                        var fileInfo = new FileInfo(file);
                        _fileStates[file] = new FileState
                        {
                            Path = file,
                            Size = fileInfo.Length,
                            LastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks
                        };
                    }
                    catch (Exception)
                    {
                        // Failed to process this file, skip it
                    }
                }
            });
        }

        private void RemovePackageFromMetadata(string filePath)
        {
            if (_packageManager?.PackageMetadata == null)
                return;

            var filename = Path.GetFileName(filePath);
            
            // Find and remove the package by matching file path or filename
            var keysToRemove = _packageManager.PackageMetadata
                .Where(kvp => 
                    kvp.Value.FilePath?.Equals(filePath, StringComparison.OrdinalIgnoreCase) == true ||
                    kvp.Value.Filename?.Equals(filename, StringComparison.OrdinalIgnoreCase) == true)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _packageManager.PackageMetadata.Remove(key);
            }
        }

        private void AddOrUpdatePackageMetadata(string filePath)
        {
            if (_packageManager?.PackageMetadata == null || !File.Exists(filePath))
                return;

            try
            {
                // Determine status based on folder location
                var status = DeterminePackageStatus(filePath);
                
                // Parse the package metadata
                var metadata = _packageManager.ParseVarMetadataComplete(filePath);
                if (metadata == null)
                    return;

                metadata.Status = status;
                metadata.FilePath = filePath;

                // Generate the metadata key
                var metadataKey = GenerateMetadataKey(metadata, status);
                
                // Add or update in the dictionary
                _packageManager.PackageMetadata[metadataKey] = metadata;
            }
            catch (Exception)
            {
                // Failed to parse package, skip it
            }
        }

        private string DeterminePackageStatus(string filePath)
        {
            if (string.IsNullOrEmpty(_rootFolder))
                return "Unknown";

            var normalizedPath = filePath.Replace('\\', '/').ToLowerInvariant();
            var normalizedRoot = _rootFolder.Replace('\\', '/').ToLowerInvariant();

            if (normalizedPath.Contains("/addonpackages/"))
                return "Loaded";
            if (normalizedPath.Contains("/archivedpackages/"))
                return "Archived";
            if (normalizedPath.Contains("/allpackages/"))
                return "Available";

            return "Unknown";
        }

        private string GenerateMetadataKey(VarMetadata metadata, string status)
        {
            var baseKey = $"{metadata.CreatorName}.{metadata.PackageName}.{metadata.Version}";
            
            if (status == "Archived")
                return $"{baseKey}#archived";
            if (status == "Available")
                return $"{baseKey}#available";
            
            return baseKey;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            StopMonitoring();
            _debounceTimer?.Dispose();
        }

        private class FileChangeEvent
        {
            public WatcherChangeTypes ChangeType { get; set; }
            public string FullPath { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private class FileState
        {
            public string Path { get; set; }
            public long Size { get; set; }
            public long LastWriteTicks { get; set; }
        }
    }

    /// <summary>
    /// Result of an incremental package refresh operation
    /// </summary>
    public class IncrementalRefreshResult
    {
        public List<string> AddedFiles { get; } = new();
        public List<string> ModifiedFiles { get; } = new();
        public List<string> RemovedFiles { get; } = new();
        public bool RecommendFullRefresh { get; set; }
        
        public int TotalChanges => AddedFiles.Count + ModifiedFiles.Count + RemovedFiles.Count;
        public bool HasChanges => TotalChanges > 0;
    }
}
