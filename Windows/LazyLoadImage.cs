using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using VPM.Services;

namespace VPM.Windows
{
    /// <summary>
    /// A Border control that lazily loads images only when they become visible in the viewport.
    /// Optimized for 1:1 aspect ratio images with pre-allocated space to avoid layout thrashing.
    /// </summary>
    public class LazyLoadImage : Border
    {
        private bool _isLoaded = false;
        private bool _isLoadingInProgress = false;
        private Image _imageControl;
        
        // Image data
        public string PackageKey { get; set; }
        public int ImageIndex { get; set; }
        public BitmapImage ImageSource { get; set; }
        
        // Callback for loading the actual image
        public Func<Task<BitmapImage>> LoadImageCallback { get; set; }
        
        // Events
        public event EventHandler ImageLoaded;
        public event EventHandler ImageUnloaded;
        
        public LazyLoadImage()
        {
            // Create the image control upfront (empty, no source yet)
            // This reserves the correct space and avoids layout recalculations
            _imageControl = new Image
            {
                Stretch = Stretch.UniformToFill,
                SnapsToDevicePixels = true,
                Source = null, // Will be set when image loads
                Opacity = 0 // Start invisible for fade-in animation
            };
            
            // Set child directly - no Grid overlay needed
            this.Child = _imageControl;
            
            // Light background visible until image loads
            this.Background = new SolidColorBrush(Color.FromArgb(15, 100, 149, 237));
            this.Cursor = System.Windows.Input.Cursors.Hand;
        }
        
        /// <summary>
        /// Checks if the image tile is visible in the viewport and loads it if needed
        /// </summary>
        public async Task<bool> CheckAndLoadIfVisibleAsync(ScrollViewer scrollViewer, double bufferSize = 200)
        {
            if (_isLoaded || _isLoadingInProgress) return _isLoaded;
            
            try
            {
                // Get the position of this element relative to the ScrollViewer
                var transform = this.TransformToAncestor(scrollViewer);
                var position = transform.Transform(new Point(0, 0));
                
                var elementTop = position.Y;
                // Use RenderSize if ActualHeight is 0 (might happen during layout)
                var elementHeight = this.ActualHeight > 0 ? this.ActualHeight : this.RenderSize.Height;
                if (elementHeight <= 0)
                {
                    // Still no height, assume a reasonable default for grid tiles
                    elementHeight = 200; // Typical grid tile size
                }
                var elementBottom = elementTop + elementHeight;
                
                var viewportTop = scrollViewer.VerticalOffset;
                var viewportBottom = viewportTop + scrollViewer.ViewportHeight;
                
                // Add buffer zone for smoother scrolling
                var loadTop = viewportTop - bufferSize;
                var loadBottom = viewportBottom + bufferSize;
                
                // Check if element is in the load zone
                if (elementBottom >= loadTop && elementTop <= loadBottom)
                {
                    await LoadImageAsync();
                    return true;
                }
            }
            catch (Exception)
            {
                // Element might not be in visual tree yet
            }
            
            return false;
        }
        
        /// <summary>
        /// Loads the actual image using the provided callback
        /// </summary>
        public async Task LoadImageAsync()
        {
            if (_isLoaded || _isLoadingInProgress) return;
            
            _isLoadingInProgress = true;
            
            try
            {
                BitmapImage image = null;
                
                // Use provided ImageSource if available, otherwise use callback
                if (ImageSource != null)
                {
                    image = ImageSource;
                }
                else if (LoadImageCallback != null)
                {
                    image = await LoadImageCallback();
                }
                
                if (image != null)
                {
                    // Set the Source and show immediately without animation for performance
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _imageControl.Source = image;
                        _imageControl.Opacity = 1;
                        _imageControl.Visibility = Visibility.Visible;
                        _isLoaded = true;
                    });
                    
                    ImageLoaded?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception)
            {
                // Failed to load image
            }
            finally
            {
                _isLoadingInProgress = false;
            }
        }
        
        /// <summary>
        /// Unloads the image to free memory (used only when clearing entire grid)
        /// </summary>
        public void UnloadImage()
        {
            if (!_isLoaded) return;
            
            try
            {
                Dispatcher.Invoke(() =>
                {
                    _imageControl.Source = null;
                    _imageControl.Opacity = 0; // Reset for potential reload with fade-in
                    _isLoaded = false;
                });
                
                ImageUnloaded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // Ignore errors during unload
            }
        }
        
        /// <summary>
        /// Checks if this image should be unloaded based on viewport position
        /// </summary>
        public bool ShouldUnload(ScrollViewer scrollViewer, double unloadThreshold = 500)
        {
            if (!_isLoaded) return false;
            
            try
            {
                var transform = this.TransformToAncestor(scrollViewer);
                var position = transform.Transform(new Point(0, 0));
                
                var elementTop = position.Y;
                var elementBottom = elementTop + this.ActualHeight;
                
                var viewportTop = scrollViewer.VerticalOffset;
                var viewportBottom = viewportTop + scrollViewer.ViewportHeight;
                
                // Unload if element is far outside the viewport
                var unloadTop = viewportTop - unloadThreshold;
                var unloadBottom = viewportBottom + unloadThreshold;
                
                return elementBottom < unloadTop || elementTop > unloadBottom;
            }
            catch (Exception)
            {
                return false;
            }
        }
        
        /// <summary>
        /// Gets whether the image is currently loaded
        /// </summary>
        public bool IsImageLoaded => _isLoaded;
    }
}
