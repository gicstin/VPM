using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VPM
{
    /// <summary>
    /// Dialog for requesting network access permission from the user
    /// </summary>
    public partial class NetworkPermissionDialog : Window
    {
        /// <summary>
        /// Gets whether the user checked "Never show this message again"
        /// </summary>
        public bool NeverShowAgain { get; private set; }
        
        /// <summary>
        /// Gets whether the user wants to update the database now
        /// </summary>
        public bool UpdateDatabase { get; private set; }

        // Windows API for dark title bar
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public NetworkPermissionDialog()
        {
            InitializeComponent();
            SourceInitialized += NetworkPermissionDialog_SourceInitialized;
        }

        private void NetworkPermissionDialog_SourceInitialized(object sender, EventArgs e)
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

        private void NeverShowAgainCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // When "Never show again" is checked, disable the Deny button
            // This ensures users can only permanently save "Allow" permission
            if (DenyButton != null)
            {
                DenyButton.IsEnabled = NeverShowAgainCheckBox.IsChecked != true;
                
                // Update visual appearance when disabled
                if (NeverShowAgainCheckBox.IsChecked == true)
                {
                    DenyButton.Opacity = 0.5;
                    DenyButton.ToolTip = "Cannot deny when 'Never show again' is selected";
                }
                else
                {
                    DenyButton.Opacity = 1.0;
                    DenyButton.ToolTip = null;
                }
            }
        }

        private void Allow_Click(object sender, RoutedEventArgs e)
        {
            NeverShowAgain = NeverShowAgainCheckBox.IsChecked == true;
            UpdateDatabase = UpdateDatabaseCheckBox.IsChecked == true;
            DialogResult = true;
            Close();
        }

        private void Deny_Click(object sender, RoutedEventArgs e)
        {
            NeverShowAgain = NeverShowAgainCheckBox.IsChecked == true;
            UpdateDatabase = false;
            DialogResult = false;
            Close();
        }
    }
}

