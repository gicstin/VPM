using System;
using System.Collections.Generic;
using System.Text;
using VPM.Models;

namespace VPM.Services.Vpb
{
    public sealed class VpbLookData
    {
        private sealed class Entry
        {
            public string Subject = "";
            public string Category = "";
            public string HubCategory = "";
            public string TagsDisplay = "";
            public IReadOnlyList<string> HubTags = Array.Empty<string>();
            public IReadOnlyList<string> HiddenHubTags = Array.Empty<string>();

            public string IdentityBlob = "";
            public string TagBlob = "";
        }

        private static readonly Dictionary<string, Entry> NoEntries =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Entry> _entries;

        public static VpbLookData Empty { get; } =
            new(NoEntries, new List<VpbDataPackRow>(), VpbHubTagPrefs.Empty, hubTagsEnabled: true);

        private VpbLookData(
            Dictionary<string, Entry> entries,
            List<VpbDataPackRow> packs,
            VpbHubTagPrefs tagPrefs,
            bool hubTagsEnabled)
        {
            _entries = entries;
            Packs = packs;
            TagPrefs = tagPrefs ?? VpbHubTagPrefs.Empty;
            HubTagsEnabled = hubTagsEnabled;
        }

        public VpbHubTagPrefs TagPrefs { get; }

        public bool HubTagsEnabled { get; }

        public IReadOnlyList<VpbDataPackRow> Packs { get; }

        public int CoveredPackageCount => _entries.Count;

        public bool HasAnything => _entries.Count > 0;

        public bool HasLookapediaPack
        {
            get
            {
                foreach (var pack in Packs)
                {
                    if (pack.IsLookapedia) return true;
                }
                return false;
            }
        }

        public string ProvenanceBlock()
        {
            var sb = new StringBuilder();
            foreach (var pack in Packs)
            {
                if (pack.PackId.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(PackDisplayName(pack.PackId));

                var build = pack.PackVersion.Length > 0 ? pack.PackVersion : pack.BuiltDate;
                if (build.Length > 0) sb.Append(' ').Append(build);
                if (pack.EntryCount > 0) sb.Append(", ").Append(pack.EntryCount.ToString("N0")).Append(" entries");
            }
            return sb.ToString();
        }

        private static string PackDisplayName(string packId)
        {
            if (string.Equals(packId, VpbDataPackIds.Lookapedia, StringComparison.OrdinalIgnoreCase))
                return "Look-A-Pedia";
            if (string.Equals(packId, VpbDataPackIds.HubTags, StringComparison.OrdinalIgnoreCase))
                return "Hub tags";
            if (string.Equals(packId, VpbDataPackIds.HubLive, StringComparison.OrdinalIgnoreCase))
                return "Hub live";
            return packId;
        }

        public string AttributionLine()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (var pack in Packs)
            {
                if (pack.Attribution.Length == 0 || !seen.Add(pack.Attribution)) continue;
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(pack.Attribution);
            }
            return sb.ToString();
        }

        public string SubjectFor(string uid) =>
            TryEntry(uid, out var e) ? e.Subject : "";

        public string CategoryFor(string uid) =>
            TryEntry(uid, out var e) ? e.Category : "";

        public string HubCategoryFor(string uid) =>
            TryEntry(uid, out var e) ? e.HubCategory : "";

        public string HubTagsDisplayFor(string uid) =>
            TryEntry(uid, out var e) ? e.TagsDisplay : "";

        public IReadOnlyList<string> HubTagsFor(string uid) =>
            TryEntry(uid, out var e) ? e.HubTags : Array.Empty<string>();

        public IReadOnlyList<string> HiddenHubTagsFor(string uid) =>
            TryEntry(uid, out var e) ? e.HiddenHubTags : Array.Empty<string>();

        public bool HasHubTag(string uid, string tag)
        {
            if (string.IsNullOrEmpty(tag) || !TryEntry(uid, out var e)) return false;
            for (int i = 0; i < e.HubTags.Count; i++)
            {
                if (string.Equals(e.HubTags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public string DetailsFor(string uid)
        {
            if (!TryEntry(uid, out var e)) return "";
            var sb = new StringBuilder();
            if (e.Subject.Length > 0) sb.Append("Looks like: ").Append(e.Subject);
            if (e.Category.Length > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("Look-A-Pedia category: ").Append(e.Category);
            }
            if (e.HubCategory.Length > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("Hub category: ").Append(e.HubCategory);
            }
            if (e.TagsDisplay.Length > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("Hub tags: ").Append(e.TagsDisplay);
            }
            return sb.ToString();
        }

        public bool MatchesTerm(string uid, string term, bool includeTags)
        {
            if (string.IsNullOrEmpty(term)) return true;
            if (!TryEntry(uid, out var e)) return false;
            if (e.IdentityBlob.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return includeTags && e.TagBlob.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool TryEntry(string uid, out Entry entry)
        {
            if (!string.IsNullOrEmpty(uid) && _entries.TryGetValue(uid, out entry)) return true;
            entry = null;
            return false;
        }

        public static string UidFor(VarMetadata metadata) => VpbLibraryData.UidFor(metadata);

        /// <summary>With <paramref name="hubTagsEnabled"/> false the hub tags are dropped on load, so nothing downstream shows or filters on them.</summary>
        public static VpbLookData Load(string vamRoot, bool hubTagsEnabled)
        {
            if (string.IsNullOrWhiteSpace(vamRoot)) return Empty;

            try
            {
                using var db = new VpbLocalDbReader(vamRoot);
                if (!db.IsAvailable) return Empty;

                var packs = db.LoadDataPacks();
                var rows = db.LoadDataPackLookRows();
                var prefs = db.LoadHubTagPrefs();
                if (packs.Count == 0 && rows.Count == 0) return Empty;

                var entries = new Dictionary<string, Entry>(rows.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in rows)
                {
                    var row = kvp.Value;
                    var hubTags = hubTagsEnabled ? row.HubTags : Array.Empty<string>();
                    var entry = new Entry
                    {
                        Subject = row.Subject,
                        Category = row.Category,
                        HubCategory = row.HubCategory,
                        TagsDisplay = string.Join(", ", hubTags),
                        HubTags = hubTags,
                        HiddenHubTags = hubTagsEnabled ? row.HiddenHubTags : Array.Empty<string>(),
                        IdentityBlob = BuildBlob(row.Subject, row.Category, row.HubCategory),
                        TagBlob = string.Join(" ", hubTags).ToLowerInvariant()
                    };
                    entries[kvp.Key] = entry;
                }

                return new VpbLookData(entries, packs, prefs, hubTagsEnabled);
            }
            catch
            {
                return Empty;
            }
        }

        private static string BuildBlob(params string[] parts)
        {
            var sb = new StringBuilder(64);
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(part);
            }
            return sb.ToString().ToLowerInvariant();
        }
    }
}
