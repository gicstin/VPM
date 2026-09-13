using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPM.Models;

namespace VPM.Services.Vpb
{
    public sealed class WhitelistCompileReport
    {
        public List<string> Unresolved { get; } = new();

        public List<string> Unreachable { get; } = new();

        public bool HasProblems => Unresolved.Count > 0 || Unreachable.Count > 0;
    }

    public static class WhitelistCompiler
    {
        public static HashSet<string> ResolveClosure(
            IEnumerable<string> roots,
            DependencyGraph graph,
            VpbPackageKeyIndex index = null,
            WhitelistCompileReport report = null)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots == null) return result;

            var queue = new Queue<string>();
            foreach (var root in roots)
            {
                var uid = index != null ? index.ResolveUid(root) : VpbUid.Canonical(root);
                if (uid == null)
                {
                    var raw = VpbUid.Canonical(root);
                    if (raw != null)
                    {
                        report?.Unresolved.Add(raw);
                        result.Add(raw);
                    }
                    continue;
                }
                queue.Enqueue(uid);
            }

            while (queue.Count > 0)
            {
                var key = queue.Dequeue();
                if (!result.Add(key)) continue;
                if (graph == null) continue;
                foreach (var dep in graph.GetDependencies(key) ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(dep)) continue;
                    var depUid = index != null ? index.ResolveUid(dep) : VpbUid.Canonical(dep);
                    if (depUid != null)
                        queue.Enqueue(depUid);
                }
            }

            return result;
        }

        /// <summary>Builds the whitelist covering <paramref name="packageKeys"/>, either replacing the current one (<paramref name="exclusive"/>) or merging into it. Pass a null <paramref name="graph"/> to whitelist exactly the keys given without pulling their dependencies in.</summary>
        public static ScanWhitelistData Compile(
            IEnumerable<string> packageKeys,
            DependencyGraph graph,
            IReadOnlyDictionary<string, VarMetadata> metadata,
            string vamRoot,
            ScanWhitelistData current,
            bool exclusive,
            WhitelistCompileReport report = null)
        {
            var index = VpbPackageKeyIndex.Build(metadata);
            var closure = ResolveClosure(packageKeys, graph, index, report);
            var keysByFolder = BuildFolderIndex(metadata, vamRoot);

            var seeding = !exclusive && (current == null || !current.Enabled);
            if (seeding)
            {
                var seeded = 0;
                foreach (var uid in AddonPackageUids(metadata))
                {
                    closure.Add(uid);
                    seeded++;
                }

                if (seeded == 0)
                {
                    return current ?? new ScanWhitelistData
                    {
                        SchemaVersion = ScanWhitelistData.CurrentSchemaVersion,
                        Enabled = false
                    };
                }
            }

            var folders = new List<string>();
            var uids = new List<string>();
            CollapseToFoldersAndUids(closure, index, keysByFolder, vamRoot, folders, uids, report);

            if (exclusive || seeding)
            {
                return new ScanWhitelistData
                {
                    SchemaVersion = ScanWhitelistData.CurrentSchemaVersion,
                    Enabled = true,
                    WhitelistedFolders = folders,
                    IncludedPackageUids = uids
                };
            }

            var merged = new ScanWhitelistData
            {
                SchemaVersion = ScanWhitelistData.CurrentSchemaVersion,
                Enabled = true,
                WhitelistedFolders = new List<string>(current.WhitelistedFolders ?? new List<string>()),
                IncludedPackageUids = (current.IncludedPackageUids ?? new List<string>())
                    .Select(VpbUid.Canonical)
                    .Where(u => u != null)
                    .ToList()
            };

            var haveFolders = new HashSet<string>(merged.WhitelistedFolders, StringComparer.OrdinalIgnoreCase);
            foreach (var f in folders)
            {
                if (haveFolders.Add(f))
                    merged.WhitelistedFolders.Add(f);
            }

            var haveUids = new HashSet<string>(merged.IncludedPackageUids, StringComparer.OrdinalIgnoreCase);
            foreach (var u in uids)
            {
                if (haveUids.Add(u))
                    merged.IncludedPackageUids.Add(u);
            }
            return merged;
        }

        public static ScanWhitelistData ExcludeUids(
            IEnumerable<string> packageKeys,
            IReadOnlyDictionary<string, VarMetadata> metadata,
            string vamRoot,
            ScanWhitelistData current)
        {
            var remove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in packageKeys ?? Array.Empty<string>())
            {
                var uid = VpbUid.Canonical(key);
                if (uid != null) remove.Add(uid);
            }

            // Absent/disabled whitelist does not narrow — VaM boot-scans all of AddonPackages. First exclusion is what turns a real whitelist on.
            if (current == null || !current.Enabled)
            {
                var survivors = AddonPackageUids(metadata).Where(uid => !remove.Contains(uid)).ToList();
                // No graph: survivors only, no dependency expansion. Compile collapses untouched folders so this stays a small file.
                return Compile(survivors, null, metadata, vamRoot, null, exclusive: true);
            }

            var keysByFolder = BuildFolderIndex(metadata, vamRoot);
            var currentIndex = ScanWhitelistIndex.From(current);

            // Pass 1: which blanket folder entries survive untouched?
            var keepFolders = new List<string>();
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in current.WhitelistedFolders ?? new List<string>())
            {
                var norm = ScanWhitelistStore.NormalizeFolder(folder);
                if (string.IsNullOrEmpty(norm) || !seenFolders.Add(norm)) continue;

                var members = SubtreeMembers(keysByFolder, norm).ToList();
                if (members.Count == 1 && members[0] == NeverInClosure)
                {
                    // Nothing known lives there - metadata may simply not be loaded yet. Keep the entry rather than silently dropping the user's whitelist.
                    keepFolders.Add(norm);
                    continue;
                }

                // A folder that loses a member stops being a blanket entry; its survivors get pinned individually in pass 3.
                if (!members.Any(remove.Contains))
                    keepFolders.Add(norm);
            }

            var keptIndex = ScanWhitelistIndex.From(new ScanWhitelistData
            {
                Enabled = true,
                WhitelistedFolders = keepFolders,
                IncludedPackageUids = new List<string>()
            });

            // Pass 2: preserve explicit uid pins that are not being removed.
            var keepUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in current.IncludedPackageUids ?? new List<string>())
            {
                var uid = VpbUid.Canonical(u);
                if (uid != null && !remove.Contains(uid))
                    keepUids.Add(uid);
            }

            // Pass 3: pin every package that was in the scan set, survives the exclusion, and is no longer covered by a folder entry we kept.
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    var meta = kvp.Value;
                    if (meta == null) continue;
                    var uid = VpbUid.Canonical(kvp.Key);
                    if (uid == null || remove.Contains(uid)) continue;
                    if (!IsPathUnderAddon(meta.FilePath)) continue;

                    var rel = RelativeVarPath(meta.FilePath, vamRoot);
                    if (!currentIndex.Contains(rel, uid)) continue;
                    if (keptIndex.Contains(rel, null)) continue;
                    keepUids.Add(uid);
                }
            }

            return new ScanWhitelistData
            {
                SchemaVersion = ScanWhitelistData.CurrentSchemaVersion,
                Enabled = true,
                WhitelistedFolders = keepFolders,
                IncludedPackageUids = keepUids.ToList()
            };
        }

        private static IEnumerable<string> AddonPackageUids(IReadOnlyDictionary<string, VarMetadata> metadata)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (metadata == null) yield break;

            foreach (var kvp in metadata)
            {
                var meta = kvp.Value;
                if (meta == null || !IsPathUnderAddon(meta.FilePath)) continue;
                var uid = VpbUid.Canonical(kvp.Key);
                if (uid != null && seen.Add(uid))
                    yield return uid;
            }
        }

        private static Dictionary<string, HashSet<string>> BuildFolderIndex(
            IReadOnlyDictionary<string, VarMetadata> metadata,
            string vamRoot)
        {
            var byFolder = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (metadata == null) return byFolder;

            foreach (var kvp in metadata)
            {
                var folder = ScanWhitelistStore.FolderFromVarPath(kvp.Value?.FilePath, vamRoot);
                if (string.IsNullOrEmpty(folder)) continue;
                var uid = VpbUid.Canonical(kvp.Key);
                if (uid == null) continue;
                if (!byFolder.TryGetValue(folder, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    byFolder[folder] = set;
                }
                set.Add(uid);
            }
            return byFolder;
        }

        private static void CollapseToFoldersAndUids(
            HashSet<string> closure,
            VpbPackageKeyIndex index,
            Dictionary<string, HashSet<string>> keysByFolder,
            string vamRoot,
            List<string> folders,
            List<string> uids,
            WhitelistCompileReport report)
        {
            var closureByFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var uid in closure)
            {
                var meta = index?.Lookup(uid);
                if (meta == null)
                {
                    uids.Add(uid);
                    continue;
                }
                var folder = ScanWhitelistStore.FolderFromVarPath(meta.FilePath, vamRoot);
                if (string.IsNullOrEmpty(folder) || !folder.StartsWith("AddonPackages", StringComparison.OrdinalIgnoreCase))
                {
                    report?.Unreachable.Add(uid);
                    uids.Add(uid);
                    continue;
                }
                if (!closureByFolder.TryGetValue(folder, out var list))
                {
                    list = new List<string>();
                    closureByFolder[folder] = list;
                }
                list.Add(uid);
            }

            foreach (var kvp in closureByFolder)
            {
                if (SubtreeMembers(keysByFolder, kvp.Key).All(closure.Contains))
                    folders.Add(kvp.Key);
                else
                    uids.AddRange(kvp.Value);
            }
        }

        private static IEnumerable<string> SubtreeMembers(Dictionary<string, HashSet<string>> keysByFolder, string folder)
        {
            var any = false;
            foreach (var kvp in keysByFolder)
            {
                if (!CoversFolder(folder, kvp.Key)) continue;
                foreach (var uid in kvp.Value)
                {
                    any = true;
                    yield return uid;
                }
            }

            if (!any) yield return NeverInClosure;
        }

        private static bool CoversFolder(string folder, string candidate)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(candidate)) return false;
            if (string.Equals(folder, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return candidate.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }

        private const string NeverInClosure = " ";

        private static bool IsPathUnderAddon(string path) =>
            !string.IsNullOrEmpty(path) && path.Replace('\\', '/').Contains("AddonPackages/", StringComparison.OrdinalIgnoreCase);

        private static string RelativeVarPath(string path, string vamRoot)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(vamRoot)) return path?.Replace('\\', '/');
            try
            {
                var root = Path.GetFullPath(vamRoot).TrimEnd('\\', '/');
                var full = Path.GetFullPath(path);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
            }
            catch { }
            return path.Replace('\\', '/');
        }
    }
}
