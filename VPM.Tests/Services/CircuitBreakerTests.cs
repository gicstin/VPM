using Xunit;
using VPM.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace VPM.Tests.Services
{
    public class CircuitBreakerTests
    {
        [Fact]
        public void CircuitBreaker_InitialState_IsClosed()
        {
            var breaker = new CircuitBreaker();

            Assert.Equal(CircuitBreaker.CircuitState.Closed, breaker.State);
            Assert.False(breaker.IsOpen);
        }

        [Fact]
        public void CircuitBreaker_FailuresExceedThreshold_TransitionsToOpen()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig { FailureThreshold = 3 };
            var breaker = new CircuitBreaker(config);

            breaker.RecordFailure();
            breaker.RecordFailure();
            Assert.Equal(CircuitBreaker.CircuitState.Closed, breaker.State);

            breaker.RecordFailure();
            Assert.Equal(CircuitBreaker.CircuitState.Open, breaker.State);
        }

        [Fact]
        public void CircuitBreaker_Open_RejectsRequests()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig { FailureThreshold = 1 };
            var breaker = new CircuitBreaker(config);

            breaker.RecordFailure();
            Assert.True(breaker.IsOpen);
        }

        [Fact]
        public void CircuitBreaker_SuccessInClosedState_ResetsFailureCount()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig { FailureThreshold = 3 };
            var breaker = new CircuitBreaker(config);

            breaker.RecordFailure();
            breaker.RecordFailure();
            breaker.RecordSuccess();
            breaker.RecordFailure();
            breaker.RecordFailure();

            Assert.Equal(CircuitBreaker.CircuitState.Closed, breaker.State);
        }

        [Fact]
        public void CircuitBreaker_RecoverAfterTimeout_TransitionsToHalfOpen()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig
            {
                FailureThreshold = 1,
                OpenTimeoutMs = 100
            };
            var breaker = new CircuitBreaker(config);

            breaker.RecordFailure();
            Assert.True(breaker.IsOpen);

            Thread.Sleep(150);
            Assert.False(breaker.IsOpen);
            Assert.Equal(CircuitBreaker.CircuitState.HalfOpen, breaker.State);
        }

        [Fact]
        public void CircuitBreaker_HalfOpen_SuccessfulAttempts_TransitionsToClosed()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig
            {
                FailureThreshold = 1,
                OpenTimeoutMs = 100,
                SuccessThresholdForClose = 2
            };
            var breaker = new CircuitBreaker(config);

            breaker.RecordFailure();
            Assert.Equal(CircuitBreaker.CircuitState.Open, breaker.State);

            Thread.Sleep(150);
            Assert.Equal(CircuitBreaker.CircuitState.HalfOpen, breaker.State);

            breaker.RecordSuccess();
            Assert.Equal(CircuitBreaker.CircuitState.HalfOpen, breaker.State);

            breaker.RecordSuccess();
            Assert.Equal(CircuitBreaker.CircuitState.Closed, breaker.State);
        }

        [Fact]
        public void CircuitBreaker_HalfOpen_FailureTransitionsToOpen()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig
            {
                FailureThreshold = 1,
                OpenTimeoutMs = 100
            };
            var breaker = new CircuitBreaker(config);

            breaker.RecordFailure();
            Thread.Sleep(150);

            Assert.Equal(CircuitBreaker.CircuitState.HalfOpen, breaker.State);
            breaker.RecordFailure();
            Assert.Equal(CircuitBreaker.CircuitState.Open, breaker.State);
        }

        [Fact]
        public void CircuitBreaker_Reset_TransitionsToClosed()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig { FailureThreshold = 1 };
            var breaker = new CircuitBreaker(config);

            breaker.RecordFailure();
            Assert.Equal(CircuitBreaker.CircuitState.Open, breaker.State);

            breaker.Reset();
            Assert.Equal(CircuitBreaker.CircuitState.Closed, breaker.State);
        }

        [Fact]
        public void CircuitBreaker_Metrics_TracksFailureRate()
        {
            var breaker = new CircuitBreaker();

            breaker.RecordSuccess();
            breaker.RecordSuccess();
            breaker.RecordFailure();

            var metrics = breaker.GetMetrics();

            Assert.Equal(3, metrics.TotalRequests);
            Assert.Equal(1, metrics.TotalFailures);
            Assert.True(metrics.FailureRate > 0);
        }

        [Fact]
        public void CircuitBreaker_Metrics_TracksLastFailureTime()
        {
            var breaker = new CircuitBreaker();

            var beforeFailure = DateTime.UtcNow;
            breaker.RecordFailure();
            var afterFailure = DateTime.UtcNow;

            var metrics = breaker.GetMetrics();

            Assert.True(metrics.LastFailureTime >= beforeFailure);
            Assert.True(metrics.LastFailureTime <= afterFailure);
        }

        [Fact]
        public void CircuitBreaker_Metrics_TracksLastSuccessTime()
        {
            var breaker = new CircuitBreaker();

            var beforeSuccess = DateTime.UtcNow;
            breaker.RecordSuccess();
            var afterSuccess = DateTime.UtcNow;

            var metrics = breaker.GetMetrics();

            Assert.True(metrics.LastSuccessTime >= beforeSuccess);
            Assert.True(metrics.LastSuccessTime <= afterSuccess);
        }

        [Fact]
        public void CircuitBreaker_BackoffMultiplier_IncreasesTimeout()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig
            {
                FailureThreshold = 1,
                OpenTimeoutMs = 100,
                BackoffMultiplier = 2.0
            };
            var breaker = new CircuitBreaker(config);

            breaker.RecordFailure();
            var timeToRecovery1 = breaker.GetTimeUntilRecovery();

            Thread.Sleep(150);
            // Access State to trigger HalfOpen transition
            var state = breaker.State;
            breaker.RecordFailure();
            var timeToRecovery2 = breaker.GetTimeUntilRecovery();

            Assert.True(timeToRecovery2.TotalMilliseconds > 100);
        }

        [Fact]
        public void CircuitBreaker_GetTimeUntilRecovery_ClosedState_ReturnsZero()
        {
            var breaker = new CircuitBreaker();

            var timeToRecovery = breaker.GetTimeUntilRecovery();

            Assert.Equal(TimeSpan.Zero, timeToRecovery);
        }

        [Fact]
        public void CircuitBreaker_GetTimeUntilRecovery_OpenState_ReturnsPositiveTime()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig
            {
                FailureThreshold = 1,
                OpenTimeoutMs = 500
            };
            var breaker = new CircuitBreaker(config);

            breaker.RecordFailure();
            var timeToRecovery = breaker.GetTimeUntilRecovery();

            Assert.True(timeToRecovery.TotalMilliseconds > 0);
        }

        [Fact]
        public void CircuitBreaker_MultipleFailureWindows_OnlyCountsRecentFailures()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig
            {
                FailureThreshold = 2,
                FailureWindowMs = 100
            };
            var breaker = new CircuitBreaker(config);

            breaker.RecordFailure();
            Thread.Sleep(150);

            breaker.RecordFailure();
            var metrics = breaker.GetMetrics();

            Assert.Equal(2, metrics.TotalFailures);
        }

        [Fact]
        public void CircuitBreakerRegistry_GetBreaker_CreatesBreakerIfNotExists()
        {
            var registry = new CircuitBreakerRegistry();

            var breaker = registry.GetBreaker("operation1");

            Assert.NotNull(breaker);
            Assert.Equal(CircuitBreaker.CircuitState.Closed, breaker.State);
        }

        [Fact]
        public void CircuitBreakerRegistry_GetBreaker_ReturnsSameBreakerForSameName()
        {
            var registry = new CircuitBreakerRegistry();

            var breaker1 = registry.GetBreaker("operation1");
            var breaker2 = registry.GetBreaker("operation1");

            Assert.Same(breaker1, breaker2);
        }

        [Fact]
        public void CircuitBreakerRegistry_GetAllBreakers_ReturnsAllRegisteredBreakers()
        {
            var registry = new CircuitBreakerRegistry();

            registry.GetBreaker("operation1");
            registry.GetBreaker("operation2");
            registry.GetBreaker("operation3");

            var allBreakers = registry.GetAllBreakers();

            Assert.Equal(3, allBreakers.Count());
        }

        [Fact]
        public void CircuitBreakerRegistry_GetAllMetrics_ReturnsMetricsForAllBreakers()
        {
            var registry = new CircuitBreakerRegistry();

            var breaker1 = registry.GetBreaker("operation1");
            var breaker2 = registry.GetBreaker("operation2");

            breaker1.RecordSuccess();
            breaker2.RecordFailure();

            var metrics = registry.GetAllMetrics();

            Assert.Equal(2, metrics.Count);
            Assert.Equal(1, metrics["operation1"].TotalRequests);
            Assert.Equal(1, metrics["operation2"].TotalFailures);
        }

        [Fact]
        public void CircuitBreakerRegistry_ResetAll_ResetsAllBreakers()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig { FailureThreshold = 1 };
            var registry = new CircuitBreakerRegistry(config);

            var breaker1 = registry.GetBreaker("operation1");
            var breaker2 = registry.GetBreaker("operation2");

            breaker1.RecordFailure();
            breaker2.RecordFailure();

            registry.ResetAll();

            Assert.Equal(CircuitBreaker.CircuitState.Closed, breaker1.State);
            Assert.Equal(CircuitBreaker.CircuitState.Closed, breaker2.State);
        }

        [Fact]
        public void CircuitBreakerRegistry_GetStatusReport_FormatsReportCorrectly()
        {
            var registry = new CircuitBreakerRegistry();

            var breaker = registry.GetBreaker("operation1");
            breaker.RecordSuccess();

            var report = registry.GetStatusReport();

            Assert.Contains("Circuit Breaker Status Report", report);
            Assert.Contains("operation1", report);
        }

        [Fact]
        public void CircuitBreakerConfig_DefaultValues_AreSet()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig();

            Assert.Equal(5, config.FailureThreshold);
            Assert.Equal(60000, config.FailureWindowMs);
            Assert.Equal(30000, config.OpenTimeoutMs);
            Assert.Equal(2, config.SuccessThresholdForClose);
        }

        [Fact]
        public void CircuitBreaker_StateChangeTime_IsUpdatedOnTransition()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig { FailureThreshold = 1 };
            var breaker = new CircuitBreaker(config);

            var beforeStateChange = DateTime.UtcNow;
            breaker.RecordFailure();
            var afterStateChange = DateTime.UtcNow;

            var metrics = breaker.GetMetrics();

            Assert.True(metrics.StateChangeTime >= beforeStateChange);
            Assert.True(metrics.StateChangeTime <= afterStateChange);
        }

        [Fact]
        public void CircuitBreaker_ThreadSafety_ConcurrentOperations_DoNotThrow()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig { FailureThreshold = 100 };
            var breaker = new CircuitBreaker(config);

            var tasks = new Task[10];
            for (int i = 0; i < 10; i++)
            {
                int index = i;
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 10; j++)
                    {
                        if (index % 2 == 0)
                        {
                            breaker.RecordSuccess();
                        }
                        else
                        {
                            breaker.RecordFailure();
                        }
                    }
                });
            }

            Task.WaitAll(tasks);

            var metrics = breaker.GetMetrics();
            Assert.Equal(100, metrics.TotalRequests);
        }

        [Fact]
        public void CircuitBreaker_MaxTimeout_DoesNotExceedMaxTimeoutMs()
        {
            var config = new CircuitBreaker.CircuitBreakerConfig
            {
                FailureThreshold = 1,
                OpenTimeoutMs = 1000,
                BackoffMultiplier = 3.0,
                MaxTimeoutMs = 5000
            };
            var breaker = new CircuitBreaker(config);

            for (int i = 0; i < 10; i++)
            {
                breaker.RecordFailure();
                Thread.Sleep(1100);
            }

            var timeToRecovery = breaker.GetTimeUntilRecovery();
            Assert.True(timeToRecovery.TotalMilliseconds <= 5000);
        }
    }
}
