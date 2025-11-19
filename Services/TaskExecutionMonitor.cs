using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace VPM.Services
{
    /// <summary>
    /// Real-time monitoring and metrics collection for task execution.
    /// Tracks performance, throughput, latency, and resource usage across all tasks.
    /// 
    /// Features:
    /// - Per-task metrics collection
    /// - Aggregated statistics by task type
    /// - Real-time throughput calculation
    /// - Latency percentile tracking
    /// - Resource utilization monitoring
    /// - Performance trend analysis
    /// </summary>
    public class TaskExecutionMonitor
    {
        private readonly ConcurrentDictionary<string, TaskMetrics> _taskMetrics;
        private readonly ConcurrentDictionary<string, TypeMetrics> _typeMetrics;
        private readonly object _statsLock = new object();
        private long _totalTasksCompleted = 0;
        private long _totalTasksFailed = 0;
        private long _totalBytesProcessed = 0;
        private DateTime _monitorStartTime;

        /// <summary>
        /// Per-task execution metrics
        /// </summary>
        public class TaskMetrics
        {
            public string TaskId { get; set; }
            public string TaskName { get; set; }
            public string TaskType { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;
            public int ProgressPercent { get; set; }
            public long BytesProcessed { get; set; }
            public long MemoryUsedMB { get; set; }
            public TaskState State { get; set; }
            public string Status { get; set; }
            public double ThroughputMBps => Duration?.TotalSeconds > 0 ? (BytesProcessed / 1024.0 / 1024.0) / Duration.Value.TotalSeconds : 0;
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Aggregated metrics by task type
        /// </summary>
        public class TypeMetrics
        {
            public string TaskType { get; set; }
            public int CompletedCount { get; set; }
            public int FailedCount { get; set; }
            public int RunningCount { get; set; }
            public TimeSpan TotalDuration { get; set; }
            public TimeSpan AverageDuration => CompletedCount > 0 ? TimeSpan.FromMilliseconds(TotalDuration.TotalMilliseconds / CompletedCount) : TimeSpan.Zero;
            public long TotalBytesProcessed { get; set; }
            public double AverageThroughputMBps { get; set; }
            public double SuccessRate => (CompletedCount + FailedCount) > 0 ? (CompletedCount * 100.0) / (CompletedCount + FailedCount) : 0;
            public List<long> LatencyHistory { get; set; } = new List<long>();
        }

        /// <summary>
        /// Overall scheduler performance snapshot
        /// </summary>
        public class PerformanceSnapshot
        {
            public DateTime CaptureTime { get; set; }
            public int TotalActiveTasks { get; set; }
            public int TotalCompletedTasks { get; set; }
            public int TotalFailedTasks { get; set; }
            public long TotalBytesProcessed { get; set; }
            public double OverallThroughputMBps { get; set; }
            public TimeSpan MonitoringDuration { get; set; }
            public Dictionary<string, TypeMetrics> TypeMetrics { get; set; }
            public List<TaskMetrics> ActiveTasks { get; set; }
            public double AverageCpuUsagePercent { get; set; }
            public double AverageMemoryUsageMB { get; set; }
        }

        public TaskExecutionMonitor()
        {
            _taskMetrics = new ConcurrentDictionary<string, TaskMetrics>();
            _typeMetrics = new ConcurrentDictionary<string, TypeMetrics>();
            _monitorStartTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Start monitoring a task
        /// </summary>
        public void StartTask(WorkTask task)
        {
            if (task == null)
                return;

            var metrics = new TaskMetrics
            {
                TaskId = task.TaskId,
                TaskName = task.TaskName,
                TaskType = task.GetTaskType(),
                StartTime = DateTime.UtcNow,
                State = TaskState.Running,
                Status = "Running"
            };

            _taskMetrics.TryAdd(task.TaskId, metrics);

            // Initialize type metrics if needed
            _typeMetrics.AddOrUpdate(task.GetTaskType(),
                new TypeMetrics { TaskType = task.GetTaskType(), RunningCount = 1 },
                (key, existing) =>
                {
                    existing.RunningCount++;
                    return existing;
                });
        }

        /// <summary>
        /// Update task progress
        /// </summary>
        public void UpdateTaskProgress(WorkTask task)
        {
            if (task == null || !_taskMetrics.TryGetValue(task.TaskId, out var metrics))
                return;

            metrics.ProgressPercent = task.ProgressPercent;
            metrics.BytesProcessed = task.CompletedWorkUnits;
            metrics.State = task.State;
        }

        /// <summary>
        /// Complete task monitoring
        /// </summary>
        public void CompleteTask(WorkTask task, bool success = true)
        {
            if (task == null || !_taskMetrics.TryGetValue(task.TaskId, out var metrics))
                return;

            metrics.EndTime = DateTime.UtcNow;
            metrics.State = success ? TaskState.Completed : TaskState.Failed;
            metrics.Status = success ? "Completed" : "Failed";
            metrics.ProgressPercent = 100;
            metrics.BytesProcessed = task.CompletedWorkUnits;

            if (!success && task.Exception != null)
            {
                metrics.ErrorMessage = task.Exception.Message;
            }

            // Update type metrics
            var duration = metrics.Duration ?? TimeSpan.Zero;
            _typeMetrics.AddOrUpdate(task.GetTaskType(),
                new TypeMetrics
                {
                    TaskType = task.GetTaskType(),
                    CompletedCount = success ? 1 : 0,
                    FailedCount = success ? 0 : 1,
                    TotalDuration = duration,
                    TotalBytesProcessed = metrics.BytesProcessed
                },
                (key, existing) =>
                {
                    if (success)
                    {
                        existing.CompletedCount++;
                    }
                    else
                    {
                        existing.FailedCount++;
                    }

                    existing.RunningCount = Math.Max(0, existing.RunningCount - 1);
                    existing.TotalDuration += duration;
                    existing.TotalBytesProcessed += metrics.BytesProcessed;
                    existing.AverageThroughputMBps = existing.CompletedCount > 0
                        ? (existing.TotalBytesProcessed / 1024.0 / 1024.0) / existing.TotalDuration.TotalSeconds
                        : 0;

                    if (existing.LatencyHistory.Count < 1000)
                    {
                        existing.LatencyHistory.Add((long)duration.TotalMilliseconds);
                    }

                    return existing;
                });

            if (success)
            {
                Interlocked.Increment(ref _totalTasksCompleted);
            }
            else
            {
                Interlocked.Increment(ref _totalTasksFailed);
            }
            Interlocked.Add(ref _totalBytesProcessed, metrics.BytesProcessed);
        }

        /// <summary>
        /// Get metrics for a specific task
        /// </summary>
        public TaskMetrics GetTaskMetrics(string taskId)
        {
            _taskMetrics.TryGetValue(taskId, out var metrics);
            return metrics;
        }

        /// <summary>
        /// Get metrics for a task type
        /// </summary>
        public TypeMetrics GetTypeMetrics(string taskType)
        {
            _typeMetrics.TryGetValue(taskType, out var metrics);
            return metrics;
        }

        /// <summary>
        /// Get all active task metrics
        /// </summary>
        public List<TaskMetrics> GetActiveTaskMetrics()
        {
            return _taskMetrics.Values
                .Where(m => m.State == TaskState.Running)
                .ToList();
        }

        /// <summary>
        /// Get all completed task metrics
        /// </summary>
        public List<TaskMetrics> GetCompletedTaskMetrics()
        {
            return _taskMetrics.Values
                .Where(m => m.State == TaskState.Completed)
                .ToList();
        }

        /// <summary>
        /// Get all failed task metrics
        /// </summary>
        public List<TaskMetrics> GetFailedTaskMetrics()
        {
            return _taskMetrics.Values
                .Where(m => m.State == TaskState.Failed)
                .ToList();
        }

        /// <summary>
        /// Get all type metrics
        /// </summary>
        public Dictionary<string, TypeMetrics> GetAllTypeMetrics()
        {
            return new Dictionary<string, TypeMetrics>(_typeMetrics);
        }

        /// <summary>
        /// Calculate latency percentile for a task type
        /// </summary>
        public long GetLatencyPercentile(string taskType, double percentile)
        {
            if (!_typeMetrics.TryGetValue(taskType, out var metrics) || metrics.LatencyHistory.Count == 0)
                return 0;

            var sorted = metrics.LatencyHistory.OrderBy(x => x).ToList();
            int index = (int)((percentile / 100.0) * sorted.Count);
            return sorted[Math.Min(index, sorted.Count - 1)];
        }

        /// <summary>
        /// Get performance snapshot
        /// </summary>
        public PerformanceSnapshot GetPerformanceSnapshot(AdaptiveOptimizer.ResourceState resourceState = null)
        {
            lock (_statsLock)
            {
                var activeTasks = GetActiveTaskMetrics();
                var completedTasks = GetCompletedTaskMetrics();
                var failedTasks = GetFailedTaskMetrics();

                var monitoringDuration = DateTime.UtcNow - _monitorStartTime;
                var overallThroughput = monitoringDuration.TotalSeconds > 0
                    ? (_totalBytesProcessed / 1024.0 / 1024.0) / monitoringDuration.TotalSeconds
                    : 0;

                return new PerformanceSnapshot
                {
                    CaptureTime = DateTime.UtcNow,
                    TotalActiveTasks = activeTasks.Count,
                    TotalCompletedTasks = (int)_totalTasksCompleted,
                    TotalFailedTasks = (int)_totalTasksFailed,
                    TotalBytesProcessed = _totalBytesProcessed,
                    OverallThroughputMBps = overallThroughput,
                    MonitoringDuration = monitoringDuration,
                    TypeMetrics = GetAllTypeMetrics(),
                    ActiveTasks = activeTasks,
                    AverageCpuUsagePercent = resourceState?.CPUUsagePercent ?? 0,
                    AverageMemoryUsageMB = resourceState?.AvailableMemoryMB ?? 0
                };
            }
        }

        /// <summary>
        /// Get formatted performance report
        /// </summary>
        public string GetPerformanceReport(PerformanceSnapshot snapshot = null)
        {
            snapshot = snapshot ?? GetPerformanceSnapshot();

            var report = new System.Text.StringBuilder();
            report.AppendLine("=== Task Execution Performance Report ===");
            report.AppendLine($"Capture Time: {snapshot.CaptureTime:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Monitoring Duration: {snapshot.MonitoringDuration:hh\\:mm\\:ss}");
            report.AppendLine();

            report.AppendLine("--- Overall Statistics ---");
            report.AppendLine($"Active Tasks: {snapshot.TotalActiveTasks}");
            report.AppendLine($"Completed Tasks: {snapshot.TotalCompletedTasks}");
            report.AppendLine($"Failed Tasks: {snapshot.TotalFailedTasks}");
            report.AppendLine($"Total Bytes Processed: {snapshot.TotalBytesProcessed / 1024.0 / 1024.0:F2} MB");
            report.AppendLine($"Overall Throughput: {snapshot.OverallThroughputMBps:F2} MB/s");
            report.AppendLine();

            report.AppendLine("--- Per-Type Statistics ---");
            foreach (var kvp in snapshot.TypeMetrics)
            {
                var metrics = kvp.Value;
                report.AppendLine($"\n{metrics.TaskType}:");
                report.AppendLine($"  Completed: {metrics.CompletedCount}");
                report.AppendLine($"  Failed: {metrics.FailedCount}");
                report.AppendLine($"  Running: {metrics.RunningCount}");
                report.AppendLine($"  Success Rate: {metrics.SuccessRate:F1}%");
                report.AppendLine($"  Avg Duration: {metrics.AverageDuration.TotalSeconds:F2}s");
                report.AppendLine($"  Avg Throughput: {metrics.AverageThroughputMBps:F2} MB/s");
            }

            report.AppendLine();
            report.AppendLine("--- Resource Usage ---");
            report.AppendLine($"CPU Usage: {snapshot.AverageCpuUsagePercent:F1}%");
            report.AppendLine($"Memory Available: {snapshot.AverageMemoryUsageMB:F0} MB");

            return report.ToString();
        }

        /// <summary>
        /// Clear all metrics
        /// </summary>
        public void Clear()
        {
            _taskMetrics.Clear();
            _typeMetrics.Clear();
            _totalTasksCompleted = 0;
            _totalTasksFailed = 0;
            _totalBytesProcessed = 0;
            _monitorStartTime = DateTime.UtcNow;
        }
    }
}
