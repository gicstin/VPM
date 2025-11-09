using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace VPM.Services
{
    /// <summary>
    /// Handles texture conversion and resizing using System.Drawing
    /// </summary>
    public class TextureConverter
    {
        // Cache JPEG encoder info to avoid repeated lookups
        private static readonly Lazy<ImageCodecInfo> _jpegEncoder = new Lazy<ImageCodecInfo>(() =>
        {
            var encoders = ImageCodecInfo.GetImageEncoders();
            return Array.Find(encoders, e => e.MimeType == "image/jpeg");
        });
        /// <summary>
        /// Resizes an image to the target resolution (only downscaling)
        /// </summary>
        /// <param name="sourceData">Source image bytes</param>
        /// <param name="targetMaxDimension">Target maximum dimension (e.g., 4096 for 4K)</param>
        /// <param name="originalExtension">Original file extension (.jpg, .png, etc.)</param>
        /// <returns>Resized image bytes, or null if no conversion needed</returns>
        public byte[] ResizeImage(byte[] sourceData, int targetMaxDimension, string originalExtension)
        {
            try
            {
                using (var ms = new MemoryStream(sourceData))
                using (var originalImage = Image.FromStream(ms))
                {
                    int originalWidth = originalImage.Width;
                    int originalHeight = originalImage.Height;
                    int maxDimension = Math.Max(originalWidth, originalHeight);

                    // CRITICAL: Only downscale, never upscale OR same-resolution
                    // If the texture is already at or below target resolution, DON'T TOUCH IT
                    if (maxDimension <= targetMaxDimension)
                    {
                        System.Diagnostics.Debug.WriteLine($"Skipping texture conversion - already at or below target resolution ({maxDimension}px <= {targetMaxDimension}px)");
                        return null; // No conversion needed
                    }

                    // Calculate new dimensions maintaining aspect ratio
                    double scale = (double)targetMaxDimension / maxDimension;
                    int newWidth = (int)(originalWidth * scale);
                    int newHeight = (int)(originalHeight * scale);

                    // Create resized image
                    using (var resizedImage = new Bitmap(newWidth, newHeight))
                    {
                        using (var graphics = Graphics.FromImage(resizedImage))
                        {
                            // Balanced quality/performance settings
                            graphics.InterpolationMode = InterpolationMode.Bilinear;
                            graphics.SmoothingMode = SmoothingMode.None;
                            graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                            graphics.CompositingQuality = CompositingQuality.HighSpeed;
                            graphics.CompositingMode = CompositingMode.SourceCopy;
                            
                            graphics.DrawImage(originalImage, 0, 0, newWidth, newHeight);
                        }

                        // Save to memory stream with appropriate format
                        using (var outputMs = new MemoryStream())
                        {
                            ImageFormat format = GetImageFormat(originalExtension);
                            
                            if (format.Equals(ImageFormat.Jpeg))
                            {
                                // Use quality 90 for optimal balance
                                var encoderParameters = new EncoderParameters(1);
                                encoderParameters.Param[0] = new EncoderParameter(
                                    System.Drawing.Imaging.Encoder.Quality, 90L);
                                
                                var jpegCodec = _jpegEncoder.Value;
                                resizedImage.Save(outputMs, jpegCodec, encoderParameters);
                            }
                            else
                            {
                                // For PNG and other formats, use default encoding
                                // Note: System.Drawing doesn't support proper PNG compression
                                resizedImage.Save(outputMs, format);
                            }

                            byte[] convertedData = outputMs.ToArray();
                            
                            // CRITICAL SAFETY CHECK: Never return a texture larger than the original
                            // This prevents "optimization" from making packages worse
                            if (convertedData.Length >= sourceData.Length)
                            {
                                System.Diagnostics.Debug.WriteLine($"Texture conversion skipped - output ({convertedData.Length} bytes) >= input ({sourceData.Length} bytes)");
                                return null; // Keep original - it's better
                            }
                            
                            return convertedData;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error resizing image: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets the ImageFormat for a file extension
        /// </summary>
        private ImageFormat GetImageFormat(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".jpg" => ImageFormat.Jpeg,
                ".jpeg" => ImageFormat.Jpeg,
                ".png" => ImageFormat.Png,
                ".bmp" => ImageFormat.Bmp,
                ".gif" => ImageFormat.Gif,
                ".tiff" => ImageFormat.Tiff,
                ".tif" => ImageFormat.Tiff,
                _ => ImageFormat.Png // Default to PNG for unknown formats
            };
        }


        /// <summary>
        /// Gets the target dimension for a resolution string
        /// </summary>
        public static int GetTargetDimension(string resolution)
        {
            return resolution switch
            {
                "8K" => 7680,
                "4K" => 4096,
                "2K" => 2048,
                "1K" => 1024,
                _ => 2048 // Default to 2K
            };
        }
    }
}

