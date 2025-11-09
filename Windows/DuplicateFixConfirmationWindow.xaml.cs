using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace VPM
{
    public partial class DuplicateFixConfirmationWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public bool Confirmed { get; private set; } = false;

        public DuplicateFixConfirmationWindow(Dictionary<string, string> packagesToMove, List<string> packagesToDelete)
        {
            InitializeComponent();
            
            // Apply dark theme to window chrome
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
                int useImmersiveDarkMode = 1;
                // Try Windows 11/10 20H1+ attribute first, then fall back to older Windows 10 attribute
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
                }
            }
            catch
            {
                // Dark mode not available on this system
            }
            
            BuildConfirmationMessage(packagesToMove, packagesToDelete);
        }

        private void BuildConfirmationMessage(Dictionary<string, string> packagesToMove, List<string> packagesToDelete)
        {
            // Group all files by package name
            var packageGroups = new Dictionary<string, PackageOperationInfo>(StringComparer.OrdinalIgnoreCase);
            
            // Process files to be deleted
            foreach (var filePath in packagesToDelete)
            {
                var packageName = Path.GetFileNameWithoutExtension(filePath);
                var baseName = ExtractBasePackageName(packageName);
                
                if (!packageGroups.TryGetValue(baseName, out var info))
                {
                    info = new PackageOperationInfo { BaseName = baseName };
                    packageGroups[baseName] = info;
                }
                
                info.FilesToDelete.Add(filePath);
            }
            
            // Process files to be moved
            foreach (var kvp in packagesToMove)
            {
                var packageName = Path.GetFileNameWithoutExtension(kvp.Key);
                var baseName = ExtractBasePackageName(packageName);
                
                if (!packageGroups.TryGetValue(baseName, out var info))
                {
                    info = new PackageOperationInfo { BaseName = baseName };
                    packageGroups[baseName] = info;
                }
                
                info.FilesToMove.Add(kvp);
            }
            
            // Find files that will be kept (exist but not in delete or move lists)
            var allDeletePaths = new HashSet<string>(packagesToDelete, StringComparer.OrdinalIgnoreCase);
            var allMovePaths = new HashSet<string>(packagesToMove.Keys, StringComparer.OrdinalIgnoreCase);
            
            // Build the message
            var message = new StringBuilder();
            long totalSpaceFreed = 0;
            int totalPackages = packageGroups.Count;
            int totalFilesToDelete = packagesToDelete.Count;
            int totalFilesToMove = packagesToMove.Count;
            
            // Calculate total space
            foreach (var path in packagesToDelete)
            {
                try
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Exists)
                        totalSpaceFreed += fileInfo.Length;
                }
                catch { }
            }
            
            // Update summary
            SummaryText.Text = $"{totalPackages} package(s) affected | {totalFilesToDelete} file(s) to delete | {totalFilesToMove} file(s) to move | {FormatFileSize(totalSpaceFreed)} to be freed";
            
            // Build detailed content grouped by package
            int packageNum = 0;
            foreach (var group in packageGroups.OrderBy(g => g.Key))
            {
                packageNum++;
                var info = group.Value;
                
                message.AppendLine($"•”•••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••");
                message.AppendLine($"•‘ PACKAGE #{packageNum}: {info.BaseName}");
                message.AppendLine($"•š•••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••");
                message.AppendLine();
                
                // Files to keep - these are files that will remain after the operation
                var filesToKeep = new List<string>();
                
                // Find all files for this package base name in both folders
                var addonPath = Path.GetDirectoryName(info.FilesToDelete.FirstOrDefault() ?? info.FilesToMove.FirstOrDefault().Key ?? "");
                var allPath = addonPath;
                
                // Search for all files matching this base name pattern
                var searchPattern = $"{info.BaseName}.*.var";
                var foundFiles = new List<string>();
                
                // Try to find files in AddonPackages and AllPackages folders
                try
                {
                    if (!string.IsNullOrEmpty(addonPath))
                    {
                        var rootPath = addonPath;
                        while (!string.IsNullOrEmpty(rootPath) && !rootPath.EndsWith("AddonPackages", StringComparison.OrdinalIgnoreCase) && !rootPath.EndsWith("AllPackages", StringComparison.OrdinalIgnoreCase))
                        {
                            rootPath = Path.GetDirectoryName(rootPath);
                        }
                        
                        if (!string.IsNullOrEmpty(rootPath))
                        {
                            var gameRoot = Path.GetDirectoryName(rootPath);
                            if (!string.IsNullOrEmpty(gameRoot))
                            {
                                var addonPackagesPath = Path.Combine(gameRoot, "AddonPackages");
                                var allPackagesPath = Path.Combine(gameRoot, "AllPackages");
                                
                                if (Directory.Exists(addonPackagesPath))
                                    foundFiles.AddRange(Directory.GetFiles(addonPackagesPath, searchPattern, SearchOption.AllDirectories));
                                if (Directory.Exists(allPackagesPath))
                                    foundFiles.AddRange(Directory.GetFiles(allPackagesPath, searchPattern, SearchOption.AllDirectories));
                            }
                        }
                    }
                }
                catch { }
                
                // Files to keep are: existing files NOT in delete list, NOT in move source list
                foreach (var file in foundFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!allDeletePaths.Contains(file) && !allMovePaths.Contains(file))
                    {
                        filesToKeep.Add(file);
                    }
                }
                
                // Add destination files from moves as kept files (they will exist after the operation)
                foreach (var kvp in info.FilesToMove)
                {
                    if (!filesToKeep.Contains(kvp.Value, StringComparer.OrdinalIgnoreCase))
                        filesToKeep.Add(kvp.Value);
                }
                
                if (filesToKeep.Count > 0)
                {
                    message.AppendLine("  ✓ FILES TO KEEP:");
                    message.AppendLine("  ─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─");
                    foreach (var file in filesToKeep.OrderBy(f => f))
                    {
                        if (File.Exists(file))
                        {
                            var fileInfo = new FileInfo(file);
                            var sha = CalculateFileSHA256(file);
                            message.AppendLine($"    • {Path.GetFileName(file)}");
                            message.AppendLine($"      Path: {file}");
                            message.AppendLine($"      Size: {FormatFileSize(fileInfo.Length),10} | SHA256: {sha}");
                            message.AppendLine();
                        }
                        else
                        {
                            // Destination file from move (doesn't exist yet)
                            message.AppendLine($"    • {Path.GetFileName(file)} (will be moved here)");
                            message.AppendLine($"      Path: {file}");
                            message.AppendLine();
                        }
                    }
                }
                
                // Files to move
                if (info.FilesToMove.Count > 0)
                {
                    message.AppendLine("  âžœ FILES TO MOVE:");
                    message.AppendLine("  ─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─");
                    foreach (var kvp in info.FilesToMove.OrderBy(m => m.Key))
                    {
                        var fileInfo = new FileInfo(kvp.Key);
                        var sha = CalculateFileSHA256(kvp.Key);
                        message.AppendLine($"    • {Path.GetFileName(kvp.Key)}");
                        message.AppendLine($"      FROM: {kvp.Key}");
                        message.AppendLine($"      TO:   {kvp.Value}");
                        message.AppendLine($"      Size: {FormatFileSize(fileInfo.Length),10} | SHA256: {sha}");
                        message.AppendLine();
                    }
                }
                
                // Files to delete
                if (info.FilesToDelete.Count > 0)
                {
                    message.AppendLine("  ✗ FILES TO DELETE:");
                    message.AppendLine("  ─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─”€─");
                    foreach (var file in info.FilesToDelete.OrderBy(f => f))
                    {
                        var fileInfo = new FileInfo(file);
                        var sha = CalculateFileSHA256(file);
                        message.AppendLine($"    • {Path.GetFileName(file)}");
                        message.AppendLine($"      Path: {file}");
                        message.AppendLine($"      Size: {FormatFileSize(fileInfo.Length),10} | SHA256: {sha}");
                        message.AppendLine();
                    }
                }
                
                message.AppendLine();
            }
            
            message.AppendLine("•••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••");
            message.AppendLine($"TOTAL SPACE TO BE FREED: {FormatFileSize(totalSpaceFreed)}");
            message.AppendLine("•••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••••");
            
            ContentText.Text = message.ToString();
        }

        private class PackageOperationInfo
        {
            public string BaseName { get; set; }
            public List<string> FilesToDelete { get; set; } = new List<string>();
            public List<KeyValuePair<string, string>> FilesToMove { get; set; } = new List<KeyValuePair<string, string>>();
        }

        private string ExtractBasePackageName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return displayName;
                
            var parts = displayName.Split('.');
            if (parts.Length >= 3) // Creator.PackageName.Version format
            {
                return $"{parts[0]}.{parts[1]}"; // Return Creator.PackageName
            }
            
            return displayName; // Return as-is if format doesn't match
        }

        private string CalculateFileSHA256(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return "N/A";
                    
                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                var hash = sha256.ComputeHash(stream);
                var hashString = BitConverter.ToString(hash).Replace("-", "");
                // Return first 8 characters for brevity
                return hashString.Substring(0, Math.Min(8, hashString.Length));
            }
            catch
            {
                return "Error";
            }
        }
        
        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            DialogResult = false;
            Close();
        }
    }
}

