using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SharpCompress.Archives;
using VPM.Models;
using Microsoft.IO;

namespace VPM.Services
{
    /// <summary>
    /// Async-based image loader pool for high-performance image loading
    /// Replaces the dedicated thread pool with Task-based async/await pattern
    /// Provides similar performance with better resource management and simpler lifecycle
    /// </summary>
    public class ImageLoaderAsyncPool : IDisposable
    {
        private readonly int _maxConcurrentLoads;
        private readonly SemaphoreSlim _concurrencySemaphore;
        private volatile bool _running;
        
        // Separate queues for priority management
        private readonly ConcurrentQueue<QueuedImage> _thumbnailQueue = new();
        private readonly ConcurrentStack<QueuedImage> _imageQueue = new();
        
        // Texture reference counting for lifecycle management
        private readonly Dictionary<BitmapImage, int> _textureUseCount = new();
        private readonly Dictionary<BitmapImage, bool> _textureTrackedCache = new();
        private readonly object _textureTrackingLock = new();
        
        // Progress tracking
        private int _progress = 0;
        private int _progressMax = 0;
        private int _numRealQueuedImages = 0;
        private readonly object _progressLock = new();
        
        // Validation metrics for performance tracking
        private int _validationRejections = 0;
        private int _totalValidationChecks = 0;
        private readonly object _validationMetricsLock = new();
        
        // File locking management - FIXED: Use atomic operations for thread safety
        private readonly ConcurrentDictionary<string, int> _activeFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _cancelledPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _cancelledPathExpiry = TimeSpan.FromMinutes(1); // Auto-expire cancelled paths
        
        // Lock-free archive reader for eliminating file lock issues
        private readonly LockFreeArchiveReader _lockFreeReader;
        
        // Callbacks and events
        public event Action<int, int> ProgressChanged; // (current, total)
        public event Action<QueuedImage> ImageProcessed;
        
        private readonly Dispatcher _dispatcher;
        private readonly ImageDiskCache _diskCache;
        private bool _disposed = false;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _processingTask;
        
        public ImageLoaderAsyncPool(ImageDiskCache diskCache = null, int maxConcurrentLoads = 0)
        {
            _diskCache = diskCache;
            if (maxConcurrentLoads <= 0)
            {
                maxConcurrentLoads = Math.Max(1, Environment.ProcessorCount / 2);
            }
            
            _maxConcurrentLoads = maxConcurrentLoads;
            _concurrencySemaphore = new SemaphoreSlim(maxConcurrentLoads, maxConcurrentLoads);
            
            // Initialize lock-free archive reader
            _lockFreeReader = new LockFreeArchiveReader();
            
            // Capture dispatcher from UI thread - must be called from UI thread
            if (Dispatcher.FromThread(Thread.CurrentThread) != null)
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
            }
            else
            {
                // Fallback: try to get main dispatcher
                _dispatcher = System.Windows.Application.Current?.Dispatcher;
            }
            
            if (_dispatcher == null)
            {
                throw new InvalidOperationException("ImageLoaderAsyncPool must be created on a thread with a Dispatcher (UI thread)");
            }
            
            StartProcessing();
        }
        
        /// <summary>
        /// Cancels pending operations for specific file paths and waits for active ones to finish
        /// </summary>
        public async Task ReleaseFileLocksAsync(IEnumerable<string> filePaths)
        {
            var paths = filePaths.ToList();
            
            // Console.WriteLine($"[ImageLoaderAsyncPool.ReleaseFileLocksAsync] Starting release for {paths.Count} paths");
            
            // 1. Mark paths as cancelled to prevent new operations
            // Also mark the package NAME (without path) to catch both AddonPackages and AllPackages versions
            var now = DateTime.UtcNow;
            foreach (var path in paths)
            {
                // Console.WriteLine($"[ImageLoaderAsyncPool.ReleaseFileLocksAsync] Marking path as cancelled: {path}");
                _cancelledPaths[path] = now; // Use indexer for atomic update
                
                // Also extract package name and mark it (e.g., "Package.Name.1" from full path)
                var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(fileName))
                {
                    _cancelledPaths[fileName] = now;
                    // Console.WriteLine($"[ImageLoaderAsyncPool.ReleaseFileLocksAsync] Also marked package name: {fileName}");
                }
                
                // CRITICAL: Invalidate lock-free archive cache for this path
                // This ensures no cached data references the file
                _lockFreeReader.InvalidateArchive(path);
                
                // Also cancel any pending items in the queue for this path
                CancelPendingForPackage(path);
            }
            
