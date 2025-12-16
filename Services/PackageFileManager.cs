using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Threading;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Handles robust file operations for moving VAR packages between loaded/unloaded folders
    /// with comprehensive error handling, performance optimizations, and progress reporting
    /// </summary>
    public class PackageFileManager : IDisposable
    {
        // Core paths
        private readonly string _rootFolder;
        private readonly string _addonPackagesFolder;
        private readonly string _allPackagesFolder;
        private readonly string _archivedPackagesFolder;

        // Package management
        private readonly ConcurrentDictionary<string, string> _packageStatusIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _packageLocks = new(StringComparer.OrdinalIgnoreCase);
        private bool _statusIndexBuilt;
        private readonly object _statusIndexLock = new object();

        // Operation tracking
        private readonly Dictionary<string, DateTime> _operationHistory;
        private readonly object _historyLock = new object();
        private int _totalOperations;
        private int _successfulOperations;
        private DateTime _lastOperationTime = DateTime.MinValue;

        // Dependencies and utilities
        private readonly ImageManager _imageManager;
        private readonly Regex _varPattern;
        private readonly SemaphoreSlim _fileLock;
        private readonly DispatcherTimer _operationTimer;

        private readonly ReaderWriterLockSlim _packageIndexLock = new ReaderWriterLockSlim();
        private Dictionary<string, PackageFileIndexEntry> _packageIndex = new Dictionary<string, PackageFileIndexEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _packageExactIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private volatile bool _packageIndexDirty = true;
        private readonly Dictionary<string, DateTime> _packageIndexDirectorySignatures = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (int fileCount, long lastWriteTicks)> _statusIndexDirectorySignatures = new Dictionary<string, (int, long)>(StringComparer.OrdinalIgnoreCase);


        // Disposal tracking
        private bool _disposed;
        
        // Events for UI feedback
        public event EventHandler<PackageOperationEventArgs> OperationStarted;
        public event EventHandler<PackageOperationEventArgs> OperationCompleted;
        public event EventHandler<PackageOperationProgressEventArgs> OperationProgress;

        private async Task<string> GetPackageStatusInternalAsync(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return "Unknown";

            // Ensure index is built
            if (!_statusIndexBuilt)
            {
                await Task.Run(() =>
                {
                    lock (_statusIndexLock)
                    {
                        if (!_statusIndexBuilt)
                        {
                            BuildPackageStatusIndex();
                        }
                    }
                });
            }
            
            return _packageStatusIndex.GetOrAdd(packageName, _ => "Not Found");
        }

        public PackageFileManager(string vamRootFolder, ImageManager imageManager)
        {
            _imageManager = imageManager ?? throw new ArgumentNullException(nameof(imageManager));
            _rootFolder = vamRootFolder ?? throw new ArgumentNullException(nameof(vamRootFolder));
            _addonPackagesFolder = Path.Combine(_rootFolder, "AddonPackages");
            _allPackagesFolder = Path.Combine(_rootFolder, "AllPackages");
            _archivedPackagesFolder = Path.Combine(_rootFolder, "ArchivedPackages");
            
            // Enhanced pattern to parse VAR filenames with better error handling
            _varPattern = new Regex(@"^([^.]+)\.(.+?)\.(\d+)\.var$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            
            // Semaphore to prevent concurrent file operations
            _fileLock = new SemaphoreSlim(1, 1);
            
            // Initialize operation tracking
            _operationHistory = new Dictionary<string, DateTime>();
            
            // Timer for debounced operations and cleanup
            _operationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(5)
            };
            _operationTimer.Tick += CleanupOperationHistory;
            _operationTimer.Start();
            
            // Ensure directories exist
            EnsureDirectoriesExist();
            
            // Build package status index eagerly in background to avoid lazy initialization delays
            // This prevents lock contention on first access
            // FIXED: Wrap in try-catch to prevent unobserved exceptions
            _ = Task.Run(() => { try { BuildPackageStatusIndex(); } catch { } });
        }

        private void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(_addonPackagesFolder);
            Directory.CreateDirectory(_allPackagesFolder);
        }

        /// <summary>
        /// Parses a VAR filename to extract creator, package name, and version
        /// </summary>
        public (string creator, string packageName, int version) ParseVarFilename(string filename)
        {
            var match = _varPattern.Match(filename);
            if (match.Success)
            {
                if (int.TryParse(match.Groups[3].Value, out int version))
                {
                    return (match.Groups[1].Value, match.Groups[2].Value, version);
                }
            }

            // Fallback parsing for edge cases
            if (filename.ToLower().EndsWith(".var"))
            {
                var parts = filename[..^4].Split('.');
                if (parts.Length >= 3 && int.TryParse(parts[^1], out int version))
                {
                    return (parts[0], string.Join(".", parts[1..^1]), version);
                }
            }

            return (null, null, 0);
        }

        private static int ParseVersionTokenToSortableInt(string versionToken)
        {
            if (string.IsNullOrWhiteSpace(versionToken))
            {
                return 0;
            }

            if (int.TryParse(versionToken, out var exact))
            {
                return exact;
            }

            var normalized = versionToken.Replace('_', '.');
            var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var numericParts = new List<int>(parts.Length);
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var n))
                {
                    return 0;
                }
                numericParts.Add(n);
            }

            if (numericParts.Count == 0)
            {
                return 0;
            }

            long sortable = 0;
            for (int i = 0; i < numericParts.Count && i < 3; i++)
            {
                sortable = sortable * 1000 + numericParts[i];
            }

            return sortable > int.MaxValue ? int.MaxValue : (int)sortable;
        }

        /// <summary>
        /// Finds the latest version of a package in the specified directories
        /// </summary>
        public string FindLatestPackageVersion(string packageBaseName, params string[] searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(packageBaseName))
            {
                return string.Empty;
            }

            EnsurePackageIndex(searchDirectories);

            _packageIndexLock.EnterReadLock();
            try
            {
                if (_packageIndex.TryGetValue(packageBaseName, out var entry))
                {
                    return entry.LatestPath ?? string.Empty;
                }

                // Attempt to find by normalized key (creator.package)
                var normalized = NormalizePackageBase(packageBaseName);
                if (!string.IsNullOrEmpty(normalized) && _packageIndex.TryGetValue(normalized, out entry))
                {
                    return entry.LatestPath ?? string.Empty;
                }

                // Partial lookup as a last resort (case-insensitive contains)
                var partialMatch = _packageIndex
                    .Where(kvp => kvp.Key.Contains(packageBaseName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(kvp => kvp.Value.LatestVersion)
                    .FirstOrDefault();

                return partialMatch.Value?.LatestPath ?? string.Empty;
            }
            finally
            {
                _packageIndexLock.ExitReadLock();
            }
        }

        private void EnsurePackageIndex(string[] searchDirectories)
        {
            if (searchDirectories == null || searchDirectories.Length == 0)
            {
                searchDirectories = new[] { _addonPackagesFolder, _allPackagesFolder };
            }

            var directoryList = searchDirectories
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (!_packageIndexDirty)
            {
                if (!directoryList.Any())
                {
                    return;
                }

                if (!HaveDirectoriesChanged(directoryList))
                {
                    return;
                }

                _packageIndexDirty = true;
            }

            _packageIndexLock.EnterUpgradeableReadLock();
            try
            {
                if (!_packageIndexDirty)
                {
                    return;
                }

                _packageIndexLock.EnterWriteLock();
                try
                {
                    BuildPackageIndex(directoryList);
                    _packageIndexDirty = false;
                }
                finally
                {
                    _packageIndexLock.ExitWriteLock();
                }
            }
            finally
            {
                _packageIndexLock.ExitUpgradeableReadLock();
            }
        }

        private void BuildPackageIndex(IEnumerable<string> searchDirectories)
        {
            var newIndex = new Dictionary<string, PackageFileIndexEntry>(StringComparer.OrdinalIgnoreCase);
            var newExactIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var newSignatures = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            foreach (var directory in searchDirectories)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                DirectoryInfo dirInfo;
                try
                {
                    dirInfo = new DirectoryInfo(directory);
                }
                catch (Exception ex)
                {
                    throw new IOException($"Failed to access directory '{directory}': {ex.Message}", ex);
                }

                newSignatures[directory] = dirInfo.LastWriteTimeUtc;

                IEnumerable<string> files;
                try
                {
                    files = SafeFileEnumerator.EnumerateFiles(directory, "*.var", recursive: true);
                }
                catch (Exception ex)
                {
                    throw new IOException($"Failed to enumerate VAR files in '{directory}': {ex.Message}", ex);
                }

                foreach (var filePath in files)
                {
                    var fileName = Path.GetFileName(filePath);
                    var (creator, package, version) = ParseVarFilename(fileName);
                    if (creator == null || package == null)
                    {
                        continue;
                    }

                    var packageBase = $"{creator}.{package}";
                    if (!newIndex.TryGetValue(packageBase, out var entry))
                    {
                        entry = new PackageFileIndexEntry(packageBase);
                        newIndex[packageBase] = entry;
                    }

                    entry.Update(version, filePath, directory);

                    var fileStem = Path.GetFileNameWithoutExtension(fileName);
                    if (!string.IsNullOrEmpty(fileStem))
                    {
                        newExactIndex[fileStem] = filePath;
                    }

                    newExactIndex[fileName] = filePath; // include extension variant for completeness
                }
            }

            foreach (var kvp in newIndex)
            {
                if (!string.IsNullOrEmpty(kvp.Value.LatestPath))
                {
                    newExactIndex[kvp.Key] = kvp.Value.LatestPath;
                }
            }

            _packageIndex = newIndex;
            _packageExactIndex = newExactIndex;

            _packageIndexDirectorySignatures.Clear();
            foreach (var kvp in newSignatures)
            {
                _packageIndexDirectorySignatures[kvp.Key] = kvp.Value;
            }
        }

        private string NormalizePackageBase(string packageBaseName)
        {
            if (string.IsNullOrWhiteSpace(packageBaseName))
            {
                return string.Empty;
            }

            var (creator, package, _) = ParseVarFilename(packageBaseName.EndsWith(".var", StringComparison.OrdinalIgnoreCase)
                ? packageBaseName
                : packageBaseName + ".var");

            if (creator != null && package != null)
            {
                return $"{creator}.{package}";
            }

            var parts = packageBaseName.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return $"{parts[0]}.{string.Join('.', parts.Skip(1))}";
            }

            return packageBaseName;
        }

        /// <summary>
        /// Resolves a dependency name (which may end with .latest or .min[NUMBER]) to an actual file path.
        /// Handles .latest, .min[NUMBER], and exact version references.
        /// </summary>
        public string ResolveDependencyToFilePath(string dependencyName)
        {
            var depInfo = DependencyVersionInfo.Parse(dependencyName);
            var baseName = depInfo.BaseName;

            // Search in all available locations
            var searchDirectories = new[] { _addonPackagesFolder, _allPackagesFolder };
            
            switch (depInfo.VersionType)
            {
                case DependencyVersionType.Latest:
                    // Find the latest version
                    var latestFile = FindLatestPackageVersion(baseName, searchDirectories);
                    if (!string.IsNullOrEmpty(latestFile))
                        return latestFile;
                    break;
                    
                case DependencyVersionType.Minimum:
                    // Find a version that meets the minimum requirement
                    var minFile = FindMinimumVersionPackage(baseName, depInfo.VersionNumber ?? 0, searchDirectories);
                    if (!string.IsNullOrEmpty(minFile))
                        return minFile;
                    // Fallback to latest if no version meets minimum
                    var fallbackFile = FindLatestPackageVersion(baseName, searchDirectories);
                    if (!string.IsNullOrEmpty(fallbackFile))
                        return fallbackFile;
                    break;
                    
                case DependencyVersionType.Exact:
                    // Try exact version first
                    var exactFile = FindExactPackagePath(dependencyName, searchDirectories);
                    if (!string.IsNullOrEmpty(exactFile))
                        return exactFile;
                    // Fallback to latest version
                    var exactFallback = FindLatestPackageVersion(baseName, searchDirectories);
                    if (!string.IsNullOrEmpty(exactFallback))
                        return exactFallback;
                    break;
            }

            // Final fallback: attempt direct lookup for exact filename without versioning
            return FindExactPackagePath(baseName, searchDirectories);
        }
        
        /// <summary>
        /// Finds a package version that meets or exceeds the minimum version requirement.
        /// Returns the smallest version >= minVersion, or the latest if none meet the requirement.
        /// </summary>
        private string FindMinimumVersionPackage(string packageBaseName, int minVersion, params string[] searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(packageBaseName))
                return string.Empty;

            EnsurePackageIndex(searchDirectories);

            _packageIndexLock.EnterReadLock();
            try
            {
                if (_packageIndex.TryGetValue(packageBaseName, out var entry))
                {
                    // Check if we have version info in the entry
                    // Use GetVersions() to avoid allocating Dictionary for single-version entries
                    var matchingVersion = entry.GetVersions()
                        .Where(kvp => kvp.Key >= minVersion)
                        .OrderBy(kvp => kvp.Key)
                        .FirstOrDefault();
                    
                    if (!string.IsNullOrEmpty(matchingVersion.Value))
                        return matchingVersion.Value;
                    
                    // Fallback to latest if no version meets minimum
                    return entry.LatestPath ?? string.Empty;
                }

                // Try normalized key
                var normalized = NormalizePackageBase(packageBaseName);
                if (!string.IsNullOrEmpty(normalized) && _packageIndex.TryGetValue(normalized, out entry))
                {
                    var matchingVersion = entry.GetVersions()
                        .Where(kvp => kvp.Key >= minVersion)
                        .OrderBy(kvp => kvp.Key)
                        .FirstOrDefault();
                    
                    if (!string.IsNullOrEmpty(matchingVersion.Value))
                        return matchingVersion.Value;
                    
                    return entry.LatestPath ?? string.Empty;
                }
            }
            finally
            {
                _packageIndexLock.ExitReadLock();
            }

            return string.Empty;
        }

        private string FindExactPackagePath(string packageBaseName, string[] searchDirectories)
        {
            EnsurePackageIndex(searchDirectories);

            _packageIndexLock.EnterReadLock();
            try
            {
                if (_packageExactIndex.TryGetValue(packageBaseName, out var exactPath))
                {
                    return exactPath;
                }

                var normalized = NormalizePackageBase(packageBaseName);
                if (!string.IsNullOrEmpty(normalized) && _packageExactIndex.TryGetValue(normalized, out exactPath))
                {
                    return exactPath;
                }
            }
            finally
            {
                _packageIndexLock.ExitReadLock();
            }

            return string.Empty;
        }

        private bool HaveDirectoriesChanged(IEnumerable<string> directories)
        {
            var directoryList = directories
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var directory in directoryList)
            {
                DateTime currentSignature;
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        if (_packageIndexDirectorySignatures.ContainsKey(directory))
                        {
                            return true;
                        }
                        continue;
                    }

                    currentSignature = Directory.GetLastWriteTimeUtc(directory);
                }
                catch
                {
                    return true;
                }

                if (!_packageIndexDirectorySignatures.TryGetValue(directory, out var knownSignature) || knownSignature != currentSignature)
                {
                    return true;
                }
            }

            foreach (var knownDirectory in _packageIndexDirectorySignatures.Keys)
            {
                if (!directoryList.Contains(knownDirectory, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Invalidates the package index cache, forcing a rebuild on next access
        /// Call this after moving/deleting packages to ensure the index is up-to-date
        /// </summary>
        public void InvalidatePackageIndex()
        {
            _packageIndexDirty = true;
        }

        /// <summary>
        /// Checks if a file can be safely moved (not locked, sufficient permissions, etc.)
        /// </summary>
        private async Task<(bool canMove, string error)> CanMoveFileAsync(string filePath, bool skipVarValidation = false)
        {
            try
            {
                // Get file info once for multiple checks (efficient)
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                {
                    return (false, "File does not exist");
                }

                // Validate VAR file integrity first (unless skipping)
                if (!skipVarValidation)
                {
                    try
                    {
                        // Open on background thread to avoid blocking UI
                        await Task.Run(() =>
                        {
                            using (var archive = SharpCompressHelper.OpenForRead(filePath))
                            {
                                // Just access Count property to validate ZIP structure (no allocation)
                                // This is orders of magnitude faster than ToList()
                                _ = archive.Entries.Count();
                            }
                        });
                    }
                    catch (System.IO.InvalidDataException ex)
                    {
                        return (false, $"Invalid or corrupt VAR file: {ex.Message}");
                    }
                    catch (Exception ex) when (ex is not System.IO.FileNotFoundException)
                    {
                        return (false, $"Error validating VAR file: {ex.Message}");
                    }
                }

                // Check if file is readable (less restrictive than exclusive lock)
                // Use Read access and allow other readers - only block if file is being written
                try
                {
                    using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        // File is readable - this is sufficient for move operation
                    }
                }
                catch (IOException)
                {
                    // File might be exclusively locked by another process
                    return (false, "File is currently locked or in use by another process");
                }

                // Check available disk space based on actual file size (not arbitrary 100MB)
                var drive = new DriveInfo(Path.GetPathRoot(filePath));
                var requiredSpace = fileInfo.Length + (10 * 1024 * 1024); // File size + 10MB buffer
                if (drive.AvailableFreeSpace < requiredSpace)
                {
                    var requiredMB = requiredSpace / (1024.0 * 1024.0);
                    var availableMB = drive.AvailableFreeSpace / (1024.0 * 1024.0);
                    return (false, $"Insufficient disk space. Required: {requiredMB:F1}MB, Available: {availableMB:F1}MB");
                }

                return (true, "");
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "Access denied - insufficient permissions");
            }
            catch (IOException ex)
            {
                return (false, $"File is locked or in use: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"Unexpected error: {ex.Message}");
            }
        }

        /// <summary>
        /// Safely moves a file with retry logic using File.Copy + File.Delete pattern.
        /// This is more robust than File.Move because:
        /// 1. File.Copy only needs read access to source (more forgiving)
        /// 2. Each operation can be retried independently
        /// 3. Partial failures are recoverable
        /// </summary>
        private async Task<(bool success, string error)> SafeMoveFileAsync(string sourcePath, string destinationPath, int maxRetries = 10, bool skipVarValidation = false)
        {
            static bool IsSameVolume(string a, string b)
            {
                try
                {
                    var rootA = Path.GetPathRoot(Path.GetFullPath(a));
                    var rootB = Path.GetPathRoot(Path.GetFullPath(b));
                    return string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            async Task ReleaseAppFileHandlesAsync(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                try
                {
                    if (_imageManager != null)
                    {
                        await _imageManager.CloseFileHandlesAsync(path);
                    }

                    FileAccessController.Instance.InvalidateFile(path);
                }
                catch
                {
                }
            }

            async Task<(bool moved, string error)> TryFastRenameMoveAsync(FileInfo sourceInfo, string src, string dst, int attempts)
            {
                string lastError = null;
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    try
                    {
                        await ReleaseAppFileHandlesAsync(src);
                        await ReleaseAppFileHandlesAsync(dst);

                        // File.Move is atomic and extremely fast when src/dst are on the same volume.
                        File.Move(src, dst);

                        // Ensure LastWriteTime is preserved (should be by rename, but enforce for safety).
                        try
                        {
                            File.SetLastWriteTime(dst, sourceInfo.LastWriteTime);
                        }
                        catch
                        {
                        }

                        return (true, "");
                    }
                    catch (IOException ex) when (attempt < attempts)
                    {
                        lastError = ex.Message;

                        // If this is transient locking, give the system a short chance to release handles.
                        await Task.Delay(40 * attempt);
                    }
                    catch (UnauthorizedAccessException ex) when (attempt < attempts)
                    {
                        lastError = ex.Message;
                        await Task.Delay(40 * attempt);
                    }
                    catch (Exception ex)
                    {
                        return (false, ex.Message);
                    }
                }

                return (false, lastError ?? "Rename move failed");
            }

            async Task<(bool success, string error)> CopyDeleteFallbackAsync(FileInfo sourceInfo, string src, string dst, int retries)
            {
                string lastError = null;

                // Step 1: Copy the file with retries
                for (int copyAttempt = 1; copyAttempt <= retries; copyAttempt++)
                {
                    try
                    {
                        await ReleaseAppFileHandlesAsync(src);
                        File.Copy(src, dst, overwrite: false);
                        break;
                    }
                    catch (IOException ex) when (copyAttempt < retries)
                    {
                        lastError = $"Copy attempt {copyAttempt} failed: {ex.Message}";
                        await ReleaseAppFileHandlesAsync(src);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        await Task.Delay(100 * copyAttempt);
                    }
                    catch (IOException ex)
                    {
                        return (false, $"Failed to copy file after {retries} attempts: {ex.Message}");
                    }
                }

                // Preserve LastWriteTime (and CreationTime best-effort)
                try
                {
                    File.SetLastWriteTime(dst, sourceInfo.LastWriteTime);
                    File.SetCreationTime(dst, sourceInfo.CreationTime);
                }
                catch
                {
                }

                // Step 2: Delete the source file with retries
                for (int deleteAttempt = 1; deleteAttempt <= retries; deleteAttempt++)
                {
                    try
                    {
                        await ReleaseAppFileHandlesAsync(src);
                        File.Delete(src);
                        return (true, "");
                    }
                    catch (IOException ex) when (deleteAttempt < retries)
                    {
                        lastError = $"Delete attempt {deleteAttempt} failed: {ex.Message}";
                        await ReleaseAppFileHandlesAsync(src);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        await Task.Delay(100 * deleteAttempt);
                    }
                    catch (IOException ex)
                    {
                        // Copy succeeded but delete failed - file exists in both locations.
                        return (false, $"File copied but could not delete source after {retries} attempts: {ex.Message}");
                    }
                }

                return (false, lastError ?? "Unexpected error in retry logic");
            }

            // Check if we can move the file
            var (canMove, canMoveError) = await CanMoveFileAsync(sourcePath, skipVarValidation);
            if (!canMove)
            {
                return (false, canMoveError);
            }

            // Ensure destination directory exists
            var destinationDir = Path.GetDirectoryName(destinationPath);
            Directory.CreateDirectory(destinationDir);

            // Check if destination already exists
            if (File.Exists(destinationPath))
            {
                return (false, "Destination file already exists");
            }

            // Capture original timestamps (for LastWriteTime preservation guarantee)
            var sourceInfo = new FileInfo(sourcePath);

            // Fast path: attempt atomic rename when on same volume.
            // This avoids the expensive copy+delete and preserves LastWriteTime.
            if (IsSameVolume(sourcePath, destinationPath))
            {
                // Critical: retry a few times to allow our own UI/image pipeline to release handles.
                // Keep this bounded to avoid long stalls; fallback will handle persistent issues.
                var fastAttempts = Math.Min(5, Math.Max(1, maxRetries));
                var (moved, moveError) = await TryFastRenameMoveAsync(sourceInfo, sourcePath, destinationPath, fastAttempts);
                if (moved)
                {
                    return (true, "");
                }
            }

            // Fallback: robust copy+delete (works across volumes and when rename is blocked).
            return await CopyDeleteFallbackAsync(sourceInfo, sourcePath, destinationPath, maxRetries);
        }

        /// <summary>
        /// Loads a package by moving it from AllPackages to AddonPackages with enhanced error handling.
        /// Handles .latest, .min[NUMBER], and exact version references.
        /// </summary>
        public async Task<(bool success, string error)> LoadPackageAsync(string packageName)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PackageFileManager));


            var operationKey = $"load_{packageName}";

            // Check for recent duplicate operations (1 second throttle for keyboard/UI responsiveness)
            if (WasRecentlyPerformed(operationKey, TimeSpan.FromSeconds(1)))
            {
                return (false, "Operation was recently performed, please wait before retrying");
            }

            // Fire operation started event
            OperationStarted?.Invoke(this, new PackageOperationEventArgs
            {
                PackageName = packageName,
                Operation = "Load"
            });

            await _fileLock.WaitAsync();
            try
            {
                RecordOperation(operationKey);
                
                // Parse the package name to get base name for lookup
                // This handles .latest, .min[NUMBER], and exact version references
                var depInfo = DependencyVersionInfo.Parse(packageName);
                var baseName = depInfo.BaseName;
                
                // First, check if already loaded (in AddonPackages)
                var loadedFile = FindMinimumVersionPackage(baseName, depInfo.VersionNumber ?? 0, _addonPackagesFolder);
                if (string.IsNullOrEmpty(loadedFile))
                {
                    loadedFile = FindLatestPackageVersion(baseName, _addonPackagesFolder);
                }
                
                if (!string.IsNullOrEmpty(loadedFile))
                {
                    // Package is already loaded
                    OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                    {
                        PackageName = packageName,
                        Operation = "Load",
                        Success = true,
                        ErrorMessage = ""
                    });
                    return (true, "");
                }
                
                // Find the package in AllPackages (available but not loaded)
                string sourceFile;
                if (depInfo.VersionType == DependencyVersionType.Minimum)
                {
                    sourceFile = FindMinimumVersionPackage(baseName, depInfo.VersionNumber ?? 0, _allPackagesFolder);
                }
                else
                {
                    sourceFile = FindLatestPackageVersion(baseName, _allPackagesFolder);
                }
                
                // Fallback: try the original package name
                if (string.IsNullOrEmpty(sourceFile))
                {
                    sourceFile = FindLatestPackageVersion(packageName, _allPackagesFolder);
                }

                if (string.IsNullOrEmpty(sourceFile))
                {
                    var errorMsg = $"Package '{packageName}' not found in available locations";
                    
                    OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                    {
                        PackageName = packageName,
                        Operation = "Load",
                        Success = false,
                        ErrorMessage = errorMsg
                    });
                    
                    return (false, errorMsg);
                }

                // Preserve subfolder structure when moving from AllPackages to AddonPackages
                var relativePath = Path.GetRelativePath(_allPackagesFolder, sourceFile);
                var destinationFile = Path.Combine(_addonPackagesFolder, relativePath);

                // Double-check if already loaded (race condition protection)
                if (File.Exists(destinationFile))
                {
                    OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                    {
                        PackageName = packageName,
                        Operation = "Load",
                        Success = true,
                        ErrorMessage = ""
                    });
                    return (true, "");
                }

                // First ensure any open file handles are closed
                if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceFile);
                
                // Move the file with enhanced error handling (skip validation to allow corrupt VARs to move)
                // SafeMoveFileAsync includes retry logic with exponential backoff for transient failures
                var (success, error) = await SafeMoveFileAsync(sourceFile, destinationFile, 5, true);
                
                if (success)
                {
                    _successfulOperations++;
                    
                    // Remove empty directories from source location
                    var sourceDirectory = Path.GetDirectoryName(sourceFile);
                    RemoveEmptyDirectories(sourceDirectory, _allPackagesFolder);
                    
                    // Update status index - package is now loaded
                    lock (_statusIndexLock)
                    {
                        UpdatePackageStatusInIndex(packageName, "Loaded");
                    }
                    
                    // Rebuild image index for this package to show previews
                    await _imageManager?.RebuildImageIndexForPackageAsync(destinationFile);

                    InvalidatePackageIndex();
                }

                OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                {
                    PackageName = packageName,
                    Operation = "Load",
                    Success = success,
                    ErrorMessage = error
                });

                return (success, error);
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Unexpected error loading package: {ex.Message}";
                    OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                    {
                        PackageName = packageName,
                        Operation = "Load",
                        Success = false,
                        ErrorMessage = errorMsg
                    });

                    return (false, errorMsg);
                }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Loads a package from an external path by copying it to AddonPackages.
        /// Used for external destination packages that need to be moved to the game folder.
        /// </summary>
        public async Task<(bool success, string error)> LoadPackageFromExternalPathAsync(string packageName, string externalFilePath)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PackageFileManager));

            if (string.IsNullOrEmpty(externalFilePath) || !File.Exists(externalFilePath))
            {
                return (false, $"External file not found: {externalFilePath}");
            }

            var operationKey = $"load_external_{packageName}";

            // Check for recent duplicate operations
            if (WasRecentlyPerformed(operationKey, TimeSpan.FromSeconds(1)))
            {
                return (false, "Operation was recently performed, please wait before retrying");
            }

            // Fire operation started event
            OperationStarted?.Invoke(this, new PackageOperationEventArgs
            {
                PackageName = packageName,
                Operation = "Load"
            });

            await _fileLock.WaitAsync();
            try
            {
                RecordOperation(operationKey);

                // Destination is directly in AddonPackages
                var fileName = Path.GetFileName(externalFilePath);
                var destinationFile = Path.Combine(_addonPackagesFolder, fileName);

                // Check if already loaded
                if (File.Exists(destinationFile))
                {
                    OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                    {
                        PackageName = packageName,
                        Operation = "Load",
                        Success = true,
                        ErrorMessage = ""
                    });
                    return (true, "");
                }

                // First ensure any open file handles are closed
                if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(externalFilePath);

                // Move the file from external location to AddonPackages
                var (success, error) = await SafeMoveFileAsync(externalFilePath, destinationFile, 5, true);

                if (success)
                {
                    _successfulOperations++;

                    // Update status index - package is now loaded
                    lock (_statusIndexLock)
                    {
                        UpdatePackageStatusInIndex(packageName, "Loaded");
                    }

                    // Rebuild image index for this package to show previews
                    await _imageManager?.RebuildImageIndexForPackageAsync(destinationFile);

                    InvalidatePackageIndex();
                }

                OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                {
                    PackageName = packageName,
                    Operation = "Load",
                    Success = success,
                    ErrorMessage = error
                });

                return (success, error);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Unexpected error loading external package: {ex.Message}";
                OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                {
                    PackageName = packageName,
                    Operation = "Load",
                    Success = false,
                    ErrorMessage = errorMsg
                });

                return (false, errorMsg);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Unloads a package by moving it from AddonPackages to AllPackages.
        /// Handles .latest, .min[NUMBER], and exact version references.
        /// </summary>
        public async Task<(bool success, string error)> UnloadPackageAsync(string packageName)
        {

            var operationKey = $"unload_{packageName}";

            // Check for recent duplicate operations (1 second throttle for keyboard/UI responsiveness)
            if (WasRecentlyPerformed(operationKey, TimeSpan.FromSeconds(1)))
            {
                return (false, "Operation was recently performed, please wait before retrying");
            }

            // Fire operation started event
            OperationStarted?.Invoke(this, new PackageOperationEventArgs
            {
                PackageName = packageName,
                Operation = "Unload"
            });

            await _fileLock.WaitAsync();
            try
            {
                RecordOperation(operationKey);

                // Parse the package name to get base name for lookup
                // This handles .latest, .min[NUMBER], and exact version references
                var depInfo = DependencyVersionInfo.Parse(packageName);
                var baseName = depInfo.BaseName;

                // Find the package file in AddonPackages using base name
                var sourceFile = FindLatestPackageVersion(baseName, _addonPackagesFolder);
                
                // If not found by base name, try the original package name
                if (string.IsNullOrEmpty(sourceFile))
                {
                    sourceFile = FindLatestPackageVersion(packageName, _addonPackagesFolder);
                }

                if (string.IsNullOrEmpty(sourceFile))
                {
                    var errorMsg = $"Package '{packageName}' not found in loaded packages";
                    
                    OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                    {
                        PackageName = packageName,
                        Operation = "Unload",
                        Success = false,
                        ErrorMessage = errorMsg
                    });
                    
                    return (false, errorMsg);
                }

                // Preserve subfolder structure when moving from AddonPackages to AllPackages
                var relativePath = Path.GetRelativePath(_addonPackagesFolder, sourceFile);
                var destinationFile = Path.Combine(_allPackagesFolder, relativePath);

                // Check if destination already exists
                if (File.Exists(destinationFile))
                {
                    try
                    {
                        // CRITICAL: Invalidate all image caches for this package BEFORE file operations
                        // This ensures no image references are held when the file is deleted
                        _imageManager?.InvalidatePackageCache(packageName);
                        
                        if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceFile);
                        
                        // Delete the loaded copy (retry on transient failures)
                        string lastDeleteError = null;
                        for (int attempt = 1; attempt <= 5; attempt++)
                        {
                            try
                            {
                                if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceFile);
                                FileAccessController.Instance.InvalidateFile(sourceFile);

                                File.Delete(sourceFile);
                                lastDeleteError = null;
                                break;
                            }
                            catch (IOException ex) when (attempt < 5)
                            {
                                lastDeleteError = ex.Message;
                                await Task.Delay(50 * attempt);
                            }
                            catch (UnauthorizedAccessException ex) when (attempt < 5)
                            {
                                lastDeleteError = ex.Message;
                                await Task.Delay(50 * attempt);
                            }
                        }

                        if (lastDeleteError != null && File.Exists(sourceFile))
                        {
                            throw new IOException(lastDeleteError);
                        }
                        
                        // Remove empty directories from source location
                        var sourceDirectory = Path.GetDirectoryName(sourceFile);
                        RemoveEmptyDirectories(sourceDirectory, _addonPackagesFolder);
                        
                        UpdatePackageStatusInIndex(packageName, "Available");
                        InvalidatePackageIndex();
                        
                        OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                        {
                            PackageName = packageName,
                            Operation = "Unload",
                            Success = true,
                            ErrorMessage = ""
                        });
                        
                        return (true, "");
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = $"Failed to remove loaded copy: {ex.Message}";
                        
                        OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                        {
                            PackageName = packageName,
                            Operation = "Unload",
                            Success = false,
                            ErrorMessage = errorMsg
                        });
                        
                        return (false, errorMsg);
                    }
                }

                // CRITICAL: Invalidate all image caches for this package BEFORE file operations
                // This ensures no image references are held when the file is moved
                _imageManager?.InvalidatePackageCache(packageName);
                
                if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceFile);
                
                // Move the file (skip validation to allow corrupt VARs to move)
                // SafeMoveFileAsync includes retry logic with exponential backoff for transient failures
                var (success, error) = await SafeMoveFileAsync(sourceFile, destinationFile, 5, true);

                if (success)
                {
                    _successfulOperations++;
                    
                    // Remove empty directories from source location
                    var sourceDirectory = Path.GetDirectoryName(sourceFile);
                    RemoveEmptyDirectories(sourceDirectory, _addonPackagesFolder);
                    
                    // Update status index - package is now available (unloaded)
                    UpdatePackageStatusInIndex(packageName, "Available");
                    
                    // Rebuild image index for this package to show previews
                    await _imageManager?.RebuildImageIndexForPackageAsync(destinationFile);
                    
                    InvalidatePackageIndex();
                }

                OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                {
                    PackageName = packageName,
                    Operation = "Unload",
                    Success = success,
                    ErrorMessage = error
                });

                return (success, error);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Unexpected error unloading package: {ex.Message}";
                OperationCompleted?.Invoke(this, new PackageOperationEventArgs
                {
                    PackageName = packageName,
                    Operation = "Unload",
                    Success = false,
                    ErrorMessage = errorMsg
                });

                return (false, errorMsg);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Loads multiple packages with enhanced progress reporting and error handling
        /// </summary>
        public async Task<List<(string packageName, bool success, string error)>> LoadPackagesAsync(
            IEnumerable<string> packageNames, 
            IProgress<(int completed, int total, string currentPackage)> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PackageFileManager));

            var packageList = packageNames.ToList();
            var results = new List<(string packageName, bool success, string error)>();
            

            // Fire progress event for batch start
            OperationProgress?.Invoke(this, new PackageOperationProgressEventArgs
            {
                Completed = 0,
                Total = packageList.Count,
                CurrentPackage = "Starting batch operation..."
            });

            var successCount = 0;

            for (int i = 0; i < packageList.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var packageName = packageList[i];
                
                // Report progress
                progress?.Report((i, packageList.Count, packageName));
                OperationProgress?.Invoke(this, new PackageOperationProgressEventArgs
                {
                    Completed = i,
                    Total = packageList.Count,
                    CurrentPackage = packageName
                });

                try
                {
                    var (success, error) = await LoadPackageAsync(packageName);
                    results.Add((packageName, success, error));

                    if (success)
                    {
                        successCount++;
                        // Ensure status is updated immediately after successful load
                        UpdatePackageStatusInIndex(packageName, "Loaded");
                    }
                }
                catch (Exception ex)
                {
                    var error = $"Exception during load: {ex.Message}";
                    results.Add((packageName, false, error));
                }
            }

            // Final progress report
            progress?.Report((packageList.Count, packageList.Count, ""));
            OperationProgress?.Invoke(this, new PackageOperationProgressEventArgs
            {
                Completed = packageList.Count,
                Total = packageList.Count,
                CurrentPackage = "Batch operation completed"
            });

            return results;
        }

        /// <summary>
        /// Unloads multiple packages with progress reporting
        /// </summary>
        public async Task<List<(string packageName, bool success, string error)>> UnloadPackagesAsync(
            IEnumerable<string> packageNames, 
            IProgress<(int completed, int total, string currentPackage)> progress = null,
            CancellationToken cancellationToken = default)
        {
            var packageList = packageNames.ToList();
            var results = new List<(string packageName, bool success, string error)>();
            

            for (int i = 0; i < packageList.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var packageName = packageList[i];
                progress?.Report((i, packageList.Count, packageName));

                var (success, error) = await UnloadPackageAsync(packageName);
                results.Add((packageName, success, error));
            }

            progress?.Report((packageList.Count, packageList.Count, ""));
            
            return results;
        }

        /// <summary>
        /// Gets the current status of a package (Loaded, Available, Missing) from pre-built index.
        /// Handles .latest, .min[NUMBER], and exact version references.
        /// </summary>
        public string GetPackageStatus(string packageName)
        {
            // Ensure index is built
            if (!_statusIndexBuilt)
            {
                lock (_statusIndexLock)
                {
                    if (!_statusIndexBuilt)
                    {
                        BuildPackageStatusIndex();
                    }
                }
            }
            
            // Try exact match first (base name like "Creator.Package")
            if (_packageStatusIndex.TryGetValue(packageName, out var status))
            {
                return status;
            }
            
            // Parse the dependency to handle .latest, .min[NUMBER], and exact versions
            var depInfo = DependencyVersionInfo.Parse(packageName);
            var baseName = depInfo.BaseName;
            
            // Try base name lookup
            if (_packageStatusIndex.TryGetValue(baseName, out status))
            {
                // For .latest and .min[NUMBER], any version on disk satisfies the dependency
                // For exact versions, we still return the status if the base package exists
                return status;
            }
            
            return "Missing";
        }

        /// <summary>
        /// Gets the current status of a package asynchronously (Loaded, Available, Missing) from pre-built index
        /// </summary>
        public Task<string> GetPackageStatusAsync(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return Task.FromResult("Unknown");

            return GetPackageStatusInternalAsync(packageName);
        }
        
        /// <summary>
        /// Registers an external package status in the index.
        /// Called by PackageManager to ensure external packages are recognized by GetPackageStatus.
        /// </summary>
        public void RegisterExternalPackageStatus(string packageName, string status)
        {
            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(status))
                return;
            
            // Only add if not already in index (don't override Loaded/Available with External)
            _packageStatusIndex.TryAdd(packageName, status);
        }
        
        /// <summary>
        /// Registers multiple external package statuses in the index.
        /// </summary>
        public void RegisterExternalPackageStatuses(Dictionary<string, string> packageStatuses)
        {
            if (packageStatuses == null || packageStatuses.Count == 0)
                return;
            
            foreach (var kvp in packageStatuses)
            {
                RegisterExternalPackageStatus(kvp.Key, kvp.Value);
            }
        }
        
        /// <summary>
        /// Builds the package status index by scanning available package locations
        /// Uses directory signatures to avoid redundant scans
        /// </summary>
        private void BuildPackageStatusIndex()
        {
            lock (_statusIndexLock)
            {
                // Check if directories have changed before rebuilding
                bool needsRebuild = true;
                
                if (_statusIndexBuilt && _packageStatusIndex.Count > 0)
                {
                    // Quick check: if directory signatures haven't changed, skip rebuild
                    // Note: This optimization can cause stale data if files were just added
                    // Use RefreshPackageStatusIndex(force: true) after downloads
                    needsRebuild = HasDirectoriesChanged();
                }
                
                if (!needsRebuild)
                    return;
                
                _packageStatusIndex.Clear();
                
                // Scan AddonPackages folder (Loaded packages) - including subfolders
                if (Directory.Exists(_addonPackagesFolder))
                {
                    var loadedFiles = Directory.GetFiles(_addonPackagesFolder, "*.var", SearchOption.AllDirectories);
                    foreach (var file in loadedFiles)
                    {
                        var packageName = ExtractPackageNameFromFilename(Path.GetFileNameWithoutExtension(file));
                        if (!string.IsNullOrEmpty(packageName))
                        {
                            _packageStatusIndex[packageName] = "Loaded";
                        }
                    }
                }
                
                // Scan AllPackages folder (Available packages) - including subfolders
                if (Directory.Exists(_allPackagesFolder))
                {
                    var availableFiles = Directory.GetFiles(_allPackagesFolder, "*.var", SearchOption.AllDirectories);
                    foreach (var file in availableFiles)
                    {
                        var packageName = ExtractPackageNameFromFilename(Path.GetFileNameWithoutExtension(file));
                        if (!string.IsNullOrEmpty(packageName))
                        {
                            // Only add as Available if not already marked as Loaded
                            // Use TryAdd to avoid overwriting Loaded status
                            _packageStatusIndex.TryAdd(packageName, "Available");
                        }
                    }
                }
                
                // Scan ArchivedPackages folder (Archived packages) - including subfolders
                // Archived packages are independent and can coexist with Loaded/Available packages
                // Use TryAdd to avoid overwriting Loaded or Available status
                if (Directory.Exists(_archivedPackagesFolder))
                {
                    var archivedFiles = Directory.GetFiles(_archivedPackagesFolder, "*.var", SearchOption.AllDirectories);
                    foreach (var file in archivedFiles)
                    {
                        var packageName = ExtractPackageNameFromFilename(Path.GetFileNameWithoutExtension(file));
                        if (!string.IsNullOrEmpty(packageName))
                        {
                            // Only add as Archived if not already marked as Loaded or Available
                            _packageStatusIndex.TryAdd(packageName, "Archived");
                        }
                    }
                }
                
                // Update directory signatures for next check
                UpdateDirectorySignatures();
                _statusIndexBuilt = true;
            }
        }
        
        /// <summary>
        /// Checks if any package directories have changed since last scan
        /// </summary>
        private bool HasDirectoriesChanged()
        {
            var directories = new[] { _addonPackagesFolder, _allPackagesFolder, _archivedPackagesFolder };
            
            foreach (var dir in directories)
            {
                if (!Directory.Exists(dir))
                    continue;
                
                var dirInfo = new DirectoryInfo(dir);
                var currentSignature = (dirInfo.GetFiles("*.var", SearchOption.AllDirectories).Length, 
                                       dirInfo.LastWriteTimeUtc.Ticks);
                
                if (!_statusIndexDirectorySignatures.TryGetValue(dir, out var lastSignature) || 
                    lastSignature != currentSignature)
                {
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Updates directory signatures for change detection
        /// </summary>
        private void UpdateDirectorySignatures()
        {
            var directories = new[] { _addonPackagesFolder, _allPackagesFolder, _archivedPackagesFolder };
            
            foreach (var dir in directories)
            {
                if (!Directory.Exists(dir))
                {
                    _statusIndexDirectorySignatures.Remove(dir);
                    continue;
                }
                
                var dirInfo = new DirectoryInfo(dir);
                var signature = (dirInfo.GetFiles("*.var", SearchOption.AllDirectories).Length, 
                                dirInfo.LastWriteTimeUtc.Ticks);
                _statusIndexDirectorySignatures[dir] = signature;
            }
        }
        
        /// <summary>
        /// Updates the status of a specific package in the index
        /// </summary>
        private void UpdatePackageStatusInIndex(string packageName, string newStatus)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return;

            lock (_statusIndexLock)
            {
                string currentStatus;
                if (_packageStatusIndex.TryGetValue(packageName, out currentStatus))
                {
                    // Only update if status actually changed
                    if (currentStatus != newStatus)
                    {
                        if (newStatus == "Missing")
                        {
                            string oldStatus;
                            _packageStatusIndex.Remove(packageName, out oldStatus);
                        }
                        else
                        {
                            _packageStatusIndex[packageName] = newStatus;
                        }
                    }
                }
                else if (newStatus != "Missing")
                {
                    _packageStatusIndex[packageName] = newStatus;
                }
            }
        }
        
        /// <summary>
        /// Extracts package name from filename (removes version number)
        /// </summary>
        private string ExtractPackageNameFromFilename(string filename)
        {
            var match = _varPattern.Match(filename + ".var");
            if (match.Success)
            {
                return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
            }
            return null;
        }
        
        /// <summary>
        /// Rebuilds the package status index (call when major changes occur)
        /// </summary>
        /// <param name="force">If true, forces a full rebuild ignoring directory signature cache</param>
        public void RefreshPackageStatusIndex(bool force = false)
        {
            if (force)
            {
                // Clear the built flag to force a full rebuild
                _statusIndexBuilt = false;
            }
            BuildPackageStatusIndex();
        }

        /// <summary>
        /// Removes empty directories recursively up to the root folder
        /// </summary>
        private void RemoveEmptyDirectories(string directoryPath, string rootFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
                    return;

                // Don't remove the root folders themselves
                if (string.Equals(Path.GetFullPath(directoryPath), Path.GetFullPath(rootFolder), StringComparison.OrdinalIgnoreCase))
                    return;

                // Check if directory is empty (no files and no subdirectories)
                if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
                {
                    Directory.Delete(directoryPath);
                    
                    // Recursively check parent directory
                    var parentDirectory = Path.GetDirectoryName(directoryPath);
                    if (!string.IsNullOrEmpty(parentDirectory))
                    {
                        RemoveEmptyDirectories(parentDirectory, rootFolder);
                    }
                }
            }
            catch (Exception)
            {
                // Silently ignore errors when removing empty directories
            }
        }

        /// <summary>
        /// Gets detailed information about a specific package version by MetadataKey
        /// MetadataKey format: "Creator.Package.Version" or "Creator.Package.Version#archived"
        /// More robust version that handles various MetadataKey formats
        /// </summary>
        public PackageFileInfo GetPackageFileInfoByMetadataKey(string metadataKey)
        {
            var info = new PackageFileInfo { PackageName = metadataKey };

            if (string.IsNullOrWhiteSpace(metadataKey))
            {
                info.Status = "Missing";
                return info;
            }

            // Parse the metadata key to extract version and status suffix
            string fullKey = metadataKey;
            string statusSuffix = "";
            
            if (metadataKey.Contains("#"))
            {
                var parts = metadataKey.Split('#');
                fullKey = parts[0];
                statusSuffix = parts[1];
            }

            // Extract version from the full key (last dot-separated number)
            int requestedVersion = 0;
            string packageBaseKey = fullKey;
            
            var keyParts = fullKey.Split('.');
            if (keyParts.Length >= 2)
            {
                var versionToken = keyParts[^1];
                if (int.TryParse(versionToken, out int version) && version > 0)
                {
                    requestedVersion = version;
                    // Remove the version from the end to get the base key (Creator.Package)
                    packageBaseKey = string.Join(".", keyParts.Take(keyParts.Length - 1));
                }
            }

            // If no version found, fall back to latest version behavior
            if (requestedVersion == 0)
            {
                return GetPackageFileInfo(metadataKey);
            }

            // Determine which folder to search based on status suffix
            string[] searchFolders;
            if (statusSuffix.Equals("archived", StringComparison.OrdinalIgnoreCase))
            {
                searchFolders = new[] { _archivedPackagesFolder };
                info.Status = "Archived";
            }
            else if (statusSuffix.Equals("available", StringComparison.OrdinalIgnoreCase))
            {
                searchFolders = new[] { _allPackagesFolder };
                info.Status = "Available";
            }
            else
            {
                // Default: search loaded first, then available, then archived
                searchFolders = new[] { _addonPackagesFolder, _allPackagesFolder, _archivedPackagesFolder };
            }

            // Search for the specific version using the correct base key
            foreach (var folder in searchFolders)
            {
                EnsurePackageIndex(new[] { folder });
                
                _packageIndexLock.EnterReadLock();
                try
                {
                    // Try exact match first
                    if (_packageIndex.TryGetValue(packageBaseKey, out var entry))
                    {
                        if (entry.TryGetVersionPath(requestedVersion, out var filePath))
                        {
                            info.CurrentPath = filePath;
                            
                            // Determine status based on folder
                            if (folder == _addonPackagesFolder)
                            {
                                info.Status = "Loaded";
                                info.LoadedPath = filePath;
                            }
                            else if (folder == _allPackagesFolder)
                            {
                                info.Status = "Available";
                                info.AvailablePath = filePath;
                            }
                            else if (folder == _archivedPackagesFolder)
                            {
                                info.Status = "Archived";
                            }
                            
                            return info;
                        }
                    }
                    
                    // If exact match didn't work, try case-insensitive search
                    var caseInsensitiveEntry = _packageIndex
                        .FirstOrDefault(kvp => kvp.Key.Equals(packageBaseKey, StringComparison.OrdinalIgnoreCase))
                        .Value;
                    
                    if (caseInsensitiveEntry != null && caseInsensitiveEntry.TryGetVersionPath(requestedVersion, out var filePath2))
                    {
                        info.CurrentPath = filePath2;
                        
                        // Determine status based on folder
                        if (folder == _addonPackagesFolder)
                        {
                            info.Status = "Loaded";
                            info.LoadedPath = filePath2;
                        }
                        else if (folder == _allPackagesFolder)
                        {
                            info.Status = "Available";
                            info.AvailablePath = filePath2;
                        }
                        else if (folder == _archivedPackagesFolder)
                        {
                            info.Status = "Archived";
                        }
                        
                        return info;
                    }
                }
                finally
                {
                    _packageIndexLock.ExitReadLock();
                }
            }

            info.Status = "Missing";
            return info;
        }

        /// <summary>
        /// Gets detailed information about a package's file locations
        /// </summary>
        public PackageFileInfo GetPackageFileInfo(string packageName)
        {
            var info = new PackageFileInfo { PackageName = packageName };

            var exact = FindExactPackagePath(packageName, new[] { _addonPackagesFolder, _allPackagesFolder, _archivedPackagesFolder });
            if (!string.IsNullOrEmpty(exact))
            {
                info.CurrentPath = exact;
                if (exact.StartsWith(_addonPackagesFolder, StringComparison.OrdinalIgnoreCase))
                {
                    info.Status = "Loaded";
                    info.LoadedPath = exact;
                }
                else if (exact.StartsWith(_allPackagesFolder, StringComparison.OrdinalIgnoreCase))
                {
                    info.Status = "Available";
                    info.AvailablePath = exact;
                }
                else if (exact.StartsWith(_archivedPackagesFolder, StringComparison.OrdinalIgnoreCase))
                {
                    info.Status = "Archived";
                }
                else
                {
                    info.Status = "Unknown";
                }

                return info;
            }

            // Check all locations (in priority order)
            info.LoadedPath = FindLatestPackageVersion(packageName, _addonPackagesFolder);
            info.AvailablePath = FindLatestPackageVersion(packageName, _allPackagesFolder);
            string archivedPath = FindLatestPackageVersion(packageName, _archivedPackagesFolder);

            // Determine status (priority: Loaded > Available > Archived > Missing)
            if (!string.IsNullOrEmpty(info.LoadedPath))
            {
                info.Status = "Loaded";
                info.CurrentPath = info.LoadedPath;
            }
            else if (!string.IsNullOrEmpty(info.AvailablePath))
            {
                info.Status = "Available";
                info.CurrentPath = info.AvailablePath;
            }
            else if (!string.IsNullOrEmpty(archivedPath))
            {
                info.Status = "Archived";
                info.CurrentPath = archivedPath;
            }
            else
            {
                info.Status = "Missing";
            }

            return info;
        }

        /// <summary>
        /// Cleans up old operation history entries to prevent memory leaks
        /// </summary>
        private void CleanupOperationHistory(object sender, EventArgs e)
        {
            lock (_historyLock)
            {
                var cutoffTime = DateTime.Now.AddHours(-1);
                var keysToRemove = _operationHistory
                    .Where(kvp => kvp.Value < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var key in keysToRemove)
                {
                    _operationHistory.Remove(key);
                }
            }
        }

        /// <summary>
        /// Checks if an operation was recently performed to prevent duplicate operations
        /// </summary>
        private bool WasRecentlyPerformed(string operationKey, TimeSpan threshold)
        {
            lock (_historyLock)
            {
                if (_operationHistory.TryGetValue(operationKey, out DateTime lastTime))
                {
                    return DateTime.Now - lastTime < threshold;
                }
                return false;
            }
        }

        /// <summary>
        /// Records an operation in the history
        /// </summary>
        private void RecordOperation(string operationKey)
        {
            lock (_historyLock)
            {
                _operationHistory[operationKey] = DateTime.Now;
                _totalOperations++;
                _lastOperationTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Gets performance statistics
        /// </summary>
        public PackageOperationStats GetOperationStats()
        {
            return new PackageOperationStats
            {
                TotalOperations = _totalOperations,
                SuccessfulOperations = _successfulOperations,
                FailureRate = _totalOperations > 0 ? (double)(_totalOperations - _successfulOperations) / _totalOperations : 0,
                LastOperationTime = _lastOperationTime
            };
        }

        #region IDisposable Implementation
        private void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _operationTimer?.Stop();
                _fileLock?.Dispose();
                foreach (var packageLock in _packageLocks.Values)
                {
                    packageLock?.Dispose();
                }
                
                // FIXED: Dispose ReaderWriterLockSlim to release unmanaged handles
                _packageIndexLock?.Dispose();
                
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~PackageFileManager()
        {
            Dispose(false);
        }
        #endregion
    }

    internal sealed class PackageFileIndexEntry
    {
        // Optimized storage for single version (common case)
        private int _singleVersion;
        private string _singleVersionPath;
        
        // Optimized storage for single directory (common case)
        private string _singleDirectory;
        
        // Fallback for multiple versions/directories
        private Dictionary<int, string> _versionPaths;
        private HashSet<string> _directories;

        public PackageFileIndexEntry(string packageBase)
        {
            if (string.IsNullOrWhiteSpace(packageBase))
                throw new ArgumentException(nameof(packageBase));

            PackageBase = StringPool.Intern(packageBase);
        }

        public string PackageBase { get; }

        public int LatestVersion { get; private set; }

        public string LatestPath { get; private set; }

        public string LatestDirectory { get; private set; }

        // Legacy property for compatibility - creates dictionary on demand if needed
        // Prefer using TryGetVersionPath or GetVersions for better performance
        public IReadOnlyDictionary<int, string> VersionPaths 
        {
            get
            {
                if (_versionPaths != null) return _versionPaths;
                if (_singleVersionPath != null) return new Dictionary<int, string> { { _singleVersion, _singleVersionPath } };
                return new Dictionary<int, string>();
            }
        }

        public IReadOnlyCollection<string> Directories
        {
            get
            {
                if (_directories != null) return _directories;
                if (_singleDirectory != null) return new[] { _singleDirectory };
                return Array.Empty<string>();
            }
        }

        public bool TryGetVersionPath(int version, out string path)
        {
            if (_versionPaths != null)
            {
                return _versionPaths.TryGetValue(version, out path);
            }
            
            if (_singleVersionPath != null && _singleVersion == version)
            {
                path = _singleVersionPath;
                return true;
            }
            
            path = null;
            return false;
        }

        public IEnumerable<KeyValuePair<int, string>> GetVersions()
        {
            if (_versionPaths != null)
            {
                foreach (var kvp in _versionPaths) yield return kvp;
            }
            else if (_singleVersionPath != null)
            {
                yield return new KeyValuePair<int, string>(_singleVersion, _singleVersionPath);
            }
        }

        public void Update(int version, string filePath, string directory)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            // Intern strings to save memory
            filePath = StringPool.InternPath(filePath);
            directory = StringPool.InternPath(directory);

            // Update Version Paths
            if (_versionPaths != null)
            {
                _versionPaths[version] = filePath;
            }
            else if (_singleVersionPath == null)
            {
                _singleVersion = version;
                _singleVersionPath = filePath;
            }
            else if (_singleVersion != version)
            {
                // Upgrade to Dictionary
                _versionPaths = new Dictionary<int, string>
                {
                    { _singleVersion, _singleVersionPath },
                    { version, filePath }
                };
                _singleVersionPath = null;
            }
            else
            {
                // Update existing single version
                _singleVersionPath = filePath;
            }

            // Update Directories
            if (!string.IsNullOrWhiteSpace(directory))
            {
                if (_directories != null)
                {
                    _directories.Add(directory);
                }
                else if (_singleDirectory == null)
                {
                    _singleDirectory = directory;
                }
                else if (!string.Equals(_singleDirectory, directory, StringComparison.OrdinalIgnoreCase))
                {
                    // Upgrade to HashSet
                    _directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        _singleDirectory,
                        directory
                    };
                    _singleDirectory = null;
                }
            }

            // Update Latest
            if (LatestPath == null || version > LatestVersion)
            {
                LatestVersion = version;
                LatestPath = filePath;
                LatestDirectory = directory;
                return;
            }

            if (version == LatestVersion && string.Equals(directory, LatestDirectory, StringComparison.OrdinalIgnoreCase))
            {
                LatestPath = filePath;
            }
        }
    }

    /// <summary>
    /// Information about a package's file locations and status
    /// </summary>
    public class PackageFileInfo
    {
        public string PackageName { get; set; }
        public string Status { get; set; }
        public string CurrentPath { get; set; }
        public string LoadedPath { get; set; }
        public string AvailablePath { get; set; }
    }

    /// <summary>
    /// Event arguments for package operations
    /// </summary>
    public class PackageOperationEventArgs : EventArgs
    {
        public string PackageName { get; set; }
        public string Operation { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Event arguments for package operation progress
    /// </summary>
    public class PackageOperationProgressEventArgs : EventArgs
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public string CurrentPackage { get; set; }
        public double ProgressPercentage => Total > 0 ? (double)Completed / Total * 100 : 0;
    }

    /// <summary>
    /// Performance statistics for package operations
    /// </summary>
    public class PackageOperationStats
    {
        public int TotalOperations { get; set; }
        public int SuccessfulOperations { get; set; }
        public double FailureRate { get; set; }
        public DateTime LastOperationTime { get; set; }
    }
}

