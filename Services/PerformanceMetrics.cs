using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace VPM.Services
{
    /// <summary>
    /// Comprehensive performance metrics collection and analysis.
    /// Tracks operation timing, memory usage, and throughput across all optimization stages.
    /// 
    /// Benefits:
    /// - Data-driven optimization decisions
    /// - Bottleneck identification
    /// - Performance trend analysis
    /// - System resource monitoring
    /// </summary>
    public class PerformanceMetrics
    {
        /// <summary>
        /// Metrics for a single operation
        /// </summary>
        public class OperationMetrics
        {
            public string OperationName { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public TimeSpan Duration => EndTime - StartTime;
            public long BytesProcessed { get; set; }
            public long MemoryUsedMB { get; set; }
            public int ItemsProcessed { get; set; }
            public double ThroughputMBps => Duration.TotalSeconds > 0 ? (BytesProcessed / 1024.0 / 1024.0) / Duration.TotalSeconds : 0;
            public double ItemsPerSecond => Duration.TotalSeconds > 0 ? ItemsProcessed / Duration.TotalSeconds : 0;
            public string Status { get; set; } = "Success";
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Aggregated metrics for operation type
        /// </summary>
        public class AggregatedMetrics
        {
            public string OperationType { get; set; }
            public int OperationCount { get; set; }
            public TimeSpan TotalDuration { get; set; }
            public TimeSpan AverageDuration => OperationCount > 0 ? TimeSpan.FromMilliseconds(TotalDuration.TotalMilliseconds / OperationCount) : TimeSpan.Zero;
            public long TotalBytesProcessed { get; set; }
            public double AverageThroughputMBps { get; set; }
            public int TotalItemsProcessed { get; set; }
            public double AverageItemsPerSecond { get; set; }
            public int SuccessCount { get; set; }
            public int FailureCount { get; set; }
            public double SuccessRate => OperationCount > 0 ? (SuccessCount * 100.0) / OperationCount : 0;
        }

        /// <summary>
        /// System resource snapshot
        /// </summary>
        public class SystemResourceSnapshot
        {
            public DateTime Timestamp { get; set; }
            public long MemoryUsedMB { get; set; }
            public long MemoryAvailableMB { get; set; }
            public double CPUUsagePercent { get; set; }
            public int ThreadCount { get; set; }
            public long GCTotalMemoryMB { get; set; }
        }

        private readonly ConcurrentDictionary<string, List<OperationMetrics>> _operationMetrics;
        private readonly ConcurrentBag<SystemResourceSnapshot> _resourceSnapshots;
        private readonly Process _currentProcess;
        private readonly object _lock = new object();

        public PerformanceMetrics()
        {
            _operationMetrics = new ConcurrentDictionary<string, List<OperationMetrics>>();
            _resourceSnapshots = new ConcurrentBag<SystemResourceSnapshot>();
            _currentProcess = Process.GetCurrentProcess();
        }

        /// <summary>
        /// Records an operation's performance metrics.
        /// </summary>
        public void RecordOperation(string operationName, DateTime startTime, DateTime endTime, long bytesProcessed, int itemsProcessed, string status = "Success", string errorMessage = null)
        {
            var metrics = new OperationMetrics
            {
                OperationName = operationName,
                StartTime = startTime,
                EndTime = endTime,
                BytesProcessed = bytesProcessed,
                ItemsProcessed = itemsProcessed,
                MemoryUsedMB = GC.GetTotalMemory(false) / 1024 / 1024,
                Status = status,
                ErrorMessage = errorMessage
            };

            var key = operationName;
            _operationMetrics.AddOrUpdate(key,
                new List<OperationMetrics> { metrics },
                (k, list) =>
                {
                    lock (_lock)
                    {
                        list.Add(metrics);
                    }
                    return list;
                });
        }

        /// <summary>
        /// Records a system resource snapshot.
        /// </summary>
        public void RecordResourceSnapshot()
        {
            try
            {
                _currentProcess.Refresh();
                var snapshot = new SystemResourceSnapshot
                {
                    Timestamp = DateTime.UtcNow,
                    MemoryUsedMB = _currentProcess.WorkingSet64 / 1024 / 1024,
                    MemoryAvailableMB = GC.GetTotalMemory(false) / 1024 / 1024,
                    ThreadCount = _currentProcess.Threads.Count,
                    GCTotalMemoryMB = GC.GetTotalMemory(false) / 1024 / 1024
                };

                _resourceSnapshots.Add(snapshot);
            }
            catch
            {
                // Silently fail if unable to capture metrics
            }
        }

        /// <summary>
        /// Gets aggregated metrics for an operation type.
        /// </summary>
        public AggregatedMetrics GetAggregatedMetrics(string operationType)
        {
            if (!_operationMetrics.TryGetValue(operationType, out var metrics) || metrics.Count == 0)
                return null;

            lock (_lock)
            {
                var successMetrics = metrics.Where(m => m.Status == "Success").ToList();
                var failureCount = metrics.Count - successMetrics.Count;

                return new AggregatedMetrics
                {
                    OperationType = operationType,
                    OperationCount = metrics.Count,
                    TotalDuration = TimeSpan.FromMilliseconds(metrics.Sum(m => m.Duration.TotalMilliseconds)),
                    TotalBytesProcessed = metrics.Sum(m => m.BytesProcessed),
                    AverageThroughputMBps = successMetrics.Count > 0 ? successMetrics.Average(m => m.ThroughputMBps) : 0,
                    TotalItemsProcessed = metrics.Sum(m => m.ItemsProcessed),
                    AverageItemsPerSecond = successMetrics.Count > 0 ? successMetrics.Average(m => m.ItemsPerSecond) : 0,
                    SuccessCount = successMetrics.Count,
                    FailureCount = failureCount
                };
            }
        }

        /// <summary>
        /// Gets all aggregated metrics.
        /// </summary>
        public List<AggregatedMetrics> GetAllAggregatedMetrics()
        {
            return _operationMetrics.Keys
                .Select(GetAggregatedMetrics)
                .Where(m => m != null)
                .OrderByDescending(m => m.TotalDuration)
                .ToList();
        }

        /// <summary>
        /// Gets the latest system resource snapshot.
        /// </summary>
        public SystemResourceSnapshot GetLatestResourceSnapshot()
        {
            return _resourceSnapshots.OrderByDescending(s => s.Timestamp).FirstOrDefault();
        }

        /// <summary>
        /// Gets average system resources over time.
        /// </summary>
        public SystemResourceSnapshot GetAverageResources()
        {
            if (_resourceSnapshots.Count == 0)
                return null;

            var snapshots = _resourceSnapshots.ToList();
            return new SystemResourceSnapshot
            {
                Timestamp = DateTime.UtcNow,
                MemoryUsedMB = (long)snapshots.Average(s => s.MemoryUsedMB),
                MemoryAvailableMB = (long)snapshots.Average(s => s.MemoryAvailableMB),
                CPUUsagePercent = snapshots.Average(s => s.CPUUsagePercent),
                ThreadCount = (int)snapshots.Average(s => s.ThreadCount),
                GCTotalMemoryMB = (long)snapshots.Average(s => s.GCTotalMemoryMB)
            };
        }

        /// <summary>
        /// Clears all collected metrics.
        /// </summary>
        public void Clear()
        {
            _operationMetrics.Clear();
            while (_resourceSnapshots.TryTake(out _)) { }
        }

        /// <summary>
        /// Generates a comprehensive metrics report.
        /// </summary>
        public string GenerateReport()
        {
            var sb = new StringBuilder();
            var allMetrics = GetAllAggregatedMetrics();
            var avgResources = GetAverageResources();

            sb.AppendLine("╔════════════════════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║                    PERFORMANCE METRICS REPORT                                  ║");
            sb.AppendLine("╠════════════════════════════════════════════════════════════════════════════════╣");

            if (allMetrics.Count > 0)
            {
                sb.AppendLine("║ OPERATION METRICS:                                                             ║");
                sb.AppendLine("╠════════════════════════════════════════════════════════════════════════════════╣");

                foreach (var metric in allMetrics)
                {
                    sb.AppendLine($"║ {metric.OperationType,-78} ║");
                    sb.AppendLine($"║   Count:              {metric.OperationCount,-60} ║");
                    sb.AppendLine($"║   Total Duration:     {metric.TotalDuration.TotalSeconds:F2}s {"",-50} ║");
                    sb.AppendLine($"║   Average Duration:   {metric.AverageDuration.TotalMilliseconds:F0}ms {"",-50} ║");
                    sb.AppendLine($"║   Throughput:         {metric.AverageThroughputMBps:F2} MB/s {"",-50} ║");
                    sb.AppendLine($"║   Items/sec:          {metric.AverageItemsPerSecond:F2} {"",-60} ║");
                    sb.AppendLine($"║   Success Rate:       {metric.SuccessRate:F1}% ({metric.SuccessCount}/{metric.OperationCount}) {"",-40} ║");
                    sb.AppendLine("╠════════════════════════════════════════════════════════════════════════════════╣");
                }
            }

            if (avgResources != null)
            {
                sb.AppendLine("║ SYSTEM RESOURCES (Average):                                                   ║");
                sb.AppendLine("╠════════════════════════════════════════════════════════════════════════════════╣");
                sb.AppendLine($"║   Memory Used:        {avgResources.MemoryUsedMB} MB {"",-60} ║");
                sb.AppendLine($"║   Memory Available:   {avgResources.MemoryAvailableMB} MB {"",-60} ║");
                sb.AppendLine($"║   Thread Count:       {avgResources.ThreadCount} {"",-60} ║");
                sb.AppendLine($"║   GC Total Memory:    {avgResources.GCTotalMemoryMB} MB {"",-60} ║");
                sb.AppendLine("╠════════════════════════════════════════════════════════════════════════════════╣");
            }

            // Summary statistics
            var totalDuration = TimeSpan.FromMilliseconds(allMetrics.Sum(m => m.TotalDuration.TotalMilliseconds));
            var totalBytes = allMetrics.Sum(m => m.TotalBytesProcessed);
            var totalItems = allMetrics.Sum(m => m.TotalItemsProcessed);
            var overallSuccess = allMetrics.Sum(m => m.SuccessCount);
            var overallFailure = allMetrics.Sum(m => m.FailureCount);

            sb.AppendLine("║ OVERALL SUMMARY:                                                               ║");
            sb.AppendLine("╠════════════════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine($"║   Total Operations:   {allMetrics.Sum(m => m.OperationCount),-60} ║");
            sb.AppendLine($"║   Total Duration:     {totalDuration.TotalSeconds:F2}s {"",-50} ║");
            sb.AppendLine($"║   Total Data:         {totalBytes / 1024 / 1024} MB {"",-60} ║");
            sb.AppendLine($"║   Total Items:        {totalItems,-60} ║");
            sb.AppendLine($"║   Success Rate:       {(overallSuccess + overallFailure > 0 ? (overallSuccess * 100.0) / (overallSuccess + overallFailure) : 0):F1}% {"",-60} ║");
            sb.AppendLine("╚════════════════════════════════════════════════════════════════════════════════╝");

            return sb.ToString();
        }

        /// <summary>
        /// Identifies performance bottlenecks.
        /// </summary>
        public List<string> IdentifyBottlenecks()
        {
            var bottlenecks = new List<string>();
            var allMetrics = GetAllAggregatedMetrics();

            if (allMetrics.Count == 0)
                return bottlenecks;

            var slowestOp = allMetrics.OrderByDescending(m => m.AverageDuration).FirstOrDefault();
            if (slowestOp != null && slowestOp.AverageDuration.TotalSeconds > 5)
            {
                bottlenecks.Add($"Slow operation detected: {slowestOp.OperationType} takes {slowestOp.AverageDuration.TotalSeconds:F2}s on average");
            }

            var lowestThroughput = allMetrics.OrderBy(m => m.AverageThroughputMBps).FirstOrDefault();
            if (lowestThroughput != null && lowestThroughput.AverageThroughputMBps < 10)
            {
                bottlenecks.Add($"Low throughput: {lowestThroughput.OperationType} processes only {lowestThroughput.AverageThroughputMBps:F2} MB/s");
            }

            var highFailureRate = allMetrics.Where(m => m.SuccessRate < 95).ToList();
            foreach (var metric in highFailureRate)
            {
                bottlenecks.Add($"High failure rate: {metric.OperationType} has {100 - metric.SuccessRate:F1}% failure rate");
            }

            var avgResources = GetAverageResources();
            if (avgResources != null && avgResources.MemoryUsedMB > 1024)
            {
                bottlenecks.Add($"High memory usage: {avgResources.MemoryUsedMB} MB average");
            }

            return bottlenecks;
        }
    }

    /// <summary>
    /// Helper class for timing operations with automatic metric recording.
    /// </summary>
    public class PerformanceTimer : IDisposable
    {
        private readonly PerformanceMetrics _metrics;
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch;
        private long _bytesProcessed;
        private int _itemsProcessed;

        public PerformanceTimer(PerformanceMetrics metrics, string operationName)
        {
            _metrics = metrics;
            _operationName = operationName;
            _stopwatch = Stopwatch.StartNew();
        }

        public void RecordBytes(long bytes)
        {
            _bytesProcessed += bytes;
        }

        public void RecordItems(int items)
        {
            _itemsProcessed += items;
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _metrics?.RecordOperation(_operationName, DateTime.UtcNow.Subtract(_stopwatch.Elapsed), DateTime.UtcNow, _bytesProcessed, _itemsProcessed);
        }
    }
}
