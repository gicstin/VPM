using System;
using System.Collections.Generic;

namespace VPM.Services.Vpb
{
    public sealed class VpbDataPackRow
    {
        public string PackId { get; init; } = "";
        public string PackVersion { get; init; } = "";
        public string BuiltDate { get; init; } = "";
        public string Attribution { get; init; } = "";
        public string SourceUrl { get; init; } = "";
        public int EntryCount { get; init; }

        public bool IsLookapedia =>
            string.Equals(PackId, VpbDataPackIds.Lookapedia, StringComparison.OrdinalIgnoreCase);
    }

    public static class VpbDataPackIds
    {
        public const string Lookapedia = "lookapedia";
        public const string HubTags = "hubtags";
        public const string HubLive = "hublive";

        public const int MatchKindCategoryOnly = 4;
    }

    public sealed class VpbLookRow
    {
        public string Subject { get; internal set; } = "";
        public string Category { get; internal set; } = "";
        public string HubCategory { get; internal set; } = "";
        public IReadOnlyList<string> HubTags { get; internal set; } = Array.Empty<string>();

        public IReadOnlyList<string> HiddenHubTags { get; internal set; } = Array.Empty<string>();

        public bool HasAnything =>
            Subject.Length > 0 || Category.Length > 0 || HubCategory.Length > 0
            || HubTags.Count > 0 || HiddenHubTags.Count > 0;
    }

    public sealed class VpbHubTagPrefs
    {
        public static VpbHubTagPrefs Empty { get; } = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));

        private readonly HashSet<string> _global;
        private readonly Dictionary<string, HashSet<string>> _byPackage;

        public VpbHubTagPrefs(HashSet<string> global, Dictionary<string, HashSet<string>> byPackage)
        {
            _global = global ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _byPackage = byPackage ?? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyCollection<string> GloballyHidden => _global;

        public int GlobalCount => _global.Count;

        public int PackageRuleCount
        {
            get
            {
                var n = 0;
                foreach (var kvp in _byPackage) n += kvp.Value.Count;
                return n;
            }
        }

        public bool Any => _global.Count > 0 || _byPackage.Count > 0;

        public bool IsHiddenGlobally(string tag) =>
            !string.IsNullOrEmpty(tag) && _global.Contains(tag);

        public bool IsHiddenOnPackage(string uid, string tag) =>
            !string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(tag)
            && _byPackage.TryGetValue(uid, out var set) && set.Contains(tag);

        public bool IsHidden(string uid, string tag) =>
            IsHiddenGlobally(tag) || IsHiddenOnPackage(uid, tag);
    }
}
