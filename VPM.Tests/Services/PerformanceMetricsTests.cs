using Xunit;
using VPM.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace VPM.Tests.Services
{
    public class PerformanceMetricsTests
    {
        [Fact]
        public void RecordOperation_SingleOperation_RecordsMetricsCorrectly()
        {
            var metrics = new PerformanceMetrics();
            var startTime = DateTime.UtcNow;
            var endTime = startTime.AddSeconds(2);

            metrics.RecordOperation("TestOp", startTime, endTime, 1024000, 10, "Success");

            var aggregated = metrics.GetAggregatedMetrics("TestOp");
            Assert.NotNull(aggregated);
            Assert.Equal(1, aggregated.OperationCount);
            Assert.Equal(1024000, aggregated.TotalBytesProcessed);
            Assert.Equal(10, aggregated.TotalItemsProcessed);
            Assert.Equal(1, aggregated.SuccessCount);
        }

        [Fact]
        public void RecordOperation_MultipleOperations_AggregatesCorrectly()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("Op1", now, now.AddSeconds(1), 1000, 5);
            metrics.RecordOperation("Op1", now.AddSeconds(2), now.AddSeconds(3), 2000, 10);
            metrics.RecordOperation("Op1", now.AddSeconds(4), now.AddSeconds(6), 3000, 15);

            var aggregated = metrics.GetAggregatedMetrics("Op1");
            Assert.Equal(3, aggregated.OperationCount);
            Assert.Equal(6000, aggregated.TotalBytesProcessed);
            Assert.Equal(30, aggregated.TotalItemsProcessed);
            Assert.Equal(3, aggregated.SuccessCount);
        }

        [Fact]
        public void AverageDuration_MultipleOperations_CalculatesCorrectly()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("Op1", now, now.AddSeconds(1), 0, 0);
            metrics.RecordOperation("Op1", now.AddSeconds(2), now.AddSeconds(4), 0, 0);
            metrics.RecordOperation("Op1", now.AddSeconds(5), now.AddSeconds(8), 0, 0);

            var aggregated = metrics.GetAggregatedMetrics("Op1");
            var avgMs = aggregated.AverageDuration.TotalMilliseconds;
            Assert.True(avgMs >= 1900 && avgMs <= 2100);
        }

        [Fact]
        public void RecordOperation_WithFailureStatus_TracksFailureCount()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("Op", now, now.AddSeconds(1), 0, 0, "Success");
            metrics.RecordOperation("Op", now.AddSeconds(1), now.AddSeconds(2), 0, 0, "Failure", "Test error");
            metrics.RecordOperation("Op", now.AddSeconds(2), now.AddSeconds(3), 0, 0, "Success");

            var aggregated = metrics.GetAggregatedMetrics("Op");
            Assert.Equal(3, aggregated.OperationCount);
            Assert.Equal(2, aggregated.SuccessCount);
            Assert.Equal(1, aggregated.FailureCount);
        }

        [Fact]
        public void SuccessRate_WithMixedResults_CalculatesPercentageCorrectly()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("Op", now, now.AddSeconds(1), 0, 0, "Success");
            metrics.RecordOperation("Op", now.AddSeconds(1), now.AddSeconds(2), 0, 0, "Success");
            metrics.RecordOperation("Op", now.AddSeconds(2), now.AddSeconds(3), 0, 0, "Failure");
            metrics.RecordOperation("Op", now.AddSeconds(3), now.AddSeconds(4), 0, 0, "Failure");

            var aggregated = metrics.GetAggregatedMetrics("Op");
            Assert.Equal(50.0, aggregated.SuccessRate);
        }

        [Fact]
        public void ThroughputMBps_WithKnownValues_CalculatesCorrectly()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            long tenMB = 10L * 1024 * 1024;
            metrics.RecordOperation("Download", now, now.AddSeconds(10), tenMB, 1);

            var aggregated = metrics.GetAggregatedMetrics("Download");
            Assert.True(aggregated.AverageThroughputMBps > 0.9 && aggregated.AverageThroughputMBps < 1.1);
        }

        [Fact]
        public void ItemsPerSecond_WithKnownValues_CalculatesCorrectly()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("Process", now, now.AddSeconds(5), 0, 100);

            var aggregated = metrics.GetAggregatedMetrics("Process");
            Assert.True(aggregated.AverageItemsPerSecond >= 19 && aggregated.AverageItemsPerSecond <= 21);
        }

        [Fact]
        public void GetAggregatedMetrics_NonExistentOperation_ReturnsNull()
        {
            var metrics = new PerformanceMetrics();

            var result = metrics.GetAggregatedMetrics("NonExistent");

            Assert.Null(result);
        }

        [Fact]
        public void GetAllAggregatedMetrics_MultipleOperationTypes_ReturnsAllInOrder()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("OpA", now, now.AddSeconds(1), 0, 0);
            metrics.RecordOperation("OpB", now, now.AddSeconds(3), 0, 0);
            metrics.RecordOperation("OpC", now, now.AddSeconds(2), 0, 0);

            var all = metrics.GetAllAggregatedMetrics();
            Assert.Equal(3, all.Count);
            Assert.Equal("OpB", all[0].OperationType);
            Assert.Equal("OpC", all[1].OperationType);
            Assert.Equal("OpA", all[2].OperationType);
        }

        [Fact]
        public void RecordResourceSnapshot_CapturesSystemMetrics()
        {
            var metrics = new PerformanceMetrics();

            metrics.RecordResourceSnapshot();
            var snapshot = metrics.GetLatestResourceSnapshot();

            Assert.NotNull(snapshot);
            Assert.True(snapshot.MemoryUsedMB > 0);
            Assert.True(snapshot.ThreadCount > 0);
        }

        [Fact]
        public void GetLatestResourceSnapshot_WithMultipleSnapshots_ReturnsNewest()
        {
            var metrics = new PerformanceMetrics();

            metrics.RecordResourceSnapshot();
            var firstTime = metrics.GetLatestResourceSnapshot().Timestamp;
            System.Threading.Thread.Sleep(10);
            metrics.RecordResourceSnapshot();
            var latestTime = metrics.GetLatestResourceSnapshot().Timestamp;

            Assert.True(latestTime >= firstTime);
        }

        [Fact]
        public void GetAverageResources_WithMultipleSnapshots_CalculatesAverages()
        {
            var metrics = new PerformanceMetrics();

            metrics.RecordResourceSnapshot();
            metrics.RecordResourceSnapshot();
            metrics.RecordResourceSnapshot();

            var average = metrics.GetAverageResources();

            Assert.NotNull(average);
            Assert.True(average.MemoryUsedMB > 0);
            Assert.True(average.ThreadCount > 0);
        }

        [Fact]
        public void GetAverageResources_NoSnapshots_ReturnsNull()
        {
            var metrics = new PerformanceMetrics();

            var average = metrics.GetAverageResources();

            Assert.Null(average);
        }

        [Fact]
        public void Clear_RemovesAllMetrics()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("Op", now, now.AddSeconds(1), 0, 0);
            metrics.RecordResourceSnapshot();
            Assert.NotNull(metrics.GetAggregatedMetrics("Op"));

            metrics.Clear();

            Assert.Null(metrics.GetAggregatedMetrics("Op"));
            Assert.Empty(metrics.GetAllAggregatedMetrics());
        }

        [Fact]
        public void GenerateReport_WithMetrics_ProducesValidReport()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("Op", now, now.AddSeconds(1), 1024, 10);
            metrics.RecordResourceSnapshot();

            var report = metrics.GenerateReport();

            Assert.NotNull(report);
            Assert.Contains("PERFORMANCE METRICS REPORT", report);
            Assert.Contains("Op", report);
            Assert.Contains("OVERALL SUMMARY", report);
        }

        [Fact]
        public void IdentifyBottlenecks_NoIssues_ReturnsEmptyList()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("Op", now, now.AddSeconds(1), 1024, 10, "Success");

            var bottlenecks = metrics.IdentifyBottlenecks();

            Assert.Empty(bottlenecks);
        }

        [Fact]
        public void IdentifyBottlenecks_SlowOperation_DetectsBottleneck()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("SlowOp", now, now.AddSeconds(10), 0, 0);

            var bottlenecks = metrics.IdentifyBottlenecks();

            Assert.NotEmpty(bottlenecks);
            Assert.Contains("Slow operation detected", bottlenecks[0]);
        }

        [Fact]
        public void IdentifyBottlenecks_HighFailureRate_DetectsBottleneck()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            for (int i = 0; i < 20; i++)
            {
                metrics.RecordOperation("FailOp", now.AddSeconds(i), now.AddSeconds(i + 0.1), 0, 0, 
                    i < 18 ? "Failure" : "Success");
            }

            var bottlenecks = metrics.IdentifyBottlenecks();

            Assert.Contains(bottlenecks, b => b.Contains("High failure rate"));
        }

        [Fact]
        public void PerformanceTimer_RecordsAndDisposesAutomatically()
        {
            var metrics = new PerformanceMetrics();

            using (var timer = new PerformanceTimer(metrics, "TimedOp"))
            {
                timer.RecordBytes(1024);
                timer.RecordItems(5);
                System.Threading.Thread.Sleep(50);
            }

            var aggregated = metrics.GetAggregatedMetrics("TimedOp");
            Assert.NotNull(aggregated);
            Assert.Equal(1024, aggregated.TotalBytesProcessed);
            Assert.Equal(5, aggregated.TotalItemsProcessed);
        }

        [Fact]
        public void PerformanceTimer_MultipleRecordCalls_AccumulateValues()
        {
            var metrics = new PerformanceMetrics();

            using (var timer = new PerformanceTimer(metrics, "Op"))
            {
                timer.RecordBytes(500);
                timer.RecordItems(2);
                timer.RecordBytes(1500);
                timer.RecordItems(8);
            }

            var aggregated = metrics.GetAggregatedMetrics("Op");
            Assert.Equal(2000, aggregated.TotalBytesProcessed);
            Assert.Equal(10, aggregated.TotalItemsProcessed);
        }

        [Fact]
        public void RecordOperation_EmptyOperationName_AcceptsAndRecords()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("", now, now.AddSeconds(1), 0, 0);

            var aggregated = metrics.GetAggregatedMetrics("");
            Assert.NotNull(aggregated);
            Assert.Equal(1, aggregated.OperationCount);
        }

        [Fact]
        public void RecordOperation_ZeroDuration_CalculatesThroughputAsZero()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("Op", now, now, 1024, 0);

            var aggregated = metrics.GetAggregatedMetrics("Op");
            Assert.Equal(0, aggregated.AverageThroughputMBps);
        }

        [Fact]
        public void RecordOperation_NegativeDuration_StillCalculatesMetrics()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;

            metrics.RecordOperation("Op", now.AddSeconds(5), now, 0, 0);

            var aggregated = metrics.GetAggregatedMetrics("Op");
            Assert.NotNull(aggregated);
            Assert.True(aggregated.TotalDuration.TotalSeconds < 0);
        }

        [Fact]
        public void GenerateReport_EmptyMetrics_ProducesValidReport()
        {
            var metrics = new PerformanceMetrics();

            var report = metrics.GenerateReport();

            Assert.NotNull(report);
            Assert.Contains("PERFORMANCE METRICS REPORT", report);
            Assert.Contains("OVERALL SUMMARY", report);
        }

        [Fact]
        public void ConcurrentRecordOperations_ThreadSafe_AllMetricsRecorded()
        {
            var metrics = new PerformanceMetrics();
            var now = DateTime.UtcNow;
            var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task>();

            for (int i = 0; i < 10; i++)
            {
                int index = i;
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    metrics.RecordOperation("ConcurrentOp", now.AddSeconds(index), now.AddSeconds(index + 1), 100, 1);
                }));
            }

            System.Threading.Tasks.Task.WaitAll(tasks.ToArray());

            var aggregated = metrics.GetAggregatedMetrics("ConcurrentOp");
            Assert.Equal(10, aggregated.OperationCount);
        }
    }
}
