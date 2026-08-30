using System;
using System.Collections.Generic;

namespace VPM.Services.Vpb
{
    /// <summary>Hash lookup over a whitelist snapshot. Rebuild after every mutation; scanning raw lists is quadratic once unlisted folders expand to uid pins.</summary>
    public sealed class ScanWhitelistIndex
    {
        private static readonly ScanWhitelistIndex DisabledIndex = new(false, null, null);

        private readonly HashSet<string> _folders;
        private readonly HashSet<string> _uids;

        public bool Enabled { get; }

        private ScanWhitelistIndex(bool enabled, HashSet<string> folders, HashSet<string> uids)
        {
            Enabled = enabled;
            _folders = folders;
            _uids = uids;
        }

        public static ScanWhitelistIndex From(ScanWhitelistData data)
        {
            if (data == null || !data.Enabled)
                return DisabledIndex;

            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (data.WhitelistedFolders != null)
            {
                foreach (var f in data.WhitelistedFolders)
                {
                    var n = ScanWhitelistStore.NormalizeFolder(f);
                    if (!string.IsNullOrEmpty(n)) folders.Add(n);
                }
            }

            var uids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (data.IncludedPackageUids != null)
            {
                foreach (var u in data.IncludedPackageUids)
                {
                    var n = u?.Trim();
                    if (!string.IsNullOrEmpty(n)) uids.Add(n);
                }
            }

            return new ScanWhitelistIndex(true, folders, uids);
        }

        /// <summary>True when the package is in VaM's boot scan set. <paramref name="varFilePath"/> is relative to the VaM root, forward-slashed.</summary>
        public bool Contains(string varFilePath, string uid)
        {
            if (!Enabled) return false;
            if (!string.IsNullOrEmpty(uid) && _uids.Contains(uid)) return true;
            if (string.IsNullOrEmpty(varFilePath)) return true;

            var norm = varFilePath.Replace('\\', '/');
            if (norm.StartsWith("AllPackages/", StringComparison.OrdinalIgnoreCase)) return true;
            if (_folders.Count == 0) return false;

            // A whitelisted folder covers everything beneath it, so walk the path's ancestor chain (depth is 2-4) instead of prefix-testing every whitelist entry.
            var probe = norm;
            while (!string.IsNullOrEmpty(probe))
            {
                if (_folders.Contains(probe)) return true;
                var slash = probe.LastIndexOf('/');
                if (slash <= 0) break;
                probe = probe.Substring(0, slash);
            }
            return false;
        }
    }
}
