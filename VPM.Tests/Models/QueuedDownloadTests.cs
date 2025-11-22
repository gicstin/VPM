using Xunit;
using VPM.Models;
using System;
using System.Collections.Generic;

namespace VPM.Tests.Models
{
    public class QueuedDownloadTests
    {
        [Fact]
        public void Constructor_CreatesDefaultInstance()
        {
            var download = new QueuedDownload();

            Assert.NotNull(download);
            Assert.Null(download.PackageName);
            Assert.Equal(DownloadStatus.Queued, download.Status);
            Assert.Equal(0, download.DownloadedBytes);
            Assert.Equal(0, download.TotalBytes);
            Assert.Equal(0, download.ProgressPercentage);
        }

        [Fact]
        public void PackageName_SetAndGet_WorksCorrectly()
        {
            var download = new QueuedDownload();
            download.PackageName = "TestPackage";

            Assert.Equal("TestPackage", download.PackageName);
        }

        [Fact]
        public void PackageName_PropertyChanged_RaisesEvent()
        {
            var download = new QueuedDownload();
            bool eventRaised = false;

            download.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(QueuedDownload.PackageName))
                    eventRaised = true;
            };

            download.PackageName = "TestPackage";

            Assert.True(eventRaised);
        }

        [Fact]
        public void Status_SetAndGet_WorksCorrectly()
        {
            var download = new QueuedDownload();
            download.Status = DownloadStatus.Downloading;

            Assert.Equal(DownloadStatus.Downloading, download.Status);
        }

        [Fact]
        public void Status_Changed_RaisesStatusTextAndStatusColorPropertyChanged()
        {
            var download = new QueuedDownload();
            var changedProperties = new HashSet<string>();

            download.PropertyChanged += (s, e) =>
            {
                changedProperties.Add(e.PropertyName);
            };

            download.Status = DownloadStatus.Downloading;

            Assert.Contains(nameof(QueuedDownload.StatusText), changedProperties);
            Assert.Contains(nameof(QueuedDownload.StatusColor), changedProperties);
        }

        [Fact]
        public void DownloadedBytes_SetAndGet_WorksCorrectly()
        {
            var download = new QueuedDownload();
            download.DownloadedBytes = 1024;

            Assert.Equal(1024, download.DownloadedBytes);
        }

        [Fact]
        public void DownloadedBytes_Changed_RaisesProgressTextPropertyChanged()
        {
            var download = new QueuedDownload();
            download.Status = DownloadStatus.Downloading;
            download.TotalBytes = 2048;
            var changedProperties = new HashSet<string>();

            download.PropertyChanged += (s, e) =>
            {
                changedProperties.Add(e.PropertyName);
            };

            download.DownloadedBytes = 1024;

            Assert.Contains(nameof(QueuedDownload.ProgressText), changedProperties);
        }

        [Fact]
        public void TotalBytes_SetAndGet_WorksCorrectly()
        {
            var download = new QueuedDownload();
            download.TotalBytes = 5120;

            Assert.Equal(5120, download.TotalBytes);
        }

        [Fact]
        public void ProgressPercentage_SetAndGet_WorksCorrectly()
        {
            var download = new QueuedDownload();
            download.ProgressPercentage = 50;

            Assert.Equal(50, download.ProgressPercentage);
        }

        [Fact]
        public void ErrorMessage_SetAndGet_WorksCorrectly()
        {
            var download = new QueuedDownload();
            string errorMsg = "Connection timeout";
            download.ErrorMessage = errorMsg;

            Assert.Equal(errorMsg, download.ErrorMessage);
        }

        [Fact]
        public void DownloadSource_SetAndGet_WorksCorrectly()
        {
            var download = new QueuedDownload();
            download.DownloadSource = "Mirror1";

            Assert.Equal("Mirror1", download.DownloadSource);
        }

        [Fact]
        public void StatusText_QueuedStatus_ReturnsQueuedText()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Queued };

            Assert.Equal("Queued", download.StatusText);
        }

        [Fact]
        public void StatusText_DownloadingStatus_ReturnsDownloadingText()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Downloading };

            Assert.Equal("Downloading", download.StatusText);
        }

        [Fact]
        public void StatusText_CompletedStatus_ReturnsCompletedText()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Completed };

            Assert.Equal("Completed", download.StatusText);
        }

        [Fact]
        public void StatusText_FailedStatus_ReturnsFailedText()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Failed };

            Assert.Equal("Failed", download.StatusText);
        }

        [Fact]
        public void StatusText_CancelledStatus_ReturnsCancelledText()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Cancelled };

            Assert.Equal("Cancelled", download.StatusText);
        }

        [Fact]
        public void StatusColor_QueuedStatus_ReturnsOrangeColor()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Queued };

            Assert.Equal("#FFA500", download.StatusColor);
        }

        [Fact]
        public void StatusColor_DownloadingStatus_ReturnsBlueColor()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Downloading };

            Assert.Equal("#03A9F4", download.StatusColor);
        }

        [Fact]
        public void StatusColor_CompletedStatus_ReturnsGreenColor()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Completed };

            Assert.Equal("#4CAF50", download.StatusColor);
        }

        [Fact]
        public void StatusColor_FailedStatus_ReturnsRedColor()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Failed };

            Assert.Equal("#F44336", download.StatusColor);
        }

        [Fact]
        public void StatusColor_CancelledStatus_ReturnsGrayColor()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Cancelled };

            Assert.Equal("#9E9E9E", download.StatusColor);
        }

        [Fact]
        public void ProgressText_WhileDownloading_FormatsProgressCorrectly()
        {
            var download = new QueuedDownload
            {
                Status = DownloadStatus.Downloading,
                DownloadedBytes = 1024 * 1024,
                TotalBytes = 10 * 1024 * 1024,
                ProgressPercentage = 10
            };

            string progressText = download.ProgressText;

            Assert.Contains("1.0", progressText);
            Assert.Contains("10.0", progressText);
            Assert.Contains("10%", progressText);
        }

        [Fact]
        public void ProgressText_WhileDownloadingWithSource_IncludesSourceInfo()
        {
            var download = new QueuedDownload
            {
                Status = DownloadStatus.Downloading,
                DownloadedBytes = 512 * 1024,
                TotalBytes = 1024 * 1024,
                ProgressPercentage = 50,
                DownloadSource = "Mirror2"
            };

            string progressText = download.ProgressText;

            Assert.Contains("Mirror2", progressText);
        }

        [Fact]
        public void ProgressText_QueuedStatus_ReturnsWaitingText()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Queued };

            Assert.Contains("Waiting in queue", download.ProgressText);
        }

        [Fact]
        public void ProgressText_CompletedStatus_ReturnsCompletionText()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Completed };

            Assert.Equal("Download completed", download.ProgressText);
        }

        [Fact]
        public void ProgressText_FailedStatus_IncludesErrorMessage()
        {
            string errorMsg = "Network error";
            var download = new QueuedDownload
            {
                Status = DownloadStatus.Failed,
                ErrorMessage = errorMsg
            };

            Assert.Contains("Failed:", download.ProgressText);
            Assert.Contains(errorMsg, download.ProgressText);
        }

        [Fact]
        public void ProgressText_CancelledStatus_ReturnsCancelledText()
        {
            var download = new QueuedDownload { Status = DownloadStatus.Cancelled };

            Assert.Equal("Download cancelled", download.ProgressText);
        }

        [Fact]
        public void ProgressText_DownloadingWithNoTotal_ReturnsEmptyString()
        {
            var download = new QueuedDownload
            {
                Status = DownloadStatus.Downloading,
                DownloadedBytes = 100,
                TotalBytes = 0
            };

            Assert.Empty(download.ProgressText);
        }

        [Fact]
        public void QueuedTime_CanBeSet()
        {
            var download = new QueuedDownload();
            var now = DateTime.Now;
            download.QueuedTime = now;

            Assert.Equal(now, download.QueuedTime);
        }

        [Fact]
        public void StartTime_CanBeSetAndNullable()
        {
            var download = new QueuedDownload();
            Assert.Null(download.StartTime);

            var now = DateTime.Now;
            download.StartTime = now;

            Assert.Equal(now, download.StartTime);
        }

        [Fact]
        public void EndTime_CanBeSetAndNullable()
        {
            var download = new QueuedDownload();
            Assert.Null(download.EndTime);

            var now = DateTime.Now;
            download.EndTime = now;

            Assert.Equal(now, download.EndTime);
        }

        [Fact]
        public void CancellationTokenSource_CanBeSet()
        {
            var download = new QueuedDownload();
            var cts = new System.Threading.CancellationTokenSource();
            download.CancellationTokenSource = cts;

            Assert.Equal(cts, download.CancellationTokenSource);
        }

        [Fact]
        public void DownloadInfo_CanBeSet()
        {
            var download = new QueuedDownload();
            var downloadInfo = new PackageDownloadInfo();
            download.DownloadInfo = downloadInfo;

            Assert.Equal(downloadInfo, download.DownloadInfo);
        }

        [Fact]
        public void ProgressText_DownloadingWithoutSource_NoSourceInText()
        {
            var download = new QueuedDownload
            {
                Status = DownloadStatus.Downloading,
                DownloadedBytes = 512 * 1024,
                TotalBytes = 1024 * 1024,
                ProgressPercentage = 50,
                DownloadSource = null
            };

            string progressText = download.ProgressText;

            Assert.DoesNotContain("(*", progressText);
        }

        [Fact]
        public void MultiplePropertyChanges_RaisesMultipleEvents()
        {
            var download = new QueuedDownload();
            int eventCount = 0;

            download.PropertyChanged += (s, e) => eventCount++;

            download.PackageName = "Package1";
            download.Status = DownloadStatus.Downloading;
            download.ProgressPercentage = 50;

            Assert.True(eventCount >= 3);
        }
    }
}
