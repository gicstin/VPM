using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Real-time metrics dashboard for UI integration.
    /// Provides formatted data for display and monitoring interfaces.
    /// 
    /// Features:
    /// - Real-time metric updates
    /// - Formatted display data
    /// - Alert generation
    /// - Historical data export
    /// - Custom metric queries
    /// </summary>
    public class MetricsDashboard
    {
        private readonly TaskExecutionMonitor _monitor;
        private readonly PerformanceAggregator _aggregator;
        private readonly AdaptiveOptimizer _adaptiveOptimizer;
        private CancellationTokenSource _updateCts;
        private Task _updateTask;
        private volatile bool _isRunning = false;

        /// <summary>
        /// Dashboard metric item for UI display
        /// </summary>
        public class DashboardMetric
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Unit { get; set; }
            public string Status { get; set; } // "Good", "Warning", "Critical"
            public double Percentage { get; set; } // 0-100 for progress bars
            public string Trend { get; set; } // "↑", "↓", "→"
            public DateTime LastUpdated { get; set; }
        }

        /// <summary>
        /// Dashboard alert
        /// </summary>
        public class DashboardAlert
        {
            public string AlertId { get; set; }
            public string Title { get; set; }
            public string Message { get; set; }
            public AlertSeverity Severity { get; set; }
            public DateTime Timestamp { get; set; }
            public bool IsResolved { get; set; }
        }

        /// <summary>
        /// Alert severity levels
        /// </summary>
        public enum AlertSeverity
        {
            Info,
            Warning,
            Critical
        }

        /// <summary>
        /// Dashboard state snapshot
        /// </summary>
        public class DashboardSnapshot
        {
            public DateTime Timestamp { get; set; }
            public List<DashboardMetric> Metrics { get; set; }
            public List<DashboardAlert> ActiveAlerts { get; set; }
            public Dictionary<string, object> CustomData { get; set; }
        }

        private readonly List<DashboardAlert> _alerts;
        private readonly object _alertsLock = new object();
        private int _nextAlertId = 0;

        public MetricsDashboard(TaskExecutionMonitor monitor, PerformanceAggregator aggregator, AdaptiveOptimizer adaptiveOptimizer)
        {
            _monitor = monitor;
            _aggregator = aggregator;
            _adaptiveOptimizer = adaptiveOptimizer;
            _alerts = new List<DashboardAlert>();
            _updateCts = new CancellationTokenSource();
        }

        /// <summary>
        /// Start dashboard updates
        /// </summary>
        public void Start(int updateIntervalMs = 1000)
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _updateCts = new CancellationTokenSource();
            _updateTask = UpdateLoopAsync(updateIntervalMs, _updateCts.Token);
        }

        /// <summary>
        /// Stop dashboard updates
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _updateCts.Cancel();

            try
            {
                await _updateTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        /// <summary>
        /// Get current dashboard snapshot
        /// </summary>
        public DashboardSnapshot GetSnapshot()
        {
            var metrics = BuildMetrics();
            var alerts = GetActiveAlerts();

            return new DashboardSnapshot
            {
                Timestamp = DateTime.UtcNow,
                Metrics = metrics,
                ActiveAlerts = alerts,
                CustomData = new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Build dashboard metrics
        /// </summary>
        private List<DashboardMetric> BuildMetrics()
        {
            var metrics = new List<DashboardMetric>();
            var monitorSnapshot = _monitor.GetPerformanceSnapshot(_adaptiveOptimizer.GetResourceState());

            // Overall throughput
            metrics.Add(new DashboardMetric
            {
                Name = "Overall Throughput",
                Value = $"{monitorSnapshot.OverallThroughputMBps:F2}",
                Unit = "MB/s",
                Status = GetStatus(monitorSnapshot.OverallThroughputMBps, 5, 1),
                Percentage = Math.Min(100, (monitorSnapshot.OverallThroughputMBps / 50) * 100),
                LastUpdated = DateTime.UtcNow
            });

            // Active tasks
            metrics.Add(new DashboardMetric
            {
                Name = "Active Tasks",
                Value = monitorSnapshot.TotalActiveTasks.ToString(),
                Unit = "tasks",
                Status = GetStatus(monitorSnapshot.TotalActiveTasks, 100, 10),
                Percentage = Math.Min(100, (monitorSnapshot.TotalActiveTasks / 200.0) * 100),
                LastUpdated = DateTime.UtcNow
            });

            // Completed tasks
            metrics.Add(new DashboardMetric
            {
                Name = "Completed Tasks",
                Value = monitorSnapshot.TotalCompletedTasks.ToString(),
                Unit = "tasks",
                Status = "Good",
                LastUpdated = DateTime.UtcNow
            });

            // Failed tasks
            var failureRate = monitorSnapshot.TotalCompletedTasks + monitorSnapshot.TotalFailedTasks > 0
                ? (monitorSnapshot.TotalFailedTasks * 100.0) / (monitorSnapshot.TotalCompletedTasks + monitorSnapshot.TotalFailedTasks)
                : 0;

            metrics.Add(new DashboardMetric
            {
                Name = "Failure Rate",
                Value = $"{failureRate:F1}",
                Unit = "%",
                Status = GetStatus(failureRate, 5, 1),
                Percentage = Math.Min(100, failureRate),
                LastUpdated = DateTime.UtcNow
            });

            // CPU usage
            var resourceState = _adaptiveOptimizer.GetResourceState();
            metrics.Add(new DashboardMetric
            {
                Name = "CPU Usage",
                Value = $"{resourceState.CPUUsagePercent:F1}",
                Unit = "%",
                Status = GetStatus(resourceState.CPUUsagePercent, 75, 50),
                Percentage = resourceState.CPUUsagePercent,
                LastUpdated = DateTime.UtcNow
            });

            // Memory usage
            metrics.Add(new DashboardMetric
            {
                Name = "Memory Available",
                Value = $"{resourceState.AvailableMemoryMB:F0}",
                Unit = "MB",
                Status = GetStatus(resourceState.AvailableMemoryMB, 500, 200),
                Percentage = Math.Min(100, (resourceState.AvailableMemoryMB / 1000) * 100),
                LastUpdated = DateTime.UtcNow
            });

            // Data processed
            metrics.Add(new DashboardMetric
            {
                Name = "Data Processed",
                Value = $"{monitorSnapshot.TotalBytesProcessed / 1024.0 / 1024.0:F2}",
                Unit = "MB",
                Status = "Good",
                LastUpdated = DateTime.UtcNow
            });

            // Per-type metrics
            foreach (var kvp in monitorSnapshot.TypeMetrics)
            {
                var typeMetrics = kvp.Value;
                metrics.Add(new DashboardMetric
                {
                    Name = $"{kvp.Key} Success Rate",
                    Value = $"{typeMetrics.SuccessRate:F1}",
                    Unit = "%",
                    Status = GetStatus(typeMetrics.SuccessRate, 95, 80),
                    Percentage = typeMetrics.SuccessRate,
                    LastUpdated = DateTime.UtcNow
                });
            }

            return metrics;
        }

        /// <summary>
        /// Get status based on thresholds
        /// </summary>
        private string GetStatus(double value, double goodThreshold, double warningThreshold)
        {
            if (value >= goodThreshold)
                return "Good";
            if (value >= warningThreshold)
                return "Warning";
            return "Critical";
        }

        /// <summary>
        /// Create alert
        /// </summary>
        public void CreateAlert(string title, string message, AlertSeverity severity)
        {
            lock (_alertsLock)
            {
                var alert = new DashboardAlert
                {
                    AlertId = $"ALERT_{_nextAlertId++}",
                    Title = title,
                    Message = message,
                    Severity = severity,
                    Timestamp = DateTime.UtcNow,
                    IsResolved = false
                };

                _alerts.Add(alert);

                // Keep only last 100 alerts
                if (_alerts.Count > 100)
                {
                    _alerts.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Get active alerts
        /// </summary>
        public List<DashboardAlert> GetActiveAlerts()
        {
            lock (_alertsLock)
            {
                return _alerts.Where(a => !a.IsResolved).ToList();
            }
        }

        /// <summary>
        /// Resolve alert
        /// </summary>
        public void ResolveAlert(string alertId)
        {
            lock (_alertsLock)
            {
                var alert = _alerts.FirstOrDefault(a => a.AlertId == alertId);
                if (alert != null)
                {
                    alert.IsResolved = true;
                }
            }
        }

        /// <summary>
        /// Get formatted dashboard report
        /// </summary>
        public string GetFormattedReport()
        {
            var snapshot = GetSnapshot();
            var report = new StringBuilder();

            report.AppendLine("╔════════════════════════════════════════════════════════════════╗");
            report.AppendLine("║           PARALLEL TASK SCHEDULER - METRICS DASHBOARD           ║");
            report.AppendLine("╚════════════════════════════════════════════════════════════════╝");
            report.AppendLine();

            report.AppendLine($"Timestamp: {snapshot.Timestamp:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine();

            report.AppendLine("┌─ PERFORMANCE METRICS ─────────────────────────────────────────┐");
            foreach (var metric in snapshot.Metrics.Take(8))
            {
                var statusIcon = metric.Status switch
                {
                    "Good" => "✓",
                    "Warning" => "⚠",
                    "Critical" => "✗",
                    _ => "•"
                };

                var bar = BuildProgressBar(metric.Percentage);
                report.AppendLine($"│ {statusIcon} {metric.Name,-25} {metric.Value,10} {metric.Unit,-6} {bar}");
            }
            report.AppendLine("└────────────────────────────────────────────────────────────────┘");
            report.AppendLine();

            if (snapshot.ActiveAlerts.Count > 0)
            {
                report.AppendLine("┌─ ACTIVE ALERTS ────────────────────────────────────────────────┐");
                foreach (var alert in snapshot.ActiveAlerts.Take(5))
                {
                    var icon = alert.Severity switch
                    {
                        AlertSeverity.Info => "ℹ",
                        AlertSeverity.Warning => "⚠",
                        AlertSeverity.Critical => "✗",
                        _ => "•"
                    };

                    report.AppendLine($"│ {icon} [{alert.Severity}] {alert.Title}");
                    report.AppendLine($"│   {alert.Message}");
                }
                report.AppendLine("└────────────────────────────────────────────────────────────────┘");
            }

            return report.ToString();
        }

        /// <summary>
        /// Build progress bar
        /// </summary>
        private string BuildProgressBar(double percentage, int width = 15)
        {
            int filled = (int)((percentage / 100.0) * width);
            var bar = new StringBuilder("[");
            bar.Append('█', filled);
            bar.Append('░', width - filled);
            bar.Append($"] {percentage:F0}%");
            return bar.ToString();
        }

        /// <summary>
        /// Update loop
        /// </summary>
        private async Task UpdateLoopAsync(int intervalMs, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);

                    // Record snapshot
                    var monitorSnapshot = _monitor.GetPerformanceSnapshot(_adaptiveOptimizer.GetResourceState());
                    _aggregator.RecordSnapshot(monitorSnapshot);

                    // Detect and alert on bottlenecks
                    var bottlenecks = _aggregator.DetectBottlenecks(monitorSnapshot);
                    foreach (var bottleneck in bottlenecks)
                    {
                        if (bottleneck.Severity > 80)
                        {
                            CreateAlert(
                                $"{bottleneck.BottleneckType} Bottleneck",
                                bottleneck.Description,
                                AlertSeverity.Critical);
                        }
                        else if (bottleneck.Severity > 50)
                        {
                            CreateAlert(
                                $"{bottleneck.BottleneckType} Warning",
                                bottleneck.Description,
                                AlertSeverity.Warning);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }
    }
}