            // Console.WriteLine($"[ImageLoaderAsyncPool.ReleaseFileLocksAsync] Paths marked as cancelled");
            
            // 2. Wait for active operations to finish
            var maxWait = TimeSpan.FromSeconds(10); // Increased from 5s to 10s for robustness
            var start = DateTime.UtcNow;
            int waitIterations = 0;
            
            while (DateTime.UtcNow - start < maxWait)
            {
                waitIterations++;
                bool anyActive = false;
                foreach (var path in paths)
                {
                    if (_activeFiles.TryGetValue(path, out int count) && count > 0)
                    {
                        if (waitIterations % 10 == 0) // Log every 500ms
                        {
                            // Console.WriteLine($"[ImageLoaderAsyncPool.ReleaseFileLocksAsync] Path still active: {path} (count: {count})");
                        }
                        anyActive = true;
                    }
                }
                
                if (!anyActive)
                {
                    // Console.WriteLine($"[ImageLoaderAsyncPool.ReleaseFileLocksAsync] All paths released after {waitIterations} iterations");
                    break;
                }
                    
                await Task.Delay(50);
            }
            
            if (waitIterations >= maxWait.TotalMilliseconds / 50)
            {
                // Console.WriteLine($"[ImageLoaderAsyncPool.ReleaseFileLocksAsync] WARNING: Timeout waiting for file locks to release");
                foreach (var path in paths)
                {
                    if (_activeFiles.TryGetValue(path, out int count) && count > 0)
                    {
                        // Console.WriteLine($"[ImageLoaderAsyncPool.ReleaseFileLocksAsync] STILL ACTIVE: {path} (count: {count})");
                    }
                }
            }
            
            // 3. Use optimized GC collection for file handle release
            // Full GC.Collect() causes UI stuttering - use non-blocking Gen 0 instead
            GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
            
