using System;
using System.Diagnostics;
using System.IO;
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
        private Grid _overlayGrid;
        private Button _extractButton;
        private Button _removeButton;
        private bool _isCurrentlyExtracted = false;
        
        // Image data
        public string PackageKey { get; set; }
        public int ImageIndex { get; set; }
        public BitmapImage ImageSource { get; set; }
        
        // Image dimensions for aspect ratio calculations
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        
        // Callback for loading the actual image
        public Func<Task<BitmapImage>> LoadImageCallback { get; set; }
        
        // Extraction data
        public string VarFilePath { get; set; }
        public string InternalImagePath { get; set; }
        public string GameFolder { get; set; }
        
        // Events
        public event EventHandler ImageLoaded;
        public event EventHandler ImageUnloaded;
        public event EventHandler<ExtractionRequestedEventArgs> ExtractionRequested;
        
        public LazyLoadImage()
        {
            // Create the image control upfront (empty, no source yet)
            // This reserves the correct space and avoids layout recalculations
            _imageControl = new Image
            {
                Stretch = Stretch.UniformToFill,
                SnapsToDevicePixels = true,
                Source = null // Will be set when image loads
            };
            
            // Create overlay grid for buttons
            _overlayGrid = new Grid();
            _overlayGrid.Children.Add(_imageControl);
            
            // Create extract button at bottom-right
            _extractButton = new Button
            {
                Padding = new Thickness(8, 5, 8, 5),
                Height = 28,
                Background = new SolidColorBrush(Color.FromArgb(200, 51, 51, 51)), // #FF333333 with slight transparency
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 6, 6),
                Visibility = Visibility.Collapsed,
                ToolTip = "Extract files from archive",
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Cursor = System.Windows.Input.Cursors.Hand,
                IsHitTestVisible = true // Keep button responsive to mouse even when disabled
            };
            
            // Style the button with rounded corners and hover effects
            var buttonStyle = new System.Windows.Style(typeof(Button));
            buttonStyle.Setters.Add(new System.Windows.Setter(Button.TemplateProperty, CreateButtonTemplate()));
            _extractButton.Style = buttonStyle;
            
            // Use named method for click handler so we can swap it later
            _extractButton.Click += ExtractButton_Click;
            
            _overlayGrid.Children.Add(_extractButton);

            // Create remove button at bottom-left
            _removeButton = new Button
            {
                Padding = new Thickness(8, 5, 8, 5),
                Height = 28,
                Background = new SolidColorBrush(Color.FromArgb(200, 180, 40, 40)), // Semi-transparent red with better opacity
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(6, 0, 0, 6),
                Visibility = Visibility.Collapsed,
                ToolTip = "Remove extracted files",
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = "X",
                IsHitTestVisible = true // Keep button responsive to mouse even when disabled
            };

            // Style the button with rounded corners
            _removeButton.Style = buttonStyle;

            _removeButton.Click += (s, e) =>
            {
                e.Handled = true;
                ExtractionRequested?.Invoke(this, new ExtractionRequestedEventArgs
                {
                    VarFilePath = this.VarFilePath,
                    InternalImagePath = this.InternalImagePath,
                    IsRemoval = true
                });
            };

            _overlayGrid.Children.Add(_removeButton);
            
            // Set child to overlay grid
            this.Child = _overlayGrid;
            
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
                    // Update property if it was loaded via callback so it can be accessed later
                    if (ImageSource == null)
                    {
                        ImageSource = image;
                    }

                    // Set the Source and show immediately
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _imageControl.Source = image;
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
        
        public bool IsExtracted { get; private set; }

        /// <summary>
        /// Updates the extract button state (shows button or checkmark)
        /// </summary>
        public void SetExtractionState(bool isExtracted)
        {
            try
            {
                IsExtracted = isExtracted;
                Dispatcher.Invoke(() =>
                {
                    // Get category name
                    var category = VarContentExtractor.GetCategoryFromPath(InternalImagePath);
                    
                    // Create content with icon and text
                    var stackPanel = new StackPanel 
                    { 
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    
                    var iconBlock = new TextBlock 
                    { 
                        Margin = new Thickness(0, 0, 6, 0),
                        FontWeight = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                        FontSize = 12
                    };
                    
                    var textBlock = new TextBlock 
                    { 
                        Text = category,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 12
                    };
                    
                    stackPanel.Children.Add(iconBlock);
                    stackPanel.Children.Add(textBlock);

                    if (isExtracted)
                    {
                        _isCurrentlyExtracted = true;
                        
                        // Show checkmark with label
                        iconBlock.Text = "✓";
                        _extractButton.Content = stackPanel;
                        // Neutral green for extracted state (not too bright)
                        _extractButton.Background = new SolidColorBrush(Color.FromArgb(160, 60, 120, 70)); 
                        _extractButton.ToolTip = $"Click to open extracted {category} files in Explorer";
                        _extractButton.IsEnabled = true; // Enable button to allow opening in Explorer

                        // Show remove button
                        _removeButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _isCurrentlyExtracted = false;
                        
                        // Determine icon based on category
                        string iconText = "📥"; // Default
                        if (string.Equals(category, "Hair", StringComparison.OrdinalIgnoreCase)) iconText = "✂️";
                        else if (string.Equals(category, "Clothing", StringComparison.OrdinalIgnoreCase)) iconText = "👕";
                        else if (string.Equals(category, "Skin", StringComparison.OrdinalIgnoreCase)) iconText = "🎨";
                        else if (string.Equals(category, "Appearance", StringComparison.OrdinalIgnoreCase)) iconText = "👤";
                        else if (string.Equals(category, "Scene", StringComparison.OrdinalIgnoreCase)) iconText = "🎬";
                        
                        // Show extract button with icon and label
                        iconBlock.Text = iconText; 
                        _extractButton.Content = stackPanel;
                        // Transparent gray for available state
                        _extractButton.Background = new SolidColorBrush(Color.FromArgb(120, 80, 80, 80)); 
                        _extractButton.ToolTip = $"Extract {category} files";
                        _extractButton.IsEnabled = true;

                        // Hide remove button
                        _removeButton.Visibility = Visibility.Collapsed;
                    }
                    
                    // Update padding for a more stylish look
                    _extractButton.Padding = new Thickness(8, 5, 8, 5);
                    _extractButton.Visibility = Visibility.Visible;
                });
            }
            catch (Exception)
            {
                // Ignore errors during state update
            }
        }
        
        /// <summary>
        /// Creates a custom button template with rounded corners and hover effects
        /// </summary>
        private ControlTemplate CreateButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            
            // Main border with rounded corners
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ButtonBorder";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); // Match theme corner radius
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            
            // Content presenter
            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            
            border.AppendChild(contentPresenter);
            template.VisualTree = border;
            
            // Hover trigger (only when enabled)
            var hoverTrigger = new MultiTrigger();
            hoverTrigger.Conditions.Add(new Condition(Button.IsMouseOverProperty, true));
            hoverTrigger.Conditions.Add(new Condition(Button.IsEnabledProperty, true));
            
            // Use theme hover color #FF454545
            hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, 
                new SolidColorBrush(Color.FromArgb(200, 69, 69, 69))));
            // Add blue border on hover - target the border element
            hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, 
                new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)), "ButtonBorder")); // #FF0078D7
            hoverTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "ButtonBorder"));
            template.Triggers.Add(hoverTrigger);
            
            // Hover trigger for disabled buttons (show blue border even when disabled)
            var disabledHoverMulti = new MultiTrigger();
            disabledHoverMulti.Conditions.Add(new Condition(Button.IsMouseOverProperty, true));
            disabledHoverMulti.Conditions.Add(new Condition(Button.IsEnabledProperty, false));
            disabledHoverMulti.Setters.Add(new Setter(Border.BorderBrushProperty, 
                new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)), "ButtonBorder")); // #FF0078D7
            template.Triggers.Add(disabledHoverMulti);
            
            // Disabled state trigger - preserve appearance when disabled
            var disabledTrigger = new Trigger
            {
                Property = Button.IsEnabledProperty,
                Value = false
            };
            disabledTrigger.Setters.Add(new Setter(Button.OpacityProperty, 1.0)); // Keep full opacity
            template.Triggers.Add(disabledTrigger);
            
            // Pressed trigger
            var pressedTrigger = new Trigger
            {
                Property = Button.IsPressedProperty,
                Value = true
            };
            // Use theme pressed color #FF555555
            pressedTrigger.Setters.Add(new Setter(Button.BackgroundProperty, 
                new SolidColorBrush(Color.FromArgb(200, 85, 85, 85))));
            template.Triggers.Add(pressedTrigger);
            
            return template;
        }
        
        /// <summary>
        /// Opens the extracted files location in Windows Explorer
        /// </summary>
        private void OpenExtractedFilesInExplorer()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(InternalImagePath) || string.IsNullOrWhiteSpace(GameFolder))
                {
                    MessageBox.Show("Cannot determine extracted files location.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Get the directory path from the internal image path
                var directoryPath = Path.GetDirectoryName(InternalImagePath);
                if (string.IsNullOrWhiteSpace(directoryPath))
                {
                    MessageBox.Show("Cannot determine extracted files location.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Construct the full path in the game folder
                var fullPath = Path.Combine(GameFolder, directoryPath.Replace('/', Path.DirectorySeparatorChar));

                // Check if the directory exists
                if (!Directory.Exists(fullPath))
                {
                    MessageBox.Show($"Extracted files directory not found:\n{fullPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Open the directory in Explorer
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = fullPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open Explorer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        /// <summary>
        /// Handler for extract button click - behavior depends on extraction state
        /// </summary>
        private void ExtractButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            
            if (_isCurrentlyExtracted)
            {
                // Open extracted files in Explorer
                OpenExtractedFilesInExplorer();
            }
            else
            {
                // Request extraction
                ExtractionRequested?.Invoke(this, new ExtractionRequestedEventArgs
                {
                    VarFilePath = this.VarFilePath,
                    InternalImagePath = this.InternalImagePath,
                    IsRemoval = false
                });
            }
        }
    }
    
    /// <summary>
    /// Event args for extraction requests
    /// </summary>
    public class ExtractionRequestedEventArgs : EventArgs
    {
        public string VarFilePath { get; set; }
        public string InternalImagePath { get; set; }
        public bool IsRemoval { get; set; }
    }
}
