using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Adaptive optimization engine that dynamically adjusts parallelism based on system resources.
    /// Monitors CPU, memory, and I/O to prevent resource exhaustion while maximizing throughput.
    /// 
    /// Now integrated with ParallelOptimizerFacade for true parallel task execution.
    /// 
    /// Benefits:
    /// - Stable performance across different systems
    /// - Prevents OOM and CPU throttling
    /// - Automatic backpressure handling
    /// - Real-time resource monitoring
    /// - Parallel task execution with work queue
    /// - Event-driven architecture for UI integration
    /// </summary>
    public class AdaptiveOptimizer : IDisposable
    {
        /// <summary>
        /// System resource state snapshot
        /// </summary>
        public class ResourceState
        {
            public double CPUUsagePercent { get; set; }
            public double MemoryUsagePercent { get; set; }
            public long AvailableMemoryMB { get; set; }
            public int ThreadCount { get; set; }
            public ResourcePressure Pressure { get; set; }
        }

        /// <summary>
        /// Resource pressure levels
        /// </summary>
        public enum ResourcePressure
        {
            Low,        // <50% CPU, <60% memory
            Moderate,   // 50-75% CPU, 60-80% memory
            High,       // 75-90% CPU, 80-90% memory
            Critical    // >90% CPU, >90% memory
        }

        /// <summary>
        /// Adaptive concurrency configuration
        /// </summary>
        public class AdaptiveConfig
        {
            public int MinConcurrency { get; set; } = 1;
            public int MaxConcurrency { get; set; } = Environment.ProcessorCount;
            public int TargetConcurrency { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);
            public long MemoryThresholdMB { get; set; } = 500; // MB
            public double CPUThresholdPercent { get; set; } = 80; // %
            public int AdjustmentIntervalMs { get; set; } = 5000; // 5 seconds
            public bool EnableAdaptiveAdjustment { get; set; } = true;
        }

        private readonly AdaptiveConfig _config;
        private readonly Process _currentProcess;
        private int _currentConcurrency;
        private DateTime _lastAdjustmentTime;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private PerformanceCounter _cpuCounter;

        public AdaptiveOptimizer(AdaptiveConfig config = null)
        {
            _config = config ?? new AdaptiveConfig();
            _currentProcess = Process.GetCurrentProcess();
            _currentConcurrency = _config.TargetConcurrency;
            _lastAdjustmentTime = DateTime.UtcNow;
            
            try
            {
                // Initialize CPU counter once
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _cpuCounter.NextValue(); // First call returns 0
            }
            catch
            {
                // Ignore if performance counters are not available
            }
        }

        /// <summary>
        /// Gets current system resource state.
        /// </summary>
        public ResourceState GetResourceState()
        {
            try
            {
                _currentProcess.Refresh();

                // Get memory info
                long totalMemory = GC.GetTotalMemory(false);
                long workingSet = _currentProcess.WorkingSet64;
                long totalSystemMemory = GC.GetGCMemoryInfo().TotalCommittedBytes;
                
                // Estimate available memory (simplified)
                long availableMemory = Math.Max(0, totalSystemMemory - workingSet);
                double memoryUsagePercent = totalSystemMemory > 0 ? (workingSet * 100.0) / totalSystemMemory : 0;

                // Get CPU usage (simplified - would need performance counter for accurate reading)
                double cpuUsage = GetCPUUsage();

                var pressure = DeterminePressure(cpuUsage, memoryUsagePercent);

                return new ResourceState
                {
                    CPUUsagePercent = cpuUsage,
                    MemoryUsagePercent = memoryUsagePercent,
                    AvailableMemoryMB = availableMemory / 1024 / 1024,
                    ThreadCount = _currentProcess.Threads.Count,
                    Pressure = pressure
                };
            }
            catch
            {
                // Return neutral state on error
                return new ResourceState
                {
                    CPUUsagePercent = 50,
                    MemoryUsagePercent = 50,
                    AvailableMemoryMB = 1024,
                    ThreadCount = Environment.ProcessorCount,
                    Pressure = ResourcePressure.Moderate
                };
            }
        }

        /// <summary>
        /// Determines resource pressure level.
        /// </summary>
        private ResourcePressure DeterminePressure(double cpuUsage, double memoryUsage)
        {
            if (cpuUsage > 90 || memoryUsage > 90)
                return ResourcePressure.Critical;
            else if (cpuUsage > 75 || memoryUsage > 80)
                return ResourcePressure.High;
            else if (cpuUsage > 50 || memoryUsage > 60)
                return ResourcePressure.Moderate;
            else
                return ResourcePressure.Low;
        }

        /// <summary>
        /// Gets estimated CPU usage percentage (synchronous version).
        /// </summary>
        private double GetCPUUsage()
        {
            if (_cpuCounter == null) return 50; // Default to moderate if unable to read
            
            try
            {
                return _cpuCounter.NextValue();
            }
            catch
            {
                return 50; // Default to moderate if unable to read
            }
        }

        /// <summary>
        /// Gets estimated CPU usage percentage (async version).
        /// </summary>
        private Task<double> GetCPUUsageAsync()
        {
            return Task.FromResult(GetCPUUsage());
        }

        /// <summary>
        /// Gets current adaptive concurrency level.
        /// </summary>
        public int GetCurrentConcurrency()
        {
            _lock.EnterReadLock();
            try
            {
                return _currentConcurrency;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Adjusts concurrency based on current resource state.
        /// </summary>
        public int AdjustConcurrency()
        {
            if (!_config.EnableAdaptiveAdjustment)
                return _currentConcurrency;

            // Check if enough time has passed since last adjustment
            if ((DateTime.UtcNow - _lastAdjustmentTime).TotalMilliseconds < _config.AdjustmentIntervalMs)
                return _currentConcurrency;

            _lock.EnterWriteLock();
            try
            {
                var resourceState = GetResourceState();
                int newConcurrency = _currentConcurrency;

                switch (resourceState.Pressure)
                {
                    case ResourcePressure.Low:
                        // Increase concurrency if we're not at max
                        if (_currentConcurrency < _config.MaxConcurrency)
                            newConcurrency = Math.Min(_config.MaxConcurrency, _currentConcurrency + 1);
                        break;

                    case ResourcePressure.Moderate:
                        // Keep current concurrency
                        newConcurrency = _currentConcurrency;
                        break;

                    case ResourcePressure.High:
                        // Decrease concurrency
                        if (_currentConcurrency > _config.MinConcurrency)
                            newConcurrency = Math.Max(_config.MinConcurrency, _currentConcurrency - 1);
                        break;

                    case ResourcePressure.Critical:
                        // Aggressively decrease concurrency
                        newConcurrency = _config.MinConcurrency;
                        break;
                }

                if (newConcurrency != _currentConcurrency)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AdaptiveOptimizer] Concurrency adjusted: {_currentConcurrency} → {newConcurrency} " +
                        $"(CPU: {resourceState.CPUUsagePercent:F1}%, Memory: {resourceState.MemoryUsagePercent:F1}%)");
                    
                    _currentConcurrency = newConcurrency;
                }

                _lastAdjustmentTime = DateTime.UtcNow;
                return _currentConcurrency;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Waits for resources to become available if under pressure.
        /// Implements backpressure handling.
        /// </summary>
        public async Task WaitForResourcesIfNeededAsync()
        {
            var resourceState = GetResourceState();

            switch (resourceState.Pressure)
            {
                case ResourcePressure.High:
                    // Wait 100ms if high pressure
                    await Task.Delay(100).ConfigureAwait(false);
                    break;

                case ResourcePressure.Critical:
                    // Wait 500ms if critical pressure
                    await Task.Delay(500).ConfigureAwait(false);
                    break;
            }
        }

        /// <summary>
        /// Calculates optimal concurrency for a specific operation type.
        /// </summary>
        public int CalculateOptimalConcurrency(string operationType)
        {
            var resourceState = GetResourceState();
            int baseConcurrency = _config.TargetConcurrency;

            return operationType?.ToLowerInvariant() switch
            {
                "io" => Math.Min(_config.MaxConcurrency, baseConcurrency * 2), // I/O-bound: more threads
                "cpu" => baseConcurrency, // CPU-bound: use target
                "memory" => Math.Max(_config.MinConcurrency, baseConcurrency / 2), // Memory-intensive: fewer threads
                "texture" => Math.Max(1, baseConcurrency / 2), // Texture conversion: very memory-intensive
                _ => baseConcurrency
            };
        }

        /// <summary>
        /// Adjusts concurrency based on operation type and current resources.
        /// </summary>
        public int GetAdaptiveConcurrency(string operationType)
        {
            int baseConcurrency = CalculateOptimalConcurrency(operationType);
            var resourceState = GetResourceState();

            // Further reduce if under pressure
            switch (resourceState.Pressure)
            {
                case ResourcePressure.High:
                    return Math.Max(_config.MinConcurrency, baseConcurrency - 1);
                case ResourcePressure.Critical:
                    return _config.MinConcurrency;
                default:
                    return baseConcurrency;
            }
        }

        /// <summary>
        /// Generates a diagnostic report of current resource state.
        /// </summary>
        public string GenerateResourceReport()
        {
            var state = GetResourceState();
            return $@"
╔════════════════════════════════════════════════════════════╗
║           ADAPTIVE OPTIMIZER RESOURCE REPORT               ║
╠════════════════════════════════════════════════════════════╣
║ Current Concurrency:      {_currentConcurrency,40}  ║
║ Target Concurrency:       {_config.TargetConcurrency,40}  ║
║ Max Concurrency:          {_config.MaxConcurrency,40}  ║
╠════════════════════════════════════════════════════════════╣
║ CPU Usage:                {state.CPUUsagePercent,39}% ║
║ Memory Usage:             {state.MemoryUsagePercent,39}% ║
║ Available Memory:         {state.AvailableMemoryMB,40} MB ║
║ Thread Count:             {state.ThreadCount,40}  ║
║ Resource Pressure:        {state.Pressure,40}  ║
╠════════════════════════════════════════════════════════════╣
║ Recommended Concurrency:                                   ║
║   I/O-bound:              {CalculateOptimalConcurrency("io"),40}  ║
║   CPU-bound:              {CalculateOptimalConcurrency("cpu"),40}  ║
║   Memory-intensive:       {CalculateOptimalConcurrency("memory"),40}  ║
║   Texture conversion:     {CalculateOptimalConcurrency("texture"),40}  ║
╚════════════════════════════════════════════════════════════╝
";
        }

        /// <summary>
        /// Resets concurrency to target value.
        /// </summary>
        public void Reset()
        {
            _lock.EnterWriteLock();
            try
            {
                _currentConcurrency = _config.TargetConcurrency;
                _lastAdjustmentTime = DateTime.UtcNow;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Parallel optimizer facade instance for task-based parallelism.
        /// Lazily initialized on first use.
        /// </summary>
        private ParallelOptimizerFacade _parallelOptimizer;
        private readonly SemaphoreSlim _parallelLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Gets or creates the parallel optimizer facade.
        /// </summary>
        public ParallelOptimizerFacade GetParallelOptimizer()
        {
            if (_parallelOptimizer != null)
            {
                System.Diagnostics.Debug.WriteLine("[PARALLEL_OPT] Returning existing parallel optimizer instance");
                return _parallelOptimizer;
            }

            _parallelLock.Wait();
            try
            {
                if (_parallelOptimizer != null)
                    return _parallelOptimizer;

                System.Diagnostics.Debug.WriteLine("[PARALLEL_OPT] Initializing new parallel optimizer facade...");
                // Configure parallel optimizer with adaptive settings
                var config = new ParallelOptimizerFacade.OptimizerConfig
                {
                    SchedulerConfig = new ParallelWorkScheduler.SchedulerConfig
                    {
                        MinWorkers = _config.MinConcurrency,
                        MaxWorkers = _config.MaxConcurrency,
                        QueueMaxSize = 10000,
                        WorkerIdleTimeoutMs = 5000,
                        ResourceCheckIntervalMs = 1000,
                        EnableAdaptiveScaling = _config.EnableAdaptiveAdjustment
                    },
                    RetryConfig = new RetryPolicy.RetryConfig
                    {
                        MaxRetries = 3,
                        InitialDelayMs = 100,
                        MaxDelayMs = 5000,
                        BackoffMultiplier = 2.0
                    },
                    CircuitBreakerConfig = new CircuitBreaker.CircuitBreakerConfig
                    {
                        FailureThreshold = 5,
                        FailureWindowMs = 60000
                    },
                    DeadLetterConfig = new DeadLetterQueue.DeadLetterConfig
                    {
                        MaxEntries = 1000,
                        RetentionTimeMs = 3600000 // 1 hour
                    },
                    DashboardUpdateIntervalMs = 1000,
                    EnableAutoRetry = true,
                    EnableCircuitBreaker = true
                };

                _parallelOptimizer = new ParallelOptimizerFacade(config);
                _parallelOptimizer.Start();

                return _parallelOptimizer;
            }
            finally
            {
                _parallelLock.Release();
            }
        }

        /// <summary>
        /// Submits a work task to the parallel optimizer.
        /// </summary>
        public async Task SubmitTaskAsync(WorkTask task, int priority = 0)
        {
            var optimizer = GetParallelOptimizer();
            await optimizer.SubmitTaskAsync(task, priority).ConfigureAwait(false);
            
            if (task.State == TaskState.Failed)
                throw new InvalidOperationException($"Task failed: {task.ErrorMessage}", task.Exception);
        }

        /// <summary>
        /// Stops the parallel optimizer gracefully.
        /// </summary>
        public async Task StopParallelOptimizerAsync()
        {
            if (_parallelOptimizer == null)
                return;

            await _parallelLock.WaitAsync();
            try
            {
                if (_parallelOptimizer == null)
                    return;

                var optimizer = _parallelOptimizer;
                _parallelOptimizer = null;
                
                // Stop asynchronously without blocking
                _ = optimizer.StopAsync().ContinueWith(t =>
                {
                    optimizer.Dispose();
                });
            }
            finally
            {
                _parallelLock.Release();
            }
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            StopParallelOptimizerAsync().Wait(5000);
            _parallelOptimizer?.Dispose();
            _cpuCounter?.Dispose();
        }
    }

    /// <summary>
    /// Helper class for adaptive semaphore that adjusts based on resource pressure.
    /// </summary>
    public class AdaptiveSemaphore : IDisposable
    {
        private readonly AdaptiveOptimizer _optimizer;
        private readonly Semaphore _semaphore;
        private int _currentCount;

        public AdaptiveSemaphore(AdaptiveOptimizer optimizer, int initialCount)
        {
            _optimizer = optimizer;
            _currentCount = initialCount;
            _semaphore = new Semaphore(initialCount, initialCount);
        }

        /// <summary>
        /// Waits for a slot, adjusting based on resource pressure.
        /// </summary>
        public async Task WaitAsync()
        {
            await _optimizer.WaitForResourcesIfNeededAsync().ConfigureAwait(false);
            _semaphore.WaitOne();
        }

        /// <summary>
        /// Waits with timeout.
        /// </summary>
        public async Task<bool> WaitAsync(int timeoutMs)
        {
            await _optimizer.WaitForResourcesIfNeededAsync().ConfigureAwait(false);
            return _semaphore.WaitOne(timeoutMs);
        }

        /// <summary>
        /// Releases a slot.
        /// </summary>
        public void Release()
        {
            _semaphore.Release();
        }

        public void Dispose()
        {
            _semaphore?.Dispose();
        }
    }
}
