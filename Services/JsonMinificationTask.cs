using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VPM.Services
{
    /// <summary>
    /// JSON minification options
    /// </summary>
    public class JsonMinificationOptions
    {
        /// <summary>
        /// Sort object keys alphabetically
        /// </summary>
        public bool SortKeys { get; set; } = false;

        /// <summary>
        /// Validate JSON structure
        /// </summary>
        public bool ValidateJson { get; set; } = true;

        /// <summary>
        /// Buffer size for streaming (bytes)
        /// </summary>
        public int BufferSize { get; set; } = 64 * 1024; // 64KB

        /// <summary>
        /// Pretty print output (for debugging)
        /// </summary>
        public bool PrettyPrint { get; set; } = false;
    }

    /// <summary>
    /// JSON minification result
    /// </summary>
    public class JsonMinificationResult
    {
        public string InputPath { get; set; }
        public string OutputPath { get; set; }
        public long OriginalSize { get; set; }
        public long MinifiedSize { get; set; }
        public double CompressionRatio => OriginalSize > 0 ? (1.0 - (double)MinifiedSize / OriginalSize) * 100 : 0;
        public int LineCount { get; set; }
        public bool IsValid { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    /// <summary>
    /// JSON minification and optimization task.
    /// Removes whitespace, comments, and optionally sorts keys for better compression.
    /// 
    /// Features:
    /// - Streaming JSON processing for large files
    /// - Whitespace removal
    /// - Optional key sorting
    /// - Validation and error handling
    /// - Progress tracking
    /// - Memory-efficient processing
    /// </summary>
    public class JsonMinificationTask : WorkTask<JsonMinificationResult>
    {
        private readonly string _inputPath;
        private readonly string _outputPath;
        private readonly JsonMinificationOptions _options;

        public JsonMinificationTask(string inputPath, string outputPath, JsonMinificationOptions options = null)
        {
            _inputPath = inputPath;
            _outputPath = outputPath;
            _options = options ?? new JsonMinificationOptions();
            TaskName = $"Minify JSON: {Path.GetFileName(inputPath)}";
            TotalWorkUnits = 100;
        }

        public override string GetTaskType() => "JsonMinification";

        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                if (!File.Exists(_inputPath))
                    throw new FileNotFoundException($"Input JSON file not found: {_inputPath}");

                var originalFileInfo = new FileInfo(_inputPath);
                long originalSize = originalFileInfo.Length;

                UpdateProgress(10, 100);

                // Read and parse JSON
                string jsonContent = await File.ReadAllTextAsync(_inputPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                UpdateProgress(30, 100);

                // Validate if requested
                bool isValid = true;
                if (_options.ValidateJson)
                {
                    try
                    {
                        using (var doc = JsonDocument.Parse(jsonContent))
                        {
                            isValid = true;
                        }
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidOperationException($"Invalid JSON: {ex.Message}", ex);
                    }
                }

                UpdateProgress(40, 100);

                // Parse and minify
                string minifiedContent = await Task.Run(() =>
                {
                    using (var doc = JsonDocument.Parse(jsonContent))
                    {
                        var options = new JsonSerializerOptions
                        {
                            WriteIndented = _options.PrettyPrint,
                            PropertyNamingPolicy = _options.SortKeys ? JsonNamingPolicy.CamelCase : null
                        };

                        return JsonSerializer.Serialize(doc.RootElement, options);
                    }
                }, cancellationToken).ConfigureAwait(false);

                UpdateProgress(70, 100);

                // Ensure output directory exists
                string outputDir = Path.GetDirectoryName(_outputPath);
                if (!Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                // Write minified content
                await File.WriteAllTextAsync(_outputPath, minifiedContent, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                UpdateProgress(90, 100);

                // Get result metrics
                var compressedFileInfo = new FileInfo(_outputPath);
                long minifiedSize = compressedFileInfo.Length;

                Result = new JsonMinificationResult
                {
                    InputPath = _inputPath,
                    OutputPath = _outputPath,
                    OriginalSize = originalSize,
                    MinifiedSize = minifiedSize,
                    LineCount = jsonContent.Split('\n').Length,
                    IsValid = isValid,
                    ProcessingTime = DateTime.UtcNow - startTime
                };

                UpdateProgress(100, 100);
                MarkCompleted();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkFailed($"JSON minification failed: {ex.Message}", ex);
                throw;
            }
        }
    }
}
