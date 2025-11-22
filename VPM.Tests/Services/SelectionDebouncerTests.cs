using Xunit;
using VPM.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Tests.Services
{
    public class SelectionDebouncerTests
    {
        [Fact]
        public void Constructor_ValidParameters_InitializesSuccessfully()
        {
            Func<Task> action = async () => await Task.CompletedTask;

            var debouncer = new SelectionDebouncer(100, action);

            Assert.NotNull(debouncer);
        }

        [Fact]
        public void Constructor_NullAction_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
            {
                new SelectionDebouncer(100, null);
            });
        }

        [Fact]
        public async Task Trigger_ExecutesActionAfterDelay()
        {
            var actionExecuted = false;
            Func<Task> action = async () =>
            {
                actionExecuted = true;
                await Task.CompletedTask;
            };

            var debouncer = new SelectionDebouncer(50, action);
            debouncer.Trigger();

            await Task.Delay(100);

            Assert.True(actionExecuted);
            debouncer.Dispose();
        }

        [Fact]
        public async Task Trigger_MultipleTriggers_OnlyExecutesLastOne()
        {
            int executionCount = 0;
            Func<Task> action = async () =>
            {
                executionCount++;
                await Task.CompletedTask;
            };

            var debouncer = new SelectionDebouncer(50, action);

            debouncer.Trigger();
            await Task.Delay(10);
            debouncer.Trigger();
            await Task.Delay(10);
            debouncer.Trigger();

            await Task.Delay(100);

            Assert.Equal(1, executionCount);
            debouncer.Dispose();
        }

        [Fact]
        public async Task Trigger_RepeatedTriggers_CancelsAndRestarts()
        {
            int executionCount = 0;
            Func<Task> action = async () =>
            {
                executionCount++;
                await Task.CompletedTask;
            };

            var debouncer = new SelectionDebouncer(100, action);

            debouncer.Trigger();
            await Task.Delay(30);
            debouncer.Trigger();
            await Task.Delay(30);
            debouncer.Trigger();
            await Task.Delay(150);

            Assert.Equal(1, executionCount);
            debouncer.Dispose();
        }

        [Fact]
        public async Task Cancel_PreventsPendingExecution()
        {
            var actionExecuted = false;
            Func<Task> action = async () =>
            {
                actionExecuted = true;
                await Task.CompletedTask;
            };

            var debouncer = new SelectionDebouncer(100, action);
            debouncer.Trigger();
            await Task.Delay(20);
            debouncer.Cancel();
            await Task.Delay(100);

            Assert.False(actionExecuted);
            debouncer.Dispose();
        }

        [Fact]
        public async Task Dispose_CancelsPendingExecution()
        {
            var actionExecuted = false;
            Func<Task> action = async () =>
            {
                actionExecuted = true;
                await Task.CompletedTask;
            };

            var debouncer = new SelectionDebouncer(100, action);
            debouncer.Trigger();
            debouncer.Dispose();
            await Task.Delay(150);

            Assert.False(actionExecuted);
        }

        [Fact]
        public async Task Trigger_WithAsyncAction_ExecutesAsync()
        {
            var actionExecuted = false;
            var delayOccurred = false;

            Func<Task> action = async () =>
            {
                actionExecuted = true;
                await Task.Delay(20);
                delayOccurred = true;
            };

            var debouncer = new SelectionDebouncer(50, action);
            debouncer.Trigger();

            await Task.Delay(30);
            Assert.True(actionExecuted);
            Assert.False(delayOccurred);

            await Task.Delay(30);
            Assert.True(delayOccurred);

            debouncer.Dispose();
        }

        [Fact]
        public async Task Trigger_ActionThrowsException_ExceptionHandled()
        {
            Func<Task> action = async () =>
            {
                await Task.CompletedTask;
                throw new InvalidOperationException("Test exception");
            };

            var debouncer = new SelectionDebouncer(50, action);

            Assert.Null(Record.Exception(() =>
            {
                debouncer.Trigger();
            }));

            await Task.Delay(100);

            debouncer.Dispose();
        }

        [Fact]
        public async Task MultipleTriggerCycles_RestartsProperlyEachTime()
        {
            int executionCount = 0;
            Func<Task> action = async () =>
            {
                executionCount++;
                await Task.CompletedTask;
            };

            var debouncer = new SelectionDebouncer(50, action);

            debouncer.Trigger();
            await Task.Delay(100);
            Assert.Equal(1, executionCount);

            debouncer.Trigger();
            await Task.Delay(100);
            Assert.Equal(2, executionCount);

            debouncer.Trigger();
            await Task.Delay(100);
            Assert.Equal(3, executionCount);

            debouncer.Dispose();
        }

        [Fact]
        public async Task Trigger_WithVeryShortDelay_StillWaitsFullDelay()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Func<Task> action = async () =>
            {
                await Task.CompletedTask;
            };

            var debouncer = new SelectionDebouncer(100, action);
            debouncer.Trigger();

            await Task.Delay(150);
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds >= 100);
            debouncer.Dispose();
        }

        [Fact]
        public async Task Trigger_WithLongDelay_WaitsFullDelay()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Func<Task> action = async () =>
            {
                await Task.CompletedTask;
            };

            var debouncer = new SelectionDebouncer(200, action);
            debouncer.Trigger();

            await Task.Delay(250);
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds >= 200);
            debouncer.Dispose();
        }

        [Fact]
        public async Task TriggerRapidly_LastTriggerDeterminesExecution()
        {
            var results = new System.Collections.Generic.List<string>();
            Func<Task> action = async () =>
            {
                results.Add("executed");
                await Task.CompletedTask;
            };

            var debouncer = new SelectionDebouncer(50, action);

            for (int i = 0; i < 10; i++)
            {
                debouncer.Trigger();
                await Task.Delay(5);
            }

            await Task.Delay(100);

            Assert.Single(results);
            debouncer.Dispose();
        }

        [Fact]
        public async Task Cancel_BeforeTrigger_DoesNotThrow()
        {
            Func<Task> action = async () => await Task.CompletedTask;
            var debouncer = new SelectionDebouncer(100, action);

            debouncer.Cancel();

            debouncer.Dispose();
        }

        [Fact]
        public async Task Dispose_Multiple_DoesNotThrow()
        {
            Func<Task> action = async () => await Task.CompletedTask;
            var debouncer = new SelectionDebouncer(100, action);

            debouncer.Dispose();
            debouncer.Dispose();
            debouncer.Dispose();
        }

        [Fact]
        public async Task Trigger_AfterDispose_ActionNotExecuted()
        {
            var actionExecuted = false;
            Func<Task> action = async () =>
            {
                actionExecuted = true;
                await Task.CompletedTask;
            };

            var debouncer = new SelectionDebouncer(50, action);
            debouncer.Dispose();
            debouncer.Trigger();

            await Task.Delay(100);

            Assert.False(actionExecuted);
        }

        [Fact]
        public async Task Trigger_ComplexAction_ExecutesCorrectly()
        {
            var state = new { Value = 0 };
            var finalValue = 0;

            Func<Task> action = async () =>
            {
                await Task.Delay(10);
                finalValue = 42;
            };

            var debouncer = new SelectionDebouncer(50, action);
            debouncer.Trigger();

            await Task.Delay(100);

            Assert.Equal(42, finalValue);
            debouncer.Dispose();
        }

        [Fact]
        public async Task Cancel_DuringExecution_StopsWaiting()
        {
            var actionStarted = false;
            var actionCompleted = false;

            Func<Task> action = async () =>
            {
                actionStarted = true;
                await Task.Delay(100);
                actionCompleted = true;
            };

            var debouncer = new SelectionDebouncer(50, action);
            debouncer.Trigger();

            await Task.Delay(60);
            Assert.True(actionStarted);

            debouncer.Cancel();
            await Task.Delay(150);

            debouncer.Dispose();
        }
    }
}
