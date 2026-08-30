using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VPM.Services.Vpb
{
    /// <summary>Reads VPB ratings.json / creator_ratings.json. Keys are VaM FileEntry uids, never package uids. A package's rating is the max across every key that resolves to it.</summary>
    public static class VpbRatingsStore
    {
        /// <summary>Package uid ("Creator.Pkg.1") to best rating found on the package or anything inside it.</summary>
        public static Dictionary<string, int> LoadPackageRatings(string vamRoot)
        {
            var byPackage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in LoadRawRatings(vamRoot))
            {
                if (kvp.Value <= 0) continue;
                var uid = PackageUidFromRatingKey(kvp.Key);
                if (string.IsNullOrEmpty(uid)) continue;
                if (!byPackage.TryGetValue(uid, out var best) || kvp.Value > best)
                    byPackage[uid] = kvp.Value;
            }
            return byPackage;
        }

        /// <summary>Every rating entry exactly as stored, keyed by VaM FileEntry uid.</summary>
        public static Dictionary<string, int> LoadRawRatings(string vamRoot)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var path = VpbPaths.RatingsPath(vamRoot);
            if (!TryLoad(path, "uid", map))
                TryLoad(path + ".bak", "uid", map);
            return map;
        }

        /// <summary>Creator name to 0-5 rating. Separate file so creator keys cannot collide with file uids.</summary>
        public static Dictionary<string, int> LoadCreatorRatings(string vamRoot)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var path = VpbPaths.CreatorRatingsPath(vamRoot);
            if (!TryLoad(path, "name", map))
                TryLoad(path + ".bak", "name", map);
            return map;
        }

        /// <summary>True when the key rates the package itself rather than a file inside it. Only these keys are VPM's to write; the "uid:/internal/path" ones belong to VPB's per-file rating UI.</summary>
        public static bool IsPackageLevelRatingKey(string key) =>
            !string.IsNullOrEmpty(key) && key.Replace('\\', '/').IndexOf(":/", StringComparison.Ordinal) < 0;

        /// <summary>Resolves a rating key to the package it belongs to, or "" for loose (non-package) files. Handles the three shapes VPB writes: "pkgUid:/internal/path", "AddonPackages|AllPackages/pkgUid.var", and a bare package uid.</summary>
        public static string PackageUidFromRatingKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var k = key.Replace('\\', '/').Trim();
            if (k.Length == 0) return "";

            var sep = k.IndexOf(":/", StringComparison.Ordinal);
            if (sep > 0) return k.Substring(0, sep).Trim();

            if (k.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
            {
                var slash = k.LastIndexOf('/');
                var name = slash >= 0 ? k.Substring(slash + 1) : k;
                return name.Substring(0, name.Length - 4).Trim();
            }

            // Bare uid: no path separators, trailing numeric version.
            if (k.IndexOf('/') < 0 && LooksLikePackageUid(k)) return k;
            return "";
        }

        private static bool LooksLikePackageUid(string value)
        {
            var lastDot = value.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == value.Length - 1) return false;
            for (int i = lastDot + 1; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i])) return false;
            }
            // Needs at least Creator.Name.Version
            return value.IndexOf('.') != lastDot;
        }

        private static bool TryLoad(string path, string idProperty, Dictionary<string, int> map)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(stream);
                if (!doc.RootElement.TryGetProperty("ratings", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return false;
                foreach (var el in arr.EnumerateArray())
                {
                    if (!el.TryGetProperty(idProperty, out var idEl)) continue;
                    var id = idEl.GetString();
                    if (string.IsNullOrEmpty(id)) continue;
                    var rating = 0;
                    if (el.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number)
                        rating = r.GetInt32();
                    map[id] = rating;
                }
                return map.Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
