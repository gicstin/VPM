using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VPM.Services.Vpb
{
    public static class VpbLockedPackagesStore
    {
        public static HashSet<string> Load(string vamRoot)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var path = VpbPaths.LockedPackagesPath(vamRoot);
            var bak = path + ".bak";
            if (TryLoad(path, set)) return set;
            TryLoad(bak, set);
            return set;
        }

        private static bool TryLoad(string path, HashSet<string> set)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                var json = File.ReadAllText(path);
                if (string.IsNullOrEmpty(json) || json.Trim().Length < 2) return false;
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list == null) return false;
                foreach (var item in list)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                        set.Add(item.Trim());
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string UidFromVarPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "";
            return Path.GetFileNameWithoutExtension(filePath) ?? "";
        }
    }
}
