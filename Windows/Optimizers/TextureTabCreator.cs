using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    public partial class MainWindow
    {
        private TabItem CreateTextureTab(string packageName, TextureValidator.ValidationResult result, VarMetadata packageMetadata, Window parentDialog)
        {
            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            int textureCount = result.Textures.Count;
            headerPanel.Children.Add(new TextBlock { Text = $"Textures ({textureCount})", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "TEXTURE OPTIMIZATION:\n\n" +
                "✓ Only downscales larger textures (never upscales)\n" +
                "✓ Selecting a resolution will resize ALL textures to that size\n" +
                "✓ Quality is reduced even if texture is already at target size\n" +
                "✓ Use 'Keep' to leave textures unchanged\n\n" +
                "Example: If you select 4K:\n" +
                "  • 8K textures †’ Downscaled to 4K\n" +
                "  • 4K textures †’ Re-encoded to 4K (quality reduced)\n" +
                "  • 2K textures †’ Upscaled to 4K (not recommended)\n\n" +
                "Quality: Uses 90% JPEG quality and optimized bilinear interpolation for fast processing.";
            
            headerPanel.Children.Add(CreateTooltipInfoIcon(tooltipText));

            var tab = new TabItem
            {
                Header = headerPanel,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var summaryGrid = new Grid();
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                
            var summaryPanel = new StackPanel();
            Grid.SetColumn(summaryPanel, 0);
            
            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                var errorText = new TextBlock
                {
                    Text = $"–ï¸ Warning: {result.ErrorMessage}",
                    FontSize = 12,
                    FontWeight = FontWeights.Normal,
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 5)
                };
                summaryPanel.Children.Add(errorText);
                
                var errorNote = new TextBlock
                {
                    Text = "Note: Some scene files have malformed JSON. Textures found before the error are shown below.",
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                summaryPanel.Children.Add(errorNote);
            }

            bool hasCompressedTextures = false;
            if (packageMetadata != null && !string.IsNullOrEmpty(packageMetadata.Description))
            {
                string descLower = packageMetadata.Description.ToLowerInvariant();
                hasCompressedTextures = descLower.Contains("texture") && 
                                     (descLower.Contains("compress") || 
                                      descLower.Contains("convert") || 
                                      descLower.Contains("4k") || 
                                      descLower.Contains("2k") || 
                                      descLower.Contains("8k") ||
                                      descLower.Contains("optimiz"));
                
                if (hasCompressedTextures)
                {
                    ParseOriginalTextureData(packageMetadata.Description, result.Textures);
                }
            }

            var statusText = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var statusBuilder = new StringBuilder();
            
            if (result.IsValid)
            {
                statusBuilder.Append($"âœ… All pass ({result.FoundCount})");
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            }
            else if (result.TotalTextureReferences == 0)
            {
                statusBuilder.Append("–ï¸ No Texture References Found");
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 152, 0));
            }
            else
            {
                statusBuilder.Append("┌ Missing Textures Detected");
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                statusBuilder.Append($"  |  âœ… Found: {result.FoundCount}  |  ┌ Missing: {result.MissingCount}");
            }

            if (hasCompressedTextures)
            {
                statusBuilder.Append("  |  „¹ï¸ This package has compressed textures");
            }
            
            statusText.Text = statusBuilder.ToString();
            
            bool hasArchiveSource = result.Textures.Any(t => t.HasArchiveSource);
            
            var statusLinePanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            statusLinePanel.Children.Add(statusText);
            
            if (hasArchiveSource)
            {
                var separatorText = new TextBlock
                {
                    Text = "  |  ",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                statusLinePanel.Children.Add(separatorText);
                
                var archiveText = new TextBlock
                {
                    Text = "📦 Original package available",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                statusLinePanel.Children.Add(archiveText);
                
                string archiveTooltip = "ORIGINAL PACKAGE DETECTED:\n\n" +
                    "✓ Original high-quality version found in ArchivedPackages folder\n" +
                    "✓ Can upscale to higher resolutions using original quality\n" +
                    "✓ Downscaling will use original as source for better results\n\n" +
                    "Example:\n" +
                    "  • Current: 4K textures\n" +
                    "  • Archive: 8K originals\n" +
                    "  • You can restore to 8K or optimize to 2K from 8K source\n\n" +
                    "This ensures maximum quality for all operations!";
                
                var tooltipIcon = CreateTooltipInfoIcon(archiveTooltip);
                tooltipIcon.VerticalAlignment = VerticalAlignment.Center;
                tooltipIcon.Margin = new Thickness(5, 0, 0, 0);
                statusLinePanel.Children.Add(tooltipIcon);
            }
            
            summaryPanel.Children.Add(statusLinePanel);
            summaryGrid.Children.Add(summaryPanel);
            Grid.SetRow(summaryGrid, 0);
            tabGrid.Children.Add(summaryGrid);

            if (result.Textures.Count > 0)
            {
                var dataGrid = CreateTextureDataGrid(result.Textures, packageName);
                Grid.SetRow(dataGrid, 2);
                tabGrid.Children.Add(dataGrid);
            }
            else
            {
                var noTexturesInfo = new TextBlock
                {
                    Text = "No texture references were found in the scene JSON files.\n\n" +
                           "This could mean:\n" +
                           "• The package doesn't contain scene files\n" +
                           "• The scene files don't reference textures\n" +
                           "• The textures are referenced in a different format",
                    FontSize = 12,
                    Margin = new Thickness(10),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180))
                };
                Grid.SetRow(noTexturesInfo, 2);
                tabGrid.Children.Add(noTexturesInfo);
            }

            tab.Content = tabGrid;
            return tab;
        }
    }
}

