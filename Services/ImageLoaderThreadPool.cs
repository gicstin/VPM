using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SharpCompress.Archives;
using VPM.Models;
using Microsoft.IO;

namespace VPM.Services
{
    /// <summary>
    /// Dedicated image worker thread pool for high-performance image loading
    /// Refactored to use async/await pattern (Task-based) instead of dedicated threads
    /// </summary>
    public class ImageLoaderThreadPool : IDisposable
    {
        private readonly ImageLoaderAsyncPool _asyncPool;

        public event Action<int, int> ProgressChanged
        {
            add => _asyncPool.ProgressChanged += value;
            remove => _asyncPool.ProgressChanged -= value;
        }

        public event Action<QueuedImage> ImageProcessed
        {
            add => _asyncPool.ImageProcessed += value;
            remove => _asyncPool.ImageProcessed -= value;
        }

        public ImageLoaderThreadPool(int workerCount = 0)
        {
            _asyncPool = new ImageLoaderAsyncPool(workerCount);
        }

        public void QueueImage(QueuedImage qi)
        {
            _asyncPool.QueueImage(qi);
        }

        public void QueueThumbnail(QueuedImage qi)
        {
            _asyncPool.QueueThumbnail(qi);
        }

        public void ClearQueues()
        {
            _asyncPool.ClearQueues();
        }

        public (int thumbnails, int images, int total) GetQueueSizes()
        {
            return _asyncPool.GetQueueSizes();
        }

        public (int current, int total) GetProgress()
        {
            return _asyncPool.GetProgress();
        }

        public void TrackTexture(BitmapImage texture)
        {
            _asyncPool.TrackTexture(texture);
        }

        public bool RegisterTextureUse(BitmapImage texture)
        {
            return _asyncPool.RegisterTextureUse(texture);
        }

        public bool DeregisterTextureUse(BitmapImage texture)
        {
            return _asyncPool.DeregisterTextureUse(texture);
        }

        public (int totalChecks, int rejections, double rejectionRate) GetValidationMetrics()
        {
            return _asyncPool.GetValidationMetrics();
        }

        public void ResetValidationMetrics()
        {
            _asyncPool.ResetValidationMetrics();
        }

        public void Dispose()
        {
            _asyncPool.Dispose();
        }
    }
}
