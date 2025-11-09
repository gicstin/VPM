using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VPM.Models;

namespace VPM
{
    public partial class ArchiveAllOldWindow : Window
    {
        // Windows API for dark title bar
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public ArchiveAllOldWindow(List<VarMetadata> oldPackages, string destinationPath)
        {
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
            
            MessageTextBlock.Text = $"The following {oldPackages.Count} old version package(s) will be moved to:\n{destinationPath}\n\nDo you want to continue?";
            
            // Create display items
            var displayItems = oldPackages.Select(p => new
            {
                DisplayName = $"{p.CreatorName}.{p.PackageName}.{p.Version}",
                Version = p.Version,
                LatestVersionNumber = p.LatestVersionNumber
            }).OrderBy(p => p.DisplayName).ToList();
            
            PackageListControl.ItemsSource = displayItems;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int value = 1;
                    // Try Windows 11/10 20H1+ attribute first, then fall back to older Windows 10 attribute
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
                    }
                }
            }
            catch
            {
                // Silently fail if dark title bar is not supported
            }
        }

        private void ArchiveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

