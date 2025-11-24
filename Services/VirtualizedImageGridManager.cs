using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using VPM.Windows;

namespace VPM.Services
{
    /// <summary>
    /// Manages virtualized image loading for image grids to optimize initial load time and performance.
    /// Uses a one-way loading approach: images are loaded on-demand as they become visible,
    /// but never unloaded (more efficient than unload/reload cycles during scrolling).
    /// </summary>
    public class VirtualizedImageGridManager
    {
        private readonly List<LazyLoadImage> _lazyImages = new List<LazyLoadImage>();
        private readonly ScrollViewer _scrollViewer;
        private DispatcherTimer _scrollDebounceTimer;
        private DispatcherTimer _continuousProcessingTimer;
        private bool _isProcessing = false;
        private int _consecutiveNoVisibleUnloadedCycles = 0;
        
        // Configuration
        public double LoadBufferSize { get; set; } = 300; // Pixels before/after viewport to start loading
        public int MaxConcurrentLoads { get; set; } = 6; // Max images to load simultaneously (currently unused)
        
        // Statistics
        public int LoadedImageCount => _lazyImages.Count(img => img.IsImageLoaded);
        public int TotalImageCount => _lazyImages.Count;
        
        public VirtualizedImageGridManager(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer ?? throw new ArgumentNullException(nameof(scrollViewer));
            
            // Set up scroll event handler with debouncing
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }
        
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
        }
        
        /// <summary>
        /// Processes all registered images and loads visible ones (one-way - never unloads)
        /// Uses binary search to efficiently find the visible range in the sorted list of images
        /// </summary>
        public async Task<int> ProcessImagesAsync()
        {
            if (_isProcessing || _lazyImages.Count == 0) return 0;
            
            _isProcessing = true;
            
            try
            {
                var viewportTop = _scrollViewer.VerticalOffset;
                var viewportHeight = _scrollViewer.ViewportHeight;
                var viewportBottom = viewportTop + viewportHeight;
                
                var loadTop = viewportTop - LoadBufferSize;
                var loadBottom = viewportBottom + LoadBufferSize;
                
                // Optimization: Use binary search to find the start index
                // This assumes images are roughly sorted by vertical position (which they are in a grid)
                int startIndex = FindFirstVisibleIndex(loadTop);
                if (startIndex < 0) startIndex = 0;
                
                int loadedCount = 0;
                
                // Iterate from start index until we go past the load bottom
                for (int i = startIndex; i < _lazyImages.Count; i++)
                {
                    var image = _lazyImages[i];
                    
                    // Skip if already loaded
                    if (image.IsImageLoaded) continue;
                    
                    // Check position
                    double top = GetVerticalPosition(image);
                    
                    // If not in visual tree yet, skip but continue (might be further down?)
                    // Actually if it returns -1, it's not connected.
                    if (top < 0) continue;
                    
                    // If we've gone past the bottom, stop processing
                    // Add a small buffer (e.g. one row height ~300px) to handle grid irregularities
                    if (top > loadBottom + 300) 
                    {
                        break;
                    }
                    
                    // Check if in range (top is within load zone, or bottom is within load zone)
                    double height = image.ActualHeight > 0 ? image.ActualHeight : (image.Height > 0 ? image.Height : 200);
                    double bottom = top + height;
                    
                    if (bottom >= loadTop && top <= loadBottom)
                    {
                        await image.LoadImageAsync();
                        loadedCount++;
                    }
                }
                
                return loadedCount;
            }
            finally
            {
                _isProcessing = false;
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
        /// </summary>
        private double GetVerticalPosition(LazyLoadImage image)
        {
            try
            {
                if (_scrollViewer == null) return -1;
                
                var transform = image.TransformToAncestor(_scrollViewer);
                var position = transform.Transform(new System.Windows.Point(0, 0));
                return position.Y;
            }
            catch (Exception)
            {
                return -1; 
            }
        }
        
        /// <summary>
        /// Loads initially visible images when the grid is first displayed
        /// </summary>
        public async Task LoadInitialVisibleImagesAsync()
        {            await ProcessImagesAsync();
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
                    var loadTop = viewportTop - LoadBufferSize;
                    var loadBottom = viewportBottom + LoadBufferSize;
                    
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
            
            Clear();
        }
    }
}

