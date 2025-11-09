using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VPM
{
    public partial class ConfirmArchiveWindow : Window
    {
        public bool ArchiveAll { get; private set; } = false;
        private int _selectedCount;
        private int _totalOldCount;

        // Windows API for dark title bar
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public ConfirmArchiveWindow(int selectedCount, string destinationPath, int totalOldCount)
        {
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
            
            _selectedCount = selectedCount;
            _totalOldCount = totalOldCount;
            
            var message = $"Archive {selectedCount} selected old version package(s)?\n\n" +
                         $"These packages will be moved to:\n" +
                         $"{destinationPath}\n\n" +
                         $"Do you want to continue?";
            
            MessageTextBlock.Text = message;
            
            // Update Archive All button text
            if (totalOldCount > selectedCount)
            {
                ArchiveAllButton.Content = $"Archive All Old ({totalOldCount})";
                ArchiveAllButton.ToolTip = $"Archive all {totalOldCount} old version packages";
            }
            else
            {
                ArchiveAllButton.Visibility = Visibility.Collapsed;
            }
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
            ArchiveAll = false;
            DialogResult = true;
            Close();
        }

        private void ArchiveAllButton_Click(object sender, RoutedEventArgs e)
        {
            ArchiveAll = true;
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