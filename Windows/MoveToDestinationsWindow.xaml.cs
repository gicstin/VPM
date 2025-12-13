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

        public MoveToDestinationViewModel()
        {
            _name = string.Empty;
            _path = string.Empty;
            _description = string.Empty;
            _isEnabled = true;
        }

        public MoveToDestinationViewModel(MoveToDestination model)
        {
            _name = model?.Name ?? string.Empty;
            _path = model?.Path ?? string.Empty;
            _description = model?.Description ?? string.Empty;
            _isEnabled = model?.IsEnabled ?? true;
            _sortOrder = model?.SortOrder ?? 0;
        }

        public MoveToDestination ToModel()
        {
            return new MoveToDestination
            {
                Name = Name,
                Path = Path,
                Description = Description,
                IsEnabled = IsEnabled,
                SortOrder = SortOrder
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
