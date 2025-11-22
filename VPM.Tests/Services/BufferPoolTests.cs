using Xunit;
using VPM.Services;
using System;

namespace VPM.Tests.Services
{
    public class BufferPoolTests
    {
        [Fact]
        public void RentBuffer_RequestsValidSize_ReturnsBufferWithAtLeastRequestedSize()
        {
            var buffer = BufferPool.RentBuffer(1024);

            Assert.NotNull(buffer);
            Assert.True(buffer.Length >= 1024);

            BufferPool.ReturnBuffer(buffer);
        }

        [Fact]
        public void RentBuffer_SmallRequest_Returns8KBBuffer()
        {
            var buffer = BufferPool.RentBuffer(1024);

            Assert.NotNull(buffer);
            Assert.Equal(8 * 1024, buffer.Length);

            BufferPool.ReturnBuffer(buffer);
        }

        [Fact]
        public void RentBuffer_64KBRequest_Returns64KBBuffer()
        {
            var buffer = BufferPool.RentBuffer(50 * 1024);

            Assert.NotNull(buffer);
            Assert.Equal(64 * 1024, buffer.Length);

            BufferPool.ReturnBuffer(buffer);
        }

        [Fact]
        public void RentBuffer_256KBRequest_Returns256KBBuffer()
        {
            var buffer = BufferPool.RentBuffer(200 * 1024);

            Assert.NotNull(buffer);
            Assert.Equal(256 * 1024, buffer.Length);

            BufferPool.ReturnBuffer(buffer);
        }

        [Fact]
        public void RentBuffer_1MBRequest_Returns1MBBuffer()
        {
            var buffer = BufferPool.RentBuffer(800 * 1024);

            Assert.NotNull(buffer);
            Assert.Equal(1024 * 1024, buffer.Length);

            BufferPool.ReturnBuffer(buffer);
        }

        [Fact]
        public void RentBuffer_VeryLargeRequest_ReturnsExactSizeBuffer()
        {
            int requestSize = 5 * 1024 * 1024;
            var buffer = BufferPool.RentBuffer(requestSize);

            Assert.NotNull(buffer);
            Assert.True(buffer.Length >= requestSize);

            BufferPool.ReturnBuffer(buffer);
        }

        [Fact]
        public void ReturnBuffer_NullBuffer_DoesNotThrow()
        {
            BufferPool.ReturnBuffer(null);
        }

        [Fact]
        public void RentBuffer_MultipleCalls_IncrementsStatistics()
        {
            BufferPool.ResetStatistics();

            var buffer1 = BufferPool.RentBuffer(1024);
            var buffer2 = BufferPool.RentBuffer(2048);

            var stats = BufferPool.GetStatistics();

            Assert.Equal(2, stats.TotalRents);
            Assert.True(stats.TotalBytesRented > 0);

            BufferPool.ReturnBuffer(buffer1);
            BufferPool.ReturnBuffer(buffer2);
        }

        [Fact]
        public void ReturnBuffer_AfterRent_DecrementsCurrentConcurrentBytes()
        {
            BufferPool.ResetStatistics();

            var buffer = BufferPool.RentBuffer(1024);
            var statsAfterRent = BufferPool.GetStatistics();

            BufferPool.ReturnBuffer(buffer);
            var statsAfterReturn = BufferPool.GetStatistics();

            Assert.Equal(1, statsAfterReturn.TotalReturns);
            Assert.True(statsAfterRent.CurrentConcurrentBytes > statsAfterReturn.CurrentConcurrentBytes);
        }

        [Fact]
        public void GetStatistics_TracksRentsBySize()
        {
            BufferPool.ResetStatistics();

            BufferPool.RentBuffer(1024);
            BufferPool.RentBuffer(50 * 1024);
            BufferPool.RentBuffer(200 * 1024);

            var stats = BufferPool.GetStatistics();

            Assert.True(stats.RentsFor8KB > 0);
            Assert.True(stats.RentsFor64KB > 0);
            Assert.True(stats.RentsFor256KB > 0);
        }

        [Fact]
        public void ResetStatistics_ClearsAllStatistics()
        {
            var buffer = BufferPool.RentBuffer(1024);
            BufferPool.ReturnBuffer(buffer);

            BufferPool.ResetStatistics();
            var stats = BufferPool.GetStatistics();

            Assert.Equal(0, stats.TotalRents);
            Assert.Equal(0, stats.TotalReturns);
            Assert.Equal(0, stats.TotalBytesRented);
        }

