using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VPM.Services
{
    /// <summary>
    /// High-level performance aggregation and analysis engine.
    /// Provides trend analysis, bottleneck detection, and optimization recommendations.
    /// 
    /// Features:
    /// - Performance trend tracking
    /// - Bottleneck identification
    /// - Optimization recommendations
    /// - SLA monitoring
    /// - Anomaly detection
    /// - Historical data retention
    /// </summary>
    public class PerformanceAggregator
    {
        private readonly List<PerformanceSnapshot> _snapshots;
        private readonly object _snapshotsLock = new object();
        private readonly int _maxSnapshots;
        private DateTime _lastSnapshotTime;

        /// <summary>
        /// Performance snapshot for historical tracking
        /// </summary>
        public class PerformanceSnapshot
        {
            public DateTime Timestamp { get; set; }
            public double ThroughputMBps { get; set; }
            public double AverageLatencyMs { get; set; }
            public double P95LatencyMs { get; set; }
            public double P99LatencyMs { get; set; }
            public int ActiveTaskCount { get; set; }
            public int CompletedTaskCount { get; set; }
            public int FailedTaskCount { get; set; }
            public double SuccessRate { get; set; }
            public double CpuUsagePercent { get; set; }
            public double MemoryUsageMB { get; set; }
        }

        /// <summary>
        /// Performance trend analysis
        /// </summary>
        public class PerformanceTrend
        {
            public string MetricName { get; set; }
            public double CurrentValue { get; set; }
            public double PreviousValue { get; set; }
            public double ChangePercent => PreviousValue != 0 ? ((CurrentValue - PreviousValue) / PreviousValue) * 100 : 0;
            public TrendDirection Direction { get; set; }
            public bool IsAnomaly { get; set; }
        }

        /// <summary>
        /// Bottleneck analysis result
        /// </summary>
        public class BottleneckAnalysis
        {
            public string BottleneckType { get; set; }
            public double Severity { get; set; } // 0-100
            public string Description { get; set; }
            public List<string> AffectedTaskTypes { get; set; }
            public string RecommendedAction { get; set; }
        }

        /// <summary>
        /// Optimization recommendation
        /// </summary>
        public class OptimizationRecommendation
        {
            public string Title { get; set; }
            public string Description { get; set; }
            public double ExpectedImprovementPercent { get; set; }
            public int Priority { get; set; } // 1-5, 5 = highest
            public string Implementation { get; set; }
        }

        /// <summary>
        /// Trend direction
        /// </summary>
        public enum TrendDirection
        {
            Improving,
            Degrading,
            Stable
        }

        public PerformanceAggregator(int maxSnapshots = 1000)
        {
            _snapshots = new List<PerformanceSnapshot>(maxSnapshots);
            _maxSnapshots = maxSnapshots;
            _lastSnapshotTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Record a performance snapshot
        /// </summary>
        public void RecordSnapshot(TaskExecutionMonitor.PerformanceSnapshot monitorSnapshot)
        {
            if (monitorSnapshot == null)
                return;

            var snapshot = new PerformanceSnapshot
            {
                Timestamp = monitorSnapshot.CaptureTime,
                ThroughputMBps = monitorSnapshot.OverallThroughputMBps,
                ActiveTaskCount = monitorSnapshot.TotalActiveTasks,
                CompletedTaskCount = monitorSnapshot.TotalCompletedTasks,
                FailedTaskCount = monitorSnapshot.TotalFailedTasks,
                CpuUsagePercent = monitorSnapshot.AverageCpuUsagePercent,
                MemoryUsageMB = monitorSnapshot.AverageMemoryUsageMB
            };

            // Calculate latency metrics
            var allLatencies = new List<long>();
            foreach (var typeMetrics in monitorSnapshot.TypeMetrics.Values)
            {
                allLatencies.AddRange(typeMetrics.LatencyHistory);
            }

            if (allLatencies.Count > 0)
            {
                var sorted = allLatencies.OrderBy(x => x).ToList();
                snapshot.AverageLatencyMs = sorted.Average();
                snapshot.P95LatencyMs = GetPercentile(sorted, 95);
                snapshot.P99LatencyMs = GetPercentile(sorted, 99);
            }

            // Calculate success rate
            int totalTasks = snapshot.CompletedTaskCount + snapshot.FailedTaskCount;
            snapshot.SuccessRate = totalTasks > 0 ? (snapshot.CompletedTaskCount * 100.0) / totalTasks : 100;

            lock (_snapshotsLock)
            {
                _snapshots.Add(snapshot);

                // Maintain max snapshots
                if (_snapshots.Count > _maxSnapshots)
                {
                    _snapshots.RemoveAt(0);
                }
            }

            _lastSnapshotTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Analyze performance trends
        /// </summary>
        public List<PerformanceTrend> AnalyzeTrends(int windowSize = 10)
        {
            lock (_snapshotsLock)
            {
                if (_snapshots.Count < 2)
                    return new List<PerformanceTrend>();

                var trends = new List<PerformanceTrend>();
                int startIndex = Math.Max(0, _snapshots.Count - windowSize);

                var recentSnapshots = _snapshots.Skip(startIndex).ToList();
                if (recentSnapshots.Count < 2)
                    return trends;

                var current = recentSnapshots.Last();
                var previous = recentSnapshots[recentSnapshots.Count - 2];

                // Throughput trend
                trends.Add(new PerformanceTrend
                {
                    MetricName = "Throughput",
                    CurrentValue = current.ThroughputMBps,
                    PreviousValue = previous.ThroughputMBps,
                    Direction = current.ThroughputMBps > previous.ThroughputMBps ? TrendDirection.Improving : TrendDirection.Degrading
                });

                // Latency trend
                trends.Add(new PerformanceTrend
                {
                    MetricName = "Average Latency",
                    CurrentValue = current.AverageLatencyMs,
                    PreviousValue = previous.AverageLatencyMs,
                    Direction = current.AverageLatencyMs < previous.AverageLatencyMs ? TrendDirection.Improving : TrendDirection.Degrading
                });

                // Success rate trend
                trends.Add(new PerformanceTrend
                {
                    MetricName = "Success Rate",
                    CurrentValue = current.SuccessRate,
                    PreviousValue = previous.SuccessRate,
                    Direction = current.SuccessRate >= previous.SuccessRate ? TrendDirection.Improving : TrendDirection.Degrading
                });

                // CPU usage trend
                trends.Add(new PerformanceTrend
                {
                    MetricName = "CPU Usage",
                    CurrentValue = current.CpuUsagePercent,
                    PreviousValue = previous.CpuUsagePercent,
                    Direction = current.CpuUsagePercent < previous.CpuUsagePercent ? TrendDirection.Improving : TrendDirection.Degrading
                });

                return trends;
            }
        }

        /// <summary>
        /// Detect bottlenecks
        /// </summary>
        public List<BottleneckAnalysis> DetectBottlenecks(TaskExecutionMonitor.PerformanceSnapshot monitorSnapshot)
        {
            var bottlenecks = new List<BottleneckAnalysis>();

            if (monitorSnapshot == null)
                return bottlenecks;

            // CPU bottleneck
            if (monitorSnapshot.AverageCpuUsagePercent > 85)
            {
                bottlenecks.Add(new BottleneckAnalysis
                {
                    BottleneckType = "CPU",
                    Severity = Math.Min(100, monitorSnapshot.AverageCpuUsagePercent),
                    Description = "CPU usage is critically high",
                    RecommendedAction = "Reduce worker count or optimize task processing logic"
                });
            }

            // Memory bottleneck
            if (monitorSnapshot.AverageMemoryUsageMB < 100) // Low available memory
            {
                bottlenecks.Add(new BottleneckAnalysis
                {
                    BottleneckType = "Memory",
                    Severity = Math.Min(100, (1 - (monitorSnapshot.AverageMemoryUsageMB / 1000)) * 100),
                    Description = "Available memory is low",
                    RecommendedAction = "Increase buffer pool size or reduce concurrent tasks"
                });
            }

            // Task queue bottleneck
            var queueStats = monitorSnapshot.TypeMetrics.Values.FirstOrDefault();
            if (queueStats != null && queueStats.RunningCount > 50)
            {
                bottlenecks.Add(new BottleneckAnalysis
                {
                    BottleneckType = "Queue",
                    Severity = Math.Min(100, (queueStats.RunningCount / 100.0) * 100),
                    Description = "Task queue is backing up",
                    RecommendedAction = "Increase worker count or optimize task completion time"
                });
            }

            // High failure rate
            var totalTasks = monitorSnapshot.TotalCompletedTasks + monitorSnapshot.TotalFailedTasks;
            if (totalTasks > 0)
            {
                double failureRate = (monitorSnapshot.TotalFailedTasks * 100.0) / totalTasks;
                if (failureRate > 5)
                {
                    bottlenecks.Add(new BottleneckAnalysis
                    {
                        BottleneckType = "Reliability",
                        Severity = Math.Min(100, failureRate),
                        Description = $"Task failure rate is {failureRate:F1}%",
                        RecommendedAction = "Review error logs and implement retry logic"
                    });
                }
            }

            return bottlenecks;
        }

        /// <summary>
        /// Generate optimization recommendations
        /// </summary>
        public List<OptimizationRecommendation> GenerateRecommendations(TaskExecutionMonitor.PerformanceSnapshot monitorSnapshot)
        {
            var recommendations = new List<OptimizationRecommendation>();

            if (monitorSnapshot == null)
                return recommendations;

            var bottlenecks = DetectBottlenecks(monitorSnapshot);

            // CPU optimization
            if (bottlenecks.Any(b => b.BottleneckType == "CPU"))
            {
                recommendations.Add(new OptimizationRecommendation
                {
                    Title = "Reduce Parallelism",
                    Description = "CPU is over-utilized. Reduce the number of worker threads.",
                    ExpectedImprovementPercent = 15,
                    Priority = 5,
                    Implementation = "Decrease MaxWorkers in SchedulerConfig"
                });

                recommendations.Add(new OptimizationRecommendation
                {
                    Title = "Optimize Task Logic",
                    Description = "Profile and optimize the most CPU-intensive task types.",
                    ExpectedImprovementPercent = 25,
                    Priority = 4,
                    Implementation = "Use profiler to identify hot paths in task execution"
                });
            }

            // Memory optimization
            if (bottlenecks.Any(b => b.BottleneckType == "Memory"))
            {
                recommendations.Add(new OptimizationRecommendation
                {
                    Title = "Increase Buffer Pool",
                    Description = "Memory is constrained. Increase buffer pool size.",
                    ExpectedImprovementPercent = 20,
                    Priority = 4,
                    Implementation = "Increase BufferPool.SIZE_* constants"
                });

                recommendations.Add(new OptimizationRecommendation
                {
                    Title = "Reduce Concurrent Tasks",
                    Description = "Lower the maximum queue size to reduce memory pressure.",
                    ExpectedImprovementPercent = 10,
                    Priority = 3,
                    Implementation = "Reduce QueueMaxSize in SchedulerConfig"
                });
            }

            // Queue optimization
            if (bottlenecks.Any(b => b.BottleneckType == "Queue"))
            {
                recommendations.Add(new OptimizationRecommendation
                {
                    Title = "Increase Worker Count",
                    Description = "Task queue is backing up. Increase worker threads.",
                    ExpectedImprovementPercent = 30,
                    Priority = 5,
                    Implementation = "Increase MaxWorkers in SchedulerConfig"
                });
            }

            // Reliability optimization
            if (bottlenecks.Any(b => b.BottleneckType == "Reliability"))
            {
                recommendations.Add(new OptimizationRecommendation
                {
                    Title = "Implement Retry Logic",
                    Description = "High failure rate detected. Implement exponential backoff retry.",
                    ExpectedImprovementPercent = 40,
                    Priority = 5,
                    Implementation = "Add retry logic in Step 4"
                });
            }

            // General optimization
            if (monitorSnapshot.OverallThroughputMBps < 10)
            {
                recommendations.Add(new OptimizationRecommendation
                {
                    Title = "Increase Task Batch Size",
                    Description = "Throughput is low. Process larger batches of data.",
                    ExpectedImprovementPercent = 35,
                    Priority = 4,
                    Implementation = "Increase buffer sizes in task options"
                });
            }

            return recommendations.OrderByDescending(r => r.Priority).ToList();
        }

        /// <summary>
        /// Get performance statistics for a time window
        /// </summary>
        public (double minThroughput, double maxThroughput, double avgThroughput) GetThroughputStats(TimeSpan window)
        {
            lock (_snapshotsLock)
            {
                var cutoffTime = DateTime.UtcNow - window;
                var relevantSnapshots = _snapshots.Where(s => s.Timestamp >= cutoffTime).ToList();

                if (relevantSnapshots.Count == 0)
                    return (0, 0, 0);

                var throughputs = relevantSnapshots.Select(s => s.ThroughputMBps).ToList();
                return (throughputs.Min(), throughputs.Max(), throughputs.Average());
            }
        }

        /// <summary>
        /// Get latency statistics for a time window
        /// </summary>
        public (double minLatency, double maxLatency, double avgLatency) GetLatencyStats(TimeSpan window)
        {
            lock (_snapshotsLock)
            {
                var cutoffTime = DateTime.UtcNow - window;
                var relevantSnapshots = _snapshots.Where(s => s.Timestamp >= cutoffTime).ToList();

                if (relevantSnapshots.Count == 0)
                    return (0, 0, 0);

                var latencies = relevantSnapshots.Select(s => s.AverageLatencyMs).ToList();
                return (latencies.Min(), latencies.Max(), latencies.Average());
            }
        }

        /// <summary>
        /// Get percentile value from sorted list
        /// </summary>
        private double GetPercentile(List<long> sortedValues, double percentile)
        {
            if (sortedValues.Count == 0)
                return 0;

            int index = (int)((percentile / 100.0) * sortedValues.Count);
            return sortedValues[Math.Min(index, sortedValues.Count - 1)];
        }

        /// <summary>
        /// Clear historical data
        /// </summary>
        public void Clear()
        {
            lock (_snapshotsLock)
            {
                _snapshots.Clear();
            }
        }
    }
}
