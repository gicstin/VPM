using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPM.Models;

namespace VPM.Services
{
    /// <summary>
    /// Manages a queue of package downloads with concurrent download support
    /// </summary>
    public class DownloadQueueManager : IDisposable
    {
        private readonly PackageDownloader _downloader;
        private readonly string _destinationFolder;
        private readonly Queue<QueuedDownload> _downloadQueue;
        private readonly ConcurrentDictionary<string, QueuedDownload> _activeDownloads;
        private readonly SemaphoreSlim _downloadSemaphore;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private Task _processingTask;
        private readonly int _maxConcurrentDownloads;
        private readonly SemaphoreSlim _queueLock;
        private readonly HashSet<string> _pendingRemoval;
        
        public event EventHandler<QueueStatusChangedEventArgs> QueueStatusChanged;
        public event EventHandler<DownloadQueuedEventArgs> DownloadQueued;
        public event EventHandler<DownloadStartedEventArgs> DownloadStarted;
        public event EventHandler<DownloadRemovedEventArgs> DownloadRemoved;
        
        /// <summary>
        /// Gets the number of items currently in the queue (not including active downloads)
        /// </summary>
        public int QueuedCount
        {
            get
            {
                var snapshot = GetQueueSnapshot();
                return snapshot.Length;
            }
        }
        
        /// <summary>
        /// Gets the number of active downloads
        /// </summary>
        public int ActiveCount => _activeDownloads.Count;
        
        /// <summary>
        /// Gets all queued downloads (not including active ones)
        /// </summary>
        public IEnumerable<QueuedDownload> QueuedDownloads => GetQueueSnapshot();
        
        /// <summary>
        /// Gets all active downloads
        /// </summary>
        public IEnumerable<QueuedDownload> ActiveDownloads => _activeDownloads.Values.ToArray();
        
        public DownloadQueueManager(PackageDownloader downloader, string destinationFolder = null, int maxConcurrentDownloads = 2)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _destinationFolder = destinationFolder;
            _maxConcurrentDownloads = Math.Max(1, maxConcurrentDownloads);
            _downloadQueue = new Queue<QueuedDownload>();
            _activeDownloads = new ConcurrentDictionary<string, QueuedDownload>();
            _downloadSemaphore = new SemaphoreSlim(_maxConcurrentDownloads, _maxConcurrentDownloads);
            _cancellationTokenSource = new CancellationTokenSource();
            _queueLock = new SemaphoreSlim(1, 1);
            _pendingRemoval = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            // Subscribe to downloader events
            _downloader.DownloadProgress += OnDownloadProgress;
            _downloader.DownloadCompleted += OnDownloadCompleted;
            _downloader.DownloadError += OnDownloadError;
            
            // Start processing queue with cancellation token
            // FIXED: Pass cancellation token to Task.Run to ensure proper cancellation propagation
            _processingTask = Task.Run(ProcessQueueAsync, _cancellationTokenSource.Token);
        }
        
        /// <summary>
        /// Adds a package to the download queue
        /// </summary>
        public bool EnqueueDownload(string packageName, PackageDownloadInfo downloadInfo)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return false;
            
            // Check if already queued or downloading
            if (_activeDownloads.ContainsKey(packageName))
            {
                return false;
            }

            var queuedDownload = new QueuedDownload
            {
                PackageName = packageName,
                DownloadInfo = downloadInfo,
                QueuedTime = DateTime.Now,
                Status = DownloadStatus.Queued
            };

