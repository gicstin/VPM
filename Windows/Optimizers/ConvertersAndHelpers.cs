using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace VPM
{
    public partial class MainWindow
    {
        private class StatusColorConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is bool exists)
                {
                    return new SolidColorBrush(exists ? 
                        Color.FromRgb(76, 175, 80) :  // Green
                        Color.FromRgb(244, 67, 54));   // Red
                }
                return new SolidColorBrush(Colors.Gray);
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }

        private class StatusArrowConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is bool exists)
                {
                    return exists ? "\u2022" : "\u25CB";
                }
                return "-";
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }

        // MOVED TO: Windows/Optimizers/MirrorsAndShadowsTabCreator.cs

        /// <summary>
        /// Converter for resolution quality color
        /// </summary>
        private class ResolutionColorConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is string resolution)
                {
                    return resolution switch
                    {
                        "8K" => new SolidColorBrush(Color.FromRgb(255, 215, 0)),     // Gold
                        "4K" => new SolidColorBrush(Color.FromRgb(192, 192, 192)),   // Silver
                        "2K" => new SolidColorBrush(Color.FromRgb(205, 127, 50)),    // Bronze
                        "1K" => new SolidColorBrush(Color.FromRgb(150, 150, 150)),   // Gray
                        "-" => new SolidColorBrush(Color.FromRgb(100, 100, 100)),    // Dark gray
                        _ => new SolidColorBrush(Color.FromRgb(180, 180, 180))       // Light gray for other sizes
                    };
                }
                return new SolidColorBrush(Color.FromRgb(180, 180, 180));
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Shows a dark-themed file conflict dialog
        /// </summary>
        private MessageBoxResult ShowFileConflictDialog(string filename)
        {
            var dialog = new Window
            {
                Title = "File Already Exists",
                Width = 600,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                BorderThickness = new Thickness(2)
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title
            var title = new TextBlock
            {
                Text = "File Already Exists",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Margin = new Thickness(0, 0, 0, 20)
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            // Message
            var messagePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
            
            var msg1 = new TextBlock
            {
                Text = $"An optimized version already exists in AddonPackages:",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            messagePanel.Children.Add(msg1);

            var msg2 = new TextBlock
            {
                Text = filename,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 15)
            };
            messagePanel.Children.Add(msg2);

            var msg3 = new TextBlock
            {
                Text = "Do you want to overwrite it?",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Margin = new Thickness(0, 0, 0, 15)
            };
            messagePanel.Children.Add(msg3);

            var options = new TextBlock
            {
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                LineHeight = 22
            };
            options.Inlines.Add(new System.Windows.Documents.Run("• ") { Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)) });
            options.Inlines.Add("Yes: Overwrite existing optimized version\n");
            options.Inlines.Add(new System.Windows.Documents.Run("• ") { Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)) });
            options.Inlines.Add("No: Create new version with timestamp\n");
            options.Inlines.Add(new System.Windows.Documents.Run("• ") { Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54)) });
            options.Inlines.Add("Cancel: Abort optimization");
            messagePanel.Children.Add(options);

            Grid.SetRow(messagePanel, 1);
            grid.Children.Add(messagePanel);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            MessageBoxResult result = MessageBoxResult.Cancel;

            var yesButton = new Button
            {
                Content = "Yes",
                Width = 120,
                Height = 40,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            yesButton.Click += (s, e) => { result = MessageBoxResult.Yes; dialog.Close(); };
            buttonPanel.Children.Add(yesButton);

            var noButton = new Button
            {
                Content = "No",
                Width = 120,
                Height = 40,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                Foreground = Brushes.Black,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            noButton.Click += (s, e) => { result = MessageBoxResult.No; dialog.Close(); };
            buttonPanel.Children.Add(noButton);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 120,
                Height = 40,
                Background = new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            cancelButton.Click += (s, e) => { result = MessageBoxResult.Cancel; dialog.Close(); };
            buttonPanel.Children.Add(cancelButton);

            Grid.SetRow(buttonPanel, 2);
            grid.Children.Add(buttonPanel);

            dialog.Content = grid;
            dialog.ShowDialog();

            return result;
        }

        /// <summary>
        /// Creates a modern checkbox style matching the Force .latest checkbox
        /// </summary>
        private Style CreateModernCheckboxStyle()
        {
            var checkboxStyle = new Style(typeof(System.Windows.Controls.CheckBox));
            var checkboxTemplate = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.CheckBox));
            
            var gridFactory = new System.Windows.FrameworkElementFactory(typeof(Grid));
            gridFactory.SetValue(Grid.BackgroundProperty, Brushes.Transparent);
            
            var col1 = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ColumnDefinition));
            col1.SetValue(System.Windows.Controls.ColumnDefinition.WidthProperty, GridLength.Auto);
            var col2 = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ColumnDefinition));
            col2.SetValue(System.Windows.Controls.ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            gridFactory.AppendChild(col1);
            gridFactory.AppendChild(col2);
            
            var borderFactory = new System.Windows.FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "CheckBoxBorder";
            borderFactory.SetValue(Grid.ColumnProperty, 0);
            borderFactory.SetValue(Border.WidthProperty, 18.0);
            borderFactory.SetValue(Border.HeightProperty, 18.0);
            borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 48)));
            borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(85, 85, 85)));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(UI_CORNER_RADIUS));
            borderFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            
            var pathFactory = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            pathFactory.Name = "CheckMark";
            pathFactory.SetValue(System.Windows.Shapes.Path.DataProperty, System.Windows.Media.Geometry.Parse("M 0,4 L 3,7 L 8,0"));
            pathFactory.SetValue(System.Windows.Shapes.Path.StrokeProperty, Brushes.White);
            pathFactory.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
            pathFactory.SetValue(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Collapsed);
            pathFactory.SetValue(System.Windows.Shapes.Path.StretchProperty, System.Windows.Media.Stretch.Uniform);
            pathFactory.SetValue(System.Windows.Shapes.Path.MarginProperty, new Thickness(3));
            
            borderFactory.AppendChild(pathFactory);
            gridFactory.AppendChild(borderFactory);
            
            var contentFactory = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(Grid.ColumnProperty, 1);
            contentFactory.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 0, 0));
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            gridFactory.AppendChild(contentFactory);
            
            checkboxTemplate.VisualTree = gridFactory;
            
            var checkedTrigger = new System.Windows.Trigger { Property = System.Windows.Controls.CheckBox.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Path.VisibilityProperty, Visibility.Visible, "CheckMark"));
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 175, 80)), "CheckBoxBorder"));
            checkboxTemplate.Triggers.Add(checkedTrigger);
            
            var checkboxHoverTrigger = new System.Windows.Trigger { Property = System.Windows.Controls.CheckBox.IsMouseOverProperty, Value = true };
            checkboxHoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(120, 120, 120)), "CheckBoxBorder"));
            checkboxTemplate.Triggers.Add(checkboxHoverTrigger);
            
            checkboxStyle.Setters.Add(new Setter(System.Windows.Controls.CheckBox.TemplateProperty, checkboxTemplate));
            
            return checkboxStyle;
        }
    }
}