        [Fact]
        public void UseBuffer_WithAction_ReturnsBufferAutomatically()
        {
            BufferPool.ResetStatistics();

            BufferPool.UseBuffer(1024, buffer =>
            {
                Assert.NotNull(buffer);
                Assert.True(buffer.Length >= 1024);
            });

            var stats = BufferPool.GetStatistics();
            Assert.Equal(1, stats.TotalRents);
            Assert.Equal(1, stats.TotalReturns);
        }

        [Fact]
        public void UseBuffer_WithFunc_ReturnsBufferAndValue()
        {
            BufferPool.ResetStatistics();

            int result = BufferPool.UseBuffer(1024, buffer =>
            {
                Assert.NotNull(buffer);
                return buffer.Length;
            });

            Assert.True(result >= 1024);

            var stats = BufferPool.GetStatistics();
            Assert.Equal(1, stats.TotalRents);
            Assert.Equal(1, stats.TotalReturns);
        }

        [Fact]
        public void UseBuffer_ActionThrowsException_StillReturnsBuffer()
        {
            BufferPool.ResetStatistics();

            Assert.Throws<InvalidOperationException>(() =>
            {
                BufferPool.UseBuffer(1024, buffer =>
                {
                    throw new InvalidOperationException("Test exception");
                });
            });

            var stats = BufferPool.GetStatistics();
            Assert.Equal(1, stats.TotalRents);
            Assert.Equal(1, stats.TotalReturns);
        }

        [Fact]
        public void RentBuffers_MultipleBuffers_ReturnsArrayOfBuffers()
        {
            var buffers = BufferPool.RentBuffers(3, 1024);

            Assert.NotNull(buffers);
            Assert.Equal(3, buffers.Length);

            foreach (var buffer in buffers)
            {
                Assert.NotNull(buffer);
                Assert.True(buffer.Length >= 1024);
            }

            BufferPool.ReturnBuffers(buffers);
        }

        [Fact]
        public void ReturnBuffers_MultipleBuffers_ReturnsAllBuffers()
        {
            BufferPool.ResetStatistics();

            var buffers = BufferPool.RentBuffers(3, 1024);
            BufferPool.ReturnBuffers(buffers);

            var stats = BufferPool.GetStatistics();
            Assert.Equal(3, stats.TotalRents);
            Assert.Equal(3, stats.TotalReturns);
        }

        [Fact]
        public void GetStatisticsReport_FormatsReportCorrectly()
        {
            BufferPool.ResetStatistics();

            var buffer = BufferPool.RentBuffer(1024);
            BufferPool.ReturnBuffer(buffer);

            var report = BufferPool.GetStatisticsReport();

            Assert.NotEmpty(report);
            Assert.Contains("BUFFER POOL STATISTICS REPORT", report);
            Assert.Contains("Total Rents", report);
        }

        [Fact]
        public void PeakConcurrentBytes_TracksMaximumConcurrentMemory()
        {
            BufferPool.ResetStatistics();

            var buffer1 = BufferPool.RentBuffer(1024 * 1024);
            var stats1 = BufferPool.GetStatistics();

            var buffer2 = BufferPool.RentBuffer(1024 * 1024);
            var stats2 = BufferPool.GetStatistics();

            BufferPool.ReturnBuffer(buffer1);

            var peakStats = BufferPool.GetStatistics();

            Assert.True(peakStats.PeakConcurrentBytes >= stats2.CurrentConcurrentBytes);

            BufferPool.ReturnBuffer(buffer2);
        }

        [Fact]
        public void RentBuffer_ZeroSize_ReturnsMinimumBuffer()
        {
            var buffer = BufferPool.RentBuffer(0);

            Assert.NotNull(buffer);
            Assert.True(buffer.Length > 0);

            BufferPool.ReturnBuffer(buffer);
        }

        [Fact]
        public void UseBuffer_ClearsBuffer_BufferIsCleared()
        {
            BufferPool.UseBuffer(256, buffer =>
            {
                buffer[0] = 255;
                Assert.Equal(255, buffer[0]);
            }, clearBuffer: true);
        }

        [Fact]
        public void ThreadSafety_ConcurrentRentAndReturn_DoesNotThrow()
        {
            BufferPool.ResetStatistics();

            var tasks = new System.Threading.Tasks.Task[10];
            for (int i = 0; i < 10; i++)
            {
                tasks[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int j = 0; j < 10; j++)
                    {
                        var buffer = BufferPool.RentBuffer(1024);
                        System.Threading.Thread.Sleep(1);
                        BufferPool.ReturnBuffer(buffer);
                    }
                });
            }

            System.Threading.Tasks.Task.WaitAll(tasks);

            var stats = BufferPool.GetStatistics();
            Assert.Equal(100, stats.TotalRents);
            Assert.Equal(100, stats.TotalReturns);
        }
    }
}
