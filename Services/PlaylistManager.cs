using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Manages playlist operations including activation, dependency resolution, and package loading/unloading
    /// </summary>
    public class PlaylistManager
    {
        private readonly PackageManager _packageManager;
        private readonly DependencyGraph _dependencyGraph;
        private readonly PackageFileManager _packageFileManager;
        private readonly Vpb.ScanControlService _scanControl;
        private readonly string _addonPackagesFolder;
        private readonly string _allPackagesFolder;

        public PlaylistManager(PackageManager packageManager, DependencyGraph dependencyGraph, string vamRootFolder, PackageFileManager packageFileManager, Vpb.ScanControlService scanControl = null)
        {
            _packageManager = packageManager ?? throw new ArgumentNullException(nameof(packageManager));
            _dependencyGraph = dependencyGraph ?? throw new ArgumentNullException(nameof(dependencyGraph));
            _packageFileManager = packageFileManager; // Optional, but recommended for robust file operations
            _scanControl = scanControl;
            _addonPackagesFolder = Path.Combine(vamRootFolder ?? throw new ArgumentNullException(nameof(vamRootFolder)), "AddonPackages");
            _allPackagesFolder = Path.Combine(vamRootFolder, "AllPackages");
        }

        /// <summary>
        /// Gets all package keys that should be loaded for a playlist, including dependencies
        /// </summary>
        public HashSet<string> GetAllPackagesToLoad(Playlist playlist)
        {
            if (playlist == null || !playlist.IsValid())
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var packagesToLoad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toProcess = new Queue<string>(playlist.PackageKeys);
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (toProcess.Count > 0)
            {
                var packageKey = toProcess.Dequeue();
                
                if (processed.Contains(packageKey))
                    continue;

                processed.Add(packageKey);
                packagesToLoad.Add(packageKey);

                var dependencies = _dependencyGraph.GetDependencies(packageKey);
                foreach (var dep in dependencies)
                {
                    if (!processed.Contains(dep))
                        toProcess.Enqueue(dep);
                }
            }

            return packagesToLoad;
        }

        /// <summary>
        /// Gets packages that need to be unloaded (not in the playlist and not in external locations)
        /// </summary>
        public List<string> GetPackagesToUnload(Playlist playlist, bool respectUnloadSetting = true)
        {
            if (!respectUnloadSetting || !playlist.UnloadOtherPackages)
                return new List<string>();

            var packagesToLoad = GetAllPackagesToLoad(playlist);
            var packagesToUnload = new List<string>();

            foreach (var kvp in _packageManager.PackageMetadata)
            {
                var metadataKey = kvp.Key;
                var metadata = kvp.Value;

                if (metadata.IsCorrupted)
                    continue;

                var isCurrentlyLoaded = metadata.Status == "Loaded" || 
                    (metadata.FilePath != null && metadata.FilePath.Contains("AddonPackages", StringComparison.OrdinalIgnoreCase));

                var isInPlaylist = packagesToLoad.Contains(metadataKey);
                var isInExternalLocation = !string.IsNullOrWhiteSpace(metadata.ExternalDestinationName);

                if (isCurrentlyLoaded && !isInPlaylist && !isInExternalLocation)
                {
                    packagesToUnload.Add(metadataKey);
                }
            }

            return packagesToUnload;
        }

        /// <summary>
        /// Activates a playlist by loading required packages and optionally unloading others
        /// </summary>
        public async Task<PlaylistActivationResult> ActivatePlaylistAsync(Playlist playlist, bool unloadOthers = true)
        {
            return await ActivatePlaylistAsync(playlist, unloadOthers, progress: null);
        }

        /// <summary>
        /// Activates a playlist by loading required packages and optionally unloading others.
        /// Reports progress updates as packages are moved.
        /// </summary>
        public async Task<PlaylistActivationResult> ActivatePlaylistAsync(
            Playlist playlist,
            bool unloadOthers,
            IProgress<PlaylistActivationProgress> progress)
        {
            var result = new PlaylistActivationResult();

            if (playlist == null || !playlist.IsValid())
            {
                result.Success = false;
                result.Message = "Invalid playlist";
                return result;
            }

            var packagesToLoad = GetAllPackagesToLoad(playlist);

            if (_scanControl != null && _scanControl.IsWhitelistMode)
            {
                if (unloadOthers && playlist.UnloadOtherPackages && packagesToLoad.Count == 0)
                {
                    result.Success = false;
                    result.Message = "Exclusive empty playlist would exclude all packages from VaM boot scan.";
                    return result;
                }

                progress?.Report(new PlaylistActivationProgress
                {
                    Phase = "Writing scan set",
                    Completed = 0,
                    Total = 1,
                    CurrentPackageKey = ""
                });

                var vamRoot = Directory.GetParent(_addonPackagesFolder)?.FullName;
                var compiled = Vpb.WhitelistCompiler.Compile(
                    packagesToLoad,
                    _dependencyGraph,
                    _packageManager.PackageMetadata,
                    vamRoot,
                    _scanControl.Whitelist,
                    exclusive: unloadOthers && playlist.UnloadOtherPackages);

                _scanControl.ActivateCompiled(compiled);
                await Task.CompletedTask;
                result.Success = true;
                result.PackagesToLoad = packagesToLoad.ToList();
                result.LoadedCount = packagesToLoad.Count;
                result.UnloadedCount = 0;
                result.Message = playlist.UnloadOtherPackages
                    ? $"Scan set replaced: {packagesToLoad.Count} packages"
                    : $"Scan set union: {packagesToLoad.Count} packages";
                progress?.Report(new PlaylistActivationProgress
                {
                    Phase = "Complete",
                    Completed = 1,
                    Total = 1,
                    CurrentPackageKey = ""
                });
                return result;
            }

            var packagesToUnload = unloadOthers ? GetPackagesToUnload(playlist, respectUnloadSetting: true) : new List<string>();

            result.PackagesToLoad = packagesToLoad.ToList();
            result.PackagesToUnload = packagesToUnload;

            int totalSteps = packagesToUnload.Count + packagesToLoad.Count;
            int completedSteps = 0;

            progress?.Report(new PlaylistActivationProgress
            {
                Phase = "Preparing",
                Completed = completedSteps,
                Total = totalSteps,
                CurrentPackageKey = ""
            });

            try
            {
                int loadedCount = 0;
                int unloadedCount = 0;

                foreach (var packageKey in packagesToUnload)
                {
                    progress?.Report(new PlaylistActivationProgress
                    {
                        Phase = "Unloading",
                        Completed = completedSteps,
                        Total = totalSteps,
                        CurrentPackageKey = packageKey
                    });

                    if (_packageManager.PackageMetadata.TryGetValue(packageKey, out var metadata) && metadata != null)
                    {
                        if (await MovePackageToAllPackagesAsync(metadata))
                            unloadedCount++;
                    }

                    completedSteps++;
                    progress?.Report(new PlaylistActivationProgress
                    {
                        Phase = "Unloading",
                        Completed = completedSteps,
                        Total = totalSteps,
                        CurrentPackageKey = packageKey
                    });
                }

                foreach (var packageKey in packagesToLoad)
                {
                    progress?.Report(new PlaylistActivationProgress
                    {
                        Phase = "Loading",
                        Completed = completedSteps,
                        Total = totalSteps,
                        CurrentPackageKey = packageKey
                    });

                    if (_packageManager.PackageMetadata.TryGetValue(packageKey, out var metadata) && metadata != null)
                    {
                        var currentStatus = metadata.Status;
                        var isLoaded = currentStatus == "Loaded" ||
                            (metadata.FilePath != null && metadata.FilePath.Contains("AddonPackages", StringComparison.OrdinalIgnoreCase));

                        if (!isLoaded)
                        {
                            if (await MovePackageToAddonPackagesAsync(metadata))
                                loadedCount++;
                        }
                        else
                        {
                            loadedCount++;
                        }
                    }

                    completedSteps++;
                    progress?.Report(new PlaylistActivationProgress
                    {
                        Phase = "Loading",
                        Completed = completedSteps,
                        Total = totalSteps,
                        CurrentPackageKey = packageKey
                    });
                }

                result.Success = true;
                result.LoadedCount = loadedCount;
                result.UnloadedCount = unloadedCount;
                result.Message = $"Playlist activated: {loadedCount} loaded, {unloadedCount} unloaded";

                progress?.Report(new PlaylistActivationProgress
                {
                    Phase = "Complete",
                    Completed = totalSteps,
                    Total = totalSteps,
                    CurrentPackageKey = ""
                });
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Error activating playlist: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Moves a package to AddonPackages folder
        /// </summary>
        private async Task<bool> MovePackageToAddonPackagesAsync(VarMetadata metadata)
        {
            if (_packageFileManager != null)
            {
                // Use robust PackageFileManager if available
                string packageKey = $"{metadata.CreatorName}.{metadata.PackageName}.{metadata.Version}";
                var (success, _) = await _packageFileManager.LoadPackageAsync(packageKey);
                
                if (success)
                {
                    // Update local metadata to reflect change
                    metadata.Status = "Loaded";
                    
                    // Get the correct path from PackageFileManager to handle subfolders correctly
                    var info = _packageFileManager.GetPackageFileInfo(packageKey);
                    if (info.Status == "Loaded" && !string.IsNullOrEmpty(info.LoadedPath))
                    {
                        metadata.FilePath = info.LoadedPath;
                    }
                    else if (!string.IsNullOrEmpty(metadata.FilePath))
                    {
                        // Fallback: assume flat structure if info not available
                        var fileName = Path.GetFileName(metadata.FilePath);
                        metadata.FilePath = Path.Combine(_addonPackagesFolder, fileName);
                    }
                }
                return success;
            }

            try
            {
                if (string.IsNullOrEmpty(metadata.FilePath))
                    return false;

                var fileName = Path.GetFileName(metadata.FilePath);
                var destPath = Path.Combine(_addonPackagesFolder, fileName);

                if (metadata.FilePath.Equals(destPath, StringComparison.OrdinalIgnoreCase))
                    return true;

                // If source file is missing, check if it's already at destination
                if (!File.Exists(metadata.FilePath))
                {
                    if (File.Exists(destPath))
                    {
                        metadata.FilePath = destPath;
                        metadata.Status = "Loaded";
                        return true;
                    }
                    return false;
                }

                Directory.CreateDirectory(_addonPackagesFolder);

                await Task.Run(() =>
                {
                    try
                    {
                        TryMoveFileWithRetry(metadata.FilePath, destPath, overwrite: true);
                    }
                    catch (IOException)
                    {
                        // Fallback to Copy if Move fails (e.g. source locked)
                        try
                        {
                            // Ensure destination is clear or overwrite
                            if (File.Exists(destPath))
                            {
                                try { File.Delete(destPath); } catch { }
                            }
                            
                            File.Copy(metadata.FilePath, destPath, true);
                            
                            // Try to delete source (best effort), ignore if locked
                            try { File.Delete(metadata.FilePath); } catch { }
                        }
                        catch
                        {
                            // If Copy also fails, check if file exists at destination anyway
                            if (!File.Exists(destPath)) throw;
                        }
                    }
                });

                metadata.FilePath = destPath;
                metadata.Status = "Loaded";
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Moves a file with retry logic for handles held by other processes
        /// </summary>
        private void TryMoveFileWithRetry(string sourceFileName, string destFileName, bool overwrite, int maxRetries = 5, int delayMs = 200)
        {
            Exception lastException = null;

            for (int i = 0; i <= maxRetries; i++)
            {
                try
                {
                    if (File.Exists(destFileName))
                    {
                        if (overwrite)
                        {
                            File.Delete(destFileName);
                        }
                        else
                        {
                            return;
                        }
                    }
                    
                    File.Move(sourceFileName, destFileName);
                    return;
                }
                catch (IOException ex)
                {
                    lastException = ex;
                    if (i < maxRetries)
                    {
                        System.Threading.Thread.Sleep(delayMs);
                    }
                }
            }
            
            throw lastException ?? new IOException($"Failed to move {sourceFileName} to {destFileName}");
        }

        /// <summary>
        /// Moves a package to AllPackages folder
        /// </summary>
        private async Task<bool> MovePackageToAllPackagesAsync(VarMetadata metadata)
        {
            if (_packageFileManager != null)
            {
                // Use robust PackageFileManager if available
                string packageKey = $"{metadata.CreatorName}.{metadata.PackageName}.{metadata.Version}";
                var (success, _) = await _packageFileManager.UnloadPackageAsync(packageKey);
                
                if (success)
                {
                    // Update local metadata to reflect change
                    metadata.Status = "Available";
                    
                    // Get the correct path from PackageFileManager to handle subfolders correctly
                    var info = _packageFileManager.GetPackageFileInfo(packageKey);
                    if (info.Status == "Available" && !string.IsNullOrEmpty(info.AvailablePath))
                    {
                        metadata.FilePath = info.AvailablePath;
                    }
                    else if (!string.IsNullOrEmpty(metadata.FilePath))
                    {
                        // Fallback: assume flat structure if info not available
                        var fileName = Path.GetFileName(metadata.FilePath);
                        metadata.FilePath = Path.Combine(_allPackagesFolder, fileName);
                    }
                }
                return success;
            }

            try
            {
                if (string.IsNullOrEmpty(metadata.FilePath))
                    return false;

                var fileName = Path.GetFileName(metadata.FilePath);
                var destPath = Path.Combine(_allPackagesFolder, fileName);

                if (metadata.FilePath.Equals(destPath, StringComparison.OrdinalIgnoreCase))
                    return true;

                Directory.CreateDirectory(_allPackagesFolder);

                await Task.Run(() =>
                {
                    TryMoveFileWithRetry(metadata.FilePath, destPath, overwrite: true);
                });

                metadata.FilePath = destPath;
                metadata.Status = "Available";
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validates a playlist (checks if all packages exist)
        /// </summary>
        public PlaylistValidationResult ValidatePlaylist(Playlist playlist)
        {
            var result = new PlaylistValidationResult();

            if (playlist == null || !playlist.IsValid())
            {
                result.IsValid = false;
                result.Errors.Add("Invalid playlist");
                return result;
            }

            var missingPackages = new List<string>();
            var packageLoadCount = 0;

            foreach (var packageKey in playlist.PackageKeys)
            {
                if (_packageManager.PackageMetadata.ContainsKey(packageKey))
                {
                    packageLoadCount++;
                }
                else
                {
                    missingPackages.Add(packageKey);
                }
            }

            result.IsValid = missingPackages.Count == 0;
            result.MissingPackages = missingPackages;
            result.PackageCount = playlist.PackageKeys.Count;
            result.AvailablePackageCount = packageLoadCount;

            if (missingPackages.Count > 0)
            {
                result.Errors.Add($"{missingPackages.Count} package(s) not found in the system");
            }

            return result;
        }
    }

    /// <summary>
    /// Result of activating a playlist
    /// </summary>
    public class PlaylistActivationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<string> PackagesToLoad { get; set; } = new List<string>();
        public List<string> PackagesToUnload { get; set; } = new List<string>();
        public int LoadedCount { get; set; }
        public int UnloadedCount { get; set; }
    }

    public class PlaylistActivationProgress
    {
        public string Phase { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public string CurrentPackageKey { get; set; }
    }

    /// <summary>
    /// Result of validating a playlist
    /// </summary>
    public class PlaylistValidationResult
    {
        public bool IsValid { get; set; }
        public int PackageCount { get; set; }
        public int AvailablePackageCount { get; set; }
        public List<string> MissingPackages { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }
}
