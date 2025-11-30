using System;
using System.Collections.Generic;
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
        
        #region Dependency Properties

        public static readonly DependencyProperty PackageKeyProperty =
            DependencyProperty.Register("PackageKey", typeof(string), typeof(LazyLoadImage), new PropertyMetadata(null, OnIdentityChanged));

        public string PackageKey
        {
            get { return (string)GetValue(PackageKeyProperty); }
            set { SetValue(PackageKeyProperty, value); }
        }

        public static readonly DependencyProperty ImageIndexProperty =
            DependencyProperty.Register("ImageIndex", typeof(int), typeof(LazyLoadImage), new PropertyMetadata(0));

        public int ImageIndex
        {
            get { return (int)GetValue(ImageIndexProperty); }
            set { SetValue(ImageIndexProperty, value); }
        }

        public static readonly DependencyProperty ImageSourceProperty =
            DependencyProperty.Register("ImageSource", typeof(ImageSource), typeof(LazyLoadImage), new PropertyMetadata(null, OnImageSourceChanged));

        public ImageSource ImageSource
        {
            get { return (ImageSource)GetValue(ImageSourceProperty); }
            set { SetValue(ImageSourceProperty, value); }
        }

        private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (LazyLoadImage)d;
            var newImage = (ImageSource)e.NewValue;
            
            if (newImage != null)
            {
                control.UpdateImageSource(newImage);
            }
            else
            {
                control.UnloadImage();
            }
        }

        private static void OnIdentityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (LazyLoadImage)d;
            control.ResetState();
        }

        private void ResetState()
        {
            // Reset loading flag
            _isLoadingInProgress = false;
            
            // Ensure the grid structure is correct (only one instance of each button)
            EnsureSingleButtonInstances();

            // Clear button content and state to prevent stacking when control is reused
            if (_extractButton != null)
            {
                _extractButton.Content = null;
                _extractButton.Visibility = Visibility.Collapsed; // Start hidden, will be shown by SetExtractionState
                _extractButton.Background = new SolidColorBrush(Color.FromArgb(140, 51, 51, 51));
                _isCurrentlyExtracted = false;
            }
            if (_removeButton != null)
            {
                _removeButton.Content = "✕";
                _removeButton.Visibility = Visibility.Collapsed;
                _removeButton.Background = new SolidColorBrush(Color.FromArgb(160, 180, 40, 40));
            }
            
            // Clear existing image source if any
            // This will trigger OnImageSourceChanged -> UnloadImage
            if (ImageSource != null)
            {
                ImageSource = null;
            }
            else
            {
                // If ImageSource was already null, ensure UI is cleared
                UnloadImage();
            }
            
            _isLoaded = false;
            
            // Don't call SetExtractionState here - let the binding update it
            // SetExtractionState will be called by OnIsExtractedChanged when the binding updates
        }

        public static readonly DependencyProperty ImageWidthProperty =
            DependencyProperty.Register("ImageWidth", typeof(int), typeof(LazyLoadImage), new PropertyMetadata(0));

        public int ImageWidth
        {
            get { return (int)GetValue(ImageWidthProperty); }
            set { SetValue(ImageWidthProperty, value); }
        }

        public static readonly DependencyProperty ImageHeightProperty =
            DependencyProperty.Register("ImageHeight", typeof(int), typeof(LazyLoadImage), new PropertyMetadata(0));

        public int ImageHeight
        {
            get { return (int)GetValue(ImageHeightProperty); }
            set { SetValue(ImageHeightProperty, value); }
        }

        public static readonly DependencyProperty LoadImageCallbackProperty =
            DependencyProperty.Register("LoadImageCallback", typeof(Func<Task<BitmapImage>>), typeof(LazyLoadImage), new PropertyMetadata(null, OnIdentityChanged));

        public Func<Task<BitmapImage>> LoadImageCallback
        {
            get { return (Func<Task<BitmapImage>>)GetValue(LoadImageCallbackProperty); }
            set { SetValue(LoadImageCallbackProperty, value); }
        }

        public static readonly DependencyProperty VarFilePathProperty =
            DependencyProperty.Register("VarFilePath", typeof(string), typeof(LazyLoadImage), new PropertyMetadata(null, OnIdentityChanged));

        public string VarFilePath
        {
            get { return (string)GetValue(VarFilePathProperty); }
            set { SetValue(VarFilePathProperty, value); }
        }

        public static readonly DependencyProperty InternalImagePathProperty =
            DependencyProperty.Register("InternalImagePath", typeof(string), typeof(LazyLoadImage), new PropertyMetadata(null, OnIdentityChanged));

        public string InternalImagePath
        {
            get { return (string)GetValue(InternalImagePathProperty); }
            set { SetValue(InternalImagePathProperty, value); }
        }

        public static readonly DependencyProperty GameFolderProperty =
            DependencyProperty.Register("GameFolder", typeof(string), typeof(LazyLoadImage), new PropertyMetadata(null));

        public string GameFolder
        {
            get { return (string)GetValue(GameFolderProperty); }
            set { SetValue(GameFolderProperty, value); }
        }

        public static readonly DependencyProperty IsExtractedProperty =
            DependencyProperty.Register("IsExtracted", typeof(bool), typeof(LazyLoadImage), new PropertyMetadata(false, OnIsExtractedChanged));

        public bool IsExtracted
        {
            get { return (bool)GetValue(IsExtractedProperty); }
            set { SetValue(IsExtractedProperty, value); }
        }

        private static void OnIsExtractedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (LazyLoadImage)d;
            var newValue = (bool)e.NewValue;
            control.SetExtractionState(newValue);
        }

        #endregion
        
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
            _overlayGrid.Children.Clear();
            _overlayGrid.Children.Add(_imageControl);
            
            // Apply clipping to respect the CornerRadius
            this.ClipToBounds = true;
            
            // Create extract button at bottom-right with binding support
            _extractButton = new Button
            {
                Name = "ExtractButton",
                Padding = new Thickness(12, 6, 12, 6),
                Height = 30,
                Background = new SolidColorBrush(Color.FromArgb(140, 51, 51, 51)),
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 12, 12),
                Visibility = Visibility.Collapsed, // Start hidden, will be shown by SetExtractionState
                ToolTip = "Extract files from archive",
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Cursor = System.Windows.Input.Cursors.Hand,
                IsHitTestVisible = true
            };
            
            // Apply button template
            _extractButton.Template = CreateButtonTemplate();
            _extractButton.Click += ExtractButton_Click;
            
            // Note: SetExtractionState will be called by OnIsExtractedChanged when IsExtracted property changes
            
            _overlayGrid.Children.Add(_extractButton);

            // Create remove button at bottom-left
            _removeButton = new Button
            {
                Name = "RemoveButton",
                Padding = new Thickness(12, 6, 12, 6),
                Height = 30,
                Background = new SolidColorBrush(Color.FromArgb(160, 180, 40, 40)),
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(12, 0, 0, 12),
                Visibility = Visibility.Collapsed,
                ToolTip = "Remove extracted files",
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = "✕",
                IsHitTestVisible = true
            };

            _removeButton.Template = CreateButtonTemplate();
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
            this.Cursor = System.Windows.Input.Cursors.Arrow;
        }
        
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            
            // Apply clipping geometry based on CornerRadius
            if (sizeInfo.NewSize.Width > 0 && sizeInfo.NewSize.Height > 0)
            {
                var cornerRadius = this.CornerRadius;
                var radiusX = cornerRadius.TopLeft;
                var radiusY = cornerRadius.TopLeft;
                
                var clipGeometry = new RectangleGeometry(
                    new Rect(0, 0, sizeInfo.NewSize.Width, sizeInfo.NewSize.Height),
                    radiusX,
                    radiusY
                );
                
                this.Clip = clipGeometry;
            }
        }
        
        protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            
            // Detect double-click by checking click count
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                OpenImageInDefaultViewer();
            }
        }
        
        private void UpdateImageSource(ImageSource image)
        {
             _imageControl.Source = image;
             _imageControl.Visibility = Visibility.Visible;
             _isLoaded = true;
             ImageLoaded?.Invoke(this, EventArgs.Empty);
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
        /// Cancels any pending loading operation and prevents future loading
        /// </summary>
        public void CancelLoading()
        {
            _isLoadingInProgress = false;
            // We can't easily cancel the callback if it's already running, 
            // but we can prevent the result from being applied
            // Note: We don't set LoadImageCallback to null here because it's a dependency property
            // and we might want to reload later.
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
                ImageSource image = null;
                
                // Use provided ImageSource if available, otherwise use callback
                if (ImageSource != null)
                {
                    image = ImageSource;
                }
                else if (LoadImageCallback != null)
                {
                    // Capture callback to local variable to handle race conditions with CancelLoading
                    var callback = LoadImageCallback;
                    if (callback != null)
                    {
                        try
                        {
                            image = await callback();
                        }
                        catch (Exception ex)
                        {
                        }
                    }
                }
                
                // Check if cancelled (callback set to null) or no image found
                if (image == null)
                {
                    return;
                }
                
                // Update property if it was loaded via callback so it can be accessed later
                if (ImageSource == null)
                {
                    ImageSource = image;
                }
                else
                {
                    // If ImageSource was already set (or set by us above), ensure UI is updated
                    // This covers the case where ImageSource was set but _isLoaded was false
                    await Dispatcher.InvokeAsync(() =>
                    {
                        UpdateImageSource(image);
                    });
                }
            }
            catch (Exception ex)
            {
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
                    
                    // Ensure clean grid state
                    EnsureSingleButtonInstances();

                    // Clear button content and state to prevent stacking
                    if (_extractButton != null)
                    {
                        _extractButton.Content = null;
                        _extractButton.Visibility = Visibility.Collapsed;
                        _extractButton.Background = new SolidColorBrush(Color.FromArgb(140, 51, 51, 51));
                        _isCurrentlyExtracted = false;
                    }
                    if (_removeButton != null)
                    {
                        _removeButton.Content = "✕";
                        _removeButton.Visibility = Visibility.Collapsed;
                        _removeButton.Background = new SolidColorBrush(Color.FromArgb(160, 180, 40, 40));
                    }
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
        
        // IsExtracted is now a Dependency Property

        /// <summary>
        /// Ensures that the overlay grid contains exactly one instance of each control
        /// explicitly clearing and rebuilding the children collection.
        /// </summary>
        private void EnsureSingleButtonInstances()
        {
            try
            {
                // Completely clear the grid to remove any stale or duplicate references
                _overlayGrid.Children.Clear();
                
                // Re-add the core controls in the correct order (Image first, then buttons on top)
                if (_imageControl != null)
                {
                    _overlayGrid.Children.Add(_imageControl);
                }
                
                if (_extractButton != null)
                {
                    _overlayGrid.Children.Add(_extractButton);
                }
                
                if (_removeButton != null)
                {
                    _overlayGrid.Children.Add(_removeButton);
                }
            }
            catch (Exception)
            {
                // Ignore errors
            }
        }

        /// <summary>
        /// Updates the extract button state (shows button or checkmark)
        /// </summary>
        public void SetExtractionState(bool isExtracted)
        {
            try
            {
                // Don't set IsExtracted here - it's already set by the binding or caller
                // Setting it again would cause a loop
                Dispatcher.Invoke(() =>
                {
                    // Ensure clean state by rebuilding the grid children
                    EnsureSingleButtonInstances();
                    
                    // Get category name - handle null path
                    var category = string.IsNullOrEmpty(InternalImagePath) 
                        ? "Content" 
                        : VarContentExtractor.GetCategoryFromPath(InternalImagePath);
                    
                    // Clear previous content to prevent button duplication
                    _extractButton.Content = null;
                    
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
                        _extractButton.Background = new SolidColorBrush(Color.FromArgb(140, 60, 120, 70)); 
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
                        else if (string.Equals(category, "Pose", StringComparison.OrdinalIgnoreCase)) iconText = "🧍";
                        
                        // Show extract button with icon and label
                        iconBlock.Text = iconText; 
                        _extractButton.Content = stackPanel;
                        // Transparent gray for available state
                        _extractButton.Background = new SolidColorBrush(Color.FromArgb(100, 80, 80, 80)); 
                        _extractButton.ToolTip = $"Extract {category} files";
                        _extractButton.IsEnabled = true;

                        // Hide remove button
                        _removeButton.Visibility = Visibility.Collapsed;
                    }
                    
                    // Update padding for a more stylish look
                    _extractButton.Padding = new Thickness(12, 6, 12, 6);
                    _extractButton.Visibility = Visibility.Visible;
                });
            }
            catch (Exception ex)
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
            
            // Always delegate to parent via event
            // The parent handler will check IsExtracted state and decide whether to extract or open folder
            ExtractionRequested?.Invoke(this, new ExtractionRequestedEventArgs
            {
                VarFilePath = this.VarFilePath,
                InternalImagePath = this.InternalImagePath,
                IsRemoval = false
            });
        }
        
        /// <summary>
        /// Opens the image in the default image viewer
        /// Extracts the image to a temporary location if it's in an archive
        /// </summary>
        private void OpenImageInDefaultViewer()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(InternalImagePath) || string.IsNullOrWhiteSpace(VarFilePath))
                {
                    MessageBox.Show("Cannot determine image location.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Get the file extension from the internal path
                var fileExtension = Path.GetExtension(InternalImagePath);
                if (string.IsNullOrWhiteSpace(fileExtension))
                {
                    fileExtension = ".png"; // Default to PNG if no extension found
                }

                // Create a temporary file path with a unique name to avoid conflicts
                var tempFileName = Path.GetFileNameWithoutExtension(InternalImagePath);
                var uniqueSuffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                tempFileName = $"{tempFileName}_{uniqueSuffix}{fileExtension}";
                
                var tempDirectory = Path.Combine(Path.GetTempPath(), "VPM_Images");
                var tempFilePath = Path.Combine(tempDirectory, tempFileName);

                // Ensure temp directory exists
                if (!Directory.Exists(tempDirectory))
                {
                    try
                    {
                        Directory.CreateDirectory(tempDirectory);
                    }
                    catch (Exception dirEx)
                    {
                        MessageBox.Show($"Cannot create temporary directory: {dirEx.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                // If image is already loaded, save it to temp location
                if (_imageControl?.Source is BitmapImage bitmapImage)
                {
                    // Save the bitmap to the temp file
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapImage));
                    
                    try
                    {
                        using (var fileStream = new FileStream(tempFilePath, FileMode.Create))
                        {
                            encoder.Save(fileStream);
                        }
                    }
                    catch (Exception saveEx)
                    {
                        MessageBox.Show($"Failed to save image to temporary location: {saveEx.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else if (LoadImageCallback != null)
                {
                    // Load the image first, then save it
                    var loadTask = LoadImageAsync();
                    loadTask.Wait(5000); // Wait up to 5 seconds for image to load
                    
                    if (_imageControl?.Source is BitmapImage loadedBitmap)
                    {
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(loadedBitmap));
                        
                        try
                        {
                            using (var fileStream = new FileStream(tempFilePath, FileMode.Create))
                            {
                                encoder.Save(fileStream);
                            }
                        }
                        catch (Exception saveEx)
                        {
                            MessageBox.Show($"Failed to save image to temporary location: {saveEx.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                    else
                    {
                        MessageBox.Show("Failed to load image.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else
                {
                    MessageBox.Show("Cannot load image.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Verify the file was created before trying to open it
                if (!File.Exists(tempFilePath))
                {
                    MessageBox.Show($"Failed to create temporary image file at: {tempFilePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Open the image with the default viewer
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tempFilePath,
                        UseShellExecute = true
                    });
                }
                catch (Exception processEx)
                {
                    MessageBox.Show($"Failed to open image viewer: {processEx.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
