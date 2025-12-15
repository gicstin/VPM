using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using VPM.Windows;

namespace VPM.Services
{
    /// <summary>
    /// Manages virtualized image loading for image grids to optimize initial load time and performance.
    /// Uses a dynamic loading/unloading approach: images are loaded on-demand as they become visible,
    /// and unloaded when they scroll far out of view to conserve memory.
    /// </summary>
    public class VirtualizedImageGridManager
    {
        private readonly List<LazyLoadImage> _lazyImages = new List<LazyLoadImage>();
        private readonly ScrollViewer _scrollViewer;
        private DispatcherTimer _scrollDebounceTimer;
        private DispatcherTimer _continuousProcessingTimer;
        private bool _isProcessing = false;
        private int _consecutiveNoVisibleUnloadedCycles = 0;
        private double _lastProcessedOffset = -1;
        private SemaphoreSlim _loadSemaphore;
        private bool _isInitialLoad = true;
        private Stopwatch _loadingStopwatch;
        
        // Configuration
        public double InitialLoadBuffer { get; set; } = 100; // Tight buffer for initial display
        public double ScrollLoadBuffer { get; set; } = 300; // Loose buffer for smooth scrolling
        public double UnloadBufferMultiplier { get; set; } = 4.0; // Unload images outside viewport * this multiplier
        public int MaxConcurrentLoads { get; set; } = 4; // Max images to load simultaneously
        private const double MinScrollDelta = 100; // Only process if scrolled this many pixels
        
        // Statistics
        public int LoadedImageCount => _lazyImages.Count(img => img.IsImageLoaded);
        public int TotalImageCount => _lazyImages.Count;
        
        public VirtualizedImageGridManager(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer ?? throw new ArgumentNullException(nameof(scrollViewer));
            _loadSemaphore = new SemaphoreSlim(MaxConcurrentLoads, MaxConcurrentLoads);
            _loadingStopwatch = new Stopwatch();
            
            // Set up scroll event handler with debouncing
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }
        
        /// <summary>
        /// Gets current loading metrics for performance monitoring
        /// </summary>
        public LoadingMetrics GetMetrics()
        {
            return new LoadingMetrics
            {
                TotalImagesLoaded = LoadedImageCount,
                TotalImages = TotalImageCount,
                InitialLoadTime = _loadingStopwatch.Elapsed,
                IsInitialLoadComplete = !_isInitialLoad,
                ProcessingCyclesCount = _processingCyclesCount
            };
        }
        
        private int _processingCyclesCount = 0;
        
        /// <summary>
        /// Registers a lazy load image for management
        /// </summary>
        public void RegisterImage(LazyLoadImage image)
        {
            if (image != null && !_lazyImages.Contains(image))
            {
                _lazyImages.Add(image);
            }
        }
        
