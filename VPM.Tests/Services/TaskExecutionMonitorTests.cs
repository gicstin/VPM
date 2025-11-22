using Xunit;
using VPM.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Tests.Services
{
    public class TaskExecutionMonitorTests
    {
        private class TestWorkTask : WorkTask
        {
            public TestWorkTask(string name = "TestTask", string type = "TestType")
            {
                TaskName = name;
                TaskType = type;
            }

            public string TaskType { get; }

            public override string GetTaskType() => TaskType;

            public override async Task ExecuteAsync(CancellationToken cancellationToken)
            {
                UpdateState(TaskState.Running);
                await Task.Delay(10, cancellationToken);
                MarkCompleted();
            }
        }

        [Fact]
        public void Constructor_InitializesMonitor()
        {
            var monitor = new TaskExecutionMonitor();

            Assert.NotNull(monitor);
        }

        [Fact]
        public void StartTask_WithValidTask_AddsTaskMetrics()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask("TestTask");

            monitor.StartTask(task);

            var metrics = monitor.GetTaskMetrics(task.TaskId);
            Assert.NotNull(metrics);
            Assert.Equal(task.TaskName, metrics.TaskName);
        }

        [Fact]
        public void StartTask_WithNullTask_DoesNotThrow()
        {
            var monitor = new TaskExecutionMonitor();

            var exception = Record.Exception(() => monitor.StartTask(null));

            Assert.Null(exception);
        }

        [Fact]
        public void StartTask_InitializesMetricsCorrectly()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask("MyTask", "ImageProcessing");

            monitor.StartTask(task);

            var metrics = monitor.GetTaskMetrics(task.TaskId);
            Assert.Equal("MyTask", metrics.TaskName);
            Assert.Equal("ImageProcessing", metrics.TaskType);
            Assert.Equal(TaskState.Running, metrics.State);
            Assert.Equal("Running", metrics.Status);
        }

        [Fact]
        public void UpdateTaskProgress_UpdatesProgressMetrics()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();
            task.UpdateState(TaskState.Running);
            task.ProgressPercent = 50;
            task.CompletedWorkUnits = 500;

            monitor.StartTask(task);
            monitor.UpdateTaskProgress(task);

            var metrics = monitor.GetTaskMetrics(task.TaskId);
            Assert.Equal(50, metrics.ProgressPercent);
            Assert.Equal(500, metrics.BytesProcessed);
        }

        [Fact]
        public void UpdateTaskProgress_WithNullTask_DoesNotThrow()
        {
            var monitor = new TaskExecutionMonitor();

            var exception = Record.Exception(() => monitor.UpdateTaskProgress(null));

            Assert.Null(exception);
        }

        [Fact]
        public void CompleteTask_MarksTaskCompleted()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();
            task.UpdateState(TaskState.Running);
            task.CompletedWorkUnits = 1000;

            monitor.StartTask(task);
            monitor.CompleteTask(task, success: true);

            var metrics = monitor.GetTaskMetrics(task.TaskId);
            Assert.Equal(TaskState.Completed, metrics.State);
            Assert.Equal("Completed", metrics.Status);
            Assert.Equal(100, metrics.ProgressPercent);
        }

        [Fact]
        public void CompleteTask_WithFailure_MarksTaskFailed()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();
            task.UpdateState(TaskState.Running);

            monitor.StartTask(task);
            monitor.CompleteTask(task, success: false);

            var metrics = monitor.GetTaskMetrics(task.TaskId);
            Assert.Equal(TaskState.Failed, metrics.State);
            Assert.Equal("Failed", metrics.Status);
        }

        [Fact]
        public void CompleteTask_WithException_StoresErrorMessage()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();
            task.UpdateState(TaskState.Running);
            var exception = new InvalidOperationException("Test error");
            task.Exception = exception;

            monitor.StartTask(task);
            monitor.CompleteTask(task, success: false);

            var metrics = monitor.GetTaskMetrics(task.TaskId);
            Assert.Equal("Test error", metrics.ErrorMessage);
        }

        [Fact]
        public void GetTaskMetrics_NonExistentTask_ReturnsNull()
        {
            var monitor = new TaskExecutionMonitor();

            var metrics = monitor.GetTaskMetrics("non_existent_id");

            Assert.Null(metrics);
        }

        [Fact]
        public void GetTypeMetrics_ReturnsMetricsForType()
        {
            var monitor = new TaskExecutionMonitor();
            var task1 = new TestWorkTask("Task1", "ImageType");
            var task2 = new TestWorkTask("Task2", "ImageType");

            monitor.StartTask(task1);
            monitor.StartTask(task2);
            monitor.CompleteTask(task1, success: true);

            var typeMetrics = monitor.GetTypeMetrics("ImageType");
            Assert.NotNull(typeMetrics);
            Assert.Equal("ImageType", typeMetrics.TaskType);
            Assert.Equal(1, typeMetrics.CompletedCount);
            Assert.Equal(1, typeMetrics.RunningCount);
        }

        [Fact]
        public void GetActiveTaskMetrics_ReturnsRunningTasks()
        {
            var monitor = new TaskExecutionMonitor();
            var task1 = new TestWorkTask();
            var task2 = new TestWorkTask();

            monitor.StartTask(task1);
            monitor.StartTask(task2);
            monitor.CompleteTask(task1, success: true);

            var activeTasks = monitor.GetActiveTaskMetrics();

            Assert.Single(activeTasks);
            Assert.Equal(task2.TaskId, activeTasks[0].TaskId);
        }

        [Fact]
        public void GetCompletedTaskMetrics_ReturnsCompletedTasks()
        {
            var monitor = new TaskExecutionMonitor();
            var task1 = new TestWorkTask();
            var task2 = new TestWorkTask();

            monitor.StartTask(task1);
            monitor.StartTask(task2);
            monitor.CompleteTask(task1, success: true);
            monitor.CompleteTask(task2, success: true);

            var completedTasks = monitor.GetCompletedTaskMetrics();

            Assert.Equal(2, completedTasks.Count);
        }

        [Fact]
        public void GetFailedTaskMetrics_ReturnsFailedTasks()
        {
            var monitor = new TaskExecutionMonitor();
            var task1 = new TestWorkTask();
            var task2 = new TestWorkTask();

            monitor.StartTask(task1);
            monitor.StartTask(task2);
            monitor.CompleteTask(task1, success: true);
            monitor.CompleteTask(task2, success: false);

            var failedTasks = monitor.GetFailedTaskMetrics();

            Assert.Single(failedTasks);
            Assert.Equal(TaskState.Failed, failedTasks[0].State);
        }

        [Fact]
        public void GetAllTypeMetrics_ReturnsAllTypes()
        {
            var monitor = new TaskExecutionMonitor();
            var task1 = new TestWorkTask("Task1", "Type1");
            var task2 = new TestWorkTask("Task2", "Type2");

            monitor.StartTask(task1);
            monitor.StartTask(task2);

            var allMetrics = monitor.GetAllTypeMetrics();

            Assert.NotNull(allMetrics);
            Assert.Contains("Type1", allMetrics.Keys);
            Assert.Contains("Type2", allMetrics.Keys);
        }

        [Fact]
        public void ThroughputMBps_CalculatesCorrectly()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();
            task.UpdateState(TaskState.Running);
            task.CompletedWorkUnits = 1024 * 1024;

            monitor.StartTask(task);
            Thread.Sleep(100);
            monitor.CompleteTask(task, success: true);

            var metrics = monitor.GetTaskMetrics(task.TaskId);
            Assert.True(metrics.ThroughputMBps > 0);
        }

        [Fact]
        public void GetPerformanceSnapshot_ReturnsSnapshot()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();

            monitor.StartTask(task);

            var snapshot = monitor.GetPerformanceSnapshot();

            Assert.NotNull(snapshot);
            Assert.Equal(1, snapshot.TotalActiveTasks);
            Assert.Equal(0, snapshot.TotalCompletedTasks);
        }

        [Fact]
        public void PerformanceSnapshot_IncludesCorrectCounts()
        {
            var monitor = new TaskExecutionMonitor();
            var task1 = new TestWorkTask();
            var task2 = new TestWorkTask();
            var task3 = new TestWorkTask();

            monitor.StartTask(task1);
            monitor.StartTask(task2);
            monitor.StartTask(task3);
            monitor.CompleteTask(task1, success: true);
            monitor.CompleteTask(task2, success: false);

            var snapshot = monitor.GetPerformanceSnapshot();

            Assert.Equal(1, snapshot.TotalActiveTasks);
            Assert.Equal(1, snapshot.TotalCompletedTasks);
            Assert.Equal(1, snapshot.TotalFailedTasks);
        }

        [Fact]
        public void GetPerformanceReport_ReturnsFormattedString()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();

            monitor.StartTask(task);
            monitor.CompleteTask(task, success: true);

            var report = monitor.GetPerformanceReport();

            Assert.NotNull(report);
            Assert.Contains("Task Execution Performance Report", report);
            Assert.Contains("Active Tasks", report);
        }

        [Fact]
        public void SuccessRate_CalculatesCorrectly()
        {
            var monitor = new TaskExecutionMonitor();
            var task1 = new TestWorkTask("Task1", "TestType");
            var task2 = new TestWorkTask("Task2", "TestType");
            var task3 = new TestWorkTask("Task3", "TestType");

            monitor.StartTask(task1);
            monitor.StartTask(task2);
            monitor.StartTask(task3);
            monitor.CompleteTask(task1, success: true);
            monitor.CompleteTask(task2, success: true);
            monitor.CompleteTask(task3, success: false);

            var typeMetrics = monitor.GetTypeMetrics("TestType");

            Assert.Equal(2, typeMetrics.CompletedCount);
            Assert.Equal(1, typeMetrics.FailedCount);
            Assert.True(typeMetrics.SuccessRate > 60 && typeMetrics.SuccessRate < 70);
        }

        [Fact]
        public void AverageDuration_CalculatesCorrectly()
        {
            var monitor = new TaskExecutionMonitor();
            var task1 = new TestWorkTask("Task1", "TestType");
            var task2 = new TestWorkTask("Task2", "TestType");

            monitor.StartTask(task1);
            Thread.Sleep(50);
            monitor.CompleteTask(task1, success: true);

            monitor.StartTask(task2);
            Thread.Sleep(100);
            monitor.CompleteTask(task2, success: true);

            var typeMetrics = monitor.GetTypeMetrics("TestType");

            Assert.True(typeMetrics.AverageDuration.TotalMilliseconds > 0);
        }

        [Fact]
        public void GetLatencyPercentile_ReturnsCorrectValue()
        {
            var monitor = new TaskExecutionMonitor();

            for (int i = 0; i < 100; i++)
            {
                var task = new TestWorkTask("Task", "TestType");
                monitor.StartTask(task);
                Thread.Sleep(1);
                monitor.CompleteTask(task, success: true);
            }

            var p50 = monitor.GetLatencyPercentile("TestType", 50);
            var p99 = monitor.GetLatencyPercentile("TestType", 99);

            Assert.True(p50 > 0);
            Assert.True(p99 >= p50);
        }

        [Fact]
        public void Clear_RemovesAllMetrics()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();

            monitor.StartTask(task);
            monitor.CompleteTask(task, success: true);
            monitor.Clear();

            var snapshot = monitor.GetPerformanceSnapshot();

            Assert.Equal(0, snapshot.TotalCompletedTasks);
            Assert.Equal(0, snapshot.TotalActiveTasks);
        }

        [Fact]
        public void MultipleTaskTypes_TrackSeparately()
        {
            var monitor = new TaskExecutionMonitor();
            var imageTask = new TestWorkTask("ImageTask", "ImageProcessing");
            var jsonTask = new TestWorkTask("JsonTask", "JsonProcessing");

            monitor.StartTask(imageTask);
            monitor.StartTask(jsonTask);
            monitor.CompleteTask(imageTask, success: true);
            monitor.CompleteTask(jsonTask, success: false);

            var imageMetrics = monitor.GetTypeMetrics("ImageProcessing");
            var jsonMetrics = monitor.GetTypeMetrics("JsonProcessing");

            Assert.Equal(1, imageMetrics.CompletedCount);
            Assert.Equal(0, imageMetrics.FailedCount);
            Assert.Equal(0, jsonMetrics.CompletedCount);
            Assert.Equal(1, jsonMetrics.FailedCount);
        }

        [Fact]
        public void BytesProcessed_AccumulatesCorrectly()
        {
            var monitor = new TaskExecutionMonitor();
            var task1 = new TestWorkTask();
            task1.CompletedWorkUnits = 1000;
            var task2 = new TestWorkTask();
            task2.CompletedWorkUnits = 2000;

            monitor.StartTask(task1);
            monitor.CompleteTask(task1, success: true);

            monitor.StartTask(task2);
            monitor.CompleteTask(task2, success: true);

            var snapshot = monitor.GetPerformanceSnapshot();

            Assert.Equal(3000, snapshot.TotalBytesProcessed);
        }

        [Fact]
        public void OverallThroughputMBps_CalculatesCorrectly()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();
            task.CompletedWorkUnits = 1024 * 1024;

            monitor.StartTask(task);
            Thread.Sleep(100);
            monitor.CompleteTask(task, success: true);

            var snapshot = monitor.GetPerformanceSnapshot();

            Assert.True(snapshot.OverallThroughputMBps > 0);
        }

        [Fact]
        public void GetPerformanceSnapshot_IncludesTypeMetrics()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask("Task", "MyType");

            monitor.StartTask(task);

            var snapshot = monitor.GetPerformanceSnapshot();

            Assert.NotNull(snapshot.TypeMetrics);
            Assert.Contains("MyType", snapshot.TypeMetrics.Keys);
        }

        [Fact]
        public void CompleteTask_WithNullTask_DoesNotThrow()
        {
            var monitor = new TaskExecutionMonitor();

            var exception = Record.Exception(() => monitor.CompleteTask(null, true));

            Assert.Null(exception);
        }

        [Fact]
        public void GetTaskMetrics_ReturnsCopyOfMetrics()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();

            monitor.StartTask(task);
            var metrics = monitor.GetTaskMetrics(task.TaskId);

            Assert.NotNull(metrics);
            Assert.Equal(task.TaskId, metrics.TaskId);
        }

        [Fact]
        public void MonitoringDuration_IncreasesOverTime()
        {
            var monitor = new TaskExecutionMonitor();
            var snapshot1 = monitor.GetPerformanceSnapshot();

            Thread.Sleep(50);

            var snapshot2 = monitor.GetPerformanceSnapshot();

            Assert.True(snapshot2.MonitoringDuration >= snapshot1.MonitoringDuration);
        }

        [Fact]
        public void LatencyHistory_LimitedToPreventMemoryLeak()
        {
            var monitor = new TaskExecutionMonitor();

            for (int i = 0; i < 2000; i++)
            {
                var task = new TestWorkTask("Task", "TestType");
                monitor.StartTask(task);
                monitor.CompleteTask(task, success: true);
            }

            var typeMetrics = monitor.GetTypeMetrics("TestType");

            Assert.True(typeMetrics.LatencyHistory.Count <= 1000);
        }

        [Fact]
        public void GetPerformanceReport_HandlesEmptyMonitor()
        {
            var monitor = new TaskExecutionMonitor();

            var report = monitor.GetPerformanceReport();

            Assert.NotNull(report);
            Assert.NotEmpty(report);
            Assert.Contains("Task Execution Performance Report", report);
        }

        [Fact]
        public void TaskMetrics_CalculatesDurationCorrectly()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();

            monitor.StartTask(task);
            Thread.Sleep(50);
            monitor.CompleteTask(task, success: true);

            var metrics = monitor.GetTaskMetrics(task.TaskId);

            Assert.NotNull(metrics.Duration);
            Assert.True(metrics.Duration.Value.TotalMilliseconds >= 50);
        }

        [Fact]
        public void StartTask_SetsCorrectStartTime()
        {
            var monitor = new TaskExecutionMonitor();
            var beforeStart = DateTime.UtcNow;
            var task = new TestWorkTask();

            monitor.StartTask(task);

            var metrics = monitor.GetTaskMetrics(task.TaskId);
            Assert.True(metrics.StartTime >= beforeStart);
            Assert.True(metrics.StartTime <= DateTime.UtcNow);
        }

        [Fact]
        public void CompleteTask_SetsCorrectEndTime()
        {
            var monitor = new TaskExecutionMonitor();
            var task = new TestWorkTask();

            monitor.StartTask(task);
            var beforeComplete = DateTime.UtcNow;
            monitor.CompleteTask(task, success: true);
            var afterComplete = DateTime.UtcNow;

            var metrics = monitor.GetTaskMetrics(task.TaskId);
            Assert.NotNull(metrics.EndTime);
            Assert.True(metrics.EndTime >= beforeComplete);
            Assert.True(metrics.EndTime <= afterComplete);
        }
    }
}
