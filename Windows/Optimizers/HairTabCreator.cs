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
        private TabItem CreateHairTab(string packageName, HairOptimizer.OptimizationResult result, Window parentDialog)
        {
            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            int hairCount = result.HairItems.Count;
            headerPanel.Children.Add(new TextBlock { Text = $"Hair ({hairCount})", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "HAIR DENSITY OPTIMIZATION:\n\n" +
                "✓ Only reduces density (never increases)\n" +
                "✓ Hair at or below target density remains unchanged\n" +
                "✓ Modifies curveDensity and hairMultiplier values\n\n" +
                "Example: If you select density 16:\n" +
                "  • Hair with density 32 †’ Reduced to 16\n" +
                "  • Hair with density 16 †’ Kept as-is\n" +
                "  • Hair with density 8 †’ Kept as-is\n\n" +
                "Performance: Lower density = better FPS in VaM.";
            
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

            // Create summary row with checkbox
            var summaryRow = new Grid();
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            int withDensity = result.HairItems.Count(h => h.CurveDensity > 0);
            var summaryText = new TextBlock
            {
                Text = $"Found {result.TotalHairItems} hair item(s) ({withDensity} with curveDensity settings)",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetColumn(summaryText, 0);
            summaryRow.Children.Add(summaryText);

            // Add checkbox to skip hair without density in bulk operations
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
                    Header = "Hair Name",
                    Binding = new Binding("HairName"),
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

                var densityColumn = new DataGridTextColumn
                {
                    Header = "Curve Density",
                    Binding = new Binding("CurveDensityFormatted"),
                    Width = new DataGridLength(120),
                    HeaderStyle = CreateCenteredHeaderStyle()
                };
                densityColumn.ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                        new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 152, 0))),
                        new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                        new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold)
                    }
                };
                dataGrid.Columns.Add(densityColumn);

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
                var noHairText = new TextBlock
                {
                    Text = "No hair items found in this package.\n\nThis package may not contain scene files with hair.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                Grid.SetRow(noHairText, 2);
                tabGrid.Children.Add(noHairText);
            }

            tab.Content = tabGrid;
            return tab;
        }

        private void AddHairDensityCheckboxColumn(DataGrid dataGrid, List<HairOptimizer.HairInfo> hairItems, string header, string bindingPath, string isEnabledPath, Color headerColor, int targetDensity, System.Windows.Controls.CheckBox skipNoDensityCheckbox = null)
        {
            var columnWidth = targetDensity == -1 ? 90 : 75; // 90px for Keep, 75px for density columns
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
                ToolTip = targetDensity == -1 ? "Keep all hair unchanged" : $"Reduce all hair to density {targetDensity}\n(Only reduces density, never increases)\n(Hair at or below {targetDensity} remains unchanged)"
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
                bool skipNoDensity = skipNoDensityCheckbox?.IsChecked == true;
                
                foreach (var hair in hairItems)
                {
                    // Skip hair without density if checkbox is checked
                    if (skipNoDensity && hair.CurveDensity <= 0)
                        continue;
                    
                    if (targetDensity == -1)
                        hair.KeepUnchanged = true;
                    else if (targetDensity == 32)
                        hair.ConvertTo32 = true;
                    else if (targetDensity == 24)
                        hair.ConvertTo24 = true;
                    else if (targetDensity == 16)
                        hair.ConvertTo16 = true;
                    else if (targetDensity == 8)
                        hair.ConvertTo8 = true;
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
            // Only bind IsEnabled if isEnabledPath is provided (null for Keep column which is always enabled)
            if (!string.IsNullOrEmpty(isEnabledPath))
            {
                checkBoxFactory.SetBinding(CheckBox.IsEnabledProperty, new Binding(isEnabledPath));
            }

            var checkBoxStyle = new Style(typeof(CheckBox));

            var hairCheckboxTemplate = new ControlTemplate(typeof(CheckBox));
            var hairBorderFactory = new FrameworkElementFactory(typeof(Border));
            hairBorderFactory.SetValue(Border.WidthProperty, 20.0);
            hairBorderFactory.SetValue(Border.HeightProperty, 20.0);
            hairBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            hairBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            hairBorderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(headerColor));
            hairBorderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(30, 30, 30)));

            var hairInnerDotFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            hairInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.WidthProperty, 12.0);
            hairInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.HeightProperty, 12.0);
            hairInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.FillProperty, new SolidColorBrush(headerColor));
            hairInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            hairInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.VerticalAlignmentProperty, VerticalAlignment.Center);
            hairInnerDotFactory.SetValue(System.Windows.Shapes.Ellipse.VisibilityProperty, Visibility.Collapsed);
            hairInnerDotFactory.Name = "InnerDot";

            hairBorderFactory.AppendChild(hairInnerDotFactory);
            hairCheckboxTemplate.VisualTree = hairBorderFactory;

            var hairCheckedTrigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            hairCheckedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Ellipse.VisibilityProperty, Visibility.Visible, "InnerDot"));
            hairCheckboxTemplate.Triggers.Add(hairCheckedTrigger);

            checkBoxStyle.Setters.Add(new Setter(CheckBox.TemplateProperty, hairCheckboxTemplate));
            var disabledTrigger = new Trigger { Property = CheckBox.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(CheckBox.OpacityProperty, 0.25));
            hairCheckboxTemplate.Triggers.Add(disabledTrigger);

            checkBoxStyle.Setters.Add(new Setter(CheckBox.CursorProperty, System.Windows.Input.Cursors.Hand));

            checkBoxFactory.SetValue(CheckBox.StyleProperty, checkBoxStyle);

            cellTemplate.VisualTree = checkBoxFactory;
            column.CellTemplate = cellTemplate;

            dataGrid.Columns.Add(column);
        }
    }
}

