using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VPM
{
    public partial class MainWindow
    {
        private Border CreateTooltipInfoIcon(string tooltipText)
        {
            var iconBorder = new Border
            {
                Width = 18,
                Height = 18,
                Background = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                CornerRadius = new CornerRadius(9), // Circular icon - radius is half of width/height
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Help,
                ToolTip = tooltipText
            };

            var iconText = new TextBlock
            {
                Text = "?",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            iconBorder.Child = iconText;

            iconBorder.MouseEnter += (s, e) => iconBorder.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            iconBorder.MouseLeave += (s, e) => iconBorder.Background = new SolidColorBrush(Color.FromRgb(100, 100, 100));

            return iconBorder;
        }

        private Style CreateCenteredHeaderStyle()
        {
            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45))));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(60, 60, 60))));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(8, 8, 8, 8)));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
            return headerStyle;
        }

        private string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        private Window CreateProgressDialog(Window owner)
        {
            var bgColor = Color.FromRgb(30, 30, 30);
            var fgColor = Color.FromRgb(220, 220, 220);
            
            var dialog = new Window
            {
                Title = "Converting Textures",
                Width = 500,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(bgColor)
            };

            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(dialog).EnsureHandle();
                int attribute = 20;
                int useImmersiveDarkMode = 1;
                DwmSetWindowAttribute(hwnd, attribute, ref useImmersiveDarkMode, sizeof(int));
            }
            catch { }

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = "Initializing conversion...",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(fgColor)
            };
            Grid.SetRow(textBlock, 0);
            grid.Children.Add(textBlock);

            var progressBar = new ProgressBar
            {
                Height = 25,
                Minimum = 0,
                Maximum = 100,
                Value = 0
            };
            Grid.SetRow(progressBar, 2);
            grid.Children.Add(progressBar);

            dialog.Content = grid;
            return dialog;
        }
    }
}

