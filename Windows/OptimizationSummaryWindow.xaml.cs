using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace VPM
{
    public partial class OptimizationSummaryWindow : Window
    {
        private string _fullReportContent;
        private string _backupFolderPath;
        private static string _appVersion = null;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public OptimizationSummaryWindow()
        {
            InitializeComponent();
            this.Loaded += (s, e) => ApplyDarkTitleBar();
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
            catch
            {
                // Ignore if dark mode not supported
            }
        }

        public void SetSummaryData(
            int packagesOptimized,
            int errorCount,
            long spaceSaved,
            double percentSaved,
            bool sizeIncreased,
            long totalOriginalSize,
            long totalNewSize,
            List<string> errors,
            Dictionary<string, OptimizationDetails> packageDetails,
            string backupFolderPath = null,
            TimeSpan? elapsedTime = null,
            int packagesSkipped = 0,
            int totalPackagesSelected = 0,
            List<string> detailedErrors = null)
        {
            // Set title
            string titleText = errorCount > 0 
                ? "✓ Optimization Complete (With Errors)" 
                : "✓ Optimization Complete!";
            TitleBlock.Text = titleText;

            // Build summary
            var summaryBuilder = new StringBuilder();
            summaryBuilder.AppendLine($"Packages Optimized: {packagesOptimized}");
            if (packagesSkipped > 0)
            {
                summaryBuilder.AppendLine($"Packages Skipped: {packagesSkipped} (no changes needed)");
            }
            if (errorCount > 0)
            {
                summaryBuilder.AppendLine($"Errors: {errorCount}");
            }
            if (elapsedTime.HasValue)
            {
                summaryBuilder.AppendLine($"Time Taken: {FormatTimeSpan(elapsedTime.Value)}");
            }

            string spaceMessage = sizeIncreased
                ? $"Size Increased: {FormatBytes(Math.Abs(spaceSaved))} (+{Math.Abs(percentSaved):F1}%)"
                : $"Space Saved: {FormatBytes(spaceSaved)} ({percentSaved:F1}%)";

            summaryBuilder.AppendLine(spaceMessage);
            summaryBuilder.AppendLine($"Original Size: {FormatBytes(totalOriginalSize)}");
            summaryBuilder.AppendLine($"New Size: {FormatBytes(totalNewSize)}");

            if (errorCount > 0)
            {
                summaryBuilder.AppendLine();
                summaryBuilder.AppendLine("Errors:");
                foreach (var error in errors.Take(5))
                {
                    summaryBuilder.AppendLine($"  • {error}");
                }
                if (errors.Count > 5)
                {
                    summaryBuilder.AppendLine($"  ... and {errors.Count - 5} more");
                }
            }

            summaryBuilder.AppendLine();
            if (!string.IsNullOrEmpty(backupFolderPath))
            {
                summaryBuilder.AppendLine($"Original packages backed up to:\n{backupFolderPath}");
            }
            else
            {
                summaryBuilder.AppendLine("Original packages backed up to ArchivedPackages folder.");
            }

            SummaryBlock.Text = summaryBuilder.ToString();

            // Store backup folder path
            _backupFolderPath = backupFolderPath;

            // Build full report
            _fullReportContent = BuildFullReport(packagesOptimized, errorCount, spaceSaved, percentSaved, 
                                                 sizeIncreased, totalOriginalSize, totalNewSize, errors, packageDetails,
                                                 elapsedTime, packagesSkipped, totalPackagesSelected, detailedErrors);

            // Set up button handlers
            OkButton.Click += (s, e) => this.Close();
            FullReportButton.Click += (s, e) => 
            {
                ShowFullReport();
                this.Close();
            };
            OpenBackupButton.Click += (s, e) => OpenBackupFolder();
            
            // Hide backup button if no backup folder path provided
            if (string.IsNullOrEmpty(_backupFolderPath))
            {
                OpenBackupButton.Visibility = Visibility.Collapsed;
            }
        }

        private string BuildFullReport(
            int packagesOptimized,
            int errorCount,
            long spaceSaved,
            double percentSaved,
            bool sizeIncreased,
            long totalOriginalSize,
            long totalNewSize,
            List<string> errors,
            Dictionary<string, OptimizationDetails> packageDetails,
            TimeSpan? elapsedTime = null,
            int packagesSkipped = 0,
            int totalPackagesSelected = 0,
            List<string> detailedErrors = null)
        {
            var report = new StringBuilder();
            string appVersion = GetAppVersion();
            string timestamp = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            
            // Calculate metrics
            int successCount = packagesOptimized - errorCount;
            double successRate = packagesOptimized > 0 ? (successCount * 100.0 / packagesOptimized) : 0;
            string duration = elapsedTime.HasValue ? FormatTimeSpan(elapsedTime.Value) : "N/A";
            double throughputMBps = elapsedTime.HasValue && elapsedTime.Value.TotalSeconds > 0 
                ? (totalOriginalSize / 1024.0 / 1024.0) / elapsedTime.Value.TotalSeconds : 0;
            double compressionRatio = totalOriginalSize > 0 ? (double)totalNewSize / totalOriginalSize : 1;
            double efficiencyScore = CalculateEfficiencyScore(percentSaved, successRate, packagesOptimized, elapsedTime);
            string efficiencyGrade = GetEfficiencyGrade(efficiencyScore);
            
            // Header
            report.AppendLine("════════════════════════════════════════════════════════════════════════════════════════════════════════");
            report.AppendLine($"  VPM OPTIMIZATION REPORT v{appVersion}");
            report.AppendLine($"  Generated: {timestamp}");
            report.AppendLine("════════════════════════════════════════════════════════════════════════════════════════════════════════");
            report.AppendLine();

            // Executive Summary
            report.AppendLine("── EXECUTIVE SUMMARY ───────────────────────────────────────────────────────────────────────────────────");
            report.AppendLine();
            string savedDisplay = sizeIncreased ? $"+{FormatBytes(Math.Abs(spaceSaved))}" : $"-{FormatBytes(spaceSaved)}";
            string savedPercent = sizeIncreased ? $"+{Math.Abs(percentSaved):F1}%" : $"-{Math.Abs(percentSaved):F1}%";
            report.AppendLine($"  SPACE {(sizeIncreased ? "INCREASED" : "RECOVERED")}:  {savedDisplay}  ({savedPercent})");
            report.AppendLine();
            string compressionBar = BuildProgressBar(100 - Math.Abs(percentSaved), 50);
            report.AppendLine($"  Compression: [{compressionBar}] {100 - Math.Abs(percentSaved):F1}%");
            report.AppendLine();
            report.AppendLine($"  Efficiency: {efficiencyGrade} ({efficiencyScore:F0}%)    Duration: {duration}    Throughput: {throughputMBps:F1} MB/s    Success: {successRate:F0}% ({successCount}/{packagesOptimized})");
            report.AppendLine();

            // Storage Analysis
            report.AppendLine("── STORAGE ANALYSIS ────────────────────────────────────────────────────────────────────────────────────");
            report.AppendLine();
            int beforeBarLen = 50;
            int afterBarLen = totalOriginalSize > 0 ? (int)(50.0 * totalNewSize / totalOriginalSize) : 50;
            // Clamp afterBarLen to ensure it doesn't exceed beforeBarLen
            afterBarLen = Math.Min(afterBarLen, beforeBarLen);
            afterBarLen = Math.Max(afterBarLen, 0);
            string beforeBar = new string('█', beforeBarLen);
            string afterBar = new string('█', afterBarLen) + new string('░', beforeBarLen - afterBarLen);
            report.AppendLine($"  BEFORE:  {FormatBytes(totalOriginalSize),-12}  [{beforeBar}]");
            report.AppendLine($"  AFTER:   {FormatBytes(totalNewSize),-12}  [{afterBar}]");
            report.AppendLine();
            report.AppendLine($"  Original Size:     {FormatBytes(totalOriginalSize),-16}  (100% baseline)");
            report.AppendLine($"  Optimized Size:    {FormatBytes(totalNewSize),-16}  ({compressionRatio:P1} of original)");
            report.AppendLine($"  Space Freed:       {FormatBytes(Math.Abs(spaceSaved)),-16}  ({(sizeIncreased ? "INCREASED" : "SAVED")})");
            report.AppendLine($"  Compression Ratio: {(1/compressionRatio):F2}:1              ({(compressionRatio < 0.5 ? "Excellent" : compressionRatio < 0.7 ? "Good" : "Moderate")} compression)");
            if (!string.IsNullOrEmpty(_backupFolderPath))
            {
                report.AppendLine($"  Backup Location:   {_backupFolderPath}");
            }
            report.AppendLine();

            // Optimization Breakdown
            var totalTextures = packageDetails?.Sum(p => p.Value.TextureCount) ?? 0;
            var totalHair = packageDetails?.Sum(p => p.Value.HairCount) ?? 0;
            var totalMirrors = packageDetails?.Sum(p => p.Value.MirrorCount) ?? 0;
            var totalLights = packageDetails?.Sum(p => p.Value.LightCount) ?? 0;
            var totalDisabledDeps = packageDetails?.Sum(p => p.Value.DisabledDependencies) ?? 0;
            var totalLatestDeps = packageDetails?.Sum(p => p.Value.LatestDependencies) ?? 0;
            var totalJsonMinified = packageDetails?.Count(p => p.Value.JsonMinified) ?? 0;
            var totalJsonSizeBefore = packageDetails?.Sum(p => p.Value.JsonSizeBeforeMinify) ?? 0;
            var totalJsonSizeAfter = packageDetails?.Sum(p => p.Value.JsonSizeAfterMinify) ?? 0;
            var totalJsonSaved = totalJsonSizeBefore - totalJsonSizeAfter;
            int totalOperations = totalTextures + totalHair + totalMirrors + totalLights + totalDisabledDeps + totalLatestDeps + totalJsonMinified;
            
            report.AppendLine("── OPTIMIZATION BREAKDOWN ──────────────────────────────────────────────────────────────────────────────");
            report.AppendLine();
            report.AppendLine($"  Total Operations: {totalOperations}");
            report.AppendLine();
            if (totalTextures > 0)
            {
                string texBar = BuildCategoryBar(totalTextures, Math.Max(totalTextures, Math.Max(totalHair, totalLights)), 30);
                report.AppendLine($"  ▸ TEXTURES      {totalTextures,4}  {texBar}  Primary size reducer");
            }
            if (totalHair > 0)
            {
                string hairBar = BuildCategoryBar(totalHair, Math.Max(totalTextures, Math.Max(totalHair, totalLights)), 30);
                report.AppendLine($"  ▸ HAIR          {totalHair,4}  {hairBar}  Performance boost");
            }
            if (totalLights > 0)
            {
                string lightBar = BuildCategoryBar(totalLights, Math.Max(totalTextures, Math.Max(totalHair, totalLights)), 30);
                report.AppendLine($"  ▸ SHADOWS       {totalLights,4}  {lightBar}  GPU memory saver");
            }
            if (totalMirrors > 0)
                report.AppendLine($"  ▸ MIRRORS       {totalMirrors,4}  [DISABLED]                       Major FPS impact");
            if (totalDisabledDeps > 0)
                report.AppendLine($"  ▸ DEPS REMOVED  {totalDisabledDeps,4}                                   Cleaner dependencies");
            if (totalLatestDeps > 0)
                report.AppendLine($"  ▸ DEPS UPDATED  {totalLatestDeps,4}                                   Future-proofed");
            if (totalJsonMinified > 0)
            {
                double jsonPercent = totalJsonSizeBefore > 0 ? (100.0 * totalJsonSaved / totalJsonSizeBefore) : 0;
                report.AppendLine($"  ▸ JSON          {totalJsonMinified,4}  Saved {FormatBytes(totalJsonSaved)} (-{jsonPercent:F1}%)         Faster loading");
            }
            report.AppendLine();

            // Top Optimizations Leaderboard
            if (packageDetails != null && packageDetails.Count > 0)
            {
                var packagesWithSavings = packageDetails
                    .Where(p => string.IsNullOrEmpty(p.Value.Error) && p.Value.OriginalSize > 0 && p.Value.OriginalSize > p.Value.NewSize)
                    .Select(p => new {
                        Name = p.Key,
                        Saved = p.Value.OriginalSize - p.Value.NewSize,
                        Percent = (p.Value.OriginalSize - p.Value.NewSize) * 100.0 / p.Value.OriginalSize,
                        Original = p.Value.OriginalSize,
                        New = p.Value.NewSize
                    })
                    .OrderByDescending(p => p.Saved)
                    .Take(5)
                    .ToList();

                if (packagesWithSavings.Any())
                {
                    report.AppendLine("── TOP OPTIMIZATIONS ───────────────────────────────────────────────────────────────────────────────────");
                    report.AppendLine();
                    report.AppendLine("  RANK  PACKAGE                                            SAVED           REDUCTION");
                    report.AppendLine("  ────  ───────                                            ─────           ─────────");
                    string[] medals = { " 1.", " 2.", " 3.", " 4.", " 5." };
                    for (int i = 0; i < packagesWithSavings.Count; i++)
                    {
                        var pkg = packagesWithSavings[i];
                        string shortName = pkg.Name.Length > 45 ? pkg.Name.Substring(0, 42) + "..." : pkg.Name;
                        string miniBar = BuildProgressBar(pkg.Percent, 10);
                        report.AppendLine($"  {medals[i]}   {shortName,-45}  {FormatBytes(pkg.Saved),-12}  {miniBar} {pkg.Percent:F1}%");
                    }
                    report.AppendLine();
                }
            }

            // Detailed Package Results
            if (packageDetails != null && packageDetails.Count > 0)
            {
                report.AppendLine("════════════════════════════════════════════════════════════════════════════════════════════════════════");
                report.AppendLine("  DETAILED PACKAGE RESULTS");
                report.AppendLine("════════════════════════════════════════════════════════════════════════════════════════════════════════");
                report.AppendLine();

                int packageNum = 1;
                foreach (var kvp in packageDetails.OrderBy(x => x.Key))
                {
                    string packageName = kvp.Key;
                    var details = kvp.Value;
                    
                    bool hasError = !string.IsNullOrEmpty(details.Error);
                    long pkgSaved = details.OriginalSize - details.NewSize;
                    double pkgPercent = details.OriginalSize > 0 ? (100.0 * pkgSaved / details.OriginalSize) : 0;
                    
                    string statusIcon = hasError ? "✗" : (pkgSaved > 0 ? "✓" : "○");
                    string statusText = hasError ? "ERROR" : (pkgSaved > 0 ? "OPTIMIZED" : "NO CHANGE");
                    
                    report.AppendLine($"── [{packageNum:D2}] {packageName} ──");
                    report.AppendLine($"  Status: {statusIcon} {statusText}");
                    
                    if (details.OriginalSize > 0)
                    {
                        int pkgBarLen = 30;
                        int pkgNewBarLen = details.OriginalSize > 0 ? (int)(30.0 * details.NewSize / details.OriginalSize) : 30;
                        // Clamp pkgNewBarLen to ensure it doesn't exceed pkgBarLen
                        pkgNewBarLen = Math.Min(pkgNewBarLen, pkgBarLen);
                        pkgNewBarLen = Math.Max(pkgNewBarLen, 0);
                        string pkgBar = new string('█', pkgNewBarLen) + new string('░', pkgBarLen - pkgNewBarLen);
                        report.AppendLine($"  Size:   {FormatBytes(details.OriginalSize),-10} -> {FormatBytes(details.NewSize),-10}  [{pkgBar}]");
                        string savingsText = pkgSaved > 0 ? $"-{FormatBytes(pkgSaved)} (-{pkgPercent:F1}%)" : 
                                           pkgSaved < 0 ? $"+{FormatBytes(Math.Abs(pkgSaved))} (+{Math.Abs(pkgPercent):F1}%)" : "No change";
                        report.AppendLine($"  Delta:  {savingsText}");
                    }

                    if (hasError)
                    {
                        report.AppendLine($"  ERROR: {details.Error}");
                    }

                    // Texture details with compression info
                    if (details.TextureCount > 0)
                    {
                        report.AppendLine();
                        report.AppendLine($"  Textures Optimized: {details.TextureCount}");
                        report.AppendLine("  Format: PNG (lossless) | Compression: Maximum | Filter: Adaptive");
                        var textureDetailsToShow = details.TextureDetailsWithSizes.Count > 0 
                            ? details.TextureDetailsWithSizes : details.TextureDetails;
                        foreach (var textureDetail in textureDetailsToShow)
                        {
                            string cleanDetail = textureDetail.TrimStart();
                            if (cleanDetail.StartsWith("•")) cleanDetail = cleanDetail.Substring(1).Trim();
                            report.AppendLine($"    - {cleanDetail}");
                        }
                    }

                    if (details.HairCount > 0)
                    {
                        report.AppendLine();
                        report.AppendLine($"  Hair Settings Modified: {details.HairCount}");
                        foreach (var hairDetail in details.HairDetails)
                            report.AppendLine($"    - {hairDetail}");
                    }

                    if (details.MirrorCount > 0)
                    {
                        report.AppendLine();
                        report.AppendLine($"  Mirrors Disabled: {details.MirrorCount} (major performance improvement)");
                    }

                    if (details.LightCount > 0)
                    {
                        report.AppendLine();
                        report.AppendLine($"  Shadow Resolution Reduced: {details.LightCount}");
                        foreach (var lightDetail in details.LightDetails)
                            report.AppendLine($"    - {lightDetail}");
                    }

                    if (details.DisabledDependencies > 0)
                    {
                        report.AppendLine();
                        report.AppendLine($"  Dependencies Removed: {details.DisabledDependencies}");
                        foreach (var depDetail in details.DisabledDependencyDetails)
                            report.AppendLine($"    - {depDetail}");
                    }

                    if (details.LatestDependencies > 0)
                    {
                        report.AppendLine();
                        report.AppendLine($"  Dependencies Updated to .latest: {details.LatestDependencies}");
                        foreach (var depDetail in details.LatestDependencyDetails)
                            report.AppendLine($"    - {depDetail}");
                    }

                    if (details.JsonMinified)
                    {
                        long jsonSaved = details.JsonSizeBeforeMinify - details.JsonSizeAfterMinify;
                        double jsonPercent = details.JsonSizeBeforeMinify > 0 ? (100.0 * jsonSaved / details.JsonSizeBeforeMinify) : 0;
                        report.AppendLine();
                        report.AppendLine($"  JSON Minified: {FormatBytes(details.JsonSizeBeforeMinify)} -> {FormatBytes(details.JsonSizeAfterMinify)} (-{jsonPercent:F1}%)");
                    }

                    report.AppendLine();
                    packageNum++;
                }
            }

            // Error Summary
            if (errorCount > 0)
            {
                report.AppendLine("── ERROR SUMMARY ───────────────────────────────────────────────────────────────────────────────────────");
                report.AppendLine();
                int errorNum = 1;
                foreach (var error in errors)
                {
                    report.AppendLine($"  {errorNum:D2}. {error}");
                    errorNum++;
                }
                report.AppendLine();
            }

            // Performance Metrics
            if (elapsedTime.HasValue && packageDetails != null && packageDetails.Count > 0)
            {
                double avgTimePerPkg = packagesOptimized > 0 ? elapsedTime.Value.TotalSeconds / packagesOptimized : 0;
                double avgSavedPerPkg = packagesOptimized > 0 ? (double)spaceSaved / packagesOptimized : 0;
                
                report.AppendLine("── PERFORMANCE METRICS ─────────────────────────────────────────────────────────────────────────────────");
                report.AppendLine();
                report.AppendLine($"  Processing Speed:  {throughputMBps:F2} MB/s");
                report.AppendLine($"  Avg Time/Package:  {FormatSeconds(avgTimePerPkg)}");
                report.AppendLine($"  Avg Saved/Package: {FormatBytes((long)Math.Abs(avgSavedPerPkg))}");
                
                if (elapsedTime.Value.TotalHours > 0 && packagesOptimized > 0)
                {
                    double pkgsPerHour = packagesOptimized / elapsedTime.Value.TotalHours;
                    report.AppendLine($"  Packages/Hour:     {pkgsPerHour:F1}");
                }
                report.AppendLine();
                
                // Find extremes
                var sortedBySize = packageDetails.Where(p => string.IsNullOrEmpty(p.Value.Error) && p.Value.OriginalSize > 0).OrderBy(p => p.Value.OriginalSize);
                if (sortedBySize.Any())
                {
                    var smallest = sortedBySize.First();
                    var largest = sortedBySize.Last();
                    var bestOpt = packageDetails
                        .Where(p => string.IsNullOrEmpty(p.Value.Error) && p.Value.OriginalSize > 0 && p.Value.OriginalSize > p.Value.NewSize)
                        .OrderByDescending(p => (p.Value.OriginalSize - p.Value.NewSize) * 100.0 / p.Value.OriginalSize)
                        .FirstOrDefault();
                    
                    report.AppendLine("  RECORDS:");
                    report.AppendLine($"    Smallest Package:  {smallest.Key} ({FormatBytes(smallest.Value.OriginalSize)})");
                    report.AppendLine($"    Largest Package:   {largest.Key} ({FormatBytes(largest.Value.OriginalSize)})");
                    if (bestOpt.Key != null)
                    {
                        long saved = bestOpt.Value.OriginalSize - bestOpt.Value.NewSize;
                        double pct = (saved * 100.0) / bestOpt.Value.OriginalSize;
                        report.AppendLine($"    Best Optimization: {bestOpt.Key} (-{FormatBytes(saved)}, -{pct:F1}%)");
                    }
                    report.AppendLine();
                }
            }

            // Detailed Error Log
            if (detailedErrors != null && detailedErrors.Count > 0)
            {
                report.AppendLine("── DETAILED ERROR LOG ──────────────────────────────────────────────────────────────────────────────────");
                report.AppendLine();
                foreach (var error in detailedErrors)
                {
                    report.AppendLine(error);
                    report.AppendLine("────────────────────────────────────────────────────────────────────────────────────────────────────");
                }
                report.AppendLine();
            }

            // Footer
            report.AppendLine("════════════════════════════════════════════════════════════════════════════════════════════════════════");
            report.AppendLine($"  END OF REPORT - VPM v{appVersion} - Generated in {duration}");
            report.AppendLine("════════════════════════════════════════════════════════════════════════════════════════════════════════");
            report.AppendLine();

            return report.ToString();
        }

        // Helper method to build progress bar
        private static string BuildProgressBar(double percent, int width)
        {
            int filled = (int)(percent / 100.0 * width);
            filled = Math.Max(0, Math.Min(width, filled));
            return new string('█', filled) + new string('░', width - filled);
        }

        // Helper method to build category bar
        private static string BuildCategoryBar(int value, int max, int width)
        {
            if (max <= 0) return new string('░', width);
            int filled = (int)((double)value / max * width);
            filled = Math.Max(1, Math.Min(width, filled));
            return new string('▓', filled) + new string('░', width - filled);
        }

        // Calculate efficiency score based on multiple factors
        private static double CalculateEfficiencyScore(double percentSaved, double successRate, int packagesOptimized, TimeSpan? elapsedTime)
        {
            double score = 0;
            
            // Space saved contributes up to 50 points
            score += Math.Min(50, percentSaved * 0.7);
            
            // Success rate contributes up to 30 points
            score += successRate * 0.3;
            
            // Speed bonus: up to 20 points for fast processing
            if (elapsedTime.HasValue && packagesOptimized > 0)
            {
                double avgSecondsPerPkg = elapsedTime.Value.TotalSeconds / packagesOptimized;
                // 20 points if < 1s per package, scaling down to 0 points at 10s per package
                double speedScore = Math.Max(0, 20 - (avgSecondsPerPkg - 1) * 2.2);
                score += speedScore;
            }
            else
            {
                score += 10; // Default if no timing
            }
            
            return Math.Min(100, Math.Max(0, score));
        }

        // Get grade based on efficiency score
        private static string GetEfficiencyGrade(double score)
        {
            if (score >= 90) return "S+";
            if (score >= 80) return "A";
            if (score >= 70) return "B";
            if (score >= 60) return "C";
            if (score >= 50) return "D";
            return "F";
        }

        private void ShowFullReport()
        {
            var reportWindow = new Window
            {
                Title = "Full Optimization Report",
                Width = 900,
                Height = 700,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ShowInTaskbar = false
            };

            // Apply dark titlebar to report window
            reportWindow.Loaded += (s, e) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(reportWindow).Handle;
                    int darkMode = 1;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                }
                catch { }
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var scrollViewer = new ScrollViewer
            {
                Margin = new Thickness(20),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 37)),
                Padding = new Thickness(15),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var textBox = new TextBox
            {
                Text = _fullReportContent,
                FontSize = 13,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(176, 176, 176)),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 37)),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0)
            };
            
            // Apply dark theme to context menu
            var contextMenu = new System.Windows.Controls.ContextMenu();
            contextMenu.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 37));
            contextMenu.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(176, 176, 176));
            
            var cutItem = new MenuItem { Header = "Cut", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(176, 176, 176)) };
            cutItem.Click += (s, e) => textBox.Cut();
            contextMenu.Items.Add(cutItem);
            
            var copyItem = new MenuItem { Header = "Copy", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(176, 176, 176)) };
            copyItem.Click += (s, e) => textBox.Copy();
            contextMenu.Items.Add(copyItem);
            
            var pasteItem = new MenuItem { Header = "Paste", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(176, 176, 176)) };
            pasteItem.Click += (s, e) => textBox.Paste();
            contextMenu.Items.Add(pasteItem);
            
            var selectAllItem = new MenuItem { Header = "Select All", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(176, 176, 176)) };
            selectAllItem.Click += (s, e) => textBox.SelectAll();
            contextMenu.Items.Add(selectAllItem);
            
            textBox.ContextMenu = contextMenu;

            scrollViewer.Content = textBox;
            Grid.SetRow(scrollViewer, 0);
            grid.Children.Add(scrollViewer);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(20),
                Height = 40
            };

            var copyButton = new Button
            {
                Content = "Copy to Clipboard",
                Width = 150,
                Height = 40,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 99, 156)),
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 0)
            };

            copyButton.Click += (s, e) =>
            {
                Clipboard.SetText(_fullReportContent);
                copyButton.Content = "Copied!";
                copyButton.IsEnabled = false;
            };

            var closeButton = new Button
            {
                Content = "Close",
                Width = 100,
                Height = 40,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45)),
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            closeButton.Click += (s, e) => reportWindow.Close();

            buttonPanel.Children.Add(copyButton);
            buttonPanel.Children.Add(closeButton);

            Grid.SetRow(buttonPanel, 1);
            grid.Children.Add(buttonPanel);

            reportWindow.Content = grid;
            reportWindow.ShowDialog();
        }

        private void OpenBackupFolder()
        {
            try
            {
                if (string.IsNullOrEmpty(_backupFolderPath))
                {
                    MessageBox.Show("Backup folder path is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (System.IO.Directory.Exists(_backupFolderPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", _backupFolderPath);
                }
                else
                {
                    MessageBox.Show($"Backup folder does not exist:\n{_backupFolderPath}", "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening backup folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private static string FormatTimeSpan(TimeSpan time)
        {
            if (time.TotalSeconds < 1)
            {
                return $"{time.TotalMilliseconds:F0}ms";
            }
            else if (time.TotalMinutes < 1)
            {
                return $"{time.TotalSeconds:F1}s";
            }
            else if (time.TotalHours < 1)
            {
                return $"{time.Minutes}m {time.Seconds}s";
            }
            else
            {
                return $"{(int)time.TotalHours}h {time.Minutes}m {time.Seconds}s";
            }
        }

        private static string FormatSeconds(double seconds)
        {
            if (seconds < 1)
            {
                return $"{seconds * 1000:F0}ms";
            }
            else if (seconds < 60)
            {
                return $"{seconds:F1}s";
            }
            else
            {
                int minutes = (int)(seconds / 60);
                int secs = (int)(seconds % 60);
                return $"{minutes}m {secs}s";
            }
        }

        private static string GetAppVersion()
        {
            if (_appVersion != null)
                return _appVersion;

            try
            {
                // Try to read from version.txt file in the application directory
                string versionFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
                if (System.IO.File.Exists(versionFile))
                {
                    _appVersion = System.IO.File.ReadAllText(versionFile).Trim();
                    if (!string.IsNullOrEmpty(_appVersion))
                        return _appVersion;
                }

                // Fallback to assembly version
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                _appVersion = $"{version.Major}.{version.Minor}.{version.Build}";
                return _appVersion;
            }
            catch
            {
                _appVersion = "Unknown";
                return _appVersion;
            }
        }
    }

    /// <summary>
    /// Holds optimization details for a single package
    /// </summary>
    public class OptimizationDetails
    {
        public long OriginalSize { get; set; }
        public long NewSize { get; set; }
        public int TextureCount { get; set; }
        public int HairCount { get; set; }
        public int MirrorCount { get; set; }
        public int LightCount { get; set; }
        public int DisabledDependencies { get; set; }
        public int LatestDependencies { get; set; }
        public bool JsonMinified { get; set; } = false;
        public long JsonSizeBeforeMinify { get; set; } = 0;
        public long JsonSizeAfterMinify { get; set; } = 0;
        public string Error { get; set; }
        
        // Detailed change information
        public List<string> TextureDetails { get; set; } = new List<string>();
        public List<string> TextureDetailsWithSizes { get; set; } = new List<string>(); // Detailed size info from repackager
        public List<string> HairDetails { get; set; } = new List<string>();
        public List<string> LightDetails { get; set; } = new List<string>();
        public List<string> DisabledDependencyDetails { get; set; } = new List<string>();
        public List<string> LatestDependencyDetails { get; set; } = new List<string>();
    }
}

