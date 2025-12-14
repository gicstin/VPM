using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VPM.Services;

namespace VPM
{
    public partial class ArchivePathSelectorWindow : Window
    {
        private readonly ISettingsManager _settingsManager;
        private string _selectedPath = "";

        public string SelectedPath
        {
            get => _selectedPath;
            set => _selectedPath = value;
        }

        public ArchivePathSelectorWindow(ISettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _selectedPath = _settingsManager.Settings.CustomArchivePath ?? "";

            InitializeWindow();
        }

        private void InitializeWindow()
        {
            Title = "Configure Archive Path";
            Width = 850;
            Height = 450;
            MinWidth = 700;
            MinHeight = 350;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));

            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
                int useImmersiveDarkMode = 1;
                DwmSetWindowAttribute(hwnd, 20, ref useImmersiveDarkMode, sizeof(int));
            }
            catch { }

            var mainGrid = new Grid { Margin = new Thickness(25) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title
            var titleBlock = new TextBlock
            {
                Text = "Custom Archive Path Configuration",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(titleBlock, 0);
            mainGrid.Children.Add(titleBlock);

            // Content area
            var contentScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };

            var contentPanel = new StackPanel 
            { 
                Margin = new Thickness(5),
                Width = double.NaN
            };

            var descriptionText = new TextBlock
            {
                Text = "Specify a custom folder where optimized package backups will be stored. If left empty, the default 'ArchivedPackages' folder in your game directory will be used.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 15)
            };
            contentPanel.Children.Add(descriptionText);

            var pathGrid = new Grid();
            pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pathGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var pathLabel = new TextBlock
            {
                Text = "Archive Path:",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(pathLabel, 0);
            pathGrid.Children.Add(pathLabel);

            var pathTextBox = new TextBox
            {
                Text = _selectedPath,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 10, 10, 10),
                VerticalAlignment = VerticalAlignment.Center,
                IsReadOnly = true,
                MinHeight = 36,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(pathTextBox, 0);
            pathGrid.Children.Add(pathTextBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 0)
            };

            var browseButton = new Button
            {
                Content = "Browse Folder",
                Width = 110,
                Height = 36,
                Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 0)
            };
            browseButton.Click += (s, e) =>
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select Archive Path",
                    ShowNewFolderButton = true
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _selectedPath = dialog.SelectedPath;
                    pathTextBox.Text = _selectedPath;
                    _settingsManager.Settings.CustomArchivePath = _selectedPath;
                }
            };
            buttonPanel.Children.Add(browseButton);

            var clearButton = new Button
            {
                Content = "Clear Path",
                Width = 110,
                Height = 36,
                Background = new SolidColorBrush(Color.FromRgb(200, 100, 100)),
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            clearButton.Click += (s, e) =>
            {
                _selectedPath = "";
                pathTextBox.Text = "";
                _settingsManager.Settings.CustomArchivePath = "";
            };
            buttonPanel.Children.Add(clearButton);

            Grid.SetRow(buttonPanel, 2);
            pathGrid.Children.Add(buttonPanel);

            contentPanel.Children.Add(pathGrid);

            var noteText = new TextBlock
            {
                Text = "Note: The custom path will be used for all future package optimizations. Existing backups in the default ArchivedPackages folder will still be available for restore operations.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 15, 0, 0),
                FontStyle = FontStyles.Italic
            };
            contentPanel.Children.Add(noteText);

            contentScroll.Content = contentPanel;
            Grid.SetRow(contentScroll, 2);
            mainGrid.Children.Add(contentScroll);

            // Close button at bottom right
            var closeButton = new Button
            {
                Content = "Close",
                Width = 100,
                Height = 35,
                Background = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            };
            closeButton.Click += (s, e) => Close();

            var closeButtonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 0, 0)
            };
            closeButtonPanel.Children.Add(closeButton);

            Grid.SetRow(closeButtonPanel, 4);
            mainGrid.Children.Add(closeButtonPanel);

            Content = mainGrid;
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
