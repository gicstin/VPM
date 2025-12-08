using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    public partial class MainWindow
    {
        private TabItem CreateDependenciesTab(string packageName, DependencyScanner.DependencyScanResult result, bool initialForceLatestState, Window parentDialog)
        {
            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            int dependencyCount = result.Dependencies.Count;
            headerPanel.Children.Add(new TextBlock { Text = $"Dependencies ({dependencyCount})", VerticalAlignment = VerticalAlignment.Center });
            
            string tooltipText = "DEPENDENCY MANAGEMENT:\n\n" +
                "✓ View all package dependencies from meta.json\n" +
                "✓ Disable unwanted dependencies\n" +
                "✓ Handles nested subdependencies\n\n" +
                "What happens:\n" +
                "  • Disabled dependencies are removed from meta.json\n" +
                "  • Uses text replacement to preserve JSON structure\n" +
                "  • Changes applied when Optimize button is clicked\n\n" +
                "Note: Be careful removing dependencies that are actually used by the package.";
            
            headerPanel.Children.Add(CreateTooltipInfoIcon(tooltipText));

            var tab = new TabItem
            {
                Header = headerPanel,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var tabGrid = new Grid { Margin = new Thickness(10) };
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Summary + Force .latest checkbox
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Search box + action buttons
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Table

            if (!result.Success)
            {
                var errorText = new TextBlock
                {
                    Text = $"⚠️ Error: {result.ErrorMessage}",
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
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Summary text
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Warning message
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Disable Morph Pre-load checkbox
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Force .latest checkbox
            
            int enabledCount = result.Dependencies.Count(d => d.IsEnabled);
            var summaryText = new TextBlock
            {
                Text = $"Found {result.Dependencies.Count} dependencies ({enabledCount} enabled)",
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
                Text = "⚠️ Warning: Removing dependencies can break scenes!",
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
                ToolTip = "Disable morph preloading for non-morph packages.\n\nThis sets 'preloadMorphs' to 'false' in meta.json for packages\nthat are NOT morph-only assets.\n\nMorph-only packages (including morph packs) will be skipped.",
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
                ToolTip = "Convert all dependency versions to .latest when optimizing.\n\nExample:\n  MacGruber.PostMagic.3 → MacGruber.PostMagic.latest\n\nChanges will be tracked in the description for easy reversion.",
                IsChecked = initialForceLatestState
            };
            
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
            
            // Save checkbox state when changed
            forceLatestCheckbox.Checked += (s, e) =>
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.ForceLatestDependencies = true;
                }
            };
            forceLatestCheckbox.Unchecked += (s, e) =>
            {
                if (_settingsManager?.Settings != null)
                {
                    _settingsManager.Settings.ForceLatestDependencies = false;
                }
            };
            
            // Apply the same checkbox style to disableMorphPreloadCheckbox
            disableMorphPreloadCheckbox.Style = checkboxStyle;
            
            // Save checkbox state when changed
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
                Text = "📝 Search dependencies...",
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
            
            // Action buttons for selected items - wrapped in Border for proper layout
            var actionButtonsContainer = new Border
            {
                Visibility = Visibility.Collapsed, // Initially hidden
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
            
            // Add rounded corners to enable button
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
            
            // Add rounded corners to disable button
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
                    IsReadOnly = true, // Make read-only to prevent edit errors on double-click
                    CanUserAddRows = false,
                    CanUserDeleteRows = false,
                    CanUserResizeRows = false,
                    CanUserSortColumns = true, // Enable sorting with custom comparer
                    SelectionMode = DataGridSelectionMode.Extended, // Allow multi-selection with Ctrl and drag
                    GridLinesVisibility = DataGridGridLinesVisibility.None,
                    HeadersVisibility = DataGridHeadersVisibility.Column,
                    RowHeight = 32,
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    BorderThickness = new Thickness(0),
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220))
                };

                // Dark theme styles
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
                
                // Subdependency background - subtle blue tint
                var subDepRowTrigger1 = new DataTrigger { Binding = new Binding("Depth"), Value = 1 };
                subDepRowTrigger1.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(32, 38, 48))));
                rowStyle.Triggers.Add(subDepRowTrigger1);
                
                var subDepRowTrigger2 = new DataTrigger { Binding = new Binding("Depth"), Value = 2 };
                subDepRowTrigger2.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(32, 38, 48))));
                rowStyle.Triggers.Add(subDepRowTrigger2);
                
                var subDepRowTrigger3 = new DataTrigger { Binding = new Binding("Depth"), Value = 3 };
                subDepRowTrigger3.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(32, 38, 48))));
                rowStyle.Triggers.Add(subDepRowTrigger3);
                
                // Alternating row color
                var alternateTrigger = new Trigger { Property = DataGridRow.AlternationIndexProperty, Value = 1 };
                alternateTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(35, 35, 35))));
                rowStyle.Triggers.Add(alternateTrigger);
                
                // Hover effect
                var rowHoverTrigger = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
                rowHoverTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
                rowStyle.Triggers.Add(rowHoverTrigger);
                
                // Selection effect - use muted professional color
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
                
                // Cell hover/selection styling
                var cellSelectedTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
                cellSelectedTrigger.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
                cellSelectedTrigger.Setters.Add(new Setter(DataGridCell.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))));
                cellStyle.Triggers.Add(cellSelectedTrigger);
                
                dataGrid.CellStyle = cellStyle;

                // Dependency Name column
                var nameColumn = new DataGridTextColumn
                {
                    Header = "Dependency Name",
                    Binding = new Binding("IndentedName"),
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                    HeaderStyle = CreateCenteredHeaderStyle(),
                    SortMemberPath = "Name"
                };
                nameColumn.ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                        new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)),
                        new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(220, 220, 220))),
                        new Setter(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Consolas"))
                    }
                };
                
                // Add trigger to make subdependencies visually distinct
                var subDepTrigger = new DataTrigger { Binding = new Binding("Depth"), Value = 1 };
                subDepTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 180, 220))));
                subDepTrigger.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
                nameColumn.ElementStyle.Triggers.Add(subDepTrigger);
                
                var subDepTrigger2 = new DataTrigger { Binding = new Binding("Depth"), Value = 2 };
                subDepTrigger2.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 180, 220))));
                subDepTrigger2.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
                nameColumn.ElementStyle.Triggers.Add(subDepTrigger2);
                
                var subDepTrigger3 = new DataTrigger { Binding = new Binding("Depth"), Value = 3 };
                subDepTrigger3.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(150, 180, 220))));
                subDepTrigger3.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
                nameColumn.ElementStyle.Triggers.Add(subDepTrigger3);
                
                dataGrid.Columns.Add(nameColumn);

                // License Type column
                var licenseColumn = new DataGridTextColumn
                {
                    Header = "License",
                    Binding = new Binding("LicenseType"),
                    Width = new DataGridLength(100),
                    HeaderStyle = CreateCenteredHeaderStyle(),
                    SortMemberPath = "LicenseType"
                };
                licenseColumn.ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                        new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center),
                        new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 180, 180)))
                    }
                };
                dataGrid.Columns.Add(licenseColumn);

                // Add Enable/Disable toggle columns with colored dots
                AddDependencyToggleColumn(dataGrid, result.Dependencies, "Enable", true, Color.FromRgb(76, 175, 80), summaryText);
                AddDependencyToggleColumn(dataGrid, result.Dependencies, "Disable", false, Color.FromRgb(244, 67, 54), summaryText);

                // Store original list for filtering
                var allDependencies = new System.Collections.ObjectModel.ObservableCollection<DependencyItemModel>(result.Dependencies);
                dataGrid.ItemsSource = allDependencies;

                dataGrid.PreviewMouseDown += OptimizeDataGrid_PreviewMouseDown;
                dataGrid.PreviewMouseUp += OptimizeDataGrid_PreviewMouseUp;
                dataGrid.PreviewMouseMove += OptimizeDataGrid_PreviewMouseMove;

                // Custom sorting to maintain parent-child hierarchy
                dataGrid.Sorting += (s, e) =>
                {
                    e.Handled = true;
                    var column = e.Column;
                    var direction = (column.SortDirection != System.ComponentModel.ListSortDirection.Ascending)
                        ? System.ComponentModel.ListSortDirection.Ascending
                        : System.ComponentModel.ListSortDirection.Descending;
                    
                    column.SortDirection = direction;
                    
                    var currentSource = dataGrid.ItemsSource as System.Collections.ObjectModel.ObservableCollection<DependencyItemModel>;
                    if (currentSource == null) return;
                    
                    var items = currentSource.ToList();
                    List<DependencyItemModel> sorted;
                    
                    if (column.SortMemberPath == "Name")
                    {
                        // Hierarchical sort: group by parent, sort parents, then sort children under each parent
                        var parents = items.Where(d => d.Depth == 0).ToList();
                        var children = items.Where(d => d.Depth > 0).ToList();
                        
                        if (direction == System.ComponentModel.ListSortDirection.Ascending)
                            parents = parents.OrderBy(d => d.Name).ToList();
                        else
                            parents = parents.OrderByDescending(d => d.Name).ToList();
                        
                        sorted = new List<DependencyItemModel>();
                        foreach (var parent in parents)
                        {
                            sorted.Add(parent);
                            var parentChildren = children.Where(c => c.ParentName == parent.Name).ToList();
                            if (direction == System.ComponentModel.ListSortDirection.Ascending)
                                parentChildren = parentChildren.OrderBy(c => c.Name).ToList();
                            else
                                parentChildren = parentChildren.OrderByDescending(c => c.Name).ToList();
                            sorted.AddRange(parentChildren);
                        }
                    }
                    else if (column.SortMemberPath == "LicenseType")
                    {
                        if (direction == System.ComponentModel.ListSortDirection.Ascending)
                            sorted = items.OrderBy(d => d.LicenseType).ThenBy(d => d.Depth).ThenBy(d => d.Name).ToList();
                        else
                            sorted = items.OrderByDescending(d => d.LicenseType).ThenBy(d => d.Depth).ThenBy(d => d.Name).ToList();
                    }
                    else
                    {
                        sorted = items;
                    }
                    
                    currentSource.Clear();
                    foreach (var item in sorted)
                    {
                        currentSource.Add(item);
                    }
                };

                // Update ForceLatest property when checkbox changes
                forceLatestCheckbox.Checked += (s, e) =>
                {
                    foreach (var dep in allDependencies)
                    {
                        dep.ForceLatest = true;
                    }
                };
                forceLatestCheckbox.Unchecked += (s, e) =>
                {
                    foreach (var dep in allDependencies)
                    {
                        dep.ForceLatest = false;
                    }
                };
                
                // Set initial state
                if (forceLatestCheckbox.IsChecked == true)
                {
                    foreach (var dep in allDependencies)
                    {
                        dep.ForceLatest = true;
                    }
                }

                // PERFORMANCE FIX: Pre-build lookup dictionaries for O(1) access instead of O(n) searches
                // This changes O(s × n) to O(s) where s = selected items, n = total dependencies
                var dependencyByName = result.Dependencies.ToDictionary(
                    d => d.Name, 
                    d => d, 
                    StringComparer.OrdinalIgnoreCase);
                
                var childrenByParent = result.Dependencies
                    .Where(d => !string.IsNullOrEmpty(d.ParentName))
                    .GroupBy(d => d.ParentName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key, 
                        g => g.ToList(), 
                        StringComparer.OrdinalIgnoreCase);
                
                // Helper to get enabled count efficiently (cached)
                int GetEnabledCount() => result.Dependencies.Count(d => d.IsEnabled);

                // Action button handlers
                enableSelectedButton.Click += (s, e) =>
                {
                    foreach (var item in dataGrid.SelectedItems)
                    {
                        if (item is DependencyItemModel dep)
                        {
                            // If this is a subdependency, automatically enable parent first
                            // PERFORMANCE FIX: O(1) lookup instead of O(n) FirstOrDefault
                            if (!string.IsNullOrEmpty(dep.ParentName) && 
                                dependencyByName.TryGetValue(dep.ParentName, out var parent) && 
                                !parent.IsEnabled)
                            {
                                parent.IsEnabled = true;
                            }
                            
                            dep.IsEnabled = true;
                            // Also enable all subdependencies
                            // PERFORMANCE FIX: O(1) lookup instead of O(n) Where
                            if (childrenByParent.TryGetValue(dep.Name, out var children))
                            {
                                foreach (var subDep in children)
                                {
                                    subDep.IsEnabled = true;
                                }
                            }
                        }
                    }
                    summaryText.Text = $"Found {result.Dependencies.Count} dependencies ({GetEnabledCount()} enabled)";
                };
                
                disableSelectedButton.Click += (s, e) =>
                {
                    foreach (var item in dataGrid.SelectedItems)
                    {
                        if (item is DependencyItemModel dep)
                        {
                            dep.IsEnabled = false;
                            dep.IsDisabledByUser = true;
                            
                            // If this is a parent dependency, also disable all its subdependencies
                            // PERFORMANCE FIX: O(1) lookup instead of O(n) Where
                            if (dep.HasSubDependencies && childrenByParent.TryGetValue(dep.Name, out var children))
                            {
                                foreach (var subDep in children)
                                {
                                    subDep.IsEnabled = false;
                                    subDep.IsDisabledByUser = true;
                                }
                            }
                        }
                    }
                    summaryText.Text = $"Found {result.Dependencies.Count} dependencies ({GetEnabledCount()} enabled)";
                };

                // Selection changed handler - show/hide action buttons
                dataGrid.SelectionChanged += (s, e) =>
                {
                    actionButtonsContainer.Visibility = dataGrid.SelectedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                };

                // Search functionality
                searchBox.TextChanged += (s, e) =>
                {
                    var searchText = searchBox.Text.ToLowerInvariant();
                    searchPlaceholder.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
                    clearSearchButton.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
                    
                    if (string.IsNullOrEmpty(searchText))
                    {
                        dataGrid.ItemsSource = allDependencies;
                        summaryText.Text = $"Found {result.Dependencies.Count} dependencies ({GetEnabledCount()} enabled)";
                    }
                    else
                    {
                        var filtered = allDependencies.Where(d => d.Name.ToLowerInvariant().Contains(searchText)).ToList();
                        dataGrid.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<DependencyItemModel>(filtered);
                        summaryText.Text = $"Showing {filtered.Count} of {result.Dependencies.Count} dependencies";
                    }
                };

                // Clear search button handler
                clearSearchButton.Click += (s, e) =>
                {
                    searchBox.Text = "";
                    searchBox.Focus();
                };

                // ESC key handler for search box
                searchBox.PreviewKeyDown += (s, e) =>
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
                var noDepsText = new TextBlock
                {
                    Text = "No dependencies found in this package.\n\nThis package does not have any dependencies listed in meta.json.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                Grid.SetRow(noDepsText, 4);
                tabGrid.Children.Add(noDepsText);
            }

            tab.Content = tabGrid;
            return tab;
        }

        /// <summary>
        /// Adds a toggle column for dependency enable/disable with checkbox styling
        /// </summary>
        private void AddDependencyToggleColumn(DataGrid dataGrid, List<DependencyItemModel> dependencies, string header, bool targetState, Color color, TextBlock summaryText)
        {
            // PERFORMANCE FIX: Pre-build lookup dictionaries for O(1) access instead of O(n) searches
            // Use GroupBy + First to handle potential duplicate dependency names (same dependency referenced multiple times)
            var dependencyByName = dependencies
                .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key, 
                    g => g.First(), 
                    StringComparer.OrdinalIgnoreCase);
            
            var childrenByParent = dependencies
                .Where(d => !string.IsNullOrEmpty(d.ParentName))
                .GroupBy(d => d.ParentName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key, 
                    g => g.ToList(), 
                    StringComparer.OrdinalIgnoreCase);
            
            int GetEnabledCount() => dependencies.Count(d => d.IsEnabled);

            var column = new DataGridTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(80),
                HeaderStyle = CreateCenteredHeaderStyle()
            };

            // Create cell template with checkbox
            var cellTemplate = new DataTemplate();
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            borderFactory.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            borderFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.SetValue(Border.CursorProperty, System.Windows.Input.Cursors.Hand);
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(5));

            // Create checkbox visual
            var checkboxBorderFactory = new FrameworkElementFactory(typeof(Border));
            checkboxBorderFactory.SetValue(Border.WidthProperty, 18.0);
            checkboxBorderFactory.SetValue(Border.HeightProperty, 18.0);
            checkboxBorderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 48)));
            checkboxBorderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(color));
            checkboxBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            checkboxBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS));
            checkboxBorderFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);

            // Create checkmark path
            var pathFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            pathFactory.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0,4 L 3,7 L 8,0"));
            pathFactory.SetValue(System.Windows.Shapes.Path.StrokeProperty, Brushes.White);
            pathFactory.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
            pathFactory.SetValue(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Collapsed);
            pathFactory.SetValue(System.Windows.Shapes.Path.StretchProperty, Stretch.Uniform);
            pathFactory.SetValue(System.Windows.Shapes.Path.MarginProperty, new Thickness(3));

            // Bind visibility based on IsEnabled state
            var visibilityBinding = new Binding("IsEnabled");
            visibilityBinding.Converter = new BoolToVisibilityConverter(targetState);
            pathFactory.SetBinding(System.Windows.Shapes.Path.VisibilityProperty, visibilityBinding);

            checkboxBorderFactory.AppendChild(pathFactory);
            borderFactory.AppendChild(checkboxBorderFactory);

            // Add click handler to the outer border
            borderFactory.AddHandler(Border.MouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler((s, e) =>
            {
                if (s is Border border && border.DataContext is DependencyItemModel dep)
                {
                    if (targetState)
                    {
                        // If this is a subdependency, automatically enable parent first
                        // PERFORMANCE FIX: O(1) lookup instead of O(n) FirstOrDefault
                        if (!string.IsNullOrEmpty(dep.ParentName) && 
                            dependencyByName.TryGetValue(dep.ParentName, out var parent) && 
                            !parent.IsEnabled)
                        {
                            parent.IsEnabled = true;
                        }
                        
                        dep.IsEnabled = true;
                        
                        // Also enable all subdependencies
                        // PERFORMANCE FIX: O(1) lookup instead of O(n) Where
                        if (childrenByParent.TryGetValue(dep.Name, out var children))
                        {
                            foreach (var subDep in children)
                            {
                                subDep.IsEnabled = true;
                            }
                        }
                    }
                    else
                    {
                        dep.IsEnabled = false;
                        dep.IsDisabledByUser = true;
                        
                        // If this is a parent dependency, also disable all its subdependencies
                        // PERFORMANCE FIX: O(1) lookup instead of O(n) Where
                        if (dep.HasSubDependencies && childrenByParent.TryGetValue(dep.Name, out var children))
                        {
                            foreach (var subDep in children)
                            {
                                subDep.IsEnabled = false;
                                subDep.IsDisabledByUser = true;
                            }
                        }
                    }
                    
                    // Update summary text
                    summaryText.Text = $"Found {dependencies.Count} dependencies ({GetEnabledCount()} enabled)";
                    
                    e.Handled = true;
                }
            }));

            cellTemplate.VisualTree = borderFactory;
            column.CellTemplate = cellTemplate;

            // Create header template with clickable header
            var headerTemplate = new DataTemplate();
            var headerBorderFactory = new FrameworkElementFactory(typeof(Border));
            headerBorderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            headerBorderFactory.SetValue(Border.CursorProperty, System.Windows.Input.Cursors.Hand);
            headerBorderFactory.SetValue(Border.PaddingProperty, new Thickness(5));

            var headerTextFactory = new FrameworkElementFactory(typeof(TextBlock));
            headerTextFactory.SetValue(TextBlock.TextProperty, header);
            headerTextFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200)));
            headerTextFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            headerTextFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);

            headerBorderFactory.AppendChild(headerTextFactory);

            // Add click handler to header to toggle all
            headerBorderFactory.AddHandler(Border.MouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler((s, e) =>
            {
                if (targetState)
                {
                    // Enable: only enable if parent is enabled (or if no parent)
                    foreach (var dep in dependencies)
                    {
                        if (string.IsNullOrEmpty(dep.ParentName))
                        {
                            // Top-level dependency, always enable
                            dep.IsEnabled = true;
                            if (dep.IsDisabledByUser)
                            {
                                dep.IsDisabledByUser = false;
                            }
                        }
                        else
                        {
                            // Subdependency, only enable if parent is enabled
                            // PERFORMANCE FIX: O(1) lookup instead of O(n) FirstOrDefault
                            if (dependencyByName.TryGetValue(dep.ParentName, out var parent) && parent.IsEnabled)
                            {
                                dep.IsEnabled = true;
                                if (dep.IsDisabledByUser)
                                {
                                    dep.IsDisabledByUser = false;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Disable: disable all and cascade to subdependencies
                    foreach (var dep in dependencies)
                    {
                        dep.IsEnabled = false;
                        dep.IsDisabledByUser = true;
                    }
                }
                
                // Update summary text
                summaryText.Text = $"Found {dependencies.Count} dependencies ({GetEnabledCount()} enabled)";
                
                e.Handled = true;
            }));

            headerTemplate.VisualTree = headerBorderFactory;
            column.HeaderTemplate = headerTemplate;

            dataGrid.Columns.Add(column);
        }

        /// <summary>
        /// Converter for dependency state to fill color
        /// </summary>
        private class DependencyStateToFillConverter : System.Windows.Data.IValueConverter
        {
            private readonly bool _targetState;
            private readonly Color _color;

            public DependencyStateToFillConverter(bool targetState, Color color)
            {
                _targetState = targetState;
                _color = color;
            }

            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is bool isEnabled)
                {
                    // Fill the bubble if current state matches target state
                    return (isEnabled == _targetState) ? new SolidColorBrush(_color) : Brushes.Transparent;
                }
                return Brushes.Transparent;
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Converter for boolean to visibility with target state matching
        /// </summary>
        private class BoolToVisibilityConverter : System.Windows.Data.IValueConverter
        {
            private readonly bool _targetState;

            public BoolToVisibilityConverter(bool targetState)
            {
                _targetState = targetState;
            }

            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is bool isEnabled)
                {
                    // Show checkmark if current state matches target state
                    return (isEnabled == _targetState) ? Visibility.Visible : Visibility.Collapsed;
                }
                return Visibility.Collapsed;
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }
    }
}

