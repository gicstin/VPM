using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VPM.Services
{
    /// <summary>
    /// Circuit breaker pattern implementation for preventing cascading failures.
    /// Prevents repeated attempts to execute failing operations.
    /// 
    /// Features:
    /// - Three states: Closed, Open, Half-Open
    /// - Configurable failure thresholds
    /// - Automatic recovery with exponential backoff
    /// - Per-operation circuit breakers
    /// - Metrics tracking
    /// </summary>
    public class CircuitBreaker
    {
        /// <summary>
        /// Circuit breaker states
        /// </summary>
        public enum CircuitState
        {
            Closed,      // Normal operation
            Open,        // Failing, reject requests
            HalfOpen     // Testing if service recovered
        }

        /// <summary>
        /// Circuit breaker configuration
        /// </summary>
        public class CircuitBreakerConfig
        {
            /// <summary>
            /// Number of failures before opening circuit
            /// </summary>
            public int FailureThreshold { get; set; } = 5;

            /// <summary>
            /// Time window for counting failures (milliseconds)
            /// </summary>
            public int FailureWindowMs { get; set; } = 60000; // 1 minute

            /// <summary>
            /// Time to wait before attempting recovery (milliseconds)
            /// </summary>
            public int OpenTimeoutMs { get; set; } = 30000; // 30 seconds

            /// <summary>
            /// Number of successful attempts needed to close circuit
            /// </summary>
            public int SuccessThresholdForClose { get; set; } = 2;

            /// <summary>
            /// Exponential backoff multiplier for recovery timeout
            /// </summary>
            public double BackoffMultiplier { get; set; } = 1.5;

            /// <summary>
            /// Maximum timeout between recovery attempts
            /// </summary>
            public int MaxTimeoutMs { get; set; } = 300000; // 5 minutes
        }

        /// <summary>
        /// Circuit breaker metrics
        /// </summary>
        public class CircuitBreakerMetrics
        {
            public CircuitState State { get; set; }
            public int FailureCount { get; set; }
            public int SuccessCount { get; set; }
            public int TotalRequests { get; set; }
            public int TotalFailures { get; set; }
            public DateTime LastFailureTime { get; set; }
            public DateTime LastSuccessTime { get; set; }
            public DateTime StateChangeTime { get; set; }
            public double FailureRate => TotalRequests > 0 ? (TotalFailures * 100.0) / TotalRequests : 0;
        }

        private readonly CircuitBreakerConfig _config;
        private CircuitState _state = CircuitState.Closed;
        private int _failureCount = 0;
        private int _successCount = 0;
        private DateTime _lastFailureTime = DateTime.MinValue;
        private DateTime _lastSuccessTime = DateTime.MinValue;
        private DateTime _stateChangeTime = DateTime.UtcNow;
        private DateTime _openedAt = DateTime.MinValue;
        private int _currentTimeoutMs;
        private int _openCount = 0;
        private long _totalRequests = 0;
        private long _totalFailures = 0;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        public CircuitBreaker(CircuitBreakerConfig config = null)
        {
            _config = config ?? new CircuitBreakerConfig();
            _currentTimeoutMs = _config.OpenTimeoutMs;
            _openCount = 0;
        }

        /// <summary>
        /// Get current circuit state
        /// </summary>
        public CircuitState State
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    // Check if timeout has elapsed and transition to HalfOpen
                    if (_state == CircuitState.Open && DateTime.UtcNow - _openedAt >= TimeSpan.FromMilliseconds(_currentTimeoutMs))
                    {
                        _lock.ExitReadLock();
                        _lock.EnterWriteLock();
                        try
                        {
                            TransitionToHalfOpen();
                        }
                        finally
                        {
                            _lock.ExitWriteLock();
                        }
                        _lock.EnterReadLock();
                    }
                    return _state;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Check if circuit is open (rejecting requests)
        /// </summary>
        public bool IsOpen
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    if (_state == CircuitState.Closed)
                        return false;

                    if (_state == CircuitState.HalfOpen)
                        return false;

                    // Check if timeout has elapsed
                    if (DateTime.UtcNow - _openedAt >= TimeSpan.FromMilliseconds(_currentTimeoutMs))
                    {
                        _lock.ExitReadLock();
                        _lock.EnterWriteLock();
                        try
                        {
                            TransitionToHalfOpen();
                        }
                        finally
                        {
                            _lock.ExitWriteLock();
                        }
                        return false;
                    }

                    return true;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Record successful operation
        /// </summary>
        public void RecordSuccess()
        {
            _lock.EnterWriteLock();
            try
            {
                Interlocked.Increment(ref _totalRequests);
                _lastSuccessTime = DateTime.UtcNow;

                if (_state == CircuitState.Closed)
                {
                    _failureCount = 0;
                    _successCount++;
                }
                else if (_state == CircuitState.HalfOpen)
                {
                    _successCount++;

                    if (_successCount >= _config.SuccessThresholdForClose)
                    {
                        TransitionToClosed();
                    }
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Record failed operation
        /// </summary>
        public void RecordFailure()
        {
            _lock.EnterWriteLock();
            try
            {
                Interlocked.Increment(ref _totalRequests);
                Interlocked.Increment(ref _totalFailures);
                _lastFailureTime = DateTime.UtcNow;

                if (_state == CircuitState.Closed)
                {
                    _failureCount++;

                    if (_failureCount >= _config.FailureThreshold)
                    {
                        TransitionToOpen();
                    }
                }
                else if (_state == CircuitState.HalfOpen)
                {
                    TransitionToOpen();
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Transition to Closed state
        /// </summary>
        private void TransitionToClosed()
        {
            _state = CircuitState.Closed;
            _failureCount = 0;
            _successCount = 0;
            _currentTimeoutMs = _config.OpenTimeoutMs;
            _stateChangeTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Transition to Open state
        /// </summary>
        private void TransitionToOpen()
        {
            _state = CircuitState.Open;
            _openedAt = DateTime.UtcNow;
            _successCount = 0;
            _stateChangeTime = DateTime.UtcNow;
            _openCount++;

            // Increase timeout with exponential backoff (only after first open)
            if (_openCount > 1)
            {
                _currentTimeoutMs = (int)Math.Min(
                    _config.MaxTimeoutMs,
                    _currentTimeoutMs * _config.BackoffMultiplier);
            }
        }

        /// <summary>
        /// Transition to Half-Open state
        /// </summary>
        private void TransitionToHalfOpen()
        {
            _state = CircuitState.HalfOpen;
            _failureCount = 0;
            _successCount = 0;
            _stateChangeTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Reset circuit breaker
        /// </summary>
        public void Reset()
        {
            _lock.EnterWriteLock();
            try
            {
                TransitionToClosed();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Get circuit breaker metrics
        /// </summary>
        public CircuitBreakerMetrics GetMetrics()
        {
            _lock.EnterReadLock();
            try
            {
                return new CircuitBreakerMetrics
                {
                    State = _state,
                    FailureCount = _failureCount,
                    SuccessCount = _successCount,
                    TotalRequests = (int)_totalRequests,
                    TotalFailures = (int)_totalFailures,
                    LastFailureTime = _lastFailureTime,
                    LastSuccessTime = _lastSuccessTime,
                    StateChangeTime = _stateChangeTime
                };
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Get time until circuit attempts recovery
        /// </summary>
        public TimeSpan GetTimeUntilRecovery()
        {
            _lock.EnterReadLock();
            try
            {
                if (_state != CircuitState.Open)
                    return TimeSpan.Zero;

                var timeElapsed = DateTime.UtcNow - _openedAt;
                var timeUntilRecovery = TimeSpan.FromMilliseconds(_currentTimeoutMs) - timeElapsed;

                return timeUntilRecovery > TimeSpan.Zero ? timeUntilRecovery : TimeSpan.Zero;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Registry for managing multiple circuit breakers
    /// </summary>
    public class CircuitBreakerRegistry
    {
        private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers;
        private readonly CircuitBreaker.CircuitBreakerConfig _defaultConfig;

        public CircuitBreakerRegistry(CircuitBreaker.CircuitBreakerConfig defaultConfig = null)
        {
            _breakers = new ConcurrentDictionary<string, CircuitBreaker>();
            _defaultConfig = defaultConfig ?? new CircuitBreaker.CircuitBreakerConfig();
        }

        /// <summary>
        /// Get or create circuit breaker for operation
        /// </summary>
        public CircuitBreaker GetBreaker(string operationName)
        {
            return _breakers.GetOrAdd(operationName, _ => new CircuitBreaker(_defaultConfig));
        }

        /// <summary>
        /// Get all circuit breakers
        /// </summary>
        public IEnumerable<KeyValuePair<string, CircuitBreaker>> GetAllBreakers()
        {
            return _breakers;
        }

        /// <summary>
        /// Get metrics for all breakers
        /// </summary>
        public Dictionary<string, CircuitBreaker.CircuitBreakerMetrics> GetAllMetrics()
        {
            var metrics = new Dictionary<string, CircuitBreaker.CircuitBreakerMetrics>();

            foreach (var kvp in _breakers)
            {
                metrics[kvp.Key] = kvp.Value.GetMetrics();
            }

            return metrics;
        }

        /// <summary>
        /// Reset all circuit breakers
        /// </summary>
        public void ResetAll()
        {
            foreach (var breaker in _breakers.Values)
            {
                breaker.Reset();
            }
        }

        /// <summary>
        /// Get status report
        /// </summary>
        public string GetStatusReport()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== Circuit Breaker Status Report ===");
            report.AppendLine();

            var metrics = GetAllMetrics();
            if (metrics.Count == 0)
            {
                report.AppendLine("No circuit breakers registered.");
                return report.ToString();
            }

            foreach (var kvp in metrics)
            {
                var m = kvp.Value;
                report.AppendLine($"Operation: {kvp.Key}");
                report.AppendLine($"  State: {m.State}");
                report.AppendLine($"  Failure Rate: {m.FailureRate:F1}%");
                report.AppendLine($"  Total Requests: {m.TotalRequests}");
                report.AppendLine($"  Total Failures: {m.TotalFailures}");
                report.AppendLine($"  Last Failure: {m.LastFailureTime:yyyy-MM-dd HH:mm:ss}");
                report.AppendLine();
            }

            return report.ToString();
        }
    }
}