            // Console.WriteLine($"[ImageLoaderAsyncPool.ReleaseFileLocksAsync] === COMPLETE ===");
        }

        /// <summary>
        /// Cancels all pending operations and waits for active ones to finish
        /// </summary>
        public async Task CancelAllOperationsAsync()
        {
            // 1. Cancel all queued items
            foreach (var item in _thumbnailQueue) item.Cancelled = true;
            foreach (var item in _imageQueue) item.Cancelled = true;
            
            // 2. Clear queues
            ClearQueues();
            
            // 3. Wait for active operations to finish
            var maxWait = TimeSpan.FromSeconds(5);
            var start = DateTime.UtcNow;
            
            while (DateTime.UtcNow - start < maxWait)
            {
                if (_activeFiles.IsEmpty)
                    break;
                    
                await Task.Delay(50);
            }
        }

        public void ResetCancellation(string path)
        {
            _cancelledPaths.TryRemove(path, out _);
            
            // Also reset by package name
            var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(fileName))
            {
                _cancelledPaths.TryRemove(fileName, out _);
            }
        }
        
        /// <summary>
        /// Cleans up expired cancelled paths to prevent memory leaks.
        /// Called periodically during processing.
        /// </summary>
        private void CleanupExpiredCancelledPaths()
        {
            var now = DateTime.UtcNow;
            var keysToRemove = _cancelledPaths
                .Where(kvp => now - kvp.Value > _cancelledPathExpiry)
                .Select(kvp => kvp.Key)
                .ToList();
                
            foreach (var key in keysToRemove)
            {
                _cancelledPaths.TryRemove(key, out _);
            }
        }

        public void RegisterActiveFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _activeFiles.AddOrUpdate(path, 1, (k, v) => v + 1);
        }

        public void UnregisterActiveFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _activeFiles.AddOrUpdate(path, 0, (k, v) => Math.Max(0, v - 1));
        }

        public bool IsFileCancelled(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            
            // Check if path is cancelled and not expired
            if (_cancelledPaths.TryGetValue(path, out var cancelTime))
            {
                if (DateTime.UtcNow - cancelTime <= _cancelledPathExpiry)
                    return true;
                    
                // Expired - remove it
                _cancelledPaths.TryRemove(path, out _);
            }
            
            // Also check by package name
            var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(fileName) && _cancelledPaths.TryGetValue(fileName, out cancelTime))
            {
                if (DateTime.UtcNow - cancelTime <= _cancelledPathExpiry)
                    return true;
                    
                _cancelledPaths.TryRemove(fileName, out _);
            }
            
            return false;
        }
        
        /// <summary>
        /// Gets the lock-free archive reader for direct access.
        /// </summary>
        public LockFreeArchiveReader LockFreeReader => _lockFreeReader;

        /// <summary>
        /// Starts the async processing loop
        /// </summary>
        private void StartProcessing()
        {
            if (_running) return;
            
            _running = true;
            _cancellationTokenSource = new CancellationTokenSource();
            // Fire and forget - processing runs in background
            _processingTask = ProcessQueueAsync(_cancellationTokenSource.Token);
        }
        
        /// <summary>
        /// Main async processing loop
        /// </summary>
        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (_running && !cancellationToken.IsCancellationRequested)
                {
                    // Try to get next image with priority (thumbnails first)
                    if (!TryDequeueImage(out var queuedImage))
                    {
                        // No images available, wait a bit before checking again
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                        
                        // Periodically cleanup expired cancelled paths
                        CleanupExpiredCancelledPaths();
                        continue;
                    }
                    
                    // Check if path is cancelled (uses expiry-aware check)
                    if (!string.IsNullOrEmpty(queuedImage.VarPath) && IsFileCancelled(queuedImage.VarPath))
                    {
                        queuedImage.HadError = true;
                        queuedImage.ErrorText = "Operation cancelled due to file lock request";
                        // Skip processing but invoke callback to cleanup
                        FinishImage(queuedImage); // This is empty but good for consistency
                        
                        // Invoke callback on UI thread to ensure UI knows it's done (failed)
                        if (!_disposed && _dispatcher != null)
                        {
                            _ = _dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    queuedImage.Callback?.Invoke(queuedImage);
                                }
                                catch { }
                            }));
                        }
                        continue;
                    }
                    
                    try
                    {
                        if (queuedImage.Cancelled)
                        {
                            queuedImage.HadError = true;
                            queuedImage.ErrorText = "Operation cancelled";
                        }
                        else
                        {
                            // Process image with concurrency control
                            await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                            try
                            {
                                // Track active file usage
                                if (!string.IsNullOrEmpty(queuedImage.VarPath))
                                {
                                    _activeFiles.AddOrUpdate(queuedImage.VarPath, 1, (k, v) => v + 1);
                                }

                                try
                                {
                                    // Process on thread pool (CPU-bound work)
                                    await Task.Run(() => ProcessImage(queuedImage), cancellationToken).ConfigureAwait(false);
                                    FinishImage(queuedImage);
                                }
                                finally
                                {
                                    // Release file usage tracking
                                    if (!string.IsNullOrEmpty(queuedImage.VarPath))
                                    {
                                        _activeFiles.AddOrUpdate(queuedImage.VarPath, 0, (k, v) => Math.Max(0, v - 1));
                                    }
                                }
                            }
                            finally
                            {
                                _concurrencySemaphore.Release();
                            }
                        }
                        
                        // Invoke callback on UI thread
                        if (!_disposed && _dispatcher != null)
                        {
                            _ = _dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    ImageProcessed?.Invoke(queuedImage);
                                    queuedImage.Callback?.Invoke(queuedImage);
                                }
                                catch (Exception)
                                {
                                }
                                finally
                                {
                                    queuedImage.RawData = null;
                                }
                            }), DispatcherPriority.Normal);
                        }
                        else
                        {
                            queuedImage.RawData = null;
                        }
                        
                        if (!queuedImage.IsThumbnail)
                        {
                            UpdateProgress();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected during shutdown
                        break;
                    }
                    catch (Exception ex)
                    {
                        queuedImage.HadError = true;
                        queuedImage.ErrorText = ex.Message;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }
        
        /// <summary>
        /// Tries to dequeue an image with priority (thumbnails first)
        /// </summary>
        private bool TryDequeueImage(out QueuedImage image)
        {
            // Thumbnails have priority
            if (_thumbnailQueue.TryDequeue(out image))
            {
                return true;
            }
            
            // Then regular images
            if (_imageQueue.TryPop(out image))
            {
                return true;
            }
            
            image = null;
            return false;
        }
        
        /// <summary>
        /// Processes an image (loads from VAR, applies transformations)
        /// Uses lock-free archive reader to eliminate file locking issues.
        /// Integrates with FileAccessController to prevent conflicts during optimization.
        /// </summary>
        private void ProcessImage(QueuedImage qi)
        {
            if (qi.Processed) return;
            
            try
            {
                // Load image from VAR archive
                if (!string.IsNullOrEmpty(qi.VarPath) && !string.IsNullOrEmpty(qi.InternalPath))
                {
                    // CRITICAL: Check FileAccessController FIRST - if file is locked for writing, fail fast
                    // This is the primary mechanism to prevent file lock conflicts during optimization
                    if (FileAccessController.Instance.IsFileLockedForWriting(qi.VarPath))
                    {
                        qi.HadError = true;
                        qi.ErrorText = "File is being optimized";
                        return;
                    }
                    
                    // Get file info for cache validation
                    try
                    {
                        var fileInfo = new FileInfo(qi.VarPath);
                        qi.FileSize = fileInfo.Length;
                        qi.LastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
                        
                        // Try disk cache first
                        if (_diskCache != null)
                        {
                            var cachedBitmap = _diskCache.TryGetCached(qi.VarPath, qi.InternalPath, qi.FileSize, qi.LastWriteTicks);
                            if (cachedBitmap != null)
                            {
                                qi.Texture = cachedBitmap;
                                qi.Finished = true;
                                qi.Processed = true;
                                return;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore file info errors, proceed to load from VAR
                    }

                    // Check if cancelled before reading (legacy cancellation system)
                    if (IsFileCancelled(qi.VarPath))
                    {
                        qi.HadError = true;
                        qi.ErrorText = "Operation cancelled";
                        return;
                    }
                    
                    // Double-check FileAccessController after potential delays
                    if (FileAccessController.Instance.IsFileLockedForWriting(qi.VarPath))
                    {
                        qi.HadError = true;
                        qi.ErrorText = "File is being optimized";
                        return;
                    }

                    // LOCK-FREE APPROACH: Use virtual archive cache instead of holding file handles
                    // This eliminates file locking issues by reading data into memory and closing the file immediately
                    
                    // Validation: Check early using header-only reads (50-70% I/O reduction for invalid images)
                    lock (_validationMetricsLock)
                    {
                        _totalValidationChecks++;
                    }
                    
                    if (!_lockFreeReader.IsValidImageEntry(qi.VarPath, qi.InternalPath))
                    {
                        lock (_validationMetricsLock)
                        {
                            _validationRejections++;
                        }
                        qi.HadError = true;
                        qi.ErrorText = "Invalid image format";
                        return;
                    }
                    
                    // Read entry data using lock-free reader (file is opened, read, and closed immediately)
                    // The lock-free reader also checks FileAccessController internally
                    var imageData = _lockFreeReader.ReadEntryData(qi.VarPath, qi.InternalPath);
                    if (imageData == null || imageData.Length == 0)
                    {
                        // Check if this was due to file being locked
                        if (FileAccessController.Instance.IsFileLockedForWriting(qi.VarPath))
                        {
                            qi.HadError = true;
                            qi.ErrorText = "File is being optimized";
                        }
                        else
                        {
                            qi.HadError = true;
                            qi.ErrorText = "Entry not found in archive or empty";
                        }
                        return;
                    }
                    
                    // Use pooled MemoryStream to avoid allocation and fragmentation
                    var memoryStream = MemoryStreamPool.Manager.GetStream();
                    memoryStream.Write(imageData, 0, imageData.Length);
                    memoryStream.Position = 0;
                    
                    // Validate image stream
                    if (!IsValidImageStream(memoryStream))
                    {
                        memoryStream.Dispose();
                        qi.HadError = true;
                        qi.ErrorText = "Invalid image stream";
                        return;
                    }
                    
                    memoryStream.Position = 0;
                    
                    // Create BitmapImage on background thread
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = memoryStream;
                        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
                        
                        if (qi.DecodeWidth > 0)
                        {
                            bitmap.DecodePixelWidth = qi.DecodeWidth;
                        }
                        if (qi.DecodeHeight > 0)
                        {
                            bitmap.DecodePixelHeight = qi.DecodeHeight;
                        }
                        
                        bitmap.EndInit();
                        bitmap.Freeze();
                        
                        qi.Texture = bitmap;
                        qi.Finished = true;
                        
                        // Save to disk cache if available
                        if (_diskCache != null && qi.FileSize > 0 && qi.LastWriteTicks > 0)
                        {
                            _diskCache.TrySaveToCache(qi.VarPath, qi.InternalPath, qi.FileSize, qi.LastWriteTicks, bitmap);
                        }
                    }
                    catch (Exception ex)
                    {
                        qi.HadError = true;
                        qi.ErrorText = "Bitmap creation failed: " + ex.Message;
                    }
                    finally
                    {
                        // Dispose stream after loading
                        memoryStream.Dispose();
                        qi.RawDataStream = null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when operation is cancelled
                qi.HadError = true;
                qi.ErrorText = "Operation cancelled";
            }
            catch (Exception ex)
            {
                qi.HadError = true;
                qi.ErrorText = ex.Message;
                // Ensure stream is disposed if we created it but failed
                if (qi.RawDataStream != null)
                {
                    qi.RawDataStream.Dispose();
                    qi.RawDataStream = null;
                }
            }
            finally
            {
                qi.Processed = true;
            }
        }
        
        /// <summary>
        /// Finishes image processing on UI thread (invokes callbacks)
        /// </summary>
        private void FinishImage(QueuedImage qi)
        {
            // No-op: Image creation now happens in ProcessImage on background thread
            // This method is kept for architectural consistency but does nothing
        }
        
        /// <summary>
        /// Validates image stream header (supports JPEG and PNG)
        /// </summary>
        private bool IsValidImageStream(Stream stream)
        {
            if (stream.Length < 4) return false;
            
            try
            {
                stream.Position = 0;
                var header = new byte[4];
                var bytesRead = stream.Read(header, 0, 4);
                
                if (bytesRead < 4) return false;
                
                // Check PNG signature (89 50 4E 47)
                if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                    return true;
                
                // Check JPEG magic bytes (FF D8 FF)
                if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                    return true;
                
                return false;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Queues an image for loading (regular priority)
        /// </summary>
        public void QueueImage(QueuedImage qi)
        {
            if (qi == null) return;
            
            // Don't queue if path is cancelled (prevents file lock conflicts during unload)
            if (!string.IsNullOrEmpty(qi.VarPath) && _cancelledPaths.ContainsKey(qi.VarPath))
            {
                return;
            }
            
            // CRITICAL: Don't queue if file is locked for writing (optimization in progress)
            // This is the primary mechanism to prevent new image loads during optimization
            if (!string.IsNullOrEmpty(qi.VarPath) && FileAccessController.Instance.IsFileLockedForWriting(qi.VarPath))
            {
                return;
            }
            
            qi.IsThumbnail = false;
            _imageQueue.Push(qi);
            
            lock (_progressLock)
            {
                _numRealQueuedImages++;
                _progressMax++;
            }
        }
        
        /// <summary>
        /// Queues a thumbnail for loading (high priority)
        /// </summary>
        public void QueueThumbnail(QueuedImage qi)
        {
            if (qi == null) return;
            
            // Don't queue if path is cancelled (prevents file lock conflicts during unload)
            if (!string.IsNullOrEmpty(qi.VarPath) && _cancelledPaths.ContainsKey(qi.VarPath))
            {
                return;
            }
            
            // CRITICAL: Don't queue if file is locked for writing (optimization in progress)
            if (!string.IsNullOrEmpty(qi.VarPath) && FileAccessController.Instance.IsFileLockedForWriting(qi.VarPath))
            {
                return;
            }
            
            qi.IsThumbnail = true;
            _thumbnailQueue.Enqueue(qi);
        }
        
        /// <summary>
        /// Updates progress tracking
        /// </summary>
        private void UpdateProgress()
        {
            int current, total;
            
            lock (_progressLock)
            {
                _progress++;
                _numRealQueuedImages--;
                
                if (_numRealQueuedImages == 0)
                {
                    _progress = 0;
                    _progressMax = 0;
                }
                
                current = _progress;
                total = _progressMax;
            }
            
            // Invoke progress callback on UI thread (only if not disposed)
            if (!_disposed && _dispatcher != null)
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ProgressChanged?.Invoke(current, total);
                    }
                    catch (Exception)
                    {
                    }
                }), DispatcherPriority.Normal);
            }
        }
        
        /// <summary>
        /// Registers texture usage (increments reference count)
        /// </summary>
        public bool RegisterTextureUse(BitmapImage texture)
        {
            if (texture == null) return false;
            
            lock (_textureTrackingLock)
            {
                if (_textureTrackedCache.ContainsKey(texture))
                {
                    if (_textureUseCount.TryGetValue(texture, out var count))
                    {
                        _textureUseCount[texture] = count + 1;
                    }
                    else
                    {
                        _textureUseCount[texture] = 1;
                    }
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Deregisters texture usage (decrements reference count, disposes if zero)
        /// </summary>
        public bool DeregisterTextureUse(BitmapImage texture)
        {
            if (texture == null) return false;
            
            lock (_textureTrackingLock)
            {
                if (_textureUseCount.TryGetValue(texture, out var count))
                {
                    count--;
                    
                    if (count > 0)
                    {
                        _textureUseCount[texture] = count;
                    }
                    else
                    {
                        // Reference count reached zero - cleanup
                        _textureUseCount.Remove(texture);
                        _textureTrackedCache.Remove(texture);
                        
                        // Note: BitmapImage doesn't need explicit disposal in WPF
                        // GC will handle it when no references remain
                    }
                    
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Tracks a texture for reference counting
        /// </summary>
        public void TrackTexture(BitmapImage texture)
        {
            if (texture == null) return;
            
            lock (_textureTrackingLock)
            {
                if (!_textureTrackedCache.ContainsKey(texture))
                {
                    _textureTrackedCache[texture] = true;
                    _textureUseCount[texture] = 1;
                }
            }
        }
        
        /// <summary>
        /// Gets current queue sizes
        /// </summary>
        public (int thumbnails, int images, int total) GetQueueSizes()
        {
            var thumbnails = _thumbnailQueue.Count;
            var images = _imageQueue.Count;
            return (thumbnails, images, thumbnails + images);
        }
        
        /// <summary>
        /// Gets progress information
        /// </summary>
        public (int current, int total) GetProgress()
        {
            lock (_progressLock)
            {
                return (_progress, _progressMax);
            }
        }
        
        /// <summary>
        /// Clears all queued images
        /// </summary>
        public void ClearQueues()
        {
            while (_thumbnailQueue.TryDequeue(out _)) { }
            while (_imageQueue.TryPop(out _)) { }
            
            lock (_progressLock)
            {
                _progress = 0;
                _progressMax = 0;
                _numRealQueuedImages = 0;
            }
        }

        /// <summary>
        /// Gets validation metrics
        /// Returns (totalChecks, rejections, rejectionRate%)
        /// </summary>
        public (int totalChecks, int rejections, double rejectionRate) GetValidationMetrics()
        {
            lock (_validationMetricsLock)
            {
                double rate = _totalValidationChecks > 0 
                    ? (_validationRejections * 100.0) / _totalValidationChecks 
                    : 0;
                return (_totalValidationChecks, _validationRejections, rate);
            }
        }

        /// <summary>
        /// Resets validation metrics
        /// </summary>
        public void ResetValidationMetrics()
        {
            lock (_validationMetricsLock)
            {
                _validationRejections = 0;
                _totalValidationChecks = 0;
            }
        }
        
        /// <summary>
        /// Cancels all pending image loads for a specific VAR file
        /// </summary>
        public void CancelPendingForPackage(string varPath)
        {
            if (string.IsNullOrEmpty(varPath)) return;
            
            // Normalize path for comparison
            var normalizedPath = varPath.Replace('/', '\\');

            foreach (var item in _thumbnailQueue)
            {
                if (item.VarPath != null && item.VarPath.Replace('/', '\\').Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    item.Cancelled = true;
                }
            }

            foreach (var item in _imageQueue)
            {
                if (item.VarPath != null && item.VarPath.Replace('/', '\\').Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    item.Cancelled = true;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _running = false;
            _cancellationTokenSource?.Cancel();
            
            try
            {
                _processingTask?.Wait(5000);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            
            _cancellationTokenSource?.Dispose();
            _concurrencySemaphore?.Dispose();
            ClearQueues();
            
            // Dispose lock-free reader
            _lockFreeReader?.Dispose();
            
            lock (_textureTrackingLock)
            {
                _textureUseCount.Clear();
                _textureTrackedCache.Clear();
            }
        }
    }
}
