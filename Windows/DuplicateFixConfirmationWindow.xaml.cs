using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using VPM.Models;
using VPM.Services;

namespace VPM
{
    public partial class DuplicateFixConfirmationWindow : Window
    {
        public bool Confirmed { get; private set; } = false;

        public DuplicateFixConfirmationWindow(Dictionary<string, string> packagesToMove, List<string> packagesToDelete)
        {
            InitializeComponent();
            DarkTitleBarHelper.Apply(this);
            BuildConfirmationMessage(packagesToMove, packagesToDelete, null);
        }

        public DuplicateFixConfirmationWindow(Dictionary<string, string> packagesToMove, List<string> packagesToDelete, List<string> packagesToKeep)
        {
            InitializeComponent();
            DarkTitleBarHelper.Apply(this);
            BuildConfirmationMessage(packagesToMove, packagesToDelete, packagesToKeep);
        }

        private void BuildConfirmationMessage(Dictionary<string, string> packagesToMove, List<string> packagesToDelete, List<string> packagesToKeep)
        {
            var packageGroups = new Dictionary<string, PackageOperationInfo>(StringComparer.OrdinalIgnoreCase);

            packagesToMove ??= new Dictionary<string, string>();
            packagesToDelete ??= new List<string>();
            packagesToKeep ??= new List<string>();

            foreach (var filePath in packagesToDelete)
            {
                AddFileToGroup(packageGroups, filePath, (info, path) => info.FilesToDelete.Add(path));
            }

            foreach (var filePath in packagesToKeep)
            {
                AddFileToGroup(packageGroups, filePath, (info, path) => info.FilesToKeep.Add(path));
            }

            foreach (var kvp in packagesToMove)
            {
                AddFileToGroup(packageGroups, kvp.Key, (info, path) => info.FilesToMove.Add(kvp));
            }

            var message = new StringBuilder();
            long totalSpaceFreed = 0;

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

            SummaryText.Text =
                $"{packageGroups.Count} package(s) affected | {packagesToKeep.Count} file(s) to keep | {packagesToDelete.Count} file(s) to delete | {packagesToMove.Count} file(s) to move | {FormatHelper.FormatFileSize(totalSpaceFreed)} to be freed";

            int packageNum = 0;
            foreach (var group in packageGroups.OrderBy(g => g.Key))
            {
                packageNum++;
                var info = group.Value;

                message.AppendLine("================================================================================");
                message.AppendLine($" PACKAGE #{packageNum}: {info.BaseName}");
                message.AppendLine("================================================================================");
                message.AppendLine();

                if (info.FilesToKeep.Count > 0)
                {
                    message.AppendLine("  [KEEP] FILES TO KEEP:");
                    message.AppendLine("  ------------------------------------------------------------------------------");
                    foreach (var file in info.FilesToKeep.OrderBy(f => f))
                        AppendFileDetails(message, file);
                }

                if (info.FilesToMove.Count > 0)
                {
                    message.AppendLine("  -> FILES TO MOVE:");
                    message.AppendLine("  ------------------------------------------------------------------------------");
                    foreach (var kvp in info.FilesToMove.OrderBy(m => m.Key))
                    {
                        AppendFileDetails(message, kvp.Key);
                        message.AppendLine($"      TO:   {kvp.Value}");
                        message.AppendLine();
                    }
                }

                if (info.FilesToDelete.Count > 0)
                {
                    message.AppendLine("  [DEL] FILES TO DELETE:");
                    message.AppendLine("  ------------------------------------------------------------------------------");
                    foreach (var file in info.FilesToDelete.OrderBy(f => f))
                        AppendFileDetails(message, file);
                }

                message.AppendLine();
            }

            message.AppendLine("================================================================================");
            message.AppendLine($"TOTAL SPACE TO BE FREED: {FormatHelper.FormatFileSize(totalSpaceFreed)}");
            message.AppendLine("================================================================================");

            ContentText.Text = message.ToString();
        }

        private static void AddFileToGroup(
            Dictionary<string, PackageOperationInfo> packageGroups,
            string filePath,
            Action<PackageOperationInfo, string> addAction)
        {
            if (string.IsNullOrEmpty(filePath))
                return;

            var packageName = Path.GetFileNameWithoutExtension(filePath);
            var baseName = DependencyVersionInfo.GetBaseName(packageName);

            if (!packageGroups.TryGetValue(baseName, out var info))
            {
                info = new PackageOperationInfo { BaseName = baseName };
                packageGroups[baseName] = info;
            }

            addAction(info, filePath);
        }

        private static void AppendFileDetails(StringBuilder message, string filePath)
        {
            message.AppendLine($"    • {Path.GetFileName(filePath)}");
            message.AppendLine($"      Path: {filePath}");

            try
            {
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    message.AppendLine($"      Size: {FormatHelper.FormatFileSize(fileInfo.Length),10} | Modified: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm}");
                }
            }
            catch { }

            message.AppendLine();
        }

        private class PackageOperationInfo
        {
            public string BaseName { get; set; }
            public List<string> FilesToKeep { get; set; } = new List<string>();
            public List<string> FilesToDelete { get; set; } = new List<string>();
            public List<KeyValuePair<string, string>> FilesToMove { get; set; } = new List<KeyValuePair<string, string>>();
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
