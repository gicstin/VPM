using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPM.Models;
using VPM.Services;

namespace VPM.Services
{
    /// <summary>
    /// Handles scanning and live tracking of external destination folders for VAR packages.
    /// Provides FileSystemWatcher support for real-time updates.
    /// </summary>
    public class ExternalDestinationScanner : IDisposable
    {
        private readonly ISettingsManager _settingsManager;
        private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, List<string>> _destinationPackages = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _scanLock = new();
        private const int WatcherBufferSize = 64 * 1024;
        private bool _disposed;

        /// <summary>
        /// Event fired when packages in an external destination change
        /// </summary>
        public event EventHandler<ExternalDestinationChangedEventArgs> DestinationChanged;

        /// <summary>
        /// Event fired when a package is added to an external destination
        /// </summary>
        public event EventHandler<ExternalPackageEventArgs> PackageAdded;

        /// <summary>
        /// Event fired when a package is removed from an external destination
        /// </summary>
        public event EventHandler<ExternalPackageEventArgs> PackageRemoved;

        public ExternalDestinationScanner(ISettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        }

        /// <summary>
        /// Gets all configured external destinations from settings
        /// </summary>
        public List<MoveToDestination> GetDestinations()
        {
            return _settingsManager?.Settings?.MoveToDestinations?.ToList() ?? new List<MoveToDestination>();
        }

        /// <summary>
        /// Gets destinations that should be shown in the main table
        /// </summary>
        public List<MoveToDestination> GetVisibleDestinations()
        {
            return GetDestinations().Where(d => d.ShowInMainTable && d.IsValid()).ToList();
        }

