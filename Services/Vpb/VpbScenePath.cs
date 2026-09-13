using System;
using System.IO;

namespace VPM.Services.Vpb
{
    public static class VpbScenePath
    {
        public static string ToSceneValue(string vamRoot, string sceneFilePath)
        {
            if (string.IsNullOrWhiteSpace(sceneFilePath)) return null;

            var parts = sceneFilePath.Split(new[] { "::" }, 2, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                var uid = VpbUid.Canonical(Path.GetFileNameWithoutExtension(parts[0]));
                var internalPath = parts[1]?.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(internalPath))
                    return null;
                return $"{uid}:/{internalPath}";
            }

            if (sceneFilePath.Contains(":/", StringComparison.Ordinal)
                && !Path.IsPathRooted(sceneFilePath))
                return sceneFilePath.Replace('\\', '/');

            var loose = sceneFilePath.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(vamRoot))
                return loose;

            try
            {
                var full = Path.GetFullPath(sceneFilePath);
                var root = Path.GetFullPath(vamRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
            }
            catch
            {
            }

            return loose;
        }
    }
}
