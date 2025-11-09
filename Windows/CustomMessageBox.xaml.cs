using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace VPM
{
    /// <summary>
    /// Custom MessageBox that supports dark mode theming
    /// </summary>
    public partial class CustomMessageBox : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        // Windows API for dark title bar
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private CustomMessageBox()
        {
            InitializeComponent();
            SourceInitialized += CustomMessageBox_SourceInitialized;
        }

        private void CustomMessageBox_SourceInitialized(object sender, EventArgs e)
        {
            // Apply dark title bar if in dark mode
            ApplyDarkTitleBar();
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                // Check if we're in dark mode by checking the application's resource dictionary
                bool isDarkMode = false;
                
                // Check if a dark theme is loaded
                if (Application.Current?.Resources != null)
                {
                    // Try to get a theme-specific resource to detect dark mode
                    if (Application.Current.Resources.MergedDictionaries.Count > 0)
                    {
                        var themeDict = Application.Current.Resources.MergedDictionaries[0];
                        if (themeDict.Source != null && themeDict.Source.ToString().Contains("Dark"))
                        {
                            isDarkMode = true;
                        }
                    }
                    
                    // Fallback: check background color
                    if (!isDarkMode && Application.Current.Resources.Contains(SystemColors.ControlBrushKey))
                    {
                        var brush = Application.Current.Resources[SystemColors.ControlBrushKey] as SolidColorBrush;
                        if (brush != null)
                        {
                            isDarkMode = brush.Color.R < 128;
                        }
                    }
                }

                if (isDarkMode)
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int value = 1;
                        // Try Windows 11 attribute first, then fall back to Windows 10
                        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                        {
                            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
                        }
                    }
                }
            }
            catch
            {
                // Silently fail if dark title bar is not supported
            }
        }

        /// <summary>
        /// Shows a custom message box with dark mode support
        /// </summary>
        public static MessageBoxResult Show(string message, string title = "Message", 
            MessageBoxButton buttons = MessageBoxButton.OK, 
            MessageBoxImage icon = MessageBoxImage.None)
        {
            var dialog = new CustomMessageBox
            {
                Title = title
            };

            dialog.MessageTextBlock.Text = message;
            dialog.SetIcon(icon);
            dialog.SetButtons(buttons);

            dialog.ShowDialog();
            return dialog.Result;
        }

        private void SetIcon(MessageBoxImage icon)
        {
            switch (icon)
            {
                case MessageBoxImage.Information:
                    IconTextBlock.Text = "„¹ï¸";
                    break;
                case MessageBoxImage.Warning:
                    IconTextBlock.Text = "–ï¸";
                    break;
                case MessageBoxImage.Error:
                    IconTextBlock.Text = "┌";
                    break;
                case MessageBoxImage.Question:
                    IconTextBlock.Text = "â“";
                    break;
                default:
                    IconTextBlock.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void SetButtons(MessageBoxButton buttons)
        {
            ButtonPanel.Children.Clear();

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    AddButton("OK", MessageBoxResult.OK, true);
                    break;

                case MessageBoxButton.OKCancel:
                    AddButton("OK", MessageBoxResult.OK, true);
                    AddButton("Cancel", MessageBoxResult.Cancel, false, true);
                    break;

                case MessageBoxButton.YesNo:
                    AddButton("Yes", MessageBoxResult.Yes, true);
                    AddButton("No", MessageBoxResult.No, false, true);
                    break;

                case MessageBoxButton.YesNoCancel:
                    AddButton("Yes", MessageBoxResult.Yes, true);
                    AddButton("No", MessageBoxResult.No, false);
                    AddButton("Cancel", MessageBoxResult.Cancel, false, true);
                    break;
            }
        }

        private void AddButton(string content, MessageBoxResult result, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button
            {
                Content = content,
                IsDefault = isDefault,
                IsCancel = isCancel
            };

            button.Click += (s, e) =>
            {
                Result = result;
                DialogResult = result != MessageBoxResult.Cancel && result != MessageBoxResult.No;
                Close();
            };

            ButtonPanel.Children.Add(button);
        }
    }
}

