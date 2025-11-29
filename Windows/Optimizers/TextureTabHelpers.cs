using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using VPM.Services;

namespace VPM
{
    public partial class MainWindow
    {
        private DataGrid CreateTextureDataGrid(List<TextureValidator.TextureInfo> textures, string packageName = null, bool isBulkMode = false)
        {
            var dataGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 32,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220))
            };

            var columnHeaderStyle = new Style(typeof(DataGridColumnHeader));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(60, 60, 60))));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(8, 8, 8, 8)));
            columnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
            dataGrid.ColumnHeaderStyle = columnHeaderStyle;

            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(30, 30, 30))));
            rowStyle.Setters.Add(new Setter(DataGridRow.BorderThicknessProperty, new Thickness(0)));
            
            // Alternating row color
            var alternateTrigger = new Trigger { Property = DataGridRow.AlternationIndexProperty, Value = 1 };
            alternateTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(35, 35, 35))));
            rowStyle.Triggers.Add(alternateTrigger);
            
            // Hover effect
            var rowHoverTrigger = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
            rowHoverTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
            rowStyle.Triggers.Add(rowHoverTrigger);
            
            // Active selection (window focused)
            var selectedTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(55, 65, 75))));
            selectedTrigger.Setters.Add(new Setter(DataGridRow.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))));
            rowStyle.Triggers.Add(selectedTrigger);
            
            dataGrid.RowStyle = rowStyle;
            dataGrid.AlternationCount = 2;

            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0)));
            cellStyle.Setters.Add(new Setter(DataGridCell.FocusVisualStyleProperty, null));
            cellStyle.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
            
            var cellSelectedTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            cellSelectedTrigger.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
            cellSelectedTrigger.Setters.Add(new Setter(DataGridCell.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))));
            cellStyle.Triggers.Add(cellSelectedTrigger);
            
            dataGrid.CellStyle = cellStyle;

            var statusColumn = new DataGridTemplateColumn
            {
                Header = "S",
                Width = new DataGridLength(30),
                HeaderStyle = CreateCenteredHeaderStyle(),
                SortMemberPath = "Exists"
            };
            var statusTemplate = new DataTemplate();
            var statusFactory = new FrameworkElementFactory(typeof(TextBlock));
            statusFactory.SetValue(TextBlock.TextProperty, "●");
            statusFactory.SetBinding(TextBlock.ForegroundProperty, new Binding("Exists")
            {
                Converter = new StatusColorConverter()
            });
            statusFactory.SetValue(TextBlock.FontSizeProperty, 20.0);
            statusFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            statusFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            statusTemplate.VisualTree = statusFactory;
            statusColumn.CellTemplate = statusTemplate;
            dataGrid.Columns.Add(statusColumn);

            // Add Package column for bulk mode
            if (isBulkMode)
            {
                var packageColumn = new DataGridTextColumn
                {
                    Header = "Package",
                    Binding = new Binding("PackageName"),
                    Width = new DataGridLength(200),
                    HeaderStyle = CreateCenteredHeaderStyle()
                };
                packageColumn.ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                        new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)),
                        new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(100, 180, 255))),
                        new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold)
                    }
                };
                dataGrid.Columns.Add(packageColumn);
            }

            var typeColumn = new DataGridTextColumn
            {
                Header = isBulkMode ? "Type" : "Texture Type",
                Binding = new Binding("TextureType"),
                Width = new DataGridLength(isBulkMode ? 100 : 120),
                HeaderStyle = CreateCenteredHeaderStyle()
            };
            typeColumn.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220)))
                }
            };
            dataGrid.Columns.Add(typeColumn);

            var resColumn = new DataGridTemplateColumn
            {
                Header = "Rez",
                Width = new DataGridLength(70),
                SortMemberPath = "Resolution",
                HeaderStyle = CreateCenteredHeaderStyle()
            };
            var resTemplate = new DataTemplate();
            var resFactory = new FrameworkElementFactory(typeof(TextBlock));
            resFactory.SetBinding(TextBlock.TextProperty, new Binding("Resolution"));
            resFactory.SetBinding(TextBlock.ForegroundProperty, new Binding("Resolution")
            {
                Converter = new ResolutionColorConverter()
            });
            resFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            resFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            resFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            resTemplate.VisualTree = resFactory;
            resColumn.CellTemplate = resTemplate;
            dataGrid.Columns.Add(resColumn);

            var sizeColumn = new DataGridTextColumn
            {
                Header = "Size",
                Binding = new Binding("FileSizeFormatted"),
                Width = new DataGridLength(90),
                HeaderStyle = CreateCenteredHeaderStyle(),
                SortMemberPath = "FileSize"
            };
            sizeColumn.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 200, 150))),
                    new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center)
                }
            };
            dataGrid.Columns.Add(sizeColumn);

            var origResColumn = new DataGridTextColumn
            {
                Header = "Orig Rez",
                Binding = new Binding("OriginalResolution"),
                Width = new DataGridLength(70),
                HeaderStyle = CreateCenteredHeaderStyle()
            };
            origResColumn.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 180, 180))),
                    new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(TextBlock.FontStyleProperty, FontStyles.Italic)
                }
            };
            dataGrid.Columns.Add(origResColumn);

            var origSizeColumn = new DataGridTextColumn
            {
                Header = "Orig Size",
                Binding = new Binding("OriginalFileSizeFormatted"),
                Width = new DataGridLength(90),
                HeaderStyle = CreateCenteredHeaderStyle(),
                SortMemberPath = "OriginalFileSize"
            };
            origSizeColumn.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 180, 180))),
                    new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(TextBlock.FontStyleProperty, FontStyles.Italic)
                }
            };
            dataGrid.Columns.Add(origSizeColumn);

            var compressionColumn = new DataGridTextColumn
            {
                Header = "Saved",
                Binding = new Binding("CompressionPercentage"),
                Width = new DataGridLength(70),
                HeaderStyle = CreateCenteredHeaderStyle()
            };
            compressionColumn.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80))),
                    new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold)
                }
            };
            dataGrid.Columns.Add(compressionColumn);

            var pathColumn = new DataGridTemplateColumn
            {
                Header = "Path",
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                HeaderStyle = CreateCenteredHeaderStyle()
            };
            var pathTemplate = new DataTemplate();
            var pathFactory = new FrameworkElementFactory(typeof(TextBlock));
            pathFactory.SetBinding(TextBlock.TextProperty, new Binding("ReferencedPath"));
            pathFactory.SetBinding(TextBlock.ToolTipProperty, new Binding("ReferencedPath"));
            pathFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            pathFactory.SetValue(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0));
            pathFactory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            pathFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 180, 180)));
            pathTemplate.VisualTree = pathFactory;
            pathColumn.CellTemplate = pathTemplate;
            dataGrid.Columns.Add(pathColumn);

            AddConversionCheckboxColumn(dataGrid, textures, "8K", "ConvertTo8K", "CanConvertTo8K", Color.FromRgb(255, 215, 0), 7680);
            AddConversionCheckboxColumn(dataGrid, textures, "4K", "ConvertTo4K", "CanConvertTo4K", Color.FromRgb(192, 192, 192), 4096);
            AddConversionCheckboxColumn(dataGrid, textures, "2K", "ConvertTo2K", "CanConvertTo2K", Color.FromRgb(205, 127, 50), 2048);

            dataGrid.ItemsSource = textures;
            
            dataGrid.PreviewMouseDown += OptimizeDataGrid_PreviewMouseDown;
            dataGrid.PreviewMouseUp += OptimizeDataGrid_PreviewMouseUp;
            dataGrid.PreviewMouseMove += OptimizeDataGrid_PreviewMouseMove;
            
            // Only add double-click handler for single-package mode
            if (!isBulkMode && !string.IsNullOrEmpty(packageName))
            {
                dataGrid.MouseDoubleClick += (s, e) =>
                {
                    if (dataGrid.SelectedItem is TextureValidator.TextureInfo texture && texture.Exists)
                    {
                        OpenTextureFromArchive(packageName, texture.ReferencedPath);
                        e.Handled = true;
                    }
                };
            }
            else if (isBulkMode)
            {
                // For bulk mode, use PackageName from the texture item
                dataGrid.MouseDoubleClick += (s, e) =>
                {
                    if (dataGrid.SelectedItem is TextureValidator.TextureInfo texture && texture.Exists && !string.IsNullOrEmpty(texture.PackageName))
                    {
                        OpenTextureFromArchive(texture.PackageName, texture.ReferencedPath);
                        e.Handled = true;
                    }
                };
            }

            return dataGrid;
        }

        private void AddConversionCheckboxColumn(DataGrid dataGrid, List<TextureValidator.TextureInfo> textures, string header, string bindingPath, string isEnabledPath, Color headerColor, int targetResolution)
        {
            var columnWidth = targetResolution == 0 ? 90 : 70; // 90px for Keep, 60px for 8K/4K/2K
            var column = new DataGridTemplateColumn
            {
                Width = new DataGridLength(columnWidth),
                CanUserResize = false
            };

            var headerButton = new Button
            {
                Content = header,
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = new SolidColorBrush(headerColor),
                BorderBrush = new SolidColorBrush(headerColor),
                BorderThickness = new Thickness(1.5),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(16, 6, 16, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinWidth = 45,
                ToolTip = targetResolution == 0 ? "Keep all textures unchanged" : $"Resize all textures to {header}\n(Will reduce quality even if already at this size)"
            };
            
            headerButton.MouseEnter += (s, e) => headerButton.Background = new SolidColorBrush(Color.FromRgb(65, 65, 65));
            headerButton.MouseLeave += (s, e) => headerButton.Background = new SolidColorBrush(Color.FromRgb(50, 50, 50));
            
            var buttonStyle = new Style(typeof(Button));
            var controlTemplate = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS));
            borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            
            var contentPresenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenterFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenterFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentPresenterFactory);
            
            controlTemplate.VisualTree = borderFactory;
            buttonStyle.Setters.Add(new Setter(Button.TemplateProperty, controlTemplate));
            headerButton.Style = buttonStyle;

            headerButton.Click += (s, e) =>
            {
                foreach (var texture in textures)
                {
                    if (targetResolution == 0)
                    {
                        // Unchanged column
                        texture.KeepUnchanged = true;
                    }
                    else
                    {
                        int maxAvailableDim = texture.HasArchiveSource && texture.ArchiveMaxDimension > 0 
                            ? texture.ArchiveMaxDimension 
                            : Math.Max(texture.Width, texture.Height);
                        
                        if (targetResolution == 7680 && maxAvailableDim >= 7680 && texture.CanConvertTo8K)
                            texture.ConvertTo8K = true;
                        else if (targetResolution == 4096 && maxAvailableDim >= 4096 && texture.CanConvertTo4K)
                            texture.ConvertTo4K = true;
                        else if (targetResolution == 2048 && maxAvailableDim >= 2048 && texture.CanConvertTo2K)
                            texture.ConvertTo2K = true;
                    }
                }
            };

            column.Header = headerButton;

            var cellTemplate = new DataTemplate();
            var checkBoxFactory = new FrameworkElementFactory(typeof(CheckBox));
            checkBoxFactory.SetValue(CheckBox.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBoxFactory.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
            checkBoxFactory.SetValue(CheckBox.FocusableProperty, true);
            checkBoxFactory.SetValue(CheckBox.IsHitTestVisibleProperty, true);
            checkBoxFactory.SetBinding(CheckBox.IsCheckedProperty, new Binding(bindingPath) 
            { 
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            
            // Only bind IsEnabled if isEnabledPath is provided (null for Unchanged column which is always enabled)
            if (!string.IsNullOrEmpty(isEnabledPath))
            {
                checkBoxFactory.SetBinding(CheckBox.IsEnabledProperty, new Binding(isEnabledPath));
            }
            
            var checkBoxStyle = new Style(typeof(CheckBox));
            
            var checkboxControlTemplate = new ControlTemplate(typeof(CheckBox));
            var checkboxBorderFactory = new FrameworkElementFactory(typeof(Border));
            checkboxBorderFactory.SetValue(Border.WidthProperty, 20.0);
            checkboxBorderFactory.SetValue(Border.HeightProperty, 20.0);
            checkboxBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            checkboxBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            checkboxBorderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(headerColor));
            checkboxBorderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(30, 30, 30)));
            checkboxBorderFactory.SetValue(Border.IsHitTestVisibleProperty, true);
            
            var innerDotFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            innerDotFactory.SetValue(System.Windows.Shapes.Ellipse.WidthProperty, 12.0);
            innerDotFactory.SetValue(System.Windows.Shapes.Ellipse.HeightProperty, 12.0);
            innerDotFactory.SetValue(System.Windows.Shapes.Ellipse.FillProperty, new SolidColorBrush(headerColor));
            innerDotFactory.SetValue(System.Windows.Shapes.Ellipse.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            innerDotFactory.SetValue(System.Windows.Shapes.Ellipse.VerticalAlignmentProperty, VerticalAlignment.Center);
            innerDotFactory.SetValue(System.Windows.Shapes.Ellipse.VisibilityProperty, Visibility.Collapsed);
            innerDotFactory.Name = "InnerDot";
            
            checkboxBorderFactory.AppendChild(innerDotFactory);
            checkboxControlTemplate.VisualTree = checkboxBorderFactory;
            
            var checkedTrigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Ellipse.VisibilityProperty, Visibility.Visible, "InnerDot"));
            checkboxControlTemplate.Triggers.Add(checkedTrigger);
            
            var disabledTrigger = new Trigger { Property = CheckBox.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(CheckBox.OpacityProperty, 0.25));
            checkboxControlTemplate.Triggers.Add(disabledTrigger);
            
            checkBoxStyle.Setters.Add(new Setter(CheckBox.TemplateProperty, checkboxControlTemplate));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.CursorProperty, System.Windows.Input.Cursors.Hand));
            
            checkBoxFactory.SetValue(CheckBox.StyleProperty, checkBoxStyle);
            
            cellTemplate.VisualTree = checkBoxFactory;
            column.CellTemplate = cellTemplate;

            dataGrid.Columns.Add(column);
        }

        private void OpenTextureFromArchive(string packageName, string texturePath)
        {
            try
            {
                var pkgInfo = _packageFileManager?.GetPackageFileInfo(packageName);
                if (pkgInfo == null || string.IsNullOrEmpty(pkgInfo.CurrentPath))
                {
                    MessageBox.Show($"Could not find package: {packageName}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string packagePath = pkgInfo.CurrentPath;
                bool isVarFile = packagePath.EndsWith(".var", StringComparison.OrdinalIgnoreCase);

                if (isVarFile)
                {
                    using (var archive = SharpCompressHelper.OpenForRead(packagePath))
                    {
                        var entry = SharpCompressHelper.FindEntryByPath(archive.Archive, texturePath);
                        if (entry != null)
                        {
                            string tempPath = Path.Combine(Path.GetTempPath(), "VAM_Textures", packageName);
                            Directory.CreateDirectory(tempPath);
                            
                            string tempFile = Path.Combine(tempPath, Path.GetFileName(texturePath));
                            using (var entryStream = entry.OpenEntryStream())
                            using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
                            {
                                entryStream.CopyTo(fileStream);
                            }
                            
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = tempFile,
                                UseShellExecute = true
                            });
                        }
                        else
                        {
                            MessageBox.Show($"Texture not found in archive: {texturePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
                else
                {
                    string fullPath = Path.Combine(packagePath, texturePath);
                    if (File.Exists(fullPath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = fullPath,
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        MessageBox.Show($"Texture file not found: {fullPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening texture: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ParseOriginalTextureData(string description, List<TextureValidator.TextureInfo> textures)
        {
            if (string.IsNullOrEmpty(description) || textures == null || textures.Count == 0)
                return;

            try
            {
                var startTag = "[VPM_TEXTURE_CONVERSION_DATA]";
                var endTag = "[/VPM_TEXTURE_CONVERSION_DATA]";
                
                string conversionData = null;
                int searchStart = 0;
                
                while (true)
                {
                    int startIndex = description.IndexOf(startTag, searchStart);
                    if (startIndex == -1) break;
                    
                    startIndex += startTag.Length;
                    int endIndex = description.IndexOf(endTag, startIndex);
                    
                    if (endIndex == -1) break;
                    
                    string candidate = description.Substring(startIndex, endIndex - startIndex).Trim();
                    
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        conversionData = candidate;
                    }
                    
                    searchStart = endIndex + endTag.Length;
                }
                
                if (string.IsNullOrWhiteSpace(conversionData))
                {
                    return;
                }
                
                var lines = conversionData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine)) continue;
                    
                    var match = System.Text.RegularExpressions.Regex.Match(trimmedLine,
                        @"[•\*\-]\s*(.+?):\s*(\d+K)\s*(?:†’|-+>)\s*(\d+K)\s*\(([0-9.]+\s*[KMG]B)\s*(?:†’|-+>)\s*([0-9.]+\s*[KMG]B)\)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    
                    if (match.Success)
                    {
                        string filename = match.Groups[1].Value.Trim();
                        string origRez = match.Groups[2].Value.ToUpper();
                        string origSize = match.Groups[4].Value;
                        
                        var matchingTexture = textures.FirstOrDefault(t => 
                        {
                            if (string.IsNullOrEmpty(t.ReferencedPath)) return false;
                            var texFilename = Path.GetFileName(t.ReferencedPath);
                            return texFilename.Equals(filename, StringComparison.OrdinalIgnoreCase);
                        });
                        
                        if (matchingTexture != null)
                        {
                            if (string.IsNullOrEmpty(matchingTexture.OriginalResolution))
                            {
                                matchingTexture.OriginalResolution = origRez;
                            }
                            if (matchingTexture.OriginalFileSize == 0)
                            {
                                matchingTexture.OriginalFileSize = ParseFileSize(origSize);
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private long ParseFileSize(string sizeStr)
        {
            try
            {
                sizeStr = sizeStr.Trim().ToUpperInvariant();
                double value = 0;
                long multiplier = 1;
                
                if (sizeStr.EndsWith("KB"))
                {
                    value = double.Parse(sizeStr.Replace("KB", "").Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    multiplier = 1024;
                }
                else if (sizeStr.EndsWith("MB"))
                {
                    value = double.Parse(sizeStr.Replace("MB", "").Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    multiplier = 1024 * 1024;
                }
                else if (sizeStr.EndsWith("GB"))
                {
                    value = double.Parse(sizeStr.Replace("GB", "").Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    multiplier = 1024 * 1024 * 1024;
                }
                
                return (long)(value * multiplier);
            }
            catch
            {
                return 0;
            }
        }
    }
}