            int queueSize;
            if (!_queueLock.Wait(TimeSpan.FromSeconds(5)))
            {
                return false;
            }
            try
            {
                if (_downloadQueue.Any(d => d.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                _pendingRemoval.Remove(packageName);
                _downloadQueue.Enqueue(queuedDownload);
                queueSize = _downloadQueue.Count;
            }
            finally
            {
                _queueLock.Release();
            }
            
            OnDownloadQueued(queuedDownload);
            OnQueueStatusChanged();
            
            return true;
        }
        
        /// <summary>
        /// Removes a package from the queue (if not yet started)
        /// </summary>
        public bool RemoveFromQueue(string packageName)
        {
            // Can't remove if actively downloading
            if (_activeDownloads.ContainsKey(packageName))
            {
                return false;
            }
            
            QueuedDownload removed = null;
            if (!_queueLock.Wait(TimeSpan.FromSeconds(5)))
            {
                return false;
            }
            try
            {
                removed = _downloadQueue.FirstOrDefault(d => d.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                if (removed == null)
                {
                    return false;
                }

                _pendingRemoval.Add(packageName);
            }
            finally
            {
                _queueLock.Release();
            }

            removed.Status = DownloadStatus.Cancelled;
            OnDownloadRemoved(removed);
            OnQueueStatusChanged();

            return true;
        }
        
        /// <summary>
        /// Cancels an active download
        /// </summary>
        public bool CancelDownload(string packageName)
        {
            if (_activeDownloads.TryGetValue(packageName, out var download))
            {
                download.CancellationTokenSource?.Cancel();
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Clears all queued downloads (does not cancel active downloads)
        /// </summary>
        public void ClearQueue()
        {
            int count;
            if (!_queueLock.Wait(TimeSpan.FromSeconds(5)))
            {
                return;
            }
            try
            {
                count = _downloadQueue.Count;
                _downloadQueue.Clear();
                _pendingRemoval.Clear();
            }
            finally
            {
                _queueLock.Release();
            }
            
            OnQueueStatusChanged();
        }
        
        /// <summary>
        /// Processes the download queue
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Wait for available slot
                    await _downloadSemaphore.WaitAsync(_cancellationTokenSource.Token);
                    
                    QueuedDownload queuedDownload = null;
                    var queueEmpty = false;

                    while (queuedDownload == null && !_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        await _queueLock.WaitAsync(_cancellationTokenSource.Token);
                        try
                        {
                            if (_downloadQueue.Count == 0)
                            {
                                queueEmpty = true;
                                break;
                            }

                            var candidate = _downloadQueue.Dequeue();
                            if (_pendingRemoval.Remove(candidate.PackageName))
                            {
                                continue;
                            }

                            queuedDownload = candidate;
                        }
                        finally
                        {
                            _queueLock.Release();
                        }
                    }

                    if (queuedDownload == null)
                    {
                        _downloadSemaphore.Release();
                        if (queueEmpty)
                        {
                            await Task.Delay(500, _cancellationTokenSource.Token);
                        }
                        continue;
                    }

                    if (!_activeDownloads.TryAdd(queuedDownload.PackageName, queuedDownload))
                    {
                        _downloadSemaphore.Release();
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessDownloadAsync(queuedDownload);
                        }
                        finally
                        {
                            _downloadSemaphore.Release();
                        }
                    }, _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(1000);
                }
            }
        }
        
        /// <summary>
        /// Processes a single download
        /// </summary>
        private async Task ProcessDownloadAsync(QueuedDownload queuedDownload)
        {
            var packageName = queuedDownload.PackageName;
            var cts = new CancellationTokenSource();
            queuedDownload.CancellationTokenSource = cts;
            queuedDownload.Status = DownloadStatus.Downloading;
            queuedDownload.StartTime = DateTime.Now;
            
            OnDownloadStarted(queuedDownload);
            OnQueueStatusChanged();
            
            try
            {
                bool success = false;
                
                // Check if we have a direct download URL (from Hub or links.txt)
                if (queuedDownload.DownloadInfo != null && !string.IsNullOrEmpty(queuedDownload.DownloadInfo.DownloadUrl))
                {
                    var downloadUrl = queuedDownload.DownloadInfo.DownloadUrl;
                    
                    // If it's a Hub URL, use HubService for download
                    if (downloadUrl.Contains("hub.virtamate.com", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var hubService = new HubService())
                        {
                            // Use provided destination folder, or fall back to app folder
                            var destinationFolder = _destinationFolder ?? System.IO.Path.Combine(
                                System.IO.Path.GetDirectoryName(_downloader.GetType().Assembly.Location),
                                "AddonPackages"
                            );
                            
                            // Create a progress handler to report download progress
                            var progress = new Progress<HubDownloadProgress>(hubProgress =>
                            {
                                if (hubProgress.IsDownloading)
                                {
                                    // Convert HubDownloadProgress to PackageDownloader format
                                    var percentage = (int)(hubProgress.Progress * 100);
                                    _downloader.FireDownloadProgress(
                                        packageName,
                                        hubProgress.DownloadedBytes,
                                        hubProgress.TotalBytes,
                                        percentage,
                                        "Hub"
                                    );
                                }
                            });
                            
                            success = await hubService.DownloadPackageAsync(
                                downloadUrl,
                                destinationFolder,
                                packageName,
                                progress,
                                cts.Token
                            );
                            
                            // Fire the completion event so UI gets updated
                            if (success)
                            {
                                var fileName = System.IO.Path.Combine(destinationFolder, packageName + ".var");
                                _downloader.FireDownloadCompleted(packageName, fileName, false);
                            }
                        }
                    }
                    else
                    {
                        // For other URLs (local links.txt), use PackageDownloader
                        success = await _downloader.DownloadPackageAsync(packageName, cts.Token);
                    }
                }
                else
                {
                    // Fallback to original method (package in downloader cache)
                    success = await _downloader.DownloadPackageAsync(packageName, cts.Token);
                }
                
                if (success)
                {
                    queuedDownload.Status = DownloadStatus.Completed;
                }
                else
                {
                    queuedDownload.Status = DownloadStatus.Failed;
                }
            }
            catch (OperationCanceledException)
            {
                queuedDownload.Status = DownloadStatus.Cancelled;
            }
            catch (Exception ex)
            {
                queuedDownload.Status = DownloadStatus.Failed;
                queuedDownload.ErrorMessage = ex.Message;
            }
            finally
            {
                queuedDownload.EndTime = DateTime.Now;
                
                // Remove from active downloads
                _activeDownloads.TryRemove(packageName, out _);
                
                OnQueueStatusChanged();
                
                cts?.Dispose();
            }
        }

