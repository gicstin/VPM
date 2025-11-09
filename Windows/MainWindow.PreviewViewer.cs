using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Text.Json;
using System.Windows.Media.Animation;
using VPM.Models;

namespace VPM
{
    /// <summary>
    /// Preview viewer functionality for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        #region Preview Panel Management

        private bool _isPreviewVisible = false;
        private string _currentPreviewFile = null;
        private PackageItem _currentPreviewPackage = null;
        private List<int> _currentSearchMatches = new List<int>();
        private int _currentSearchMatchIndex = -1;
        private RichTextBox _currentSearchRichTextBox = null;

        /// <summary>
        /// Shows the preview panel with animation
        /// </summary>
        private void ShowPreviewPanel()
        {
            if (_isPreviewVisible) return;

            _isPreviewVisible = true;
            
            // Set initial height and make visible
            PreviewRowDefinition.Height = new GridLength(300, GridUnitType.Pixel);
            PreviewPanel.Visibility = Visibility.Visible;
            PreviewSplitter.Visibility = Visibility.Visible;

            // Animate the preview panel appearance
            var storyboard = new Storyboard();
            
            // Animate opacity
            var opacityAnimation = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(opacityAnimation, PreviewPanel);
            Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("Opacity"));
            storyboard.Children.Add(opacityAnimation);

