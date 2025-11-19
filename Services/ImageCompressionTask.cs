using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// Image compression options
    /// </summary>
    public class ImageCompressionOptions
    {
        /// <summary>
        /// JPEG quality (0-100)
        /// </summary>
        public int JpegQuality { get; set; } = 85;

        /// <summary>
        /// PNG compression level (0-9)
        /// </summary>
        public int PngCompressionLevel { get; set; } = 6;

        /// <summary>
        /// Maximum width (0 = no limit)
        /// </summary>
        public int MaxWidth { get; set; } = 0;

        /// <summary>
        /// Maximum height (0 = no limit)
        /// </summary>
        public int MaxHeight { get; set; } = 0;

        /// <summary>
        /// Preserve aspect ratio when resizing
        /// </summary>
        public bool PreserveAspectRatio { get; set; } = true;

        /// <summary>
        /// Remove metadata (EXIF, etc.)
        /// </summary>
        public bool RemoveMetadata { get; set; } = true;

        /// <summary>
        /// Use high-quality resampling
        /// </summary>
        public bool HighQualityResampling { get; set; } = true;
    }

    /// <summary>
    /// Compression result
    /// </summary>
    public class ImageCompressionResult
    {
        public string InputPath { get; set; }
        public string OutputPath { get; set; }
        public long OriginalSize { get; set; }
        public long CompressedSize { get; set; }
        public double CompressionRatio => OriginalSize > 0 ? (1.0 - (double)CompressedSize / OriginalSize) * 100 : 0;
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        public int FinalWidth { get; set; }
        public int FinalHeight { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    /// <summary>
    /// Parallel image compression and resizing task.
    /// Handles JPEG, PNG, and other image formats with configurable quality and dimensions.
    /// 
    /// Features:
    /// - Configurable compression quality
    /// - Automatic resizing with aspect ratio preservation
    /// - Memory-efficient streaming
    /// - Progress tracking
    /// - Format-specific optimization
    /// </summary>
    public class ImageCompressionTask : WorkTask<ImageCompressionResult>
    {
        private readonly string _inputPath;
        private readonly string _outputPath;
        private readonly ImageCompressionOptions _options;

        public ImageCompressionTask(string inputPath, string outputPath, ImageCompressionOptions options = null)
        {
            _inputPath = inputPath;
            _outputPath = outputPath;
            _options = options ?? new ImageCompressionOptions();
            TaskName = $"Compress Image: {Path.GetFileName(inputPath)}";
            TotalWorkUnits = 100;
        }

        public override string GetTaskType() => "ImageCompression";

        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                if (!File.Exists(_inputPath))
                    throw new FileNotFoundException($"Input image not found: {_inputPath}");

                // Get original file info
                var originalFileInfo = new FileInfo(_inputPath);
                long originalSize = originalFileInfo.Length;

                UpdateProgress(10, 100);

                // Load and process image
                await Task.Run(() =>
                {
                    using (var originalImage = Image.FromFile(_inputPath))
                    {
                        UpdateProgress(30, 100);

                        // Calculate new dimensions
                        var (newWidth, newHeight) = CalculateNewDimensions(
                            originalImage.Width,
                            originalImage.Height);

                        UpdateProgress(40, 100);

                        // Resize if needed
                        Bitmap processedImage = originalImage as Bitmap;
                        if (newWidth != originalImage.Width || newHeight != originalImage.Height)
                        {
                            processedImage = ResizeImage(originalImage as Bitmap, newWidth, newHeight);
                        }

                        UpdateProgress(60, 100);

                        // Remove metadata if requested
                        if (_options.RemoveMetadata)
                        {
                            RemoveMetadata(processedImage);
                        }

                        UpdateProgress(75, 100);

                        // Save with compression
                        SaveImage(processedImage, newWidth, newHeight);

                        UpdateProgress(95, 100);

                        if (processedImage != originalImage)
                            processedImage?.Dispose();
                    }
                }, cancellationToken).ConfigureAwait(false);

                // Get compressed file info
                var compressedFileInfo = new FileInfo(_outputPath);
                long compressedSize = compressedFileInfo.Length;

                // Create result
                Result = new ImageCompressionResult
                {
                    InputPath = _inputPath,
                    OutputPath = _outputPath,
                    OriginalSize = originalSize,
                    CompressedSize = compressedSize,
                    ProcessingTime = DateTime.UtcNow - startTime
                };

                using (var img = Image.FromFile(_inputPath))
                {
                    Result.OriginalWidth = img.Width;
                    Result.OriginalHeight = img.Height;
                }

                using (var img = Image.FromFile(_outputPath))
                {
                    Result.FinalWidth = img.Width;
                    Result.FinalHeight = img.Height;
                }

                UpdateProgress(100, 100);
                MarkCompleted();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkFailed($"Image compression failed: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Calculate new dimensions based on options
        /// </summary>
        private (int width, int height) CalculateNewDimensions(int originalWidth, int originalHeight)
        {
            if (_options.MaxWidth == 0 && _options.MaxHeight == 0)
                return (originalWidth, originalHeight);

            int newWidth = originalWidth;
            int newHeight = originalHeight;

            if (_options.MaxWidth > 0 && originalWidth > _options.MaxWidth)
            {
                newWidth = _options.MaxWidth;
                if (_options.PreserveAspectRatio)
                    newHeight = (int)((double)originalHeight * newWidth / originalWidth);
            }

            if (_options.MaxHeight > 0 && newHeight > _options.MaxHeight)
            {
                newHeight = _options.MaxHeight;
                if (_options.PreserveAspectRatio)
                    newWidth = (int)((double)originalWidth * newHeight / originalHeight);
            }

            return (newWidth, newHeight);
        }

        /// <summary>
        /// Resize image with high-quality resampling
        /// </summary>
        private Bitmap ResizeImage(Bitmap original, int newWidth, int newHeight)
        {
            var resized = new Bitmap(newWidth, newHeight);

            using (var graphics = Graphics.FromImage(resized))
            {
                if (_options.HighQualityResampling)
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                }

                graphics.DrawImage(original, 0, 0, newWidth, newHeight);
            }

            return resized;
        }

        /// <summary>
        /// Remove metadata from image
        /// </summary>
        private void RemoveMetadata(Bitmap image)
        {
            try
            {
                var propertyIds = image.PropertyIdList;
                foreach (var propId in propertyIds)
                {
                    image.RemovePropertyItem(propId);
                }
            }
            catch
            {
                // Some formats may not support metadata removal
            }
        }

        /// <summary>
        /// Save image with appropriate compression
        /// </summary>
        private void SaveImage(Bitmap image, int width, int height)
        {
            string extension = Path.GetExtension(_outputPath).ToLower();

            // Ensure output directory exists
            string outputDir = Path.GetDirectoryName(_outputPath);
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            switch (extension)
            {
                case ".jpg":
                case ".jpeg":
                    SaveAsJpeg(image);
                    break;

                case ".png":
                    SaveAsPng(image);
                    break;

                case ".bmp":
                    image.Save(_outputPath, ImageFormat.Bmp);
                    break;

                case ".gif":
                    image.Save(_outputPath, ImageFormat.Gif);
                    break;

                default:
                    image.Save(_outputPath);
                    break;
            }
        }

        /// <summary>
        /// Save as JPEG with quality settings
        /// </summary>
        private void SaveAsJpeg(Bitmap image)
        {
            var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_options.JpegQuality);

            image.Save(_outputPath, jpegEncoder, encoderParams);
            encoderParams.Dispose();
        }

        /// <summary>
        /// Save as PNG with compression settings
        /// </summary>
        private void SaveAsPng(Bitmap image)
        {
            var pngEncoder = GetEncoder(ImageFormat.Png);
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Compression, (long)_options.PngCompressionLevel);

            image.Save(_outputPath, pngEncoder, encoderParams);
            encoderParams.Dispose();
        }

        /// <summary>
        /// Get image encoder for format
        /// </summary>
        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                    return codec;
            }
            return null;
        }
    }
}
