using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VPM.Services
{
    public static class BrowserAssistService
    {
        private const string BaPluginDataPath = "Saves\\PluginData\\JayJayWon\\BrowserAssist";
        private static readonly string[] ThumbnailExtensions = { ".jpg", ".png", ".jpeg" };

        public static string GetOffloadedVarsFolder(string vamRoot)
            => Path.Combine(vamRoot, BaPluginDataPath, "OffloadedVARs");

        public static bool IsPathInOffloadedVars(string filePath, string offloadedVarsFolder)
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(offloadedVarsFolder))
                return false;

            try
            {
                var fullFile = Path.GetFullPath(filePath);
                var fullFolder = Path.GetFullPath(offloadedVarsFolder).TrimEnd(Path.DirectorySeparatorChar)
                                 + Path.DirectorySeparatorChar;
                return fullFile.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BA] Failed to validate path containment: {ex.Message}");
                return false;
            }
        }

        public static string GetVarMetaCacheFolder(string vamRoot)
            => Path.Combine(vamRoot, BaPluginDataPath, "DependencyCache", "VARs");

        public static string GetVarThumbnailCacheFolder(string vamRoot)
            => Path.Combine(vamRoot, BaPluginDataPath, "DependencyCache", "VARThumbnails");

        public static bool IsInstalled(string vamRoot)
            => Directory.Exists(GetOffloadedVarsFolder(vamRoot));

        public static string GetSettingsFilePath(string vamRoot)
            => Path.Combine(vamRoot, BaPluginDataPath, "BASettings.cfg");

        // Returns true if BA's built-in VAR manager is active.
        // When true, VPM must not move packages - BA owns the load/unload lifecycle.
        public static bool IsVarManagementEnabled(string vamRoot)
        {
            string settingsPath = GetSettingsFilePath(vamRoot);
            if (!File.Exists(settingsPath))
                return false;

            try
            {
                string json = File.ReadAllText(settingsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("VARMgmgtSettings", out var varMgmt) &&
                    varMgmt.TryGetProperty("varMgmntEnabled", out var enabled))
                {
                    // BA stores booleans as JSON strings: "true" / "false"
                    return string.Equals(enabled.GetString(), "true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BA] Failed to read BASettings.cfg: {ex.Message}");
            }

            return false;
        }

        // varFilename can be "Creator.Package.Version.var" or "Creator.Package.Version"
        public static void CleanCacheForPackage(string vamRoot, string varFilename)
        {
            string varName = varFilename.EndsWith(".var", StringComparison.OrdinalIgnoreCase)
                ? varFilename[..^4]
                : varFilename;

            try
            {
                string metaPath = Path.Combine(GetVarMetaCacheFolder(vamRoot), varName + ".meta");
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                    Debug.WriteLine($"[BA] Deleted meta cache: {metaPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BA] Failed to delete meta cache for {varName}: {ex.Message}");
            }

            try
            {
                string thumbDir = Path.Combine(GetVarThumbnailCacheFolder(vamRoot), varName);
                if (Directory.Exists(thumbDir))
                {
                    Directory.Delete(thumbDir, recursive: true);
                    Debug.WriteLine($"[BA] Deleted thumbnail dir: {thumbDir}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BA] Failed to delete thumbnail dir for {varName}: {ex.Message}");
            }

            // Also check for flat image files alongside the thumbnail dirs
            string thumbBase = Path.Combine(GetVarThumbnailCacheFolder(vamRoot), varName);
            foreach (string ext in ThumbnailExtensions)
            {
                try
                {
                    string flatFile = thumbBase + ext;
                    if (File.Exists(flatFile))
                    {
                        File.Delete(flatFile);
                        Debug.WriteLine($"[BA] Deleted flat thumbnail: {flatFile}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BA] Failed to delete flat thumbnail {varName}{ext}: {ex.Message}");
                }
            }
        }

        // Deletes the {varFilePath}.json companion sidecar left by BA when it offloads a VAR
        public static void DeleteCompanionJson(string varFilePath)
        {
            string companionPath = varFilePath + ".json";
            try
            {
                if (File.Exists(companionPath))
                {
                    File.Delete(companionPath);
                    Debug.WriteLine($"[BA] Deleted companion JSON: {companionPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BA] Failed to delete companion JSON for {varFilePath}: {ex.Message}");
            }
        }
    }
}
