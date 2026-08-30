using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace VPM.Services.Vpb
{
    /// <summary>One loose Custom/Saves file to tag. Never fan out by pkg_uid — that uid is empty.</summary>
    public sealed class UserFileTagTarget
    {
        /// <summary>VaM-relative path with forward slashes, matching VPB's FileEntry uid.</summary>
        public string RelativePath { get; init; } = "";

        /// <summary>VPB gallery category to use when cat_mem has no row for this file yet.</summary>
        public string Category { get; init; } = "Other";
    }

    /// <summary>Writes user tags into VPB SQLite. Tags gallery items, not packages — a package tag fans out across every cat_mem row it owns. VPB reads uncached, so writes show on its next refresh while VaM is running.</summary>
    public static class VpbTagWriter
    {
        /// <summary>Mirrors VPB's GalleryUserTagNameMaxLength.</summary>
        public const int TagNameMaxLength = 512;

        /// <summary>Mirrors VPB's GalleryUserTagMaxPerItem.</summary>
        public const int MaxTagsPerItem = 100;

        private const int BusyTimeoutMs = 8000;

        /// <summary>Mirrors VPB's NormalizeGalleryUserTagName. Returns "" for anything VPB would refuse.</summary>
        public static string NormalizeTagName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var s = raw.Trim().ToLowerInvariant();
            if (s.Length == 0 || s.Length > TagNameMaxLength) return "";
            foreach (var c in s)
            {
                if (c == '\0' || c == '\n' || c == '\r') return "";
                if (c == '\t') continue;
                if (char.IsControl(c)) return "";
            }
            return s;
        }

        public static VpbWriteResult AddTags(string vamRoot, IReadOnlyCollection<string> uids, IReadOnlyCollection<string> tags) =>
            Apply(vamRoot, uids, tags, adding: true);

        public static VpbWriteResult RemoveTags(string vamRoot, IReadOnlyCollection<string> uids, IReadOnlyCollection<string> tags) =>
            Apply(vamRoot, uids, tags, adding: false);

        public static VpbWriteResult AddTagsToUserFiles(string vamRoot, IReadOnlyCollection<UserFileTagTarget> files, IReadOnlyCollection<string> tags) =>
            ApplyUserFiles(vamRoot, files, tags, adding: true);

        public static VpbWriteResult RemoveTagsFromUserFiles(string vamRoot, IReadOnlyCollection<UserFileTagTarget> files, IReadOnlyCollection<string> tags) =>
            ApplyUserFiles(vamRoot, files, tags, adding: false);

        private static VpbWriteResult Apply(
            string vamRoot,
            IReadOnlyCollection<string> uids,
            IReadOnlyCollection<string> tags,
            bool adding)
        {
            if (string.IsNullOrWhiteSpace(vamRoot)) return VpbWriteResult.Failed("No VaM folder selected.");
            if (uids == null || uids.Count == 0 || tags == null || tags.Count == 0) return VpbWriteResult.Ok(0);

            var normalized = new List<string>(tags.Count);
            var rejected = new List<string>();
            foreach (var raw in tags)
            {
                var name = NormalizeTagName(raw);
                if (name.Length == 0) { if (!string.IsNullOrWhiteSpace(raw)) rejected.Add(raw.Trim()); continue; }
                if (!normalized.Contains(name, StringComparer.Ordinal)) normalized.Add(name);
            }
            if (normalized.Count == 0)
                return VpbWriteResult.Failed(rejected.Count > 0
                    ? "Not a usable tag name: " + string.Join(", ", rejected)
                    : "No tag names given.");

            var dbPath = VpbPaths.DatabasePath(vamRoot);
            if (!File.Exists(dbPath))
                return VpbWriteResult.Failed("VPB's database was not found. Run VPB once so it can build its index.");

            try
            {
                using var conn = OpenWrite(dbPath);
                if (conn == null)
                    return VpbWriteResult.Failed("VPB's database is locked. Close VaM's package browser and try again.");

                // VPB owns this schema; if it has not built the tag tables yet, creating them here would risk a shape it will not recognise.
                var schema = VpbDbSchema.Probe(conn);
                if (!schema.HasColumns("gallery_user_tag", "tag_id", "name")
                    || !schema.HasColumns("gallery_item_user_tag", "category", "pkg_uid", "internal_path", "tag_id")
                    || !schema.HasColumns("cat_mem", "category", "pkg_uid", "internal_path"))
                {
                    return VpbWriteResult.Failed("This VPB build has no user-tag tables yet. Open VPB's gallery once, then retry.");
                }

                int changed;
                using (var tx = conn.BeginTransaction())
                {
                    var tagIds = new List<long>(normalized.Count);
                    foreach (var name in normalized)
                    {
                        var id = adding ? GetOrCreateTagId(conn, tx, name) : FindTagId(conn, tx, name);
                        if (id > 0) tagIds.Add(id);
                    }

                    changed = tagIds.Count == 0
                        ? 0
                        : ApplyToPackages(conn, tx, uids, tagIds, adding);

                    tx.Commit();
                }

                if (rejected.Count > 0 && changed > 0)
                {
                    return new VpbWriteResult
                    {
                        Success = true,
                        PackagesChanged = changed,
                        Message = "Skipped unusable tag name(s): " + string.Join(", ", rejected)
                    };
                }

                return VpbWriteResult.Ok(changed);
            }
            catch (Exception ex)
            {
                return VpbWriteResult.Failed("Could not update VPB tags: " + ex.Message);
            }
        }

        private static int ApplyToPackages(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyCollection<string> uids,
            List<long> tagIds,
            bool adding)
        {
            int changed = 0;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = adding
                // Fan the tag across every gallery row the package owns, skipping rows already at VPB's per-item ceiling so this can never push a row past what VPB itself would allow.
                ? "INSERT OR IGNORE INTO gallery_item_user_tag(category, pkg_uid, internal_path, tag_id) " +
                  "SELECT m.category, m.pkg_uid, m.internal_path, $tag FROM cat_mem m " +
                  "WHERE m.pkg_uid = $uid AND (" +
                  "  SELECT COUNT(*) FROM gallery_item_user_tag g " +
                  "  WHERE g.category = m.category AND g.pkg_uid = m.pkg_uid AND g.internal_path = m.internal_path" +
                  ") < $limit"
                : "DELETE FROM gallery_item_user_tag WHERE pkg_uid = $uid AND tag_id = $tag";

            var uidParam = cmd.Parameters.Add("$uid", SqliteType.Text);
            var tagParam = cmd.Parameters.Add("$tag", SqliteType.Integer);
            if (adding) cmd.Parameters.AddWithValue("$limit", MaxTagsPerItem);
            cmd.Prepare();

            foreach (var uid in uids)
            {
                if (string.IsNullOrEmpty(uid)) continue;
                uidParam.Value = uid;

                int rowsForPackage = 0;
                foreach (var tagId in tagIds)
                {
                    tagParam.Value = tagId;
                    rowsForPackage += cmd.ExecuteNonQuery();
                }
                if (rowsForPackage > 0) changed++;
            }

            return changed;
        }

        private static VpbWriteResult ApplyUserFiles(
            string vamRoot,
            IReadOnlyCollection<UserFileTagTarget> files,
            IReadOnlyCollection<string> tags,
            bool adding)
        {
            if (string.IsNullOrWhiteSpace(vamRoot)) return VpbWriteResult.Failed("No VaM folder selected.");
            if (files == null || files.Count == 0 || tags == null || tags.Count == 0) return VpbWriteResult.Ok(0);

            var normalized = new List<string>(tags.Count);
            var rejected = new List<string>();
            foreach (var raw in tags)
            {
                var name = NormalizeTagName(raw);
                if (name.Length == 0) { if (!string.IsNullOrWhiteSpace(raw)) rejected.Add(raw.Trim()); continue; }
                if (!normalized.Contains(name, StringComparer.Ordinal)) normalized.Add(name);
            }
            if (normalized.Count == 0)
                return VpbWriteResult.Failed(rejected.Count > 0
                    ? "Not a usable tag name: " + string.Join(", ", rejected)
                    : "No tag names given.");

            var dbPath = VpbPaths.DatabasePath(vamRoot);
            if (!File.Exists(dbPath))
                return VpbWriteResult.Failed("VPB's database was not found. Run VPB once so it can build its index.");

            try
            {
                using var conn = OpenWrite(dbPath);
                if (conn == null)
                    return VpbWriteResult.Failed("VPB's database is locked. Close VaM's package browser and try again.");

                var schema = VpbDbSchema.Probe(conn);
                if (!schema.HasColumns("gallery_user_tag", "tag_id", "name")
                    || !schema.HasColumns("gallery_item_user_tag", "category", "pkg_uid", "internal_path", "tag_id"))
                {
                    return VpbWriteResult.Failed("This VPB build has no user-tag tables yet. Open VPB's gallery once, then retry.");
                }

                bool canLookupCatMem = schema.HasColumns("cat_mem", "category", "pkg_uid", "internal_path");

                int changed;
                using (var tx = conn.BeginTransaction())
                {
                    var tagIds = new List<long>(normalized.Count);
                    foreach (var name in normalized)
                    {
                        var id = adding ? GetOrCreateTagId(conn, tx, name) : FindTagId(conn, tx, name);
                        if (id > 0) tagIds.Add(id);
                    }

                    changed = tagIds.Count == 0
                        ? 0
                        : ApplyToUserFiles(conn, tx, files, tagIds, adding, canLookupCatMem);

                    tx.Commit();
                }

                if (rejected.Count > 0 && changed > 0)
                {
                    return new VpbWriteResult
                    {
                        Success = true,
                        PackagesChanged = changed,
                        Message = "Skipped unusable tag name(s): " + string.Join(", ", rejected)
                    };
                }

                return VpbWriteResult.Ok(changed);
            }
            catch (Exception ex)
            {
                return VpbWriteResult.Failed("Could not update VPB tags: " + ex.Message);
            }
        }

        /// <summary>Tags one gallery row per file by internal_path with empty pkg_uid. Never WHERE pkg_uid = $uid — empty uid would hit every user file.</summary>
        private static int ApplyToUserFiles(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyCollection<UserFileTagTarget> files,
            List<long> tagIds,
            bool adding,
            bool canLookupCatMem)
        {
            int changed = 0;

            using var write = conn.CreateCommand();
            write.Transaction = tx;
            write.CommandText = adding
                ? "INSERT OR IGNORE INTO gallery_item_user_tag(category, pkg_uid, internal_path, tag_id) " +
                  "SELECT $cat, $uid, $path, $tag WHERE (" +
                  "  SELECT COUNT(*) FROM gallery_item_user_tag g " +
                  "  WHERE g.category = $cat AND g.pkg_uid = $uid AND g.internal_path = $path" +
                  ") < $limit"
                : "DELETE FROM gallery_item_user_tag " +
                  "WHERE category = $cat AND pkg_uid = $uid AND internal_path = $path AND tag_id = $tag";

            var catParam = write.Parameters.Add("$cat", SqliteType.Text);
            var uidParam = write.Parameters.Add("$uid", SqliteType.Text);
            var pathParam = write.Parameters.Add("$path", SqliteType.Text);
            var tagParam = write.Parameters.Add("$tag", SqliteType.Integer);
            if (adding) write.Parameters.AddWithValue("$limit", MaxTagsPerItem);
            write.Prepare();

            foreach (var file in files)
            {
                if (file == null) continue;
                var relative = VpbUserFileKeys.NormalizePath(file.RelativePath);
                if (relative.Length == 0) continue;

                var (category, pkgUid, internalPath) = ResolveUserFileRow(
                    conn, tx, relative, file.Category, canLookupCatMem);

                catParam.Value = category;
                uidParam.Value = pkgUid;
                pathParam.Value = internalPath;

                int rowsForFile = 0;
                foreach (var tagId in tagIds)
                {
                    tagParam.Value = tagId;
                    rowsForFile += write.ExecuteNonQuery();
                }
                if (rowsForFile > 0) changed++;
            }

            return changed;
        }

        private static (string category, string pkgUid, string internalPath) ResolveUserFileRow(
            SqliteConnection conn,
            SqliteTransaction tx,
            string relativePath,
            string fallbackCategory,
            bool canLookupCatMem)
        {
            var category = string.IsNullOrWhiteSpace(fallbackCategory) ? "Other" : fallbackCategory;
            if (!canLookupCatMem) return (category, "", relativePath);

            try
            {
                using var select = conn.CreateCommand();
                select.Transaction = tx;
                select.CommandText =
                    "SELECT category, pkg_uid, internal_path FROM cat_mem " +
                    "WHERE (pkg_uid = '' OR pkg_uid IS NULL) " +
                    "AND (internal_path = $fwd OR internal_path = $bwd) LIMIT 1";
                select.Parameters.AddWithValue("$fwd", relativePath);
                select.Parameters.AddWithValue("$bwd", relativePath.Replace('/', '\\'));
                using var reader = select.ExecuteReader();
                if (reader.Read())
                {
                    var cat = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var uid = reader.IsDBNull(1) ? "" : reader.GetString(1) ?? "";
                    var path = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    if (cat.Length > 0 && path.Length > 0)
                        return (cat, uid, path);
                }
            }
            catch { }

            return (category, "", relativePath);
        }

        private static long GetOrCreateTagId(SqliteConnection conn, SqliteTransaction tx, string normalizedName)
        {
            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT OR IGNORE INTO gallery_user_tag(name) VALUES($name)";
                insert.Parameters.AddWithValue("$name", normalizedName);
                insert.ExecuteNonQuery();
            }
            return FindTagId(conn, tx, normalizedName);
        }

        private static long FindTagId(SqliteConnection conn, SqliteTransaction tx, string normalizedName)
        {
            using var select = conn.CreateCommand();
            select.Transaction = tx;
            select.CommandText = "SELECT tag_id FROM gallery_user_tag WHERE name = $name";
            select.Parameters.AddWithValue("$name", normalizedName);
            var value = select.ExecuteScalar();
            if (value == null || value == DBNull.Value) return 0;
            try { return Convert.ToInt64(value); } catch { return 0; }
        }

        private static SqliteConnection OpenWrite(string path)
        {
            SqliteConnection conn = null;
            try
            {
                var cs = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false
                };
                conn = new SqliteConnection(cs.ToString());
                conn.DefaultTimeout = Math.Max(1, BusyTimeoutMs / 1000);
                conn.Open();
                using (var pragma = conn.CreateCommand())
                {
                    // WAL allows one writer alongside VaM's readers; the timeout rides out its commits.
                    pragma.CommandText = "PRAGMA busy_timeout=" + BusyTimeoutMs + ";";
                    pragma.ExecuteNonQuery();
                }
                return conn;
            }
            catch
            {
                try { conn?.Dispose(); } catch { }
                return null;
            }
        }
    }
}
