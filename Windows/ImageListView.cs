using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VPM.Windows
{
    public class ImageListView : ListView
    {
        public ImageListView()
        {
            // Disable horizontal scrolling, enable vertical
            ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        }
    }
}
