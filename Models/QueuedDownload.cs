using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;

namespace VPM.Models
{
    /// <summary>
    /// Represents a download in the queue
    /// </summary>
    public class QueuedDownload : INotifyPropertyChanged
    {
        private string _packageName;
        private DownloadStatus _status;
        private long _downloadedBytes;
        private long _totalBytes;
        private int _progressPercentage;
        private string _errorMessage;
        private string _downloadSource;
        
        public event PropertyChangedEventHandler PropertyChanged;
        
        public string PackageName
        {
            get => _packageName;
            set => SetProperty(ref _packageName, value);
        }
        
        public PackageDownloadInfo DownloadInfo { get; set; }
        
        public DownloadStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }
        
        public long DownloadedBytes
        {
            get => _downloadedBytes;
            set
            {
                if (SetProperty(ref _downloadedBytes, value))
                {
                    OnPropertyChanged(nameof(ProgressText));
                }
            }
        }
        
        public long TotalBytes
        {
            get => _totalBytes;
            set
            {
                if (SetProperty(ref _totalBytes, value))
                {
                    OnPropertyChanged(nameof(ProgressText));
                }
            }
        }
        
        public int ProgressPercentage
        {
            get => _progressPercentage;
            set => SetProperty(ref _progressPercentage, value);
        }
        
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }
        
        public string DownloadSource
        {
            get => _downloadSource;
            set
            {
                if (SetProperty(ref _downloadSource, value))
                {
                    OnPropertyChanged(nameof(ProgressText));
                }
            }
        }
        
        public DateTime QueuedTime { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        
        public CancellationTokenSource CancellationTokenSource { get; set; }
        
        // Download URL and destination for queue processing
        public string DownloadUrl { get; set; }
        public string DestinationPath { get; set; }
        
        /// <summary>
        /// Whether this download can be cancelled (queued or downloading)
        /// </summary>
        public bool CanCancel => Status == DownloadStatus.Queued || Status == DownloadStatus.Downloading;
        
        /// <summary>
        /// Cancel this download
        /// </summary>
        public void Cancel()
        {
            CancellationTokenSource?.Cancel();
            Status = DownloadStatus.Cancelled;
        }
        
        // Display properties
        public string StatusText
        {
            get
            {
                return Status switch
                {
                    DownloadStatus.Queued => "Queued",
                    DownloadStatus.Downloading => "Downloading",
                    DownloadStatus.Completed => "Completed",
                    DownloadStatus.Failed => "Failed",
                    DownloadStatus.Cancelled => "Cancelled",
                    _ => "Unknown"
                };
            }
        }
        
        public string StatusColor
        {
            get
            {
                return Status switch
                {
                    DownloadStatus.Queued => "#FFA500",      // Orange
                    DownloadStatus.Downloading => "#03A9F4", // Blue
                    DownloadStatus.Completed => "#4CAF50",   // Green
                    DownloadStatus.Failed => "#F44336",      // Red
                    DownloadStatus.Cancelled => "#9E9E9E",   // Gray
                    _ => "#9E9E9E"
                };
            }
        }
        
        public string ProgressText
        {
            get
            {
                if (Status == DownloadStatus.Downloading && TotalBytes > 0)
                {
                    var downloadedMB = DownloadedBytes / (1024.0 * 1024.0);
                    var totalMB = TotalBytes / (1024.0 * 1024.0);
                    var sourceText = !string.IsNullOrEmpty(DownloadSource) ? $" (*{DownloadSource})" : "";
                    return $"{downloadedMB:F1} / {totalMB:F1} MB ({ProgressPercentage}%){sourceText}";
                }
                else if (Status == DownloadStatus.Queued)
                {
                    return "Waiting in queue...";
                }
                else if (Status == DownloadStatus.Completed)
                {
                    return "Download completed";
                }
                else if (Status == DownloadStatus.Failed)
                {
                    return $"Failed: {ErrorMessage}";
                }
                else if (Status == DownloadStatus.Cancelled)
                {
                    return "Download cancelled";
                }
                
                return "";
            }
        }
        
        protected virtual bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(backingStore, value))
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
    
    /// <summary>
    /// Download status enumeration
    /// </summary>
    public enum DownloadStatus
    {
        Queued,
        Downloading,
        Completed,
        Failed,
        Cancelled
    }
}

