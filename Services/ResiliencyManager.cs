using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Provides resilient operation execution with retry policies and circuit breaking
    /// </summary>
    public class ResiliencyManager
    {
        private const int MAX_CONCURRENT_IO = 32;
        private static readonly SemaphoreSlim _ioThrottle = new(MAX_CONCURRENT_IO);
        
        private readonly Dictionary<string, CircuitBreakerState> _circuitBreakers = new();
        private readonly object _circuitLock = new();
        
        // Resource cleanup tracking
        private readonly ConcurrentDictionary<string, DateTime> _resourceLocks = new();
        private readonly TimeSpan _resourceTimeout = TimeSpan.FromMinutes(5);

        private class CircuitBreakerState
        {
            public bool IsOpen { get; set; }
            public DateTime LastFailure { get; set; }
            public int FailureCount { get; set; }
            public TimeSpan ResetTimeout { get; set; }
        }

        /// <summary>
        /// Executes an operation with retry policy and circuit breaker pattern
        /// </summary>
        public async Task<T> ExecuteWithResiliencyAsync<T>(
            string operationKey,
            Func<Task<T>> operation,
            int maxRetries = 3,
            TimeSpan? retryDelay = null,
            TimeSpan? circuitResetTimeout = null)
        {
            var delay = retryDelay ?? TimeSpan.FromMilliseconds(200);
            var resetTimeout = circuitResetTimeout ?? TimeSpan.FromSeconds(30);

            // Check circuit breaker
            if (IsCircuitOpen(operationKey, resetTimeout))
            {
                throw new InvalidOperationException($"Circuit breaker is open for operation: {operationKey}");
            }

            Exception lastException = null;
            for (int i = 0; i <= maxRetries; i++)
            {
                try
                {
                    var result = await operation();
                    ResetCircuitBreaker(operationKey);
                    return result;
                }
                catch (Exception ex) when (i < maxRetries &&
                    (ex is IOException || ex is TimeoutException || ex is UnauthorizedAccessException))
                {
                    lastException = ex;
                    RecordFailure(operationKey, resetTimeout);
                    
                    if (i < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Math.Pow(2, i)));
                    }
                }
            }

            throw new AggregateException("Operation failed after all retries", lastException);
        }

        private bool IsCircuitOpen(string operationKey, TimeSpan resetTimeout)
        {
            lock (_circuitLock)
            {
                if (_circuitBreakers.TryGetValue(operationKey, out var state))
                {
                    if (state.IsOpen)
                    {
                        // Check if enough time has passed to try again
                        if (DateTime.UtcNow - state.LastFailure > state.ResetTimeout)
                        {
                            state.IsOpen = false;
                            return false;
                        }
                        return true;
                    }
                }
                return false;
            }
        }

        private void RecordFailure(string operationKey, TimeSpan resetTimeout)
        {
            lock (_circuitLock)
            {
                if (!_circuitBreakers.TryGetValue(operationKey, out var state))
                {
                    state = new CircuitBreakerState
                    {
                        ResetTimeout = resetTimeout
                    };
                    _circuitBreakers[operationKey] = state;
                }

                state.FailureCount++;
                state.LastFailure = DateTime.UtcNow;

                // Open circuit after 5 consecutive failures
                if (state.FailureCount >= 5)
                {
                    state.IsOpen = true;
                }
            }
        }

        private void ResetCircuitBreaker(string operationKey)
        {
            lock (_circuitLock)
            {
                if (_circuitBreakers.TryGetValue(operationKey, out var state))
                {
                    state.FailureCount = 0;
                    state.IsOpen = false;
                }
            }
        }

        /// <summary>
        /// Executes a file operation with proper resource management and retries
        /// </summary>
        public async Task<T> ExecuteFileOperationAsync<T>(
            string filePath,
            Func<Task<T>> operation,
            int maxRetries = 3)
        {
            var operationKey = $"file:{filePath}";
            await _ioThrottle.WaitAsync();

            try
            {
                // Check if file is locked
                if (_resourceLocks.TryGetValue(filePath, out var lockTime))
                {
                    if (DateTime.UtcNow - lockTime > _resourceTimeout)
                    {
                        _resourceLocks.TryRemove(filePath, out _);
                    }
                    else
                    {
                        throw new IOException($"File {filePath} is locked by another operation");
                    }
                }

                // Add lock
                _resourceLocks.TryAdd(filePath, DateTime.UtcNow);

                return await ExecuteWithResiliencyAsync(
                    operationKey,
                    async () =>
                    {
                        try
                        {
                            // Add jitter to reduce contention (Random.Shared is thread-safe)
                            await Task.Delay(Random.Shared.Next(50));
                            return await operation();
                        }
                        finally
                        {
                            _resourceLocks.TryRemove(filePath, out _);
                        }
                    },
                    maxRetries);
            }
            finally
            {
                _ioThrottle.Release();
            }
        }
    }
}
