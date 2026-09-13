using System;
using System.Collections.Generic;
using VPM.Models;

namespace VPM.Services.Vpb
{
    public static class VpbUid
    {
        public static string Canonical(string metadataKeyOrUid)
        {
            if (string.IsNullOrWhiteSpace(metadataKeyOrUid)) return null;

            var key = metadataKeyOrUid.Trim();

            var hash = key.IndexOf('#');
            if (hash >= 0)
                key = key.Substring(0, hash);

            if (key.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - 4);

            key = key.Trim();
            return key.Length == 0 ? null : key;
        }
    }

    public sealed class VpbPackageKeyIndex
    {
        private readonly Dictionary<string, VarMetadata> _byUid;
        private readonly Dictionary<string, List<(string Uid, VarMetadata Meta)>> _byBaseName;

        private VpbPackageKeyIndex(
            Dictionary<string, VarMetadata> byUid,
            Dictionary<string, List<(string, VarMetadata)>> byBaseName)
        {
            _byUid = byUid;
            _byBaseName = byBaseName;
        }

        public static VpbPackageKeyIndex Build(IReadOnlyDictionary<string, VarMetadata> metadata)
        {
            var byUid = new Dictionary<string, VarMetadata>(StringComparer.OrdinalIgnoreCase);
            var byBaseName = new Dictionary<string, List<(string, VarMetadata)>>(StringComparer.OrdinalIgnoreCase);
            if (metadata == null)
                return new VpbPackageKeyIndex(byUid, byBaseName);

            foreach (var kvp in metadata)
            {
                var meta = kvp.Value;
                if (meta == null) continue;

                var uid = VpbUid.Canonical(kvp.Key);
                if (uid == null) continue;

                if (!byUid.TryGetValue(uid, out var existing) || PrefersOver(meta, existing))
                    byUid[uid] = meta;

                var baseName = $"{meta.CreatorName}.{meta.PackageName}";
                if (string.IsNullOrWhiteSpace(baseName)) continue;
                if (!byBaseName.TryGetValue(baseName, out var list))
                {
                    list = new List<(string, VarMetadata)>();
                    byBaseName[baseName] = list;
                }
                list.Add((uid, meta));
            }

            return new VpbPackageKeyIndex(byUid, byBaseName);
        }

        private static bool PrefersOver(VarMetadata candidate, VarMetadata current)
        {
            if (current == null) return true;
            return Rank(candidate) > Rank(current);
        }

        private static int Rank(VarMetadata meta)
        {
            if (meta == null) return -1;
            var path = meta.FilePath?.Replace('\\', '/') ?? "";
            var archived = string.Equals(meta.Status, "Archived", StringComparison.OrdinalIgnoreCase);
            if (path.Contains("AddonPackages/", StringComparison.OrdinalIgnoreCase))
                return archived ? 2 : 3;
            if (path.Contains("AllPackages/", StringComparison.OrdinalIgnoreCase))
                return archived ? 0 : 1;
            return 0;
        }

        public VarMetadata Lookup(string uid) =>
            uid != null && _byUid.TryGetValue(uid, out var meta) ? meta : null;

        public string ResolveUid(string key)
        {
            var canonical = VpbUid.Canonical(key);
            if (canonical == null) return null;
            if (_byUid.ContainsKey(canonical)) return canonical;

            var info = DependencyVersionInfo.Parse(canonical);
            var baseName = string.IsNullOrWhiteSpace(info.BaseName) ? canonical : info.BaseName;
            if (!_byBaseName.TryGetValue(baseName, out var variants) || variants.Count == 0)
                return null;

            string best = null;
            var bestVersion = int.MinValue;
            var bestRank = int.MinValue;
            foreach (var (uid, meta) in variants)
            {
                if (!info.IsSatisfiedBy(meta.Version)) continue;
                var rank = Rank(meta);
                if (meta.Version < bestVersion) continue;
                if (meta.Version == bestVersion && rank <= bestRank) continue;
                bestVersion = meta.Version;
                bestRank = rank;
                best = uid;
            }
            return best;
        }
    }
}
