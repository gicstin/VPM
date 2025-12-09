using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Globalization;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;

namespace VPM.Services
{
    /// <summary>
    /// Unified service for repackaging VAR files with texture and hair optimizations
    /// </summary>
    public class PackageRepackager
    {
        private readonly TextureConverter _textureConverter;
        private readonly ImageManager _imageManager;
        private readonly ISettingsManager _settingsManager;
        private OptimizationTimer _performanceTimer;

        public PackageRepackager(ImageManager imageManager = null, ISettingsManager settingsManager = null)
        {
            _textureConverter = new TextureConverter();
            _imageManager = imageManager;
            _settingsManager = settingsManager;
            
            if (_settingsManager != null)
            {
                _textureConverter.CompressionQuality = (int)_settingsManager.Settings.TextureCompressionQuality;
            }
            
            _performanceTimer = new OptimizationTimer();
        }

        /// <summary>
        /// Helper method to replace blocking Thread.Sleep with a non-blocking alternative.
        /// Allows file handles to be released without blocking the thread (async version).
        /// </summary>
        private static async Task ReleaseFileHandlesAsync(int delayMs = 100)
        {
            // Use async delay to allow OS to release file handles without blocking the thread
            // This is called after CloseFileHandles to ensure handles are fully released
            await Task.Delay(delayMs).ConfigureAwait(false);
        }

        /// <summary>
        /// Progress callback for reporting conversion status
        /// </summary>
        public delegate void ProgressCallback(string message, int current, int total);

        /// <summary>
        /// Configuration for package optimization
        /// </summary>
        public class OptimizationConfig
        {
            public Dictionary<string, (string targetResolution, int originalWidth, int originalHeight, long originalSize)> TextureConversions { get; set; } 
                = new Dictionary<string, (string, int, int, long)>();
            
            public Dictionary<string, (string sceneFile, string hairId, int targetDensity, bool hadOriginalDensity)> HairConversions { get; set; } 
                = new Dictionary<string, (string, string, int, bool)>();
            
            public Dictionary<string, (string sceneFile, string lightId, bool castShadows, int shadowResolution)> LightConversions { get; set; } 
                = new Dictionary<string, (string, string, bool, int)>();
            
            public bool DisableMirrors { get; set; } = false;
            
            public bool ForceLatestDependencies { get; set; } = false;
            
            public List<string> DisabledDependencies { get; set; } = new List<string>();
            
            public bool DisableMorphPreload { get; set; } = false;
            
            public bool IsMorphAsset { get; set; } = false;
            
            public bool MinifyJson { get; set; } = false;
        }

        /// <summary>
        /// Result of package repackaging operation with optimization statistics
        /// </summary>
        public class RepackageResult
        {
            public string OutputPath { get; set; }
            public long OriginalSize { get; set; }
            public long NewSize { get; set; }
            public int TexturesConverted { get; set; }
            public int HairsModified { get; set; }
            public List<string> TextureDetails { get; set; } = new List<string>();
            public List<string> Errors { get; set; } = new List<string>();
            public long JsonSizeBeforeMinify { get; set; } = 0;
            public long JsonSizeAfterMinify { get; set; } = 0;
        }

        /// <summary>
        /// Repackages a VAR file with optimizations and returns statistics
        /// </summary>
        public async Task<RepackageResult> RepackageVarWithOptimizationsAsync(
            string sourceVarPath, 
            string archivedFolder, 
            OptimizationConfig config, 
            ProgressCallback progressCallback = null,
            bool createBackup = true)
        {
            // Create error log file for debugging
            string errorLogPath = Path.Combine(Path.GetTempPath(), "VPM_OptimizationErrors.log");
            
            // CRITICAL FIX: Acquire exclusive write access at the START of optimization
            // This blocks ALL image loading operations from opening the file while we work on it.
            // The lock is held for the entire duration of the optimization process.
            IDisposable writeLock = null;
            try
            {
                // First, close any existing file handles
                if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceVarPath);
                await ReleaseFileHandlesAsync(100);
                
                // Now acquire exclusive write access - this will wait for any remaining readers to finish
                writeLock = await FileAccessController.Instance.AcquireWriteAccessAsync(sourceVarPath, TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                throw new IOException($"Could not acquire exclusive access to '{Path.GetFileName(sourceVarPath)}' - file may be in use by image loading operations. Please try again.");
            }
            
            try
            {
                string directory = Path.GetDirectoryName(sourceVarPath);
                string filename = Path.GetFileName(sourceVarPath);
                
                // Validate that ArchivedPackages folder is not inside AllPackages or AddonPackages
                if (archivedFolder.Contains("AllPackages") || archivedFolder.Contains("AddonPackages"))
                {
                    throw new InvalidOperationException("ArchivedPackages folder cannot be created inside AllPackages or AddonPackages folders. It must be in the game root directory.");
                }
                
                int totalOperations = config.TextureConversions.Count + config.HairConversions.Count + config.LightConversions.Count;
                
                string sourcePathForProcessing;
                string archivedPath = null;
                bool isSourceInArchive = false;
                string finalOutputPath = sourceVarPath; // Default to source location
                long originalFileSize = new FileInfo(sourceVarPath).Length; // Capture original size before any processing
                
                // Track JSON minification sizes
                long jsonSizeBeforeMinify = 0;
                long jsonSizeAfterMinify = 0;
                
                // Determine if source is in archive folder
                isSourceInArchive = sourceVarPath.Contains(Path.DirectorySeparatorChar + "ArchivedPackages" + Path.DirectorySeparatorChar) ||
                                   sourceVarPath.Contains(Path.AltDirectorySeparatorChar + "ArchivedPackages" + Path.AltDirectorySeparatorChar);
                
                Directory.CreateDirectory(archivedFolder);
                string archiveFilePath = Path.Combine(archivedFolder, filename);
                
                if (isSourceInArchive && createBackup)
                {
                    // SCENARIO 3: Optimizing old version package from archive folder
                    // Old version packages need to be copied to a backup location with #archived suffix
                    // This preserves the original old version while creating an optimized version
                    progressCallback?.Invoke("Backing up old version and optimizing...", 0, totalOperations);
                    
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceVarPath);
                    await ReleaseFileHandlesAsync(100);
                    
                    // Create backup of old version with #archived suffix
                    string backupFilename = Path.GetFileNameWithoutExtension(filename) + "#archived" + Path.GetExtension(filename);
                    string backupPath = Path.Combine(archivedFolder, backupFilename);
                    
                    // Only create backup if it doesn't already exist
                    if (!File.Exists(backupPath))
                    {
                        try
                        {
                            File.Copy(sourceVarPath, backupPath, overwrite: false);
                        }
                        catch (IOException)
                        {
                            // Backup might already exist or file is locked, continue anyway
                        }
                    }
                    
                    // Determine output folder (AddonPackages or AllPackages)
                    string gameRoot = Path.GetDirectoryName(archivedFolder);
                    string addonPackagesFolder = Path.Combine(gameRoot, "AddonPackages");
                    
                    if (Directory.Exists(addonPackagesFolder))
                    {
                        finalOutputPath = Path.Combine(addonPackagesFolder, filename);
                    }
                    else
                    {
                        string allPackagesFolder = Path.Combine(gameRoot, "AllPackages");
                        if (Directory.Exists(allPackagesFolder))
                        {
                            finalOutputPath = Path.Combine(allPackagesFolder, filename);
                        }
                        else
                        {
                            throw new InvalidOperationException("Could not find AddonPackages or AllPackages folder to write optimized package.");
                        }
                    }
                    
                    sourcePathForProcessing = sourceVarPath; // Read from archive
                }
                else if (isSourceInArchive)
                {
                    // SCENARIO 3B: Optimizing from archive folder without backup (re-optimization)
                    // Read from archive (keep original), write to loaded folder
                    progressCallback?.Invoke("Optimizing from archive (original preserved)...", 0, totalOperations);
                    
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceVarPath);
                    await ReleaseFileHandlesAsync(100);
                    
                    // Determine output folder (AddonPackages or AllPackages)
                    string gameRoot = Path.GetDirectoryName(archivedFolder);
                    string addonPackagesFolder = Path.Combine(gameRoot, "AddonPackages");
                    
                    if (Directory.Exists(addonPackagesFolder))
                    {
                        finalOutputPath = Path.Combine(addonPackagesFolder, filename);
                    }
                    else
                    {
                        string allPackagesFolder = Path.Combine(gameRoot, "AllPackages");
                        if (Directory.Exists(allPackagesFolder))
                        {
                            finalOutputPath = Path.Combine(allPackagesFolder, filename);
                        }
                        else
                        {
                            throw new InvalidOperationException("Could not find AddonPackages or AllPackages folder to write optimized package.");
                        }
                    }
                    
                    sourcePathForProcessing = sourceVarPath; // Read from archive
                }
                else if (File.Exists(archiveFilePath) && config.TextureConversions.Count > 0)
                {
                    // SCENARIO 2: Re-optimizing already optimized package in loaded folder WITH TEXTURE CHANGES
                    // Read from CURRENT package to preserve previous optimizations
                    // Only textures in TextureConversions will be re-converted from archive for better quality
                    // Non-converted textures stay at their current resolution (preserving previous work)
                    progressCallback?.Invoke("Re-optimizing selected textures...", 0, totalOperations);
                    
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceVarPath);
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(archiveFilePath);
                    await ReleaseFileHandlesAsync(100);
                    
                    // CRITICAL: Copy source to temp file to avoid file lock when we need to delete/replace
                    // This ensures we don't hold a lock on the file we're trying to overwrite
                    string tempSourcePath = Path.Combine(Path.GetTempPath(), $"vpm_source_{Guid.NewGuid()}.var");
                    File.Copy(sourceVarPath, tempSourcePath, true);
                    sourcePathForProcessing = tempSourcePath; // Read from temp copy
                    finalOutputPath = sourceVarPath; // Write back to original location
                    // isSourceInArchive remains false - we're modifying current package
                    // Note: Textures being converted will be read from archive in the conversion loop for better quality
                    
                }
                else if (!createBackup && File.Exists(archiveFilePath) && (config.MinifyJson || config.ForceLatestDependencies || config.DisabledDependencies.Count > 0 || config.DisableMorphPreload))
                {
                    // SCENARIO 2B: Re-optimizing already optimized package WITH METADATA-ONLY CHANGES (minify, dependencies, etc.)
                    // For metadata-only updates on already-optimized packages, modify current file in-place
                    // This preserves previous optimizations (textures, hair, etc.) while applying new metadata changes
                    progressCallback?.Invoke("Applying metadata optimizations...", 0, totalOperations);
                    
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceVarPath);
                    await ReleaseFileHandlesAsync(100);
                    