        /// <summary>
        /// Batch registers multiple images and triggers initial load (more efficient than individual registration)
        /// </summary>
        public async Task BatchRegisterAsync(IEnumerable<LazyLoadImage> images)
        {
            if (images == null)
                return;
            
            var imageList = images.ToList();
            if (imageList.Count == 0)
                return;
            
            // Start timing the initial load
            if (_isInitialLoad)
            {
                _loadingStopwatch.Start();
            }
            
            // Add all images at once
            foreach (var image in imageList)
            {
                if (image != null && !_lazyImages.Contains(image))
                {
                    _lazyImages.Add(image);
                }
            }
            
            // Defer initial load to allow UI to render first
            // Use Dispatcher to schedule on UI thread after current batch completes
            if (_isInitialLoad)
            {
                _ = _scrollViewer.Dispatcher.InvokeAsync(async () =>
                {
                    // Give UI time to render the controls
                    await Task.Delay(50);
                    await LoadInitialVisibleImagesAsync();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Unregisters a lazy load image from management
        /// </summary>
        public void UnregisterImage(LazyLoadImage image)
        {
            if (image != null)
            {
                image.UnloadImage();
                _lazyImages.Remove(image);
            }
        }

        /// <summary>
        /// Unregisters multiple lazy load images from management
        /// </summary>
        public void UnregisterImages(IEnumerable<LazyLoadImage> images)
        {
            if (images == null)
                return;

            foreach (var image in images)
            {
                UnregisterImage(image);
            }
        }
        
        /// <summary>
        /// Clears all registered images
        /// </summary>
        public void Clear()
        {
            // Unload all images first
            foreach (var image in _lazyImages)
            {
                image.UnloadImage();
            }
            
            _lazyImages.Clear();
            
            // Reset initial load flag so next batch will trigger deferred load
            _isInitialLoad = true;
            _loadingStopwatch.Reset();
            _processingCyclesCount = 0;
            _lastProcessedOffset = -1;
        }
        
        /// <summary>
        /// Unloads images that are far outside the viewport to free memory
        /// </summary>
        private void UnloadInvisibleImages(double viewportHeight)
        {
            // Safety check - if viewport height is invalid, don't unload anything
            if (viewportHeight <= 0) return;

            // Define a larger buffer for unloading to prevent rapid load/unload cycles
            // Keep images loaded if they are within N screens of the viewport
            double unloadBuffer = viewportHeight * UnloadBufferMultiplier;
            
            // Coordinates are relative to the viewport (0 is top of visible area)
            double unloadTop = -unloadBuffer;
            double unloadBottom = viewportHeight + unloadBuffer;
            
            for (int i = 0; i < _lazyImages.Count; i++)
            {
                var image = _lazyImages[i];
                
                if (!image.IsImageLoaded) continue;
                
                // Check position (relative to viewport)
                double top = GetVerticalPosition(image);
                
                // If position is invalid (NaN), skip unloading to be safe
                // This prevents unloading images that are in the visual tree but have transform issues
                if (double.IsNaN(top)) continue;
                
                double height = image.ActualHeight > 0 ? image.ActualHeight : (image.Height > 0 ? image.Height : 200);
                double bottom = top + height;
                
                // If completely outside the unload buffer zone, unload it
                if (bottom < unloadTop || top > unloadBottom)
                {
                    image.UnloadImage();
                }
            }
        }

        /// <summary>
        /// Processes all registered images and loads visible ones (one-way - never unloads)
        /// Uses binary search to efficiently find the visible range in the sorted list of images
        /// Includes semaphore-based concurrency control to prevent memory spikes
        /// </summary>
        public async Task<int> ProcessImagesAsync()
        {
            if (_isProcessing || _lazyImages.Count == 0) return 0;
            
            _isProcessing = true;
            _processingCyclesCount++;
            
            try
            {
                var viewportTop = _scrollViewer.VerticalOffset;
                var viewportHeight = _scrollViewer.ViewportHeight;
                
                // MEMORY FIX: Unload images that are far outside the viewport
                if (!_isInitialLoad)
                {
                    UnloadInvisibleImages(viewportHeight);
                }
                
                // Use two-tier buffer: tight for initial, loose for scrolling
                var currentBuffer = _isInitialLoad ? InitialLoadBuffer : ScrollLoadBuffer;
                
                // Coordinates are relative to the viewport
                var loadTop = -currentBuffer;
                var loadBottom = viewportHeight + currentBuffer;
                
                // Optimization: Skip redundant processing if scroll delta is small
                if (!_isInitialLoad && Math.Abs(viewportTop - _lastProcessedOffset) < MinScrollDelta)
                {
                    return 0;
                }
                
                _lastProcessedOffset = viewportTop;
                
                int loadedCount = 0;
                var loadTasks = new List<Task>();
                
                // During initial load, load ALL images immediately (don't use binary search)
                // After initial load, use binary search to optimize
                if (_isInitialLoad)
                {
                    
                    // Load all images during initial phase
                    for (int i = 0; i < _lazyImages.Count; i++)
                    {
                        var image = _lazyImages[i];
                        
                        // Skip if already loaded
                        if (image.IsImageLoaded) continue;
                        
                        var loadTask = LoadImageWithSemaphoreAsync(image);
                        loadTasks.Add(loadTask);
                        loadedCount++;
                    }
                }
                else
                {
                    // After initial load, use optimized viewport-based loading
                    // Note: FindFirstVisibleIndex uses GetVerticalPosition which is relative.
                    // We need to pass the relative target top.
                    int startIndex = FindFirstVisibleIndex(loadTop);
                    if (startIndex < 0) startIndex = 0;
                    
                    // Iterate from start index until we go past the load bottom
                    for (int i = startIndex; i < _lazyImages.Count; i++)
                    {
                        var image = _lazyImages[i];
                        
                        // Skip if already loaded
                        if (image.IsImageLoaded) continue;
                        
                        // Check position
                        double top = GetVerticalPosition(image);
                        
                        // Only load images that are in the visual tree and in viewport
                        // Note: GetVerticalPosition returns -1 on error, but can return negative values for valid positions.
                        // We should check if it's reasonably valid?
                        // Actually, if it's not in visual tree, TransformToAncestor throws and we catch and return -1.
                        // If it IS in visual tree but scrolled off top, it returns negative.
                        // We can't easily distinguish -1 error from -1 position.
                        // But -1 position is very close to viewport top (0).
                        // So treating -1 as valid position is fine.
                        
                        // If we've gone past the bottom, stop processing
                        if (top > loadBottom + 300) 
                        {
                            break;
                        }
                        
                        // Check if in range
                        double height = image.ActualHeight > 0 ? image.ActualHeight : (image.Height > 0 ? image.Height : 200);
                        double bottom = top + height;
                        
                        if (bottom >= loadTop && top <= loadBottom)
                        {
                            // Load with semaphore to control concurrency
                            var loadTask = LoadImageWithSemaphoreAsync(image);
                            loadTasks.Add(loadTask);
                            loadedCount++;
                        }
                    }
                }
                
                // Wait for all pending loads to complete (with timeout to prevent hanging)
                if (loadTasks.Count > 0)
                {
                    await Task.WhenAny(Task.WhenAll(loadTasks), Task.Delay(5000));
                }
                
                // Mark initial load as complete after first processing
                if (_isInitialLoad && loadedCount > 0)
                {
                    _isInitialLoad = false;
                }
                
                return loadedCount;
            }
            finally
            {
                _isProcessing = false;
            }
        }
        
        /// <summary>
        /// Loads an image with semaphore-based concurrency control
        /// </summary>
        private async Task LoadImageWithSemaphoreAsync(LazyLoadImage image)
        {
            await _loadSemaphore.WaitAsync();
            try
            {
                await image.LoadImageAsync();
            }
            catch (Exception)
            {
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }
        
        /// <summary>
        /// Binary search to find the first image that might be visible
        /// </summary>
        private int FindFirstVisibleIndex(double targetTop)
        {
            int left = 0;
            int right = _lazyImages.Count - 1;
            int result = 0;
            
            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                var image = _lazyImages[mid];
                double top = GetVerticalPosition(image);
                
                if (top < 0)
                {
                    // Item not in visual tree, treat as "before" or "after"?
                    // This is tricky. If items are virtualized out, they might return -1.
                    // But we assume no UI virtualization.
                    // If -1, we can't rely on it. Linearly search?
                    // For now, assume 0 to be safe if we hit this edge case.
                    // Or just check neighbors?
                    // Let's just fallback to linear scan if we hit -1, or skip.
                    // But usually -1 means not loaded.
                    
                    // Fallback: just return 0 to be safe
                    return 0;
                }
                
                double height = image.ActualHeight > 0 ? image.ActualHeight : (image.Height > 0 ? image.Height : 200);
                
                if (top + height < targetTop)
                {
                    // Too high up, look down
                    left = mid + 1;
                }
                else
                {
                    // This one is visible or below, but there might be one before it
                    result = mid;
                    right = mid - 1;
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Gets the vertical position of an image relative to the ScrollViewer
        /// Returns double.NaN on error
        /// </summary>
        private double GetVerticalPosition(LazyLoadImage image)
        {
            try
            {
                if (_scrollViewer == null) return double.NaN;
                
                var transform = image.TransformToAncestor(_scrollViewer);
                var position = transform.Transform(new System.Windows.Point(0, 0));
                return position.Y;
            }
            catch (Exception)
            {
                return double.NaN; 
            }
        }
        
        /// <summary>
        /// Loads initially visible images when the grid is first displayed
        /// </summary>
        public async Task LoadInitialVisibleImagesAsync()
        {
            await ProcessImagesAsync();
            StartContinuousProcessing();
        }
        
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrollDebounceTimer == null)
            {
                _scrollDebounceTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                
                _scrollDebounceTimer.Tick += async (s, args) =>
                {
                    _scrollDebounceTimer.Stop();
                    await ProcessImagesAsync();
                    StartContinuousProcessing();
                };
            }
            
            _scrollDebounceTimer.Stop();
            _scrollDebounceTimer.Start();
        }
        
        /// <summary>
        /// Starts a timer that continuously processes images until all visible ones are loaded
        /// </summary>
        private void StartContinuousProcessing()
        {
            if (_lazyImages.All(img => img.IsImageLoaded))
            {
                _continuousProcessingTimer?.Stop();
                return;
            }
            
            if (_continuousProcessingTimer != null && _continuousProcessingTimer.IsEnabled)
            {
                return;
            }
            
            if (_continuousProcessingTimer == null)
            {
                _continuousProcessingTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                
                _continuousProcessingTimer.Tick += async (s, args) =>
                {
                    if (_lazyImages.All(img => img.IsImageLoaded))
                    {
                        _continuousProcessingTimer.Stop();
                        return;
                    }
                    
                    var viewportTop = _scrollViewer.VerticalOffset;
                    var viewportBottom = viewportTop + _scrollViewer.ViewportHeight;
                    var loadTop = viewportTop - ScrollLoadBuffer;
                    var loadBottom = viewportBottom + ScrollLoadBuffer;
                    
                    var unloadedInLoadZone = _lazyImages
                        .Where(img => !img.IsImageLoaded && img.IsLoaded)
                        .Select(img => new { Image = img, Position = GetVerticalPosition(img) })
                        .Count(x => x.Position >= 0 && x.Position <= loadBottom && (x.Position + 200) >= loadTop);
                    
                    if (unloadedInLoadZone == 0)
                    {
                        _consecutiveNoVisibleUnloadedCycles++;
                        if (_consecutiveNoVisibleUnloadedCycles >= 3)
                        {
                            _continuousProcessingTimer.Stop();
                            _consecutiveNoVisibleUnloadedCycles = 0;
                            return;
                        }
                    }
                    else
                    {
                        _consecutiveNoVisibleUnloadedCycles = 0;
                    }
                    
                    await ProcessImagesAsync();
                };
            }
            
            _continuousProcessingTimer.Start();
        }
        
        /// <summary>
        /// Forces immediate processing of images without debouncing
        /// </summary>
        public async Task RefreshAsync()
        {
            _scrollDebounceTimer?.Stop();
            await ProcessImagesAsync();
        }
        
        /// <summary>
        /// Disposes of the manager and cleans up resources
        /// </summary>
        public void Dispose()
        {
            _scrollDebounceTimer?.Stop();
            _scrollDebounceTimer = null;
            
            _continuousProcessingTimer?.Stop();
            _continuousProcessingTimer = null;
            
            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
            }
            
            _loadingStopwatch?.Stop();
            _loadSemaphore?.Dispose();
            
            Clear();
        }
    }
    
    /// <summary>
    /// Metrics for monitoring image loading performance
    /// </summary>
    public class LoadingMetrics
    {
        public int TotalImagesLoaded { get; set; }
        public int TotalImages { get; set; }
        public TimeSpan InitialLoadTime { get; set; }
        public bool IsInitialLoadComplete { get; set; }
        public int ProcessingCyclesCount { get; set; }
    }
}

