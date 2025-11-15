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
        /// Images are sorted by vertical position to ensure top-to-bottom loading order
        /// Returns the number of images loaded in this cycle
        /// </summary>
        public async Task<int> ProcessImagesAsync()
        {
            if (_isProcessing || _lazyImages.Count == 0) return 0;
            
            _isProcessing = true;
            
            try
            {
                var viewportTop = _scrollViewer.VerticalOffset;
                var viewportBottom = viewportTop + _scrollViewer.ViewportHeight;
                var viewportCenter = (viewportTop + viewportBottom) / 2;
                var loadTop = viewportTop - LoadBufferSize;
                var loadBottom = viewportBottom + LoadBufferSize;
                
                // Get unloaded images that are in the visual tree
                var unloadedImages = _lazyImages
                    .Where(img => !img.IsImageLoaded && img.IsLoaded)
                    .Select(img => new { Image = img, Position = GetVerticalPosition(img) })
                    .Where(x => x.Position >= 0)  // Only images in visual tree
                    .ToList();                
                // Filter to ONLY images in the load zone, then sort by distance from viewport center
                var imagesToLoad = unloadedImages
                    .Where(x => x.Position <= loadBottom && (x.Position + 200) >= loadTop)  // In load zone
                    .OrderBy(x => Math.Abs(x.Position - viewportCenter))  // Closest to center first
                    .Select(x => x.Image)
                    .ToList();
                
                // Load images in priority order (closest to viewport center first)
                int loadedCount = 0;
                foreach (var image in imagesToLoad)
                {
                    try
                    {
                        // Check if image is visible and load if needed
                        var wasLoaded = await image.CheckAndLoadIfVisibleAsync(_scrollViewer, LoadBufferSize);
                        if (wasLoaded)
                        {
                            loadedCount++;
                        }
                    }
                    catch (Exception)
                    {
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
            // Process images on any scroll change (including initial layout when ViewportHeight changes)
            // This ensures visible images load even if the user hasn't scrolled yet            
            // Debounce scroll events for better performance
            _scrollDebounceTimer?.Stop();
            _scrollDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50), // Fast response
                IsEnabled = false
            };
            
            _scrollDebounceTimer.Tick += async (s, args) =>
            {
                _scrollDebounceTimer.Stop();                await ProcessImagesAsync();
                
                // Start continuous processing if there are still unloaded images
                StartContinuousProcessing();
            };
            
            _scrollDebounceTimer.Start();
        }
        
        /// <summary>
        /// Starts a timer that continuously processes images until all visible ones are loaded
        /// </summary>
        private void StartContinuousProcessing()
        {
            // Only start if there are unloaded images
            if (_lazyImages.All(img => img.IsImageLoaded))
            {
                _continuousProcessingTimer?.Stop();
                return;
            }
            
            if (_continuousProcessingTimer != null && _continuousProcessingTimer.IsEnabled)
            {
                return; // Already running
            }
            
            _continuousProcessingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100), // Check every 100ms to load ~20 images
                IsEnabled = true
            };
            
            _continuousProcessingTimer.Tick += async (s, args) =>
            {
                // Stop if all images are loaded
                if (_lazyImages.All(img => img.IsImageLoaded))
                {
                    _continuousProcessingTimer.Stop();                    return;
                }
                
                // Calculate load zone
                var viewportTop = _scrollViewer.VerticalOffset;
                var viewportBottom = viewportTop + _scrollViewer.ViewportHeight;
                var loadTop = viewportTop - LoadBufferSize;
                var loadBottom = viewportBottom + LoadBufferSize;
                
                // Count unloaded images that are IN THE LOAD ZONE
                var unloadedInLoadZone = _lazyImages
                    .Where(img => !img.IsImageLoaded && img.IsLoaded)
                    .Select(img => new { Image = img, Position = GetVerticalPosition(img) })
                    .Count(x => x.Position >= 0 && x.Position <= loadBottom && (x.Position + 200) >= loadTop);
                
                if (unloadedInLoadZone == 0)
                {
                    _consecutiveNoVisibleUnloadedCycles++;
                    if (_consecutiveNoVisibleUnloadedCycles >= 3)
                    {
                        _continuousProcessingTimer.Stop();                        _consecutiveNoVisibleUnloadedCycles = 0;
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

