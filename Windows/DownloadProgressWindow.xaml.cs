using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace VPM.Windows
{
    public partial class DownloadProgressWindow : Window
    {
        private readonly ObservableCollection<DownloadItemViewModel> _downloadItems;
        private CancellationTokenSource _cancellationTokenSource;
        private int _completedCount = 0;
        private int _totalCount = 0;
        private bool _allCompleted = false;
        
        // Event to notify when download count changes
        public event EventHandler DownloadCountChanged;

        public DownloadProgressWindow()
        {
            InitializeComponent();
            _downloadItems = new ObservableCollection<DownloadItemViewModel>();
            ResetCancellationToken();
            DownloadItemsControl.ItemsSource = _downloadItems;
        }

        public CancellationToken CancellationToken => _cancellationTokenSource.Token;
        
        /// <summary>
        /// Resets the cancellation token for a new download session
        /// </summary>
        public void ResetCancellationToken()
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Adds a package to the download list
        /// </summary>
        public void AddPackage(string packageName)
        {
            Dispatcher.Invoke(() =>
            {
                var item = new DownloadItemViewModel
                {
                    PackageName = packageName,
                    StatusText = "Queued",
                    StatusColor = Brushes.Gray,
                    Progress = 0,
                    ProgressText = "Waiting to start..."
                };
                
                _downloadItems.Add(item);
                _totalCount++;
                UpdateSummary();
                UpdateCancelButtonVisibility();
                DownloadCountChanged?.Invoke(this, EventArgs.Empty);
            });
        }

        /// <summary>
        /// Gets the count of active downloads (queued or downloading)
        /// </summary>
        public int GetActiveDownloadCount()
        {
            return Dispatcher.Invoke(() =>
            {
                return _downloadItems.Count(d => 
                    d.StatusText == "Queued" || 
                    d.StatusText == "Downloading");
            });
        }
        
        /// <summary>
        /// Checks if a package has been cancelled by the user
        /// </summary>
        public bool IsPackageCancelled(string packageName)
        {
            return Dispatcher.Invoke(() =>
            {
                var item = _downloadItems.FirstOrDefault(d => d.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                
                if (item == null)
                {
                    var baseName = GetBaseName(packageName);
                    item = _downloadItems.FirstOrDefault(d => 
                        d.PackageName.Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
                        GetBaseName(d.PackageName).Equals(baseName, StringComparison.OrdinalIgnoreCase));
                }
                
                return item?.StatusText == "Cancelled";
            });
        }
        
        /// <summary>
        /// Updates the progress of a package download
        /// </summary>
        public void UpdateProgress(string packageName, long downloadedBytes, long totalBytes, int percentage, string downloadSource = null)
        {
            Dispatcher.Invoke(() =>
            {
                // Try exact match first, then try matching base name
                var item = _downloadItems.FirstOrDefault(d => d.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                
                if (item == null)
                {
                    var baseName = GetBaseName(packageName);
                    item = _downloadItems.FirstOrDefault(d => 
                        d.PackageName.Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
                        GetBaseName(d.PackageName).Equals(baseName, StringComparison.OrdinalIgnoreCase));
                }
                
                if (item != null)
                {
                    // If item is cancelled, ignore progress updates
                    if (item.StatusText == "Cancelled")
                    {
                        return;
                    }
                    
                    item.StatusText = "Downloading";
                    item.StatusColor = new SolidColorBrush(Color.FromRgb(3, 169, 244)); // Light blue
                    
                    var mbDownloaded = downloadedBytes / (1024.0 * 1024.0);
                    var sourceText = !string.IsNullOrEmpty(downloadSource) ? $" (*{downloadSource})" : "";
                    item.ProgressText = $"{mbDownloaded:F1} MB downloaded...{sourceText}";
                    
                    DownloadCountChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        /// <summary>
        /// Marks a package as completed
        /// </summary>
        public void MarkCompleted(string packageName, bool success, string message = null)
        {
            Dispatcher.Invoke(() =>
            {
                // Try exact match first, then try matching base name (without version)
                var item = _downloadItems.FirstOrDefault(d => d.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                
                if (item == null)
                {
                    // Try matching without version suffix (e.g., "Oronan.F12" matches "Oronan.F12.1")
                    var baseName = GetBaseName(packageName);
                    item = _downloadItems.FirstOrDefault(d => 
                        d.PackageName.Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
                        GetBaseName(d.PackageName).Equals(baseName, StringComparison.OrdinalIgnoreCase));
                }
                
                if (item != null)
                {
                    // If item is already cancelled, ignore completion events
                    if (item.StatusText == "Cancelled")
                    {
                        // Download was cancelled by user, ignore this completion event
                        return;
                    }
                    
                    if (success)
                    {
                        item.StatusText = "✓ Completed";
                        item.StatusColor = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                        item.ProgressText = message ?? "Download completed successfully";
                    }
                    else
                    {
                        item.StatusText = "✗ Failed";
                        item.StatusColor = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
                        item.ProgressText = message ?? "Download failed";
                    }
                    
                    // Hide cancel button when completed/failed
                    item.CancelButtonVisibility = Visibility.Collapsed;
                    
                    _completedCount++;
                    UpdateSummary();
                    UpdateCancelButtonVisibility();
                    DownloadCountChanged?.Invoke(this, EventArgs.Empty);
                    
                    // Check if all downloads are complete
                    if (_completedCount >= _totalCount)
                    {
                        OnAllDownloadsCompleted();
                    }
                }
            });
        }
        
        /// <summary>
        /// Gets the base name of a package (without version number)
        /// </summary>
        private string GetBaseName(string packageName)
        {
            // Remove .var extension if present
            if (packageName.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
            {
                packageName = packageName.Substring(0, packageName.Length - 4);
            }
            
            // Find last dot followed by a number
            var lastDot = packageName.LastIndexOf('.');
            if (lastDot > 0 && lastDot < packageName.Length - 1)
            {
                var afterDot = packageName.Substring(lastDot + 1);
                if (int.TryParse(afterDot, out _))
                {
                    return packageName.Substring(0, lastDot);
                }
            }
            
            return packageName;
        }

        /// <summary>
        /// Marks a package as cancelled
        /// </summary>
        public void MarkCancelled(string packageName)
        {
            Dispatcher.Invoke(() =>
            {
                var item = _downloadItems.FirstOrDefault(d => d.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                if (item != null && item.StatusText != "✓ Completed" && item.StatusText != "✗ Failed")
                {
                    item.StatusText = "Cancelled";
                    item.StatusColor = Brushes.Orange;
                    item.ProgressText = "Download cancelled";
                    item.CancelButtonVisibility = Visibility.Collapsed;
                    
                    _completedCount++;
                    UpdateSummary();
                }
            });
        }

        private void UpdateSummary()
        {
            var successCount = _downloadItems.Count(d => d.StatusText == "✓ Completed");
            var failedCount = _downloadItems.Count(d => d.StatusText == "✗ Failed");
            var cancelledCount = _downloadItems.Count(d => d.StatusText == "Cancelled");
            var downloadingCount = _downloadItems.Count(d => d.StatusText == "Downloading");
            
            if (downloadingCount > 0)
            {
                SummaryText.Text = $"Downloading {downloadingCount} package(s)... ({_completedCount} / {_totalCount} completed)";
            }
            else if (_completedCount >= _totalCount && _totalCount > 0)
            {
                SummaryText.Text = $"All downloads completed! ({successCount} successful, {failedCount} failed, {cancelledCount} cancelled)";
            }
            else
            {
                SummaryText.Text = $"Preparing downloads... ({_completedCount} / {_totalCount} completed)";
            }
        }
        
        private void UpdateCancelButtonVisibility()
        {
            // Show Cancel All button only if there are packages and some are in progress
            var hasActiveDownloads = _downloadItems.Any(d => 
                d.StatusText == "Queued" || 
                d.StatusText == "Downloading");
            
            // Check if there are any completed/failed items (not just cancelled)
            var hasCompletedOrFailed = _downloadItems.Any(d => 
                d.StatusText == "✓ Completed" || 
                d.StatusText == "✗ Failed");
            
            // Show button if: active downloads OR (all completed AND has completed/failed items, not just cancelled)
            CancelAllButton.Visibility = (_downloadItems.Count > 0 && (hasActiveDownloads || (_allCompleted && hasCompletedOrFailed))) 
                ? Visibility.Visible 
                : Visibility.Collapsed;
        }

        private void OnAllDownloadsCompleted()
        {
            _allCompleted = true;
            CancelAllButton.Content = "Clear Completed";
            CancelAllButton.IsEnabled = true;
            UpdateCancelButtonVisibility();
        }

        private void CancelAllButton_Click(object sender, RoutedEventArgs e)
        {
            // Check if button says "Clear Completed" (all downloads done)
            if (CancelAllButton.Content.ToString() == "Clear Completed")
            {
                // Clear all completed/failed/cancelled items
                var itemsToRemove = _downloadItems.Where(d => 
                    d.StatusText == "✓ Completed" || 
                    d.StatusText == "✗ Failed" || 
                    d.StatusText == "Cancelled").ToList();
                
                foreach (var item in itemsToRemove)
                {
                    _downloadItems.Remove(item);
                    _totalCount--;
                    _completedCount--;
                }
                
                UpdateSummary();
                UpdateCancelButtonVisibility();
                
                // Reset button state
                if (_downloadItems.Count == 0)
                {
                    // No items left, reset to initial state
                    CancelAllButton.Content = "Cancel All";
                    CancelAllButton.IsEnabled = true;
                    _allCompleted = false;
                    _completedCount = 0;
                    _totalCount = 0;
                }
                else
                {
                    // Reset button if there are still active downloads
                    CancelAllButton.Content = "Cancel All";
                    _allCompleted = false;
                }
            }
            else
            {
                // Cancel all downloads
                var result = VPM.CustomMessageBox.Show(
                    "Are you sure you want to cancel all downloads?",
                    "Cancel Downloads",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    _cancellationTokenSource.Cancel();
                    
                    // Mark all non-completed items as cancelled
                    foreach (var item in _downloadItems.Where(d => d.StatusText != "✓ Completed" && d.StatusText != "✗ Failed"))
                    {
                        MarkCancelled(item.PackageName);
                    }
                    
                    CancelAllButton.IsEnabled = false;
                }
            }
        }
        
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // Double-click to maximize/restore
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                // Single click to drag
                DragMove();
            }
        }
        
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }
        
        private void WindowCloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        
        private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string packageName)
            {
                // Find the item
                var item = _downloadItems.FirstOrDefault(d => d.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    // Mark as cancelled (don't actually cancel the download token)
                    // Just update the UI - the download will be ignored when it tries to complete
                    item.StatusText = "Cancelled";
                    item.StatusColor = Brushes.Orange;
                    item.ProgressText = "Download cancelled by user";
                    
                    // Hide the cancel button - no retry option
                    item.CancelButtonVisibility = Visibility.Collapsed;
                    
                    _completedCount++;
                    UpdateSummary();
                    UpdateCancelButtonVisibility();
                    DownloadCountChanged?.Invoke(this, EventArgs.Empty);
                    
                    // Check if all downloads are complete
                    if (_completedCount >= _totalCount)
                    {
                        OnAllDownloadsCompleted();
                    }
                    
                    // Note: We don't cancel the token here because that would cancel ALL downloads
                    // Individual download cancellation would require per-item cancellation tokens
                }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Just hide the window, don't show popup
            // Downloads continue in background
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// View model for a single download item
    /// </summary>
    public class DownloadItemViewModel : INotifyPropertyChanged
    {
        private string _packageName;
        private string _statusText;
        private Brush _statusColor;
        private double _progress;
        private string _progressText;
        private Visibility _cancelButtonVisibility = Visibility.Visible;
        private string _cancelButtonText = "✓";
        private Brush _cancelButtonColor = new SolidColorBrush(Color.FromRgb(196, 43, 28)); // Red
        private string _cancelButtonTooltip = "Cancel download";

        public event PropertyChangedEventHandler PropertyChanged;

        public string PackageName
        {
            get => _packageName;
            set => SetProperty(ref _packageName, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public Brush StatusColor
        {
            get => _statusColor;
            set => SetProperty(ref _statusColor, value);
        }

        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        public string ProgressText
        {
            get => _progressText;
            set => SetProperty(ref _progressText, value);
        }
        
        public Visibility CancelButtonVisibility
        {
            get => _cancelButtonVisibility;
            set => SetProperty(ref _cancelButtonVisibility, value);
        }
        
        public string CancelButtonText
        {
            get => _cancelButtonText;
            set => SetProperty(ref _cancelButtonText, value);
        }
        
        public Brush CancelButtonColor
        {
            get => _cancelButtonColor;
            set => SetProperty(ref _cancelButtonColor, value);
        }
        
        public string CancelButtonTooltip
        {
            get => _cancelButtonTooltip;
            set => SetProperty(ref _cancelButtonTooltip, value);
        }

        protected virtual bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(backingStore, value))
                return false;

            backingStore = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

