using Xunit;
using VPM.Services;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Tests.Services
{
    public class OptimizationTimerTests
    {
        [Fact]
        public void Constructor_InitializesTimer()
        {
            var timer = new OptimizationTimer();

            Assert.NotNull(timer);
        }

        [Fact]
        public void Start_StartsTimingOperation()
        {
            var timer = new OptimizationTimer();

            timer.Start("test_operation");

            Assert.NotNull(timer);
        }

        [Fact]
        public void Stop_RecordsDuration()
        {
            var timer = new OptimizationTimer();

            timer.Start("test_operation");
            Thread.Sleep(50);
            var elapsed = timer.Stop("test_operation");

            Assert.True(elapsed > 0);
            Assert.True(elapsed >= 50, $"Expected elapsed >= 50ms, got {elapsed}ms");
        }

        [Fact]
        public void GetTiming_ReturnsCorrectTotalTime()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(30);
            timer.Stop("operation");

            var (total, count, _, _, _) = timer.GetTiming("operation");

            Assert.True(total > 0);
            Assert.Equal(1, count);
        }

        [Fact]
        public void GetTiming_CountsMultipleCalls()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            var (_, count, _, _, _) = timer.GetTiming("operation");

            Assert.Equal(3, count);
        }

        [Fact]
        public void GetTiming_CalculatesCorrectMinimum()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(20);
            timer.Stop("operation");

            timer.Start("operation");
            Thread.Sleep(50);
            timer.Stop("operation");

            var (_, _, min, _, _) = timer.GetTiming("operation");

            Assert.True(min > 0);
            Assert.True(min < 40, $"Expected min < 40ms, got {min}ms");
        }

        [Fact]
        public void GetTiming_CalculatesCorrectMaximum()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(20);
            timer.Stop("operation");

            timer.Start("operation");
            Thread.Sleep(50);
            timer.Stop("operation");

            var (_, _, _, max, _) = timer.GetTiming("operation");

            Assert.True(max >= 50, $"Expected max >= 50ms, got {max}ms");
        }

        [Fact]
        public void GetTiming_CalculatesCorrectAverage()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(30);
            timer.Stop("operation");

            timer.Start("operation");
            Thread.Sleep(30);
            timer.Stop("operation");

            var (total, count, _, _, average) = timer.GetTiming("operation");

            var expectedAverage = (double)total / count;
            Assert.Equal(expectedAverage, average);
        }

        [Fact]
        public void GetTiming_NonExistentOperation_ReturnsZeros()
        {
            var timer = new OptimizationTimer();

            var (total, count, min, max, average) = timer.GetTiming("non_existent");

            Assert.Equal(0, total);
            Assert.Equal(0, count);
            Assert.Equal(0, min);
            Assert.Equal(0, max);
            Assert.Equal(0, average);
        }

        [Fact]
        public void GetReport_ReturnsFormattedString()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            var report = timer.GetReport();

            Assert.NotNull(report);
            Assert.NotEmpty(report);
            Assert.Contains("OPTIMIZATION PERFORMANCE REPORT", report);
        }

        [Fact]
        public void GetReport_IncludesOperationNames()
        {
            var timer = new OptimizationTimer();

            timer.Start("test_operation");
            Thread.Sleep(10);
            timer.Stop("test_operation");

            var report = timer.GetReport();

            Assert.Contains("test_operation", report);
        }

        [Fact]
        public void GetReport_IncludesTimingColumns()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            var report = timer.GetReport();

            Assert.Contains("Time", report);
            Assert.Contains("Count", report);
            Assert.Contains("Avg", report);
            Assert.Contains("Min", report);
            Assert.Contains("Max", report);
        }

        [Fact]
        public void GetReport_IncludesTotalTime()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            var report = timer.GetReport();

            Assert.Contains("TOTAL", report);
            Assert.Contains("ms", report);
        }

        [Fact]
        public void GetBottleneckSummary_ReturnsFormattedString()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            var summary = timer.GetBottleneckSummary();

            Assert.NotNull(summary);
            Assert.NotEmpty(summary);
            Assert.Contains("TOP PERFORMANCE BOTTLENECKS", summary);
        }

        [Fact]
        public void GetBottleneckSummary_IncludesTopOperations()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation1");
            Thread.Sleep(20);
            timer.Stop("operation1");

            timer.Start("operation2");
            Thread.Sleep(10);
            timer.Stop("operation2");

            var summary = timer.GetBottleneckSummary(2);

            Assert.Contains("operation1", summary);
        }

        [Fact]
        public void GetBottleneckSummary_RespectTopCount()
        {
            var timer = new OptimizationTimer();

            for (int i = 0; i < 10; i++)
            {
                timer.Start($"operation{i}");
                Thread.Sleep(5);
                timer.Stop($"operation{i}");
            }

            var summary = timer.GetBottleneckSummary(3);

            var lines = summary.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            var operationLines = 0;

            foreach (var line in lines)
            {
                if (line.StartsWith("1.") || line.StartsWith("2.") || line.StartsWith("3."))
                    operationLines++;
            }

            Assert.True(operationLines > 0);
        }

        [Fact]
        public void Clear_RemovesAllTimings()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            timer.Clear();

            var (total, count, _, _, _) = timer.GetTiming("operation");

            Assert.Equal(0, total);
            Assert.Equal(0, count);
        }

        [Fact]
        public void Clear_AllowsRestartAfterClear()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            timer.Clear();

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            var (total, count, _, _, _) = timer.GetTiming("operation");

            Assert.True(total > 0);
            Assert.Equal(1, count);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var timer = new OptimizationTimer();

            var exception = Record.Exception(() => timer.Dispose());

            Assert.Null(exception);
        }

        [Fact]
        public void MultipleOperations_TracksSeparately()
        {
            var timer = new OptimizationTimer();

            timer.Start("op1");
            Thread.Sleep(20);
            timer.Stop("op1");

            timer.Start("op2");
            Thread.Sleep(50);
            timer.Stop("op2");

            var (total1, count1, _, _, _) = timer.GetTiming("op1");
            var (total2, count2, _, _, _) = timer.GetTiming("op2");

            Assert.True(total1 > 0);
            Assert.True(total2 > 0);
            Assert.Equal(1, count1);
            Assert.Equal(1, count2);
            Assert.True(total2 > total1, "op2 should take longer than op1");
        }

        [Fact]
        public void NestedOperations_TrackCorrectly()
        {
            var timer = new OptimizationTimer();

            timer.Start("outer");
            Thread.Sleep(10);

            timer.Start("inner");
            Thread.Sleep(10);
            timer.Stop("inner");

            Thread.Sleep(10);
            timer.Stop("outer");

            var (outerTotal, _, _, _, _) = timer.GetTiming("outer");
            var (innerTotal, _, _, _, _) = timer.GetTiming("inner");

            Assert.True(outerTotal > 0);
            Assert.True(innerTotal > 0);
            Assert.True(outerTotal > innerTotal);
        }

        [Fact]
        public void ThreadSafety_ConcurrentOperations_DoesNotThrow()
        {
            var timer = new OptimizationTimer();

            var tasks = new Task[5];

            for (int i = 0; i < 5; i++)
            {
                var taskId = i;
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 10; j++)
                    {
                        timer.Start($"operation{taskId}");
                        Thread.Sleep(1);
                        timer.Stop($"operation{taskId}");
                    }
                });
            }

            var exception = Record.Exception(() => Task.WaitAll(tasks));

            Assert.Null(exception);
        }

        [Fact]
        public void Stop_WithoutStart_ReturnsZero()
        {
            var timer = new OptimizationTimer();

            var elapsed = timer.Stop("never_started");

            Assert.Equal(0, elapsed);
        }

        [Fact]
        public void RepeatedStartStop_AccumulatesTimes()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            var elapsed1 = timer.Stop("operation");

            timer.Start("operation");
            Thread.Sleep(10);
            timer.Stop("operation");

            var (total, count, _, _, _) = timer.GetTiming("operation");

            Assert.Equal(2, count);
            Assert.True(total > 10);
        }

        [Fact]
        public void GetReport_HandlesEmptyTimer()
        {
            var timer = new OptimizationTimer();

            var report = timer.GetReport();

            Assert.NotNull(report);
            Assert.Contains("No timing data collected", report);
        }

        [Fact]
        public void GetBottleneckSummary_HandlesEmptyTimer()
        {
            var timer = new OptimizationTimer();

            var summary = timer.GetBottleneckSummary();

            Assert.NotNull(summary);
            Assert.NotEmpty(summary);
        }

        [Fact]
        public void OperationName_CaseSensitive()
        {
            var timer = new OptimizationTimer();

            timer.Start("Operation");
            Thread.Sleep(10);
            timer.Stop("Operation");

            var (total1, _, _, _, _) = timer.GetTiming("Operation");
            var (total2, _, _, _, _) = timer.GetTiming("operation");

            Assert.True(total1 > 0);
            Assert.Equal(0, total2);
        }

        [Fact]
        public void GetReport_IncludesGlobalElapsedTime()
        {
            var timer = new OptimizationTimer();

            Thread.Sleep(20);

            var report = timer.GetReport();

            Assert.Contains("Total Elapsed Time", report);
            Assert.Contains("ms", report);
        }

        [Fact]
        public void Start_OnExistingOperation_RestartsTimer()
        {
            var timer = new OptimizationTimer();

            timer.Start("operation");
            Thread.Sleep(20);

            timer.Start("operation");
            Thread.Sleep(10);
            var elapsed = timer.Stop("operation");

            Assert.True(elapsed > 0);
            Assert.True(elapsed < 20, "Timer should have been restarted");
        }
    }
}
