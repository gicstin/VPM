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
            int totalPackagesSelected = 0)
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
            summaryBuilder.AppendLine("Original packages backed up to ArchivedPackages folder.");

            SummaryBlock.Text = summaryBuilder.ToString();

            // Store backup folder path
            _backupFolderPath = backupFolderPath;

            // Build full report
            _fullReportContent = BuildFullReport(packagesOptimized, errorCount, spaceSaved, percentSaved, 
                                                 sizeIncreased, totalOriginalSize, totalNewSize, errors, packageDetails,
                                                 elapsedTime, packagesSkipped, totalPackagesSelected);

            // Set up button handlers
            OkButton.Click += (s, e) => this.Close();
            FullReportButton.Click += (s, e) => ShowFullReport();
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
            int totalPackagesSelected = 0)
        {
            var report = new StringBuilder();

            // Get app version
            string appVersion = GetAppVersion();
            
            report.AppendLine("╔═══════════════════════════════════════════════════════════════════════════════════════════════════╗");
            report.AppendLine($"║                         VPM OPTIMIZATION REPORT - v{appVersion,-38}║");
            report.AppendLine("╚═══════════════════════════════════════════════════════════════════════════════════════════════════╝");
            report.AppendLine();

            // Overall Summary
            report.AppendLine("┌─ OVERALL SUMMARY ─────────────────────────────────────────────────────────────────────────────────┐");
            report.AppendLine($"│ Timestamp:               {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            
            if (totalPackagesSelected > 0)
            {
                report.AppendLine($"│ Packages Selected:       {totalPackagesSelected}");
                report.AppendLine($"│ Packages Optimized:      {packagesOptimized}");
                if (packagesSkipped > 0)
                {
                    report.AppendLine($"│ Packages Skipped:        {packagesSkipped} (no changes needed)");
                }
            }
            else
            {
                report.AppendLine($"│ Packages Processed:      {packagesOptimized}");
            }
            
            report.AppendLine($"│ Errors Encountered:      {errorCount}");
            int successCount = packagesOptimized - errorCount;
            report.AppendLine($"│ Success Rate:            {(packagesOptimized > 0 ? (successCount * 100.0 / packagesOptimized).ToString("F1") : "0")}% ({successCount}/{packagesOptimized})");
            
            if (elapsedTime.HasValue)
            {
                report.AppendLine("│");
                report.AppendLine($"│ Total Time Elapsed:      {FormatTimeSpan(elapsedTime.Value)}");
                if (packagesOptimized > 0)
                {
                    double avgSeconds = elapsedTime.Value.TotalSeconds / packagesOptimized;
                    report.AppendLine($"│ Average Time/Package:    {FormatSeconds(avgSeconds)}");
                    
                    // Calculate throughput
                    if (elapsedTime.Value.TotalHours > 0)
                    {
                        double packagesPerHour = packagesOptimized / elapsedTime.Value.TotalHours;
                        report.AppendLine($"│ Processing Rate:         {packagesPerHour:F1} packages/hour");
                    }
                }
            }
            
            report.AppendLine("│");

            string spaceMessage = sizeIncreased
                ? $"Size Increased: {FormatBytes(Math.Abs(spaceSaved))} (+{Math.Abs(percentSaved):F1}%)"
                : $"Space Saved: {FormatBytes(spaceSaved)} ({percentSaved:F1}%)";

            report.AppendLine($"│ {spaceMessage}");
            report.AppendLine($"│ Total Original Size:     {FormatBytes(totalOriginalSize)} ({totalOriginalSize:N0} bytes)");
            report.AppendLine($"│ Total New Size:          {FormatBytes(totalNewSize)} ({totalNewSize:N0} bytes)");
            report.AppendLine($"│ Absolute Difference:     {FormatBytes(Math.Abs(spaceSaved))} ({Math.Abs(spaceSaved):N0} bytes)");
            report.AppendLine("└───────────────────────────────────────────────────────────────────────────────────────────────────┘");
            report.AppendLine();

            // Per-Package Details
            if (packageDetails != null && packageDetails.Count > 0)
            {
                report.AppendLine("┌─ PACKAGE-BY-PACKAGE DETAILS ─────────────────────────────────────────────────────────────────────┐");
                report.AppendLine();

                int packageNum = 1;
                foreach (var kvp in packageDetails.OrderBy(x => x.Key))
                {
                    string packageName = kvp.Key;
                    var details = kvp.Value;

                    report.AppendLine($"  [{packageNum:D2}] Package: {packageName}");
                    report.AppendLine($"      ─ Original Size:         {FormatBytes(details.OriginalSize)} ({details.OriginalSize:N0} bytes)");
                    report.AppendLine($"      ─ New Size:              {FormatBytes(details.NewSize)} ({details.NewSize:N0} bytes)");
                    
                    long pkgSaved = details.OriginalSize - details.NewSize;
                    double pkgPercent = details.OriginalSize > 0 ? (100.0 * pkgSaved / details.OriginalSize) : 0;
                    
                    if (pkgSaved >= 0)
                    {
                        report.AppendLine($"      ─ Space Saved:          {FormatBytes(pkgSaved)} ({pkgPercent:F1}%) ({pkgSaved:N0} bytes)");
                    }
                    else
                    {
                        report.AppendLine($"      ─ Size Increased:       {FormatBytes(Math.Abs(pkgSaved))} (+{Math.Abs(pkgPercent):F1}%) ({Math.Abs(pkgSaved):N0} bytes)");
                    }

                    report.AppendLine($"      ─ Compression Ratio:    {(details.OriginalSize > 0 ? (details.NewSize * 100.0 / details.OriginalSize).ToString("F1") : "0")}%");
                    report.AppendLine("      │");

                    // Optimization details
                    if (details.TextureCount > 0 || details.HairCount > 0 || details.MirrorCount > 0 || 
                        details.LightCount > 0 || details.DisabledDependencies > 0 || details.LatestDependencies > 0 || details.JsonMinified)
                    {
                        report.AppendLine("      ─ Optimizations Applied:");
                        
                        if (details.TextureCount > 0)
                        {
                            report.AppendLine($"      │  ─ Textures Optimized:        {details.TextureCount}");
                            // Use detailed size information if available, otherwise use basic details
                            var textureDetailsToShow = details.TextureDetailsWithSizes.Count > 0 
                                ? details.TextureDetailsWithSizes 
                                : details.TextureDetails;
                            foreach (var textureDetail in textureDetailsToShow)
                            {
                                report.AppendLine($"      │  │  {textureDetail}");
                            }
                        }

                        if (details.HairCount > 0)
                        {
                            report.AppendLine($"      │  ─ Hair Settings Modified:    {details.HairCount}");
                            foreach (var hairDetail in details.HairDetails)
                            {
                                report.AppendLine($"      │  │  • {hairDetail}");
                            }
                        }

                        if (details.MirrorCount > 0)
                        {
                            report.AppendLine($"      │  ─ Mirrors Disabled:          Yes");
                        }

                        if (details.LightCount > 0)
                        {
                            report.AppendLine($"      │  ─ Shadow Settings Modified:  {details.LightCount}");
                            foreach (var lightDetail in details.LightDetails)
                            {
                                report.AppendLine($"      │  │  • {lightDetail}");
                            }
                        }

                        if (details.DisabledDependencies > 0)
                        {
                            report.AppendLine($"      │  ─ Dependencies Removed:     {details.DisabledDependencies}");
                            foreach (var depDetail in details.DisabledDependencyDetails)
                            {
                                report.AppendLine($"      │  │  • {depDetail}");
                            }
                        }

                        if (details.LatestDependencies > 0)
                        {
                            report.AppendLine($"      │  ─ Dependencies to .latest:  {details.LatestDependencies}");
                            foreach (var depDetail in details.LatestDependencyDetails)
                            {
                                report.AppendLine($"      │     • {depDetail}");
                            }
                        }

                        if (details.JsonMinified)
                        {
                            long jsonSaved = details.JsonSizeBeforeMinify - details.JsonSizeAfterMinify;
                            double jsonPercent = details.JsonSizeBeforeMinify > 0 ? (100.0 * jsonSaved / details.JsonSizeBeforeMinify) : 0;
                            report.AppendLine($"      │  └─ JSON Minification:        Yes");
                            report.AppendLine($"      │     ─ Before Minify:         {FormatBytes(details.JsonSizeBeforeMinify)} ({details.JsonSizeBeforeMinify:N0} bytes)");
                            report.AppendLine($"      │     ─ After Minify:          {FormatBytes(details.JsonSizeAfterMinify)} ({details.JsonSizeAfterMinify:N0} bytes)");
                            report.AppendLine($"      │     └─ Space Saved:           {FormatBytes(jsonSaved)} ({jsonPercent:F1}%) ({jsonSaved:N0} bytes)");
                        }
                    }

                    if (!string.IsNullOrEmpty(details.Error))
                    {
                        report.AppendLine($"      └─ Status:               ✗ ERROR");
                        report.AppendLine($"         Error Details:       {details.Error}");
                    }
                    else if (details.OriginalSize > 0)
                    {
                        report.AppendLine($"      └─ Status:               ✓ SUCCESS");
                    }
                    else
                    {
                        report.AppendLine($"      └─ Status:               – NO CHANGES");
                    }

                    report.AppendLine();
                    packageNum++;
                }

                report.AppendLine("└───────────────────────────────────────────────────────────────────────────────────────────────────┘");
                report.AppendLine();
            }

            // Errors Section
            if (errorCount > 0)
            {
                report.AppendLine("┌─ ERRORS ─────────────────────────────────────────────────────────────────────────────────────────┐");
                int errorNum = 1;
                foreach (var error in errors)
                {
                    report.AppendLine($"  [{errorNum:D2}] {error}");
                    errorNum++;
                }
                report.AppendLine("└───────────────────────────────────────────────────────────────────────────────────────────────────┘");
                report.AppendLine();
            }

            // Summary Statistics
            report.AppendLine("┌─ SUMMARY STATISTICS ──────────────────────────────────────────────────────────────────────────────┐");
            report.AppendLine($"│ Total Packages:          {packageDetails?.Count ?? 0}");
            report.AppendLine($"│ Successful:              {(packageDetails?.Count(p => string.IsNullOrEmpty(p.Value.Error)) ?? 0)}");
            report.AppendLine($"│ Failed:                  {errorCount}");
            
            var totalTextures = packageDetails?.Sum(p => p.Value.TextureCount) ?? 0;
            var totalHair = packageDetails?.Sum(p => p.Value.HairCount) ?? 0;
            var totalLights = packageDetails?.Sum(p => p.Value.LightCount) ?? 0;
            var totalDisabledDeps = packageDetails?.Sum(p => p.Value.DisabledDependencies) ?? 0;
            var totalLatestDeps = packageDetails?.Sum(p => p.Value.LatestDependencies) ?? 0;
            var totalJsonMinified = packageDetails?.Count(p => p.Value.JsonMinified) ?? 0;
            var totalJsonSizeBefore = packageDetails?.Sum(p => p.Value.JsonSizeBeforeMinify) ?? 0;
            var totalJsonSizeAfter = packageDetails?.Sum(p => p.Value.JsonSizeAfterMinify) ?? 0;
            var totalJsonSaved = totalJsonSizeBefore - totalJsonSizeAfter;

            if (totalTextures > 0) report.AppendLine($"│ Total Textures Optimized: {totalTextures}");
            if (totalHair > 0) report.AppendLine($"│ Total Hair Modifications: {totalHair}");
            if (totalLights > 0) report.AppendLine($"│ Total Light Modifications: {totalLights}");
            if (totalDisabledDeps > 0) report.AppendLine($"│ Total Dependencies Removed: {totalDisabledDeps}");
            if (totalLatestDeps > 0) report.AppendLine($"│ Total Dependencies to .latest: {totalLatestDeps}");
            if (totalJsonMinified > 0)
            {
                double totalJsonPercent = totalJsonSizeBefore > 0 ? (100.0 * totalJsonSaved / totalJsonSizeBefore) : 0;
                report.AppendLine($"│ Total JSON Minifications:  {totalJsonMinified}");
                report.AppendLine($"│   ─ Total Size Before:    {FormatBytes(totalJsonSizeBefore)} ({totalJsonSizeBefore:N0} bytes)");
                report.AppendLine($"│   ─ Total Size After:     {FormatBytes(totalJsonSizeAfter)} ({totalJsonSizeAfter:N0} bytes)");
                report.AppendLine($"│   └─ Total Space Saved:    {FormatBytes(totalJsonSaved)} ({totalJsonPercent:F1}%) ({totalJsonSaved:N0} bytes)");
            }

            report.AppendLine("└───────────────────────────────────────────────────────────────────────────────────────────────────┘");
            report.AppendLine();

            // Performance Breakdown (if we have timing data)
            if (elapsedTime.HasValue && packageDetails != null && packageDetails.Count > 0)
            {
                report.AppendLine("┌─ PERFORMANCE BREAKDOWN ──────────────────────────────────────────────────────────────────────────┐");
                
                // Find fastest and slowest packages by size
                var sortedBySize = packageDetails.Where(p => string.IsNullOrEmpty(p.Value.Error) && p.Value.OriginalSize > 0)
                    .OrderBy(p => p.Value.OriginalSize).ToList();
                
                if (sortedBySize.Any())
                {
                    var smallest = sortedBySize.First();
                    var largest = sortedBySize.Last();
                    
                    report.AppendLine($"│ Smallest Package:        {smallest.Key}");
                    report.AppendLine($"│   ─ Size:               {FormatBytes(smallest.Value.OriginalSize)}");
                    report.AppendLine($"│");
                    report.AppendLine($"│ Largest Package:         {largest.Key}");
                    report.AppendLine($"│   ─ Size:               {FormatBytes(largest.Value.OriginalSize)}");
                }
                
                // Most optimized package (by percentage)
                var packagesWithSavings = packageDetails.Where(p => string.IsNullOrEmpty(p.Value.Error) && 
                    p.Value.OriginalSize > 0 && p.Value.OriginalSize > p.Value.NewSize).ToList();
                
                if (packagesWithSavings.Any())
                {
                    var mostOptimized = packagesWithSavings.OrderByDescending(p => 
                        (p.Value.OriginalSize - p.Value.NewSize) * 100.0 / p.Value.OriginalSize).First();
                    
                    long saved = mostOptimized.Value.OriginalSize - mostOptimized.Value.NewSize;
                    double percent = (saved * 100.0) / mostOptimized.Value.OriginalSize;
                    
                    report.AppendLine($"│");
                    report.AppendLine($"│ Most Optimized Package:  {mostOptimized.Key}");
                    report.AppendLine($"│   ─ Space Saved:        {FormatBytes(saved)} ({percent:F1}%)");
                }
                
                report.AppendLine("└───────────────────────────────────────────────────────────────────────────────────────────────────┘");
                report.AppendLine();
            }

            report.AppendLine("╔═══════════════════════════════════════════════════════════════════════════════════════════════════╗");
            report.AppendLine($"║                              END OF REPORT - VPM v{appVersion,-38} ║");
            if (elapsedTime.HasValue)
            {
                report.AppendLine($"║                              Generated in {FormatTimeSpan(elapsedTime.Value),-44} ║");
            }
            report.AppendLine("╚═══════════════════════════════════════════════════════════════════════════════════════════════════╝");

            return report.ToString();
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
                Padding = new Thickness(15)
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