        /// <summary>
        /// Checks if a destination path is nested inside another configured destination.
        /// Returns true if this destination is a subdirectory of another configured destination.
        /// </summary>
        public bool IsNestedInConfiguredPath(MoveToDestination destination, List<MoveToDestination> allDestinations)
        {
            if (destination == null || !destination.IsValid())
                return false;

            var destPath = Path.GetFullPath(destination.Path).TrimEnd(Path.DirectorySeparatorChar);

            foreach (var other in allDestinations)
            {
                if (other == null || !other.IsValid() || other.Name.Equals(destination.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var otherPath = Path.GetFullPath(other.Path).TrimEnd(Path.DirectorySeparatorChar);
                
                // Check if destPath is inside otherPath
                if (destPath.StartsWith(otherPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Scans all configured external destinations for VAR files
        /// </summary>
        /// <returns>Dictionary mapping destination name to list of VAR file paths</returns>
        public async Task<Dictionary<string, List<string>>> ScanAllDestinationsAsync()
        {
            var results = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var destinations = GetDestinations();

            var scanTasks = destinations
                .Where(d => d.IsValid() && d.PathExists())
                .Select(async dest =>
                {
                    var files = await ScanDestinationAsync(dest);
                    lock (results)
                    {
                        results[dest.Name] = files;
                    }
                });

            await Task.WhenAll(scanTasks);

            // Update internal cache
            foreach (var kvp in results)
            {
                _destinationPackages[kvp.Key] = kvp.Value;
            }

            return results;
        }

        /// <summary>
        /// Scans a single destination folder for VAR files
        /// </summary>
        public async Task<List<string>> ScanDestinationAsync(MoveToDestination destination)
        {
            if (destination == null || !destination.IsValid() || !destination.PathExists())
                return new List<string>();

            return await Task.Run(() =>
            {
                try
                {
                    return SafeFileEnumerator.EnumerateFiles(destination.Path, "*.var", recursive: true).ToList();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error scanning destination {destination.Name}: {ex.Message}");
                    return new List<string>();
                }
            });
        }

        /// <summary>
        /// Gets the destination info for a given file path
        /// </summary>
        public MoveToDestination GetDestinationForPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return null;

            var normalizedPath = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar);
            
            foreach (var dest in GetDestinations())
            {
                if (!dest.IsValid() || !dest.PathExists())
                    continue;

                var destPath = Path.GetFullPath(dest.Path).TrimEnd(Path.DirectorySeparatorChar);
                if (normalizedPath.StartsWith(destPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.Equals(destPath, StringComparison.OrdinalIgnoreCase))
                {
                    return dest;
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if a file path is within any external destination
        /// </summary>
        public bool IsExternalPath(string filePath)
        {
            return GetDestinationForPath(filePath) != null;
        }

        /// <summary>
        /// Starts FileSystemWatchers for all configured destinations
        /// </summary>
        public void StartWatching()
        {
            StopWatching(); // Clear any existing watchers

            foreach (var dest in GetDestinations())
            {
                if (!dest.IsValid() || !dest.PathExists())
                    continue;

                try
                {
                    var watcher = new FileSystemWatcher(dest.Path)
                    {
                        Filter = "*.var",
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                        InternalBufferSize = WatcherBufferSize
                    };

                    watcher.Created += (s, e) => OnFileCreated(dest, e);
                    watcher.Deleted += (s, e) => OnFileDeleted(dest, e);
                    watcher.Renamed += (s, e) => OnFileRenamed(dest, e);
                    watcher.Error += (s, e) => OnWatcherError(dest, e);

                    watcher.EnableRaisingEvents = true;
                    _watchers[dest.Name] = watcher;

                    System.Diagnostics.Debug.WriteLine($"Started watching external destination: {dest.Name} at {dest.Path}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error starting watcher for {dest.Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Stops all FileSystemWatchers
        /// </summary>
        public void StopWatching()
        {
            foreach (var watcher in _watchers.Values)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch { }
            }
            _watchers.Clear();
        }

        /// <summary>
        /// Refreshes watchers when destinations configuration changes
        /// </summary>
        public void RefreshWatchers()
        {
            StartWatching();
        }

        private void OnFileCreated(MoveToDestination destination, FileSystemEventArgs e)
        {
            if (!e.FullPath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                return;

            System.Diagnostics.Debug.WriteLine($"External package added: {e.FullPath} in {destination.Name}");

            // Update cache
            if (_destinationPackages.TryGetValue(destination.Name, out var packages))
            {
                lock (packages)
                {
                    if (!packages.Contains(e.FullPath, StringComparer.OrdinalIgnoreCase))
                        packages.Add(e.FullPath);
                }
            }

            PackageAdded?.Invoke(this, new ExternalPackageEventArgs(destination, e.FullPath));
            DestinationChanged?.Invoke(this, new ExternalDestinationChangedEventArgs(destination, ExternalChangeType.Added, e.FullPath));
        }

        private void OnFileDeleted(MoveToDestination destination, FileSystemEventArgs e)
        {
            if (!e.FullPath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                return;

            System.Diagnostics.Debug.WriteLine($"External package removed: {e.FullPath} from {destination.Name}");

            // Update cache
            if (_destinationPackages.TryGetValue(destination.Name, out var packages))
            {
                lock (packages)
                {
                    packages.RemoveAll(p => p.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase));
                }
            }

            PackageRemoved?.Invoke(this, new ExternalPackageEventArgs(destination, e.FullPath));
            DestinationChanged?.Invoke(this, new ExternalDestinationChangedEventArgs(destination, ExternalChangeType.Removed, e.FullPath));
        }

        private void OnFileRenamed(MoveToDestination destination, RenamedEventArgs e)
        {
            // Handle as delete + create
            if (e.OldFullPath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
            {
                OnFileDeleted(destination, new FileSystemEventArgs(WatcherChangeTypes.Deleted, Path.GetDirectoryName(e.OldFullPath), Path.GetFileName(e.OldFullPath)));
            }
            if (e.FullPath.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
            {
                OnFileCreated(destination, e);
            }
        }

        private void OnWatcherError(MoveToDestination destination, ErrorEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"Watcher error for {destination.Name}: {e.GetException()?.Message}");
            
            // Try to restart the watcher
            Task.Delay(1000).ContinueWith(_ =>
            {
                try
                {
                    if (_watchers.TryRemove(destination.Name, out var oldWatcher))
                    {
                        oldWatcher.Dispose();
                    }

                    if (destination.PathExists())
                    {
                        var watcher = new FileSystemWatcher(destination.Path)
                        {
                            Filter = "*.var",
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                            InternalBufferSize = WatcherBufferSize
                        };

                        watcher.Created += (s, ev) => OnFileCreated(destination, ev);
                        watcher.Deleted += (s, ev) => OnFileDeleted(destination, ev);
                        watcher.Renamed += (s, ev) => OnFileRenamed(destination, ev);
                        watcher.Error += (s, ev) => OnWatcherError(destination, ev);

                        watcher.EnableRaisingEvents = true;
                        _watchers[destination.Name] = watcher;
                    }
                }
                catch { }
            });
        }

        /// <summary>
        /// Gets cached packages for a destination
        /// </summary>
        public List<string> GetCachedPackages(string destinationName)
        {
            if (_destinationPackages.TryGetValue(destinationName, out var packages))
            {
                lock (packages)
                {
                    return packages.ToList();
                }
            }
            return new List<string>();
        }

        /// <summary>
        /// Gets all cached external packages across all destinations
        /// </summary>
        public Dictionary<string, List<string>> GetAllCachedPackages()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _destinationPackages)
            {
                lock (kvp.Value)
                {
                    result[kvp.Key] = kvp.Value.ToList();
                }
            }
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopWatching();
            _destinationPackages.Clear();
        }
    }

    /// <summary>
    /// Event args for external destination changes
    /// </summary>
    public class ExternalDestinationChangedEventArgs : EventArgs
    {
        public MoveToDestination Destination { get; }
        public ExternalChangeType ChangeType { get; }
        public string FilePath { get; }

        public ExternalDestinationChangedEventArgs(MoveToDestination destination, ExternalChangeType changeType, string filePath)
        {
            Destination = destination;
            ChangeType = changeType;
            FilePath = filePath;
        }
    }

    /// <summary>
    /// Event args for external package events
    /// </summary>
    public class ExternalPackageEventArgs : EventArgs
    {
        public MoveToDestination Destination { get; }
        public string FilePath { get; }
        public string PackageName => Path.GetFileNameWithoutExtension(FilePath);

        public ExternalPackageEventArgs(MoveToDestination destination, string filePath)
        {
            Destination = destination;
            FilePath = filePath;
        }
    }

    /// <summary>
    /// Type of change in external destination
    /// </summary>
    public enum ExternalChangeType
    {
        Added,
        Removed,
        Modified
    }
}
