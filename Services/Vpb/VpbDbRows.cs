using System.Collections.Generic;

namespace VPM.Services.Vpb
{
    /// <summary>A row of VPB's <c>pkg</c> index: what VPB learned about a var without VPM reopening the zip.</summary>
    public sealed class VpbPackageRow
    {
        /// <summary>VaM package uid, "Creator.Name.Version".</summary>
        public string Uid { get; init; } = "";
        public string Creator { get; init; } = "";
        /// <summary>Path relative to the VaM root, e.g. "AddonPackages/Creator.Name.1.var".</summary>
        public string VarPath { get; init; } = "";
        /// <summary>License string from meta.json ("PC", "CC BY", …), "" when VPB could not resolve one.</summary>
        public string License { get; init; } = "";
        /// <summary>Lowercased "creator.name" with the version stripped — VPB's version-family key.</summary>
        public string Family { get; init; } = "";
        public int Version { get; init; }
        /// <summary>VPB's winner flag within <see cref="Family"/>. Unparseable uids are flagged newest.</summary>
        public bool IsNewest { get; init; }
        /// <summary>Sitting in AddonPackages (VaM loads it) rather than AllPackages.</summary>
        public bool IsLoaded { get; init; }
        /// <summary>VPB found no gallery category for this package at all.</summary>
        public bool HasNoCategory { get; init; }
        public long SizeBytes { get; init; }
        /// <summary>DateTime.ToBinary of the var's last write time.</summary>
        public long LastWriteBinary { get; init; }
        /// <summary>DateTime.ToBinary of when VPB first indexed this package.</summary>
        public long FirstScannedBinary { get; init; }
    }

    /// <summary>A row of pkg_insight. Bitmask columns are carried raw — flag definitions were not in the VPB source this reader was written against.</summary>
    public sealed class VpbPackageInsightRow
    {
        public string Uid { get; init; } = "";
        public long IssueFlags { get; init; }
        public long RiskFlags { get; init; }
        public int MorphCount { get; init; }
        public int ScriptCount { get; init; }
        public int DllCount { get; init; }
        public int BundleCount { get; init; }
        /// <summary>Dependencies VPB resolved from content but that meta.json never declared.</summary>
        public string Undeclared { get; init; } = "";

        public bool HasExecutableContent => DllCount > 0 || ScriptCount > 0;
    }

    /// <summary>Per-package gallery category tallies, e.g. "Clothing" to 42. Category names are VPB's.</summary>
    public sealed class VpbCategoryCounts
    {
        public string Uid { get; init; } = "";
        public Dictionary<string, int> CountsByCategory { get; init; } = new();

        public int For(string category) =>
            category != null && CountsByCategory.TryGetValue(category, out var n) ? n : 0;
    }
}
