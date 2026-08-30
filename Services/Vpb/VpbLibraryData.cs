using System;
using System.Collections.Generic;
using System.Linq;
using VPM.Models;

namespace VPM.Services.Vpb
{
    /// <summary>VPB ratings and tags keyed by package uid. Rebuild on refresh; heavier index tables stay on <see cref="VpbLocalDbReader"/>.</summary>
    public sealed class VpbLibraryData
    {
        private static readonly Dictionary<string, int> NoRatings =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<string>> NoTags =
            new(StringComparer.OrdinalIgnoreCase);

        public static VpbLibraryData Empty { get; } = new(NoRatings, NoRatings, NoTags, NoRatings, NoTags, 0, false);

        private readonly Dictionary<string, int> _ratings;
        private readonly Dictionary<string, int> _packageLevelRatings;
        private readonly Dictionary<string, HashSet<string>> _tags;
        private readonly Dictionary<string, int> _fileRatings;
        private readonly Dictionary<string, HashSet<string>> _fileTags;

        private VpbLibraryData(
            Dictionary<string, int> ratings,
            Dictionary<string, int> packageLevelRatings,
            Dictionary<string, HashSet<string>> tags,
            Dictionary<string, int> fileRatings,
            Dictionary<string, HashSet<string>> fileTags,
            int schemaVersion,
            bool databaseAvailable)
        {
            _ratings = ratings;
            _packageLevelRatings = packageLevelRatings;
            _tags = tags;
            _fileRatings = fileRatings;
            _fileTags = fileTags;
            SchemaVersion = schemaVersion;
            DatabaseAvailable = databaseAvailable;

            var vocabulary = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in tags.Values)
            {
                foreach (var tag in set) vocabulary.Add(tag);
            }
            foreach (var set in fileTags.Values)
            {
                foreach (var tag in set) vocabulary.Add(tag);
            }
            AllTags = vocabulary.ToList();
        }

        /// <summary>VPB's SQLite index opened successfully. Ratings can still exist without it.</summary>
        public bool DatabaseAvailable { get; }

        /// <summary>meta.schema_version of the database VPB wrote, 0 when unavailable.</summary>
        public int SchemaVersion { get; }

        /// <summary>Every tag name in use anywhere in the library, sorted, for the filter list.</summary>
        public IReadOnlyList<string> AllTags { get; }

        public int RatedPackageCount => _ratings.Count;

        public bool HasAnything =>
            _ratings.Count > 0 || _tags.Count > 0 || _fileRatings.Count > 0 || _fileTags.Count > 0;

        /// <summary>What VPB shows for the package: the best rating on it or on anything inside it.</summary>
        public int RatingFor(string uid) =>
            !string.IsNullOrEmpty(uid) && _ratings.TryGetValue(uid, out var r) ? r : 0;

        /// <summary>Rating on a loose Custom/Saves file, keyed by vam-relative path.</summary>
        public int RatingForFile(string relativePath)
        {
            var key = VpbUserFileKeys.NormalizePath(relativePath);
            return key.Length > 0 && _fileRatings.TryGetValue(key, out var r) ? r : 0;
        }

        /// <summary>The rating set on the package itself, ignoring anything rated inside it.</summary>
        public int PackageLevelRatingFor(string uid) =>
            !string.IsNullOrEmpty(uid) && _packageLevelRatings.TryGetValue(uid, out var r) ? r : 0;

        /// <summary>True when the stars come from rated content inside the package rather than the package itself, which is why clearing the package rating can leave stars behind.</summary>
        public bool IsRatingInherited(string uid)
        {
            var shown = RatingFor(uid);
            return shown > 0 && PackageLevelRatingFor(uid) < shown;
        }

        public IReadOnlyCollection<string> TagsFor(string uid) =>
            !string.IsNullOrEmpty(uid) && _tags.TryGetValue(uid, out var t)
                ? (IReadOnlyCollection<string>)t
                : TagsForFile(uid);

        public IReadOnlyCollection<string> TagsForFile(string relativePath)
        {
            var key = VpbUserFileKeys.NormalizePath(relativePath);
            return key.Length > 0 && _fileTags.TryGetValue(key, out var t)
                ? (IReadOnlyCollection<string>)t
                : Array.Empty<string>();
        }

