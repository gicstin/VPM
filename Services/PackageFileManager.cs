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
            
            // Don't build package status index here - it will be built lazily on first use
            // to avoid blocking the UI thread during startup
            _statusIndexBuilt = false;
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
                    files = Directory.EnumerateFiles(directory, "*.var", SearchOption.AllDirectories);
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
        /// Resolves a dependency name (which may end with .latest) to an actual file path
        /// </summary>
        public string ResolveDependencyToFilePath(string dependencyName)
        {
            // Remove .latest suffix if present
            var baseName = dependencyName;
            if (baseName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName[..^7]; // Remove ".latest"
            }

            // Search in all available locations
            var searchDirectories = new[] { _addonPackagesFolder, _allPackagesFolder };
            
            // First try to find the latest version
            var latestFile = FindLatestPackageVersion(baseName, searchDirectories);
            if (!string.IsNullOrEmpty(latestFile))
            {
                return latestFile;
            }

            // Fallback: attempt direct lookup for exact filename without versioning
            return FindExactPackagePath(baseName, searchDirectories);
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
        /// Safely moves a file with retry logic and atomic operations
        /// </summary>
        private async Task<(bool success, string error)> SafeMoveFileAsync(string sourcePath, string destinationPath, int maxRetries = 3, bool skipVarValidation = false)
        {
            Exception lastException = null;
            string lastError = null;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // Check if we can move the file
                    var (canMove, error) = await CanMoveFileAsync(sourcePath, skipVarValidation);
                    if (!canMove)
                    {
                        lastError = error;
                        return (false, error);
                    }

                    // Ensure destination directory exists
                    var destinationDir = Path.GetDirectoryName(destinationPath);
                    Directory.CreateDirectory(destinationDir);

                    // Check if destination already exists
                    if (File.Exists(destinationPath))
                    {
                        lastError = "Destination file already exists";
                        return (false, lastError);
                    }

                    // Perform atomic move
                    File.Move(sourcePath, destinationPath);
                    return (true, "");
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    lastError = $"Attempt {attempt} failed: {ex.GetType().Name}: {ex.Message}";
                    if (attempt == maxRetries)
                    {
                        return (false, $"Failed after {maxRetries} attempts. Last error: {ex.GetType().Name}: {ex.Message}");
                    }
                    // Wait before retry
                    await Task.Delay(1000 * attempt);
                }
            }
            // Should not reach here, but return last error if it does
            return (false, lastError ?? "Unexpected error in retry logic");
        }

        /// <summary>
        /// Loads a package by moving it from AllPackages to AddonPackages with enhanced error handling
        /// </summary>
        public async Task<(bool success, string error)> LoadPackageAsync(string packageName)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PackageFileManager));

            // Prevent loading archived packages
            if (packageName.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Cannot load archived packages. Archived packages are read-only.");
            }

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
                
                // Find the package file in available locations (excluding ArchivedPackages)
                var sourceFile = FindLatestPackageVersion(packageName, _allPackagesFolder);

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
                _imageManager?.CloseFileHandles(sourceFile);

                // Move the file with enhanced error handling (skip validation to allow corrupt VARs to move)
                var (success, error) = await SafeMoveFileAsync(sourceFile, destinationFile, 3, true);
                
                if (success)
                {
                    _successfulOperations++;
                    
                    // Remove empty directories from source location
                    var sourceDirectory = Path.GetDirectoryName(sourceFile);
                    RemoveEmptyDirectories(sourceDirectory, _allPackagesFolder);
                    
                    // Update status index - package is now loaded
                    await Task.Run(() => 
                    {
                        lock (_statusIndexLock)
                        {
                            UpdatePackageStatusInIndex(packageName, "Loaded");
                            // Also refresh status index to ensure accuracy
                            BuildPackageStatusIndex();
                        }
                    });
                    // Update image index paths
                    _imageManager?.UpdateVarPath(sourceFile, destinationFile);

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
        /// Unloads a package by moving it from AddonPackages to AllPackages
        /// </summary>
        public async Task<(bool success, string error)> UnloadPackageAsync(string packageName)
        {
            // Prevent unloading archived packages
            if (packageName.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Cannot unload archived packages. Archived packages are read-only.");
            }

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

                // Find the package file in AddonPackages
                var sourceFile = FindLatestPackageVersion(packageName, _addonPackagesFolder);

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
                        // Ensure any open handles are closed before deletion
                        _imageManager?.CloseFileHandles(sourceFile);
                        File.Delete(sourceFile);
                        
                        // Remove empty directories from source location
                        var sourceDirectory = Path.GetDirectoryName(sourceFile);
                        RemoveEmptyDirectories(sourceDirectory, _addonPackagesFolder);
                        
                        UpdatePackageStatusInIndex(packageName, "Available");
                        _imageManager?.UpdateVarPath(sourceFile, destinationFile);
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

                // First ensure any open file handles are closed
                _imageManager?.CloseFileHandles(sourceFile);

                // Move the file (skip validation to allow corrupt VARs to move)
                var (success, error) = await SafeMoveFileAsync(sourceFile, destinationFile, 3, true);

                if (success)
                {
                    _successfulOperations++;
                    
                    // Remove empty directories from source location
                    var sourceDirectory = Path.GetDirectoryName(sourceFile);
                    RemoveEmptyDirectories(sourceDirectory, _addonPackagesFolder);
                    
                    // Update status index - package is now available (unloaded)
                    UpdatePackageStatusInIndex(packageName, "Available");
                    // Update image index paths
                    _imageManager?.UpdateVarPath(sourceFile, destinationFile);
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
        /// Gets the current status of a package (Loaded, Available, Missing) from pre-built index
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
            
            // Return status from index, default to Missing if not found
            return _packageStatusIndex.TryGetValue(packageName, out var status) ? status : "Missing";
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
        /// Builds the complete package status index by scanning all directories including archived packages
        /// </summary>
        private void BuildPackageStatusIndex()
        {
            lock (_statusIndexLock)
            {
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
                        if (!string.IsNullOrEmpty(packageName) && !_packageStatusIndex.ContainsKey(packageName))
                        {
                            _packageStatusIndex[packageName] = "Available";
                        }
                    }
                }
                
                // Scan ArchivedPackages folder (Archived packages) - including subfolders
                // Archived packages are independent and can coexist with Loaded/Available packages
                if (Directory.Exists(_archivedPackagesFolder))
                {
                    var archivedFiles = Directory.GetFiles(_archivedPackagesFolder, "*.var", SearchOption.AllDirectories);
                    foreach (var file in archivedFiles)
                    {
                        var packageName = ExtractPackageNameFromFilename(Path.GetFileNameWithoutExtension(file));
                        if (!string.IsNullOrEmpty(packageName))
                        {
                            // Create a unique key for archived packages to allow them to coexist with optimized versions
                            string archivedKey = $"{packageName}#archived";
                            _packageStatusIndex[archivedKey] = "Archived";
                        }
                    }
                }
                
                _statusIndexBuilt = true;
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
        public void RefreshPackageStatusIndex()
        {
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
            if (keyParts.Length >= 2 && int.TryParse(keyParts[^1], out int version))
            {
                requestedVersion = version;
                // Remove the version from the end to get the base key (Creator.Package)
                packageBaseKey = string.Join(".", keyParts.Take(keyParts.Length - 1));
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
                        if (entry.VersionPaths.TryGetValue(requestedVersion, out var filePath))
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
                    
                    if (caseInsensitiveEntry != null && caseInsensitiveEntry.VersionPaths.TryGetValue(requestedVersion, out var filePath2))
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

            // Check if this is an archived package (has #archived suffix)
            if (packageName.EndsWith("#archived", StringComparison.OrdinalIgnoreCase))
            {
                // Remove #archived suffix to get the actual filename
                string actualPackageName = packageName.Substring(0, packageName.Length - 9);
                string archivedPathForSuffix = FindLatestPackageVersion(actualPackageName, _archivedPackagesFolder);
                
                if (!string.IsNullOrEmpty(archivedPathForSuffix))
                {
                    info.Status = "Archived";
                    info.CurrentPath = archivedPathForSuffix;
                    return info;
                }
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
        private readonly Dictionary<int, string> _versionPaths = new Dictionary<int, string>();
        private readonly HashSet<string> _directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public PackageFileIndexEntry(string packageBase)
        {
            if (string.IsNullOrWhiteSpace(packageBase))
                throw new ArgumentException(nameof(packageBase));

            PackageBase = packageBase;
        }

        public string PackageBase { get; }

        public int LatestVersion { get; private set; }

        public string LatestPath { get; private set; }

        public string LatestDirectory { get; private set; }

        public IReadOnlyDictionary<int, string> VersionPaths => _versionPaths;

        public IReadOnlyCollection<string> Directories => _directories;

        public void Update(int version, string filePath, string directory)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            _versionPaths[version] = filePath;

            if (!string.IsNullOrWhiteSpace(directory))
            {
                _directories.Add(directory);
            }

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

