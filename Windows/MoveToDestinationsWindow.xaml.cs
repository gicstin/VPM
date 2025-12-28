using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shapes;
using Microsoft.Win32;
using VPM.Models;
using VPM.Services;

namespace VPM.Windows
{
    /// <summary>
    /// Window for managing Move To destination paths
    /// </summary>
    public partial class MoveToDestinationsWindow : Window
    {
        private readonly ISettingsManager _settingsManager;
        private ObservableCollection<MoveToDestinationViewModel> _destinations;

        public MoveToDestinationsWindow(ISettingsManager settingsManager)
        {
            InitializeComponent();
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            
            LoadDestinations();
            UpdateStatus();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            EnableDarkTitleBar();
        }

        private void EnableDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    // DWMWA_USE_IMMERSIVE_DARK_MODE = 20
                    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
                    int darkMode = 1;
                    
                    // Call DwmSetWindowAttribute to enable dark mode
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                }
            }
            catch { /* Ignore if dark mode not supported */ }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void LoadDestinations()
        {
            var existingDestinations = _settingsManager.Settings?.MoveToDestinations ?? new List<MoveToDestination>();
            _destinations = new ObservableCollection<MoveToDestinationViewModel>(
                existingDestinations.Select(d => new MoveToDestinationViewModel(d))
            );
            DestinationsDataGrid.ItemsSource = _destinations;

            if (_settingsManager?.Settings != null)
            {
                DisableMoveToConfirmationCheckBox.IsChecked = _settingsManager.Settings.DisableMoveToConfirmation;
            }
        }

        private void UpdateStatus()
        {
            int enabledCount = _destinations.Count(d => d.IsEnabled);
            StatusText.Text = $"{_destinations.Count} destination(s) configured, {enabledCount} enabled";
        }

        private MessageBoxResult ShowDarkThemedDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 450,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 224, 224)),
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            dialog.Loaded += (s, e) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(dialog).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
                        int darkMode = 1;
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                    }
                }
                catch { }
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var messageBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 224, 224))
            };
            Grid.SetRow(messageBlock, 0);
            grid.Children.Add(messageBlock);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };

            Action<Button> styleButton = (btn) =>
            {
                btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51));
                btn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 224, 224));
                btn.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100));
                btn.BorderThickness = new Thickness(1);
                btn.Padding = new Thickness(12, 6, 12, 6);
                btn.Cursor = System.Windows.Input.Cursors.Hand;

                btn.MouseEnter += (s, e) =>
                {
                    btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(70, 70, 70));
                };
                btn.MouseLeave += (s, e) =>
                {
                    btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51));
                };
            };

            if (buttons == MessageBoxButton.YesNo)
            {
                var yesButton = new Button
                {
                    Content = "Yes",
                    Width = 80,
                    Height = 32,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                styleButton(yesButton);
                yesButton.Click += (s, e) => { dialog.DialogResult = true; dialog.Close(); };

                var noButton = new Button
                {
                    Content = "No",
                    Width = 80,
                    Height = 32
                };
                styleButton(noButton);
                noButton.Click += (s, e) => { dialog.DialogResult = false; dialog.Close(); };

                buttonPanel.Children.Add(yesButton);
                buttonPanel.Children.Add(noButton);
            }
            else
            {
                var okButton = new Button
                {
                    Content = "OK",
                    Width = 80,
                    Height = 32
                };
                styleButton(okButton);
                okButton.Click += (s, e) => { dialog.DialogResult = true; dialog.Close(); };
                buttonPanel.Children.Add(okButton);
            }

            Grid.SetRow(buttonPanel, 1);
            grid.Children.Add(buttonPanel);

            dialog.Content = grid;
            var result = dialog.ShowDialog();
            return result == true ? (buttons == MessageBoxButton.YesNo ? MessageBoxResult.Yes : MessageBoxResult.OK) : MessageBoxResult.No;
        }

        private void DestinationsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasSelection = DestinationsDataGrid.SelectedItem != null;
            int selectedIndex = DestinationsDataGrid.SelectedIndex;
            
            EditButton.IsEnabled = hasSelection;
            RemoveButton.IsEnabled = hasSelection;
            MoveUpButton.IsEnabled = hasSelection && selectedIndex > 0;
            MoveDownButton.IsEnabled = hasSelection && selectedIndex < _destinations.Count - 1;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new MoveToDestinationEditDialog(null)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                var newDest = new MoveToDestinationViewModel(dialog.Result)
                {
                    SortOrder = _destinations.Count
                };
                _destinations.Add(newDest);
                DestinationsDataGrid.SelectedItem = newDest;
                UpdateStatus();
            }
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (DestinationsDataGrid.SelectedItem is MoveToDestinationViewModel selected)
            {
                var dialog = new MoveToDestinationEditDialog(selected.ToModel())
                {
                    Owner = this
                };

                if (dialog.ShowDialog() == true)
                {
                    selected.Name = dialog.Result.Name;
                    selected.Path = dialog.Result.Path;
                    selected.Description = dialog.Result.Description;
                    UpdateStatus();
                }
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (DestinationsDataGrid.SelectedItem is MoveToDestinationViewModel selected)
            {
                var result = ShowDarkThemedDialog(
                    $"Are you sure you want to remove the destination '{selected.Name}'?",
                    "Confirm Removal",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _destinations.Remove(selected);
                    UpdateSortOrders();
                    UpdateStatus();
                }
            }
        }

        private void MoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            int index = DestinationsDataGrid.SelectedIndex;
            if (index > 0)
            {
                var item = _destinations[index];
                _destinations.RemoveAt(index);
                _destinations.Insert(index - 1, item);
                DestinationsDataGrid.SelectedIndex = index - 1;
                UpdateSortOrders();
            }
        }

        private void MoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            int index = DestinationsDataGrid.SelectedIndex;
            if (index < _destinations.Count - 1)
            {
                var item = _destinations[index];
                _destinations.RemoveAt(index);
                _destinations.Insert(index + 1, item);
                DestinationsDataGrid.SelectedIndex = index + 1;
                UpdateSortOrders();
            }
        }

        private void UpdateSortOrders()
        {
            for (int i = 0; i < _destinations.Count; i++)
            {
                _destinations[i].SortOrder = i;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate all destinations
            var invalidDests = _destinations.Where(d => !d.IsValid()).ToList();
            if (invalidDests.Any())
            {
                ShowDarkThemedDialog(
                    "Some destinations have invalid names or paths. Please fix them before saving.",
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Check for duplicate names
            var duplicateNames = _destinations
                .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateNames.Any())
            {
                ShowDarkThemedDialog(
                    $"Duplicate destination names found: {string.Join(", ", duplicateNames)}. Please use unique names.",
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Save to settings
            UpdateSortOrders();
            var destinations = _destinations.Select(d => d.ToModel()).ToList();
            
            if (_settingsManager?.Settings != null)
            {
                _settingsManager.Settings.MoveToDestinations = destinations;
                _settingsManager.Settings.DisableMoveToConfirmation = DisableMoveToConfirmationCheckBox.IsChecked == true;
                _settingsManager.SaveSettingsImmediate();
            }
            else
            {
                ShowDarkThemedDialog("Error: Settings manager is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ColorPickerButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not MoveToDestinationViewModel viewModel)
                return;

            // Show color picker dialog
            var colorDialog = new ColorPickerDialog(viewModel.StatusColor)
            {
                Owner = this
            };

            if (colorDialog.ShowDialog() == true)
            {
                viewModel.StatusColor = colorDialog.SelectedColorHex;
            }
        }
    }

    /// <summary>
    /// Color picker dialog with HSV gradient picker and OK/Cancel buttons
    /// </summary>
    public class ColorPickerDialog : Window
    {
        public string SelectedColorHex { get; private set; }
        private TextBox _hexTextBox;
        private Border _previewBorder;
        private Canvas _colorCanvas;
        private Canvas _hueSlider;
        private Ellipse _colorSelector;
        private Border _hueSelector;
        private double _currentHue = 0;
        private double _currentSaturation = 1;
        private double _currentValue = 1;
        private bool _isDraggingColor = false;
        private bool _isDraggingHue = false;
        private bool _suppressHexUpdate = false;

        public ColorPickerDialog(string currentColor)
        {
            Title = "Select Color";
            Width = 400;
            Height = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45));
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 224, 224));

            SelectedColorHex = currentColor ?? "#FF0000";

            // Parse initial color to HSV
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(SelectedColorHex);
                ColorToHsv(color, out _currentHue, out _currentSaturation, out _currentValue);
            }
            catch { }

            Loaded += (s, e) => EnableDarkTitleBar();

            var mainGrid = new Grid { Margin = new Thickness(15) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(200) }); // Color canvas
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) }); // Hue slider
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Preview
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Hex input
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons

            // Color canvas (saturation/value)
            var colorCanvasBorder = new Border
            {
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };
            _colorCanvas = new Canvas
            {
                Width = 350,
                Height = 200,
                ClipToBounds = true,
                Cursor = System.Windows.Input.Cursors.Cross
            };
            UpdateColorCanvasBackground();

            // Color selector circle
            _colorSelector = new Ellipse
            {
                Width = 16,
                Height = 16,
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 2,
                Fill = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = false
            };
            _colorCanvas.Children.Add(_colorSelector);
            UpdateColorSelectorPosition();

            _colorCanvas.MouseLeftButtonDown += ColorCanvas_MouseLeftButtonDown;
            _colorCanvas.MouseMove += ColorCanvas_MouseMove;
            _colorCanvas.MouseLeftButtonUp += ColorCanvas_MouseLeftButtonUp;
            _colorCanvas.MouseLeave += ColorCanvas_MouseLeave;

            colorCanvasBorder.Child = _colorCanvas;
            Grid.SetRow(colorCanvasBorder, 0);
            mainGrid.Children.Add(colorCanvasBorder);

            // Hue slider
            var hueSliderBorder = new Border
            {
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };
            _hueSlider = new Canvas
            {
                Width = 350,
                Height = 25,
                ClipToBounds = true,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            
            // Create hue gradient
            var hueGradient = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0.5),
                EndPoint = new System.Windows.Point(1, 0.5)
            };
            hueGradient.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(255, 0, 0), 0));
            hueGradient.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(255, 255, 0), 0.167));
            hueGradient.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0, 255, 0), 0.333));
            hueGradient.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0, 255, 255), 0.5));
            hueGradient.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0, 0, 255), 0.667));
            hueGradient.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(255, 0, 255), 0.833));
            hueGradient.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(255, 0, 0), 1));
            _hueSlider.Background = hueGradient;

            // Hue selector
            _hueSelector = new Border
            {
                Width = 6,
                Height = 25,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false
            };
            _hueSlider.Children.Add(_hueSelector);
            UpdateHueSelectorPosition();

            _hueSlider.MouseLeftButtonDown += HueSlider_MouseLeftButtonDown;
            _hueSlider.MouseMove += HueSlider_MouseMove;
            _hueSlider.MouseLeftButtonUp += HueSlider_MouseLeftButtonUp;
            _hueSlider.MouseLeave += HueSlider_MouseLeave;

            hueSliderBorder.Child = _hueSlider;
            Grid.SetRow(hueSliderBorder, 2);
            mainGrid.Children.Add(hueSliderBorder);

            // Color preview
            var previewPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var previewLabel = new TextBlock { Text = "Selected:", Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            _previewBorder = new Border
            {
                Width = 80,
                Height = 35,
                CornerRadius = new CornerRadius(4),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100)),
                BorderThickness = new Thickness(1)
            };
            UpdatePreviewColor();
            previewPanel.Children.Add(previewLabel);
            previewPanel.Children.Add(_previewBorder);
            Grid.SetRow(previewPanel, 4);
            mainGrid.Children.Add(previewPanel);

            // Hex input
            var hexPanel = new DockPanel();
            var hexLabel = new TextBlock { Text = "Hex:", Width = 40, VerticalAlignment = VerticalAlignment.Center, Foreground = Foreground };
            _hexTextBox = new TextBox
            {
                Text = SelectedColorHex,
                Width = 100,
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),
                Foreground = Foreground,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100))
            };
            _hexTextBox.TextChanged += HexTextBox_TextChanged;
            DockPanel.SetDock(hexLabel, Dock.Left);
            hexPanel.Children.Add(hexLabel);
            hexPanel.Children.Add(_hexTextBox);
            Grid.SetRow(hexPanel, 6);
            mainGrid.Children.Add(hexPanel);

            // Buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okButton = new Button
            {
                Content = "OK",
                Width = 90,
                Height = 32,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            okButton.Click += (s, e) => { DialogResult = true; Close(); };
            
            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 90,
                Height = 32,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(70, 70, 70)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };
            
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            Grid.SetRow(buttonPanel, 8);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;
        }

        private void ColorCanvas_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingColor = true;
            _colorCanvas.CaptureMouse();
            UpdateColorFromCanvas(e.GetPosition(_colorCanvas));
        }

        private void ColorCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDraggingColor)
                UpdateColorFromCanvas(e.GetPosition(_colorCanvas));
        }

        private void ColorCanvas_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingColor = false;
            _colorCanvas.ReleaseMouseCapture();
        }

        private void ColorCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDraggingColor)
                return;
        }

        private void HueSlider_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingHue = true;
            _hueSlider.CaptureMouse();
            UpdateHueFromSlider(e.GetPosition(_hueSlider));
        }

        private void HueSlider_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDraggingHue)
                UpdateHueFromSlider(e.GetPosition(_hueSlider));
        }

        private void HueSlider_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingHue = false;
            _hueSlider.ReleaseMouseCapture();
        }

        private void HueSlider_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDraggingHue)
                return;
        }

        private void UpdateColorFromCanvas(System.Windows.Point pos)
        {
            _currentSaturation = Math.Max(0, Math.Min(1, pos.X / _colorCanvas.Width));
            _currentValue = Math.Max(0, Math.Min(1, 1 - pos.Y / _colorCanvas.Height));
            UpdateSelectedColor();
            UpdateColorSelectorPosition();
        }

        private void UpdateHueFromSlider(System.Windows.Point pos)
        {
            _currentHue = Math.Max(0, Math.Min(360, pos.X / _hueSlider.Width * 360));
            UpdateColorCanvasBackground();
            UpdateSelectedColor();
            UpdateHueSelectorPosition();
        }

        private void UpdateColorCanvasBackground()
        {
            var hueColor = HsvToColor(_currentHue, 1, 1);
            
            // Create saturation gradient (white to hue color)
            var satGradient = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 0)
            };
            satGradient.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.White, 0));
            satGradient.GradientStops.Add(new System.Windows.Media.GradientStop(hueColor, 1));

            // Create value gradient (transparent to black)
            var valGradient = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1)
            };
            valGradient.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Transparent, 0));
            valGradient.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Black, 1));

            // Apply both gradients using a visual brush
            var rect1 = new System.Windows.Shapes.Rectangle { Width = _colorCanvas.Width, Height = _colorCanvas.Height, Fill = satGradient };
            var rect2 = new System.Windows.Shapes.Rectangle { Width = _colorCanvas.Width, Height = _colorCanvas.Height, Fill = valGradient };
            
            _colorCanvas.Background = satGradient;
            
            // Remove old overlay if exists
            var toRemove = _colorCanvas.Children.OfType<System.Windows.Shapes.Rectangle>().ToList();
            foreach (var r in toRemove) _colorCanvas.Children.Remove(r);
            
            // Add value overlay
            var overlay = new System.Windows.Shapes.Rectangle
            {
                Width = _colorCanvas.Width,
                Height = _colorCanvas.Height,
                Fill = valGradient
            };
            Canvas.SetLeft(overlay, 0);
            Canvas.SetTop(overlay, 0);
            _colorCanvas.Children.Insert(0, overlay);
        }

        private void UpdateColorSelectorPosition()
        {
            double x = _currentSaturation * _colorCanvas.Width - _colorSelector.Width / 2;
            double y = (1 - _currentValue) * _colorCanvas.Height - _colorSelector.Height / 2;
            Canvas.SetLeft(_colorSelector, x);
            Canvas.SetTop(_colorSelector, y);
        }

        private void UpdateHueSelectorPosition()
        {
            double x = _currentHue / 360 * _hueSlider.Width - _hueSelector.Width / 2;
            Canvas.SetLeft(_hueSelector, x);
            Canvas.SetTop(_hueSelector, 0);
        }

        private void UpdateSelectedColor()
        {
            var color = HsvToColor(_currentHue, _currentSaturation, _currentValue);
            SelectedColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            
            _suppressHexUpdate = true;
            _hexTextBox.Text = SelectedColorHex;
            _suppressHexUpdate = false;
            
            UpdatePreviewColor();
        }

        private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressHexUpdate) return;
            
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_hexTextBox.Text);
                SelectedColorHex = _hexTextBox.Text;
                ColorToHsv(color, out _currentHue, out _currentSaturation, out _currentValue);
                UpdateColorCanvasBackground();
                UpdateColorSelectorPosition();
                UpdateHueSelectorPosition();
                UpdatePreviewColor();
            }
            catch { }
        }

        private void UpdatePreviewColor()
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(SelectedColorHex);
                _previewBorder.Background = new System.Windows.Media.SolidColorBrush(color);
            }
            catch
            {
                _previewBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128));
            }
        }

        private static System.Windows.Media.Color HsvToColor(double h, double s, double v)
        {
            double r, g, b;
            int hi = (int)(h / 60) % 6;
            double f = h / 60 - hi;
            double p = v * (1 - s);
            double q = v * (1 - f * s);
            double t = v * (1 - (1 - f) * s);

            switch (hi)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }

            return System.Windows.Media.Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        private static void ColorToHsv(System.Windows.Media.Color color, out double h, out double s, out double v)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            v = max;
            s = max == 0 ? 0 : delta / max;

            if (delta == 0)
            {
                h = 0;
            }
            else if (max == r)
            {
                h = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                h = 60 * ((b - r) / delta + 2);
            }
            else
            {
                h = 60 * ((r - g) / delta + 4);
            }

            if (h < 0) h += 360;
        }

        private void EnableDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
                    int darkMode = 1;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                }
            }
            catch { }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }

    /// <summary>
    /// ViewModel wrapper for MoveToDestination with INotifyPropertyChanged support
    /// </summary>
    public class MoveToDestinationViewModel : INotifyPropertyChanged
    {
        private string _name;
        private string _path;
        private string _description;
        private bool _isEnabled;
        private int _sortOrder;
        private bool _showInMainTable;
        private string _statusColor;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string Path
        {
            get => _path;
            set { _path = value; OnPropertyChanged(); }
        }

        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public int SortOrder
        {
            get => _sortOrder;
            set { _sortOrder = value; OnPropertyChanged(); }
        }

        public bool ShowInMainTable
        {
            get => _showInMainTable;
            set { _showInMainTable = value; OnPropertyChanged(); }
        }

        public string StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusColorBrush)); }
        }

        public System.Windows.Media.SolidColorBrush StatusColorBrush
        {
            get
            {
                try
                {
                    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_statusColor ?? "#808080");
                    return new System.Windows.Media.SolidColorBrush(color);
                }
                catch
                {
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128));
                }
            }
        }

        public MoveToDestinationViewModel()
        {
            _name = string.Empty;
            _path = string.Empty;
            _description = string.Empty;
            _isEnabled = true;
            _showInMainTable = true;
            _statusColor = "#808080";
        }

        public MoveToDestinationViewModel(MoveToDestination model)
        {
            _name = model?.Name ?? string.Empty;
            _path = model?.Path ?? string.Empty;
            _description = model?.Description ?? string.Empty;
            _isEnabled = model?.IsEnabled ?? true;
            _sortOrder = model?.SortOrder ?? 0;
            _showInMainTable = model?.ShowInMainTable ?? true;
            _statusColor = model?.StatusColor ?? "#808080";
        }

        public MoveToDestination ToModel()
        {
            return new MoveToDestination
            {
                Name = Name,
                Path = Path,
                Description = Description,
                IsEnabled = IsEnabled,
                SortOrder = SortOrder,
                ShowInMainTable = ShowInMainTable,
                StatusColor = StatusColor
            };
        }

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Path);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Dialog for adding/editing a single Move To destination
    /// </summary>
    public class MoveToDestinationEditDialog : Window
    {
        public MoveToDestination Result { get; private set; }

        public MoveToDestinationEditDialog(MoveToDestination existing)
        {
            InitializeDialog(existing);
        }

        private TextBox _nameTextBox;
        private TextBox _pathTextBox;
        private TextBox _descriptionTextBox;

        private void InitializeDialog(MoveToDestination existing)
        {
            Title = existing == null ? "Add Destination" : "Edit Destination";
            Width = 500;
            Height = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            
            // Use dynamic resources for theme support
            SetResourceReference(BackgroundProperty, SystemColors.WindowBrushKey);
            SetResourceReference(ForegroundProperty, SystemColors.WindowTextBrushKey);
            
            // Enable dark title bar on Windows 10+
            Loaded += (s, e) => EnableDarkTitleBar();

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Name row
            var namePanel = new DockPanel();
            var nameLabel = new TextBlock { Text = "Name:", Width = 80, VerticalAlignment = VerticalAlignment.Center };
            nameLabel.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.ControlTextBrushKey);
            _nameTextBox = new TextBox { Text = existing?.Name ?? "", VerticalContentAlignment = VerticalAlignment.Center, Height = 28 };
            _nameTextBox.SetResourceReference(TextBox.BackgroundProperty, SystemColors.ControlBrushKey);
            _nameTextBox.SetResourceReference(TextBox.ForegroundProperty, SystemColors.ControlTextBrushKey);
            _nameTextBox.SetResourceReference(TextBox.BorderBrushProperty, SystemColors.ActiveBorderBrushKey);
            DockPanel.SetDock(nameLabel, Dock.Left);
            namePanel.Children.Add(nameLabel);
            namePanel.Children.Add(_nameTextBox);
            Grid.SetRow(namePanel, 0);
            grid.Children.Add(namePanel);

            // Path row
            var pathPanel = new DockPanel();
            var pathLabel = new TextBlock { Text = "Path:", Width = 80, VerticalAlignment = VerticalAlignment.Center };
            pathLabel.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.ControlTextBrushKey);
            var browseButton = new Button { Content = "...", Width = 30, Height = 28, Margin = new Thickness(5, 0, 0, 0) };
            browseButton.Click += BrowseButton_Click;
            _pathTextBox = new TextBox { Text = existing?.Path ?? "", VerticalContentAlignment = VerticalAlignment.Center, Height = 28 };
            _pathTextBox.SetResourceReference(TextBox.BackgroundProperty, SystemColors.ControlBrushKey);
            _pathTextBox.SetResourceReference(TextBox.ForegroundProperty, SystemColors.ControlTextBrushKey);
            _pathTextBox.SetResourceReference(TextBox.BorderBrushProperty, SystemColors.ActiveBorderBrushKey);
            DockPanel.SetDock(pathLabel, Dock.Left);
            DockPanel.SetDock(browseButton, Dock.Right);
            pathPanel.Children.Add(pathLabel);
            pathPanel.Children.Add(browseButton);
            pathPanel.Children.Add(_pathTextBox);
            Grid.SetRow(pathPanel, 2);
            grid.Children.Add(pathPanel);

            // Description row
            var descPanel = new DockPanel();
            var descLabel = new TextBlock { Text = "Description:", Width = 80, VerticalAlignment = VerticalAlignment.Center };
            descLabel.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.ControlTextBrushKey);
            _descriptionTextBox = new TextBox { Text = existing?.Description ?? "", VerticalContentAlignment = VerticalAlignment.Center, Height = 28 };
            _descriptionTextBox.SetResourceReference(TextBox.BackgroundProperty, SystemColors.ControlBrushKey);
            _descriptionTextBox.SetResourceReference(TextBox.ForegroundProperty, SystemColors.ControlTextBrushKey);
            _descriptionTextBox.SetResourceReference(TextBox.BorderBrushProperty, SystemColors.ActiveBorderBrushKey);
            DockPanel.SetDock(descLabel, Dock.Left);
            descPanel.Children.Add(descLabel);
            descPanel.Children.Add(_descriptionTextBox);
            Grid.SetRow(descPanel, 4);
            grid.Children.Add(descPanel);

            // Buttons row
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okButton = new Button { Content = "OK", Width = 80, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
            okButton.Click += OkButton_Click;
            var cancelButton = new Button { Content = "Cancel", Width = 80, Height = 32 };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            Grid.SetRow(buttonPanel, 6);
            grid.Children.Add(buttonPanel);

            Content = grid;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select destination folder",
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrEmpty(_pathTextBox.Text) && Directory.Exists(_pathTextBox.Text))
            {
                dialog.SelectedPath = _pathTextBox.Text;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _pathTextBox.Text = dialog.SelectedPath;
                
                // Auto-fill name if empty
                if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
                {
                    _nameTextBox.Text = System.IO.Path.GetFileName(dialog.SelectedPath);
                }
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
            {
                MessageBox.Show("Please enter a name for this destination.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                _nameTextBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(_pathTextBox.Text))
            {
                MessageBox.Show("Please enter or select a path for this destination.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                _pathTextBox.Focus();
                return;
            }

            Result = new MoveToDestination
            {
                Name = _nameTextBox.Text.Trim(),
                Path = _pathTextBox.Text.Trim(),
                Description = _descriptionTextBox.Text.Trim(),
                IsEnabled = true
            };

            DialogResult = true;
            Close();
        }

        private void EnableDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    // DWMWA_USE_IMMERSIVE_DARK_MODE = 20
                    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
                    int darkMode = 1;
                    
                    // Call DwmSetWindowAttribute to enable dark mode
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                }
            }
            catch { /* Ignore if dark mode not supported */ }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
