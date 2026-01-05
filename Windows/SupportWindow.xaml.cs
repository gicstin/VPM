using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VPM.Services;

namespace VPM
{
    public partial class SupportWindow : Window
    {
        // Windows API for dark title bar
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private SupportInfo _supportInfo;

        public SupportWindow()
        {
            InitializeComponent();
            SourceInitialized += SupportWindow_SourceInitialized;
            LoadDataAsync();
        }

        private void SupportWindow_SourceInitialized(object sender, EventArgs e)
        {
            ApplyDarkTitleBar();
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                bool isDarkMode = false;

                if (Application.Current?.Resources != null)
                {
                    if (Application.Current.Resources.MergedDictionaries.Count > 0)
                    {
                        var themeDict = Application.Current.Resources.MergedDictionaries[0];
                        if (themeDict.Source != null && themeDict.Source.ToString().Contains("Dark"))
                        {
                            isDarkMode = true;
                        }
                    }

                    if (!isDarkMode && Application.Current.Resources.Contains(System.Windows.SystemColors.ControlBrushKey))
                    {
                        var brush = Application.Current.Resources[System.Windows.SystemColors.ControlBrushKey] as System.Windows.Media.SolidColorBrush;
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

        private async void LoadDataAsync()
        {
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                
                // Load Supporters asynchronously
                _supportInfo = await SupportService.GetSupportInfoAsync();
                
                SupportersList.ItemsSource = _supportInfo.Supporters;
                
                // Update UI if we have a valid link, otherwise keep default behavior
                // (Link text is initially collapsed)
                
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
            catch (System.Exception ex)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                MessageBox.Show($"Failed to load support data: {ex.Message}");
            }
        }

        private void PatreonButton_Click(object sender, RoutedEventArgs e)
        {
            // Use loaded link or fallback
            string url = _supportInfo?.PatreonLink ?? "https://www.patreon.com/gicstin";
            OpenUrl(url);
        }

        private void SupporterItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is SupporterItem item)
            {
                if (!string.IsNullOrEmpty(item.Link))
                {
                    OpenUrl(item.Link);
                }
            }
        }

        private void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Could not open link: {ex.Message}");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
