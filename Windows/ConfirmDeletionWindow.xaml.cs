using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace VPM
{
    public partial class ConfirmDeletionWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public ConfirmDeletionWindow(string title, string message, IEnumerable<string> details, string warningMessage)
        {
            InitializeComponent();

            Title = title;
            TitleTextBlock.Text = title;
            MessageTextBlock.Text = message;

            if (details != null)
            {
                DetailsItemsControl.ItemsSource = details.ToList();
            }
            else
            {
                DetailsItemsControl.ItemsSource = Array.Empty<string>();
            }

            if (string.IsNullOrWhiteSpace(warningMessage))
            {
                WarningTextBlock.Visibility = Visibility.Collapsed;
            }
            else
            {
                WarningTextBlock.Text = warningMessage;
                WarningTextBlock.Visibility = Visibility.Visible;
            }

            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
                int useImmersiveDarkMode = 1;
                // Try Windows 11/10 20H1+ attribute first, then fall back to older Windows 10 attribute
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
                }
            }
            catch
            {
                // Dark mode not available on this system
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
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

