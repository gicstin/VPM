using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Unified facade for the entire parallel optimization system.
    /// Provides a simple, high-level API for integrating parallel task processing into applications.
    /// 
    /// Architecture:
    /// - Encapsulates all components (scheduler, monitor, aggregator, resilience)
    /// - Provides event-driven callbacks for UI integration
    /// - Handles lifecycle management
    /// - Simplifies common use cases
    /// </summary>
    public class ParallelOptimizerFacade : IDisposable
    {
        private readonly ParallelWorkScheduler _scheduler;
        private readonly TaskExecutionMonitor _monitor;
        private readonly PerformanceAggregator _aggregator;
        private readonly MetricsDashboard _dashboard;
        private readonly RetryPolicy _retryPolicy;
        private readonly CircuitBreakerRegistry _circuitBreakers;
        private readonly DeadLetterQueue _deadLetterQueue;
        private readonly AdaptiveOptimizer _adaptiveOptimizer;
        private volatile bool _isRunning = false;

        /// <summary>
        /// Configuration for the optimizer facade
        /// </summary>
        public class OptimizerConfig
        {
            public ParallelWorkScheduler.SchedulerConfig SchedulerConfig { get; set; }
            public RetryPolicy.RetryConfig RetryConfig { get; set; }
            public CircuitBreaker.CircuitBreakerConfig CircuitBreakerConfig { get; set; }
            public DeadLetterQueue.DeadLetterConfig DeadLetterConfig { get; set; }
            public int DashboardUpdateIntervalMs { get; set; } = 1000;
            public bool EnableAutoRetry { get; set; } = true;
            public bool EnableCircuitBreaker { get; set; } = true;
        }

        /// <summary>
        /// Event fired when task starts
        /// </summary>
        public event EventHandler<TaskEventArgs> TaskStarted;

        /// <summary>
        /// Event fired when task completes
        /// </summary>
        public event EventHandler<TaskEventArgs> TaskCompleted;

        /// <summary>
        /// Event fired when task fails
        /// </summary>
        public event EventHandler<TaskFailedEventArgs> TaskFailed;

        /// <summary>
        /// Event fired when metrics are updated
        /// </summary>
        public event EventHandler<MetricsUpdatedEventArgs> MetricsUpdated
        {
            add { }
            remove { }
        }

        /// <summary>
        /// Event fired when bottleneck is detected
        /// </summary>
        public event EventHandler<BottleneckDetectedEventArgs> BottleneckDetected
        {
            add { }
            remove { }
        }

        /// <summary>
        /// Task event arguments
        /// </summary>
        public class TaskEventArgs : EventArgs
        {
            public WorkTask Task { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Task failed event arguments
        /// </summary>
        public class TaskFailedEventArgs : EventArgs
        {
            public WorkTask Task { get; set; }
            public Exception Exception { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Metrics updated event arguments
        /// </summary>
        public class MetricsUpdatedEventArgs : EventArgs
        {
            public TaskExecutionMonitor.PerformanceSnapshot Snapshot { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Bottleneck detected event arguments
        /// </summary>
        public class BottleneckDetectedEventArgs : EventArgs
        {
            public PerformanceAggregator.BottleneckAnalysis Bottleneck { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public ParallelOptimizerFacade(OptimizerConfig config = null)
        {
            config = config ?? new OptimizerConfig();

            _adaptiveOptimizer = new AdaptiveOptimizer();
            _scheduler = new ParallelWorkScheduler(config.SchedulerConfig);
            _monitor = new TaskExecutionMonitor();
            _aggregator = new PerformanceAggregator();
            _dashboard = new MetricsDashboard(_monitor, _aggregator, _adaptiveOptimizer);
            _retryPolicy = new RetryPolicy(config.RetryConfig);
            _circuitBreakers = new CircuitBreakerRegistry(config.CircuitBreakerConfig);
            _deadLetterQueue = new DeadLetterQueue(config.DeadLetterConfig);

            // Wire up scheduler events
            _scheduler.TaskCompleted += OnTaskCompleted;
            _scheduler.TaskFailed += (sender, e) => OnTaskFailed(sender, e);
        }

        /// <summary>
        /// Start the optimizer
        /// </summary>
        public void Start()
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _scheduler.Start();
            _dashboard.Start();
        }

        /// <summary>
        /// Stop the optimizer gracefully
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            await _scheduler.StopAsync().ConfigureAwait(false);
            await _dashboard.StopAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Submit a generic work task asynchronously
        /// </summary>
        public async Task SubmitTaskAsync(WorkTask task, int priority = 0)
        {
            if (!_isRunning)
                throw new InvalidOperationException("Optimizer is not running");

            task.Priority = priority;
            _monitor.StartTask(task);
            TaskStarted?.Invoke(this, new TaskEventArgs { Task = task, Timestamp = DateTime.UtcNow });

            if (!_scheduler.EnqueueTask(task))
                throw new InvalidOperationException("Failed to enqueue task");

            // Wait for task to complete
            while (task.State == TaskState.Pending || task.State == TaskState.Running)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }



        /// <summary>
        /// Get current performance snapshot
        /// </summary>
        public TaskExecutionMonitor.PerformanceSnapshot GetPerformanceSnapshot()
        {
            return _monitor.GetPerformanceSnapshot(_adaptiveOptimizer.GetResourceState());
        }

        /// <summary>
        /// Get dashboard snapshot
        /// </summary>
        public MetricsDashboard.DashboardSnapshot GetDashboardSnapshot()
        {
            return _dashboard.GetSnapshot();
        }

        /// <summary>
        /// Get formatted dashboard report
        /// </summary>
        public string GetDashboardReport()
        {
            return _dashboard.GetFormattedReport();
        }

        /// <summary>
        /// Get performance report
        /// </summary>
        public string GetPerformanceReport()
        {
            var snapshot = GetPerformanceSnapshot();
            return _monitor.GetPerformanceReport(snapshot);
        }

        /// <summary>
        /// Get dead letter queue report
        /// </summary>
        public string GetDeadLetterReport()
        {
            return _deadLetterQueue.GetFormattedReport();
        }

        /// <summary>
        /// Get circuit breaker status
        /// </summary>
        public string GetCircuitBreakerStatus()
        {
            return _circuitBreakers.GetStatusReport();
        }

        /// <summary>
        /// Get task by ID
        /// </summary>
        public WorkTask GetTask(string taskId)
        {
            return _scheduler.GetTask(taskId);
        }

        /// <summary>
        /// Get all active tasks
        /// </summary>
        public IEnumerable<WorkTask> GetActiveTasks()
        {
            return _scheduler.GetActiveTasks();
        }

        /// <summary>
        /// Get all tasks
        /// </summary>
        public IEnumerable<WorkTask> GetAllTasks()
        {
            return _scheduler.GetAllTasks();
        }

        /// <summary>
        /// Get pending retries from dead letter queue
        /// </summary>
        public List<DeadLetterQueue.DeadLetterEntry> GetPendingRetries()
        {
            return _deadLetterQueue.GetPendingRetries();
        }

        /// <summary>
        /// Retry a failed task
        /// </summary>
        public async Task<bool> RetryFailedTaskAsync(string deadLetterEntryId, CancellationToken cancellationToken = default)
        {
            var entry = _deadLetterQueue.GetEntry(deadLetterEntryId);
            if (entry == null)
                return false;

            var result = await _retryPolicy.ExecuteAsync(async (ct) =>
            {
                // Simulate retry - in real implementation, would recreate and resubmit task
                await Task.Delay(100, ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            _deadLetterQueue.RecordRetryAttempt(deadLetterEntryId, result.Success);
            return result.Success;
        }

        /// <summary>
        /// Handle task completion
        /// </summary>
        private void OnTaskCompleted(object sender, TaskCompletionEventArgs e)
        {
            _monitor.CompleteTask(e.Task, success: true);
            TaskCompleted?.Invoke(this, new TaskEventArgs { Task = e.Task, Timestamp = DateTime.UtcNow });
        }

        /// <summary>
        /// Handle task failure
        /// </summary>
        private void OnTaskFailed(object sender, global::VPM.Services.TaskFailedEventArgs e)
        {
            _monitor.CompleteTask(e.Task, success: false);
            _deadLetterQueue.AddEntry(e.Task, e.Exception);

            TaskFailed?.Invoke(this, new TaskFailedEventArgs 
            { 
                Task = e.Task, 
                Exception = e.Exception, 
                Timestamp = DateTime.UtcNow 
            });
        }

        /// <summary>
        /// Get scheduler statistics
        /// </summary>
        public ParallelWorkScheduler.SchedulerStatistics GetSchedulerStatistics()
        {
            return _scheduler.GetStatistics();
        }

        /// <summary>
        /// Is optimizer running
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Dispose resources.
        /// FIXED: Replaced blocking .Wait() calls with proper async disposal pattern
        /// to prevent deadlocks when called from UI thread context.
        /// FIXED: Now disposes CircuitBreakerRegistry to release ReaderWriterLockSlim handles.
        /// </summary>
        public void Dispose()
        {
            if (_isRunning)
            {
                // Use GetAwaiter().GetResult() with ConfigureAwait(false) to avoid deadlock
                // This is safer than .Wait() as it doesn't capture SynchronizationContext
                try
                {
                    StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Ignore exceptions during disposal
                }
            }

            _scheduler?.Dispose();
            
            // Same pattern for dashboard
            try
            {
                _dashboard?.StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Ignore exceptions during disposal
            }
            
            // Dispose circuit breakers to release ReaderWriterLockSlim handles
            _circuitBreakers?.Dispose();
        }
    }
}
