using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VPM
{
    /// <summary>DataGrid gripper double-click sets Width=Auto. Star columns auto-size to 0px and the gripper disappears, so they cannot be expanded.</summary>
    internal static class DataGridGripperAutoSize
    {
        public static void SuppressIfGripperDoubleClick(MouseButtonEventArgs e)
        {
            if (e == null || e.ClickCount != 2)
                return;

            var thumb = FindParent<Thumb>(e.OriginalSource as DependencyObject);
            if (thumb == null)
                return;

            var name = thumb.Name ?? string.Empty;
            if (name.IndexOf("Gripper", StringComparison.OrdinalIgnoreCase) < 0 &&
                thumb.Cursor != Cursors.SizeWE)
                return;

            e.Handled = true;
        }

        private static T FindParent<T>(DependencyObject obj) where T : DependencyObject
        {
            while (obj != null)
            {
                if (obj is T match)
                    return match;
                obj = VisualTreeHelper.GetParent(obj);
            }
            return null;
        }
    }
}
