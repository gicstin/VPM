using System;
using System.Collections.Generic;
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
        private TabItem CreateMirrorsTab(string packageName, HairOptimizer.OptimizationResult result, Window parentDialog)
        {
            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            int mirrorCount = result.MirrorItems.Count;
            headerPanel.Children.Add(new TextBlock { Text = $"Mirrors ({mirrorCount})", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "MIRROR OPTIMIZATION:\n\n" +
                "✓ Disables ReflectiveSlate objects in scenes\n" +
                "✓ Mirrors are expensive for performance\n" +
                "✓ Can toggle individual mirrors on/off\n\n" +
                "What happens:\n" +
                "  • Sets the 'on' property to 'false' in scene JSON\n" +
                "  • Mirrors remain in scene but are disabled\n" +
                "  • Can be re-enabled manually in VaM if needed\n\n" +
                "Performance: Disabling mirrors significantly improves FPS.";
            
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

            int mirrorsOn = result.MirrorItems.Count(m => m.IsCurrentlyOn);
            var summaryText = new TextBlock
            {
                Text = $"Found {result.TotalMirrorItems} mirror(s) ({mirrorsOn} currently ON)",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 0, 0, 10)
            };
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

                var nameColumn = new DataGridTextColumn
                {
                    Header = "Mirror ID",
                    Binding = new Binding("MirrorName"),
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                    HeaderStyle = CreateCenteredHeaderStyle()
                };
                nameColumn.ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                        new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)),
                        new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220)))
                    }
                };
                dataGrid.Columns.Add(nameColumn);

                var statusColumn = new DataGridTextColumn
                {
                    Header = "Current",
                    Binding = new Binding("CurrentStatus"),
                    Width = new DataGridLength(80),
                    HeaderStyle = CreateCenteredHeaderStyle()
                };
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
                var noMirrorsText = new TextBlock
                {
                    Text = "No mirrors found in this package.\n\nThis package may not contain scene files with ReflectiveSlate objects.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                Grid.SetRow(noMirrorsText, 2);
                tabGrid.Children.Add(noMirrorsText);
            }

            tab.Content = tabGrid;
            return tab;
        }

        private void AddMirrorToggleColumn(DataGrid dataGrid, List<HairOptimizer.MirrorInfo> mirrorItems, string header, bool targetState, Color headerColor)
        {
            var column = new DataGridTemplateColumn
            {
                Width = new DataGridLength(75),
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
                Padding = new Thickness(14, 6, 14, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinWidth = 50,
                ToolTip = targetState ? "Keep all currently ON mirrors enabled" : "Disable all currently ON mirrors"
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
                foreach (var mirror in mirrorItems)
                {
                    if (targetState)
                        mirror.Enable = true;  // Set all to Enable
                    else
                        mirror.Disable = true; // Set all to Disable
                }
            };

            column.Header = headerButton;

            var cellTemplate = new DataTemplate();
            var checkBoxFactory = new FrameworkElementFactory(typeof(CheckBox));
            checkBoxFactory.SetValue(CheckBox.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBoxFactory.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
            
            // Bind to Enable for "On" column, Disable for "Off" column
            var binding = new Binding(targetState ? "Enable" : "Disable")
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
            checkBoxFactory.SetBinding(CheckBox.IsCheckedProperty, binding);

            var checkBoxStyle = new Style(typeof(CheckBox));
            var mirrorCheckboxTemplate = new ControlTemplate(typeof(CheckBox));
            
            var mirrorBorderFactory = new FrameworkElementFactory(typeof(Border));
            mirrorBorderFactory.SetValue(Border.WidthProperty, 20.0);
            mirrorBorderFactory.SetValue(Border.HeightProperty, 20.0);
            mirrorBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            mirrorBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            mirrorBorderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(headerColor));
            mirrorBorderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Colors.Transparent));
            
            var mirrorInnerDotFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            mirrorInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.WidthProperty, 12.0);
            mirrorInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.HeightProperty, 12.0);
            mirrorInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.FillProperty, new SolidColorBrush(headerColor));
            mirrorInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.VisibilityProperty, Visibility.Collapsed);
            mirrorInnerDotFactory.Name = "InnerDot";
            
            mirrorBorderFactory.AppendChild(mirrorInnerDotFactory);
            mirrorCheckboxTemplate.VisualTree = mirrorBorderFactory;
            
            var mirrorCheckedTrigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            mirrorCheckedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Ellipse.VisibilityProperty, Visibility.Visible, "InnerDot"));
            mirrorCheckboxTemplate.Triggers.Add(mirrorCheckedTrigger);
            
            checkBoxStyle.Setters.Add(new Setter(CheckBox.TemplateProperty, mirrorCheckboxTemplate));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.CursorProperty, System.Windows.Input.Cursors.Hand));
            
            checkBoxFactory.SetValue(CheckBox.StyleProperty, checkBoxStyle);
            
            cellTemplate.VisualTree = checkBoxFactory;
            column.CellTemplate = cellTemplate;

            dataGrid.Columns.Add(column);
        }

        private TabItem CreateShadowsTab(string packageName, HairOptimizer.OptimizationResult result, Window parentDialog)
        {
            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            int shadowCount = result.LightItems.Count;
            headerPanel.Children.Add(new TextBlock { Text = $"Shadows ({shadowCount})", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "SHADOW OPTIMIZATION:\n\n" +
                "✓ Adjusts shadow quality for lights\n" +
                "✓ Can disable shadows completely\n" +
                "✓ Affects InvisibleLight and SpotLight objects\n\n" +
                "Shadow Resolutions:\n" +
                "  • 2048 (VeryHigh) - Best quality, lowest FPS\n" +
                "  • 1024 (High) - Good balance\n" +
                "  • 512 (Medium) - Better performance\n" +
                "  • Off - Best performance, no shadows\n\n" +
                "Performance: Lower resolution or disabled shadows improve FPS.";
            
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

            // Create summary row with checkbox
            var summaryRow = new Grid();
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            int lightsWithShadows = result.LightItems.Count(l => l.CastShadows);
            var summaryText = new TextBlock
            {
                Text = $"Found {result.TotalLightItems} light(s) ({lightsWithShadows} casting shadows)",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetColumn(summaryText, 0);
            summaryRow.Children.Add(summaryText);

            // Add checkbox to skip disabled lights in bulk operations
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

                var nameColumn = new DataGridTextColumn
                {
                    Header = "Light ID",
                    Binding = new Binding("LightName"),
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                    HeaderStyle = CreateCenteredHeaderStyle()
                };
                nameColumn.ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                        new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)),
                        new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220)))
                    }
                };
                dataGrid.Columns.Add(nameColumn);

                var typeColumn = new DataGridTextColumn
                {
                    Header = "Type",
                    Binding = new Binding("LightType"),
                    Width = new DataGridLength(120),
                    HeaderStyle = CreateCenteredHeaderStyle()
                };
                typeColumn.ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                        new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                        new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 180, 180)))
                    }
                };
                dataGrid.Columns.Add(typeColumn);

                var statusColumn = new DataGridTextColumn
                {
                    Header = "Current",
                    Binding = new Binding("CurrentShadowStatus"),
                    Width = new DataGridLength(80),
                    HeaderStyle = CreateCenteredHeaderStyle()
                };
                var statusStyle = new Style(typeof(TextBlock));
                statusStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
                statusStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
                statusStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
                statusStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 152, 0))));
                statusColumn.ElementStyle = statusStyle;
                dataGrid.Columns.Add(statusColumn);

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
                var noLightsText = new TextBlock
                {
                    Text = "No lights found in this package.\n\nThis package may not contain scene files with InvisibleLight or SpotLight objects.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                Grid.SetRow(noLightsText, 2);
                tabGrid.Children.Add(noLightsText);
            }

            tab.Content = tabGrid;
            return tab;
        }

        private void AddShadowToggleColumn(DataGrid dataGrid, List<HairOptimizer.LightInfo> lightItems, string header, string bindingPath, Color headerColor, System.Windows.Controls.CheckBox skipDisabledCheckbox = null)
        {
            var columnWidth = 85; // 85px for shadow columns
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
                Padding = new Thickness(14, 6, 14, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinWidth = 50,
                ToolTip = header == "Off" ? "Disable shadows on all lights" : $"Set all lights to shadow quality: {header}"
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
                bool skipDisabled = skipDisabledCheckbox?.IsChecked == true;
                
                foreach (var light in lightItems)
                {
                    // Skip lights with shadows currently disabled if checkbox is checked
                    if (skipDisabled && !light.CastShadows)
                        continue;
                    
                    if (bindingPath == "SetShadowsOff")
                        light.SetShadowsOff = true;
                    else if (bindingPath == "SetShadows512")
                        light.SetShadows512 = true;
                    else if (bindingPath == "SetShadows1024")
                        light.SetShadows1024 = true;
                    else if (bindingPath == "SetShadows2048")
                        light.SetShadows2048 = true;
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

            var checkBoxStyle = new Style(typeof(CheckBox));
            var shadowCheckboxTemplate = new ControlTemplate(typeof(CheckBox));

            var shadowBorderFactory = new FrameworkElementFactory(typeof(Border));
            shadowBorderFactory.SetValue(Border.WidthProperty, 20.0);
            shadowBorderFactory.SetValue(Border.HeightProperty, 20.0);
            shadowBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            shadowBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            shadowBorderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(headerColor));
            shadowBorderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Colors.Transparent));

            var shadowInnerDotFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            shadowInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.WidthProperty, 12.0);
            shadowInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.HeightProperty, 12.0);
            shadowInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.FillProperty, new SolidColorBrush(headerColor));
            shadowInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.VisibilityProperty, Visibility.Collapsed);
            shadowInnerDotFactory.Name = "InnerDot";

            shadowBorderFactory.AppendChild(shadowInnerDotFactory);
            shadowCheckboxTemplate.VisualTree = shadowBorderFactory;

            var shadowCheckedTrigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            shadowCheckedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Ellipse.VisibilityProperty, Visibility.Visible, "InnerDot"));
            shadowCheckboxTemplate.Triggers.Add(shadowCheckedTrigger);

            checkBoxStyle.Setters.Add(new Setter(CheckBox.TemplateProperty, shadowCheckboxTemplate));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.CursorProperty, System.Windows.Input.Cursors.Hand));

            checkBoxFactory.SetValue(CheckBox.StyleProperty, checkBoxStyle);

            cellTemplate.VisualTree = checkBoxFactory;
            column.CellTemplate = cellTemplate;
            dataGrid.Columns.Add(column);
        }

        private class InverseBooleanConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is bool boolValue)
                    return !boolValue;
                return false;
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is bool boolValue)
                    return !boolValue;
                return false;
            }
        }
    }
}

