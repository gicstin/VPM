using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Comprehensive example demonstrating how to use the Parallel Optimizer system.
    /// Shows common patterns, configuration, and integration scenarios.
    /// 
    /// This file serves as both documentation and a working example.
    /// </summary>
    public class ParallelOptimizerExample
    {
        /// <summary>
        /// Example 1: Basic usage with default configuration
        /// </summary>
        public static async Task Example1_BasicUsageAsync()
        {
            // Create optimizer with default settings
            var optimizer = new ParallelOptimizerFacade();

            try
            {
                // Start the optimizer
                optimizer.Start();

                // Submit tasks
                optimizer.SubmitImageCompressionTask(
                    @"C:\input\image.jpg",
                    @"C:\output\image_compressed.jpg",
                    new ImageCompressionOptions { JpegQuality = 85 },
                    priority: 1);

                optimizer.SubmitJsonMinificationTask(
                    @"C:\input\data.json",
                    @"C:\output\data.min.json",
                    priority: 0);

                // Wait a bit for processing
                await Task.Delay(5000).ConfigureAwait(false);

                // Get and display metrics
                var report = optimizer.GetPerformanceReport();
                Console.WriteLine(report);
            }
            finally
            {
                await optimizer.StopAsync().ConfigureAwait(false);
                optimizer.Dispose();
            }
        }

        /// <summary>
        /// Example 2: Custom configuration for high-performance scenarios
        /// </summary>
        public static async Task Example2_CustomConfigurationAsync()
        {
            var config = new ParallelOptimizerFacade.OptimizerConfig
            {
                SchedulerConfig = new ParallelWorkScheduler.SchedulerConfig
                {
                    MinWorkers = 4,
                    MaxWorkers = 16,
                    QueueMaxSize = 5000,
                    EnableAdaptiveScaling = true
                },
                RetryConfig = new RetryPolicy.RetryConfig
                {
                    MaxRetries = 3,
                    InitialDelayMs = 100,
                    MaxDelayMs = 5000,
                    BackoffMultiplier = 2.0,
                    JitterFactor = 0.1
                },
                CircuitBreakerConfig = new CircuitBreaker.CircuitBreakerConfig
                {
                    FailureThreshold = 5,
                    FailureWindowMs = 60000,
                    OpenTimeoutMs = 30000
                },
                DashboardUpdateIntervalMs = 500,
                EnableAutoRetry = true,
                EnableCircuitBreaker = true
            };

            var optimizer = new ParallelOptimizerFacade(config);

            try
            {
                optimizer.Start();

                // Submit batch of tasks
                var tasks = new List<string>
                {
                    @"C:\input\file1.jpg",
                    @"C:\input\file2.jpg",
                    @"C:\input\file3.jpg"
                };

                foreach (var file in tasks)
                {
                    optimizer.SubmitImageCompressionTask(
                        file,
                        file.Replace("input", "output").Replace(".jpg", "_opt.jpg"));
                }

                await Task.Delay(10000).ConfigureAwait(false);

                var dashboard = optimizer.GetDashboardReport();
                Console.WriteLine(dashboard);
            }
            finally
            {
                await optimizer.StopAsync().ConfigureAwait(false);
                optimizer.Dispose();
            }
        }

        /// <summary>
        /// Example 3: Event-driven integration with UI
        /// </summary>
        public static async Task Example3_EventDrivenIntegrationAsync()
        {
            var optimizer = new ParallelOptimizerFacade();

            // Subscribe to events
            optimizer.TaskStarted += (sender, e) =>
            {
                Console.WriteLine($"[STARTED] {e.Task.TaskName} - {e.Timestamp:HH:mm:ss}");
            };

            optimizer.TaskCompleted += (sender, e) =>
            {
                Console.WriteLine($"[COMPLETED] {e.Task.TaskName} - Duration: {e.Task.Duration?.TotalSeconds:F2}s");
            };

            optimizer.TaskFailed += (sender, e) =>
            {
                Console.WriteLine($"[FAILED] {e.Task.TaskName} - Error: {e.Exception?.Message}");
            };

            optimizer.MetricsUpdated += (sender, e) =>
            {
                Console.WriteLine($"[METRICS] Throughput: {e.Snapshot.OverallThroughputMBps:F2} MB/s, " +
                    $"Active: {e.Snapshot.TotalActiveTasks}, " +
                    $"Completed: {e.Snapshot.TotalCompletedTasks}");
            };

            optimizer.BottleneckDetected += (sender, e) =>
            {
                Console.WriteLine($"[BOTTLENECK] {e.Bottleneck.BottleneckType} - " +
                    $"Severity: {e.Bottleneck.Severity:F0}% - {e.Bottleneck.Description}");
            };

            try
            {
                optimizer.Start();

                optimizer.SubmitImageCompressionTask(
                    @"C:\input\large_image.jpg",
                    @"C:\output\large_image_opt.jpg");

                await Task.Delay(15000).ConfigureAwait(false);
            }
            finally
            {
                await optimizer.StopAsync().ConfigureAwait(false);
                optimizer.Dispose();
            }
        }

        /// <summary>
        /// Example 4: Monitoring and diagnostics
        /// </summary>
        public static async Task Example4_MonitoringAndDiagnosticsAsync()
        {
            var optimizer = new ParallelOptimizerFacade();

            try
            {
                optimizer.Start();

                // Submit tasks
                for (int i = 0; i < 10; i++)
                {
                    optimizer.SubmitJsonMinificationTask(
                        $@"C:\input\data{i}.json",
                        $@"C:\output\data{i}.min.json");
                }

                // Monitor in real-time
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(2000).ConfigureAwait(false);

                    var stats = optimizer.GetSchedulerStatistics();
                    Console.WriteLine($"\n--- Iteration {i + 1} ---");
                    Console.WriteLine($"Active Workers: {stats.CurrentWorkerCount}/{stats.TargetWorkerCount}");
                    Console.WriteLine($"Active Tasks: {stats.ActiveTaskCount}");
                    Console.WriteLine($"Queue Stats: {stats.QueueStats.CurrentSize}/{stats.QueueStats.MaxSize}");
                    Console.WriteLine($"Completed: {stats.TotalTasksProcessed}, Failed: {stats.TotalTasksFailed}");
                    Console.WriteLine($"Avg Duration: {stats.AverageTaskDuration:F0}ms");
                }

                // Get final reports
                Console.WriteLine("\n=== FINAL REPORTS ===\n");
                Console.WriteLine(optimizer.GetPerformanceReport());
                Console.WriteLine("\n");
                Console.WriteLine(optimizer.GetDeadLetterReport());
                Console.WriteLine("\n");
                Console.WriteLine(optimizer.GetCircuitBreakerStatus());
            }
            finally
            {
                await optimizer.StopAsync().ConfigureAwait(false);
                optimizer.Dispose();
            }
        }

        /// <summary>
        /// Example 5: Error handling and resilience
        /// </summary>
        public static async Task Example5_ErrorHandlingAndResilienceAsync()
        {
            var config = new ParallelOptimizerFacade.OptimizerConfig
            {
                RetryConfig = new RetryPolicy.RetryConfig
                {
                    MaxRetries = 3,
                    InitialDelayMs = 500,
                    BackoffMultiplier = 2.0
                },
                DeadLetterConfig = new DeadLetterQueue.DeadLetterConfig
                {
                    EnableAutoRetry = true,
                    MaxEntries = 1000
                }
            };

            var optimizer = new ParallelOptimizerFacade(config);

            optimizer.TaskFailed += (sender, e) =>
            {
                Console.WriteLine($"Task failed: {e.Task.TaskName}");
                Console.WriteLine($"Error: {e.Exception?.Message}");
            };

            try
            {
                optimizer.Start();

                // Submit tasks that might fail
                optimizer.SubmitImageCompressionTask(
                    @"C:\input\nonexistent.jpg",
                    @"C:\output\result.jpg");

                await Task.Delay(5000).ConfigureAwait(false);

                // Check dead letter queue
                var dlqReport = optimizer.GetDeadLetterReport();
                Console.WriteLine(dlqReport);

                // Get pending retries
                var pendingRetries = optimizer.GetPendingRetries();
                Console.WriteLine($"\nPending retries: {pendingRetries.Count}");

                foreach (var retry in pendingRetries)
                {
                    Console.WriteLine($"  - {retry.TaskName} (Attempt {retry.RetryCount}/{retry.MaxRetries})");
                }
            }
            finally
            {
                await optimizer.StopAsync().ConfigureAwait(false);
                optimizer.Dispose();
            }
        }

        /// <summary>
        /// Example 6: Batch processing with priority
        /// </summary>
        public static async Task Example6_BatchProcessingWithPriorityAsync()
        {
            var optimizer = new ParallelOptimizerFacade();

            try
            {
                optimizer.Start();

                // High priority tasks (priority = 10)
                optimizer.SubmitImageCompressionTask(
                    @"C:\input\urgent1.jpg",
                    @"C:\output\urgent1_opt.jpg",
                    priority: 10);

                optimizer.SubmitImageCompressionTask(
                    @"C:\input\urgent2.jpg",
                    @"C:\output\urgent2_opt.jpg",
                    priority: 10);

                // Normal priority tasks (priority = 0)
                for (int i = 0; i < 5; i++)
                {
                    optimizer.SubmitJsonMinificationTask(
                        $@"C:\input\data{i}.json",
                        $@"C:\output\data{i}.min.json",
                        priority: 0);
                }

                // Low priority tasks (priority = -5)
                optimizer.SubmitArchiveCompressionTask(
                    @"C:\input\archive.zip",
                    @"C:\output\archive_opt.zip",
                    priority: -5);

                await Task.Delay(10000).ConfigureAwait(false);

                var snapshot = optimizer.GetPerformanceSnapshot();
                Console.WriteLine($"Completed: {snapshot.TotalCompletedTasks}");
                Console.WriteLine($"Failed: {snapshot.TotalFailedTasks}");
                Console.WriteLine($"Throughput: {snapshot.OverallThroughputMBps:F2} MB/s");
            }
            finally
            {
                await optimizer.StopAsync().ConfigureAwait(false);
                optimizer.Dispose();
            }
        }

        /// <summary>
        /// Example 7: Custom task type integration
        /// </summary>
        public static async Task Example7_CustomTaskIntegrationAsync()
        {
            // Create custom task
            var customTask = new CustomOptimizationTask(@"C:\input\custom.dat", @"C:\output\custom_opt.dat");

            var optimizer = new ParallelOptimizerFacade();

            try
            {
                optimizer.Start();

                // Note: For custom tasks, you would need to enqueue directly to scheduler
                // This is an advanced pattern for extending the system
                var scheduler = new ParallelWorkScheduler();
                scheduler.Start();
                scheduler.EnqueueTask(customTask);

                await Task.Delay(5000).ConfigureAwait(false);

                await scheduler.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                await optimizer.StopAsync().ConfigureAwait(false);
                optimizer.Dispose();
            }
        }

        /// <summary>
        /// Custom task example for extending the system
        /// </summary>
        private class CustomOptimizationTask : WorkTask
        {
            private readonly string _inputPath;
            private readonly string _outputPath;

            public CustomOptimizationTask(string inputPath, string outputPath)
            {
                _inputPath = inputPath;
                _outputPath = outputPath;
                TaskName = $"Custom Optimization: {System.IO.Path.GetFileName(inputPath)}";
                TotalWorkUnits = 100;
            }

            public override string GetTaskType() => "CustomOptimization";

            public override async System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken cancellationToken)
            {
                try
                {
                    UpdateProgress(25, 100);
                    await System.Threading.Tasks.Task.Delay(500, cancellationToken).ConfigureAwait(false);

                    UpdateProgress(50, 100);
                    await System.Threading.Tasks.Task.Delay(500, cancellationToken).ConfigureAwait(false);

                    UpdateProgress(75, 100);
                    await System.Threading.Tasks.Task.Delay(500, cancellationToken).ConfigureAwait(false);

                    UpdateProgress(100, 100);
                    MarkCompleted();
                }
                catch (System.OperationCanceledException)
                {
                    throw;
                }
                catch (System.Exception ex)
                {
                    MarkFailed($"Custom task failed: {ex.Message}", ex);
                    throw;
                }
            }
        }

    }
}
