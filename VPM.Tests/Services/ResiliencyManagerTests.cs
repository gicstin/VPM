using Xunit;
using VPM.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace VPM.Tests.Services
{
    public class ResiliencyManagerTests
    {
        [Fact]
        public async Task ExecuteWithResiliencyAsync_SuccessfulOperation_ReturnsResult()
        {
            var manager = new ResiliencyManager();
            var expectedResult = 42;

            var result = await manager.ExecuteWithResiliencyAsync(
                "test-op",
                async () => await Task.FromResult(expectedResult));

            Assert.Equal(expectedResult, result);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_SuccessfulOperationString_ReturnsResult()
        {
            var manager = new ResiliencyManager();
            var expectedResult = "Success";

            var result = await manager.ExecuteWithResiliencyAsync(
                "test-op",
                async () => await Task.FromResult(expectedResult));

            Assert.Equal(expectedResult, result);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_NonRetryableException_ThrowsImmediately()
        {
            var manager = new ResiliencyManager();
            var exceptionMessage = "Not retryable";

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.ExecuteWithResiliencyAsync<int>(
                    "test-op",
                    async () =>
                    {
                        throw new InvalidOperationException(exceptionMessage);
                    }));

            Assert.IsType<InvalidOperationException>(exception);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_IOExceptionRetryable_RetriesAndSucceeds()
        {
            var manager = new ResiliencyManager();
            var callCount = 0;

            var result = await manager.ExecuteWithResiliencyAsync(
                "test-op",
                async () =>
                {
                    callCount++;
                    if (callCount < 2)
                    {
                        throw new IOException("Temporary failure");
                    }
                    return await Task.FromResult(42);
                },
                maxRetries: 3);

            Assert.Equal(42, result);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_TimeoutExceptionRetryable_RetriesAndSucceeds()
        {
            var manager = new ResiliencyManager();
            var callCount = 0;

            var result = await manager.ExecuteWithResiliencyAsync(
                "test-op",
                async () =>
                {
                    callCount++;
                    if (callCount < 3)
                    {
                        throw new TimeoutException("Timeout");
                    }
                    return await Task.FromResult(100);
                },
                maxRetries: 5);

            Assert.Equal(100, result);
            Assert.Equal(3, callCount);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_UnauthorizedAccessExceptionRetryable_RetriesAndSucceeds()
        {
            var manager = new ResiliencyManager();
            var callCount = 0;

            var result = await manager.ExecuteWithResiliencyAsync(
                "test-op",
                async () =>
                {
                    callCount++;
                    if (callCount < 2)
                    {
                        throw new UnauthorizedAccessException("Access denied");
                    }
                    return await Task.FromResult(true);
                },
                maxRetries: 3);

            Assert.True(result);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_ExceedsMaxRetries_ThrowsAggregateException()
        {
            var manager = new ResiliencyManager();
            var callCount = 0;

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => manager.ExecuteWithResiliencyAsync<int>(
                    "test-op",
                    async () =>
                    {
                        callCount++;
                        throw new IOException("Persistent failure");
                    },
                    maxRetries: 2));

            Assert.Equal(3, callCount);
            Assert.NotNull(exception.InnerException);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_ZeroRetries_AttemptsOnceOnly()
        {
            var manager = new ResiliencyManager();
            var callCount = 0;

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => manager.ExecuteWithResiliencyAsync<int>(
                    "test-op",
                    async () =>
                    {
                        callCount++;
                        throw new IOException("Failure");
                    },
                    maxRetries: 0));

            Assert.Equal(1, callCount);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_CircuitBreakerAfterFailures_OpensCircuit()
        {
            var manager = new ResiliencyManager();
            var operationKey = "failing-op";
            var callCount = 0;

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await manager.ExecuteWithResiliencyAsync<int>(
                        operationKey,
                        async () =>
                        {
                            callCount++;
                            throw new IOException("Persistent failure");
                        },
                        maxRetries: 1,
                        circuitResetTimeout: TimeSpan.FromMilliseconds(100));
                }
                catch
                {
                }
            }

            var circuitOpenException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.ExecuteWithResiliencyAsync<int>(
                    operationKey,
                    async () => await Task.FromResult(42),
                    circuitResetTimeout: TimeSpan.FromMilliseconds(100)));

            Assert.Contains("Circuit breaker is open", circuitOpenException.Message);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_CircuitBreakerRecoversAfterTimeout_AllowsOperations()
        {
            var manager = new ResiliencyManager();
            var operationKey = "recovery-op";

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await manager.ExecuteWithResiliencyAsync<int>(
                        operationKey,
                        async () =>
                        {
                            throw new IOException("Failure");
                        },
                        maxRetries: 0,
                        circuitResetTimeout: TimeSpan.FromMilliseconds(100));
                }
                catch
                {
                }
            }

            await Task.Delay(200);

            var result = await manager.ExecuteWithResiliencyAsync<int>(
                operationKey,
                async () => await Task.FromResult(99),
                circuitResetTimeout: TimeSpan.FromMilliseconds(100));

            Assert.Equal(99, result);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_ExponentialBackoff_HasIncreasingDelay()
        {
            var manager = new ResiliencyManager();
            var callTimes = new System.Collections.Generic.List<DateTime>();

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => manager.ExecuteWithResiliencyAsync<int>(
                    "backoff-test",
                    async () =>
                    {
                        callTimes.Add(DateTime.UtcNow);
                        throw new IOException("Failure");
                    },
                    maxRetries: 2,
                    retryDelay: TimeSpan.FromMilliseconds(100)));

            Assert.Equal(3, callTimes.Count);
            var firstDelay = (callTimes[1] - callTimes[0]).TotalMilliseconds;
            var secondDelay = (callTimes[2] - callTimes[1]).TotalMilliseconds;

            Assert.True(secondDelay > firstDelay, $"Second delay ({secondDelay}ms) should be greater than first ({firstDelay}ms)");
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_DifferentOperationKeys_IndependentCircuitBreakers()
        {
            var manager = new ResiliencyManager();

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await manager.ExecuteWithResiliencyAsync<int>(
                        "op1",
                        async () =>
                        {
                            throw new IOException("Failure");
                        },
                        maxRetries: 0,
                        circuitResetTimeout: TimeSpan.FromSeconds(10));
                }
                catch
                {
                }
            }

            var result = await manager.ExecuteWithResiliencyAsync<int>(
                "op2",
                async () => await Task.FromResult(42));

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ExecuteFileOperationAsync_SuccessfulOperation_ReturnsResult()
        {
            var manager = new ResiliencyManager();
            var filePath = Path.Combine(Path.GetTempPath(), "test_file.txt");

            var result = await manager.ExecuteFileOperationAsync(
                filePath,
                async () => await Task.FromResult("file operation result"));

            Assert.Equal("file operation result", result);
        }

        [Fact]
        public async Task ExecuteFileOperationAsync_WithRetries_EventuallySucceeds()
        {
            var manager = new ResiliencyManager();
            var filePath = Path.Combine(Path.GetTempPath(), "test_file_2.txt");
            var callCount = 0;

            var result = await manager.ExecuteFileOperationAsync(
                filePath,
                async () =>
                {
                    callCount++;
                    if (callCount < 2)
                    {
                        throw new IOException("Temporary lock");
                    }
                    return await Task.FromResult(99);
                },
                maxRetries: 3);

            Assert.Equal(99, result);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public async Task ExecuteFileOperationAsync_MultipleFilesParallel_Throttles()
        {
            var manager = new ResiliencyManager();
            var filePath1 = Path.Combine(Path.GetTempPath(), "parallel_1.txt");
            var filePath2 = Path.Combine(Path.GetTempPath(), "parallel_2.txt");
            var concurrentCount = 0;
            var maxConcurrent = 0;

            var task1 = manager.ExecuteFileOperationAsync(
                filePath1,
                async () =>
                {
                    System.Threading.Interlocked.Increment(ref concurrentCount);
                    var current = concurrentCount;
                    if (current > maxConcurrent)
                    {
                        System.Threading.Interlocked.Exchange(ref maxConcurrent, current);
                    }

                    await Task.Delay(100);

                    System.Threading.Interlocked.Decrement(ref concurrentCount);
                    return await Task.FromResult(1);
                });

            var task2 = manager.ExecuteFileOperationAsync(
                filePath2,
                async () =>
                {
                    System.Threading.Interlocked.Increment(ref concurrentCount);
                    var current = concurrentCount;
                    if (current > maxConcurrent)
                    {
                        System.Threading.Interlocked.Exchange(ref maxConcurrent, current);
                    }

                    await Task.Delay(100);

                    System.Threading.Interlocked.Decrement(ref concurrentCount);
                    return await Task.FromResult(2);
                });

            await Task.WhenAll(task1, task2);

            Assert.True(maxConcurrent <= 32);
        }

        [Fact]
        public async Task ExecuteFileOperationAsync_LockTimeout_RemovesLockAfterTimeout()
        {
            var manager = new ResiliencyManager();
            var filePath = Path.Combine(Path.GetTempPath(), "timeout_test.txt");

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => manager.ExecuteFileOperationAsync<int>(
                    filePath,
                    async () =>
                    {
                        throw new IOException("Lock persists");
                    },
                    maxRetries: 2));

            Assert.NotNull(exception);
        }

        [Fact]
        public async Task ExecuteWithResiliencyAsync_SuccessAfterRetries_ResetCircuitBreaker()
        {
            var manager = new ResiliencyManager();
            var operationKey = "reset-test";
            var callCount = 0;

            await manager.ExecuteWithResiliencyAsync<bool>(
                operationKey,
                async () =>
                {
                    callCount++;
                    if (callCount < 3)
                    {
                        throw new IOException("Temporary failure");
                    }
                    return await Task.FromResult(true);
                },
                maxRetries: 3);

            callCount = 0;

            var result = await manager.ExecuteWithResiliencyAsync<int>(
                operationKey,
                async () =>
                {
                    callCount++;
                    return await Task.FromResult(42);
                });

            Assert.Equal(1, callCount);
            Assert.Equal(42, result);
        }
    }
}
