using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Central orchestration engine for parallel work task execution.
    /// Manages task scheduling, worker threads, resource allocation, and backpressure.
    /// 
    /// Architecture:
    /// - Work queue with priority support
    /// - Adaptive worker pool based on system resources
    /// - Integration with AdaptiveOptimizer for dynamic concurrency
    /// - Comprehensive task tracking and monitoring
    /// - Graceful shutdown with task completion
    /// 
    /// Features:
    /// - Automatic worker scaling based on resource pressure
    /// - Task priority support
    /// - Cancellation token propagation
    /// - Real-time statistics and diagnostics
    /// - Backpressure handling (queue size limits)
    /// </summary>
    public class ParallelWorkScheduler : IDisposable
    {
        private readonly WorkQueue _workQueue;
        private readonly AdaptiveOptimizer _adaptiveOptimizer;
        private readonly ConcurrentDictionary<string, WorkTask> _allTasks;
        private readonly ConcurrentDictionary<string, WorkTask> _activeTasks;
        private readonly List<Task> _workerTasks;
        private readonly object _workerLock = new object();
        private CancellationTokenSource _schedulerCts;
        private volatile bool _isRunning = false;
        private volatile int _currentWorkerCount = 0;
        private int _targetWorkerCount;
        private readonly int _minWorkers;
        private readonly int _maxWorkers;
        private readonly int _queueMaxSize;
        private long _totalTasksProcessed = 0;
        private long _totalTasksFailed = 0;

        /// <summary>
        /// Scheduler configuration
        /// </summary>
        public class SchedulerConfig
        {
            public int MinWorkers { get; set; } = 2;
            public int MaxWorkers { get; set; } = Environment.ProcessorCount;
            public int QueueMaxSize { get; set; } = 10000;
            public int WorkerIdleTimeoutMs { get; set; } = 5000;
            public int ResourceCheckIntervalMs { get; set; } = 1000;
            public bool EnableAdaptiveScaling { get; set; } = true;
        }

        /// <summary>
        /// Scheduler statistics
        /// </summary>
        public class SchedulerStatistics
        {
            public int CurrentWorkerCount { get; set; }
            public int TargetWorkerCount { get; set; }
            public int ActiveTaskCount { get; set; }
            public long TotalTasksProcessed { get; set; }
            public long TotalTasksFailed { get; set; }
            public WorkQueue.QueueStatistics QueueStats { get; set; }
            public AdaptiveOptimizer.ResourceState ResourceState { get; set; }
            public double AverageTaskDuration { get; set; }
            public bool IsRunning { get; set; }
        }

        /// <summary>
        /// Task completion event
        /// </summary>
        public event EventHandler<TaskCompletionEventArgs> TaskCompleted;

        /// <summary>
        /// Task failed event
        /// </summary>
        public event EventHandler<TaskFailedEventArgs> TaskFailed;

        /// <summary>
        /// Worker count changed event
        /// </summary>
        public event EventHandler<WorkerCountChangedEventArgs> WorkerCountChanged;

        public ParallelWorkScheduler(SchedulerConfig config = null)
        {
            config = config ?? new SchedulerConfig();
            _minWorkers = config.MinWorkers;
            _maxWorkers = config.MaxWorkers;
            _queueMaxSize = config.QueueMaxSize;
            _targetWorkerCount = config.MinWorkers;

            _workQueue = new WorkQueue(config.QueueMaxSize);
            _adaptiveOptimizer = new AdaptiveOptimizer();
            _allTasks = new ConcurrentDictionary<string, WorkTask>();
            _activeTasks = new ConcurrentDictionary<string, WorkTask>();
            _workerTasks = new List<Task>();
            _schedulerCts = new CancellationTokenSource();
        }

        /// <summary>
        /// Start the scheduler and worker threads
        /// </summary>
        public void Start()
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _schedulerCts = new CancellationTokenSource();

            lock (_workerLock)
            {
                // Start initial workers
                for (int i = 0; i < _targetWorkerCount; i++)
                {
                    SpawnWorker();
                }
            }

            // Start resource monitoring task
            _ = MonitorResourcesAsync(_schedulerCts.Token);
        }

        /// <summary>
        /// Stop the scheduler gracefully
        /// </summary>
        public async Task StopAsync(int timeoutMs = 30000)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _schedulerCts.Cancel();

            try
            {
                await Task.WhenAll(_workerTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }

            _workerTasks.Clear();
            _currentWorkerCount = 0;
        }

        /// <summary>
        /// Enqueue a task for execution
        /// </summary>
        public bool EnqueueTask(WorkTask task, int timeoutMs = -1)
        {
            if (task == null || !_isRunning)
            {
                System.Diagnostics.Debug.WriteLine($"[SCHEDULER_ENQUEUE_FAIL] Task={task?.TaskName} IsRunning={_isRunning}");
                return false;
            }

            if (!_workQueue.TryEnqueue(task, timeoutMs))
            {
                System.Diagnostics.Debug.WriteLine($"[SCHEDULER_ENQUEUE_FAIL] Queue full for {task.TaskName}");
                return false;
            }

            _allTasks.TryAdd(task.TaskId, task);
            System.Diagnostics.Debug.WriteLine($"[SCHEDULER_ENQUEUE_SUCCESS] Task={task.TaskName} Priority={task.Priority} QueueSize={_workQueue.GetStatistics().CurrentSize}");
            return true;
        }

        /// <summary>
        /// Get task by ID
        /// </summary>
        public WorkTask GetTask(string taskId)
        {
            _allTasks.TryGetValue(taskId, out var task);
            return task;
        }

        /// <summary>
        /// Get all tasks
        /// </summary>
        public IEnumerable<WorkTask> GetAllTasks()
        {
            return _allTasks.Values.ToList();
        }

        /// <summary>
        /// Get active tasks
        /// </summary>
        public IEnumerable<WorkTask> GetActiveTasks()
        {
            return _activeTasks.Values.ToList();
        }

        /// <summary>
        /// Get scheduler statistics
        /// </summary>
        public SchedulerStatistics GetStatistics()
        {
            var resourceState = _adaptiveOptimizer.GetResourceState();
            var queueStats = _workQueue.GetStatistics();
            var allTasks = _allTasks.Values.ToList();
            var avgDuration = allTasks.Where(t => t.Duration.HasValue).Average(t => t.Duration.Value.TotalMilliseconds);

            return new SchedulerStatistics
            {
                CurrentWorkerCount = _currentWorkerCount,
                TargetWorkerCount = _targetWorkerCount,
                ActiveTaskCount = _activeTasks.Count,
                TotalTasksProcessed = _totalTasksProcessed,
                TotalTasksFailed = _totalTasksFailed,
                QueueStats = queueStats,
                ResourceState = resourceState,
                AverageTaskDuration = double.IsNaN(avgDuration) ? 0 : avgDuration,
                IsRunning = _isRunning
            };
        }

        /// <summary>
        /// Worker thread main loop
        /// </summary>
        private async Task WorkerAsync(int workerId, CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"[WORKER_START] Worker {workerId} started on thread {System.Threading.Thread.CurrentThread.ManagedThreadId}");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!_workQueue.TryDequeue(out var task, 1000))
                    {
                        // Queue is empty, check if we should scale down
                        if (_currentWorkerCount > _targetWorkerCount)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WORKER_EXIT] Worker {workerId} exiting (scale down)");
                            break; // Exit this worker
                        }
                        continue;
                    }

                    try
                    {
                        _activeTasks.TryAdd(task.TaskId, task);
                        task.UpdateState(TaskState.Running);
                        System.Diagnostics.Debug.WriteLine($"[WORKER_EXECUTE] Worker {workerId} executing {task.TaskName} (ID={task.TaskId})");

                        var startTime = DateTime.UtcNow;
                        using (var taskCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            await task.ExecuteAsync(taskCts.Token).ConfigureAwait(false);
                        }
                        var elapsed = DateTime.UtcNow - startTime;

                        if (task.State != TaskState.Cancelled)
                        {
                            task.MarkCompleted();
                            Interlocked.Increment(ref _totalTasksProcessed);
                            System.Diagnostics.Debug.WriteLine($"[WORKER_COMPLETE] Worker {workerId} completed {task.TaskName} in {elapsed.TotalMilliseconds:F0}ms");
                            TaskCompleted?.Invoke(this, new TaskCompletionEventArgs(task));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        task.UpdateState(TaskState.Cancelled);
                        System.Diagnostics.Debug.WriteLine($"[WORKER_CANCELLED] Worker {workerId} cancelled {task.TaskName}");
                    }
                    catch (Exception ex)
                    {
                        task.MarkFailed($"Task execution failed: {ex.Message}", ex);
                        Interlocked.Increment(ref _totalTasksFailed);
                        System.Diagnostics.Debug.WriteLine($"[WORKER_FAILED] Worker {workerId} failed {task.TaskName}: {ex.Message}");
                        TaskFailed?.Invoke(this, new TaskFailedEventArgs(task, ex));
                    }
                    finally
                    {
                        _activeTasks.TryRemove(task.TaskId, out _);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WORKER_FATAL] Worker {workerId} encountered fatal error: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _currentWorkerCount);
                System.Diagnostics.Debug.WriteLine($"[WORKER_EXIT] Worker {workerId} exited");
            }
        }

        /// <summary>
        /// Spawn a new worker thread
        /// </summary>
        private void SpawnWorker()
        {
            int workerId = Interlocked.Increment(ref _currentWorkerCount);
            var workerTask = WorkerAsync(workerId, _schedulerCts.Token);
            _workerTasks.Add(workerTask);
        }

        /// <summary>
        /// Monitor system resources and adjust worker count
        /// </summary>
        private async Task MonitorResourcesAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

                    if (!_isRunning)
                        break;

                    var resourceState = _adaptiveOptimizer.GetResourceState();

                    // Adjust worker count based on resource pressure
                    int newTargetWorkers = CalculateTargetWorkers(resourceState);

                    if (newTargetWorkers != _targetWorkerCount)
                    {
                        int oldTarget = _targetWorkerCount;
                        _targetWorkerCount = newTargetWorkers;

                        lock (_workerLock)
                        {
                            if (newTargetWorkers > _currentWorkerCount)
                            {
                                // Spawn additional workers
                                for (int i = _currentWorkerCount; i < newTargetWorkers; i++)
                                {
                                    SpawnWorker();
                                }
                            }
                        }

                        WorkerCountChanged?.Invoke(this, new WorkerCountChangedEventArgs(oldTarget, newTargetWorkers));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        /// <summary>
        /// Calculate target worker count based on resource state
        /// </summary>
        private int CalculateTargetWorkers(AdaptiveOptimizer.ResourceState resourceState)
        {
            int target = _targetWorkerCount;

            switch (resourceState.Pressure)
            {
                case AdaptiveOptimizer.ResourcePressure.Low:
                    // Can scale up
                    target = Math.Min(_maxWorkers, _targetWorkerCount + 1);
                    break;

                case AdaptiveOptimizer.ResourcePressure.Moderate:
                    // Maintain current level
                    target = _targetWorkerCount;
                    break;

                case AdaptiveOptimizer.ResourcePressure.High:
                    // Scale down slightly
                    target = Math.Max(_minWorkers, _targetWorkerCount - 1);
                    break;

                case AdaptiveOptimizer.ResourcePressure.Critical:
                    // Scale down significantly
                    target = _minWorkers;
                    break;
            }

            return Math.Clamp(target, _minWorkers, _maxWorkers);
        }

        public void Dispose()
        {
            _schedulerCts?.Dispose();
            _workQueue?.Clear();
        }
    }

    /// <summary>
    /// Task completion event arguments
    /// </summary>
    public class TaskCompletionEventArgs : EventArgs
    {
        public WorkTask Task { get; }

        public TaskCompletionEventArgs(WorkTask task)
        {
            Task = task;
        }
    }

    /// <summary>
    /// Task failed event arguments
    /// </summary>
    public class TaskFailedEventArgs : EventArgs
    {
        public WorkTask Task { get; }
        public Exception Exception { get; }

        public TaskFailedEventArgs(WorkTask task, Exception exception)
        {
            Task = task;
            Exception = exception;
        }
    }

    /// <summary>
    /// Worker count changed event arguments
    /// </summary>
    public class WorkerCountChangedEventArgs : EventArgs
    {
        public int OldCount { get; }
        public int NewCount { get; }

        public WorkerCountChangedEventArgs(int oldCount, int newCount)
        {
            OldCount = oldCount;
            NewCount = newCount;
        }
    }
}
