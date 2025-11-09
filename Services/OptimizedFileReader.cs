using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Provides optimized file reading operations using buffer pooling and async I/O
    /// </summary>
    public class OptimizedFileReader : IDisposable
    {
        private const int DEFAULT_BUFFER_SIZE = 81920; // 80KB buffer
        private static readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
        
        private byte[] _buffer;
        private bool _disposed;

        public OptimizedFileReader()
        {
            _buffer = _arrayPool.Rent(DEFAULT_BUFFER_SIZE);
        }

        /// <summary>
        /// Reads a file asynchronously using pooled buffers for optimal memory usage
        /// </summary>
        public async Task<byte[]> ReadFileAsync(string filePath, int? customBufferSize = null)
        {
            if (customBufferSize.HasValue && customBufferSize.Value > _buffer.Length)
            {
                // Return current buffer and get a larger one
                _arrayPool.Return(_buffer);
                _buffer = _arrayPool.Rent(customBufferSize.Value);
            }

            try
            {
                using var fileStream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    DEFAULT_BUFFER_SIZE,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                if (fileStream.Length > int.MaxValue)
                {
                    throw new IOException("File is too large to process");
                }

                var result = new byte[fileStream.Length];
                var totalBytesRead = 0;
                
                while (totalBytesRead < result.Length)
                {
                    var bytesRemaining = result.Length - totalBytesRead;
                    var bytesToRead = Math.Min(bytesRemaining, _buffer.Length);
                    
                    var bytesRead = await fileStream.ReadAsync(_buffer.AsMemory(0, (int)bytesToRead));
                    if (bytesRead == 0) break;
                    
                    Buffer.BlockCopy(_buffer, 0, result, totalBytesRead, bytesRead);
                    totalBytesRead += bytesRead;
                }

                return result;
            }
            catch (Exception ex)
            {
                throw new IOException($"Error reading file {filePath}", ex);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_buffer != null)
                {
                    _arrayPool.Return(_buffer);
                    _buffer = null;
                }
                _disposed = true;
            }
        }
    }
}
