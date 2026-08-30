using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VPM.Services
{
    /// <summary>What kind of content is being discarded. Decides which VPB folder it lands in.</summary>
    public enum DiscardKind
    {
        /// <summary>.var packages — DeletedPackages.</summary>
        Package,
        /// <summary>Saves/scene JSON and its previews — DeletedScenes.</summary>
        Scene,
        /// <summary>Local presets / Custom assets (appearance, clothing, hair, subscenes) — DeletedPresets.</summary>
        Preset
    }

    /// <summary>VaM-root discard folders shared with VPB. VPM writes DeletedPackages/Scenes/Presets; legacy DiscardedPackages is read-only fallback.</summary>
    public static class DiscardPaths
    {
        /// <summary>Discarded .var packages. VPB: GalleryPanel.Toolbox.DeletePackages.</summary>
        public const string DeletedPackages = "DeletedPackages";

        /// <summary>Discarded local scenes (Saves/scene JSON + preview/fav/hide sidecars).</summary>
        public const string DeletedScenes = "DeletedScenes";

        /// <summary>Discarded local presets and Custom assets.</summary>
        public const string DeletedPresets = "DeletedPresets";

        /// <summary>Packages the VaM/VPB FileManager hook rejected at load time.</summary>
        public const string InvalidPackages = "InvalidPackages";

        /// <summary>Pre-consolidation VPM discard folder: packages, scenes and presets mixed flat.</summary>
        public const string LegacyDiscardedPackages = "DiscardedPackages";

        /// <summary>Even older VPM/VaM-tool naming seen in the wild.</summary>
        public const string LegacyDeletedVars = "DeletedVars";

        /// <summary>Archive root. Archiving is "keep but hide", not "discard" — kept separate.</summary>
        public const string ArchivedPackages = "ArchivedPackages";

        /// <summary>VPM's archive bucket for superseded versions: ArchivedPackages/OldPackages.</summary>
        public const string ArchivedOldPackages = "OldPackages";

        /// <summary>Superseded versions. VPB cleanup writes DeletedPackages/OldVersions.</summary>
        public const string BucketOldVersions = "OldVersions";

        /// <summary>Same package present twice. VPB cleanup bucket.</summary>
        public const string BucketDuplicates = "Duplicates";

        /// <summary>Unreadable/corrupt content. VPB cleanup bucket.</summary>
        public const string BucketDamaged = "Damaged";

        /// <summary>Orphaned texture cache. VPB cleanup bucket.</summary>
        public const string BucketStaleCache = "StaleCache";

        /// <summary>Buckets the VaM/VPB hook creates under InvalidPackages.</summary>
        public static readonly string[] InvalidPackagesBuckets =
        {
            "InvalidName", "InvalidZip", "Duplicated", "OldVersion"
        };

        public static string FolderNameFor(DiscardKind kind) => kind switch
        {
            DiscardKind.Scene => DeletedScenes,
            DiscardKind.Preset => DeletedPresets,
            _ => DeletedPackages
        };

        /// <summary>Full path VPM discards <paramref name="kind"/> into, optionally under a reason bucket (<see cref="BucketOldVersions"/> and friends). Does not create anything.</summary>
        public static string GetDiscardFolder(string vamRoot, DiscardKind kind, string bucket = null)
        {
            if (string.IsNullOrEmpty(vamRoot))
                return null;

            var path = Path.Combine(vamRoot, FolderNameFor(kind));
            return string.IsNullOrEmpty(bucket) ? path : Path.Combine(path, bucket);
        }

        /// <summary><see cref="GetDiscardFolder"/> plus directory creation. Returns null when the root is unknown or the folder cannot be created.</summary>
        public static string EnsureDiscardFolder(string vamRoot, DiscardKind kind, string bucket = null)
        {
            var path = GetDiscardFolder(vamRoot, kind, bucket);
            if (string.IsNullOrEmpty(path))
                return null;

            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiscardPaths] Could not create '{path}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Older folders that may still hold content of this kind, newest convention first. Read-only: VPM never moves new files here, but still looks here when locating or opening what the user discarded before the consolidation.</summary>
        public static IReadOnlyList<string> GetLegacyFolders(string vamRoot, DiscardKind kind)
        {
            if (string.IsNullOrEmpty(vamRoot))
                return Array.Empty<string>();

            var legacy = new List<string> { Path.Combine(vamRoot, LegacyDiscardedPackages) };
            if (kind == DiscardKind.Package)
                legacy.Add(Path.Combine(vamRoot, LegacyDeletedVars));

            return legacy;
        }

        /// <summary>Every folder in the VaM root where content can end up "removed", current and legacy, VPM-written and hook-written. Only folders that exist are returned.</summary>
        public static IReadOnlyList<string> GetExistingDiscardLocations(string vamRoot)
        {
            if (string.IsNullOrEmpty(vamRoot))
                return Array.Empty<string>();

            var candidates = new[]
            {
                DeletedPackages,
                DeletedScenes,
                DeletedPresets,
                InvalidPackages,
                LegacyDiscardedPackages,
                LegacyDeletedVars
            };

            var found = new List<string>();
            foreach (var name in candidates)
            {
                var path = Path.Combine(vamRoot, name);
                try
                {
                    if (Directory.Exists(path))
                        found.Add(path);
                }
                catch
                {
                }
            }

            return found;
        }

        /// <summary>Folder to show the user for this kind: the VPB folder when it has content, otherwise the first legacy folder that still has files in it, otherwise the VPB folder (created).</summary>
        public static string ResolveFolderToOpen(string vamRoot, DiscardKind kind)
        {
            if (string.IsNullOrEmpty(vamRoot))
                return null;

            var current = GetDiscardFolder(vamRoot, kind);
            if (DirectoryHasEntries(current))
                return current;

            foreach (var legacy in GetLegacyFolders(vamRoot, kind))
            {
                if (DirectoryHasEntries(legacy))
                    return legacy;
            }

            return EnsureDiscardFolder(vamRoot, kind) ?? current;
        }

        /// <summary>True when the path sits inside any discard/quarantine folder, current or legacy.</summary>
        public static bool IsInsideDiscardLocation(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            var normalized = path.Replace('/', Path.DirectorySeparatorChar);
            var sep = Path.DirectorySeparatorChar;

            var names = new[]
            {
                DeletedPackages, DeletedScenes, DeletedPresets,
                InvalidPackages, LegacyDiscardedPackages, LegacyDeletedVars
            };

            foreach (var name in names)
            {
                if (normalized.IndexOf($"{sep}{name}{sep}", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>Non-colliding destination inside <paramref name="destDir"/>, matching VPB's scheme: "name__yyyyMMdd_HHmmss_fff.ext", so a file discarded twice keeps both copies and the suffix says when. Falls back to a GUID if even that collides.</summary>
        public static string PickUniqueDestinationPath(string destDir, string fileName)
        {
            var dest = Path.Combine(destDir, fileName);
            if (!File.Exists(dest))
                return dest;

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

            for (int attempt = 0; attempt < 512; attempt++)
            {
                var suffix = attempt == 0 ? stamp : $"{stamp}_{attempt}";
                dest = Path.Combine(destDir, $"{baseName}__{suffix}{ext}");
                if (!File.Exists(dest))
                    return dest;
            }

            return Path.Combine(destDir, $"{baseName}__{Guid.NewGuid():N}{ext}");
        }

        /// <summary>Outcome of <see cref="MigrateLegacyVarDiscards"/>.</summary>
        public readonly struct LegacyMigrationResult
        {
            public LegacyMigrationResult(int moved, int leftBehind, int failed)
            {
                Moved = moved;
                LeftBehind = leftBehind;
                Failed = failed;
            }

            /// <summary>.var files relocated into DeletedPackages.</summary>
            public int Moved { get; }

            /// <summary>Non-.var files deliberately left where they are (scenes, presets, previews).</summary>
            public int LeftBehind { get; }

            /// <summary>.var files that could not be moved (locked, permissions).</summary>
            public int Failed { get; }

            public bool MovedAnything => Moved > 0;
        }

        /// <summary>Move .var files from legacy flat discard folders into DeletedPackages. Leaves scenes/presets — both are .json/.vap so extension cannot tell them apart. Idempotent; collisions get the VPB timestamp suffix.</summary>
        public static LegacyMigrationResult MigrateLegacyVarDiscards(string vamRoot)
        {
            int moved = 0, leftBehind = 0, failed = 0;

            if (string.IsNullOrEmpty(vamRoot))
                return new LegacyMigrationResult(0, 0, 0);

            foreach (var legacyRoot in GetLegacyFolders(vamRoot, DiscardKind.Package))
            {
                if (!DirectoryHasEntries(legacyRoot))
                    continue;

                List<string> files;
                try
                {
                    files = Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories).ToList();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DiscardPaths] Cannot enumerate '{legacyRoot}': {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    if (!file.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                    {
                        leftBehind++;
                        continue;
                    }

                    try
                    {
                        var relativeDir = Path.GetDirectoryName(Path.GetRelativePath(legacyRoot, file));
                        var targetDir = EnsureDiscardFolder(vamRoot, DiscardKind.Package, relativeDir);
                        if (string.IsNullOrEmpty(targetDir))
                        {
                            failed++;
                            continue;
                        }

                        File.Move(file, PickUniqueDestinationPath(targetDir, Path.GetFileName(file)));
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        System.Diagnostics.Debug.WriteLine($"[DiscardPaths] Could not migrate '{file}': {ex.Message}");
                    }
                }

                PruneEmptyDirectories(legacyRoot);
            }

            if (moved > 0 || failed > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DiscardPaths] Legacy discard migration: {moved} moved, {leftBehind} left in place, {failed} failed.");
            }

            return new LegacyMigrationResult(moved, leftBehind, failed);
        }

        /// <summary>Removes directories under (and including) <paramref name="root"/> that hold nothing.</summary>
        private static void PruneEmptyDirectories(string root)
        {
            try
            {
                if (!Directory.Exists(root))
                    return;

                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    TryDeleteIfEmpty(dir);
                }

                TryDeleteIfEmpty(root);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiscardPaths] Prune failed for '{root}': {ex.Message}");
            }
        }

        private static void TryDeleteIfEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch
            {
            }
        }

        private static bool DirectoryHasEntries(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
            }
            catch
            {
                return false;
            }
        }
    }
}