        public bool HasTag(string uid, string tag) =>
            (!string.IsNullOrEmpty(uid) && _tags.TryGetValue(uid, out var t) && t.Contains(tag))
            || HasTagOnFile(uid, tag);

        public bool HasTagOnFile(string relativePath, string tag)
        {
            var key = VpbUserFileKeys.NormalizePath(relativePath);
            return key.Length > 0 && _fileTags.TryGetValue(key, out var t) && t.Contains(tag);
        }

        /// <summary>Tags joined for display, alphabetical so a row's label is stable between refreshes.</summary>
        public string TagsDisplayFor(string uid)
        {
            if (string.IsNullOrEmpty(uid) || !_tags.TryGetValue(uid, out var t) || t.Count == 0)
                return "";
            var ordered = new List<string>(t);
            ordered.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(", ", ordered);
        }

        public string TagsDisplayForFile(string relativePath)
        {
            var tags = TagsForFile(relativePath);
            if (tags.Count == 0) return "";
            var ordered = new List<string>(tags);
            ordered.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(", ", ordered);
        }

        /// <summary>How many packages carry each tag, for the filter list counts.</summary>
        public Dictionary<string, int> TagUsageCounts()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in _tags.Values)
            {
                foreach (var tag in set)
                {
                    counts.TryGetValue(tag, out var n);
                    counts[tag] = n + 1;
                }
            }
            foreach (var set in _fileTags.Values)
            {
                foreach (var tag in set)
                {
                    counts.TryGetValue(tag, out var n);
                    counts[tag] = n + 1;
                }
            }
            return counts;
        }

        /// <summary>Uid VPB indexes a package under: "Creator.Name.Version". Taken from the file name first (VaM's VarPackage.Uid); meta.json CreatorName/PackageName can disagree.</summary>
        public static string UidFor(VarMetadata metadata)
        {
            if (metadata == null) return "";

            var filename = metadata.Filename ?? "";
            if (filename.Length > 0)
            {
                var slash = filename.LastIndexOfAny(new[] { '/', '\\' });
                if (slash >= 0) filename = filename.Substring(slash + 1);
                if (filename.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                    filename = filename.Substring(0, filename.Length - 4);
                if (filename.Length > 0) return filename;
            }

            var creator = metadata.CreatorName ?? "";
            var name = metadata.PackageName ?? "";
            if (creator.Length == 0 || name.Length == 0) return "";
            return creator + "." + name + "." + metadata.Version;
        }

        public static VpbLibraryData Load(string vamRoot)
        {
            if (string.IsNullOrWhiteSpace(vamRoot)) return Empty;

            var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var packageLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var fileRatings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var kvp in VpbRatingsStore.LoadRawRatings(vamRoot))
                {
                    if (kvp.Value <= 0) continue;
                    var uid = VpbRatingsStore.PackageUidFromRatingKey(kvp.Key);
                    if (uid.Length == 0)
                    {
                        var path = VpbUserFileKeys.NormalizePath(kvp.Key);
                        if (path.Length == 0) continue;
                        if (!fileRatings.TryGetValue(path, out var fileBest) || kvp.Value > fileBest)
                            fileRatings[path] = kvp.Value;
                        continue;
                    }

                    if (!ratings.TryGetValue(uid, out var best) || kvp.Value > best)
                        ratings[uid] = kvp.Value;

                    if (VpbRatingsStore.IsPackageLevelRatingKey(kvp.Key)
                        && (!packageLevel.TryGetValue(uid, out var own) || kvp.Value > own))
                        packageLevel[uid] = kvp.Value;
                }
            }
            catch { }

            var tags = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var fileTags = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var schemaVersion = 0;
            var available = false;
            try
            {
                using var db = new VpbLocalDbReader(vamRoot);
                if (db.IsAvailable)
                {
                    available = true;
                    schemaVersion = db.Schema.SchemaVersion;
                    foreach (var kvp in db.LoadPackageUserTags())
                        tags[kvp.Key] = kvp.Value;
                    foreach (var kvp in db.LoadUserFileUserTags())
                        fileTags[kvp.Key] = kvp.Value;
                }
            }
            catch { }

            if (ratings.Count == 0 && tags.Count == 0 && fileRatings.Count == 0 && fileTags.Count == 0 && !available)
                return Empty;
            return new VpbLibraryData(ratings, packageLevel, tags, fileRatings, fileTags, schemaVersion, available);
        }
    }
}
