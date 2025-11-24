using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;

namespace VPM.Services
{
    /// <summary>
    /// Handles VAR file repackaging with modified textures
    /// </summary>
    public class VarRepackager
    {
        private readonly TextureConverter _textureConverter;
        private readonly ImageManager _imageManager;
        private readonly ISettingsManager _settingsManager;

        public VarRepackager(ImageManager imageManager = null, ISettingsManager settingsManager = null)
        {
            _textureConverter = new TextureConverter();
            _imageManager = imageManager;
            _settingsManager = settingsManager;
            
            if (_settingsManager != null)
            {
                _textureConverter.CompressionQuality = _settingsManager.Settings.TextureCompressionQuality;
            }
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
        /// Repackages a VAR file with converted textures and returns statistics
        /// </summary>
        public async Task<(string outputPath, long originalSize, long newSize, int texturesConverted)> RepackageVarWithStatsAsync(string sourceVarPath, string archivedFolder, Dictionary<string, (string targetResolution, int originalWidth, int originalHeight, long originalSize)> textureConversions, ProgressCallback progressCallback = null)
        {
            var result = await RepackageVarInternalAsync(sourceVarPath, archivedFolder, textureConversions, progressCallback);
            return result;
        }

        /// <summary>
        /// Repackages a VAR file with converted textures
        /// </summary>
        /// <param name="sourceVarPath">Source VAR file path</param>
        /// <param name="archivedFolder">Path to ArchivedPackages folder</param>
        /// <param name="textureConversions">Dictionary of texture paths to target resolutions with original dimensions</param>
        /// <param name="progressCallback">Optional progress callback</param>
        /// <returns>Path to the new VAR file</returns>
        private async Task<(string outputPath, long originalSize, long newSize, int texturesConverted)> RepackageVarInternalAsync(string sourceVarPath, string archivedFolder, Dictionary<string, (string targetResolution, int originalWidth, int originalHeight, long originalSize)> textureConversions, ProgressCallback progressCallback = null)
        {
            try
            {
                string directory = Path.GetDirectoryName(sourceVarPath);
                string filename = Path.GetFileName(sourceVarPath);
                
                // Validate that ArchivedPackages folder is not inside AllPackages or AddonPackages
                if (archivedFolder.Contains("AllPackages") || archivedFolder.Contains("AddonPackages"))
                {
                    throw new InvalidOperationException("ArchivedPackages folder cannot be created inside AllPackages or AddonPackages folders. It must be in the game root directory.");
                }
                
                string sourcePathForProcessing;
                string archivedPath = null;
                bool isSourceInArchive = false;
                string finalOutputPath = sourceVarPath; // Default to source location
                long originalFileSize = new FileInfo(sourceVarPath).Length; // Capture original size before any processing
                
                // Determine if source is in archive folder
                isSourceInArchive = sourceVarPath.Contains(Path.DirectorySeparatorChar + "ArchivedPackages" + Path.DirectorySeparatorChar) ||
                                   sourceVarPath.Contains(Path.AltDirectorySeparatorChar + "ArchivedPackages" + Path.AltDirectorySeparatorChar);
                
                Directory.CreateDirectory(archivedFolder);
                string archiveFilePath = Path.Combine(archivedFolder, filename);
                
                if (isSourceInArchive)
                {
                    // SCENARIO 3: Optimizing from archive folder
                    // Read from archive (keep original), write to loaded folder
                    progressCallback?.Invoke("Optimizing from archive (original preserved)...", 0, textureConversions.Count);
                    
                    _imageManager?.CloseFileHandles(sourceVarPath);
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
                else if (File.Exists(archiveFilePath))
                {
                    // SCENARIO 2: Re-optimizing already optimized package in loaded folder
                    // Read from archive (original) to allow re-optimization with different settings
                    // This provides BETTER QUALITY when downscaling (e.g., 8K†’2K is better than 4K†’2K)
                    progressCallback?.Invoke("Re-optimizing from original archive (better quality)...", 0, textureConversions.Count);
                    
                    _imageManager?.CloseFileHandles(sourceVarPath);
                    _imageManager?.CloseFileHandles(archiveFilePath);
                    await ReleaseFileHandlesAsync(100);
                    
                    sourcePathForProcessing = archiveFilePath; // Read from archive (original)
                    finalOutputPath = sourceVarPath; // Write back to loaded folder
                    isSourceInArchive = true; // Treat as reading from archive for file handling
                }
                else
                {
                    // SCENARIO 1: First-time optimization
                    // Move original to archive, then optimize
                    progressCallback?.Invoke("Moving original to archive...", 0, textureConversions.Count);
                    
                    _imageManager?.CloseFileHandles(sourceVarPath);
                    await ReleaseFileHandlesAsync(100);
                    
                    archivedPath = archiveFilePath;
                    
                    // Retry logic for moving file
                    bool moveSuccess = false;
                    Exception lastException = null;
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            File.Move(sourceVarPath, archivedPath);
                            moveSuccess = true;
                            break;
                        }
                        catch (IOException ex)
                        {
                            lastException = ex;
                            if (attempt < 3)
                            {
                                await ReleaseFileHandlesAsync(1000 * attempt);
                                _imageManager?.CloseFileHandles(sourceVarPath);
                            }
                        }
                    }
                    
                    if (!moveSuccess)
                    {
                        throw new IOException($"Could not move file to archive after 3 attempts. Please close any programs accessing this file. Error: {lastException?.Message}", lastException);
                    }
                    
                    sourcePathForProcessing = archivedPath; // Read from archive
                    finalOutputPath = sourceVarPath; // Write back to original location (now empty)
                }
                
                // STEP 2: Now work with the source file (from archive or after moving to archive)
                // Create temp file for the converted version in the output directory
                string outputDirectory = Path.GetDirectoryName(finalOutputPath);
                string tempOutputPath = Path.Combine(outputDirectory, "~temp_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + filename);
                
                // Delete temp file if it already exists
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }
                
                progressCallback?.Invoke("🔍 Analyzing package...", 0, textureConversions.Count);

                try
                {
                    int processedCount = 0;
                    long originalTotalSize = 0;
                    long newTotalSize = 0;
                    var conversionDetails = new System.Collections.Concurrent.ConcurrentBag<string>();

                    // Open source VAR (from archive or after moving to archive)
                    using (var sourceArchive = SharpCompressHelper.OpenForRead(sourcePathForProcessing))
                    using (var outputArchive = ZipArchive.Create())
                    {
                        string originalMetaJson = null;

                        var allEntries = sourceArchive.Entries.ToList();
                        var conversionInputs = new List<(string fullName, DateTimeOffset lastWriteTime, byte[] data, (string targetResolution, int originalWidth, int originalHeight, long originalSize) info)>();
                        
                        progressCallback?.Invoke($"📚 Reading {allEntries.Count} files from archive...", 0, textureConversions.Count);

                        foreach (var entry in allEntries)
                        {
                            if (entry.Key.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                            {
                                using (var stream = entry.OpenEntryStream())
                                using (var reader = new StreamReader(stream))
                                {
                                    originalMetaJson = await reader.ReadToEndAsync();
                                }
                                continue;
                            }

                            if (textureConversions.TryGetValue(entry.Key, out var conversionInfo))
                            {
                                using (var stream = entry.OpenEntryStream())
                                using (var ms = new MemoryStream())
                                {
                                    await stream.CopyToAsync(ms);
                                    conversionInputs.Add((entry.Key, entry.LastModifiedTime ?? DateTimeOffset.Now, ms.ToArray(), conversionInfo));
                                }
                            }
                        }

                        var convertedTextures = new System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] data, DateTimeOffset lastWriteTime)>();
                        int totalConversions = conversionInputs.Count;
                        
                        if (totalConversions > 0)
                        {
                            progressCallback?.Invoke($"🖼️  Starting conversion of {totalConversions} texture(s)...", 0, totalConversions);
                        }

                        // Use adaptive memory-aware parallelism with proper async I/O
                        System.Diagnostics.Debug.WriteLine($"[TEXTURE_CONVERSION_START] Processing {totalConversions} textures with adaptive parallelism");
                        
                        // OPTIMIZATION: Use full CPU cores for texture conversion (CPU-bound operation)
                        // Texture resizing is CPU-intensive, not I/O-bound, so we can use all cores
                        int maxConcurrentTextures = Math.Max(2, Environment.ProcessorCount); // Full parallelism for CPU-bound work
                        using (var semaphore = new System.Threading.SemaphoreSlim(maxConcurrentTextures))
                        {
                            var tasks = conversionInputs.Select(async item =>
                            {
                                await semaphore.WaitAsync();
                                try
                                {
                                    var (fullName, lastWriteTime, sourceData, conversionInfo) = item;

                                    System.Threading.Interlocked.Add(ref originalTotalSize, sourceData.Length);

                                    // Convert texture asynchronously
                                    int targetDimension = TextureConverter.GetTargetDimension(conversionInfo.targetResolution);
                                    string extension = Path.GetExtension(fullName);
                                    byte[] convertedData = await System.Threading.Tasks.Task.Run(() => 
                                        _textureConverter.ResizeImage(sourceData, targetDimension, extension));

                                    int current = System.Threading.Interlocked.Increment(ref processedCount);
                                    progressCallback?.Invoke($"🖼️  [{current}/{totalConversions}] Converting: {Path.GetFileName(fullName)}", current, Math.Max(1, totalConversions));

                                    if (convertedData != null)
                                    {
                                        System.Threading.Interlocked.Add(ref newTotalSize, convertedData.Length);
                                        
                                        string textureName = Path.GetFileName(fullName);
                                        string originalRes = GetResolutionString(conversionInfo.originalWidth, conversionInfo.originalHeight);
                                        string detail = $"  • {textureName}: {originalRes} → {conversionInfo.targetResolution} ({FormatBytes(sourceData.Length)} → {FormatBytes(convertedData.Length)})";
                                        conversionDetails.Add(detail);
                                        
                                        convertedTextures[fullName] = (convertedData, lastWriteTime);
                                    }
                                    else
                                    {
                                        System.Threading.Interlocked.Add(ref newTotalSize, sourceData.Length);
                                    }
                                }
                                finally
                                {
                                    semaphore.Release();
                                }
                            }).ToArray();

                            // Wait for all texture conversions to complete
                            await System.Threading.Tasks.Task.WhenAll(tasks);
                            System.Diagnostics.Debug.WriteLine($"[TEXTURE_CONVERSION_COMPLETE] All {totalConversions} textures processed");
                        }

                        progressCallback?.Invoke("📝 Writing optimized package...", totalConversions, totalConversions);
                        
                        int writeIndex = 0;
                        int totalWrites = allEntries.Count;
                        
                        foreach (var entry in allEntries)
                        {
                            if (entry.Key.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            
                            writeIndex++;
                            if (writeIndex % 100 == 0)
                            {
                                progressCallback?.Invoke($"📝 Writing files... ({writeIndex}/{totalWrites})", totalConversions, totalConversions);
                            }

                            if (convertedTextures.TryGetValue(entry.Key, out var converted))
                            {
                                outputArchive.AddEntry(entry.Key, new MemoryStream(converted.data));
                            }
                            else
                            {
                                // Try to read source first before creating entry to prevent corrupted empty entries
                                try
                                {
                                    using (var sourceStream = entry.OpenEntryStream())
                                    using (var ms = new MemoryStream())
                                    {
                                        await sourceStream.CopyToAsync(ms);
                                        outputArchive.AddEntry(entry.Key, new MemoryStream(ms.ToArray()));
                                    }
                                }
                                catch (InvalidDataException ex)
                                {
                                    // Skip entries with unsupported compression methods
                                    Console.WriteLine($"[WRITE-SKIP] Skipping {entry.Key} due to unsupported compression: {ex.Message}");
                                }
                                catch (Exception ex)
                                {
                                    // Skip entries that cannot be read
                                    Console.WriteLine($"[WRITE-SKIP] Skipping {entry.Key}: {ex.GetType().Name}: {ex.Message}");
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(originalMetaJson))
                        {
                            progressCallback?.Invoke("📋 Updating package metadata...", totalConversions, totalConversions);
                            string updatedMetaJson = UpdateMetaJsonDescription(originalMetaJson, conversionDetails, originalTotalSize, newTotalSize);

                            outputArchive.AddEntry("meta.json", new MemoryStream(Encoding.UTF8.GetBytes(updatedMetaJson)));
                        }
                        
                        // Save the archive
                        using (var outputFileStream = new FileStream(tempOutputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            outputArchive.SaveTo(outputFileStream, CompressionType.Deflate);
                        }
                    }

                    progressCallback?.Invoke("✅ Finalizing package...", textureConversions.Count, textureConversions.Count);
                    
                    // STEP 3: Move the converted temp file to the final output location
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
                            // Overwriting in the same location - delete the old file
                            File.Delete(finalOutputPath);
                        }
                    }
                    File.Move(tempOutputPath, finalOutputPath);
                    
                    // Force timestamp update to ensure cache invalidation
                    File.SetLastWriteTimeUtc(finalOutputPath, DateTime.UtcNow);

                    progressCallback?.Invoke("✨ Texture optimization complete!", textureConversions.Count, textureConversions.Count);
                    
                    // Get file sizes for statistics
                    long convertedSize = new FileInfo(finalOutputPath).Length;
                    
                    // Use the original file size we captured at the start
                    return (finalOutputPath, originalFileSize, convertedSize, conversionDetails.Count);
                }
                catch
                {
                    // On error, clean up and restore if needed
                    try
                    {
                        // Delete temp file if exists
                        if (File.Exists(tempOutputPath))
                            File.Delete(tempOutputPath);
                        
                        // Only restore if we moved the file to archive (not re-optimizing from archive)
                        if (!isSourceInArchive && archivedPath != null && File.Exists(archivedPath) && !File.Exists(sourceVarPath))
                        {
                            File.Move(archivedPath, sourceVarPath);
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
                progressCallback?.Invoke($"Error: {ex.Message}", 0, 0);
                throw;
            }
        }

        /// <summary>
        /// Updates the meta.json description with conversion details
        /// </summary>
        private string UpdateMetaJsonDescription(string originalMetaJson, System.Collections.Concurrent.ConcurrentBag<string> conversionDetails, long originalSize, long newSize)
        {
            try
            {
                using (var doc = JsonDocument.Parse(originalMetaJson))
                {
                    var root = doc.RootElement;
                    var options = new JsonWriterOptions { Indented = true };
                    
                    using (var stream = new MemoryStream())
                    {
                        using (var writer = new Utf8JsonWriter(stream, options))
                        {
                            writer.WriteStartObject();
                            
                            foreach (var property in root.EnumerateObject())
                            {
                                if (property.Name.Equals("description", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Build enhanced description with machine-readable flags
                                    string originalDescription = property.Value.GetString() ?? "";
                                    
                                    var descriptionBuilder = new StringBuilder();
                                    descriptionBuilder.AppendLine("–ï¸ TEXTURE-OPTIMIZED VERSION");
                                    descriptionBuilder.AppendLine();
                                    descriptionBuilder.AppendLine($"Textures Converted: {conversionDetails.Count}");
                                    descriptionBuilder.AppendLine($"Space Saved: {FormatBytes(originalSize - newSize)} ({(originalSize > 0 ? (100.0 * (originalSize - newSize) / originalSize).ToString("F1") : "0")}%)");
                                    descriptionBuilder.AppendLine();
                                    
                                    // Add machine-readable conversion data
                                    descriptionBuilder.AppendLine("[VPM_TEXTURE_CONVERSION_DATA]");
                                    foreach (var detail in conversionDetails)
                                    {
                                        descriptionBuilder.AppendLine(detail);
                                    }
                                    descriptionBuilder.AppendLine("[/VPM_TEXTURE_CONVERSION_DATA]");
                                    descriptionBuilder.AppendLine();
                                    descriptionBuilder.AppendLine("─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─");
                                    descriptionBuilder.AppendLine("ORIGINAL DESCRIPTION:");
                                    descriptionBuilder.Append(originalDescription);
                                    
                                    writer.WriteString(property.Name, descriptionBuilder.ToString());
                                }
                                else
                                {
                                    property.WriteTo(writer);
                                }
                            }
                            
                            writer.WriteEndObject();
                        }
                        
                        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
                    }
                }
            }
            catch
            {
                // If JSON parsing fails, return original
                return originalMetaJson;
            }
        }

        /// <summary>
        /// Gets a resolution string from dimensions
        /// </summary>
        private string GetResolutionString(int width, int height)
        {
            int maxDim = Math.Max(width, height);
            if (maxDim >= 7680) return "8K";
            if (maxDim >= 4096) return "4K";
            if (maxDim >= 2048) return "2K";
            if (maxDim >= 1024) return "1K";
            return $"{width}x{height}";
        }

        /// <summary>
        /// Formats bytes to human-readable string
        /// </summary>
        private string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        /// <summary>
        /// Validates that a VAR file can be repackaged
        /// </summary>
        public bool ValidateVarFile(string varPath, out string errorMessage)
        {
            errorMessage = null;

            try
            {
                if (!File.Exists(varPath))
                {
                    errorMessage = "VAR file does not exist";
                    return false;
                }

                // Try to open as ZIP archive
                using (var archive = SharpCompressHelper.OpenForRead(varPath))
                {
                    // Check for meta.json
                    var metaEntry = SharpCompressHelper.FindEntry(archive, "meta.json");
                    if (metaEntry == null)
                    {
                        errorMessage = "VAR file is missing meta.json";
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Invalid VAR file: {ex.Message}";
                return false;
            }
        }
    }
}

