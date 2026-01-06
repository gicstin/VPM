using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;

namespace VPM.Services
{
    /// <summary>
    /// Validates texture references in scene JSON files against actual files in packages
    /// </summary>
    public class TextureValidator
    {
        // Static cache for texture info (persists until app closes)
        // Key is normalized (lowercase) to ensure consistency
        private static ConcurrentDictionary<string, (string resolution, long fileSize, int width, int height)> _textureCache = 
            new ConcurrentDictionary<string, (string resolution, long fileSize, int width, int height)>();
        
        // Track conversion flags for each package to invalidate cache when needed
        private static ConcurrentDictionary<string, string> _packageConversionFlags = new ConcurrentDictionary<string, string>();
        
        // Track file modification times to invalidate cache when package changes
        private static ConcurrentDictionary<string, DateTime> _packageModificationTimes = new ConcurrentDictionary<string, DateTime>();
        /// <summary>
        /// Information about a texture file
        /// </summary>
        public class TextureInfo : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;

            private string _resolution;
            private long _fileSize;
            private int _width;
            private int _height;
            private int _originalWidth;  // Track original dimensions for conversion capability checks
            private int _originalHeight;

            public string PackageName { get; set; }
            public string TextureType { get; set; }
            public string ReferencedPath { get; set; }
            public bool Exists { get; set; }
            public string ErrorMessage { get; set; }
            
            private string _originalResolution;
            private long _originalFileSize;
            private long _archiveFileSize;

            public string OriginalResolution 
            { 
                get 
                {
                    // Prioritize manually set OriginalResolution (from metadata)
                    if (!string.IsNullOrEmpty(_originalResolution)) return _originalResolution;
                    
                    // Fall back to archive resolution if available
                    if (HasArchiveSource && ArchiveMaxDimension > 0)
                        return TextureUtils.GetResolutionLabel(ArchiveMaxDimension);
                    
                    // Use OriginalWidth if set from metadata but label wasn't
                    if (OriginalWidth > 0)
                        return TextureUtils.GetResolutionLabel(OriginalWidth);
                    
                    return Resolution;
                }
                set
                {
                    if (_originalResolution == value) return;
                    _originalResolution = value;
                    OnPropertyChanged(nameof(OriginalResolution));
                }
            }

            public long OriginalFileSize 
            { 
                get
                {
                    // Prioritize manually set OriginalFileSize (from metadata)
                    if (_originalFileSize > 0) return _originalFileSize;
                    
                    // Fall back to archive size if available but no metadata
                    // This was previously missing and caused optimized packages to show 0.0% saved
                    if (HasArchiveSource && ArchiveMaxDimension > 0 && _archiveFileSize > 0)
                        return _archiveFileSize;
                    
                    // Fall back to current FileSize as base if no metadata or archive
                    return FileSize;
                }
                set
                {
                    if (_originalFileSize == value) return;
                    _originalFileSize = value;
                    OnPropertyChanged(nameof(OriginalFileSize));
                    OnPropertyChanged(nameof(OriginalFileSizeFormatted));
                    OnPropertyChanged(nameof(CompressionPercentage));
                }
            }
            
            /// <summary>
            /// Original dimensions (set once, never changes) - used for conversion capability checks
            /// </summary>
            public int OriginalWidth 
            { 
                get => _originalWidth;
                set 
                {
                    if (_originalWidth == value) return;
                    _originalWidth = value;
                    OnPropertyChanged(nameof(OriginalWidth));
                    OnPropertyChanged(nameof(CanConvertTo8K));
                    OnPropertyChanged(nameof(CanConvertTo4K));
                    OnPropertyChanged(nameof(CanConvertTo2K));
                }
            }
            
            public int OriginalHeight 
            { 
                get => _originalHeight;
                set 
                {
                    if (_originalHeight == value) return;
                    _originalHeight = value;
                    OnPropertyChanged(nameof(OriginalHeight));
                }
            }
            
            public string OriginalFileSizeFormatted => TextureUtils.FormatFileSize(OriginalFileSize);
            
            public string CompressionPercentage
            {
                get
                {
                    if (OriginalFileSize == 0 || FileSize == 0) return "-";
                    double reduction = ((double)(OriginalFileSize - FileSize) / OriginalFileSize) * 100;
                    return $"{reduction:F1}%";
                }
            }

            public string Resolution
            {
                get => _resolution;
                set
                {
                    _resolution = value;
                    OnPropertyChanged(nameof(Resolution));
                    OnPropertyChanged(nameof(CanConvertTo8K));
                    OnPropertyChanged(nameof(CanConvertTo4K));
                    OnPropertyChanged(nameof(CanConvertTo2K));
                }
            }

            public long FileSize
            {
                get => _fileSize;
                set
                {
                    _fileSize = value;
                    OnPropertyChanged(nameof(FileSize));
                    OnPropertyChanged(nameof(FileSizeFormatted));
                    OnPropertyChanged(nameof(CompressionPercentage));
                }
            }

            public int Width
            {
                get => _width;
                set
                {
                    _width = value;
                    OnPropertyChanged(nameof(Width));
                }
            }

            public int Height
            {
                get => _height;
                set
                {
                    _height = value;
                    OnPropertyChanged(nameof(Height));
                }
            }

            // Conversion target selection - uses enum internally for cleaner state management
            public enum ConversionTarget { None, Keep, To2K, To4K, To8K }
            private ConversionTarget _target = ConversionTarget.None;

            private void SetTarget(ConversionTarget newTarget)
            {
                if (_target == newTarget) return;
                _target = newTarget;
                OnPropertyChanged(nameof(ConvertTo8K));
                OnPropertyChanged(nameof(ConvertTo4K));
                OnPropertyChanged(nameof(ConvertTo2K));
                OnPropertyChanged(nameof(KeepUnchanged));
                OnPropertyChanged(nameof(HasConversionSelected));
            }

            public bool ConvertTo8K
            {
                get => _target == ConversionTarget.To8K;
                set { if (value) SetTarget(ConversionTarget.To8K); else if (_target == ConversionTarget.To8K) SetTarget(ConversionTarget.None); }
            }

            public bool ConvertTo4K
            {
                get => _target == ConversionTarget.To4K;
                set { if (value) SetTarget(ConversionTarget.To4K); else if (_target == ConversionTarget.To4K) SetTarget(ConversionTarget.None); }
            }

            public bool ConvertTo2K
            {
                get => _target == ConversionTarget.To2K;
                set { if (value) SetTarget(ConversionTarget.To2K); else if (_target == ConversionTarget.To2K) SetTarget(ConversionTarget.None); }
            }

