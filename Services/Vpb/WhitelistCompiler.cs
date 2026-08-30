using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPM.Models;

namespace VPM.Services.Vpb
{
    public static class WhitelistCompiler
    {
        /// <summary>The roots plus everything they depend on. A null <paramref name="graph"/> means "do not walk dependencies" and yields the roots alone — that is how callers ask to include a package without dragging its whole tree in.</summary>
        public static HashSet<string> ResolveClosure(IEnumerable<string> roots, DependencyGraph graph)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots == null) return result;

            var queue = new Queue<string>();
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                queue.Enqueue(root.Trim());
            }

            while (queue.Count > 0)
            {
                var key = queue.Dequeue();
                if (!result.Add(key)) continue;
                if (graph == null) continue;
                foreach (var dep in graph.GetDependencies(key) ?? Enumerable.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(dep))
                        queue.Enqueue(dep);
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
            bool exclusive)
        {
            var closure = ResolveClosure(packageKeys, graph);
            var keysByFolder = BuildFolderIndex(metadata, vamRoot);
            var folders = new List<string>();
            var uids = new List<string>();

            CollapseToFoldersAndUids(closure, metadata, keysByFolder, vamRoot, folders, uids);

            if (exclusive)
            {
                return new ScanWhitelistData
                {
                    SchemaVersion = ScanWhitelistData.CurrentSchemaVersion,
                    Enabled = true,
                    WhitelistedFolders = folders,
                    IncludedPackageUids = uids
                };
            }

            var merged = current != null
                ? new ScanWhitelistData
                {
                    SchemaVersion = ScanWhitelistData.CurrentSchemaVersion,
                    Enabled = true,
                    WhitelistedFolders = new List<string>(current.WhitelistedFolders ?? new List<string>()),
                    IncludedPackageUids = new List<string>(current.IncludedPackageUids ?? new List<string>())
                }
                : new ScanWhitelistData { Enabled = true };

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
            var remove = new HashSet<string>(packageKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            // Absent/disabled whitelist does not narrow — VaM boot-scans all of AddonPackages. First exclusion is what turns a real whitelist on.
            if (current == null || !current.Enabled)
            {
                var survivors = new List<string>();
                if (metadata != null)
                {
                    foreach (var kvp in metadata)
                    {
                        var meta = kvp.Value;
                        if (meta == null || remove.Contains(kvp.Key)) continue;
                        if (!IsPathUnderAddon(meta.FilePath)) continue;
                        survivors.Add(kvp.Key);
                    }
                }
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

                if (!keysByFolder.TryGetValue(norm, out var members) || members.Count == 0)
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
                if (!remove.Contains(u))
                    keepUids.Add(u);
            }

            // Pass 3: pin every package that was in the scan set, survives the exclusion, and is no longer covered by a folder entry we kept.
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    var meta = kvp.Value;
                    if (meta == null || remove.Contains(kvp.Key)) continue;
                    if (!IsPathUnderAddon(meta.FilePath)) continue;

                    var rel = RelativeVarPath(meta.FilePath, vamRoot);
                    if (!currentIndex.Contains(rel, kvp.Key)) continue;
                    if (keptIndex.Contains(rel, null)) continue;
                    keepUids.Add(kvp.Key);
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

        /// <summary>Normalized folder path → metadata keys living in it. Built once per compile so membership is a hash lookup.</summary>
        private static Dictionary<string, List<string>> BuildFolderIndex(
            IReadOnlyDictionary<string, VarMetadata> metadata,
            string vamRoot)
        {
            var byFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (metadata == null) return byFolder;

            foreach (var kvp in metadata)
            {
                var folder = ScanWhitelistStore.FolderFromVarPath(kvp.Value?.FilePath, vamRoot);
                if (string.IsNullOrEmpty(folder)) continue;
                if (!byFolder.TryGetValue(folder, out var list))
                {
                    list = new List<string>();
                    byFolder[folder] = list;
                }
                list.Add(kvp.Key);
            }
            return byFolder;
        }

        private static void CollapseToFoldersAndUids(
            HashSet<string> closure,
            IReadOnlyDictionary<string, VarMetadata> metadata,
            Dictionary<string, List<string>> keysByFolder,
            string vamRoot,
            List<string> folders,
            List<string> uids)
        {
            var closureByFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in closure)
            {
                if (metadata == null || !metadata.TryGetValue(key, out var meta) || meta == null)
                {
                    uids.Add(key);
                    continue;
                }
                var folder = ScanWhitelistStore.FolderFromVarPath(meta.FilePath, vamRoot);
                if (string.IsNullOrEmpty(folder) || !folder.StartsWith("AddonPackages", StringComparison.OrdinalIgnoreCase))
                {
                    uids.Add(key);
                    continue;
                }
                if (!closureByFolder.TryGetValue(folder, out var list))
                {
                    list = new List<string>();
                    closureByFolder[folder] = list;
                }
                list.Add(key);
            }

            foreach (var kvp in closureByFolder)
            {
                // Collapse to a single folder entry only when every package that lives there is in the closure; otherwise pin the closure members by uid.
                if (keysByFolder.TryGetValue(kvp.Key, out var allInFolder)
                    && allInFolder.Count > 0
                    && allInFolder.All(closure.Contains))
                {
                    folders.Add(kvp.Key);
                }
                else
                {
                    uids.AddRange(kvp.Value);
                }
            }
        }

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
