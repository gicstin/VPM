using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using VPM.Services;

namespace VPM
{
    public partial class ConfirmArchiveWindow : Window
    {
        public bool ArchiveAll { get; private set; } = false;
        private int _selectedCount;
        private int _totalOldCount;

        public ConfirmArchiveWindow(int selectedCount, string destinationPath, int totalOldCount)
        {
            InitializeComponent();
            Loaded += (s, e) => DarkTitleBarHelper.Apply(this);
            
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

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }
    }
}