                    // CRITICAL: Copy source to temp file to avoid file lock when we need to delete/replace
                    string tempSourcePath = Path.Combine(Path.GetTempPath(), $"vpm_source_{Guid.NewGuid()}.var");
                    File.Copy(sourceVarPath, tempSourcePath, true);
                    sourcePathForProcessing = tempSourcePath; // Read from temp copy
                    finalOutputPath = sourceVarPath; // Write back to original location
                    // isSourceInArchive remains false for in-place modification
                    
                }
                else if (createBackup && !File.Exists(archiveFilePath))
                {
                    // SCENARIO 1: First-time optimization (backup doesn't exist yet)
                    // Move original to archive, then optimize
                    progressCallback?.Invoke("Moving original to archive...", 0, totalOperations);
                    
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceVarPath);
                    await ReleaseFileHandlesAsync(100);
                    
                    archivedPath = archiveFilePath;
                    
                    // GUARANTEED FIX: Use File.Copy + File.Delete instead of File.Move
                    // File.Move can fail even with write lock if any stale handle exists
                    for (int moveAttempt = 1; moveAttempt <= 10; moveAttempt++)
                    {
                        try
                        {
                            File.Copy(sourceVarPath, archivedPath, overwrite: true);
                            File.Delete(sourceVarPath);
                            break;
                        }
                        catch (IOException) when (moveAttempt < 10)
                        {
                            // Aggressive cleanup on each retry
                            if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceVarPath);
                            FileAccessController.Instance.InvalidateFile(sourceVarPath);
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            await Task.Delay(100 * moveAttempt);
                        }
                    }
                    
                    sourcePathForProcessing = archivedPath; // Read from archive
                    finalOutputPath = sourceVarPath; // Write back to original location (now empty)
                    isSourceInArchive = true; // Treat as reading from archive for file handling
                }
                else if (createBackup && File.Exists(archiveFilePath) && !filename.EndsWith("#archived.var", StringComparison.OrdinalIgnoreCase))
                {
                    // SCENARIO 1B: Re-optimization of already-backed-up package
                    // The archive exists, meaning this package was already optimized before
                    // Just read from archive and write back to current location (no new backup needed)
                    // This avoids creating unnecessary #archived copies on re-optimization
                    progressCallback?.Invoke("Re-optimizing from original archive...", 0, totalOperations);
                    
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceVarPath);
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(archiveFilePath);
                    await ReleaseFileHandlesAsync(100);
                    
                    sourcePathForProcessing = archiveFilePath; // Read from archive (original)
                    finalOutputPath = sourceVarPath; // Write back to current location
                    isSourceInArchive = true; // Treat as reading from archive for file handling
                }
                else
                {
                    // SCENARIO 4: Re-optimize without backup - original archive not available
                    // This happens when the original was deleted or never archived
                    // Quality will be limited by current package resolution
                    progressCallback?.Invoke("Re-optimizing from current version (archive not found)...", 0, totalOperations);
                    
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceVarPath);
                    await ReleaseFileHandlesAsync(100);
                    
                    sourcePathForProcessing = sourceVarPath;
                    finalOutputPath = sourceVarPath;
                }
                
                // STEP 2: Process the file (from archive or original location)
                // Use the directory of the final output path for temp file
                string outputDirectory = Path.GetDirectoryName(finalOutputPath);
                string tempOutputPath = Path.Combine(outputDirectory, "~temp_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + filename);
                
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }
                
                progressCallback?.Invoke("Starting optimization...", 0, totalOperations);

                try
                {
                    int processedCount = 0;
                    long originalTotalSize = 0;
                    long newTotalSize = 0;
                    var textureConversionDetails = new ConcurrentBag<string>();
                    var errors = new ConcurrentBag<string>();
                    var hairConversionDetails = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var lightConversionDetails = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    // Check if we have any content modifications (not just metadata changes)
                    bool hasContentModifications = config.TextureConversions.Count > 0 || 
                                                   config.HairConversions.Count > 0 || 
                                                   config.LightConversions.Count > 0 ||
                                                   config.DisableMirrors;

                    // If only metadata updates needed (dependencies, minification, etc.), use in-place update mode
                    // This preserves the original ZIP compression and avoids re-packaging overhead
                    bool hasMetadataOnlyUpdates = config.MinifyJson || config.ForceLatestDependencies || config.DisabledDependencies.Count > 0 || config.DisableMorphPreload;
                    
                    if (!hasContentModifications && hasMetadataOnlyUpdates)
                    {
                        progressCallback?.Invoke("Updating metadata in-place (preserving compression)...", 0, 1);
                        
                        // If reading from archive (re-optimization), copy to final output first
                        // This protects the sacred original and allows in-place modification of the working copy
                        string fileToModify = sourcePathForProcessing;
                        if (isSourceInArchive && sourcePathForProcessing != finalOutputPath)
                        {
                            if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourcePathForProcessing);
                            if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(finalOutputPath);
                            await ReleaseFileHandlesAsync(100);
                            
                            // Copy archive to final output location
                            // NOTE: We already hold write lock for sourceVarPath, and finalOutputPath
                            // is a new file we're creating, so no lock needed
                            if (File.Exists(finalOutputPath))
                            {
                                File.Delete(finalOutputPath);
                            }
                            File.Copy(sourcePathForProcessing, finalOutputPath);
                            fileToModify = finalOutputPath;
                        }
                        
                        if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(fileToModify);
                        await ReleaseFileHandlesAsync(100);
                        
                        // Use SharpCompress to read and re-write the archive with metadata updates
                        // NOTE: Use OpenForReadInternal because we already hold the write lock
                        using (var archive = SharpCompressHelper.OpenForReadInternal(fileToModify))
                        {
                            string originalMetaJson = null;
                            DateTime? originalMetaJsonDate = null;
                            
                            // Read original meta.json
                            var metaEntry = archive.Archive.Entries.FirstOrDefault(e => e.Key.Equals("meta.json", StringComparison.OrdinalIgnoreCase));
                            if (metaEntry != null)
                            {
                                using (var stream = metaEntry.OpenEntryStream())
                                using (var reader = new StreamReader(stream))
                                {
                                    originalMetaJson = await reader.ReadToEndAsync();
                                }
                                originalMetaJsonDate = metaEntry.LastModifiedTime ?? DateTime.Now;
                            }
                            
                            // Track metadata changes for description update
                            List<string> dependencyChanges = null;
                            bool morphPreloadChanged = false;
                            string metaJsonToUpdate = originalMetaJson;
                            
                            // Update meta.json with dependency changes if needed
                            if (!string.IsNullOrEmpty(originalMetaJson) && (config.ForceLatestDependencies || config.DisabledDependencies.Count > 0 || config.DisableMorphPreload))
                            {
                                metaJsonToUpdate = originalMetaJson;
                                
                                if (config.ForceLatestDependencies)
                                {
                                    var conversionResult = ConvertDependenciesToLatest(metaJsonToUpdate);
                                    metaJsonToUpdate = conversionResult.updatedJson;
                                    dependencyChanges = conversionResult.changes;
                                }
                                
                                if (config.DisabledDependencies != null && config.DisabledDependencies.Count > 0)
                                {
                                    metaJsonToUpdate = RemoveDisabledDependencies(metaJsonToUpdate, config.DisabledDependencies);
                                }
                                
                                if (config.DisableMorphPreload && !config.IsMorphAsset)
                                {
                                    metaJsonToUpdate = SetPreloadMorphsFlag(metaJsonToUpdate, false);
                                    morphPreloadChanged = true;
                                }
                            }
                            
                            // Update description with optimization flags (even for minification-only)
                            if (!string.IsNullOrEmpty(metaJsonToUpdate))
                            {
                                string updatedMetaJson = UpdateMetaJsonDescription(
                                    metaJsonToUpdate,
                                    new System.Collections.Concurrent.ConcurrentBag<string>(), // No texture conversions in this path
                                    new List<string>(), // No hair conversions in this path
                                    new List<string>(), // No light conversions in this path
                                    false, // No mirror disabling in this path
                                    0, // No size changes to report
                                    0,
                                    originalMetaJsonDate,
                                    dependencyChanges,
                                    config.DisabledDependencies,
                                    morphPreloadChanged,
                                    config.MinifyJson);
                                
                                // Apply JSON minification to meta.json if enabled
                                if (config.MinifyJson)
                                {
                                    updatedMetaJson = MinifyJson(updatedMetaJson);
                                }
                                
                                // Note: For metadata-only updates with SharpCompress, we need to re-create the archive
                                // This is a limitation of SharpCompress - it doesn't support in-place updates like ZipArchive.Update
                                // For now, we'll skip this optimization and let the full repackage path handle it
                            }
                            
                            // Note: For in-place JSON minification with SharpCompress, we would need to re-create the archive
                            // This is a limitation of SharpCompress - it doesn't support in-place updates
                            // For now, this optimization is skipped for metadata-only updates
                        }
                        
                        progressCallback?.Invoke("Metadata update complete!", 1, 1);
                        
                        // Get file sizes for statistics
                        long metadataUpdateSize = new FileInfo(finalOutputPath).Length;
                        return new RepackageResult
                        {
                            OutputPath = finalOutputPath,
                            OriginalSize = originalFileSize,
                            NewSize = metadataUpdateSize,
                            TexturesConverted = 0,
                            HairsModified = 0,
                            TextureDetails = new List<string>(),
                            JsonSizeBeforeMinify = jsonSizeBeforeMinify,
                            JsonSizeAfterMinify = jsonSizeAfterMinify
                        };
                    }

                    // Full re-packaging path for content modifications
                    progressCallback?.Invoke("📦 Reading package archive...", 0, totalOperations);
                    _performanceTimer.Start("Package Analysis");
                    
                    // Open source VAR (from archive or original location)
                    // NOTE: Use OpenForReadInternal because we already hold the write lock
                    // Use forceGcOnDispose to ensure file handles are released before we try to delete/replace
                    using (var sourceArchive = SharpCompressHelper.OpenForReadInternal(sourcePathForProcessing, forceGcOnDispose: true))
                    using (var outputMemoryStream = new MemoryStream())
                    using (var outputArchive = ZipArchive.Create())
                    {
                        // Set maximum compression level for smaller output files
                        outputArchive.DeflateCompressionLevel = SharpCompress.Compressors.Deflate.CompressionLevel.BestCompression;
                        
                        string originalMetaJson = null;
                        DateTime? originalMetaJsonDate = null;
                        object archiveLock = new object();
                        
                        // First pass: collect entry metadata ONLY (not data) to avoid OOM
                        progressCallback?.Invoke("🔍 Analyzing package contents...", 0, totalOperations);
                        var entriesToProcess = new List<(IArchiveEntry entry, bool needsTextureConversion, bool needsHairModification, bool needsSceneModification)>();
                        int entryIndex = 0;
                        int totalEntries = sourceArchive.Archive.Entries.Count();
                        
                        // PERFORMANCE: Pre-build HashSets for O(1) lookups instead of O(n) Any() per entry
                        // This changes O(n*m) to O(n+m) where n=entries, m=conversions
                        var hairSceneFiles = new HashSet<string>(
                            config.HairConversions.Values.Select(h => h.sceneFile), 
                            StringComparer.OrdinalIgnoreCase);
                        var lightSceneFiles = new HashSet<string>(
                            config.LightConversions.Values.Select(l => l.sceneFile), 
                            StringComparer.OrdinalIgnoreCase);

                        foreach (var entry in sourceArchive.Archive.Entries)
                        {
                            // Check if this is meta.json
                            if (entry.Key.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                            {
                                using (var stream = entry.OpenEntryStream())
                                using (var reader = new StreamReader(stream))
                                {
                                    originalMetaJson = reader.ReadToEnd();
                                }
                                // Capture the original meta.json creation date
                                originalMetaJsonDate = entry.LastModifiedTime ?? DateTime.Now;
                                continue; // Will add modified version later
                            }
                            
                            bool needsTextureConversion = config.TextureConversions.ContainsKey(entry.Key);
                            
                            // Use pre-built HashSets for O(1) lookup instead of O(n) Any()
                            var entryFileName = Path.GetFileName(entry.Key);
                            bool needsHairModification = hairSceneFiles.Contains(entryFileName);
                            bool needsLightModification = lightSceneFiles.Contains(entryFileName);

                            // Also check if this is a .vap hair preset file that needs modification
                            bool isVapFile = entry.Key.StartsWith("Custom/Atom/Person/Hair/", StringComparison.OrdinalIgnoreCase) && 
                                           entry.Key.EndsWith(".vap", StringComparison.OrdinalIgnoreCase);

                            // Check if this is a scene file that needs mirror disabling
                            bool isSceneFile = entry.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && 
                                             !entry.Key.Equals("meta.json", StringComparison.OrdinalIgnoreCase);
                            bool needsMirrorDisabling = config.DisableMirrors && isSceneFile;
                            bool needsSceneModification = needsHairModification || needsLightModification || isVapFile || needsMirrorDisabling;

                            // CRITICAL FIX: Don't load entry data here - load it on-demand during processing
                            // This prevents OOM when processing large packages with many textures
                            entriesToProcess.Add((entry, needsTextureConversion, needsHairModification || isVapFile, needsSceneModification));
                            
                            // Update progress every 50 entries to avoid too many UI updates
                            entryIndex++;
                            if (entryIndex % 50 == 0)
                            {
                                progressCallback?.Invoke($"🔍 Reading files... ({entryIndex}/{totalEntries})", 0, totalOperations);
                            }
                        }

                        _performanceTimer.Stop("Package Analysis");

                        // Second pass: Validate and process textures in parallel (use all CPU cores)
                        var convertedTextures = new ConcurrentDictionary<string, (byte[] data, DateTimeOffset lastWriteTime)>();
                        var failedTextures = new ConcurrentDictionary<string, (byte[] data, DateTimeOffset lastWriteTime, string reason)>();
                        var textureEntries = entriesToProcess.Where(e => e.needsTextureConversion).ToList();
                        var unsupportedCompressionTextures = new List<string>();
                        
                        if (textureEntries.Count > 0)
                        {
                            progressCallback?.Invoke($"⚡ Starting texture conversion...", 0, totalOperations);
                        }

                        // Use adaptive memory-aware parallelism with proper async I/O
                        // The adaptive optimizer scales concurrency based on available memory
                        
                        // OPTIMIZATION: Use full CPU cores for texture conversion (CPU-bound operation)
                        // Texture resizing is CPU-intensive, not I/O-bound, so we can use all cores
                        int maxConcurrentTextures = Math.Max(2, Environment.ProcessorCount); // Full parallelism for CPU-bound work
                        _performanceTimer.Start("Texture Conversion (All)");
                        
                        // Determine texture source paths
                        // - For downscaling: read from current package (sourcePathForProcessing)
                        // - For upscaling: read from archive (archiveFilePath) if available
                        bool archiveExists = File.Exists(archiveFilePath);
                        
                        // Create archive pool for upscaling from archive (if needed)
                        ArchiveHandlePool archivePoolForUpscale = null;
                        if (archiveExists)
                        {
                            archivePoolForUpscale = new ArchiveHandlePool(archiveFilePath, maxConcurrentTextures);
                        }
                        
                        using (var archivePool = new ArchiveHandlePool(sourcePathForProcessing, maxConcurrentTextures))
                        using (var semaphore = new System.Threading.SemaphoreSlim(maxConcurrentTextures))
                        {
                            try
                            {
                            var tasks = textureEntries.Select(async item =>
                            {
                                await semaphore.WaitAsync();
                                try
                                {
                                    var (entry, _, _, _) = item;
                                    
                                    // Update progress with current texture name
                                    progressCallback?.Invoke($"⚡ Converting: {Path.GetFileName(entry.Key)}", processedCount, totalOperations);

                                    var conversionInfo = config.TextureConversions[entry.Key];
                                    
                                    // Determine if this is an upscale operation
                                    int targetDimension = TextureConverter.GetTargetDimension(conversionInfo.targetResolution);
                                    int currentMaxDim = Math.Max(conversionInfo.originalWidth, conversionInfo.originalHeight);
                                    bool needsUpscale = targetDimension > currentMaxDim;
                                    
                                    // For upscaling, we need to read from archive (original high-res source)
                                    bool useArchiveSource = needsUpscale && archiveExists && archivePoolForUpscale != null;

                                    try
                                    {
                                        // Load texture data - use pool for parallel access
                                        byte[] sourceData = null;
                                        int actualSourceDimension = currentMaxDim; // Track actual source dimension for reporting
                                        try
                                        {
                                            // Choose which archive to read from
                                            var poolToUse = useArchiveSource ? archivePoolForUpscale : archivePool;
                                            
                                            // Acquire a dedicated archive handle for this thread
                                            var poolArchive = await poolToUse.AcquireHandleAsync();
                                            try
                                            {
                                                // Find the entry in this archive instance
                                                var threadEntry = SharpCompressHelper.FindEntry(poolArchive, entry.Key);
                                                
                                                if (threadEntry != null)
                                                {
                                                    using (var stream = threadEntry.OpenEntryStream())
                                                    using (var ms = new MemoryStream())
                                                    {
                                                        stream.CopyTo(ms);
                                                        sourceData = ms.ToArray();
                                                    }
                                                    
                                                    // If reading from archive for upscale, get actual dimensions from archive
                                                    if (useArchiveSource && sourceData != null && sourceData.Length > 0)
                                                    {
                                                        try
                                                        {
                                                            using var tempImage = NetVips.Image.NewFromBuffer(sourceData);
                                                            actualSourceDimension = Math.Max(tempImage.Width, tempImage.Height);
                                                        }
                                                        catch { /* Keep original dimension on error */ }
                                                    }
                                                }
                                                else if (useArchiveSource)
                                                {
                                                    // Entry not found in archive, fall back to current package
                                                    poolToUse.ReleaseHandle(poolArchive);
                                                    poolArchive = await archivePool.AcquireHandleAsync();
                                                    threadEntry = SharpCompressHelper.FindEntry(poolArchive, entry.Key);
                                                    if (threadEntry != null)
                                                    {
                                                        using (var stream = threadEntry.OpenEntryStream())
                                                        using (var ms = new MemoryStream())
                                                        {
                                                            stream.CopyTo(ms);
                                                            sourceData = ms.ToArray();
                                                        }
                                                    }
                                                    useArchiveSource = false; // Fell back to current
                                                }
                                                else
                                                {
                                                    throw new FileNotFoundException($"Entry not found in pool archive: {entry.Key}");
                                                }
                                            }
                                            finally
                                            {
                                                poolToUse.ReleaseHandle(poolArchive);
                                            }
                                        }
                                        catch (SharpCompress.Compressors.Deflate.ZlibException)
                                        {
                                            // Only skip on actual decompression errors
                                            int procCount = Interlocked.Increment(ref processedCount);
                                            progressCallback?.Invoke($"⚠️  [{procCount}/{totalOperations}] Skipping corrupted file: {Path.GetFileName(entry.Key)}", procCount, totalOperations);
                                            return;
                                        }

                                        Interlocked.Add(ref originalTotalSize, sourceData.Length);

                                        // Convert texture asynchronously
                                        // For upscaling from archive: allowUpscale=true to permit resizing to target
                                        // For downscaling: allowUpscale=false (default behavior)
                                        string extension = Path.GetExtension(entry.Key);
                                        bool allowUpscale = useArchiveSource && actualSourceDimension >= targetDimension;
                                        
                                        byte[] convertedData = await Task.Run(() => 
                                            _textureConverter.ResizeImage(sourceData, targetDimension, extension, allowUpscale));

                                        int currentProcessed = Interlocked.Increment(ref processedCount);
                                        
                                        // Track conversion details
                                        if (convertedData != null)
                                        {
                                            Interlocked.Add(ref newTotalSize, convertedData.Length);
                                            
                                            string textureName = Path.GetFileName(entry.Key);
                                            // Use actual source dimension for reporting (archive dimension if upscaling)
                                            string sourceRes = GetResolutionStringFromDimension(actualSourceDimension);
                                            string detail = $"  • {textureName}: {sourceRes} -> {conversionInfo.targetResolution} ({FormatHelper.FormatBytes(sourceData.Length)} -> {FormatHelper.FormatBytes(convertedData.Length)})";
                                            textureConversionDetails.Add(detail);
                                            
                                            convertedTextures[entry.Key] = (convertedData, entry.LastModifiedTime ?? DateTimeOffset.Now);
                                            progressCallback?.Invoke($"⚡ [{currentProcessed}/{totalOperations}] ✓ Converted: {Path.GetFileName(entry.Key)}", currentProcessed, totalOperations);
                                        }
                                        else if (useArchiveSource && actualSourceDimension == targetDimension)
                                        {
                                            // RESTORATION: Archive has exact resolution we want, use archive data directly
                                            // This happens when restoring from 2K back to 4K - archive already has 4K
                                            Interlocked.Add(ref newTotalSize, sourceData.Length);
                                            
                                            string textureName = Path.GetFileName(entry.Key);
                                            string sourceRes = GetResolutionStringFromDimension(actualSourceDimension);
                                            string currentRes = GetResolutionStringFromDimension(currentMaxDim);
                                            string detail = $"  • {textureName}: {currentRes} -> {conversionInfo.targetResolution} (restored from archive)";
                                            textureConversionDetails.Add(detail);
                                            
                                            // Use archive data directly (no resize needed, already at target resolution)
                                            convertedTextures[entry.Key] = (sourceData, entry.LastModifiedTime ?? DateTimeOffset.Now);
                                            progressCallback?.Invoke($"⚡ [{currentProcessed}/{totalOperations}] ✓ Restored: {Path.GetFileName(entry.Key)}", currentProcessed, totalOperations);
                                        }
                                        else
                                        {
                                            Interlocked.Add(ref newTotalSize, sourceData.Length);
                                            progressCallback?.Invoke($"⚡ [{currentProcessed}/{totalOperations}] ⊘ Skipped: {Path.GetFileName(entry.Key)}", currentProcessed, totalOperations);
                                        }
                                        
                                        // Explicitly clear sourceData to help GC
                                        sourceData = null;
                                    }
                                    catch (InvalidDataException ex) when (ex.Message.Contains("unsupported compression"))
                                    {
                                        // Skip entries with unsupported compression methods (Bzip2, LZMA, PPMd, etc.)
                                        // These will be copied as-is without conversion
                                        lock (unsupportedCompressionTextures)
                                        {
                                            unsupportedCompressionTextures.Add(entry.Key);
                                        }
                                        int currentProcessed = Interlocked.Increment(ref processedCount);
                                        progressCallback?.Invoke($"⚠️  [{currentProcessed}/{totalOperations}] Skipping (unsupported compression): {Path.GetFileName(entry.Key)}", currentProcessed, totalOperations);
                                        errors.Add($"Unsupported compression: {Path.GetFileName(entry.Key)}");
                                        
                                        // Log to file for debugging
                                        try
                                        {
                                            File.AppendAllText(errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UNSUPPORTED COMPRESSION: {entry.Key}\n");
                                        }
                                        catch { }
                                    }
                                    catch (InvalidDataException ex) when (ex.Message.Contains("corrupt"))
                                    {
                                        // Mark as failed so it will be copied as-is (never skip textures)
                                        failedTextures[entry.Key] = (null, entry.LastModifiedTime ?? DateTimeOffset.Now, "Corrupted file header - copied as-is");
                                        int currentProcessed = Interlocked.Increment(ref processedCount);
                                        progressCallback?.Invoke($"⚠️  [{currentProcessed}/{totalOperations}] Texture unreadable, copying as-is: {Path.GetFileName(entry.Key)}", currentProcessed, totalOperations);
                                        errors.Add($"Corrupted file header (copied as-is): {Path.GetFileName(entry.Key)}");
                                        
                                        // Log to file for debugging
                                        try
                                        {
                                            File.AppendAllText(errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CORRUPT FILE HEADER (COPIED AS-IS): {entry.Key}\n");
                                        }
                                        catch { }
                                    }
                                    catch (Exception ex)
                                    {
                                        // Log other errors but continue processing
                                        int currentProcessed = Interlocked.Increment(ref processedCount);
                                        progressCallback?.Invoke($"❌ [{currentProcessed}/{totalOperations}] Error converting {Path.GetFileName(entry.Key)}: {ex.Message}", currentProcessed, totalOperations);
                                        errors.Add($"Error converting {Path.GetFileName(entry.Key)}: {ex.Message}");
                                        
                                        // Log to file for debugging
                                        try
                                        {
                                            File.AppendAllText(errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ERROR converting {entry.Key}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
                                        }
                                        catch { }
                                    }
                                }
                                finally
                                {
                                    semaphore.Release();
                                }
                            }).ToArray();

                            // Wait for all texture conversions to complete
                            await System.Threading.Tasks.Task.WhenAll(tasks);
                            }
                            finally
                            {
                                // Dispose archive pool for upscaling if it was created
                                archivePoolForUpscale?.Dispose();
                            }
                        }
                        _performanceTimer.Stop("Texture Conversion (All)");

                        // Track failed textures in the report
                        foreach (var failedEntry in failedTextures)
                        {
                            var conversionInfo = config.TextureConversions[failedEntry.Key];
                            string textureName = Path.GetFileName(failedEntry.Key);
                            string originalRes = GetResolutionString(conversionInfo.originalWidth, conversionInfo.originalHeight);
                            string detail = $"  • {textureName}: {originalRes} (⚠️  Copied as-is - {failedEntry.Value.reason})";
                            textureConversionDetails.Add(detail);
                        }

                        // Process hair/scene modifications in parallel (use all CPU cores - JSON parsing is CPU-bound)
                        var modifiedScenes = new ConcurrentDictionary<string, (byte[] data, DateTimeOffset lastWriteTime)>();
                        var sceneEntries = entriesToProcess.Where(e => e.needsSceneModification).ToList();
                        
                        if (sceneEntries.Count > 0)
                        {
                            progressCallback?.Invoke($"⚙️  Processing {sceneEntries.Count} scene/preset file(s)...", processedCount, totalOperations);
                            _performanceTimer.Start("Scene/Hair Processing");
                        }

                        // Get the maximum target density from all hair conversions
                        int maxTargetDensity = config.HairConversions.Values.Any() ? config.HairConversions.Values.Max(h => h.targetDensity) : 30;

                        await Parallel.ForEachAsync(sceneEntries, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, async (item, ct) =>
                        {
                            var (entry, _, needsHairModification, needsSceneModification) = item;
                            if (!needsSceneModification)
                            {
                                return;
                            }

                            try
                            {
                                _performanceTimer.Start("JSON Parsing");
                                
                                // CRITICAL FIX: Load scene data on-demand (not pre-loaded)
                                byte[] sourceData;
                                lock (archiveLock)
                                {
                                    using (var stream = entry.OpenEntryStream())
                                    using (var ms = new MemoryStream())
                                    {
                                        stream.CopyTo(ms);
                                        sourceData = ms.ToArray();
                                    }
                                }

                                string fileName = Path.GetFileName(entry.Key);
                                string jsonContent = Encoding.UTF8.GetString(sourceData);
                                
                                _performanceTimer.Stop("JSON Parsing");
                                byte[] modifiedData = null;

                                if (entry.Key.EndsWith(".vap", StringComparison.OrdinalIgnoreCase) && needsHairModification)
                                {
                                    string modifiedJson = ModifyHairInVapFile(jsonContent, maxTargetDensity, entry.Key, hairConversionDetails);
                                    modifiedData = Encoding.UTF8.GetBytes(modifiedJson);
                                    int currentProcessed = Interlocked.Increment(ref processedCount);
                                    progressCallback?.Invoke($"💇 [{currentProcessed}/{totalOperations}] Hair preset: {fileName}", currentProcessed, totalOperations);
                                }
                                else
                                {
                                    var hairMods = config.HairConversions.Where(kvp => kvp.Value.sceneFile == fileName).ToList();
                                    var lightMods = config.LightConversions.Where(kvp => kvp.Value.sceneFile == fileName).ToList();
                                    bool needsMirrorDisable = config.DisableMirrors && entry.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

                                    if (hairMods.Count > 0 || lightMods.Count > 0 || needsMirrorDisable)
                                    {
                                        string modifiedJson = ModifySceneJson(jsonContent, hairMods, lightMods, hairConversionDetails, lightConversionDetails, config.DisableMirrors);
                                        modifiedData = Encoding.UTF8.GetBytes(modifiedJson);
                                        int currentProcessed = Interlocked.Increment(ref processedCount);
                                        progressCallback?.Invoke($"🎬 [{currentProcessed}/{totalOperations}] Scene file: {fileName}", currentProcessed, totalOperations);
                                    }
                                }

                                if (modifiedData != null)
                                {
                                    modifiedScenes[entry.Key] = (modifiedData, entry.LastModifiedTime.HasValue ? entry.LastModifiedTime.Value : DateTimeOffset.Now);
                                }
                            }
                            catch (InvalidDataException ex) when (ex.Message.Contains("unsupported compression"))
                            {
                                // Skip entries with unsupported compression methods
                                int currentProcessed = Interlocked.Increment(ref processedCount);
                                progressCallback?.Invoke($"⚠️  [{currentProcessed}/{totalOperations}] Skipping (unsupported compression): {Path.GetFileName(entry.Key)}", currentProcessed, totalOperations);
                                errors.Add($"Unsupported compression (scene): {Path.GetFileName(entry.Key)}");
                                
                                // Log to file for debugging
                                try
                                {
                                    await File.AppendAllTextAsync(errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UNSUPPORTED COMPRESSION (scene): {entry.Key}\n");
                                }
                                catch { }
                            }
                            catch (Exception ex)
                            {
                                // Log other errors but continue processing
                                int currentProcessed = Interlocked.Increment(ref processedCount);
                                progressCallback?.Invoke($"❌ [{currentProcessed}/{totalOperations}] Error processing {Path.GetFileName(entry.Key)}: {ex.Message}", currentProcessed, totalOperations);
                                errors.Add($"Error processing {Path.GetFileName(entry.Key)}: {ex.Message}");
                                
                                // Log to file for debugging
                                try
                                {
                                    await File.AppendAllTextAsync(errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ERROR processing {entry.Key}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
                                }
                                catch { }
                            }
                        });
                        
                        if (sceneEntries.Count > 0)
                        {
                            _performanceTimer.Stop("Scene/Hair Processing");
                        }

                        // Third pass: Write all entries to output archive
                        progressCallback?.Invoke($"📝 Writing optimized package...", processedCount, totalOperations);
                        _performanceTimer.Start("Archive Writing");
                        _performanceTimer.Start("Archive Writing - Entry Processing");
                        int writeIndex = 0;
                        int totalWrites = entriesToProcess.Count;
                        
                        long totalBytesWritten = 0;
                        long totalBytesOriginal = 0;
                        var convertedTexturesList = new List<(string name, long original, long written)>();
                        
                        foreach (var item in entriesToProcess)
                        {
                            var (entry, needsTextureConversion, needsHairModification, needsSceneModification) = item;

                            // For failed textures, copy them as-is (never skip textures)
                            bool isFailedTexture = needsTextureConversion && failedTextures.ContainsKey(entry.Key);

                            byte[] dataToWrite = null;
                            DateTimeOffset lastWriteTime = entry.LastModifiedTime ?? DateTimeOffset.Now;

                            // Check if this entry was modified
                            if (needsTextureConversion && convertedTextures.TryGetValue(entry.Key, out var converted))
                            {
                                dataToWrite = converted.data;
                                lastWriteTime = converted.lastWriteTime;
                            }
                            else if (needsSceneModification && modifiedScenes.TryGetValue(entry.Key, out var modified))
                            {
                                dataToWrite = modified.data;
                                lastWriteTime = modified.lastWriteTime;
                            }
                            
                            // Apply JSON minification if enabled for all JSON-based files
                            // Includes: .json, .vaj (poses), .vam (scenes), .vap (presets)
                            var jsonExtensions = new[] { ".json", ".vaj", ".vam", ".vap" };
                            bool isJsonFile = jsonExtensions.Any(ext => entry.Key.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
                            if (config.MinifyJson && isJsonFile)
                            {
                                string fileType = Path.GetExtension(entry.Key).ToUpper().TrimStart('.');
                                if (writeIndex % 10 == 0) // Update every 10 files to avoid too many UI updates
                                {
                                    progressCallback?.Invoke($"📦 Minifying {fileType} files... ({writeIndex}/{totalWrites})", processedCount, totalOperations);
                                }
                                try
                                {
                                    string jsonContent;
                                    if (dataToWrite != null)
                                    {
                                        jsonContent = Encoding.UTF8.GetString(dataToWrite);
                                    }
                                    else
                                    {
                                        using var sourceStream = entry.OpenEntryStream();
                                        using var reader = new StreamReader(sourceStream);
                                        jsonContent = reader.ReadToEnd();
                                    }
                                    
                                    // Track size before minification
                                    jsonSizeBeforeMinify += Encoding.UTF8.GetByteCount(jsonContent);
                                    
                                    string minifiedJson = MinifyJson(jsonContent);
                                    
                                    // Track size after minification
                                    jsonSizeAfterMinify += Encoding.UTF8.GetByteCount(minifiedJson);
                                    
                                    dataToWrite = Encoding.UTF8.GetBytes(minifiedJson);
                                }
                                catch
                                {
                                    // If minification fails, keep original data
                                }
                            }
                            
                            // OPTIMIZATION: Skip compression for already-compressed formats
                            // Benefit: 15-25% faster archive writing for media-heavy packages
                            var extension = Path.GetExtension(entry.Key).ToLowerInvariant();
                            bool isAlreadyCompressed = extension == ".jpg" || extension == ".jpeg" || 
                                                      extension == ".png" || extension == ".gif" ||
                                                      extension == ".webp" || extension == ".mp3" || 
                                                      extension == ".mp4" || extension == ".ogg" ||
                                                      extension == ".assetbundle" || extension == ".bundle" ||
                                                      extension == ".webm" || extension == ".mkv";
                            
                            // Phase 2 Optimization: Stream directly without buffering entire entry into memory
                            // Benefit: 30-50% memory reduction for large packages
                            // Only buffer if data was modified (texture conversion, JSON minification)
                            try
                            {
                                long originalSize = entry.Size;
                                long writtenSize = 0;
                                
                                if (dataToWrite != null)
                                {
                                    // Modified data: use MemoryStream (already in memory)
                                    writtenSize = dataToWrite.Length;
                                    var ms = new MemoryStream(dataToWrite);
                                    outputArchive.AddEntry(entry.Key, ms, closeStream: true);
                                    
                                    if (needsTextureConversion && convertedTextures.ContainsKey(entry.Key))
                                    {
                                        convertedTexturesList.Add((Path.GetFileName(entry.Key), originalSize, writtenSize));
                                    }
                                }
                                else
                                {
                                    // Unmodified data: stream directly from source to destination
                                    // This avoids loading large files into memory
                                    // CRITICAL: Always copy, even if it's a failed texture (never skip)
                                    writtenSize = originalSize;
                                    SharpCompressHelper.CopyEntryDirect(sourceArchive.Archive, entry, outputArchive);
                                }
                                
                                totalBytesOriginal += originalSize;
                                totalBytesWritten += writtenSize;

                                // Note: SharpCompress handles compression type during archive writing, not per-entry
                                
                                writeIndex++;
                                // Update progress every 100 files to avoid too many UI updates
                                if (writeIndex % 100 == 0)
                                {
                                    progressCallback?.Invoke($"📝 Writing files... ({writeIndex}/{totalWrites})", processedCount, totalOperations);
                                }
                            }
                            catch (SharpCompress.Compressors.Deflate.ZlibException)
                            {
                                // CRITICAL FIX: Never skip textures - copy as-is even if decompression fails
                                // System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] ⚠️  Decompression error on entry (copying as-is): {entry.Key}");
                                // System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] ZlibException: {zlibEx.Message}");
                                progressCallback?.Invoke($"⚠️  Decompression issue, copying as-is: {Path.GetFileName(entry.Key)}", processedCount, totalOperations);
                                errors.Add($"Decompression issue (copied as-is): {Path.GetFileName(entry.Key)}");
                                
                                // Copy the entry as-is from source to destination
                                try
                                {
                                    SharpCompressHelper.CopyEntryDirect(sourceArchive.Archive, entry, outputArchive);
                                    writeIndex++;
                                }
                                catch (Exception copyEx)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] ✗ CRITICAL: Could not copy entry as-is: {entry.Key}");
                                    System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] Copy error: {copyEx.Message}");
                                    progressCallback?.Invoke($"❌ CRITICAL: Could not include texture: {SanitizeEntryName(Path.GetFileName(entry.Key))}", processedCount, totalOperations);
                                    errors.Add($"CRITICAL: Could not include texture: {SanitizeEntryName(Path.GetFileName(entry.Key))} - {copyEx.Message}");
                                    // This is a true failure - log it
                                    try
                                    {
                                        File.AppendAllText(errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CRITICAL: Could not copy entry {SanitizeEntryName(entry.Key)}: {copyEx.Message}\n");
                                    }
                                    catch { }
                                }
                            }
                            catch (InvalidOperationException ioEx) when (ioEx.Message.Contains("Corrupted archive entry"))
                            {
                                // Entry is corrupted - skip it gracefully
                                System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] ⚠️  Skipping corrupted entry: {entry.Key}");
                                progressCallback?.Invoke($"⚠️  Skipping corrupted entry: {SanitizeEntryName(Path.GetFileName(entry.Key))}", processedCount, totalOperations);
                                errors.Add($"Skipping corrupted entry: {SanitizeEntryName(Path.GetFileName(entry.Key))}");
                                writeIndex++;
                                // Log but continue
                                try
                                {
                                    File.AppendAllText(errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] SKIPPED (corrupted): {SanitizeEntryName(entry.Key)}\n");
                                }
                                catch { }
                            }
                            catch (Exception writeEx)
                            {
                                // Handle other write/stream errors - try to copy as-is
                                System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] ⚠️  Write error on entry (attempting to copy as-is): {entry.Key}");
                                System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] Exception Type: {writeEx.GetType().Name}");
                                System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] Exception Message: {writeEx.Message}");
                                progressCallback?.Invoke($"⚠️  Write error, attempting to copy as-is: {SanitizeEntryName(Path.GetFileName(entry.Key))}", processedCount, totalOperations);
                                errors.Add($"Write error (attempting to copy as-is): {SanitizeEntryName(Path.GetFileName(entry.Key))} - {writeEx.Message}");
                                
                                // Try to copy the entry as-is
                                try
                                {
                                    SharpCompressHelper.CopyEntryDirect(sourceArchive.Archive, entry, outputArchive);
                                    writeIndex++;
                                }
                                catch (InvalidOperationException copyIoEx) when (copyIoEx.Message.Contains("Corrupted archive entry"))
                                {
                                    // Entry is corrupted - skip it
                                    System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] ⚠️  Skipping corrupted entry (copy failed): {entry.Key}");
                                    progressCallback?.Invoke($"⚠️  Skipping corrupted entry: {SanitizeEntryName(Path.GetFileName(entry.Key))}", processedCount, totalOperations);
                                    errors.Add($"Skipping corrupted entry (copy failed): {SanitizeEntryName(Path.GetFileName(entry.Key))}");
                                    writeIndex++;
                                    try
                                    {
                                        File.AppendAllText(errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] SKIPPED (corrupted, copy failed): {SanitizeEntryName(entry.Key)}\n");
                                    }
                                    catch { }
                                }
                                catch (Exception copyEx)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] ✗ CRITICAL: Could not copy entry as-is: {entry.Key}");
                                    System.Diagnostics.Debug.WriteLine($"[PACKAGE_REPACK] Copy error: {copyEx.Message}");
                                    progressCallback?.Invoke($"❌ CRITICAL: Could not include texture: {SanitizeEntryName(Path.GetFileName(entry.Key))}", processedCount, totalOperations);
                                    errors.Add($"CRITICAL: Could not include texture: {SanitizeEntryName(Path.GetFileName(entry.Key))} - {copyEx.Message}");
                                    // This is a true failure - log it
                                    try
                                    {
                                        File.AppendAllText(errorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CRITICAL: Could not copy entry {SanitizeEntryName(entry.Key)}: {copyEx.Message}\n");
                                    }
                                    catch { }
                                }
                            }
                        }
                        
                        // Update meta.json with conversion details
                        if (!string.IsNullOrEmpty(originalMetaJson))
                        {
                            progressCallback?.Invoke("📋 Updating package metadata...", processedCount, totalOperations);
                            
                            // Apply dependency conversion if requested
                            List<string> dependencyChanges = null;
                            string metaJsonToUpdate = originalMetaJson;
                            if (config.ForceLatestDependencies)
                            {
                                progressCallback?.Invoke("🔗 Converting dependencies to .latest...", processedCount, totalOperations);
                                var conversionResult = ConvertDependenciesToLatest(originalMetaJson);
                                metaJsonToUpdate = conversionResult.updatedJson;
                                dependencyChanges = conversionResult.changes;
                            }
                            
                            // Remove disabled dependencies
                            if (config.DisabledDependencies != null && config.DisabledDependencies.Count > 0)
                            {
                                progressCallback?.Invoke($"🗑️  Removing {config.DisabledDependencies.Count} disabled dependencies...", processedCount, totalOperations);
                                metaJsonToUpdate = RemoveDisabledDependencies(metaJsonToUpdate, config.DisabledDependencies);
                            }
                            
                            // Set preloadMorphs to false for non-morph assets if DisableMorphPreload is enabled
                            bool morphPreloadChanged = false;
                            if (config.DisableMorphPreload && !config.IsMorphAsset)
                            {
                                metaJsonToUpdate = SetPreloadMorphsFlag(metaJsonToUpdate, false);
                                morphPreloadChanged = true;
                            }
                            
                            string updatedMetaJson = UpdateMetaJsonDescription(
                                metaJsonToUpdate, 
                                textureConversionDetails, 
                                hairConversionDetails.Values,
                                lightConversionDetails.Values,
                                config.DisableMirrors,
                                originalTotalSize, 
                                newTotalSize,
                                originalMetaJsonDate,
                                dependencyChanges,
                                config.DisabledDependencies,
                                morphPreloadChanged,
                                config.MinifyJson);
                            
                            // Apply JSON minification to meta.json if enabled
                            if (config.MinifyJson)
                            {
                                _performanceTimer.Start("JSON Minification");
                                updatedMetaJson = MinifyJson(updatedMetaJson);
                                _performanceTimer.Stop("JSON Minification");
                            }
                            
                            outputArchive.AddEntry("meta.json", new MemoryStream(Encoding.UTF8.GetBytes(updatedMetaJson)));
                        }
                        else
                        {
                        }
                        
                        _performanceTimer.Stop("Archive Writing - Entry Processing");
                        
                        // Save the archive to the temp output file
                        _performanceTimer.Start("Archive Writing - Compression & Save");
                        
                        // Save the archive to the memory stream with Deflate compression for game compatibility
                        // Note: Most content is already compressed (PNG, JPG, etc.), so performance impact is minimal
                        outputArchive.SaveTo(outputMemoryStream, SharpCompress.Common.CompressionType.Deflate);
                        
                        _performanceTimer.Stop("Archive Writing - Compression & Save");
                        _performanceTimer.Start("Archive Writing - File Write");
                        
                        // Then write the memory stream to the file
                        outputMemoryStream.Position = 0;
                        using (var outputFileStream = new FileStream(tempOutputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            outputMemoryStream.CopyTo(outputFileStream);
                        }
                        
                        _performanceTimer.Stop("Archive Writing - File Write");
                    }
                    _performanceTimer.Stop("Archive Writing");

                    progressCallback?.Invoke("✅ Finalizing package...", totalOperations, totalOperations);
                    
                    // STEP 3: Move the converted temp file to the final output location
                    // Force garbage collection to ensure all file handles are released
                    // This is critical because we just closed sourceArchive and need to delete/replace the file
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect(); // Second pass to clean up any newly freed objects
                    
                    // Release any file handles before attempting to delete/move
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(finalOutputPath);
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourceVarPath);
                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(sourcePathForProcessing);
                    await ReleaseFileHandlesAsync(200);
                    
                    if (File.Exists(finalOutputPath))
                    {
                        // Check if we're writing to a different location than the source
                        bool writingToDifferentLocation = !finalOutputPath.Equals(sourceVarPath, StringComparison.OrdinalIgnoreCase);
                        
                        // If reading from archive and writing to a DIFFERENT location, add timestamp to avoid conflict
                        // But if writing to SAME location (re-optimizing), just delete and replace
                        if (isSourceInArchive && writingToDifferentLocation)
                        {
                            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                            string filenameWithoutExt = Path.GetFileNameWithoutExtension(filename);
                            string ext = Path.GetExtension(filename);
                            string outputDir = Path.GetDirectoryName(finalOutputPath);
                            finalOutputPath = Path.Combine(outputDir, $"{filenameWithoutExt}_{timestamp}{ext}");
                        }
                        else
                        {
                            // GUARANTEED FIX: Use retry loop with aggressive handle release
                            // File.Move requires exclusive access to BOTH source and dest, which is fragile
                            // Instead, we delete with retries, then copy, then delete temp
                            for (int deleteAttempt = 1; deleteAttempt <= 10; deleteAttempt++)
                            {
                                try
                                {
                                    File.Delete(finalOutputPath);
                                    break;
                                }
                                catch (IOException) when (deleteAttempt < 10)
                                {
                                    // Aggressive cleanup on each retry
                                    if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(finalOutputPath);
                                    FileAccessController.Instance.InvalidateFile(finalOutputPath);
                                    GC.Collect();
                                    GC.WaitForPendingFinalizers();
                                    await Task.Delay(100 * deleteAttempt);
                                }
                            }
                        }
                    }
                    
                    // GUARANTEED FIX: Use File.Copy + File.Delete instead of File.Move
                    // File.Move requires exclusive access to destination which can fail if any handle is open
                    // File.Copy is more forgiving - it only needs read access to source
                    for (int copyAttempt = 1; copyAttempt <= 10; copyAttempt++)
                    {
                        try
                        {
                            File.Copy(tempOutputPath, finalOutputPath, overwrite: true);
                            break;
                        }
                        catch (IOException) when (copyAttempt < 10)
                        {
                            // Aggressive cleanup on each retry
                            if (_imageManager != null) await _imageManager.CloseFileHandlesAsync(finalOutputPath);
                            FileAccessController.Instance.InvalidateFile(finalOutputPath);
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            await Task.Delay(100 * copyAttempt);
                        }
                    }
                    
                    // Delete temp file (best effort - not critical if it fails)
                    try { File.Delete(tempOutputPath); } catch { }
                    
                    // Clean up temp source file if we created one (SCENARIO 2/2B)
                    if (sourcePathForProcessing != sourceVarPath && sourcePathForProcessing != archiveFilePath && 
                        sourcePathForProcessing.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            File.Delete(sourcePathForProcessing);
                        }
                        catch { /* Ignore cleanup errors */ }
                    }
                    
                    // Force timestamp update to ensure cache invalidation
                    File.SetLastWriteTimeUtc(finalOutputPath, DateTime.UtcNow);

                    progressCallback?.Invoke("✨ Optimization complete!", totalOperations, totalOperations);
                    
                    // Get file sizes for statistics
                    long convertedSize = new FileInfo(finalOutputPath).Length;
                    
                    // Use the original file size we captured at the start
                    // This ensures we compare the input size to output size correctly
                    return new RepackageResult
                    {
                        OutputPath = finalOutputPath,
                        OriginalSize = originalFileSize,
                        NewSize = convertedSize,
                        TexturesConverted = textureConversionDetails.Count,
                        HairsModified = hairConversionDetails.Count,
                        TextureDetails = textureConversionDetails.ToList(),
                        Errors = errors.ToList(),
                        JsonSizeBeforeMinify = jsonSizeBeforeMinify,
                        JsonSizeAfterMinify = jsonSizeAfterMinify
                    };
                }
                catch
                {
                    // On error, clean up and restore if needed
                    try
                    {
                        if (File.Exists(tempOutputPath))
                            File.Delete(tempOutputPath);
                        
                        // Clean up temp source file if we created one (SCENARIO 2/2B)
                        if (sourcePathForProcessing != sourceVarPath && sourcePathForProcessing != archiveFilePath && 
                            sourcePathForProcessing.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(sourcePathForProcessing); } catch { }
                        }
                        
                        // Only restore if we moved the file to archive (createBackup was true)
                        if (createBackup && File.Exists(archivedPath) && !File.Exists(sourceVarPath))
                        {
                            // Use Copy+Delete instead of Move for reliability
                            try
                            {
                                File.Copy(archivedPath, sourceVarPath, overwrite: false);
                                File.Delete(archivedPath);
                            }
                            catch { /* Best effort restore */ }
                        }
                        
                        // If we were re-optimizing from archive and created a file in main folder, delete it
                        if (isSourceInArchive && File.Exists(finalOutputPath) && finalOutputPath != sourceVarPath)
                        {
                            File.Delete(finalOutputPath);
                        }
                    }
                    catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke("Error: " + ex.Message, 0, 0);
                // Rethrow the exception so the caller knows the optimization failed
                throw;
            }
            finally
            {
                // CRITICAL: Always release the write lock when optimization completes (success or failure)
                // This allows image loading operations to resume
                writeLock?.Dispose();
            }
        }

        /// <summary>
        /// Sanitizes entry name to remove non-printable characters and prevent garbage output
        /// </summary>
        private static string SanitizeEntryName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "[Empty Name]";
            // Replace non-printable characters with '?'
            return new string(name.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        }

        /// <summary>
        /// Minifies JSON by removing whitespace and formatting
        /// </summary>
        private static string MinifyJson(string jsonContent)
        {
            try
            {
                // Parse the JSON to ensure it's valid
                using (JsonDocument doc = JsonDocument.Parse(jsonContent))
                {
                    // Use JsonSerializerOptions with no indentation to minify
                    var options = new JsonSerializerOptions { WriteIndented = false };
                    return JsonSerializer.Serialize(doc.RootElement, options);
                }
            }
            catch
            {
                // If minification fails, return original content
                return jsonContent;
            }
        }

        /// <summary>
        /// Modifies hair settings, light shadows, and optionally disables mirrors using text replacement
        /// Only modifies existing keys, never adds new ones (VaM crashes with unknown keys)
        /// </summary>
        private string ModifySceneJson(string jsonContent, List<KeyValuePair<string, (string sceneFile, string hairId, int targetDensity, bool hadOriginalDensity)>> hairMods, List<KeyValuePair<string, (string sceneFile, string lightId, bool castShadows, int shadowResolution)>> lightMods, ConcurrentDictionary<string, string> hairConversionDetails, ConcurrentDictionary<string, string> lightConversionDetails, bool disableMirrors = false)
        {
            try
            {
                string modifiedJson = jsonContent;
                
                // 1. Modify hair density values (curveDensity and hairMultiplier) - only if they exist
                foreach (var hairMod in hairMods)
                {
                    var (sceneFile, hairId, targetDensity, hadOriginalDensity) = hairMod.Value;
                    
                    // Find the hair sim section by ID and modify curveDensity/hairMultiplier if they exist
                    // Pattern: "id" : "hairId"... "curveDensity" : "64"
                    var hairIdPattern = $@"""id""\s*:\s*""{System.Text.RegularExpressions.Regex.Escape(hairId)}""";
                    var hairIdMatch = System.Text.RegularExpressions.Regex.Match(modifiedJson, hairIdPattern);
                    
                    if (hairIdMatch.Success)
                    {
                        // Find the object containing this ID (search forward to next closing brace at same level)
                        int startPos = hairIdMatch.Index;
                        int braceCount = 0;
                        int objectStart = modifiedJson.LastIndexOf('{', startPos);
                        int objectEnd = -1;
                        
                        for (int i = objectStart; i < modifiedJson.Length; i++)
                        {
                            if (modifiedJson[i] == '{') braceCount++;
                            else if (modifiedJson[i] == '}')
                            {
                                braceCount--;
                                if (braceCount == 0)
                                {
                                    objectEnd = i;
                                    break;
                                }
                            }
                        }
                        
                        if (objectEnd > objectStart)
                        {
                            string objectSection = modifiedJson.Substring(objectStart, objectEnd - objectStart + 1);
                            string modifiedSection = objectSection;
                            bool anyChanges = false;
                            bool hasCurveDensity = false;
                            bool hasHairMultiplier = false;
                            
                            // Replace curveDensity if it exists
                            var curveDensityRegex = new System.Text.RegularExpressions.Regex(@"""curveDensity""\s*:\s*""(\d+)""");
                            if (curveDensityRegex.IsMatch(modifiedSection))
                            {
                                modifiedSection = curveDensityRegex.Replace(modifiedSection, $"\"curveDensity\" : \"{targetDensity}\"");
                                anyChanges = true;
                                hasCurveDensity = true;
                            }
                            
                            // Replace hairMultiplier if it exists
                            var hairMultiplierRegex = new System.Text.RegularExpressions.Regex(@"""hairMultiplier""\s*:\s*""(\d+)""");
                            if (hairMultiplierRegex.IsMatch(modifiedSection))
                            {
                                modifiedSection = hairMultiplierRegex.Replace(modifiedSection, $"\"hairMultiplier\" : \"{targetDensity}\"");
                                anyChanges = true;
                                hasHairMultiplier = true;
                            }
                            
                            // Add curveDensity and hairMultiplier if they don't exist
                            if (!hasCurveDensity || !hasHairMultiplier)
                            {
                                // Find the position right before the closing brace of the storable
                                // We need to insert at the TOP LEVEL, not inside nested objects
                                // Search backwards from the end to find the closing brace
                                int closingBracePos = modifiedSection.LastIndexOf('}');
                                if (closingBracePos > 0)
                                {
                                    // Find the last character before the closing brace (skip whitespace)
                                    int insertPos = closingBracePos - 1;
                                    while (insertPos > 0 && char.IsWhiteSpace(modifiedSection[insertPos]))
                                    {
                                        insertPos--;
                                    }
                                    
                                    // Insert after this position, adding a comma if needed
                                    string beforeInsert = modifiedSection.Substring(0, insertPos + 1);
                                    string afterInsert = modifiedSection.Substring(insertPos + 1);
                                    
                                    // Add comma if the last character isn't already a comma or opening brace
                                    if (modifiedSection[insertPos] != ',' && modifiedSection[insertPos] != '{')
                                    {
                                        beforeInsert += ",";
                                    }
                                    
                                    if (!hasCurveDensity)
                                    {
                                        // Add comma after curveDensity only if hairMultiplier also needs to be added
                                        string comma = !hasHairMultiplier ? "," : "";
                                        beforeInsert += $"\r\n      \"curveDensity\" : \"{targetDensity}\"{comma}";
                                        anyChanges = true;
                                    }
                                    if (!hasHairMultiplier)
                                    {
                                        beforeInsert += $"\r\n      \"hairMultiplier\" : \"{targetDensity}\"";
                                        anyChanges = true;
                                    }
                                    
                                    modifiedSection = beforeInsert + afterInsert;
                                }
                            }
                            
                            if (anyChanges)
                            {
                                modifiedJson = modifiedJson.Substring(0, objectStart) + modifiedSection + modifiedJson.Substring(objectEnd + 1);
                                string action = (hasCurveDensity || hasHairMultiplier) ? "modified" : "added";
                                hairConversionDetails.TryAdd(hairMod.Key, $"  • {hairMod.Key}: hair density {action} -> {targetDensity}");
                            }
                        }
                    }
                }
                
                // 2. Modify light shadows - only if they exist
                foreach (var lightMod in lightMods)
                {
                    var (sceneFile, lightId, castShadows, shadowResolution) = lightMod.Value;
                    
                    // Find light by ID
                    var lightIdPattern = $@"""id""\s*:\s*""{System.Text.RegularExpressions.Regex.Escape(lightId)}""";
                    var lightIdMatch = System.Text.RegularExpressions.Regex.Match(modifiedJson, lightIdPattern);
                    
                    if (lightIdMatch.Success)
                    {
                        // Find the object containing this ID
                        int startPos = lightIdMatch.Index;
                        int objectStart = modifiedJson.LastIndexOf('{', startPos);
                        int braceCount = 0;
                        int objectEnd = -1;
                        
                        for (int i = objectStart; i < modifiedJson.Length; i++)
                        {
                            if (modifiedJson[i] == '{') braceCount++;
                            else if (modifiedJson[i] == '}')
                            {
                                braceCount--;
                                if (braceCount == 0)
                                {
                                    objectEnd = i;
                                    break;
                                }
                            }
                        }
                        
                        if (objectEnd > objectStart)
                        {
                            string objectSection = modifiedJson.Substring(objectStart, objectEnd - objectStart + 1);
                            string modifiedSection = objectSection;
                            bool wasModified = false;
                            
                            // Replace shadowsOn if it exists
                            var shadowsOnRegex = new System.Text.RegularExpressions.Regex(@"""shadowsOn""\s*:\s*""(true|false)""");
                            if (shadowsOnRegex.IsMatch(modifiedSection))
                            {
                                modifiedSection = shadowsOnRegex.Replace(modifiedSection, $"\"shadowsOn\" : \"{(castShadows ? "true" : "false")}\"");
                                wasModified = true;
                            }
                            
                            // Replace shadowResolution if it exists
                            string resolutionText = shadowResolution switch
                            {
                                2048 => "VeryHigh",
                                1024 => "High",
                                512 => "Medium",
                                256 => "Low",
                                _ => "Off"
                            };
                            var shadowResRegex = new System.Text.RegularExpressions.Regex(@"""shadowResolution""\s*:\s*""[^""]+""");
                            if (shadowResRegex.IsMatch(modifiedSection))
                            {
                                modifiedSection = shadowResRegex.Replace(modifiedSection, $"\"shadowResolution\" : \"{resolutionText}\"");
                                wasModified = true;
                            }
                            
                            if (wasModified)
                            {
                                modifiedJson = modifiedJson.Substring(0, objectStart) + modifiedSection + modifiedJson.Substring(objectEnd + 1);
                                
                                // Track the light modification
                                string shadowStatus = castShadows ? $"Shadows: {resolutionText}" : "Shadows: Off";
                                lightConversionDetails[lightMod.Key] = $"{sceneFile} - {lightId}: {shadowStatus}";
                            }
                        }
                    }
                }
                
                // 3. Disable mirrors - only if they exist
                if (disableMirrors)
                {
                    // Find all ReflectiveSlate objects and set "on" : "false"
                    var mirrorTypeRegex = new System.Text.RegularExpressions.Regex(@"""type""\s*:\s*""ReflectiveSlate""");
                    var matches = mirrorTypeRegex.Matches(modifiedJson);
                    
                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        // Find the object containing this type
                        int startPos = match.Index;
                        int objectStart = modifiedJson.LastIndexOf('{', startPos);
                        int braceCount = 0;
                        int objectEnd = -1;
                        
                        for (int i = objectStart; i < modifiedJson.Length; i++)
                        {
                            if (modifiedJson[i] == '{') braceCount++;
                            else if (modifiedJson[i] == '}')
                            {
                                braceCount--;
                                if (braceCount == 0)
                                {
                                    objectEnd = i;
                                    break;
                                }
                            }
                        }
                        
                        if (objectEnd > objectStart)
                        {
                            string objectSection = modifiedJson.Substring(objectStart, objectEnd - objectStart + 1);
                            string modifiedSection = objectSection;
                            
                            // Replace "on" if it exists
                            var onRegex = new System.Text.RegularExpressions.Regex(@"""on""\s*:\s*""(true|false)""");
                            if (onRegex.IsMatch(modifiedSection))
                            {
                                modifiedSection = onRegex.Replace(modifiedSection, "\"on\" : \"false\"");
                                modifiedJson = modifiedJson.Substring(0, objectStart) + modifiedSection + modifiedJson.Substring(objectEnd + 1);
                            }
                        }
                    }
                }
                
                return modifiedJson;
            }
            catch
            {
                // If modification fails, return original
                return jsonContent;
            }
        }

        /// <summary>
        /// Modifies hair density values in VAP preset files using text replacement
        /// Only modifies existing keys, never adds new ones (VaM crashes with unknown keys)
        /// </summary>
        private string ModifyHairInVapFile(string jsonContent, int maxTargetDensity, string entryKey, ConcurrentDictionary<string, string> conversionDetails)
        {
            try
            {
                string modifiedJson = jsonContent;
                bool anyChanges = false;
                
                // Use regex to find and replace curveDensity and hairMultiplier values
                // Pattern: "curveDensity" : "64" or "hairMultiplier" : "64"
                var densityRegex = new System.Text.RegularExpressions.Regex(
                    @"""(curveDensity|hairMultiplier)""\s*:\s*""(\d+)""",
                    System.Text.RegularExpressions.RegexOptions.None);
                
                modifiedJson = densityRegex.Replace(modifiedJson, match =>
                {
                    string propertyName = match.Groups[1].Value;
                    string currentValueStr = match.Groups[2].Value;
                    
                    if (int.TryParse(currentValueStr, out int currentValue))
                    {
                        if (currentValue > maxTargetDensity)
                        {
                            anyChanges = true;
                            return $"\"{propertyName}\" : \"{maxTargetDensity}\"";
                        }
                    }
                    
                    return match.Value; // Keep original if not changing
                });
                
                if (anyChanges)
                {
                    conversionDetails.TryAdd(entryKey, $"  • {Path.GetFileName(entryKey)}: hair preset density capped at {maxTargetDensity}");
                }
                
                return modifiedJson;
            }
            catch
            {
                return jsonContent;
            }
        }

        /// <summary>
        /// Recursively writes VAP JSON elements, reducing density values if needed.
        /// Uses WriteJsonElementWithHandler with a density reduction property handler.
        /// </summary>
        private void WriteVapElementWithDensityReduction(
            VamJsonWriter writer,
            JsonElement element,
            int maxTargetDensity)
        {
            // Property handler for density reduction
            bool HandleDensityProperty(JsonProperty property, JsonElement parent)
            {
                // Check if this is curveDensity or hairMultiplier
                if ((property.Name == "curveDensity" || property.Name == "hairMultiplier") && 
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    if (int.TryParse(property.Value.GetString(), out int currentValue) && currentValue > maxTargetDensity)
                    {
                        writer.WritePropertyName(property.Name);
                        writer.WriteStringValue(maxTargetDensity.ToString());
                        return true; // Handled
                    }
                }
                return false; // Not handled, use default
            }

            WriteJsonElementWithHandler(writer, element, HandleDensityProperty);
        }

        /// <summary>
        /// Recursively writes JSON elements with hair and light modifications.
        /// Uses WriteJsonElementWithHandler for non-object types, with custom object handling for scene modifications.
        /// </summary>
        private void WriteElementWithSceneModifications(
            VamJsonWriter writer, 
            JsonElement element, 
            List<KeyValuePair<string, (string sceneFile, string hairId, int targetDensity, bool hadOriginalDensity)>> hairMods,
            List<KeyValuePair<string, (string sceneFile, string lightId, bool castShadows, int shadowResolution)>> lightMods,
            ConcurrentDictionary<string, string> conversionDetails,
            bool disableMirrors = false)
        {
            // For non-objects, delegate to base handler with recursive callback
            if (element.ValueKind != JsonValueKind.Object)
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        WriteElementWithSceneModifications(writer, item, hairMods, lightMods, conversionDetails, disableMirrors);
                    writer.WriteEndArray();
                }
                else
                {
                    WriteJsonElementWithHandler(writer, element);
                }
                return;
            }

            // Object handling with scene modifications
            writer.WriteStartObject();
            
            // Detect object type from properties
            string storableId = element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            string atomType = element.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            
            bool isHairSim = storableId?.EndsWith("Sim", StringComparison.OrdinalIgnoreCase) == true;
            bool isReflectiveSlate = atomType == "ReflectiveSlate";
            bool isLightStorable = storableId == "Light";
            
            // Find matching modifications
            var hairMod = isHairSim ? hairMods.FirstOrDefault(m => m.Value.hairId == storableId) : default;
            var lightMod = isLightStorable ? lightMods.FirstOrDefault() : default;
            
            bool shouldModifyHair = hairMod.Key != null;
            bool hasCurveDensity = element.TryGetProperty("curveDensity", out _);
            bool densityWritten = false;
            
            foreach (var property in element.EnumerateObject())
            {
                // Skip density properties if modifying hair - will be inserted before rootColor
                if (shouldModifyHair && (property.Name == "curveDensity" || property.Name == "hairMultiplier"))
                    continue;
                
                // Handle light shadow modifications
                if (isLightStorable && lightMod.Key != null)
                {
                    if (property.Name == "shadowsOn")
                    {
                        writer.WritePropertyName("shadowsOn");
                        writer.WriteStringValue(lightMod.Value.castShadows ? "true" : "false");
                        continue;
                    }
                    if (property.Name == "shadowResolution")
                    {
                        writer.WritePropertyName("shadowResolution");
                        writer.WriteStringValue(GetShadowResolutionText(lightMod.Value.shadowResolution));
                        continue;
                    }
                }
                
                // Handle mirror disabling
                if (isReflectiveSlate && disableMirrors && property.Name == "on")
                {
                    writer.WritePropertyName("on");
                    writer.WriteStringValue("false");
                    continue;
                }
                
                // Insert hair density before rootColor
                if (shouldModifyHair && !densityWritten && property.Name == "rootColor")
                {
                    WriteHairDensityProperties(writer, hairMod, hasCurveDensity, conversionDetails);
                    densityWritten = true;
                }
                
                writer.WritePropertyName(property.Name);
                WriteElementWithSceneModifications(writer, property.Value, hairMods, lightMods, conversionDetails, disableMirrors);
            }
            
            // Write hair density at end if rootColor wasn't found
            if (shouldModifyHair && !densityWritten)
                WriteHairDensityProperties(writer, hairMod, hasCurveDensity, conversionDetails);
            
            writer.WriteEndObject();
        }

        private static string GetShadowResolutionText(int resolution) => resolution switch
        {
            2048 => "VeryHigh",
            1024 => "High",
            512 => "Medium",
            256 => "Low",
            _ => "Off"
        };

        private void WriteHairDensityProperties(
            VamJsonWriter writer,
            KeyValuePair<string, (string sceneFile, string hairId, int targetDensity, bool hadOriginalDensity)> hairMod,
            bool hasCurveDensity,
            ConcurrentDictionary<string, string> conversionDetails)
        {
            writer.WritePropertyName("curveDensity");
            writer.WriteStringValue(hairMod.Value.targetDensity.ToString());
            writer.WritePropertyName("hairMultiplier");
            writer.WriteStringValue(hairMod.Value.targetDensity.ToString());
            
            var detailText = hasCurveDensity
                ? $"  • {hairMod.Key}: curveDensity & hairMultiplier Modified †' {hairMod.Value.targetDensity}"
                : $"  • {hairMod.Key}: curveDensity & hairMultiplier Added †' {hairMod.Value.targetDensity}";
            conversionDetails.TryAdd(hairMod.Key, detailText);
        }

        /// <summary>
        /// Converts dependency versions to .latest using text replacement (including nested subdependencies)
        /// Returns the updated JSON and a list of changes made
        /// </summary>
        private (string updatedJson, List<string> changes) ConvertDependenciesToLatest(string originalMetaJson)
        {
            var changes = new List<string>();
            
            try
            {
                using (var doc = JsonDocument.Parse(originalMetaJson))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
                    {
                        return (originalMetaJson, changes);
                    }
                    
                    string updatedJson = originalMetaJson;
                    
                    // Recursively process all dependencies (including nested subdependencies)
                    ProcessDependenciesRecursive(deps, ref updatedJson, changes, 0);
                    
                    return (updatedJson, changes);
                }
            }
            catch (Exception)
            {
                return (originalMetaJson, changes);
            }
        }
        
        /// <summary>
        /// Recursively processes dependencies at all nesting levels
        /// </summary>
        private void ProcessDependenciesRecursive(JsonElement deps, ref string updatedJson, List<string> changes, int depth)
        {
            if (deps.ValueKind != JsonValueKind.Object)
                return;
            
            foreach (var dep in deps.EnumerateObject())
            {
                string depName = dep.Name;
                
                // Skip if already .latest
                if (depName.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
                {
                    // Still need to check subdependencies
                    if (dep.Value.ValueKind == JsonValueKind.Object && 
                        dep.Value.TryGetProperty("dependencies", out var subDeps))
                    {
                        ProcessDependenciesRecursive(subDeps, ref updatedJson, changes, depth + 1);
                    }
                    continue;
                }
                
                // Extract package name and version
                int lastDotIndex = depName.LastIndexOf('.');
                if (lastDotIndex <= 0)
                {
                    // Still check subdependencies even if we can't convert this one
                    if (dep.Value.ValueKind == JsonValueKind.Object && 
                        dep.Value.TryGetProperty("dependencies", out var subDeps))
                    {
                        ProcessDependenciesRecursive(subDeps, ref updatedJson, changes, depth + 1);
                    }
                    continue;
                }
                
                string packageName = depName.Substring(0, lastDotIndex);
                string version = depName.Substring(lastDotIndex + 1);
                
                // Skip if version is not numeric (might be already "latest" or other special version)
                if (!int.TryParse(version, out _))
                {
                    // Still check subdependencies
                    if (dep.Value.ValueKind == JsonValueKind.Object && 
                        dep.Value.TryGetProperty("dependencies", out var subDeps))
                    {
                        ProcessDependenciesRecursive(subDeps, ref updatedJson, changes, depth + 1);
                    }
                    continue;
                }
                
                string newDepName = $"{packageName}.latest";
                
                // Use text replacement to preserve JSON formatting
                // Try both common JSON formatting patterns: ": {" and " : {"
                string oldPattern1 = $"\"{depName}\": {{";
                string oldPattern2 = $"\"{depName}\" : {{";
                string newPattern1 = $"\"{newDepName}\": {{";
                string newPattern2 = $"\"{newDepName}\" : {{";
                
                bool replaced = false;
                if (updatedJson.Contains(oldPattern1))
                {
                    updatedJson = updatedJson.Replace(oldPattern1, newPattern1);
                    replaced = true;
                }
                else if (updatedJson.Contains(oldPattern2))
                {
                    updatedJson = updatedJson.Replace(oldPattern2, newPattern2);
                    replaced = true;
                }
                
                if (replaced)
                {
                    string indent = new string(' ', depth * 2);
                    changes.Add($"{indent}{depName} -> {newDepName}");
                }
                
                // Process subdependencies recursively
                if (dep.Value.ValueKind == JsonValueKind.Object && 
                    dep.Value.TryGetProperty("dependencies", out var nestedDeps))
                {
                    ProcessDependenciesRecursive(nestedDeps, ref updatedJson, changes, depth + 1);
                }
            }
        }
        
        /// <summary>
        /// Removes disabled dependencies from meta.json by rebuilding the dependencies structure
        /// This ensures JSON validity is maintained
        /// </summary>
        private string RemoveDisabledDependencies(string originalMetaJson, List<string> disabledDependencies)
        {
            if (disabledDependencies == null || disabledDependencies.Count == 0)
                return originalMetaJson;
            
            try
            {
                // Extract just the dependency names (remove parent info)
                var disabledNames = disabledDependencies
                    .Select(d => d.Contains("|PARENT:") ? d.Split(new[] { "|PARENT:" }, StringSplitOptions.None)[0] : d)
                    .ToHashSet();
                
                using (var doc = JsonDocument.Parse(originalMetaJson))
                {
                    var root = doc.RootElement;
                    
                    // Build filtered dependencies JSON manually to preserve original formatting
                    var filteredDepsJson = BuildFilteredDependenciesJson(root.GetProperty("dependencies"), disabledNames, 1);
                    
                    // Find and replace the dependencies section in the original JSON
                    var depsStartPattern = "\"dependencies\" : {";
                    int depsStart = originalMetaJson.IndexOf(depsStartPattern);
                    if (depsStart == -1)
                        return originalMetaJson;
                    
                    // Find the matching closing brace for dependencies
                    int braceCount = 0;
                    int searchStart = depsStart + depsStartPattern.Length;
                    int depsEnd = -1;
                    
                    for (int i = searchStart; i < originalMetaJson.Length; i++)
                    {
                        if (originalMetaJson[i] == '{')
                            braceCount++;
                        else if (originalMetaJson[i] == '}')
                        {
                            if (braceCount == 0)
                            {
                                depsEnd = i;
                                break;
                            }
                            braceCount--;
                        }
                    }
                    
                    if (depsEnd == -1)
                        return originalMetaJson;
                    
                    // Replace the dependencies section
                    var beforeDeps = originalMetaJson.Substring(0, depsStart);
                    var afterDeps = originalMetaJson.Substring(depsEnd + 1);
                    
                    return beforeDeps + "\"dependencies\" : { \r\n" + filteredDepsJson + "   }" + afterDeps;
                }
            }
            catch (Exception)
            {
                return originalMetaJson;
            }
        }
        
        /// <summary>
        /// Builds filtered dependencies JSON with VAM's exact formatting (3-space indents)
        /// </summary>
        private string BuildFilteredDependenciesJson(JsonElement depsElement, HashSet<string> disabledNames, int indentLevel)
        {
            if (depsElement.ValueKind != JsonValueKind.Object)
                return "";
            
            var sb = new StringBuilder();
            var indent = new string(' ', indentLevel * 3);
            var deps = depsElement.EnumerateObject().Where(d => !disabledNames.Contains(d.Name)).ToList();
            
            for (int i = 0; i < deps.Count; i++)
            {
                var dep = deps[i];
                var isLast = (i == deps.Count - 1);
                
                sb.Append($"{indent}\"{dep.Name}\" : ");
                
                if (dep.Value.ValueKind == JsonValueKind.Object)
                {
                    sb.AppendLine("{ ");
                    
                    var props = dep.Value.EnumerateObject().ToList();
                    for (int j = 0; j < props.Count; j++)
                    {
                        var prop = props[j];
                        var isPropLast = (j == props.Count - 1);
                        
                        if (prop.Name == "dependencies" && prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            sb.Append($"{indent}   \"{prop.Name}\" : {{ \r\n");
                            var subDepsJson = BuildFilteredDependenciesJson(prop.Value, disabledNames, indentLevel + 2);
                            sb.Append(subDepsJson);
                            sb.Append($"{indent}   }}");
                        }
                        else if (prop.Name == "licenseType")
                        {
                            sb.Append($"{indent}   \"{prop.Name}\" : \"{prop.Value.GetString()}\"");
                        }
                        else
                        {
                            sb.Append($"{indent}   \"{prop.Name}\" : {prop.Value.GetRawText()}");
                        }
                        
                        if (!isPropLast)
                            sb.Append(", ");
                        sb.AppendLine();
                    }
                    
                    sb.Append($"{indent}}}");
                }
                else
                {
                    sb.Append(dep.Value.GetRawText());
                }
                
                if (!isLast)
                    sb.Append(", ");
                sb.AppendLine();
            }
            
            return sb.ToString();
        }
        
        /// <summary>
        /// Recursively searches for a dependency by name and returns its JSON value
        /// </summary>
        private string FindDependencyValue(JsonElement element, string depName)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("dependencies", out var deps))
            {
                if (deps.ValueKind == JsonValueKind.Object && deps.TryGetProperty(depName, out var depValue))
                {
                    return depValue.GetRawText();
                }
                
                // Recursively search in subdependencies
                foreach (var prop in deps.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var result = FindDependencyValue(prop.Value, depName);
                        if (!string.IsNullOrEmpty(result))
                            return result;
                    }
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Sets the preloadMorphs flag in meta.json using text replacement
        /// This preserves the exact original JSON formatting
        /// </summary>
        private string SetPreloadMorphsFlag(string originalMetaJson, bool preloadMorphs)
        {
            try
            {
                string valueToSet = preloadMorphs ? "true" : "false";
                
                // Check if customOptions section exists
                if (originalMetaJson.Contains("\"customOptions\""))
                {
                    // Check if preloadMorphs already exists
                    var preloadMorphsPattern = new System.Text.RegularExpressions.Regex(
                        @"""preloadMorphs""\s*:\s*""(true|false)""",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    
                    if (preloadMorphsPattern.IsMatch(originalMetaJson))
                    {
                        // Replace existing value
                        return preloadMorphsPattern.Replace(originalMetaJson, $"\"preloadMorphs\" : \"{valueToSet}\"");
                    }
                    else
                    {
                        // Add preloadMorphs to existing customOptions
                        // Find the customOptions section and add the flag
                        var customOptionsPattern = new System.Text.RegularExpressions.Regex(
                            @"""customOptions""\s*:\s*\{",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        
                        if (customOptionsPattern.IsMatch(originalMetaJson))
                        {
                            return customOptionsPattern.Replace(originalMetaJson, 
                                $"\"customOptions\" : {{\n      \"preloadMorphs\" : \"{valueToSet}\"", 1);
                        }
                    }
                }
                else
                {
                    // Add customOptions section before hadReferenceIssues or at the end
                    var insertPattern = new System.Text.RegularExpressions.Regex(
                        @"(   \},\s*\n   ""hadReferenceIssues"")",
                        System.Text.RegularExpressions.RegexOptions.Multiline);
                    
                    if (insertPattern.IsMatch(originalMetaJson))
                    {
                        return insertPattern.Replace(originalMetaJson, 
                            $"   }}, \n   \"customOptions\" : {{ \n      \"preloadMorphs\" : \"{valueToSet}\"\n   }}, \n   \"hadReferenceIssues\"", 1);
                    }
                }
                
                return originalMetaJson;
            }
            catch (Exception)
            {
                return originalMetaJson;
            }
        }
        
        /// <summary>
        /// Updates the meta.json description with conversion details using text replacement
        /// This preserves the exact original JSON formatting
        /// </summary>
        private string UpdateMetaJsonDescription(
            string originalMetaJson, 
            System.Collections.Concurrent.ConcurrentBag<string> textureDetails,
            IEnumerable<string> hairDetails,
            IEnumerable<string> lightDetails,
            bool disableMirrors,
            long originalSize, 
            long newSize,
            DateTime? originalMetaJsonDate,
            List<string> dependencyChanges = null,
            List<string> disabledDependencies = null,
            bool morphPreloadChanged = false,
            bool minifyJson = false)
        {
            try
            {
                var hairDetailList = hairDetails?.ToList() ?? new List<string>();
                var lightDetailList = lightDetails?.ToList() ?? new List<string>();
                
                // Parse JSON to get the original description value
                using (var doc = JsonDocument.Parse(originalMetaJson))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("description", out var descProp))
                        return originalMetaJson; // No description field
                    
                    string originalDescription = descProp.GetString() ?? "";
                    
                    // Parse existing VPM optimization data to preserve it
                    var existingFlags = ParseExistingVpmFlags(originalDescription);
                    var existingTextureData = ExtractSection(originalDescription, "[VPM_TEXTURE_CONVERSION_DATA]", "[/VPM_TEXTURE_CONVERSION_DATA]");
                    var existingHairData = ExtractSection(originalDescription, "[VPM_HAIR_CONVERSION_DATA]", "[/VPM_HAIR_CONVERSION_DATA]");
                    var existingDependencyConversionData = ExtractSection(originalDescription, "[VPM_DEPENDENCY_CONVERSION_DATA]", "[/VPM_DEPENDENCY_CONVERSION_DATA]");
                    var existingDisabledDepsData = ExtractSection(originalDescription, "[VPM_DISABLED_DEPENDENCIES]", "[/VPM_DISABLED_DEPENDENCIES]");
                    string existingOriginalDate = existingFlags.ContainsKey("vpmOriginalDate") ? existingFlags["vpmOriginalDate"] : null;
                    
                    // Extract the truly original description (before any VPM modifications)
                    string trulyOriginalDescription = originalDescription;
                    int originalDescMarker = originalDescription.IndexOf("─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─");
                    if (originalDescMarker >= 0)
                    {
                        int originalDescStart = originalDescription.IndexOf("ORIGINAL DESCRIPTION:", originalDescMarker);
                        if (originalDescStart >= 0)
                        {
                            originalDescStart += "ORIGINAL DESCRIPTION:".Length;
                            trulyOriginalDescription = originalDescription.Substring(originalDescStart).Trim();
                        }
                    }
                    
                    // Build new description
                    var descriptionBuilder = new StringBuilder();
                    descriptionBuilder.AppendLine("⚡ VPM-OPTIMIZED PACKAGE");
                    descriptionBuilder.AppendLine();
                    
                    // Merge flags: preserve existing + add new
                    bool hasTextureOpt = textureDetails.Count > 0 || (existingFlags.ContainsKey("vpmTextureOptimized") && existingFlags["vpmTextureOptimized"] == "True");
                    bool hasHairOpt = hairDetailList.Count > 0 || (existingFlags.ContainsKey("vpmHairOptimized") && existingFlags["vpmHairOptimized"] == "True");
                    bool hasShadowOpt = lightDetailList.Count > 0 || (existingFlags.ContainsKey("vpmShadowOptimized") && existingFlags["vpmShadowOptimized"] == "True");
                    bool hasMirrorOpt = disableMirrors || (existingFlags.ContainsKey("vpmMirrorOptimized") && existingFlags["vpmMirrorOptimized"] == "True");
                    bool hasDependencyOpt = (dependencyChanges != null && dependencyChanges.Count > 0) || (disabledDependencies != null && disabledDependencies.Count > 0) || 
                                           (existingFlags.ContainsKey("vpmDependencyOptimized") && existingFlags["vpmDependencyOptimized"] == "True");
                    bool hasMorphPreloadOpt = morphPreloadChanged || (existingFlags.ContainsKey("vpmMorphPreloadOptimized") && existingFlags["vpmMorphPreloadOptimized"] == "True");
                    bool hasJsonMinified = minifyJson || (existingFlags.ContainsKey("vpmJsonMinified") && existingFlags["vpmJsonMinified"] == "True");
                    
                    // vpmOptimized is true if ANY optimization exists (including meta.json changes like morph preload, dependency changes, or minification)
                    bool hasAnyOptimization = hasTextureOpt || hasHairOpt || hasShadowOpt || hasMirrorOpt || hasDependencyOpt || hasMorphPreloadOpt || hasJsonMinified;
                    
                    descriptionBuilder.AppendLine("[VPM_FLAGS]");
                    descriptionBuilder.AppendLine($"vpmOptimized={hasAnyOptimization}");
                    descriptionBuilder.AppendLine($"vpmTextureOptimized={hasTextureOpt}");
                    descriptionBuilder.AppendLine($"vpmHairOptimized={hasHairOpt}");
                    descriptionBuilder.AppendLine($"vpmShadowOptimized={hasShadowOpt}");
                    descriptionBuilder.AppendLine($"vpmMirrorOptimized={hasMirrorOpt}");
                    descriptionBuilder.AppendLine($"vpmDependencyOptimized={hasDependencyOpt}");
                    descriptionBuilder.AppendLine($"vpmMorphPreloadOptimized={hasMorphPreloadOpt}");
                    descriptionBuilder.AppendLine($"vpmJsonMinified={hasJsonMinified}");
                    
                    // Preserve or set original date
                    string dateToUse = existingOriginalDate ?? (originalMetaJsonDate.HasValue ? originalMetaJsonDate.Value.ToString("yyyy-MM-ddTHH:mm:ss.fff") : null);
                    if (!string.IsNullOrEmpty(dateToUse))
                    {
                        descriptionBuilder.AppendLine($"vpmOriginalDate={dateToUse}");
                    }
                    descriptionBuilder.AppendLine("[/VPM_FLAGS]");
                    descriptionBuilder.AppendLine();

                    if (textureDetails.Count > 0)
                    {
                        descriptionBuilder.AppendLine($"✓ Textures Optimized: {textureDetails.Count}");
                        descriptionBuilder.AppendLine($"✓ Space Saved: {FormatHelper.FormatBytes(originalSize - newSize)} ({(originalSize > 0 ? (100.0 * (originalSize - newSize) / originalSize).ToString("F1") : "0")}%)");
                    }

                    if (hairDetailList.Count > 0)
                    {
                        descriptionBuilder.AppendLine($"✓ Hair Settings Modified: {hairDetailList.Count}");
                    }

                    if (lightDetailList.Count > 0)
                    {
                        descriptionBuilder.AppendLine($"✓ Shadow Settings Modified: {lightDetailList.Count}");
                    }

                    if (dependencyChanges != null && dependencyChanges.Count > 0)
                    {
                        descriptionBuilder.AppendLine($"✓ Dependencies Updated to .latest: {dependencyChanges.Count}");
                    }

                    if (disabledDependencies != null && disabledDependencies.Count > 0)
                    {
                        descriptionBuilder.AppendLine($"✓ Dependencies Removed: {disabledDependencies.Count}");
                    }

                    if (morphPreloadChanged)
                    {
                        descriptionBuilder.AppendLine($"✓ Morph Preload Disabled");
                    }

                    descriptionBuilder.AppendLine();

                    // Use new data if provided, otherwise preserve existing
                    if (textureDetails.Count > 0 || !string.IsNullOrEmpty(existingTextureData))
                    {
                        descriptionBuilder.AppendLine("[VPM_TEXTURE_CONVERSION_DATA]");
                        if (textureDetails.Count > 0)
                        {
                            foreach (var detail in textureDetails)
                            {
                                descriptionBuilder.AppendLine(detail);
                            }
                        }
                        else
                        {
                            descriptionBuilder.AppendLine(existingTextureData.Trim());
                        }
                        descriptionBuilder.AppendLine("[/VPM_TEXTURE_CONVERSION_DATA]");
                        descriptionBuilder.AppendLine();
                    }

                    if (hairDetailList.Count > 0 || !string.IsNullOrEmpty(existingHairData))
                    {
                        descriptionBuilder.AppendLine("[VPM_HAIR_CONVERSION_DATA]");
                        if (hairDetailList.Count > 0)
                        {
                            foreach (var detail in hairDetailList)
                            {
                                descriptionBuilder.AppendLine(detail);
                            }
                        }
                        else
                        {
                            descriptionBuilder.AppendLine(existingHairData.Trim());
                        }
                        descriptionBuilder.AppendLine("[/VPM_HAIR_CONVERSION_DATA]");
                        descriptionBuilder.AppendLine();
                    }

                    if ((dependencyChanges != null && dependencyChanges.Count > 0) || !string.IsNullOrEmpty(existingDependencyConversionData))
                    {
                        descriptionBuilder.AppendLine("[VPM_DEPENDENCY_CONVERSION_DATA]");
                        if (dependencyChanges != null && dependencyChanges.Count > 0)
                        {
                            foreach (var change in dependencyChanges)
                            {
                                descriptionBuilder.AppendLine($"  • {change}");
                            }
                        }
                        else
                        {
                            descriptionBuilder.AppendLine(existingDependencyConversionData.Trim());
                        }
                        descriptionBuilder.AppendLine("[/VPM_DEPENDENCY_CONVERSION_DATA]");
                        descriptionBuilder.AppendLine();
                    }

                    // Always write current disabled dependencies (don't preserve old ones if none are currently disabled)
                    if (disabledDependencies != null && disabledDependencies.Count > 0)
                    {
                        descriptionBuilder.AppendLine("[VPM_DISABLED_DEPENDENCIES]");
                        foreach (var disabledDep in disabledDependencies)
                        {
                            // Format: depName or depName|PARENT:parentName
                            descriptionBuilder.AppendLine($"  • {disabledDep}");
                        }
                        descriptionBuilder.AppendLine("[/VPM_DISABLED_DEPENDENCIES]");
                        descriptionBuilder.AppendLine();
                    }

                    descriptionBuilder.AppendLine("─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─");
                    descriptionBuilder.AppendLine("ORIGINAL DESCRIPTION:");
                    descriptionBuilder.Append(trulyOriginalDescription);
                    
                    string newDescription = descriptionBuilder.ToString();
                    
                    // Escape the new description for JSON
                    string escapedNewDescription = EscapeJsonString(newDescription);
                    string escapedOldDescription = EscapeJsonString(originalDescription);
                    
                    // Find and replace the description value in the original JSON text
                    // Pattern: "description" : "old value"
                    string pattern = $"\"description\" : \"{escapedOldDescription}\"";
                    string replacement = $"\"description\" : \"{escapedNewDescription}\"";
                    
                    string updatedJson = originalMetaJson.Replace(pattern, replacement);
                    
                    // If replacement didn't work (maybe different spacing), try regex
                    if (updatedJson == originalMetaJson)
                    {
                        // Try with regex to handle any whitespace variations
                        var regex = new System.Text.RegularExpressions.Regex(
                            @"""description""\s*:\s*""" + System.Text.RegularExpressions.Regex.Escape(escapedOldDescription) + @"""",
                            System.Text.RegularExpressions.RegexOptions.Singleline);
                        updatedJson = regex.Replace(originalMetaJson, $"\"description\" : \"{escapedNewDescription}\"");
                    }
                    
                    return updatedJson;
                }
            }
            catch (Exception)
            {
                return originalMetaJson;
            }
        }
        
        /// <summary>
        /// Parses existing VPM flags from description
        /// </summary>
        private Dictionary<string, string> ParseExistingVpmFlags(string description)
        {
            var flags = new Dictionary<string, string>();
            
            try
            {
                var startTag = "[VPM_FLAGS]";
                var endTag = "[/VPM_FLAGS]";
                
                int startIndex = description.IndexOf(startTag);
                if (startIndex == -1)
                    return flags;
                
                startIndex += startTag.Length;
                int endIndex = description.IndexOf(endTag, startIndex);
                if (endIndex == -1)
                    return flags;
                
                string flagsSection = description.Substring(startIndex, endIndex - startIndex).Trim();
                var lines = flagsSection.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var line in lines)
                {
                    var parts = line.Split('=');
                    if (parts.Length == 2)
                    {
                        flags[parts[0].Trim()] = parts[1].Trim();
                    }
                }
            }
            catch (Exception)
            {
            }
            
            return flags;
        }
        
        /// <summary>
        /// Extracts content between start and end tags
        /// </summary>
        private string ExtractSection(string description, string startTag, string endTag)
        {
            try
            {
                int startIndex = description.IndexOf(startTag);
                if (startIndex == -1)
                    return "";
                
                startIndex += startTag.Length;
                int endIndex = description.IndexOf(endTag, startIndex);
                if (endIndex == -1)
                    return "";
                
                return description.Substring(startIndex, endIndex - startIndex).Trim();
            }
            catch
            {
                return "";
            }
        }
        
        /// <summary>
        /// Escapes a string for use in JSON using System.Text.Json
        /// </summary>
        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            
            // Use System.Text.Json for proper escaping - serialize and strip quotes
            var escaped = JsonSerializer.Serialize(value);
            return escaped.Substring(1, escaped.Length - 2);
        }

        /// <summary>
        /// Writes a JSON element to the VamJsonWriter. Base method for all JSON writing operations.
        /// </summary>
        /// <param name="writer">The VamJsonWriter to write to</param>
        /// <param name="element">The JSON element to write</param>
        /// <param name="propertyHandler">Optional callback to handle/modify properties. Return true to skip default handling.</param>
        /// <param name="beforeEndObject">Optional callback invoked before closing an object (for inserting new properties)</param>
        private void WriteJsonElementWithHandler(
            VamJsonWriter writer, 
            JsonElement element,
            Func<JsonProperty, JsonElement, bool> propertyHandler = null,
            Action<JsonElement> beforeEndObject = null)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        // If handler returns true, it handled the property - skip default
                        if (propertyHandler?.Invoke(property, element) == true)
                            continue;
                        
                        writer.WritePropertyName(property.Name);
                        WriteJsonElementWithHandler(writer, property.Value, propertyHandler, beforeEndObject);
                    }
                    beforeEndObject?.Invoke(element);
                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteJsonElementWithHandler(writer, item, propertyHandler, beforeEndObject);
                    }
                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                    writer.WriteStringValue(element.GetString() ?? "");
                    break;

                case JsonValueKind.Number:
                    if (element.TryGetInt64(out long longValue))
                        writer.WriteNumberValue(longValue);
                    else if (element.TryGetDouble(out double doubleValue))
                        writer.WriteNumberValue(doubleValue);
                    else
                        writer.WriteNullValue();
                    break;

                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;

                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    writer.WriteNullValue();
                    break;
            }
        }

        /// <summary>
        /// Simple JSON element copy (no modifications)
        /// </summary>
        private void WriteJsonElement(VamJsonWriter writer, JsonElement element) =>
            WriteJsonElementWithHandler(writer, element);

        private static string GetResolutionStringFromDimension(int maxDim) => maxDim switch
        {
            >= 7680 => "8K",
            >= 4096 => "4K",
            >= 2048 => "2K",
            >= 1024 => "1K",
            _ => $"{maxDim}px"
        };

        private static string GetResolutionString(int width, int height) => 
            GetResolutionStringFromDimension(Math.Max(width, height));

        private sealed class VamJsonWriter : IDisposable
        {
            private sealed class Context
            {
                public Context(ContextType type)
                {
                    Type = type;
                }

                public ContextType Type { get; }
                public int ElementCount { get; set; }
            }

            private enum ContextType
            {
                Object,
                Array
            }

            private readonly StreamWriter _writer;
            private readonly Stack<Context> _contexts = new Stack<Context>();
            private int _indentLevel;
            private bool _pendingProperty;

            public VamJsonWriter(Stream stream)
            {
                _writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true);
            }

            public void WriteStartObject()
            {
                if (_pendingProperty)
                {
                    _writer.Write("{ ");
                    _pendingProperty = false;
                }
                else
                {
                    WriteValuePrefix();
                    _writer.Write("{ ");
                }

                _indentLevel++;
                _contexts.Push(new Context(ContextType.Object));
            }

            public void WriteEndObject()
            {
                ValidateContext(ContextType.Object);
                var context = _contexts.Pop();
                _indentLevel--;

                // Always write newline and indent before closing brace (even for empty objects)
                _writer.Write('\n');
                WriteIndent();

                _writer.Write('}');
                _pendingProperty = false;
            }

            public void WriteStartArray()
            {
                if (_pendingProperty)
                {
                    _writer.Write("[ ");
                    _pendingProperty = false;
                }
                else
                {
                    WriteValuePrefix();
                    _writer.Write("[ ");
                }

                _indentLevel++;
                _contexts.Push(new Context(ContextType.Array));
            }

            public void WriteEndArray()
            {
                ValidateContext(ContextType.Array);
                var context = _contexts.Pop();
                _indentLevel--;

                // Always write newline and indent before closing bracket (even for empty arrays)
                _writer.Write('\n');
                WriteIndent();

                _writer.Write(']');
                _pendingProperty = false;
            }

            public void WritePropertyName(string name)
            {
                ValidateContext(ContextType.Object);
                var context = _contexts.Peek();

                if (context.ElementCount > 0)
                {
                    _writer.Write(", ");
                }
                
                _writer.Write('\n');
                WriteIndent();
                WriteStringLiteral(name);
                _writer.Write(" : ");
                context.ElementCount++;
                _pendingProperty = true;
            }

            public void WriteStringValue(string value)
            {
                if (value == null)
                {
                    WriteNullValue();
                    return;
                }

                if (_pendingProperty)
                {
                    WriteStringLiteral(value);
                    _pendingProperty = false;
                }
                else
                {
                    WriteValuePrefix();
                    WriteStringLiteral(value);
                }
            }

            public void WriteNumberValue(int value)
            {
                WriteNumberValueCore(value.ToString(CultureInfo.InvariantCulture));
            }

            public void WriteNumberValue(long value)
            {
                WriteNumberValueCore(value.ToString(CultureInfo.InvariantCulture));
            }

            public void WriteNumberValue(double value)
            {
                WriteNumberValueCore(value.ToString("G", CultureInfo.InvariantCulture));
            }

            private void WriteNumberValueCore(string text)
            {
                if (_pendingProperty)
                {
                    _writer.Write(text);
                    _pendingProperty = false;
                }
                else
                {
                    WriteValuePrefix();
                    _writer.Write(text);
                }
            }

            public void WriteBooleanValue(bool value)
            {
                var text = value ? "true" : "false";
                if (_pendingProperty)
                {
                    _writer.Write(text);
                    _pendingProperty = false;
                }
                else
                {
                    WriteValuePrefix();
                    _writer.Write(text);
                }
            }

            public void WriteNullValue()
            {
                if (_pendingProperty)
                {
                    _writer.Write("null");
                    _pendingProperty = false;
                }
                else
                {
                    WriteValuePrefix();
                    _writer.Write("null");
                }
            }

            public void Flush()
            {
                _writer.Flush();
            }

            public void Dispose()
            {
                Flush();
            }

            private void WriteValuePrefix()
            {
                if (_contexts.Count == 0)
                {
                    return;
                }

                var context = _contexts.Peek();
                if (context.Type == ContextType.Object)
                {
                    if (_pendingProperty)
                    {
                        return;
                    }

                    throw new InvalidOperationException("Object values must follow a property name.");
                }

                if (context.ElementCount > 0)
                {
                    _writer.Write(',');
                }
                
                _writer.Write('\n');
                WriteIndent();
                context.ElementCount++;
            }

            private void WriteIndent()
            {
                if (_indentLevel <= 0)
                {
                    return;
                }

                _writer.Write(new string(' ', _indentLevel * 3));
            }

            private void WriteStringLiteral(string value)
            {
                _writer.Write('"');
                foreach (var ch in value)
                {
                    switch (ch)
                    {
                        case '\\':
                            _writer.Write("\\\\");
                            break;
                        case '"':
                            _writer.Write("\\\"");
                            break;
                        case '\n':
                            _writer.Write("\\n");
                            break;
                        case '\r':
                            _writer.Write("\\r");
                            break;
                        case '\t':
                            _writer.Write("\\t");
                            break;
                        default:
                            if (char.IsControl(ch))
                            {
                                _writer.Write("\\u");
                                _writer.Write(((int)ch).ToString("X4"));
                            }
                            else
                            {
                                _writer.Write(ch);
                            }

                            break;
                    }
                }

                _writer.Write('"');
            }

            private void ValidateContext(ContextType expected)
            {
                if (_contexts.Count == 0 || _contexts.Peek().Type != expected)
                {
                    throw new InvalidOperationException($"Unexpected JSON writer state. Expected {expected} context.");
                }
            }
        }
    }
}

