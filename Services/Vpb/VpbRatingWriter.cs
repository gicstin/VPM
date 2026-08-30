using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VPM.Services.Vpb
{
    /// <summary>One package's new star rating. <see cref="Stars"/> of 0 clears it.</summary>
    public sealed class PackageRatingChange
    {
        /// <summary>Package uid, "Creator.Name.Version".</summary>
        public string Uid { get; init; } = "";

        /// <summary>Where the var actually lives, relative to the VaM root ("AddonPackages/x.var"). Only picks which of the two equivalent key spellings to write; empty is fine.</summary>
        public string VarPathRelative { get; init; } = "";

        public int Stars { get; init; }
    }

    /// <summary>One loose Custom/Saves file's new star rating. <see cref="Stars"/> of 0 clears it.</summary>
    public sealed class FileRatingChange
    {
        /// <summary>VaM-relative path with forward slashes. This is the ratings.json key itself.</summary>
        public string RelativePath { get; init; } = "";

        public int Stars { get; init; }
    }

    /// <summary>Writes package-level keys into VPB's ratings.json. Never deletes per-file ratings, so stars can remain after a package clear. tmp/bak rotation matches VPB's RatingsManager.Save.</summary>
    public static class VpbRatingWriter
    {
        /// <summary>VPB loads ratings.json once and never re-reads it, so a write while VaM is running is lost the next time VPB saves that session.</summary>
        public static bool WritesAreVolatileRightNow() => VpbPresence.IsVaMRunning();

        public static VpbWriteResult SetRatings(string vamRoot, IReadOnlyCollection<PackageRatingChange> changes)
        {
            if (string.IsNullOrWhiteSpace(vamRoot)) return VpbWriteResult.Failed("No VaM folder selected.");
            if (changes == null || changes.Count == 0) return VpbWriteResult.Ok(0);

            var path = VpbPaths.RatingsPath(vamRoot);
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return VpbWriteResult.Failed("Could not resolve VPB's plugin data folder.");

            try
            {
                // Re-read immediately before writing: VPB may have saved since the grid was populated, and this file holds per-file ratings VPM never displays but must not drop.
                var ratings = VpbRatingsStore.LoadRawRatings(vamRoot);

                int changed = 0;
                foreach (var change in changes)
                {
                    if (change == null || string.IsNullOrEmpty(change.Uid)) continue;
                    if (ApplyOne(ratings, change)) changed++;
                }

                if (changed == 0) return VpbWriteResult.Ok(0);

                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                if (!TryWriteAtomically(path, ratings, out var error))
                    return VpbWriteResult.Failed(error);

                return VpbWriteResult.Ok(changed);
            }
            catch (Exception ex)
            {
                return VpbWriteResult.Failed("Could not update VPB ratings: " + ex.Message);
            }
        }

        public static VpbWriteResult SetFileRatings(string vamRoot, IReadOnlyCollection<FileRatingChange> changes)
        {
            if (string.IsNullOrWhiteSpace(vamRoot)) return VpbWriteResult.Failed("No VaM folder selected.");
            if (changes == null || changes.Count == 0) return VpbWriteResult.Ok(0);

            var path = VpbPaths.RatingsPath(vamRoot);
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return VpbWriteResult.Failed("Could not resolve VPB's plugin data folder.");

            try
            {
                var ratings = VpbRatingsStore.LoadRawRatings(vamRoot);

                int changed = 0;
                foreach (var change in changes)
                {
                    if (change == null) continue;
                    if (ApplyFile(ratings, change)) changed++;
                }

                if (changed == 0) return VpbWriteResult.Ok(0);

                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                if (!TryWriteAtomically(path, ratings, out var error))
                    return VpbWriteResult.Failed(error);

                return VpbWriteResult.Ok(changed);
            }
            catch (Exception ex)
            {
                return VpbWriteResult.Failed("Could not update VPB ratings: " + ex.Message);
            }
        }

        /// <summary>Package-level keys VPB accepts for one package, most canonical first.</summary>
        private static List<string> PackageLevelKeys(PackageRatingChange change)
        {
            var uid = change.Uid;
            var keys = new List<string>(3);

            var relative = (change.VarPathRelative ?? "").Replace('\\', '/').Trim();
            if (relative.Length > 0 && relative.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                keys.Add(relative);

            const string addon = "AddonPackages/";
            const string all = "AllPackages/";
            foreach (var prefix in new[] { addon, all })
            {
                var key = prefix + uid + ".var";
                if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) keys.Add(key);
            }

            // Some entries were written as a bare uid before VPB settled on path keys.
            keys.Add(uid);
            return keys;
        }

        private static bool ApplyOne(Dictionary<string, int> ratings, PackageRatingChange change)
        {
            var keys = PackageLevelKeys(change);
            var stars = change.Stars < 0 ? 0 : change.Stars > 5 ? 5 : change.Stars;

            bool changed = false;
            foreach (var key in keys)
            {
                if (ratings.Remove(key)) changed = true;
            }

            if (stars > 0)
            {
                // keys[0] is the real on-disk location when the caller knew it.
                ratings[keys[0]] = stars;
                changed = true;
            }

            return changed;
        }

        /// <summary>Writes the relative path key only. Package-level AddonPackages/ keys would be wrong here — these files are not vars.</summary>
        private static bool ApplyFile(Dictionary<string, int> ratings, FileRatingChange change)
        {
            var relative = VpbUserFileKeys.NormalizePath(change.RelativePath);
            if (relative.Length == 0) return false;
            if (VpbRatingsStore.PackageUidFromRatingKey(relative).Length > 0) return false;

            var stars = change.Stars < 0 ? 0 : change.Stars > 5 ? 5 : change.Stars;
            var backslash = relative.Replace('/', '\\');

            bool changed = ratings.Remove(relative);
            if (backslash != relative) changed |= ratings.Remove(backslash);

            if (stars > 0)
            {
                ratings[relative] = stars;
                changed = true;
            }

            return changed;
        }

        private static bool TryWriteAtomically(string path, Dictionary<string, int> ratings, out string error)
        {
            error = "";
            var tmpPath = path + ".tmp";
            var backupPath = path + ".bak";

            try
            {
                var json = Serialize(ratings);
                if (json.Length < 2)
                {
                    error = "Refusing to write an empty ratings file.";
                    return false;
                }

                File.WriteAllText(tmpPath, json, new UTF8Encoding(false));

                // Never rotate a file we failed to write in full.
                var written = new FileInfo(tmpPath);
                if (!written.Exists || written.Length < 2)
                {
                    error = "Temporary ratings file was not written.";
                    return false;
                }

                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > 2)
                    {
                        try
                        {
                            if (File.Exists(backupPath)) File.Delete(backupPath);
                            File.Move(path, backupPath);
                        }
                        catch
                        {
                            // Backup is best effort; the verified .tmp is still the safe copy.
                            File.Delete(path);
                        }
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }

                File.Move(tmpPath, path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                return false;
            }
        }

        /// <summary>Emits the exact shape VPB deserializes: { "ratings": [ { "uid", "rating" } ] }.</summary>
        private static string Serialize(Dictionary<string, int> ratings)
        {
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("ratings");
                foreach (var kvp in ratings)
                {
                    if (string.IsNullOrEmpty(kvp.Key) || kvp.Value <= 0) continue;
                    writer.WriteStartObject();
                    writer.WriteString("uid", kvp.Key);
                    writer.WriteNumber("rating", kvp.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
