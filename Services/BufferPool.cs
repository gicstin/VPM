using System;
using System.Buffers;
using System.Collections.Generic;

namespace VPM.Services
{
    /// <summary>
    /// Memory pooling utility for efficient buffer reuse across the application.
    /// Reduces GC pressure and allocation overhead by reusing buffers from ArrayPool.
    /// </summary>
    public static class BufferPool
    {
        /// <summary>
        /// Rents a buffer from the pool. Must be returned with ReturnBuffer().
        /// </summary>
        public static byte[] RentBuffer(int minimumLength)
        {
            return ArrayPool<byte>.Shared.Rent(minimumLength);
        }

        /// <summary>
        /// Returns a rented buffer to the pool for reuse.
        /// </summary>
        public static void ReturnBuffer(byte[] buffer, bool clearBuffer = false)
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearBuffer);
            }
        }

        /// <summary>
        /// Executes an action with a rented buffer, automatically returning it.
        /// Ensures buffer is always returned even if an exception occurs.
        /// </summary>
        public static void UseBuffer(int minimumLength, Action<byte[]> action, bool clearBuffer = false)
        {
            byte[] buffer = RentBuffer(minimumLength);
            try
            {
                action(buffer);
            }
            finally
            {
                ReturnBuffer(buffer, clearBuffer);
            }
        }

        /// <summary>
        /// Executes a function with a rented buffer, automatically returning it.
        /// Ensures buffer is always returned even if an exception occurs.
        /// </summary>
        public static T UseBuffer<T>(int minimumLength, Func<byte[], T> func, bool clearBuffer = false)
        {
            byte[] buffer = RentBuffer(minimumLength);
            try
            {
                return func(buffer);
            }
            finally
            {
                ReturnBuffer(buffer, clearBuffer);
            }
        }

        /// <summary>
        /// Rents multiple buffers at once.
        /// </summary>
        public static byte[][] RentBuffers(int count, int minimumLength)
        {
            byte[][] buffers = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                buffers[i] = RentBuffer(minimumLength);
            }
            return buffers;
        }

        /// <summary>
        /// Returns multiple buffers to the pool.
        /// </summary>
        public static void ReturnBuffers(byte[][] buffers, bool clearBuffers = false)
        {
            if (buffers != null)
            {
                foreach (var buffer in buffers)
                {
                    ReturnBuffer(buffer, clearBuffers);
                }
            }
        }
    }
}
