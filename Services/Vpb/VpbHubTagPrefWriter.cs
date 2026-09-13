using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace VPM.Services.Vpb
{
    public static class VpbHubTagPrefWriter
    {
        private const int BusyTimeoutMs = 8000;

        private const string Ddl =
            "CREATE TABLE IF NOT EXISTS datapack_tag_pref (" +
            "scope_uid TEXT NOT NULL, tag TEXT NOT NULL, PRIMARY KEY(scope_uid, tag));" +
            "CREATE INDEX IF NOT EXISTS idx_dp_tag_pref_tag ON datapack_tag_pref(tag);";

        public static string NormalizeTag(string tag) =>
            string.IsNullOrEmpty(tag) ? "" : tag.Trim().ToLowerInvariant();

        public static VpbWriteResult HideGlobally(string vamRoot, IReadOnlyCollection<string> tags) =>
            Apply(vamRoot, new[] { "" }, tags, hide: true);

        public static VpbWriteResult ShowGlobally(string vamRoot, IReadOnlyCollection<string> tags) =>
            Apply(vamRoot, new[] { "" }, tags, hide: false);

        public static VpbWriteResult HideOnPackages(string vamRoot, IReadOnlyCollection<string> uids, IReadOnlyCollection<string> tags) =>
            Apply(vamRoot, uids, tags, hide: true);

        public static VpbWriteResult ShowOnPackages(string vamRoot, IReadOnlyCollection<string> uids, IReadOnlyCollection<string> tags) =>
            Apply(vamRoot, uids, tags, hide: false);

        public static VpbWriteResult ShowAll(string vamRoot, bool globalOnly)
        {
            if (string.IsNullOrWhiteSpace(vamRoot)) return VpbWriteResult.Failed("No VaM folder selected.");

            var dbPath = VpbPaths.DatabasePath(vamRoot);
            if (!File.Exists(dbPath))
                return VpbWriteResult.Failed("VPB's database was not found. Run VPB once so it can build its index.");

            try
            {
                using var conn = OpenWrite(dbPath);
                if (conn == null)
                    return VpbWriteResult.Failed("VPB's database is locked. Close VaM's package browser and try again.");

                EnsureSchema(conn);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = globalOnly
                    ? "DELETE FROM datapack_tag_pref WHERE scope_uid = ''"
                    : "DELETE FROM datapack_tag_pref";
                return VpbWriteResult.Ok(cmd.ExecuteNonQuery());
            }
            catch (Exception ex)
            {
                return VpbWriteResult.Failed("Could not update hub tag preferences: " + ex.Message);
            }
        }

        private static VpbWriteResult Apply(
            string vamRoot,
            IReadOnlyCollection<string> scopes,
            IReadOnlyCollection<string> tags,
            bool hide)
        {
            if (string.IsNullOrWhiteSpace(vamRoot)) return VpbWriteResult.Failed("No VaM folder selected.");
            if (scopes == null || scopes.Count == 0 || tags == null || tags.Count == 0) return VpbWriteResult.Ok(0);

            var normalized = new List<string>(tags.Count);
            foreach (var raw in tags)
            {
                var tag = NormalizeTag(raw);
                if (tag.Length == 0 || normalized.Contains(tag, StringComparer.Ordinal)) continue;
                normalized.Add(tag);
            }
            if (normalized.Count == 0) return VpbWriteResult.Failed("No usable tag name given.");

            var dbPath = VpbPaths.DatabasePath(vamRoot);
            if (!File.Exists(dbPath))
                return VpbWriteResult.Failed("VPB's database was not found. Run VPB once so it can build its index.");

            try
            {
                using var conn = OpenWrite(dbPath);
                if (conn == null)
                    return VpbWriteResult.Failed("VPB's database is locked. Close VaM's package browser and try again.");

                EnsureSchema(conn);

                int changed;
                using (var tx = conn.BeginTransaction())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = hide
                        ? "INSERT OR IGNORE INTO datapack_tag_pref(scope_uid, tag) VALUES($scope, $tag)"
                        : "DELETE FROM datapack_tag_pref WHERE scope_uid = $scope AND tag = $tag";

                    var scopeParam = cmd.Parameters.Add("$scope", SqliteType.Text);
                    var tagParam = cmd.Parameters.Add("$tag", SqliteType.Text);
                    cmd.Prepare();

                    changed = 0;
                    foreach (var scope in scopes)
                    {
                        scopeParam.Value = scope ?? "";
                        int rowsForScope = 0;
                        foreach (var tag in normalized)
                        {
                            tagParam.Value = tag;
                            rowsForScope += cmd.ExecuteNonQuery();
                        }
                        if (rowsForScope > 0) changed++;
                    }

                    tx.Commit();
                }

                return VpbWriteResult.Ok(changed);
            }
            catch (Exception ex)
            {
                return VpbWriteResult.Failed("Could not update hub tag preferences: " + ex.Message);
            }
        }

        private static void EnsureSchema(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Ddl;
            cmd.ExecuteNonQuery();
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