            storyboard.Begin();
        }

        private bool _searchPanelInitialized = false;

        /// <summary>
        /// Initializes the search panel with event handlers (one-time setup)
        /// </summary>
        private void InitializeSearchPanel(RichTextBox contentRichTextBox, bool isSearchable = false)
        {
            _currentSearchRichTextBox = contentRichTextBox;
            _currentSearchMatches.Clear();
            _currentSearchMatchIndex = -1;

            // Only show search panel for searchable content
            if (!isSearchable)
            {
                PreviewSearchPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // Clear previous search
            PreviewSearchBox.Clear();
            PreviewMatchCounter.Text = "";
            PreviewSearchPanel.Visibility = Visibility.Visible;

            // Wire up search events only once
            if (!_searchPanelInitialized)
            {
                PreviewSearchBox.TextChanged += PreviewSearchBox_TextChanged;
                PreviewCaseToggle.Checked += PreviewCaseToggle_Changed;
                PreviewCaseToggle.Unchecked += PreviewCaseToggle_Changed;
                PreviewPrevButton.Click += PreviewPrevButton_Click;
                PreviewNextButton.Click += PreviewNextButton_Click;
                PreviewSearchBox.KeyDown += PreviewSearchBox_KeyDown;
                _searchPanelInitialized = true;
            }

            // Focus the search box for immediate typing
            PreviewSearchBox.Focus();
        }

        /// <summary>
        /// Hides the search panel
        /// </summary>
        private void HideSearchPanel()
        {
            PreviewSearchPanel.Visibility = Visibility.Collapsed;
            PreviewSearchBox.Clear();
            PreviewMatchCounter.Text = "";
            _currentSearchMatches.Clear();
            _currentSearchMatchIndex = -1;
            _currentSearchRichTextBox = null;
        }

        /// <summary>
        /// Hides the preview panel with animation
        /// </summary>
        private void HidePreviewPanel()
        {
            if (!_isPreviewVisible) return;

            var storyboard = new Storyboard();
            
            // Animate opacity
            var opacityAnimation = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(opacityAnimation, PreviewPanel);
            Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("Opacity"));
            storyboard.Children.Add(opacityAnimation);

            storyboard.Completed += (s, e) =>
            {
                PreviewPanel.Visibility = Visibility.Collapsed;
                PreviewSplitter.Visibility = Visibility.Collapsed;
                PreviewRowDefinition.Height = new GridLength(0);
                _isPreviewVisible = false;
                _currentPreviewFile = null;
                _currentPreviewPackage = null;
                HideSearchPanel();
            };

            storyboard.Begin();
        }

        /// <summary>
        /// Close preview button click handler
        /// </summary>
        private void ClosePreview_Click(object sender, RoutedEventArgs e)
        {
            HidePreviewPanel();
        }

        #endregion

        #region File Preview Methods

        /// <summary>
        /// Shows a preview of the selected file
        /// </summary>
        /// <param name="filePath">Path to the file within the package</param>
        /// <param name="packageItem">The package containing the file</param>
        public void ShowFilePreview(string filePath, PackageItem packageItem)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || packageItem == null)
                {
                    HidePreviewPanel();
                    return;
                }

                _currentPreviewFile = filePath;
                _currentPreviewPackage = packageItem;

                // Update header text
                var fileName = Path.GetFileName(filePath);
                PreviewHeaderText.Text = $"Preview: {fileName}";

                // Determine file type and show appropriate preview
                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                
                switch (extension)
                {
                    case ".jpg":
                    case ".jpeg":
                    case ".png":
                    case ".gif":
                    case ".bmp":
                    case ".tiff":
                    case ".webp":
                        ShowImagePreview(filePath, packageItem);
                        break;
                    
                    case ".mp3":
                    case ".wav":
                    case ".ogg":
                    case ".m4a":
                        ShowAudioPreview(filePath, packageItem);
                        break;
                    
                    case ".json":
                    case ".vam":
                    case ".vaj":
                        ShowJsonPreview(filePath, packageItem);
                        break;
                    
                    case ".txt":
                    case ".cs":
                    case ".xml":
                    case ".log":
                    case ".vmi":
                        ShowTextPreview(filePath, packageItem);
                        break;
                    
                    default:
                        ShowGenericFileInfo(filePath, packageItem);
                        break;
                }

                ShowPreviewPanel();
            }
            catch (Exception ex)
            {
                ShowErrorPreview($"Error loading preview: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows an image preview
        /// </summary>
        private void ShowImagePreview(string filePath, PackageItem packageItem)
        {
            try
            {
                var imageBytes = ExtractFileFromPackage(filePath, packageItem);
                if (imageBytes == null) return;

                using (var stream = new MemoryStream(imageBytes))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var image = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxWidth = 800,
                        MaxHeight = 600
                    };

                    // Wrap image in border with rounded corners
                    var imageBorder = new Border
                    {
                        Child = image,
                        Background = new SolidColorBrush(Color.FromArgb(10, 50, 50, 50)),
                        CornerRadius = new CornerRadius(UI_CORNER_RADIUS),
                        ClipToBounds = true,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(80, 100, 100, 100)),
                        BorderThickness = new Thickness(1)
                    };

                    // Add image info
                    var infoPanel = new StackPanel { Orientation = Orientation.Vertical };
                    
                    var imageInfo = new TextBlock
                    {
                        Text = $"Dimensions: {bitmap.PixelWidth} Ã— {bitmap.PixelHeight}\n" +
                               $"Size: {FormatFileSize(imageBytes.Length)}\n" +
                               $"Format: {Path.GetExtension(filePath).ToUpperInvariant()}",
                        FontSize = 11,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(0, 0, 0, 8),
                        TextAlignment = TextAlignment.Center
                    };

                    infoPanel.Children.Add(imageInfo);
                    infoPanel.Children.Add(imageBorder);

                    PreviewContentPresenter.Content = infoPanel;
                }
            }
            catch (Exception ex)
            {
                ShowErrorPreview($"Error loading image: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows an audio preview with play controls
        /// </summary>
        private void ShowAudioPreview(string filePath, PackageItem packageItem)
        {
            try
            {
                var audioBytes = ExtractFileFromPackage(filePath, packageItem);
                if (audioBytes == null) return;

                var panel = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };

                // Audio file info
                var audioInfo = new TextBlock
                {
                    Text = $"Audio File: {Path.GetFileName(filePath)}\n" +
                           $"Size: {FormatFileSize(audioBytes.Length)}\n" +
                           $"Format: {Path.GetExtension(filePath).ToUpperInvariant()}",
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 16),
                    TextAlignment = TextAlignment.Center
                };

                // Play button
                var playButton = new Button
                {
                    Content = "–¶ Play Audio",
                    Width = 120,
                    Height = 35,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 8)
                };

                playButton.Click += (s, e) => PlayAudioFile(filePath, packageItem);

                // Note about external player
                var note = new TextBlock
                {
                    Text = "Audio will open in your default media player",
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    TextAlignment = TextAlignment.Center
                };

                panel.Children.Add(audioInfo);
                panel.Children.Add(playButton);
                panel.Children.Add(note);

                PreviewContentPresenter.Content = panel;
            }
            catch (Exception ex)
            {
                ShowErrorPreview($"Error loading audio: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows a JSON file preview with formatted content and search
        /// </summary>
        private void ShowJsonPreview(string filePath, PackageItem packageItem)
        {
            try
            {
                var jsonBytes = ExtractFileFromPackage(filePath, packageItem);
                if (jsonBytes == null) return;

                var jsonText = Encoding.UTF8.GetString(jsonBytes);
                
                // Try to format JSON
                string formattedJson;
                try
                {
                    var jsonDoc = JsonDocument.Parse(jsonText);
                    formattedJson = JsonSerializer.Serialize(jsonDoc, new JsonSerializerOptions 
                    { 
                        WriteIndented = true 
                    });
                }
                catch
                {
                    formattedJson = jsonText; // Use original if parsing fails
                }

                // Use RichTextBox for better highlighting and scrolling
                var richTextBox = new RichTextBox
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Background = (SolidColorBrush)FindResource(SystemColors.WindowBrushKey),
                    Foreground = (SolidColorBrush)FindResource(SystemColors.WindowTextBrushKey),
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                };

                // Set the text content
                var paragraph = new Paragraph(new Run(formattedJson));
                paragraph.Margin = new Thickness(0);
                paragraph.LineHeight = 1;
                richTextBox.Document.Blocks.Clear();
                richTextBox.Document.Blocks.Add(paragraph);

                // Store original content for search
                richTextBox.Tag = formattedJson;

                // Apply context menu styling for dark theme
                ApplyRichTextBoxContextMenuStyling(richTextBox);

                PreviewHeaderText.Text = $"JSON Preview - {Path.GetFileName(filePath)} ({FormatFileSize(jsonBytes.Length)})";
                PreviewContentPresenter.Content = richTextBox;
                
                // Initialize search panel with searchable content
                InitializeSearchPanel(richTextBox, true);
            }
            catch (Exception ex)
            {
                ShowErrorPreview($"Error loading JSON: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows preview for text files with search functionality
        /// </summary>
        private void ShowTextPreview(string filePath, PackageItem packageItem)
        {
            try
            {
                var textBytes = ExtractFileFromPackage(filePath, packageItem);
                if (textBytes == null)
                {
                    ShowErrorPreview("Could not extract text file from package");
                    return;
                }

                var content = Encoding.UTF8.GetString(textBytes);
                
                // Truncate very large files for performance
                bool wasTruncated = false;
                if (content.Length > 50000)
                {
                    content = content.Substring(0, 50000);
                    wasTruncated = true;
                }

                // Use RichTextBox for better highlighting and scrolling
                var richTextBox = new RichTextBox
                {
                    FontFamily = new FontFamily("Consolas, Courier New"),
                    FontSize = 12,
                    Background = (SolidColorBrush)FindResource(SystemColors.WindowBrushKey),
                    Foreground = (SolidColorBrush)FindResource(SystemColors.WindowTextBrushKey),
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                };

                // Set the text content
                var paragraph = new Paragraph(new Run(content + (wasTruncated ? "\n\n... (File truncated for performance)" : "")));
                paragraph.Margin = new Thickness(0);
                paragraph.LineHeight = 1;
                richTextBox.Document.Blocks.Clear();
                richTextBox.Document.Blocks.Add(paragraph);

                // Store original content for search
                richTextBox.Tag = content;

                // Apply context menu styling for dark theme
                ApplyRichTextBoxContextMenuStyling(richTextBox);

                PreviewHeaderText.Text = $"Text Preview - {Path.GetFileName(filePath)} ({FormatFileSize(textBytes.Length)})";
                PreviewContentPresenter.Content = richTextBox;
                
                // Initialize search panel with searchable content
                InitializeSearchPanel(richTextBox, true);
            }
            catch (Exception ex)
            {
                ShowErrorPreview($"Error loading text: {ex.Message}");
            }
        }

        /// <summary>
        /// Performs search when text changes
        /// </summary>
        private void PreviewSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PerformPreviewSearch();
        }

        /// <summary>
        /// Performs search when case toggle changes
        /// </summary>
        private void PreviewCaseToggle_Changed(object sender, RoutedEventArgs e)
        {
            PerformPreviewSearch();
        }

        /// <summary>
        /// Navigates to previous match
        /// </summary>
        private void PreviewPrevButton_Click(object sender, RoutedEventArgs e)
        {
            NavigatePreviewMatch(-1);
        }

        /// <summary>
        /// Navigates to next match
        /// </summary>
        private void PreviewNextButton_Click(object sender, RoutedEventArgs e)
        {
            NavigatePreviewMatch(1);
        }

        /// <summary>
        /// Handles keyboard shortcuts in search box
        /// </summary>
        private void PreviewSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    NavigatePreviewMatch(-1);
                else
                    NavigatePreviewMatch(1);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                PreviewSearchBox.Clear();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Performs the preview search
        /// </summary>
        private void PerformPreviewSearch()
        {
            if (_currentSearchRichTextBox == null) return;

            var searchText = PreviewSearchBox.Text;
            var originalContent = _currentSearchRichTextBox.Tag as string ?? "";
            
            _currentSearchMatches.Clear();
            _currentSearchMatchIndex = -1;

            // Clear previous highlighting
            ClearHighlighting(_currentSearchRichTextBox);

            if (string.IsNullOrEmpty(searchText))
            {
                PreviewMatchCounter.Text = "";
                PreviewSearchBox.Background = (SolidColorBrush)FindResource(SystemColors.WindowBrushKey);
                return;
            }

            // Find all matches
            var comparison = PreviewCaseToggle.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var index = 0;
            while ((index = originalContent.IndexOf(searchText, index, comparison)) != -1)
            {
                _currentSearchMatches.Add(index);
                index += searchText.Length;
            }

            // Update match counter and highlight matches
            if (_currentSearchMatches.Count > 0)
            {
                _currentSearchMatchIndex = 0;
                PreviewMatchCounter.Text = $"1 of {_currentSearchMatches.Count}";
                PreviewMatchCounter.Foreground = Brushes.Green;
                PreviewSearchBox.Background = (SolidColorBrush)FindResource(SystemColors.WindowBrushKey);
                HighlightAllMatches(_currentSearchRichTextBox, searchText);
                ScrollToMatch(_currentSearchRichTextBox, _currentSearchMatches[_currentSearchMatchIndex]);
            }
            else
            {
                PreviewMatchCounter.Text = "0 matches";
                PreviewMatchCounter.Foreground = Brushes.Red;
                PreviewSearchBox.Background = new SolidColorBrush(Color.FromArgb(80, 255, 0, 0)); // Light red
            }
        }

        /// <summary>
        /// Navigates to the next or previous match
        /// </summary>
        private void NavigatePreviewMatch(int direction)
        {
            if (_currentSearchMatches.Count == 0 || _currentSearchRichTextBox == null) return;

            _currentSearchMatchIndex += direction;
            if (_currentSearchMatchIndex < 0) _currentSearchMatchIndex = _currentSearchMatches.Count - 1;
            if (_currentSearchMatchIndex >= _currentSearchMatches.Count) _currentSearchMatchIndex = 0;

            PreviewMatchCounter.Text = $"{_currentSearchMatchIndex + 1} of {_currentSearchMatches.Count}";
            PreviewMatchCounter.Foreground = Brushes.Green;
            
            ScrollToMatch(_currentSearchRichTextBox, _currentSearchMatches[_currentSearchMatchIndex]);
        }

        /// <summary>
        /// Clears all highlighting in the RichTextBox
        /// </summary>
        private void ClearHighlighting(RichTextBox richTextBox)
        {
            try
            {
                var textRange = new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd);
                textRange.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
            }
            catch
            {
                // Ignore errors
            }
        }

        /// <summary>
        /// Highlights all matches in the RichTextBox
        /// </summary>
        private void HighlightAllMatches(RichTextBox richTextBox, string searchText)
        {
            try
            {
                var document = richTextBox.Document;
                var contentStart = document.ContentStart;
                var contentEnd = document.ContentEnd;
                
                // Get the actual text from the document
                var textRange = new TextRange(contentStart, contentEnd);
                var documentText = textRange.Text;
                
                var comparison = PreviewCaseToggle.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                
                // Find matches in the actual document text
                var index = 0;
                while ((index = documentText.IndexOf(searchText, index, comparison)) != -1)
                {
                    // Navigate to the position using character count
                    var start = contentStart;
                    var charCount = 0;
                    
                    while (start != null && charCount < index)
                    {
                        if (start.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                        {
                            var textRun = start.GetTextInRun(LogicalDirection.Forward);
                            var runLength = Math.Min(textRun.Length, index - charCount);
                            
                            if (charCount + runLength <= index)
                            {
                                charCount += runLength;
                                start = start.GetPositionAtOffset(runLength, LogicalDirection.Forward);
                            }
                            else
                            {
                                var offset = index - charCount;
                                start = start.GetPositionAtOffset(offset, LogicalDirection.Forward);
                                charCount = index;
                            }
                        }
                        else
                        {
                            start = start.GetNextContextPosition(LogicalDirection.Forward);
                        }
                    }
                    
                    if (start != null)
                    {
                        // Now find the end position by counting characters forward
                        var end = start;
                        var endCharCount = 0;
                        
                        while (end != null && endCharCount < searchText.Length)
                        {
                            if (end.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                            {
                                var textRun = end.GetTextInRun(LogicalDirection.Forward);
                                var runLength = Math.Min(textRun.Length, searchText.Length - endCharCount);
                                
                                if (endCharCount + runLength <= searchText.Length)
                                {
                                    endCharCount += runLength;
                                    end = end.GetPositionAtOffset(runLength, LogicalDirection.Forward);
                                }
                                else
                                {
                                    var offset = searchText.Length - endCharCount;
                                    end = end.GetPositionAtOffset(offset, LogicalDirection.Forward);
                                    endCharCount = searchText.Length;
                                }
                            }
                            else
                            {
                                end = end.GetNextContextPosition(LogicalDirection.Forward);
                            }
                        }
                        
                        if (end != null)
                        {
                            var highlightRange = new TextRange(start, end);
                            highlightRange.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Yellow);
                            highlightRange.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Black);
                        }
                    }
                    
                    index += searchText.Length;
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        /// <summary>
        /// Scrolls to a specific match in the RichTextBox
        /// </summary>
        private void ScrollToMatch(RichTextBox richTextBox, int matchIndex)
        {
            try
            {
                var document = richTextBox.Document;
                var contentStart = document.ContentStart;
                var contentEnd = document.ContentEnd;
                
                // Get the actual text from the document
                var textRange = new TextRange(contentStart, contentEnd);
                var documentText = textRange.Text;
                
                // Navigate to the position using character count
                var start = contentStart;
                var charCount = 0;
                
                while (start != null && charCount < matchIndex)
                {
                    if (start.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                    {
                        var textRun = start.GetTextInRun(LogicalDirection.Forward);
                        var runLength = Math.Min(textRun.Length, matchIndex - charCount);
                        
                        if (charCount + runLength <= matchIndex)
                        {
                            charCount += runLength;
                            start = start.GetPositionAtOffset(runLength, LogicalDirection.Forward);
                        }
                        else
                        {
                            var offset = matchIndex - charCount;
                            start = start.GetPositionAtOffset(offset, LogicalDirection.Forward);
                            charCount = matchIndex;
                        }
                    }
                    else
                    {
                        start = start.GetNextContextPosition(LogicalDirection.Forward);
                    }
                }
                
                if (start != null)
                {
                    var rect = start.GetCharacterRect(LogicalDirection.Forward);
                    richTextBox.ScrollToVerticalOffset(Math.Max(0, rect.Top - 50)); // Offset for better visibility
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        /// <summary>
        /// Shows generic file information for unsupported file types
        /// </summary>
        private void ShowGenericFileInfo(string filePath, PackageItem packageItem)
        {
            try
            {
                var fileBytes = ExtractFileFromPackage(filePath, packageItem);
                var fileSize = fileBytes?.Length ?? 0;

                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

                var icon = new TextBlock
                {
                    Text = "📝",
                    FontSize = 48,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 16)
                };

                var info = new TextBlock
                {
                    Text = $"File: {Path.GetFileName(filePath)}\n" +
                           $"Type: {Path.GetExtension(filePath).ToUpperInvariant()}\n" +
                           $"Size: {FormatFileSize(fileSize)}\n\n" +
                           "Preview not available for this file type",
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center,
                    LineHeight = 18
                };

                var openButton = new Button
                {
                    Content = "Open File",
                    Width = 100,
                    Height = 30,
                    Margin = new Thickness(0, 16, 0, 0)
                };

                openButton.Click += (s, e) => OpenFileInViewer(filePath, packageItem);

                panel.Children.Add(icon);
                panel.Children.Add(info);
                panel.Children.Add(openButton);

                PreviewContentPresenter.Content = panel;
            }
            catch (Exception ex)
            {
                ShowErrorPreview($"Error loading file info: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows an error message in the preview panel
        /// </summary>
        private void ShowErrorPreview(string errorMessage)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            var icon = new TextBlock
            {
                Text = "–ï¸",
                FontSize = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var message = new TextBlock
            {
                Text = errorMessage,
                FontSize = 12,
                Foreground = Brushes.Red,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            panel.Children.Add(icon);
            panel.Children.Add(message);

            PreviewContentPresenter.Content = panel;
            ShowPreviewPanel();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Extracts a file from a VAR package
        /// </summary>
        private byte[] ExtractFileFromPackage(string filePath, PackageItem packageItem)
        {
            try
            {
                if (string.IsNullOrEmpty(_settingsManager?.Settings?.SelectedFolder))
                    return null;

                string packageVarPath = null;
                string vamFolder = _settingsManager.Settings.SelectedFolder;
                
                if (_packageManager?.PackageMetadata.TryGetValue(packageItem.MetadataKey, out var metadata) == true)
                {
                    var possiblePaths = new[]
                    {
                        Path.Combine(vamFolder, "AddonPackages", metadata.Filename),
                        Path.Combine(vamFolder, "AllPackages", metadata.Filename),
                        Path.Combine(vamFolder, "ArchivedPackages", metadata.Filename)
                    };
                    
                    foreach (var path in possiblePaths)
                    {
                        if (File.Exists(path))
                        {
                            packageVarPath = path;
                            break;
                        }
                    }
                }
                
                if (string.IsNullOrEmpty(packageVarPath))
                    return null;

                using (var archive = ZipFile.OpenRead(packageVarPath))
                {
                    var entry = archive.Entries.FirstOrDefault(e => 
                        e.FullName.Equals(filePath, StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.Replace("\\", "/").Equals(filePath.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase));
                    
                    if (entry == null)
                        return null;

                    using (var stream = entry.Open())
                    using (var memoryStream = new MemoryStream())
                    {
                        stream.CopyTo(memoryStream);
                        return memoryStream.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Plays an audio file by extracting it to temp and opening with default player
        /// </summary>
        private void PlayAudioFile(string filePath, PackageItem packageItem)
        {
            try
            {
                var audioBytes = ExtractFileFromPackage(filePath, packageItem);
                if (audioBytes == null) 
                {
                    ShowErrorPreview("Could not extract audio file from package");
                    return;
                }

                var tempPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(filePath));
                File.WriteAllBytes(tempPath, audioBytes);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowErrorPreview($"Error playing audio: {ex.Message}");
            }
        }

        /// <summary>
        /// Formats file size for display
        /// </summary>
        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }

        /// <summary>
        /// Applies dark theme styling to RichTextBox context menu
        /// </summary>
        private void ApplyRichTextBoxContextMenuStyling(RichTextBox richTextBox)
        {
            try
            {
                // Hook into context menu opening to apply styling
                richTextBox.ContextMenuOpening += (s, e) =>
                {
                    if (richTextBox.ContextMenu != null)
                    {
                        richTextBox.ContextMenu.Background = (SolidColorBrush)FindResource(SystemColors.ControlBrushKey);
                        richTextBox.ContextMenu.Foreground = (SolidColorBrush)FindResource(SystemColors.ControlTextBrushKey);
                        richTextBox.ContextMenu.BorderBrush = (SolidColorBrush)FindResource(SystemColors.ActiveBorderBrushKey);
                        richTextBox.ContextMenu.BorderThickness = new Thickness(1);

                        // Style all menu items
                        foreach (var item in richTextBox.ContextMenu.Items)
                        {
                            if (item is MenuItem menuItem)
                            {
                                menuItem.Background = (SolidColorBrush)FindResource(SystemColors.ControlBrushKey);
                                menuItem.Foreground = (SolidColorBrush)FindResource(SystemColors.ControlTextBrushKey);
                            }
                        }
                    }
                };
            }
            catch
            {
                // Silently ignore styling errors
            }
        }

        #endregion
    }
}

