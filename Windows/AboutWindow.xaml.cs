using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VPM.Services;

namespace VPM
{
    /// <summary>
    /// About window with enhanced UI and system information
    /// </summary>
    public partial class AboutWindow : Window
    {
        // Windows API for dark title bar
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public AboutWindow()
        {
            InitializeComponent();
            SourceInitialized += AboutWindow_SourceInitialized;
            PopulateInformation();
        }

        private void AboutWindow_SourceInitialized(object sender, EventArgs e)
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

        private void PopulateInformation()
        {
            try
            {
                // Version information
                VersionTextBlock.Text = VersionInfo.DisplayVersion;

                // Build information
                FrameworkTextBlock.Text = ".NET 10.0";
                PlatformTextBlock.Text = $"{(Environment.Is64BitProcess ? "Windows (64-bit)" : "Windows (32-bit)")}";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating About window: {ex.Message}");
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening link: {ex.Message}");
            }
        }

        private void GitHubButton_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/gicstin/VPM");
        }

        private void SharpCompressLink_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/adamhathcock/sharpcompress");
        }

        private void NetVipsLink_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/kleber-silveira/net-vips");
        }

        private void GraphShapeLink_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/KeRNeLith/GraphShape");
        }

        private void RecyclableMemoryStreamLink_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream");
        }

        private void ImageListViewLink_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/oozcitak/imagelistview");
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
