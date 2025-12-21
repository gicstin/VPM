using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace VPM.Services
{
    /// <summary>
    /// Helper for applying dark title bar to WPF windows.
    /// Consolidates duplicated DwmSetWindowAttribute code from multiple windows.
    /// </summary>
    public static class DarkTitleBarHelper
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        /// <summary>
        /// Applies dark title bar to the specified window.
        /// Safe to call - silently fails if not supported.
        /// </summary>
        public static void Apply(Window window)
        {
            if (window == null) return;

            try
            {
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                if (hwnd != IntPtr.Zero)
                {
                    int value = 1;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
                    }
                }
            }
            catch
            {
                // Dark mode not available on this system
            }
        }

        public static bool IsDarkModeEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int intValue)
                    return intValue == 0;
                if (value is byte byteValue)
                    return byteValue == 0;
            }
            catch
            {
            }

            return false;
        }

        public static void ApplyIfDark(Window window)
        {
            if (!IsDarkModeEnabled())
                return;

            Apply(window);
        }

        /// <summary>
        /// Applies dark title bar using an existing window handle.
        /// </summary>
        public static void Apply(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            try
            {
                int value = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
                }
            }
            catch
            {
                // Dark mode not available on this system
            }
        }
    }
}