            public bool KeepUnchanged
            {
                get => _target == ConversionTarget.Keep;
                set { if (value) SetTarget(ConversionTarget.Keep); else if (_target == ConversionTarget.Keep) SetTarget(ConversionTarget.None); }
            }

            public void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

            // Archive source information for intelligent upscaling
            public bool HasArchiveSource { get; set; }
            public int ArchiveMaxDimension { get; set; }
            
            // Enabled states for conversion options
            // 1. Target resolution is <= current resolution (Downscale - always allowed)
            // 2. Target resolution is > current resolution (Upscale - ONLY allowed if original archive backup exists AND we have original high-res info)
            public bool CanConvertTo8K => (GetMaxDimension() >= 7680) || (HasArchiveSource && Math.Max(ArchiveMaxDimension, OriginalWidth) >= 7680);
            public bool CanConvertTo4K => (GetMaxDimension() >= 4096) || (HasArchiveSource && Math.Max(ArchiveMaxDimension, OriginalWidth) >= 4096);
            public bool CanConvertTo2K => (GetMaxDimension() >= 2048) || (HasArchiveSource && Math.Max(ArchiveMaxDimension, OriginalWidth) >= 2048);

            public string FileSizeFormatted => TextureUtils.FormatFileSize(FileSize);

            private int GetMaxDimension()
            {
                return Math.Max(Width, Height);
            }
            
            /// <summary>
            /// Returns true if an upscale is possible (has archive source with higher resolution)
            /// </summary>
            public bool HasUpscaleSource => HasArchiveSource && ArchiveMaxDimension > GetMaxDimension();

            private int GetOriginalMaxDimension()
            {
                // If we have an archive source, that is the TRUE original source
                if (HasArchiveSource && ArchiveMaxDimension > 0)
                    return ArchiveMaxDimension;

                // If no archive source, we CANNOT upscale. 
                // Metadata might say original was 8K, but if current is 2K and we have no backup, 
                // the effective "original" for future operations is 2K.
                int currentMax = GetMaxDimension();
                
                if (OriginalWidth > 0 && OriginalHeight > 0)
                {
                    int metaMax = Math.Max(OriginalWidth, OriginalHeight);
                    // Only trust metadata if it doesn't imply an upscale we can't perform
                    return Math.Min(currentMax, metaMax);
                }

                return currentMax;
            }

            public bool HasConversionSelected => ConvertTo8K || ConvertTo4K || ConvertTo2K;

            /// <summary>
            /// Checks if the selected conversion target is different from the current resolution
            /// </summary>
            public bool HasActualConversion
            {
                get
                {
                    if (!HasConversionSelected) return false;
                    
                    // Check if selected target is different from current resolution
                    // AND it's a valid/available conversion
                    if (ConvertTo8K && Resolution != "8K" && CanConvertTo8K) return true;
                    if (ConvertTo4K && Resolution != "4K" && CanConvertTo4K) return true;
                    if (ConvertTo2K && Resolution != "2K" && CanConvertTo2K) return true;
                    
                    return false;
                }
            }
            
            /// <summary>
            /// Debug information for troubleshooting texture conversion issues
            /// </summary>
            public string GetDebugInfo()
            {
                return $"Path: {ReferencedPath} | " +
                       $"Resolution: {Resolution} | " +
                       $"Dims: {Width}x{Height} | " +
                       $"OrigDims: {OriginalWidth}x{OriginalHeight} | " +
                       $"HasConversionSelected: {HasConversionSelected} | " +
                       $"HasActualConversion: {HasActualConversion} | " +
                       $"ConvertTo8K: {ConvertTo8K} | " +
                       $"ConvertTo4K: {ConvertTo4K} | " +
                       $"ConvertTo2K: {ConvertTo2K} | " +
                       $"CanConvertTo8K: {CanConvertTo8K} | " +
                       $"CanConvertTo4K: {CanConvertTo4K} | " +
                       $"CanConvertTo2K: {CanConvertTo2K} | " +
                       $"HasArchiveSource: {HasArchiveSource} | " +
                       $"ArchiveMaxDim: {ArchiveMaxDimension}";
            }

            /// <summary>
            /// Sets the default conversion target based on current texture resolution
            /// The bubble selection should match what's actually in the package
            /// </summary>
            public void SetDefaultConversionTarget()
            {
                // Select the bubble that matches the texture's CURRENT resolution
                // ONLY if that bubble is enabled (available)
                _target = Resolution switch
                {
                    "8K" when CanConvertTo8K => ConversionTarget.To8K,
                    "4K" when CanConvertTo4K => ConversionTarget.To4K,
                    "2K" when CanConvertTo2K => ConversionTarget.To2K,
                    _ => ConversionTarget.Keep // 1K, unknown, or bubble disabled
                };
            }
        }

