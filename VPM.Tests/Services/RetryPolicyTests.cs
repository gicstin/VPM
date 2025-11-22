using Xunit;
using VPM.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Tests.Services
{
    public class RetryPolicyTests
    {
        [Fact]
        public void Execute_SuccessfulAction_ReturnsSingleAttempt()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 3 };
            var policy = new RetryPolicy(config);

            int attemptCount = 0;
            var result = policy.Execute(() =>
            {
                attemptCount++;
            });

            Assert.True(result.Success);
            Assert.Equal(1, result.AttemptCount);
            Assert.Equal(1, attemptCount);
            Assert.Empty(result.Attempts.FindAll(a => !a.Success));
        }

        [Fact]
        public void Execute_RetryableException_RetriesAndSucceeds()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 3, InitialDelayMs = 10 };
            var policy = new RetryPolicy(config);

            int attemptCount = 0;
            var result = policy.Execute(() =>
            {
                attemptCount++;
                if (attemptCount < 3)
                {
                    throw new TimeoutException("Timeout");
                }
            });

            Assert.True(result.Success);
            Assert.Equal(3, result.AttemptCount);
            Assert.Equal(3, attemptCount);
        }

        [Fact]
        public void Execute_ExceedsMaxRetries_ReturnsFailed()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 2, InitialDelayMs = 10 };
            var policy = new RetryPolicy(config);

            int attemptCount = 0;
            var result = policy.Execute(() =>
            {
                attemptCount++;
                throw new TimeoutException("Timeout");
            });

            Assert.False(result.Success);
            Assert.Equal(3, result.AttemptCount);
            Assert.Equal(3, attemptCount);
            Assert.NotNull(result.LastException);
            Assert.IsType<TimeoutException>(result.LastException);
        }

        [Fact]
        public void Execute_NonRetryableException_StopsImmediately()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 3 };
            var policy = new RetryPolicy(config);

            int attemptCount = 0;
            var result = policy.Execute(() =>
            {
                attemptCount++;
                throw new ArgumentException("Invalid argument");
            });

            Assert.False(result.Success);
            Assert.Equal(1, result.AttemptCount);
            Assert.Equal(1, attemptCount);
        }

        [Fact]
        public void Execute_TracksDuration()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 1, InitialDelayMs = 50 };
            var policy = new RetryPolicy(config);

            int attemptCount = 0;
            var result = policy.Execute(() =>
            {
                attemptCount++;
                if (attemptCount == 1)
                {
                    throw new TimeoutException();
                }
            });

            Assert.True(result.Success);
            Assert.True(result.TotalDuration.TotalMilliseconds >= 50);
        }

        [Fact]
        public void Execute_ExponentialBackoff_IncreasesDuration()
        {
            var config = new RetryPolicy.RetryConfig
            {
                MaxRetries = 3,
                InitialDelayMs = 10,
                BackoffMultiplier = 2.0,
                JitterFactor = 0
            };
            var policy = new RetryPolicy(config);

            var result = policy.Execute(() =>
            {
                throw new TimeoutException();
            });

            Assert.False(result.Success);
            Assert.True(result.Attempts.Count >= 2);

            int previousDelay = 0;
            foreach (var attempt in result.Attempts)
            {
                if (attempt.DelayBeforeNextMs > 0)
                {
                    Assert.True(attempt.DelayBeforeNextMs >= previousDelay);
                    previousDelay = attempt.DelayBeforeNextMs;
                }
            }
        }

        [Fact]
        public void Execute_RecordsAllAttempts()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 2, InitialDelayMs = 5 };
            var policy = new RetryPolicy(config);

            int attemptCount = 0;
            var result = policy.Execute(() =>
            {
                attemptCount++;
                throw new TimeoutException();
            });

            Assert.Equal(3, result.Attempts.Count);
            for (int i = 0; i < result.Attempts.Count; i++)
            {
                Assert.Equal(i + 1, result.Attempts[i].AttemptNumber);
            }
        }

        [Fact]
        public async Task ExecuteAsync_SuccessfulAction_ReturnsSingleAttempt()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 3 };
            var policy = new RetryPolicy(config);

            int attemptCount = 0;
            var result = await policy.ExecuteAsync(async ct =>
            {
                attemptCount++;
                await Task.CompletedTask;
            });

            Assert.True(result.Success);
            Assert.Equal(1, result.AttemptCount);
            Assert.Equal(1, attemptCount);
        }

        [Fact]
        public async Task ExecuteAsync_RetryableException_RetriesAndSucceeds()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 3, InitialDelayMs = 10 };
            var policy = new RetryPolicy(config);

            int attemptCount = 0;
            var result = await policy.ExecuteAsync(async ct =>
            {
                attemptCount++;
                if (attemptCount < 3)
                {
                    throw new TimeoutException("Timeout");
                }
                await Task.CompletedTask;
            });

            Assert.True(result.Success);
            Assert.Equal(3, result.AttemptCount);
        }

        [Fact]
        public async Task ExecuteAsync_ExceedsMaxRetries_ReturnsFailed()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 2, InitialDelayMs = 10 };
            var policy = new RetryPolicy(config);

            var result = await policy.ExecuteAsync(async ct =>
            {
                await Task.Delay(1, ct);
                throw new TimeoutException("Timeout");
            });

            Assert.False(result.Success);
            Assert.Equal(3, result.AttemptCount);
        }

        [Fact]
        public async Task ExecuteAsync_CancellationToken_CancelsExecution()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 5, InitialDelayMs = 100 };
            var policy = new RetryPolicy(config);

            var cts = new CancellationTokenSource(500);

            var result = await policy.ExecuteAsync(async ct =>
            {
                throw new TimeoutException();
            }, cts.Token);

            Assert.False(result.Success);
            Assert.True(result.AttemptCount < 5);
        }

        [Fact]
        public void RetryConfig_DefaultValues_AreSet()
        {
            var config = new RetryPolicy.RetryConfig();

            Assert.Equal(3, config.MaxRetries);
            Assert.Equal(100, config.InitialDelayMs);
            Assert.Equal(30000, config.MaxDelayMs);
            Assert.Equal(2.0, config.BackoffMultiplier);
        }

        [Fact]
        public void RetryResult_TracksDuration()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 1, InitialDelayMs = 30 };
            var policy = new RetryPolicy(config);

            var result = policy.Execute(() =>
            {
                throw new TimeoutException();
            });

            Assert.True(result.TotalDuration.TotalMilliseconds >= 30);
        }

        [Fact]
        public void RetryAttempt_RecordsStartTime()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 1, InitialDelayMs = 10 };
            var policy = new RetryPolicy(config);

            var beforeExecution = DateTime.UtcNow;
            var result = policy.Execute(() => throw new TimeoutException());
            var afterExecution = DateTime.UtcNow;

            foreach (var attempt in result.Attempts)
            {
                Assert.True(attempt.StartTime >= beforeExecution);
                Assert.True(attempt.StartTime <= afterExecution);
            }
        }

        [Fact]
        public void Execute_CustomRetryableExceptions_RetriesOnCustomException()
        {
            var customException = typeof(CustomRetryableException);
            var config = new RetryPolicy.RetryConfig
            {
                MaxRetries = 2,
                InitialDelayMs = 10,
                RetryableExceptions = new System.Collections.Generic.List<Type> { customException }
            };
            var policy = new RetryPolicy(config);

            int attemptCount = 0;
            var result = policy.Execute(() =>
            {
                attemptCount++;
                if (attemptCount < 2)
                {
                    throw new CustomRetryableException();
                }
            });

            Assert.True(result.Success);
            Assert.Equal(2, attemptCount);
        }

        [Fact]
        public void Execute_CustomNonRetryableExceptions_DoesNotRetry()
        {
            var config = new RetryPolicy.RetryConfig
            {
                MaxRetries = 3,
                NonRetryableExceptions = new System.Collections.Generic.List<Type> { typeof(CustomNonRetryableException) }
            };
            var policy = new RetryPolicy(config);

            int attemptCount = 0;
            var result = policy.Execute(() =>
            {
                attemptCount++;
                throw new CustomNonRetryableException();
            });

            Assert.False(result.Success);
            Assert.Equal(1, attemptCount);
        }

        [Fact]
        public void GetRetryStats_FormatsStatsCorrectly()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 2, InitialDelayMs = 10 };
            var policy = new RetryPolicy(config);

            var result = policy.Execute(() => throw new TimeoutException());
            var stats = RetryPolicy.GetRetryStats(result);

            Assert.Contains("FAILED", stats);
            Assert.Contains("Total Attempts:", stats);
            Assert.Contains("Attempt Details:", stats);
        }

        [Fact]
        public void Execute_JitterAffectsDelay()
        {
            var config = new RetryPolicy.RetryConfig
            {
                MaxRetries = 2,
                InitialDelayMs = 100,
                JitterFactor = 0.5,
                BackoffMultiplier = 1.0
            };
            var policy = new RetryPolicy(config);

            var result = policy.Execute(() => throw new TimeoutException());

            Assert.True(result.Attempts.Count >= 2);
            var firstRetryDelay = result.Attempts[0].DelayBeforeNextMs;
            Assert.True(firstRetryDelay > 0);
            Assert.True(firstRetryDelay <= 150);
        }

        [Fact]
        public void Execute_SuccessfulFirstAttempt_NoDelayNeeded()
        {
            var config = new RetryPolicy.RetryConfig { MaxRetries = 3, InitialDelayMs = 100 };
            var policy = new RetryPolicy(config);

            var result = policy.Execute(() => { });

            Assert.True(result.Success);
            Assert.Single(result.Attempts);
            Assert.Equal(0, result.Attempts[0].DelayBeforeNextMs);
        }

        private class CustomRetryableException : Exception { }
        private class CustomNonRetryableException : Exception { }
    }
}