        private QueuedDownload[] GetQueueSnapshot()
        {
            if (!_queueLock.Wait(TimeSpan.FromSeconds(5)))
            {
                return Array.Empty<QueuedDownload>();
            }
            try
            {
                if (_downloadQueue.Count == 0)
                {
                    return Array.Empty<QueuedDownload>();
                }

                if (_pendingRemoval.Count == 0)
                {
                    return _downloadQueue.ToArray();
                }

                return _downloadQueue
                    .Where(d => !_pendingRemoval.Contains(d.PackageName))
                    .ToArray();
            }
            finally
            {
                _queueLock.Release();
            }
        }
        
        private void OnDownloadProgress(object sender, DownloadProgressEventArgs e)
        {
            if (_activeDownloads.TryGetValue(e.PackageName, out var download))
            {
                download.DownloadedBytes = e.DownloadedBytes;
                download.TotalBytes = e.TotalBytes;
                download.ProgressPercentage = e.ProgressPercentage;
                download.DownloadSource = e.DownloadSource;
            }
        }
        
        private void OnDownloadCompleted(object sender, DownloadCompletedEventArgs e)
        {
            // Status will be updated in ProcessDownloadAsync
        }
        
        private void OnDownloadError(object sender, DownloadErrorEventArgs e)
        {
            if (_activeDownloads.TryGetValue(e.PackageName, out var download))
            {
                download.ErrorMessage = e.ErrorMessage;
            }
        }
        
        protected virtual void OnQueueStatusChanged()
        {
            QueueStatusChanged?.Invoke(this, new QueueStatusChangedEventArgs
            {
                QueuedCount = QueuedCount,
                ActiveCount = ActiveCount
            });
        }
        
        protected virtual void OnDownloadQueued(QueuedDownload download)
        {
            DownloadQueued?.Invoke(this, new DownloadQueuedEventArgs { Download = download });
        }
        
        protected virtual void OnDownloadStarted(QueuedDownload download)
        {
            DownloadStarted?.Invoke(this, new DownloadStartedEventArgs { Download = download });
        }
        
        protected virtual void OnDownloadRemoved(QueuedDownload download)
        {
            DownloadRemoved?.Invoke(this, new DownloadRemovedEventArgs { Download = download });
        }
        
        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            
            // Wait for processing task to complete with proper timeout handling
            if (_processingTask != null)
            {
                try
                {
                    _processingTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException) { /* Task was cancelled or faulted */ }
                catch (ObjectDisposedException) { /* Already disposed */ }
            }
            
            // Unsubscribe from events
            if (_downloader != null)
            {
                _downloader.DownloadProgress -= OnDownloadProgress;
                _downloader.DownloadCompleted -= OnDownloadCompleted;
                _downloader.DownloadError -= OnDownloadError;
            }
            
            _cancellationTokenSource?.Dispose();
            _downloadSemaphore?.Dispose();
            _queueLock?.Dispose();
        }
    }
    
    #region Event Args
    
    public class QueueStatusChangedEventArgs : EventArgs
    {
        public int QueuedCount { get; set; }
        public int ActiveCount { get; set; }
    }
    
    public class DownloadQueuedEventArgs : EventArgs
    {
        public QueuedDownload Download { get; set; }
    }
    
    public class DownloadStartedEventArgs : EventArgs
    {
        public QueuedDownload Download { get; set; }
    }
    
    public class DownloadRemovedEventArgs : EventArgs
    {
        public QueuedDownload Download { get; set; }
    }
    
    #endregion
}

