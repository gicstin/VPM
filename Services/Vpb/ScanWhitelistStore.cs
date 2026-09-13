using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VPM.Services.Vpb
{
    public static class ScanWhitelistStore
    {
        public static ScanWhitelistData Load(string vamRoot)
        {
            var path = VpbPaths.ScanWhitelistPath(vamRoot);
            var bak = path + ".bak";

            if (TryLoadFile(path, out var data) && data != null)
                return data;
            if (TryLoadFile(bak, out data) && data != null)
            {
                try { File.Copy(bak, path, true); } catch { }
                return data;
            }

            return new ScanWhitelistData
            {
                SchemaVersion = ScanWhitelistData.CurrentSchemaVersion,
                Enabled = false
            };
        }

        public static void Save(string vamRoot, ScanWhitelistData data, bool allowEnabledEmpty = false)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.IsEnabledEmpty && !allowEnabledEmpty)
                throw new InvalidOperationException("Refusing to write enabled-empty whitelist (VaM boot would scan nothing).");

            data.SchemaVersion = ScanWhitelistData.CurrentSchemaVersion;
            data.WhitelistedFolders = NormalizeFolders(data.WhitelistedFolders);
            data.IncludedPackageUids = NormalizeUids(data.IncludedPackageUids);

            var dir = VpbPaths.PluginDataDirectory(vamRoot);
            Directory.CreateDirectory(dir);

            var path = VpbPaths.ScanWhitelistPath(vamRoot);
            var tmpPath = path + ".tmp";
            var backupPath = path + ".bak";

            var json = JsonSerializer.Serialize(data, JsonSourceGeneration);
            if (string.IsNullOrEmpty(json) || json.Trim().Length < 2)
                throw new InvalidOperationException("Whitelist serialize produced empty JSON.");

            File.WriteAllText(tmpPath, json);

            if (!File.Exists(tmpPath) || new FileInfo(tmpPath).Length < 2)
                throw new InvalidOperationException("Whitelist tmp write failed.");

            if (!TryLoadFile(tmpPath, out var roundtrip) || roundtrip == null)
            {
                try { File.Delete(tmpPath); } catch { }
                throw new InvalidOperationException("Whitelist tmp failed validation.");
            }

            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 2)
                {
                    try
                    {
                        if (File.Exists(backupPath)) File.Delete(backupPath);
                        File.Move(path, backupPath);
                    }
                    catch
                    {
                        if (File.Exists(path)) File.Delete(path);
                    }
                }
                else
                {
                    File.Delete(path);
                }
            }

            File.Move(tmpPath, path);
        }

        public static string NormalizeFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            return folder.Replace('\\', '/').TrimEnd('/').Trim();
        }

        public static string FolderFromVarPath(string varFilePath, string vamRoot)
        {
            if (string.IsNullOrEmpty(varFilePath)) return null;
            var dir = Path.GetDirectoryName(varFilePath);
            if (string.IsNullOrEmpty(dir)) return null;
            if (!string.IsNullOrEmpty(vamRoot))
            {
                var root = Path.GetFullPath(vamRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = Path.GetFullPath(dir);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = full.Substring(root.Length).TrimStart('\\', '/');
                    return NormalizeFolder(rel);
                }
            }
            return NormalizeFolder(dir);
        }

        private static readonly JsonSerializerOptions JsonSourceGeneration = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private static bool TryLoadFile(string path, out ScanWhitelistData data)
        {
            data = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                var json = File.ReadAllText(path);
                if (string.IsNullOrEmpty(json) || json.Trim().Length < 2) return false;
                data = JsonSerializer.Deserialize<ScanWhitelistData>(json, JsonSourceGeneration);
                if (data == null) return false;
                if (data.SchemaVersion <= 0)
                    data.SchemaVersion = ScanWhitelistData.CurrentSchemaVersion;
                data.WhitelistedFolders = NormalizeFolders(data.WhitelistedFolders);
                data.IncludedPackageUids = NormalizeUids(data.IncludedPackageUids);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScanWhitelistStore] parse failed: {ex.Message}");
                return false;
            }
        }

        private static List<string> NormalizeFolders(List<string> folders)
        {
            var result = new List<string>();
            if (folders == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in folders)
            {
                var n = NormalizeFolder(f);
                if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
                result.Add(n);
            }
            return result;
        }

        private static List<string> NormalizeUids(List<string> uids)
        {
            var result = new List<string>();
            if (uids == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in uids)
            {
                var n = VpbUid.Canonical(u);
                if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
                result.Add(n);
            }
            return result;
        }
    }
}
