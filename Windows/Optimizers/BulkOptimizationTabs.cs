using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    public partial class MainWindow
    {
        private TabItem CreateBulkTextureTab(TextureValidator.ValidationResult result, Window parentDialog)
        {
            // Create header with tooltip icon
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            int textureCount = result.Textures.Count;
            headerPanel.Children.Add(new TextBlock { Text = $"Textures ({textureCount})", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "TEXTURE OPTIMIZATION:\n\n" +
                "✓ Only downscales larger textures (never upscales)\n" +
                "✓ Textures at target size are NOT re-encoded (preserves quality)\n" +
                "✓ Smaller textures remain unchanged\n\n" +
                "Example: If you select 4K:\n" +
                "  • 8K textures †’ Downscaled to 4K\n" +
                "  • 4K textures †’ Kept as-is (no quality loss)\n" +
                "  • 2K textures †’ Kept as-is\n\n" +
                "Quality: Uses 90% JPEG quality and optimized bilinear interpolation for fast processing.";
            
            headerPanel.Children.Add(CreateTooltipInfoIcon(tooltipText));

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Summary panel with archive indicator and errors
            var summaryContainer = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            
            var summaryPanel = new StackPanel { Orientation = Orientation.Horizontal };
            
            var summaryText = new TextBlock
            {
                Text = $"Found {result.Textures.Count} texture(s) across all packages",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                VerticalAlignment = VerticalAlignment.Center
            };
            summaryPanel.Children.Add(summaryText);
            
            // Check if any textures have archive source
            bool hasArchiveSource = result.Textures.Any(t => t.HasArchiveSource);
            if (hasArchiveSource)
            {
                int archiveCount = result.Textures.Count(t => t.HasArchiveSource);
                var separatorText = new TextBlock
                {
                    Text = "  |  ",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                summaryPanel.Children.Add(separatorText);
                
                var archiveText = new TextBlock
                {
                    Text = $"📦 {archiveCount} with original backup",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "These textures have original high-quality versions in ArchivedPackages folder.\nCan upscale or optimize from original source for better quality."
                };
                summaryPanel.Children.Add(archiveText);
            }
            
            summaryContainer.Children.Add(summaryPanel);
            
            Grid.SetRow(summaryContainer, 0);
            tabGrid.Children.Add(summaryContainer);

            if (result.Textures.Count > 0)
            {
                // Use shared table creation method for consistency
                var dataGrid = CreateTextureDataGrid(result.Textures, packageName: null, isBulkMode: true);
                Grid.SetRow(dataGrid, 2);
                tabGrid.Children.Add(dataGrid);
            }
            else
            {
                var noTexturesInfo = new TextBlock { Text = "No texture references found.", FontSize = 12, Margin = new Thickness(10), Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetRow(noTexturesInfo, 2);
                tabGrid.Children.Add(noTexturesInfo);
            }

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Creates the Hair tab for bulk optimization
        /// </summary>
        private TabItem CreateBulkHairTab(HairOptimizer.OptimizationResult result, Window parentDialog)
        {
            // Create header with tooltip icon
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            int hairCount = result.HairItems.Count;
            headerPanel.Children.Add(new TextBlock { Text = $"Hair ({hairCount})", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "HAIR DENSITY OPTIMIZATION:\n\n" +
                " Only reduces density (never increases)\n" +
                " Hair at or below target density remains unchanged\n" +
                " Modifies curveDensity and hairMultiplier values\n\n" +
                "Example: If you select density 16:\n" +
                "  Hair with density 32 †’ Reduced to 16\n" +
                "  Hair with density 16 †’ Kept as-is\n" +
                "  Hair with density 8 †’ Kept as-is\n\n" +
                "Performance: Lower density = better FPS in VaM.";
            
            headerPanel.Children.Add(CreateTooltipInfoIcon(tooltipText));

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Create summary row with checkbox
            var summaryRow = new Grid();
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var summaryText = new TextBlock { Text = $"Found {result.TotalHairItems} hair item(s) across all packages", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetColumn(summaryText, 0);
            summaryRow.Children.Add(summaryText);

            var skipNoDensityCheckbox = new System.Windows.Controls.CheckBox
            {
                Content = "Skip Hair Without Density in Bulk Selection",
                IsChecked = true,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "When enabled, column header buttons will not modify hair items that have no curveDensity set (shown as -).\nYou can still set density manually for individual hair items.",
                Style = CreateModernCheckboxStyle()
            };
            Grid.SetColumn(skipNoDensityCheckbox, 1);
            summaryRow.Children.Add(skipNoDensityCheckbox);

            Grid.SetRow(summaryRow, 0);
            tabGrid.Children.Add(summaryRow);

            if (result.HairItems.Count > 0)
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
                
                var alternateTrigger = new Trigger { Property = DataGridRow.AlternationIndexProperty, Value = 1 };
                alternateTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(35, 35, 35))));
                rowStyle.Triggers.Add(alternateTrigger);
                
                var rowHoverTrigger = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
                rowHoverTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
                rowStyle.Triggers.Add(rowHoverTrigger);
                
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

                // Package column
                var packageColumn = new DataGridTextColumn { Header = "Package", Binding = new Binding("PackageName"), Width = new DataGridLength(200), HeaderStyle = CreateCenteredHeaderStyle() };
                packageColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(100, 180, 255))), new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold) } };
                dataGrid.Columns.Add(packageColumn);

                // Hair Name column
                var nameColumn = new DataGridTextColumn { Header = "Hair Name", Binding = new Binding("HairName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), HeaderStyle = CreateCenteredHeaderStyle() };
                nameColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))) } };
                dataGrid.Columns.Add(nameColumn);

                // Current Density column
                var densityColumn = new DataGridTextColumn { Header = "Current", Binding = new Binding("CurveDensityFormatted"), Width = new DataGridLength(80), HeaderStyle = CreateCenteredHeaderStyle() };
                densityColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center), new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 152, 0))) } };
                dataGrid.Columns.Add(densityColumn);

                // Add density toggle columns
                AddHairDensityCheckboxColumn(dataGrid, result.HairItems, "32", "ConvertTo32", "CanConvertTo32", Color.FromRgb(255, 215, 0), 32, skipNoDensityCheckbox);
                AddHairDensityCheckboxColumn(dataGrid, result.HairItems, "24", "ConvertTo24", "CanConvertTo24", Color.FromRgb(192, 192, 192), 24, skipNoDensityCheckbox);
                AddHairDensityCheckboxColumn(dataGrid, result.HairItems, "16", "ConvertTo16", "CanConvertTo16", Color.FromRgb(205, 127, 50), 16, skipNoDensityCheckbox);
                AddHairDensityCheckboxColumn(dataGrid, result.HairItems, "8", "ConvertTo8", "CanConvertTo8", Color.FromRgb(139, 69, 19), 8, skipNoDensityCheckbox);
                AddHairDensityCheckboxColumn(dataGrid, result.HairItems, "Keep", "KeepUnchanged", null, Color.FromRgb(100, 200, 100), -1, skipNoDensityCheckbox);

                dataGrid.ItemsSource = result.HairItems;

                dataGrid.PreviewMouseDown += OptimizeDataGrid_PreviewMouseDown;
                dataGrid.PreviewMouseUp += OptimizeDataGrid_PreviewMouseUp;
                dataGrid.PreviewMouseMove += OptimizeDataGrid_PreviewMouseMove;

                Grid.SetRow(dataGrid, 2);
                tabGrid.Children.Add(dataGrid);
            }
            else
            {
                var noHairText = new TextBlock { Text = "No hair items found.", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetRow(noHairText, 2);
                tabGrid.Children.Add(noHairText);
            }

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Creates the Mirrors tab for bulk optimization
        /// </summary>
        private TabItem CreateBulkMirrorsTab(HairOptimizer.OptimizationResult result, Window parentDialog)
        {
            // Create header with tooltip icon
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            int mirrorCount = result.MirrorItems.Count;
            headerPanel.Children.Add(new TextBlock { Text = $"Mirrors ({mirrorCount})", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "MIRROR OPTIMIZATION:\n\n" +
                " Disables ReflectiveSlate objects in scenes\n" +
                " Mirrors are expensive for performance\n" +
                " Can toggle individual mirrors on/off\n\n" +
                "What happens:\n" +
                "  Sets the 'on' property to 'false' in scene JSON\n" +
                "  Mirrors remain in scene but are disabled\n" +
                "  Can be re-enabled manually in VaM if needed\n\n" +
                "Performance: Disabling mirrors significantly improves FPS.";
            
            headerPanel.Children.Add(CreateTooltipInfoIcon(tooltipText));

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            int mirrorsOn = result.MirrorItems.Count(m => m.IsCurrentlyOn);
            var summaryText = new TextBlock { Text = $"Found {result.TotalMirrorItems} mirror(s) ({mirrorsOn} on) across all packages", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(summaryText, 0);
            tabGrid.Children.Add(summaryText);

            if (result.MirrorItems.Count > 0)
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
                
                var alternateTrigger = new Trigger { Property = DataGridRow.AlternationIndexProperty, Value = 1 };
                alternateTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(35, 35, 35))));
                rowStyle.Triggers.Add(alternateTrigger);
                
                var rowHoverTrigger = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
                rowHoverTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
                rowStyle.Triggers.Add(rowHoverTrigger);
                
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

                // Package column
                var packageColumn = new DataGridTextColumn { Header = "Package", Binding = new Binding("PackageName"), Width = new DataGridLength(200), HeaderStyle = CreateCenteredHeaderStyle() };
                packageColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(100, 180, 255))), new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold) } };
                dataGrid.Columns.Add(packageColumn);

                // Mirror Name column
                var nameColumn = new DataGridTextColumn { Header = "Mirror ID", Binding = new Binding("MirrorName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), HeaderStyle = CreateCenteredHeaderStyle() };
                nameColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))) } };
                dataGrid.Columns.Add(nameColumn);

                // Current Status column
                var statusColumn = new DataGridTextColumn { Header = "Current", Binding = new Binding("CurrentStatus"), Width = new DataGridLength(80), HeaderStyle = CreateCenteredHeaderStyle() };
                var statusStyle = new Style(typeof(TextBlock));
                statusStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
                statusStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
                statusStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
                var onTrigger = new DataTrigger { Binding = new Binding("IsCurrentlyOn"), Value = true };
                onTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80))));
                statusStyle.Triggers.Add(onTrigger);
                var offTrigger = new DataTrigger { Binding = new Binding("IsCurrentlyOn"), Value = false };
                offTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(244, 67, 54))));
                statusStyle.Triggers.Add(offTrigger);
                statusColumn.ElementStyle = statusStyle;
                dataGrid.Columns.Add(statusColumn);

                // Add ON/OFF toggle columns
                AddMirrorToggleColumn(dataGrid, result.MirrorItems, "On", true, Color.FromRgb(76, 175, 80));
                AddMirrorToggleColumn(dataGrid, result.MirrorItems, "Off", false, Color.FromRgb(244, 67, 54));

                dataGrid.ItemsSource = result.MirrorItems;

                dataGrid.PreviewMouseDown += OptimizeDataGrid_PreviewMouseDown;
                dataGrid.PreviewMouseUp += OptimizeDataGrid_PreviewMouseUp;
                dataGrid.PreviewMouseMove += OptimizeDataGrid_PreviewMouseMove;

                Grid.SetRow(dataGrid, 2);
                tabGrid.Children.Add(dataGrid);
            }
            else
            {
                var noMirrorsText = new TextBlock { Text = "No mirrors found.", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetRow(noMirrorsText, 2);
                tabGrid.Children.Add(noMirrorsText);
            }

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Creates the Shadows tab for bulk optimization
        /// </summary>
        private TabItem CreateBulkShadowsTab(HairOptimizer.OptimizationResult result, Window parentDialog)
        {
            // Create header with tooltip icon
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            int lightCount = result.LightItems.Count;
            headerPanel.Children.Add(new TextBlock { Text = $"Shadows ({lightCount})", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "SHADOW OPTIMIZATION:\n\n" +
                " Adjusts shadow quality for lights\n" +
                " Can disable shadows completely\n" +
                " Affects InvisibleLight and SpotLight objects\n\n" +
                "Shadow Resolutions:\n" +
                "  2048 (VeryHigh) - Best quality, lowest FPS\n" +
                "  1024 (High) - Good balance\n" +
                "  512 (Medium) - Better performance\n" +
                "  Off - Best performance, no shadows\n\n" +
                "Performance: Lower resolution or disabled shadows improve FPS.";
            
            headerPanel.Children.Add(CreateTooltipInfoIcon(tooltipText));

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Create summary row with checkbox
            var summaryRow = new Grid();
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            int lightsWithShadows = result.LightItems.Count(l => l.CastShadows);
            var summaryText = new TextBlock { Text = $"Found {result.TotalLightItems} light(s) ({lightsWithShadows} casting shadows) across all packages", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetColumn(summaryText, 0);
            summaryRow.Children.Add(summaryText);

            var skipDisabledCheckbox = new System.Windows.Controls.CheckBox
            {
                Content = "Skip Disabled Lights in Bulk Selection",
                IsChecked = true,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "When enabled, column header buttons will not modify lights that currently have shadows disabled (Off).\nYou can still enable shadows manually for individual lights.",
                Style = CreateModernCheckboxStyle()
            };
            Grid.SetColumn(skipDisabledCheckbox, 1);
            summaryRow.Children.Add(skipDisabledCheckbox);

            Grid.SetRow(summaryRow, 0);
            tabGrid.Children.Add(summaryRow);

            if (result.LightItems.Count > 0)
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
                
                var alternateTrigger = new Trigger { Property = DataGridRow.AlternationIndexProperty, Value = 1 };
                alternateTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(35, 35, 35))));
                rowStyle.Triggers.Add(alternateTrigger);
                
                var rowHoverTrigger = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
                rowHoverTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
                rowStyle.Triggers.Add(rowHoverTrigger);
                
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

                // Package column
                var packageColumn = new DataGridTextColumn { Header = "Package", Binding = new Binding("PackageName"), Width = new DataGridLength(200), HeaderStyle = CreateCenteredHeaderStyle() };
                packageColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(100, 180, 255))), new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold) } };
                dataGrid.Columns.Add(packageColumn);

                // Light Name column
                var nameColumn = new DataGridTextColumn { Header = "Light ID", Binding = new Binding("LightName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), HeaderStyle = CreateCenteredHeaderStyle() };
                nameColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))) } };
                dataGrid.Columns.Add(nameColumn);

                // Light Type column
                var typeColumn = new DataGridTextColumn { Header = "Type", Binding = new Binding("LightType"), Width = new DataGridLength(120), HeaderStyle = CreateCenteredHeaderStyle() };
                typeColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 180, 180))) } };
                dataGrid.Columns.Add(typeColumn);

                // Current Shadow Status column
                var statusColumn = new DataGridTextColumn { Header = "Current", Binding = new Binding("CurrentShadowStatus"), Width = new DataGridLength(80), HeaderStyle = CreateCenteredHeaderStyle() };
                var statusStyle = new Style(typeof(TextBlock));
                statusStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
                statusStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
                statusStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
                statusStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 152, 0))));
                statusColumn.ElementStyle = statusStyle;
                dataGrid.Columns.Add(statusColumn);

                // Add shadow resolution toggle columns (highest to lowest, left to right)
                AddShadowToggleColumn(dataGrid, result.LightItems, "2048", "SetShadows2048", Color.FromRgb(255, 215, 0), skipDisabledCheckbox);
                AddShadowToggleColumn(dataGrid, result.LightItems, "1024", "SetShadows1024", Color.FromRgb(192, 192, 192), skipDisabledCheckbox);
                AddShadowToggleColumn(dataGrid, result.LightItems, "512", "SetShadows512", Color.FromRgb(205, 127, 50), skipDisabledCheckbox);
                AddShadowToggleColumn(dataGrid, result.LightItems, "Off", "SetShadowsOff", Color.FromRgb(244, 67, 54), skipDisabledCheckbox);

                dataGrid.ItemsSource = result.LightItems;

                dataGrid.PreviewMouseDown += OptimizeDataGrid_PreviewMouseDown;
                dataGrid.PreviewMouseUp += OptimizeDataGrid_PreviewMouseUp;
                dataGrid.PreviewMouseMove += OptimizeDataGrid_PreviewMouseMove;

                Grid.SetRow(dataGrid, 2);
                tabGrid.Children.Add(dataGrid);
            }
            else
            {
                var noLightsText = new TextBlock { Text = "No lights found.", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetRow(noLightsText, 2);
                tabGrid.Children.Add(noLightsText);
            }

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Creates the Dependencies tab for bulk optimization
        /// </summary>
        private TabItem CreateBulkDependenciesTab(DependencyScanner.DependencyScanResult result, bool initialForceLatestState, Window parentDialog)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            int dependencyCount = result.Dependencies.Count;
            headerPanel.Children.Add(new TextBlock { Text = $"Dependencies ({dependencyCount})", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "DEPENDENCY MANAGEMENT:\n\n" +
                " View all package dependencies from meta.json\n" +
                " Disable unwanted dependencies\n" +
                " Handles nested subdependencies\n\n" +
                "What happens:\n" +
                "  Disabled dependencies are removed from meta.json\n" +
                "  Uses text replacement to preserve JSON structure\n" +
                "  Changes applied when Optimize button is clicked\n\n" +
                "Note: Be careful removing dependencies that are actually used by the package.";
            
            headerPanel.Children.Add(CreateTooltipInfoIcon(tooltipText));

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            if (!result.Success)
            {
                var errorText = new TextBlock
                {
                    Text = $" Error: {result.ErrorMessage}",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetRow(errorText, 0);
                tabGrid.Children.Add(errorText);
                
                tab.Content = tabGrid;
                return tab;
            }

            // Summary row with warning message and checkboxes
            var summaryRow = new Grid();
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            int enabledCount = result.Dependencies.Count(d => d.IsEnabled);
            var summaryText = new TextBlock
            {
                Text = $"Found {result.Dependencies.Count} dependencies ({enabledCount} enabled) across all packages",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(summaryText, 0);
            summaryRow.Children.Add(summaryText);
            
            // Warning message
            var warningText = new TextBlock
            {
                Text = " Warning: Removing dependencies can break scenes!",
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(15, 0, 15, 0)
            };
            Grid.SetColumn(warningText, 1);
            summaryRow.Children.Add(warningText);
            
            // Create Disable Morph Pre-load checkbox
            var disableMorphPreloadCheckbox = new System.Windows.Controls.CheckBox
            {
                Content = "Disable Morph Pre-load",
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(15, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Set preloadMorphs to false in meta.json for non-morph packages.\n\nThis can improve VaM startup time by preventing unnecessary morph preloading.\n\nMorph-only packages will NOT be affected (they keep preloadMorphs=true).",
                IsChecked = _settingsManager?.Settings?.DisableMorphPreload ?? true
            };
            
            // Create Force .latest checkbox for this tab with modern styling
            var forceLatestCheckbox = new System.Windows.Controls.CheckBox
            {
                Content = "Force .latest",
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(15, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Convert all dependency versions to .latest when optimizing.\n\nExample:\n  MacGruber.PostMagic.3 †’ MacGruber.PostMagic.latest\n\nChanges will be tracked in the description for easy reversion.",
                IsChecked = initialForceLatestState
            };
            
            // Initialize ForceLatest property on all dependencies based on checkbox state
            foreach (var dep in result.Dependencies)
            {
                dep.ForceLatest = initialForceLatestState;
            }
            
            // Apply modern checkbox style
            var checkboxStyle = new Style(typeof(System.Windows.Controls.CheckBox));
            var checkboxTemplate = new ControlTemplate(typeof(System.Windows.Controls.CheckBox));
            
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            gridFactory.SetValue(Grid.BackgroundProperty, Brushes.Transparent);
            
            var col1 = new FrameworkElementFactory(typeof(System.Windows.Controls.ColumnDefinition));
            col1.SetValue(System.Windows.Controls.ColumnDefinition.WidthProperty, GridLength.Auto);
            var col2 = new FrameworkElementFactory(typeof(System.Windows.Controls.ColumnDefinition));
            col2.SetValue(System.Windows.Controls.ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            gridFactory.AppendChild(col1);
            gridFactory.AppendChild(col2);
            
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "CheckBoxBorder";
            borderFactory.SetValue(Grid.ColumnProperty, 0);
            borderFactory.SetValue(Border.WidthProperty, 18.0);
            borderFactory.SetValue(Border.HeightProperty, 18.0);
            borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 48)));
            borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(85, 85, 85)));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS));
            borderFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            
            var pathFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            pathFactory.Name = "CheckMark";
            pathFactory.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0,4 L 3,7 L 8,0"));
            pathFactory.SetValue(System.Windows.Shapes.Path.StrokeProperty, Brushes.White);
            pathFactory.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
            pathFactory.SetValue(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Collapsed);
            pathFactory.SetValue(System.Windows.Shapes.Path.StretchProperty, Stretch.Uniform);
            pathFactory.SetValue(System.Windows.Shapes.Path.MarginProperty, new Thickness(3));
            
            borderFactory.AppendChild(pathFactory);
            gridFactory.AppendChild(borderFactory);
            
            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(Grid.ColumnProperty, 1);
            contentFactory.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 0, 0));
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            gridFactory.AppendChild(contentFactory);
            
            checkboxTemplate.VisualTree = gridFactory;
            
            var checkedTrigger = new Trigger { Property = System.Windows.Controls.CheckBox.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Visible, "CheckMark"));
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
            checkboxTemplate.Triggers.Add(checkedTrigger);
            
            var checkboxHoverTrigger = new Trigger { Property = System.Windows.Controls.CheckBox.IsMouseOverProperty, Value = true };
            checkboxHoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(120, 120, 120)), "CheckBoxBorder"));
            checkboxTemplate.Triggers.Add(checkboxHoverTrigger);
            
            checkboxStyle.Setters.Add(new Setter(System.Windows.Controls.CheckBox.TemplateProperty, checkboxTemplate));
            forceLatestCheckbox.Style = checkboxStyle;
            disableMorphPreloadCheckbox.Style = checkboxStyle;
            
            // Save checkbox state when changed for disableMorphPreloadCheckbox
            disableMorphPreloadCheckbox.Checked += (s, e) =>
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.DisableMorphPreload = true;
                }
            };
            disableMorphPreloadCheckbox.Unchecked += (s, e) =>
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.DisableMorphPreload = false;
                }
            };
            
            // Save checkbox state when changed and update all dependencies
            forceLatestCheckbox.Checked += (s, e) =>
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.ForceLatestDependencies = true;
                }
                
                // Update ForceLatest property on all dependencies to show .latest indicator
                foreach (var dep in result.Dependencies)
                {
                    dep.ForceLatest = true;
                }
            };
            forceLatestCheckbox.Unchecked += (s, e) =>
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.ForceLatestDependencies = false;
                }
                
                // Update ForceLatest property on all dependencies to hide .latest indicator
                foreach (var dep in result.Dependencies)
                {
                    dep.ForceLatest = false;
                }
            };
            
            Grid.SetColumn(disableMorphPreloadCheckbox, 2);
            summaryRow.Children.Add(disableMorphPreloadCheckbox);
            
            Grid.SetColumn(forceLatestCheckbox, 3);
            summaryRow.Children.Add(forceLatestCheckbox);
            
            Grid.SetRow(summaryRow, 0);
            tabGrid.Children.Add(summaryRow);

            // Search box row with action buttons on the right
            var searchRow = new Grid();
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            var searchBox = new System.Windows.Controls.TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                FontSize = 13,
                Height = 32
            };
            
            var searchPlaceholder = new TextBlock
            {
                Text = " Search dependencies...",
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                FontSize = 13,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            // Clear button for search box
            var clearSearchButton = new Button
            {
                Content = "✓",
                Width = 30,
                Height = 30,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
                Visibility = Visibility.Collapsed,
                ToolTip = "Clear search (ESC)"
            };

            var searchGrid = new Grid();
            searchGrid.Children.Add(searchBox);
            searchGrid.Children.Add(searchPlaceholder);
            searchGrid.Children.Add(clearSearchButton);
            
            Grid.SetColumn(searchGrid, 0);
            searchRow.Children.Add(searchGrid);
            
            // Action buttons for selected items
            var actionButtonsContainer = new Border
            {
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(0)
            };
            
            var actionButtonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            
            var enableSelectedButton = new Button
            {
                Content = "Enable Selected",
                Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(20, 10, 20, 10),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(12, 0, 8, 0),
                Height = 36
            };
            
            var enableButtonStyle = new Style(typeof(Button));
            var enableButtonTemplate = new ControlTemplate(typeof(Button));
            var enableBorderFactory = new FrameworkElementFactory(typeof(Border));
            enableBorderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            enableBorderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            enableBorderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            enableBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS));
            enableBorderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            
            var enableContentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            enableContentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            enableContentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            enableBorderFactory.AppendChild(enableContentFactory);
            enableButtonTemplate.VisualTree = enableBorderFactory;
            
            var enableButtonHoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            enableButtonHoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(67, 160, 71))));
            enableButtonTemplate.Triggers.Add(enableButtonHoverTrigger);
            
            enableButtonStyle.Setters.Add(new Setter(Button.TemplateProperty, enableButtonTemplate));
            enableSelectedButton.Style = enableButtonStyle;
            
            var disableSelectedButton = new Button
            {
                Content = "Disable Selected",
                Background = new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(20, 10, 20, 10),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 0),
                Height = 36
            };
            
            var disableButtonStyle = new Style(typeof(Button));
            var disableButtonTemplate = new ControlTemplate(typeof(Button));
            var disableBorderFactory = new FrameworkElementFactory(typeof(Border));
            disableBorderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            disableBorderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            disableBorderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            disableBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS));
            disableBorderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            
            var disableContentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            disableContentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            disableContentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            disableBorderFactory.AppendChild(disableContentFactory);
            disableButtonTemplate.VisualTree = disableBorderFactory;
            
            var disableButtonHoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            disableButtonHoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(229, 57, 53))));
            disableButtonTemplate.Triggers.Add(disableButtonHoverTrigger);
            
            disableButtonStyle.Setters.Add(new Setter(Button.TemplateProperty, disableButtonTemplate));
            disableSelectedButton.Style = disableButtonStyle;
            
            actionButtonsPanel.Children.Add(enableSelectedButton);
            actionButtonsPanel.Children.Add(disableSelectedButton);
            actionButtonsContainer.Child = actionButtonsPanel;
            
            Grid.SetColumn(actionButtonsContainer, 1);
            searchRow.Children.Add(actionButtonsContainer);
            
            Grid.SetRow(searchRow, 2);
            tabGrid.Children.Add(searchRow);

            // Create DataGrid for dependencies
            if (result.Dependencies.Count > 0)
            {
                var dataGrid = new DataGrid
                {
                    AutoGenerateColumns = false,
                    IsReadOnly = true,
                    CanUserAddRows = false,
                    CanUserDeleteRows = false,
                    CanUserResizeRows = false,
                    CanUserSortColumns = true,
                    SelectionMode = DataGridSelectionMode.Extended,
                    GridLinesVisibility = DataGridGridLinesVisibility.None,
                    HeadersVisibility = DataGridHeadersVisibility.Column,
                    RowHeight = 32,
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    BorderThickness = new Thickness(0),
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220))
                };

                var columnHeaderStyle = new Style(typeof(DataGridColumnHeader));
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
                
                var alternateTrigger = new Trigger { Property = DataGridRow.AlternationIndexProperty, Value = 1 };
                alternateTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(35, 35, 35))));
                rowStyle.Triggers.Add(alternateTrigger);
                
                var rowHoverTrigger = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
                rowHoverTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
                rowStyle.Triggers.Add(rowHoverTrigger);
                
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

                // Package column
                var packageColumn = new DataGridTextColumn { Header = "Package", Binding = new Binding("PackageName"), Width = new DataGridLength(200), HeaderStyle = CreateCenteredHeaderStyle(), SortMemberPath = "PackageName" };
                packageColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(100, 180, 255))), new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold) } };
                dataGrid.Columns.Add(packageColumn);

                // Dependency name column with indentation for subdependencies
                var nameColumn = new DataGridTextColumn { Header = "Dependency Name", Binding = new Binding("DisplayName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), HeaderStyle = CreateCenteredHeaderStyle(), SortMemberPath = "Name" };
                nameColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))), new Setter(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Consolas")) } };
                dataGrid.Columns.Add(nameColumn);

                // License Type column
                var licenseColumn = new DataGridTextColumn { Header = "License", Binding = new Binding("LicenseType"), Width = new DataGridLength(100), HeaderStyle = CreateCenteredHeaderStyle(), SortMemberPath = "LicenseType" };
                licenseColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center), new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 180, 180))) } };
                dataGrid.Columns.Add(licenseColumn);

                // Add Enable/Disable toggle columns with colored dots
                AddDependencyToggleColumn(dataGrid, result.Dependencies, "Enable", true, Color.FromRgb(76, 175, 80), summaryText);
                AddDependencyToggleColumn(dataGrid, result.Dependencies, "Disable", false, Color.FromRgb(244, 67, 54), summaryText);

                var allDependencies = new System.Collections.ObjectModel.ObservableCollection<DependencyItemModel>(result.Dependencies);
                dataGrid.ItemsSource = allDependencies;

                dataGrid.PreviewMouseDown += OptimizeDataGrid_PreviewMouseDown;
                dataGrid.PreviewMouseUp += OptimizeDataGrid_PreviewMouseUp;
                dataGrid.PreviewMouseMove += OptimizeDataGrid_PreviewMouseMove;

                // Selection changed handler to show/hide action buttons
                dataGrid.SelectionChanged += (s, e) =>
                {
                    actionButtonsContainer.Visibility = dataGrid.SelectedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                };

                // Enable selected button handler
                enableSelectedButton.Click += (s, e) =>
                {
                    foreach (var item in dataGrid.SelectedItems.Cast<DependencyItemModel>().ToList())
                    {
                        if (!string.IsNullOrEmpty(item.ParentName))
                        {
                            var parent = result.Dependencies.FirstOrDefault(d => d.Name == item.ParentName);
                            if (parent != null && !parent.IsEnabled)
                            {
                                parent.IsEnabled = true;
                            }
                        }
                        
                        item.IsEnabled = true;
                        
                        foreach (var subDep in result.Dependencies.Where(d => d.ParentName == item.Name))
                        {
                            subDep.IsEnabled = true;
                        }
                    }
                    
                    summaryText.Text = $"Found {result.Dependencies.Count} dependencies ({result.Dependencies.Count(d => d.IsEnabled)} enabled) across all packages";
                    dataGrid.Items.Refresh();
                };

                // Disable selected button handler
                disableSelectedButton.Click += (s, e) =>
                {
                    foreach (var item in dataGrid.SelectedItems.Cast<DependencyItemModel>().ToList())
                    {
                        item.IsEnabled = false;
                        item.IsDisabledByUser = true;
                        
                        if (item.HasSubDependencies)
                        {
                            foreach (var subDep in result.Dependencies.Where(d => d.ParentName == item.Name))
                            {
                                subDep.IsEnabled = false;
                                subDep.IsDisabledByUser = true;
                            }
                        }
                    }
                    
                    summaryText.Text = $"Found {result.Dependencies.Count} dependencies ({result.Dependencies.Count(d => d.IsEnabled)} enabled) across all packages";
                    dataGrid.Items.Refresh();
                };

                // Search functionality
                searchBox.TextChanged += (s, e) =>
                {
                    string searchText = searchBox.Text.ToLowerInvariant();
                    searchPlaceholder.Visibility = string.IsNullOrEmpty(searchText) ? Visibility.Visible : Visibility.Collapsed;
                    clearSearchButton.Visibility = string.IsNullOrEmpty(searchText) ? Visibility.Collapsed : Visibility.Visible;
                    
                    if (string.IsNullOrEmpty(searchText))
                    {
                        dataGrid.ItemsSource = allDependencies;
                    }
                    else
                    {
                        var filtered = result.Dependencies.Where(d => 
                            d.Name.ToLowerInvariant().Contains(searchText) ||
                            (d.PackageName != null && d.PackageName.ToLowerInvariant().Contains(searchText))
                        ).ToList();
                        dataGrid.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<DependencyItemModel>(filtered);
                    }
                };

                // Clear search button handler
                clearSearchButton.Click += (s, e) =>
                {
                    searchBox.Text = "";
                    searchBox.Focus();
                };

                // ESC key to clear search
                searchBox.KeyDown += (s, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Escape)
                    {
                        searchBox.Text = "";
                        e.Handled = true;
                    }
                };

                Grid.SetRow(dataGrid, 4);
                tabGrid.Children.Add(dataGrid);
            }
            else
            {
                var noDepsText = new TextBlock { Text = "No dependencies found.", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetRow(noDepsText, 4);
                tabGrid.Children.Add(noDepsText);
            }

            tab.Content = tabGrid;
            return tab;
        }

        private TabItem CreateBulkSummaryTab(List<PackageItem> packages, TextureValidator.ValidationResult textureResult, HairOptimizer.OptimizationResult hairResult, DependencyScanner.DependencyScanResult dependencyResult, Window parentDialog)
        {
            var tab = new TabItem
            {
                Header = "Summary",
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var headerText = new TextBlock
            {
                Text = packages.Count == 1 ? "Optimization Summary" : $"Bulk Optimization Summary - {packages.Count} Packages",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(headerText, 0);
            tabGrid.Children.Add(headerText);

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var contentPanel = new StackPanel { Margin = new Thickness(5) };

            var selectedTextures = textureResult.Textures.Where(t => t.HasActualConversion).ToList();
            var selectedHairs = hairResult.HairItems.Where(h => h.HasConversionSelected).ToList();
            var selectedMirrors = hairResult.MirrorItems.Where(m => m.Disable).ToList();
            var selectedLights = hairResult.LightItems.Where(l => l.HasActualShadowConversion).ToList();

            var textureHeader = new TextBlock
            {
                Text = "📄 Texture Optimizations",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            contentPanel.Children.Add(textureHeader);

            if (selectedTextures.Count > 0)
            {
                var textureInfoPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 15) };
                
                var texturesByPackage = selectedTextures.GroupBy(t => t.PackageName).OrderBy(g => g.Key);
                var countText = new TextBlock
                {
                    Text = $"✓ {selectedTextures.Count} texture(s) across {texturesByPackage.Count()} package(s) will be converted",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                textureInfoPanel.Children.Add(countText);

                foreach (var packageGroup in texturesByPackage.Take(10))
                {
                    var packageText = new TextBlock
                    {
                        Text = $"  📦 {packageGroup.Key} ({packageGroup.Count()} textures)",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 4, 0, 2)
                    };
                    textureInfoPanel.Children.Add(packageText);

                    foreach (var texture in packageGroup.Take(5))
                    {
                        string targetRes = texture.ConvertTo8K ? "8K" : texture.ConvertTo4K ? "4K" : texture.ConvertTo2K ? "2K" : "?";
                        var itemText = new TextBlock
                        {
                            Text = $"    • {Path.GetFileName(texture.ReferencedPath)}: {texture.Resolution} → {targetRes}",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                            FontFamily = new FontFamily("Consolas"),
                            Margin = new Thickness(0, 1, 0, 1)
                        };
                        textureInfoPanel.Children.Add(itemText);
                    }

                    if (packageGroup.Count() > 5)
                    {
                        var moreText = new TextBlock
                        {
                            Text = $"    ... and {packageGroup.Count() - 5} more",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                            FontStyle = FontStyles.Italic,
                            Margin = new Thickness(0, 1, 0, 1)
                        };
                        textureInfoPanel.Children.Add(moreText);
                    }
                }

                if (texturesByPackage.Count() > 10)
                {
                    var morePackagesText = new TextBlock
                    {
                        Text = $"  ... and {texturesByPackage.Count() - 10} more packages",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(0, 4, 0, 2)
                    };
                    textureInfoPanel.Children.Add(morePackagesText);
                }

                contentPanel.Children.Add(textureInfoPanel);
            }
            else
            {
                var noTextureText = new TextBlock
                {
                    Text = "  No texture conversions selected",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(15, 0, 0, 15)
                };
                contentPanel.Children.Add(noTextureText);
            }

            var hairHeader = new TextBlock
            {
                Text = "💇 Hair Optimizations",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 10, 0, 10)
            };
            contentPanel.Children.Add(hairHeader);

            if (selectedHairs.Count > 0)
            {
                var hairInfoPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 15) };
                
                var hairsByPackage = selectedHairs.GroupBy(h => h.PackageName).OrderBy(g => g.Key);
                var countText = new TextBlock
                {
                    Text = $"✓ {selectedHairs.Count} hair item(s) across {hairsByPackage.Count()} package(s) will be modified",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                hairInfoPanel.Children.Add(countText);

                foreach (var packageGroup in hairsByPackage.Take(10))
                {
                    var packageText = new TextBlock
                    {
                        Text = $"  📦 {packageGroup.Key} ({packageGroup.Count()} items)",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 4, 0, 2)
                    };
                    hairInfoPanel.Children.Add(packageText);

                    foreach (var hair in packageGroup.Take(3))
                    {
                        int targetDensity = hair.ConvertTo32 ? 32 : hair.ConvertTo24 ? 24 : hair.ConvertTo16 ? 16 : hair.ConvertTo8 ? 8 : 0;
                        string status = hair.HasCurveDensity ? $"{hair.CurveDensity} †’ {targetDensity}" : $"Add †’ {targetDensity}";
                        var itemText = new TextBlock
                        {
                            Text = $"    • {hair.HairName}: density {status}",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                            FontFamily = new FontFamily("Consolas"),
                            Margin = new Thickness(0, 1, 0, 1)
                        };
                        hairInfoPanel.Children.Add(itemText);
                    }

                    if (packageGroup.Count() > 3)
                    {
                        var moreText = new TextBlock
                        {
                            Text = $"    ... and {packageGroup.Count() - 3} more",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                            FontStyle = FontStyles.Italic,
                            Margin = new Thickness(0, 1, 0, 1)
                        };
                        hairInfoPanel.Children.Add(moreText);
                    }
                }

                if (hairsByPackage.Count() > 10)
                {
                    var morePackagesText = new TextBlock
                    {
                        Text = $"  ... and {hairsByPackage.Count() - 10} more packages",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(0, 4, 0, 2)
                    };
                    hairInfoPanel.Children.Add(morePackagesText);
                }

                contentPanel.Children.Add(hairInfoPanel);
            }
            else
            {
                var noHairText = new TextBlock
                {
                    Text = "  No hair modifications selected",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(15, 0, 0, 15)
                };
                contentPanel.Children.Add(noHairText);
            }

            var mirrorHeader = new TextBlock
            {
                Text = "🪞 Mirror Optimizations",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 10, 0, 10)
            };
            contentPanel.Children.Add(mirrorHeader);

            if (selectedMirrors.Count > 0)
            {
                var mirrorInfoPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 15) };
                var mirrorsByPackage = selectedMirrors.GroupBy(m => m.PackageName).OrderBy(g => g.Key);
                var countText = new TextBlock
                {
                    Text = $"✓ {selectedMirrors.Count} mirror(s) across {mirrorsByPackage.Count()} package(s) will be disabled",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                mirrorInfoPanel.Children.Add(countText);
                contentPanel.Children.Add(mirrorInfoPanel);
            }
            else
            {
                var noMirrorText = new TextBlock
                {
                    Text = "  No mirror modifications selected",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(15, 0, 0, 15)
                };
                contentPanel.Children.Add(noMirrorText);
            }

            var lightHeader = new TextBlock
            {
                Text = "💡 Shadow Optimizations",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 10, 0, 10)
            };
            contentPanel.Children.Add(lightHeader);

            if (selectedLights.Count > 0)
            {
                var lightInfoPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 15) };
                var lightsByPackage = selectedLights.GroupBy(l => l.PackageName).OrderBy(g => g.Key);
                var countText = new TextBlock
                {
                    Text = $"✓ {selectedLights.Count} light(s) across {lightsByPackage.Count()} package(s) will be modified",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                lightInfoPanel.Children.Add(countText);
                contentPanel.Children.Add(lightInfoPanel);
            }
            else
            {
                var noLightText = new TextBlock
                {
                    Text = "  No shadow modifications selected",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(15, 0, 0, 15)
                };
                contentPanel.Children.Add(noLightText);
            }

            var dependencyHeader = new TextBlock
            {
                Text = "🔗 Dependency Modifications",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 10, 0, 10)
            };
            contentPanel.Children.Add(dependencyHeader);

            if (dependencyResult != null && dependencyResult.Success)
            {
                var disabledDeps = dependencyResult.Dependencies.Where(d => !d.IsEnabled).ToList();
                // Only count dependencies that will actually be changed (not already .latest)
                // Also check the global ForceLatestDependencies setting
                bool forceLatestGlobalSetting = _settingsManager?.Settings?.ForceLatestDependencies ?? false;
                var forceLatestDeps = dependencyResult.Dependencies.Where(d => d.IsEnabled && (d.ForceLatest || forceLatestGlobalSetting) && d.WillBeConvertedToLatest).ToList();
                
                if (disabledDeps.Count > 0 || forceLatestDeps.Count > 0)
                {
                    var depInfoPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 15) };
                    
                    if (disabledDeps.Count > 0)
                    {
                        var disabledDepsByPackage = disabledDeps.GroupBy(d => d.PackageName).OrderBy(g => g.Key);
                        var disabledCountText = new TextBlock
                        {
                            Text = $"✓ {disabledDeps.Count} dependency(ies) across {disabledDepsByPackage.Count()} package(s) will be removed",
                            FontSize = 14,
                            Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                            Margin = new Thickness(0, 0, 0, 10)
                        };
                        depInfoPanel.Children.Add(disabledCountText);
                    }
                    
                    if (forceLatestDeps.Count > 0)
                    {
                        var forceLatestDepsByPackage = forceLatestDeps.GroupBy(d => d.PackageName).OrderBy(g => g.Key);
                        var forceLatestCountText = new TextBlock
                        {
                            Text = $"✓ {forceLatestDeps.Count} dependency(ies) across {forceLatestDepsByPackage.Count()} package(s) will be set to .latest",
                            FontSize = 14,
                            Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                            Margin = new Thickness(0, 0, 0, 10)
                        };
                        depInfoPanel.Children.Add(forceLatestCountText);
                    }
                    
                    contentPanel.Children.Add(depInfoPanel);
                }
                else
                {
                    var noDepsText = new TextBlock
                    {
                        Text = "  No dependency modifications selected",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(15, 0, 0, 15)
                    };
                    contentPanel.Children.Add(noDepsText);
                }
            }
            else
            {
                var noDepsText = new TextBlock
                {
                    Text = "  No dependencies found",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(15, 0, 0, 15)
                };
                contentPanel.Children.Add(noDepsText);
            }

            var notesHeader = new TextBlock
            {
                Text = "Important Notes",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                Margin = new Thickness(0, 20, 0, 10)
            };
            contentPanel.Children.Add(notesHeader);

            var notesPanel = new StackPanel { Margin = new Thickness(15, 0, 0, 0) };
            
            notesPanel.Children.Add(new TextBlock
            {
                Text = "• Original packages will be backed up to ArchivedPackages folder",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            });

            notesPanel.Children.Add(new TextBlock
            {
                Text = "• Optimized packages will replace originals in AddonPackages",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            });

            notesPanel.Children.Add(new TextBlock
            {
                Text = "• Package descriptions will be updated with optimization details",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            });

            notesPanel.Children.Add(new TextBlock
            {
                Text = packages.Count == 1 
                    ? "• This operation may take several minutes depending on package size"
                    : $"• This operation may take considerable time for {packages.Count} packages",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            });

            contentPanel.Children.Add(notesPanel);

            scrollViewer.Content = contentPanel;
            Grid.SetRow(scrollViewer, 2);
            tabGrid.Children.Add(scrollViewer);

            tab.Content = tabGrid;
            return tab;
        }

        private TabItem CreateBulkMiscTab(List<PackageItem> packages, Window parentDialog)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = "Misc", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "MISCELLANEOUS OPTIONS:\n\n" +
                "• Minify JSON files: Removes whitespace and formatting from .json, .vaj, .vam, .vap files\n" +
                "  This reduces file size without affecting functionality\n" +
                "  Useful for reducing overall package size";
            
            headerPanel.Children.Add(CreateTooltipInfoIcon(tooltipText));

            var tab = new TabItem { Header = headerPanel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var headerText = new TextBlock
            {
                Text = "Miscellaneous Options",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(headerText, 0);
            tabGrid.Children.Add(headerText);

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var contentPanel = new StackPanel { Margin = new Thickness(5) };

            // Minify JSON checkbox
            var jsonMinifyPanel = new StackPanel { Margin = new Thickness(15, 10, 15, 20), Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            
            // Create modern checkbox style matching the second image
            var checkBoxStyle = new Style(typeof(CheckBox));
            var checkboxControlTemplate = new ControlTemplate(typeof(CheckBox));
            
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            gridFactory.SetValue(Grid.BackgroundProperty, Brushes.Transparent);
            
            var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            var col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col2.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            gridFactory.AppendChild(col1);
            gridFactory.AppendChild(col2);
            
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "CheckBoxBorder";
            borderFactory.SetValue(Grid.ColumnProperty, 0);
            borderFactory.SetValue(Border.WidthProperty, 18.0);
            borderFactory.SetValue(Border.HeightProperty, 18.0);
            borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 48)));
            borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(85, 85, 85)));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            borderFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.SetValue(Border.MarginProperty, new Thickness(0, 0, 8, 0));
            
            var pathFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            pathFactory.Name = "CheckMark";
            pathFactory.SetValue(System.Windows.Shapes.Path.DataProperty, System.Windows.Media.Geometry.Parse("M 0,4 L 3,7 L 8,0"));
            pathFactory.SetValue(System.Windows.Shapes.Path.StrokeProperty, Brushes.White);
            pathFactory.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
            pathFactory.SetValue(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Collapsed);
            pathFactory.SetValue(System.Windows.Shapes.Path.StretchProperty, System.Windows.Media.Stretch.Uniform);
            pathFactory.SetValue(System.Windows.Shapes.Path.MarginProperty, new Thickness(3));
            
            borderFactory.AppendChild(pathFactory);
            gridFactory.AppendChild(borderFactory);
            
            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(Grid.ColumnProperty, 1);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            gridFactory.AppendChild(contentFactory);
            
            checkboxControlTemplate.VisualTree = gridFactory;
            
            var checkedTrigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Visible, "CheckMark"));
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
            checkboxControlTemplate.Triggers.Add(checkedTrigger);
            
            var hoverTrigger = new Trigger { Property = CheckBox.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
            checkboxControlTemplate.Triggers.Add(hoverTrigger);
            
            checkBoxStyle.Setters.Add(new Setter(CheckBox.TemplateProperty, checkboxControlTemplate));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.CursorProperty, System.Windows.Input.Cursors.Hand));
            
            var jsonMinifyCheckBox = new CheckBox
            {
                Content = "Minify JSON files (.json, .vaj, .vam, .vap)",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = _settingsManager?.Settings?.MinifyJsonFiles ?? true,
                Name = "MinifyJsonCheckBox",
                Style = checkBoxStyle
            };
            
            // Save minify JSON setting when changed
            jsonMinifyCheckBox.Checked += (s, e) =>
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.MinifyJsonFiles = true;
                }
            };
            jsonMinifyCheckBox.Unchecked += (s, e) =>
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.MinifyJsonFiles = false;
                }
            };
            
            jsonMinifyPanel.Children.Add(jsonMinifyCheckBox);

            var jsonMinifyDescription = new TextBlock
            {
                Text = "Removes whitespace and formatting from JSON files to reduce file size. This does not affect functionality.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(25, 0, 0, 0)
            };
            jsonMinifyPanel.Children.Add(jsonMinifyDescription);

            contentPanel.Children.Add(jsonMinifyPanel);

            var infoPanel = new StackPanel { Margin = new Thickness(15, 20, 15, 0) };
            
            var infoHeader = new TextBlock
            {
                Text = "ℹ️ Information",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            infoPanel.Children.Add(infoHeader);

            var infoText = new TextBlock
            {
                Text = packages.Count == 1 
                    ? $"Options will be applied to: {packages[0].Name}"
                    : $"Options will be applied to {packages.Count} selected packages",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 0, 0, 0)
            };
            infoPanel.Children.Add(infoText);

            contentPanel.Children.Add(infoPanel);

            scrollViewer.Content = contentPanel;
            Grid.SetRow(scrollViewer, 2);
            tabGrid.Children.Add(scrollViewer);

            tab.Content = tabGrid;
            // Store checkbox reference in tab's Tag for later retrieval
            tab.Tag = jsonMinifyCheckBox;
            return tab;
        }
    }
}

