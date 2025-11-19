using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Flexible retry policy engine for resilient task execution.
    /// Supports exponential backoff, jitter, and custom retry strategies.
    /// 
    /// Features:
    /// - Exponential backoff with jitter
    /// - Configurable retry limits
    /// - Exception filtering
    /// - Retry metrics tracking
    /// - Custom retry strategies
    /// - Async/await support
    /// </summary>
    public class RetryPolicy
    {
        /// <summary>
        /// Retry configuration
        /// </summary>
        public class RetryConfig
        {
            /// <summary>
            /// Maximum number of retry attempts
            /// </summary>
            public int MaxRetries { get; set; } = 3;

            /// <summary>
            /// Initial delay in milliseconds
            /// </summary>
            public int InitialDelayMs { get; set; } = 100;

            /// <summary>
            /// Maximum delay in milliseconds
            /// </summary>
            public int MaxDelayMs { get; set; } = 30000;

            /// <summary>
            /// Backoff multiplier (exponential)
            /// </summary>
            public double BackoffMultiplier { get; set; } = 2.0;

            /// <summary>
            /// Add random jitter to delays (0-1)
            /// </summary>
            public double JitterFactor { get; set; } = 0.1;

            /// <summary>
            /// Exception types to retry on
            /// </summary>
            public List<Type> RetryableExceptions { get; set; } = new List<Type>
            {
                typeof(TimeoutException),
                typeof(IOException),
                typeof(InvalidOperationException)
            };

            /// <summary>
            /// Exception types to never retry
            /// </summary>
            public List<Type> NonRetryableExceptions { get; set; } = new List<Type>
            {
                typeof(ArgumentException),
                typeof(NotImplementedException),
                typeof(NotSupportedException)
            };
        }

        /// <summary>
        /// Retry attempt result
        /// </summary>
        public class RetryResult
        {
            public bool Success { get; set; }
            public int AttemptCount { get; set; }
            public TimeSpan TotalDuration { get; set; }
            public Exception LastException { get; set; }
            public List<RetryAttempt> Attempts { get; set; } = new List<RetryAttempt>();
        }

        /// <summary>
        /// Individual retry attempt
        /// </summary>
        public class RetryAttempt
        {
            public int AttemptNumber { get; set; }
            public DateTime StartTime { get; set; }
            public TimeSpan Duration { get; set; }
            public bool Success { get; set; }
            public Exception Exception { get; set; }
            public int DelayBeforeNextMs { get; set; }
        }

        private readonly RetryConfig _config;
        private readonly Random _random = new Random();

        public RetryPolicy(RetryConfig config = null)
        {
            _config = config ?? new RetryConfig();
        }

        /// <summary>
        /// Execute action with retry logic
        /// </summary>
        public async Task<RetryResult> ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            var result = new RetryResult();
            var stopwatch = Stopwatch.StartNew();

            for (int attempt = 1; attempt <= _config.MaxRetries + 1; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Success = false;
                    result.LastException = new OperationCanceledException("Task was cancelled");
                    break;
                }

                var attemptStart = DateTime.UtcNow;
                var attemptStopwatch = Stopwatch.StartNew();

                try
                {
                    await action(cancellationToken).ConfigureAwait(false);

                    attemptStopwatch.Stop();
                    result.Attempts.Add(new RetryAttempt
                    {
                        AttemptNumber = attempt,
                        StartTime = attemptStart,
                        Duration = attemptStopwatch.Elapsed,
                        Success = true
                    });

                    result.Success = true;
                    result.AttemptCount = attempt;
                    break;
                }
                catch (Exception ex)
                {
                    attemptStopwatch.Stop();
                    result.LastException = ex;

                    // Check if we should retry
                    if (!ShouldRetry(ex, attempt))
                    {
                        result.Attempts.Add(new RetryAttempt
                        {
                            AttemptNumber = attempt,
                            StartTime = attemptStart,
                            Duration = attemptStopwatch.Elapsed,
                            Success = false,
                            Exception = ex
                        });

                        result.Success = false;
                        result.AttemptCount = attempt;
                        break;
                    }

                    // Calculate delay
                    int delayMs = CalculateDelay(attempt);

                    result.Attempts.Add(new RetryAttempt
                    {
                        AttemptNumber = attempt,
                        StartTime = attemptStart,
                        Duration = attemptStopwatch.Elapsed,
                        Success = false,
                        Exception = ex,
                        DelayBeforeNextMs = delayMs
                    });

                    // Wait before retry
                    if (attempt <= _config.MaxRetries)
                    {
                        try
                        {
                            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            result.Success = false;
                            result.LastException = new OperationCanceledException("Task was cancelled during retry delay");
                            result.AttemptCount = attempt;
                            break;
                        }
                    }
                }
            }

            stopwatch.Stop();
            result.TotalDuration = stopwatch.Elapsed;
            return result;
        }

        /// <summary>
        /// Execute action with retry logic (non-async)
        /// </summary>
        public RetryResult Execute(Action action)
        {
            var result = new RetryResult();
            var stopwatch = Stopwatch.StartNew();

            for (int attempt = 1; attempt <= _config.MaxRetries + 1; attempt++)
            {
                var attemptStart = DateTime.UtcNow;
                var attemptStopwatch = Stopwatch.StartNew();

                try
                {
                    action();

                    attemptStopwatch.Stop();
                    result.Attempts.Add(new RetryAttempt
                    {
                        AttemptNumber = attempt,
                        StartTime = attemptStart,
                        Duration = attemptStopwatch.Elapsed,
                        Success = true
                    });

                    result.Success = true;
                    result.AttemptCount = attempt;
                    break;
                }
                catch (Exception ex)
                {
                    attemptStopwatch.Stop();
                    result.LastException = ex;

                    if (!ShouldRetry(ex, attempt))
                    {
                        result.Attempts.Add(new RetryAttempt
                        {
                            AttemptNumber = attempt,
                            StartTime = attemptStart,
                            Duration = attemptStopwatch.Elapsed,
                            Success = false,
                            Exception = ex
                        });

                        result.Success = false;
                        result.AttemptCount = attempt;
                        break;
                    }

                    int delayMs = CalculateDelay(attempt);

                    result.Attempts.Add(new RetryAttempt
                    {
                        AttemptNumber = attempt,
                        StartTime = attemptStart,
                        Duration = attemptStopwatch.Elapsed,
                        Success = false,
                        Exception = ex,
                        DelayBeforeNextMs = delayMs
                    });

                    if (attempt <= _config.MaxRetries)
                    {
                        Thread.Sleep(delayMs);
                    }
                }
            }

            stopwatch.Stop();
            result.TotalDuration = stopwatch.Elapsed;
            return result;
        }

        /// <summary>
        /// Determine if exception should trigger retry
        /// </summary>
        private bool ShouldRetry(Exception ex, int attemptNumber)
        {
            // Don't retry if max attempts exceeded
            if (attemptNumber > _config.MaxRetries)
                return false;

            var exceptionType = ex.GetType();

            // Check non-retryable exceptions first
            foreach (var nonRetryable in _config.NonRetryableExceptions)
            {
                if (nonRetryable.IsAssignableFrom(exceptionType))
                    return false;
            }

            // Check retryable exceptions
            foreach (var retryable in _config.RetryableExceptions)
            {
                if (retryable.IsAssignableFrom(exceptionType))
                    return true;
            }

            // Default: don't retry unknown exceptions
            return false;
        }

        /// <summary>
        /// Calculate delay with exponential backoff and jitter
        /// </summary>
        private int CalculateDelay(int attemptNumber)
        {
            // Exponential backoff: initialDelay * (multiplier ^ (attempt - 1))
            double exponentialDelay = _config.InitialDelayMs * Math.Pow(_config.BackoffMultiplier, attemptNumber - 1);
            int delayMs = (int)Math.Min(exponentialDelay, _config.MaxDelayMs);

            // Add jitter
            if (_config.JitterFactor > 0)
            {
                double jitter = delayMs * _config.JitterFactor * (_random.NextDouble() * 2 - 1);
                delayMs = (int)Math.Max(0, delayMs + jitter);
            }

            return delayMs;
        }

        /// <summary>
        /// Get retry statistics
        /// </summary>
        public static string GetRetryStats(RetryResult result)
        {
            var stats = new System.Text.StringBuilder();
            stats.AppendLine($"Retry Result: {(result.Success ? "SUCCESS" : "FAILED")}");
            stats.AppendLine($"Total Attempts: {result.AttemptCount}");
            stats.AppendLine($"Total Duration: {result.TotalDuration.TotalSeconds:F2}s");

            if (!result.Success && result.LastException != null)
            {
                stats.AppendLine($"Last Exception: {result.LastException.GetType().Name}: {result.LastException.Message}");
            }

            stats.AppendLine("\nAttempt Details:");
            foreach (var attempt in result.Attempts)
            {
                stats.AppendLine($"  Attempt {attempt.AttemptNumber}: {(attempt.Success ? "✓" : "✗")} " +
                    $"({attempt.Duration.TotalMilliseconds:F0}ms)" +
                    (attempt.DelayBeforeNextMs > 0 ? $" → Wait {attempt.DelayBeforeNextMs}ms" : ""));
            }

            return stats.ToString();
        }
    }
}