        /// <summary>
        /// Result of texture validation
        /// </summary>
        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public List<TextureInfo> Textures { get; set; } = new List<TextureInfo>();
            public string ErrorMessage { get; set; }
            public bool UseThoroughScan { get; set; }
            public int TotalTextureReferences => Textures.Count;
            public int FoundCount => Textures.Count(t => t.Exists);
            public int MissingCount => Textures.Count(t => !t.Exists);
        }

        /// <summary>
        /// Validates textures for a package by reading from meta.json
        /// </summary>
        /// <param name="packagePath">Path to the .var file or unarchived package folder</param>
        /// <param name="archiveFolder">Optional path to ArchivedPackages folder to check for original source</param>
        /// <returns>Validation result</returns>
        public ValidationResult ValidatePackageTextures(string packagePath, string archiveFolder = null)
        {
            var result = new ValidationResult { IsValid = true };
            
            // Validate that the package file exists and is accessible
            if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
            {
                result.ErrorMessage = $"Package file not found or not accessible: {packagePath}";
                return result;
            }
            
            // Check if original archive exists for intelligent upscaling
            string archivePackagePath = null;
            bool hasArchiveSource = false;
            if (!string.IsNullOrEmpty(archiveFolder))
            {
                string filename = Path.GetFileName(packagePath);
                archivePackagePath = Path.Combine(archiveFolder, filename);
                hasArchiveSource = File.Exists(archivePackagePath);
            }

            try
            {
                bool isVarFile = packagePath.EndsWith(".var", StringComparison.OrdinalIgnoreCase);

                // Use fast TextureDetector to find orphaned images (textures)
                List<string> detectedTexturePaths = new List<string>();
                
                // Enable debug for packages with texture conversions to diagnose detection issues
                bool enableTextureDebug = false;
                
                if (isVarFile)
                {
                    detectedTexturePaths = TextureDetector.DetectTexturesInVarFile(packagePath, enableTextureDebug);
                }
                else
                {
                    // For unarchived packages, scan Custom folder for orphaned images
                    detectedTexturePaths = DetectTexturesInUnarchived(packagePath);
                }

                if (detectedTexturePaths.Count == 0)
                {
                    result.IsValid = true;
                    return result;
                }

                // Get archive texture dimensions and file sizes if archive exists
                Dictionary<string, (int width, int height)> archiveTextureDimensions = null;
                Dictionary<string, long> archiveFileSizes = null;
                if (hasArchiveSource)
                {
                    archiveTextureDimensions = GetArchiveImageDimensions(archivePackagePath);
                    archiveFileSizes = GetArchiveFileSizes(archivePackagePath);
                }

                // For VAR files: Open archive ONCE and process all textures (10-50x faster)
                if (isVarFile)
                {
                    using (var archive = ZipArchive.Open(packagePath))
                    {
                        // Build entry lookup dictionary for O(1) access instead of O(n) per texture
                        var entryLookup = new Dictionary<string, IArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in archive.Entries)
                        {
                            if (!entry.IsDirectory)
                                entryLookup[entry.Key] = entry;
                        }
                        
                        foreach (var texturePath in detectedTexturePaths)
                        {
                            try
                            {
                                var info = CreateTextureInfoFromArchive(archive, entryLookup, texturePath, 
                                    hasArchiveSource, archiveTextureDimensions, archiveFileSizes, enableTextureDebug);
                                if (info != null)
                                    result.Textures.Add(info);
                            }
                            catch
                            {
                                // Silently skip files that fail to process
                            }
                        }
                    }
                }
                else
                {
                    // For unarchived packages, parallel processing is safe
                    var textureInfos = new ConcurrentBag<TextureInfo>();
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = ParallelArchiveProcessor.GetOptimalParallelism("io")
                    };
                    
                    Parallel.ForEach(detectedTexturePaths, parallelOptions, texturePath =>
                    {
                        try
                        {
                            ProcessTextureFileParallel(packagePath, texturePath, isVarFile, hasArchiveSource, 
                                archiveTextureDimensions, archiveFileSizes, textureInfos, enableTextureDebug);
                        }
                        catch
                        {
                            // Silently skip files that fail to process
                        }
                    });
                    
                    // Add all collected texture infos to result
                    foreach (var textureInfo in textureInfos)
                    {
                        result.Textures.Add(textureInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error during validation: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Detects textures in an unarchived package folder using orphaned image logic.
        /// Uses FULL PATHS to handle cases where same filename exists in different directories.
        /// </summary>
        private List<string> DetectTexturesInUnarchived(string packagePath)
        {
            var textures = new List<string>();

            try
            {
                string customPath = System.IO.Path.Combine(packagePath, "Custom");
                if (!Directory.Exists(customPath))
                    return textures;

                // Get all files in Custom folder (using full paths)
                var allFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allImageFiles = new List<(string relativePath, string fullPath)>();

                foreach (var file in Directory.GetFiles(customPath, "*.*", SearchOption.AllDirectories))
                {
                    var relativePath = System.IO.Path.GetRelativePath(packagePath, file).Replace("\\", "/").ToLower();
                    allFilePaths.Add(relativePath);

                    var ext = System.IO.Path.GetExtension(file);
                    if (TextureUtils.IsImageExtension(ext))
                    {
                        allImageFiles.Add((relativePath, file));
                    }
                }

                // Check each image for orphaned status (using full paths)
                foreach (var (relativePath, fullPath) in allImageFiles)
                {
                    if (IsOrphanedImageFile(relativePath, allFilePaths))
                    {
                        textures.Add(relativePath);
                    }
                }
            }
            catch
            {
                // Silently fail
            }

            return textures;
        }

        /// <summary>
        /// Checks if an image file is orphaned (no companion files in same directory).
        /// Delegates to TextureDetector.IsOrphanedImagePath for consistent logic.
        /// </summary>
        private static bool IsOrphanedImageFile(string imagePath, IEnumerable<string> allFilePaths) =>
            TextureDetector.IsOrphanedImagePath(imagePath, allFilePaths);

        /// <summary>
        /// Creates a TextureInfo from an already-open archive (avoids reopening archive per texture).
        /// This is 10-50x faster than CreateTextureInfo for VAR files.
        /// </summary>
        private TextureInfo CreateTextureInfoFromArchive(IArchive archive, Dictionary<string, IArchiveEntry> entryLookup,
            string texturePath, bool hasArchiveSource, Dictionary<string, (int width, int height)> archiveTextureDimensions,
            Dictionary<string, long> archiveFileSizes, bool enableDebug = false)
        {
            string fileName = System.IO.Path.GetFileName(texturePath);

            // O(1) lookup instead of O(n) FindEntryByPath
            if (!entryLookup.TryGetValue(texturePath, out var entry))
            {
                if (enableDebug) System.Diagnostics.Debug.WriteLine($"  SKIP (not found): {fileName}");
                return null;
            }

            // Get dimensions directly from entry using header-only read
            var (width, height) = SharpCompressHelper.GetImageDimensionsFromEntry(archive, entry);
            long fileSize = entry.Size;

            // Skip if we couldn't read dimensions
            if (width == 0 || height == 0)
            {
                if (enableDebug) System.Diagnostics.Debug.WriteLine($"  SKIP (no dims): {fileName} -> {width}x{height}");
                return null;
            }

            // Filter: Only include if max dimension > 512px (textures, not previews)
            int maxDim = Math.Max(width, height);
            if (maxDim <= 512)
            {
                if (enableDebug) System.Diagnostics.Debug.WriteLine($"  SKIP (too small): {fileName} -> {maxDim}px");
                return null;
            }

            // Determine texture type and resolution
            string textureType = TextureUtils.GetTextureType(texturePath);
            string resolution = TextureUtils.GetResolutionLabel(width, height);

            // Get archive dimensions and file sizes if available
            int archiveWidth = 0;
            int archiveHeight = 0;
            int archiveMaxDim = 0;
            if (hasArchiveSource && archiveTextureDimensions != null)
            {
                // Try exact path match first
                if (archiveTextureDimensions.TryGetValue(texturePath, out var archiveDims))
                {
                    archiveWidth = archiveDims.width;
                    archiveHeight = archiveDims.height;
                }
                else
                {
                    // Extension might have changed (e.g. .png -> .jpg)
                    // Try matching by filename without extension in the same directory
                    string dir = Path.GetDirectoryName(texturePath);
                    string fileNameNoExt = Path.GetFileNameWithoutExtension(texturePath);
                    
                    var match = archiveTextureDimensions.FirstOrDefault(kvp => 
                        Path.GetDirectoryName(kvp.Key).Equals(dir, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileNameWithoutExtension(kvp.Key).Equals(fileNameNoExt, StringComparison.OrdinalIgnoreCase));
                    
                    if (match.Key != null)
                    {
                        archiveWidth = match.Value.width;
                        archiveHeight = match.Value.height;
                    }
                }
                
                archiveMaxDim = Math.Max(archiveWidth, archiveHeight);
            }

            if (enableDebug)
            {
                System.Diagnostics.Debug.WriteLine($"  ADDED: {fileName} (current={maxDim}px [{width}x{height}], archive={archiveMaxDim}px, {textureType})");
            }

            var textureInfo = new TextureInfo
            {
                TextureType = textureType,
                ReferencedPath = texturePath,
                Exists = true,
                Resolution = resolution,
                FileSize = fileSize,
                Width = width,
                Height = height,
                // Only set OriginalWidth/Height if we actually found them in the archive
                OriginalWidth = archiveWidth,
                OriginalHeight = archiveHeight,
                HasArchiveSource = hasArchiveSource && archiveMaxDim > 0,
                ArchiveMaxDimension = archiveMaxDim,
                _archiveFileSize = 0 // Will be set below
            };

            // Set OriginalResolution and OriginalFileSize from archive if available
            if (hasArchiveSource && archiveMaxDim > 0)
            {
                // Try to get archive file size (with fuzzy extension match if needed)
                if (archiveFileSizes != null)
                {
                    if (archiveFileSizes.TryGetValue(texturePath, out long archiveSize))
                    {
                        textureInfo._archiveFileSize = archiveSize;
                    }
                    else
                    {
                        string dir = Path.GetDirectoryName(texturePath);
                        string fileNameNoExt = Path.GetFileNameWithoutExtension(texturePath);
                        var match = archiveFileSizes.FirstOrDefault(kvp => 
                            Path.GetDirectoryName(kvp.Key).Equals(dir, StringComparison.OrdinalIgnoreCase) &&
                            Path.GetFileNameWithoutExtension(kvp.Key).Equals(fileNameNoExt, StringComparison.OrdinalIgnoreCase));
                        
                        if (match.Key != null)
                        {
                            textureInfo._archiveFileSize = match.Value;
                        }
                    }
                }
            }

            textureInfo.SetDefaultConversionTarget();
            return textureInfo;
        }

        /// <summary>
        /// Creates a TextureInfo object from texture file data. Returns null if texture should be skipped.
        /// Consolidates common logic from ProcessTextureFile and ProcessTextureFileParallel.
        /// NOTE: For VAR files, prefer CreateTextureInfoFromArchive which is 10-50x faster.
        /// </summary>
        private TextureInfo CreateTextureInfo(string packagePath, string texturePath, bool isVarFile,
            bool hasArchiveSource, Dictionary<string, (int width, int height)> archiveTextureDimensions,
            Dictionary<string, long> archiveFileSizes, bool enableDebug = false)
        {
            string fileName = System.IO.Path.GetFileName(texturePath);

            // Check if file exists
            bool exists = false;
            if (isVarFile)
            {
                using (var zipFile = ZipArchive.Open(packagePath))
                {
                    exists = SharpCompressHelper.FindEntryByPath(zipFile, texturePath) != null;
                }
            }
            else
            {
                string fullPath = System.IO.Path.Combine(packagePath, texturePath);
                exists = File.Exists(fullPath);
            }

            if (!exists)
            {
                if (enableDebug) System.Diagnostics.Debug.WriteLine($"  SKIP (not found): {fileName}");
                return null;
            }

            // Get resolution and file size
            var (resolution, fileSize, width, height) = GetTextureInfo(packagePath, texturePath, isVarFile);

            // Skip if we couldn't read dimensions
            if (width == 0 || height == 0)
            {
                if (enableDebug) System.Diagnostics.Debug.WriteLine($"  SKIP (no dims): {fileName} -> {width}x{height}");
                return null;
            }

            // Filter: Only include if max dimension > 512px (textures, not previews)
            int maxDim = Math.Max(width, height);
            if (maxDim <= 512)
            {
                if (enableDebug) System.Diagnostics.Debug.WriteLine($"  SKIP (too small): {fileName} -> {maxDim}px");
                return null;
            }

            // Determine texture type using shared utility
            string textureType = TextureUtils.GetTextureType(texturePath);

            // Get archive dimensions and file sizes if available
            int archiveWidth = 0;
            int archiveHeight = 0;
            int archiveMaxDim = 0;
            if (hasArchiveSource && archiveTextureDimensions != null)
            {
                // Try exact path match first
                if (archiveTextureDimensions.TryGetValue(texturePath, out var archiveDims))
                {
                    archiveWidth = archiveDims.width;
                    archiveHeight = archiveDims.height;
                }
                else
                {
                    // Extension might have changed (e.g. .png -> .jpg)
                    // Try matching by filename without extension in the same directory
                    string dir = Path.GetDirectoryName(texturePath);
                    string fileNameNoExt = Path.GetFileNameWithoutExtension(texturePath);
                    
                    var match = archiveTextureDimensions.FirstOrDefault(kvp => 
                        Path.GetDirectoryName(kvp.Key).Equals(dir, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileNameWithoutExtension(kvp.Key).Equals(fileNameNoExt, StringComparison.OrdinalIgnoreCase));
                    
                    if (match.Key != null)
                    {
                        archiveWidth = match.Value.width;
                        archiveHeight = match.Value.height;
                    }
                }
                
                archiveMaxDim = Math.Max(archiveWidth, archiveHeight);
            }

            if (enableDebug)
            {
                System.Diagnostics.Debug.WriteLine($"  ADDED: {fileName} (current={maxDim}px [{width}x{height}], archive={archiveMaxDim}px, {textureType})");
                if (hasArchiveSource && archiveMaxDim == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"    WARNING: No archive dims found!");
                }
            }

            var textureInfo = new TextureInfo
            {
                TextureType = textureType,
                ReferencedPath = texturePath,
                Exists = true,
                Resolution = resolution,
                FileSize = fileSize,
                Width = width,
                Height = height,
                // Only set OriginalWidth/Height if we actually found them in the archive
                OriginalWidth = archiveWidth,
                OriginalHeight = archiveHeight,
                HasArchiveSource = hasArchiveSource && archiveMaxDim > 0,
                ArchiveMaxDimension = archiveMaxDim,
                _archiveFileSize = 0 // Will be set below
            };

            // Set OriginalResolution and OriginalFileSize from archive if available
            if (hasArchiveSource && archiveMaxDim > 0)
            {
                // Try to get archive file size (with fuzzy extension match if needed)
                if (archiveFileSizes != null)
                {
                    if (archiveFileSizes.TryGetValue(texturePath, out long archiveSize))
                    {
                        textureInfo._archiveFileSize = archiveSize;
                    }
                    else
                    {
                        // Normalize directory separator for comparison
                        string dir = Path.GetDirectoryName(texturePath)?.Replace("\\", "/");
                        string fileNameNoExt = Path.GetFileNameWithoutExtension(texturePath);
                        
                        var match = archiveFileSizes.FirstOrDefault(kvp => 
                        {
                            string kvpDir = Path.GetDirectoryName(kvp.Key)?.Replace("\\", "/");
                            return string.Equals(kvpDir, dir, StringComparison.OrdinalIgnoreCase) &&
                                   Path.GetFileNameWithoutExtension(kvp.Key).Equals(fileNameNoExt, StringComparison.OrdinalIgnoreCase);
                        });
                        
                        if (match.Key != null)
                        {
                            textureInfo._archiveFileSize = match.Value;
                        }
                    }
                }
            }

            textureInfo.SetDefaultConversionTarget();
            return textureInfo;
        }

        /// <summary>
        /// Processes a single texture file and adds it to the result
        /// </summary>
        private void ProcessTextureFile(string packagePath, string texturePath, bool isVarFile, ValidationResult result,
            bool hasArchiveSource = false, Dictionary<string, (int width, int height)> archiveTextureDimensions = null, 
            Dictionary<string, long> archiveFileSizes = null)
        {
            try
            {
                var info = CreateTextureInfo(packagePath, texturePath, isVarFile, hasArchiveSource, 
                    archiveTextureDimensions, archiveFileSizes, false);
                if (info != null) result.Textures.Add(info);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProcessTextureFile] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes a single texture file for parallel execution (thread-safe version)
        /// </summary>
        private void ProcessTextureFileParallel(string packagePath, string texturePath, bool isVarFile,
            bool hasArchiveSource, Dictionary<string, (int width, int height)> archiveTextureDimensions,
            Dictionary<string, long> archiveFileSizes, ConcurrentBag<TextureInfo> textureInfos, bool enableDebug = false)
        {
            try
            {
                var info = CreateTextureInfo(packagePath, texturePath, isVarFile, hasArchiveSource,
                    archiveTextureDimensions, archiveFileSizes, enableDebug);
                if (info != null) textureInfos.Add(info);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProcessTextureFileParallel] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts conversion flags from meta.json description
        /// </summary>
        private string GetConversionFlags(string packagePath, bool isVarFile)
        {
            try
            {
                if (isVarFile)
                {
                    using (var zipFile = ZipArchive.Open(packagePath))
                    {
                        var metaEntry = SharpCompressHelper.FindEntryByPath(zipFile, "meta.json");
                        if (metaEntry != null)
                        {
                            string metaJson = SharpCompressHelper.ReadEntryAsString(zipFile, metaEntry);
                            
                            // Look for conversion data flags
                            int startIdx = metaJson.IndexOf("[VPM_TEXTURE_CONVERSION_DATA]");
                            int endIdx = metaJson.IndexOf("[/VPM_TEXTURE_CONVERSION_DATA]");
                            
                            if (startIdx >= 0 && endIdx > startIdx)
                            {
                                return metaJson.Substring(startIdx, endIdx - startIdx + "[/VPM_TEXTURE_CONVERSION_DATA]".Length);
                            }
                        }
                    }
                }
            }
            catch { }
            
            return null;
        }
        
        /// <summary>
        /// Checks if cache should be invalidated for a package based on conversion flags or file modification
        /// </summary>
        private bool ShouldInvalidateCache(string packagePath, bool isVarFile)
        {
            if (string.IsNullOrEmpty(packagePath))
                return false;
            
            string normalizedPath = packagePath.ToLowerInvariant();
            bool shouldInvalidate = false;
            
            // Check file modification time
            try
            {
                if (File.Exists(packagePath))
                {
                    DateTime currentModTime = File.GetLastWriteTimeUtc(packagePath);
                    if (_packageModificationTimes.TryGetValue(normalizedPath, out DateTime cachedModTime))
                    {
                        if (currentModTime != cachedModTime)
                        {
                            // File was modified, invalidate cache
                            shouldInvalidate = true;
                            _packageModificationTimes[normalizedPath] = currentModTime;
                        }
                    }
                    else
                    {
                        // First time seeing this file
                        _packageModificationTimes[normalizedPath] = currentModTime;
                    }
                }
            }
            catch { }
            
            // Also check conversion flags
            string currentFlags = GetConversionFlags(packagePath, isVarFile);
            
            if (_packageConversionFlags.TryGetValue(normalizedPath, out string cachedFlags))
            {
                // If flags changed (or one was added/removed), invalidate cache
                if (cachedFlags != currentFlags)
                {
                    shouldInvalidate = true;
                    _packageConversionFlags[normalizedPath] = currentFlags;
                }
            }
            else
            {
                // First time seeing this package, store flags
                _packageConversionFlags[normalizedPath] = currentFlags;
            }
            
            if (shouldInvalidate)
            {
                // Clear all cache entries for this package
                var prefix = normalizedPath + "|";
                var keysToRemove = _textureCache.Keys.Where(k => k.StartsWith(prefix)).ToList();
                foreach (var key in keysToRemove)
                {
                    _textureCache.TryRemove(key, out _);
                }
            }
            
            return shouldInvalidate;
        }

        /// <summary>
        /// Gets detailed information about a texture file (resolution, size, dimensions)
        /// </summary>
        private (string resolution, long fileSize, int width, int height) GetTextureInfo(string packagePath, string texturePath, bool isVarFile)
        {
            // Check if cache should be invalidated due to conversion
            bool cacheInvalidated = ShouldInvalidateCache(packagePath, isVarFile);
            
            string cacheKey = $"{packagePath.ToLowerInvariant()}|{texturePath.ToLowerInvariant()}";
            
            // Check cache first
            if (_textureCache.TryGetValue(cacheKey, out var cachedResult))
            {
                return cachedResult;
            }
            // Strategy 1: Try with progressively larger buffer sizes
            int[] bufferSizes = { 8192, 16384, 32768, 65536, 131072 };
            
            foreach (int bufferSize in bufferSizes)
            {
                var result = GetTextureInfoWithBuffer(packagePath, texturePath, isVarFile, bufferSize);
                if (result.width > 0 && result.height > 0)
                {
                    _textureCache[cacheKey] = result;
                    return result;
                }
            }
            
            // Strategy 2: Try reading header only (streaming) for dimension detection
            // Benefit: 40-60% memory reduction by reading only first 64KB instead of entire file
            try
            {
                long fileSize = 0;
                byte[] headerBuffer = null;
                
                if (isVarFile)
                {
                    using (var zipFile = ZipArchive.Open(packagePath))
                    {
                        var entry = SharpCompressHelper.FindEntryByPath(zipFile, texturePath);
                        if (entry != null)
                        {
                            fileSize = entry.Size;
                            // Use streaming header read instead of loading entire file
                            headerBuffer = SharpCompressHelper.ReadEntryHeader(zipFile, entry, 65536);
                        }
                    }
                }
                else
                {
                    string fullPath = System.IO.Path.Combine(packagePath, texturePath);
                    if (File.Exists(fullPath))
                    {
                        var fileInfo = new FileInfo(fullPath);
                        fileSize = fileInfo.Length;
                        // Read only first 64KB for header parsing using pooled buffer
                        int bufferSize = Math.Min(65536, (int)fileInfo.Length);
                        byte[] pooledBuffer = BufferPool.RentBuffer(bufferSize);
                        try
                        {
                            using (var stream = File.OpenRead(fullPath))
                            {
                                int bytesRead = stream.Read(pooledBuffer, 0, bufferSize);
                                headerBuffer = new byte[bytesRead];
                                Array.Copy(pooledBuffer, 0, headerBuffer, 0, bytesRead);
                            }
                        }
                        finally
                        {
                            BufferPool.ReturnBuffer(pooledBuffer);
                        }
                    }
                }
                
                if (headerBuffer != null && headerBuffer.Length > 0)
                {
                    var (width, height) = ReadImageDimensionsFromBuffer(headerBuffer, headerBuffer.Length, texturePath);
                    if (width > 0 && height > 0)
                    {
                        string resolution = TextureUtils.GetResolutionLabel(width, height);
                        var finalResult = (resolution, fileSize, width, height);
                        _textureCache[cacheKey] = finalResult;
                        return finalResult;
                    }
                }
            }
            catch { }
            
            // Strategy 3: Try alternative parsing methods for specific formats
            try
            {
                var result = TryAlternativeImageParsing(packagePath, texturePath, isVarFile);
                if (result.width > 0 && result.height > 0)
                {
                    _textureCache[cacheKey] = result;
                    return result;
                }
            }
            catch { }
            
            var failResult = ("-", 0, 0, 0);
            _textureCache[cacheKey] = failResult;
            return failResult;
        }
        
        /// <summary>
        /// Alternative image parsing using different byte scanning strategies
        /// Uses memory pooling for efficient buffer management
        /// </summary>
        private (string resolution, long fileSize, int width, int height) TryAlternativeImageParsing(string packagePath, string texturePath, bool isVarFile)
        {
            try
            {
                // Rent buffer from pool instead of allocating new
                byte[] buffer = BufferPool.RentBuffer(65536);
                try
                {
                    long fileSize = 0;
                    int bytesRead = 0;
                    
                    if (isVarFile)
                    {
                        using (var zipFile = ZipArchive.Open(packagePath))
                        {
                            var entry = SharpCompressHelper.FindEntryByPath(zipFile, texturePath);
                            if (entry != null)
                            {
                                fileSize = entry.Size;
                                bytesRead = SharpCompressHelper.ReadEntryIntoBuffer(zipFile, entry, buffer, 0, buffer.Length);
                            }
                        }
                    }
                    else
                    {
                        string fullPath = System.IO.Path.Combine(packagePath, texturePath);
                        if (File.Exists(fullPath))
                        {
                            fileSize = new FileInfo(fullPath).Length;
                            using (var stream = File.OpenRead(fullPath))
                            {
                                bytesRead = stream.Read(buffer, 0, buffer.Length);
                            }
                        }
                    }
                    
                    if (bytesRead > 0)
                    {
                        // Try scanning entire buffer for dimension markers
                        var (width, height) = ScanBufferForDimensions(buffer, bytesRead, texturePath);
                        
                        if (width > 0 && height > 0)
                        {
                            string resolution = TextureUtils.GetResolutionLabel(width, height);
                            return (resolution, fileSize, width, height);
                        }
                    }
                }
                finally
                {
                    // Always return buffer to pool
                    BufferPool.ReturnBuffer(buffer);
                }
            }
            catch { }
            
            return ("-", 0, 0, 0);
        }
        
        /// <summary>
        /// Scans entire buffer looking for dimension patterns
        /// </summary>
        private (int width, int height) ScanBufferForDimensions(byte[] buffer, int length, string filename)
        {
            string ext = System.IO.Path.GetExtension(filename).ToLowerInvariant();
            int maxWidth = 0;
            int maxHeight = 0;
            
            // For JPEG, scan for all SOF markers
            if (ext == ".jpg" || ext == ".jpeg")
            {
                for (int i = 0; i < length - 10; i++)
                {
                    if (buffer[i] == 0xFF && buffer[i + 1] >= 0xC0 && buffer[i + 1] <= 0xCF && 
                        buffer[i + 1] != 0xC4 && buffer[i + 1] != 0xC8 && buffer[i + 1] != 0xCC)
                    {
                        int height = (buffer[i + 5] << 8) | buffer[i + 6];
                        int width = (buffer[i + 7] << 8) | buffer[i + 8];
                        
                        if (width > 0 && height > 0 && width < 100000 && height < 100000)
                        {
                            if (width > maxWidth)
                            {
                                maxWidth = width;
                                maxHeight = height;
                            }
                        }
                    }
                }
                return (maxWidth, maxHeight);
            }
            
            // For PNG, look for IHDR chunk anywhere
            if (ext == ".png")
            {
                for (int i = 0; i < length - 20; i++)
                {
                    if (buffer[i] == 'I' && buffer[i + 1] == 'H' && buffer[i + 2] == 'D' && buffer[i + 3] == 'R')
                    {
                        int width = (buffer[i + 4] << 24) | (buffer[i + 5] << 16) | (buffer[i + 6] << 8) | buffer[i + 7];
                        int height = (buffer[i + 8] << 24) | (buffer[i + 9] << 16) | (buffer[i + 10] << 8) | buffer[i + 11];
                        
                        if (width > 0 && height > 0 && width < 100000 && height < 100000)
                        {
                            if (width > maxWidth)
                            {
                                maxWidth = width;
                                maxHeight = height;
                            }
                        }
                    }
                }
                return (maxWidth, maxHeight);
            }
            
            return (0, 0);
        }

        /// <summary>
        /// Gets texture resolution with specific buffer size
        /// Uses memory pooling for efficient buffer management
        /// </summary>
        private (string resolution, long fileSize, int width, int height) GetTextureInfoWithBuffer(string packagePath, string texturePath, bool isVarFile, int bufferSize)
        {
            try
            {
                int width = 0, height = 0;
                long fileSize = 0;

                if (isVarFile)
                {
                    using (var zipFile = ZipArchive.Open(packagePath))
                    {
                        var entry = SharpCompressHelper.FindEntryByPath(zipFile, texturePath);
                        if (entry != null)
                        {
                            fileSize = entry.Size;
                            
                            // Read header into memory for parsing - safely cast long to int
                            long bufferSizeLong = Math.Min(entry.Size, (long)bufferSize);
                            int actualBufferSize = (int)Math.Min(bufferSizeLong, int.MaxValue);
                            
                            // Rent buffer from pool for efficiency
                            byte[] buffer = BufferPool.RentBuffer(actualBufferSize);
                            try
                            {
                                int bytesRead = SharpCompressHelper.ReadEntryIntoBuffer(zipFile, entry, buffer, 0, buffer.Length);
                                if (bytesRead > 0)
                                {
                                    (width, height) = ReadImageDimensionsFromBuffer(buffer, bytesRead, texturePath);
                                }
                            }
                            finally
                            {
                                BufferPool.ReturnBuffer(buffer);
                            }
                        }
                    }
                }
                else
                {
                    string fullPath = Path.Combine(packagePath, texturePath);
                    if (File.Exists(fullPath))
                    {
                        var fileInfo = new FileInfo(fullPath);
                        fileSize = fileInfo.Length;
                        
                        using (var stream = File.OpenRead(fullPath))
                        {
                            // Safely cast long to int
                            long bufferSizeLong = Math.Min(fileSize, (long)bufferSize);
                            int actualBufferSize = (int)Math.Min(bufferSizeLong, int.MaxValue);
                            
                            // Rent buffer from pool for efficiency
                            byte[] buffer = BufferPool.RentBuffer(actualBufferSize);
                            try
                            {
                                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                                (width, height) = ReadImageDimensionsFromBuffer(buffer, bytesRead, texturePath);
                            }
                            finally
                            {
                                BufferPool.ReturnBuffer(buffer);
                            }
                        }
                    }
                }

                string resolution = (width > 0 && height > 0) 
                    ? TextureUtils.GetResolutionLabel(width, height) 
                    : "-";

                return (resolution, fileSize, width, height);
            }
            catch
            {
                return ("-", 0, 0, 0);
            }
        }

        /// <summary>
        /// Reads image dimensions from buffer without loading full image
        /// Fast header-only reading for PNG, JPEG, BMP, GIF, TGA, WEBP
        /// </summary>
        private (int width, int height) ReadImageDimensionsFromBuffer(byte[] buffer, int bytesRead, string filename)
        {
            try
            {
                // Validate buffer before any access
                if (buffer == null || bytesRead < 4) return (0, 0);

                var ext = Path.GetExtension(filename).ToLowerInvariant();

                // PNG: 89 50 4E 47
                if (bytesRead >= 24 && buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
                {
                    int width = (buffer[16] << 24) | (buffer[17] << 16) | (buffer[18] << 8) | buffer[19];
                    int height = (buffer[20] << 24) | (buffer[21] << 16) | (buffer[22] << 8) | buffer[23];
                    return (width, height);
                }
                // JPEG: FF D8
                else if (bytesRead >= 2 && buffer[0] == 0xFF && buffer[1] == 0xD8)
                {
                    int pos = 2;
                    int maxWidth = 0;
                    int maxHeight = 0;
                    
                    while (pos + 2 < bytesRead)
                    {
                        // Find next marker
                        while (pos < bytesRead && buffer[pos] != 0xFF) pos++;
                        if (pos >= bytesRead - 1) break;

                        byte marker = buffer[pos + 1];
                        
                        // Skip padding bytes
                        if (marker == 0x00 || marker == 0xFF)
                        {
                            pos++;
                            continue;
                        }

                        pos += 2;
                        if (pos + 2 > bytesRead) break;

                        int length = (buffer[pos] << 8) | buffer[pos + 1];

                        // SOF markers (all variants)
                        if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7) || 
                            (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
                        {
                            if (pos + 7 <= bytesRead)
                            {
                                int height = (buffer[pos + 3] << 8) | buffer[pos + 4];
                                int width = (buffer[pos + 5] << 8) | buffer[pos + 6];
                                
                                if (width > 0 && height > 0 && width < 100000 && height < 100000)
                                {
                                    // Store the largest dimensions found in the buffer
                                    // Some JPEGs have thumbnails stored as separate markers before the main image
                                    if (width > maxWidth)
                                    {
                                        maxWidth = width;
                                        maxHeight = height;
                                    }
                                }
                            }
                        }

                        pos += length;
                        if (pos > bytesRead) break;
                    }
                    
                    if (maxWidth > 0 && maxHeight > 0)
                        return (maxWidth, maxHeight);
                }
                // BMP: 42 4D
                else if (bytesRead >= 26 && buffer[0] == 0x42 && buffer[1] == 0x4D)
                {
                    int width = buffer[18] | (buffer[19] << 8) | (buffer[20] << 16) | (buffer[21] << 24);
                    int height = buffer[22] | (buffer[23] << 8) | (buffer[24] << 16) | (buffer[25] << 24);
                    return (width, Math.Abs(height)); // Height can be negative for top-down BMPs
                }
                // GIF: 47 49 46 38
                else if (bytesRead >= 10 && buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x38)
                {
                    int width = buffer[6] | (buffer[7] << 8);
                    int height = buffer[8] | (buffer[9] << 8);
                    return (width, height);
                }
                // TGA: Check footer for "TRUEVISION-XFILE"
                else if (ext == ".tga" && bytesRead >= 18)
                {
                    // TGA has dimensions at bytes 12-15
                    int width = buffer[12] | (buffer[13] << 8);
                    int height = buffer[14] | (buffer[15] << 8);
                    if (width > 0 && height > 0 && width < 65536 && height < 65536)
                    {
                        return (width, height);
                    }
                }
                // WEBP: 52 49 46 46 ... 57 45 42 50
                else if (bytesRead >= 30 && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46)
                {
                    if (buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50)
                    {
                        // VP8 format
                        if (buffer[12] == 0x56 && buffer[13] == 0x50 && buffer[14] == 0x38)
                        {
                            if (buffer[15] == 0x20) // VP8
                            {
                                int width = ((buffer[26] | (buffer[27] << 8)) & 0x3FFF);
                                int height = ((buffer[28] | (buffer[29] << 8)) & 0x3FFF);
                                return (width, height);
                            }
                            else if (buffer[15] == 0x4C && bytesRead >= 25) // VP8L
                            {
                                int bits = buffer[21] | (buffer[22] << 8) | (buffer[23] << 16) | (buffer[24] << 24);
                                int width = (bits & 0x3FFF) + 1;
                                int height = ((bits >> 14) & 0x3FFF) + 1;
                                return (width, height);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Failed to read header
            }

            return (0, 0);
        }

        /// <summary>
        /// Extracts texture URL references from scene JSON content with type information
        /// </summary>
        private Dictionary<string, string> ExtractTextureReferencesWithTypes(string jsonContent)
        {
            var references = new Dictionary<string, string>(); // path -> type

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(jsonContent))
                {
                    // Recursively search for texture references
                    FindTextureReferencesRecursiveWithTypes(doc.RootElement, references);
                }
            }
            catch
            {
                // If JSON parsing fails, try regex fallback
                references = ExtractTextureReferencesWithRegexAndTypes(jsonContent);
            }

            return references;
        }

        /// <summary>
        /// Recursively finds texture URL properties in JSON with type information
        /// </summary>
        private void FindTextureReferencesRecursiveWithTypes(JsonElement element, Dictionary<string, string> references)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    // Check if this is a texture URL property
                    if (IsTextureUrlProperty(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        string value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value) && value.Contains("SELF:"))
                        {
                            if (!references.ContainsKey(value))
                            {
                                references[value] = GetTextureTypeName(property.Name);
                            }
                        }
                    }
                    else
                    {
                        // Recurse into nested objects/arrays
                        FindTextureReferencesRecursiveWithTypes(property.Value, references);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    FindTextureReferencesRecursiveWithTypes(item, references);
                }
            }
        }

        /// <summary>
        /// Gets image dimensions from archive package using streaming reads
        /// </summary>
        private Dictionary<string, (int width, int height)> GetArchiveImageDimensions(string archivePackagePath)
        {
            var dimensions = new Dictionary<string, (int width, int height)>(StringComparer.OrdinalIgnoreCase);
            
            try
            {
                using (var zipFile = ZipArchive.Open(archivePackagePath))
                {
                    var allEntries = SharpCompressHelper.GetAllEntries(zipFile);
                    foreach (var entry in allEntries)
                    {
                        string ext = Path.GetExtension(entry.Key).ToLowerInvariant();
                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                        {
                            try
                            {
                                // Use streaming header read instead of loading entire image
                                // Benefit: 40-60% memory reduction for large image files
                                byte[] headerData = SharpCompressHelper.ReadEntryHeader(zipFile, entry, 65536);
                                
                                // Validate header data before processing
                                if (headerData != null && headerData.Length > 0)
                                {
                                    var (width, height) = ReadImageDimensionsFromBuffer(headerData, headerData.Length, entry.Key);
                                    if (width > 0 && height > 0)
                                    {
                                        dimensions[entry.Key] = (width, height);
                                    }
                                }
                            }
                            catch
                            {
                                // Skip textures we can't read
                            }
                        }
                    }
                }
            }
            catch { }
            
            return dimensions;
        }

        /// <summary>
        /// Gets texture file sizes from archive package
        /// </summary>
        private Dictionary<string, long> GetArchiveFileSizes(string archivePackagePath)
        {
            var fileSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            
            try
            {
                using (var zipFile = ZipArchive.Open(archivePackagePath))
                {
                    var allEntries = SharpCompressHelper.GetAllEntries(zipFile);
                    foreach (var entry in allEntries)
                    {
                        string ext = Path.GetExtension(entry.Key).ToLowerInvariant();
                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                        {
                            fileSizes[entry.Key] = entry.Size;
                        }
                    }
                }
            }
            catch { }
            
            return fileSizes;
        }

        /// <summary>
        /// Gets a friendly name for texture type from property name
        /// </summary>
        private string GetTextureTypeName(string propertyName)
        {
            // Convert camelCase property names to friendly names
            // e.g., "faceDiffuseUrl" -> "Face Diffuse"
            string result = System.Text.RegularExpressions.Regex.Replace(propertyName.Replace("Url", ""), "([a-z])([A-Z])", "$1 $2");
            
            // Capitalize first letter
            if (!string.IsNullOrEmpty(result))
            {
                result = char.ToUpper(result[0]) + result.Substring(1);
            }
            
            return result;
        }

        /// <summary>
        /// Checks if a property name is a texture URL property
        /// </summary>
        private bool IsTextureUrlProperty(string propertyName)
        {
            var textureProperties = new[]
            {
                "faceDiffuseUrl", "torsoDiffuseUrl", "limbsDiffuseUrl", "genitalsDiffuseUrl",
                "faceSpecularUrl", "torsoSpecularUrl", "limbsSpecularUrl", "genitalsSpecularUrl",
                "faceGlossUrl", "torsoGlossUrl", "limbsGlossUrl", "genitalsGlossUrl",
                "faceNormalUrl", "torsoNormalUrl", "limbsNormalUrl", "genitalsNormalUrl",
                "faceDecalUrl", "torsoDecalUrl", "limbsDecalUrl", "genitalsDecalUrl"
            };

            return textureProperties.Contains(propertyName, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Fallback method to extract texture references using regex with types
        /// </summary>
        private Dictionary<string, string> ExtractTextureReferencesWithRegexAndTypes(string jsonContent)
        {
            var references = new Dictionary<string, string>();
            var regex = new System.Text.RegularExpressions.Regex(@"""(face|torso|limbs|genitals)(Diffuse|Specular|Gloss|Normal|Decal)Url""\s*:\s*""(SELF:[^""]+)""");
            
            var matches = regex.Matches(jsonContent);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count > 3)
                {
                    string path = match.Groups[3].Value;
                    string type = $"{match.Groups[1].Value} {match.Groups[2].Value}";
                    if (!references.ContainsKey(path))
                    {
                        references[path] = type;
                    }
                }
            }

            return references;
        }
    }
}

