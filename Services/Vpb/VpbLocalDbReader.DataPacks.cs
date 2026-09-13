using System;
using System.Collections.Generic;

namespace VPM.Services.Vpb
{
    public sealed partial class VpbLocalDbReader
    {
        private const int HubTagsPerPackageCap = 24;

        public List<VpbDataPackRow> LoadDataPacks()
        {
            var result = new List<VpbDataPackRow>();
            var conn = Connection();
            if (conn == null || !_schema.HasColumns("datapack", "pack_id")) return result;

            var ordinals = SelectableColumns(
                "datapack",
                out var sql,
                "pack_id", "pack_version", "built_date", "attribution", "source_url", "entry_count");

            Query(conn, sql, r =>
            {
                var packId = Text(r, ordinals, "pack_id");
                if (packId.Length == 0) return;
                result.Add(new VpbDataPackRow
                {
                    PackId = packId,
                    PackVersion = Text(r, ordinals, "pack_version"),
                    BuiltDate = Text(r, ordinals, "built_date"),
                    Attribution = Text(r, ordinals, "attribution"),
                    SourceUrl = Text(r, ordinals, "source_url"),
                    EntryCount = (int)Number(r, ordinals, "entry_count")
                });
            });

            return result;
        }

        public Dictionary<string, VpbLookRow> LoadDataPackLookRows()
        {
            var result = new Dictionary<string, VpbLookRow>(StringComparer.OrdinalIgnoreCase);
            var conn = Connection();
            if (conn == null) return result;
            if (!_schema.HasColumns("datapack_link", "pkg_uid", "pack_id", "entry_id", "match_kind")) return result;
            if (!_schema.HasColumns("datapack_entry", "pack_id", "entry_id", "subject", "category")) return result;

            using var snapshot = new ReadSnapshot(conn);

            Query(conn,
                "SELECT dl.pkg_uid, de.subject, de.category FROM datapack_link dl " +
                "INNER JOIN datapack_entry de ON de.pack_id = dl.pack_id AND de.entry_id = dl.entry_id " +
                "WHERE length(trim(ifnull(de.subject,''))) > 0 " +
                "AND dl.match_kind <> " + VpbDataPackIds.MatchKindCategoryOnly + " " +
                "ORDER BY dl.match_kind ASC", r =>
                {
                    var uid = GetString(r, 0);
                    if (uid.Length == 0) return;
                    var subject = GetString(r, 1).Trim();
                    var category = GetString(r, 2).Trim();
                    if (subject.Length == 0) return;

                    var row = Slot(result, uid);

                    if (subject.Length > 0 && row.Subject.Length == 0)
                    {
                        row.Subject = subject;
                        if (category.Length > 0) row.Category = category;
                    }
                });

            LoadHubCategoriesInto(result);
            LoadHubTagsInto(result);
            PartitionHiddenTags(result, LoadHubTagPrefs());

            foreach (var uid in new List<string>(result.Keys))
            {
                if (!result[uid].HasAnything) result.Remove(uid);
            }

            return result;
        }

        private void LoadHubCategoriesInto(Dictionary<string, VpbLookRow> rows)
        {
            var conn = Connection();
            if (conn == null) return;

            Query(conn,
                "SELECT dl.pkg_uid, de.category FROM datapack_link dl " +
                "INNER JOIN datapack_entry de ON de.pack_id = dl.pack_id AND de.entry_id = dl.entry_id " +
                "WHERE length(trim(ifnull(de.category,''))) > 0 " +
                "AND (dl.pack_id = '" + VpbDataPackIds.HubTags + "' " +
                "OR dl.pack_id = '" + VpbDataPackIds.HubLive + "') " +
                "ORDER BY dl.match_kind ASC", r =>
                {
                    var uid = GetString(r, 0);
                    var category = GetString(r, 1).Trim();
                    if (uid.Length == 0 || category.Length == 0) return;

                    var row = Slot(rows, uid);
                    if (row.HubCategory.Length == 0)
                        row.HubCategory = VpbHubCategoryNames.Display(category);
                });
        }

        private void LoadHubTagsInto(Dictionary<string, VpbLookRow> rows)
        {
            var conn = Connection();
            if (conn == null) return;
            if (!_schema.HasColumns("datapack_tag", "pack_id", "entry_id", "ns", "tag")) return;

            var pending = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Query(conn,
                "SELECT DISTINCT dl.pkg_uid, dt.tag FROM datapack_link dl " +
                "INNER JOIN datapack_tag dt ON dt.pack_id = dl.pack_id AND dt.entry_id = dl.entry_id " +
                "AND dt.ns = 'hub' " +
                "WHERE dl.match_kind <> " + VpbDataPackIds.MatchKindCategoryOnly + " " +
                "ORDER BY dl.pkg_uid, dt.tag", r =>
                {
                    var uid = GetString(r, 0);
                    var tag = GetString(r, 1).Trim();
                    if (uid.Length == 0 || tag.Length == 0) return;
                    if (!pending.TryGetValue(uid, out var list))
                    {
                        list = new List<string>(4);
                        pending[uid] = list;
                    }
                    if (list.Count < HubTagsPerPackageCap) list.Add(tag);
                });

            foreach (var kvp in pending)
                Slot(rows, kvp.Key).HubTags = kvp.Value;
        }

        public VpbHubTagPrefs LoadHubTagPrefs()
        {
            var global = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byPackage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            var conn = Connection();
            if (conn == null || !_schema.HasColumns("datapack_tag_pref", "scope_uid", "tag"))
                return new VpbHubTagPrefs(global, byPackage);

            Query(conn, "SELECT scope_uid, tag FROM datapack_tag_pref", r =>
            {
                var scope = GetString(r, 0);
                var tag = GetString(r, 1).Trim();
                if (tag.Length == 0) return;
                if (scope.Length == 0)
                {
                    global.Add(tag);
                    return;
                }
                if (!byPackage.TryGetValue(scope, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    byPackage[scope] = set;
                }
                set.Add(tag);
            });

            return new VpbHubTagPrefs(global, byPackage);
        }

        private static void PartitionHiddenTags(Dictionary<string, VpbLookRow> rows, VpbHubTagPrefs prefs)
        {
            if (rows.Count == 0 || prefs == null || !prefs.Any) return;

            foreach (var kvp in rows)
            {
                var row = kvp.Value;
                if (row.HubTags.Count == 0) continue;

                List<string> kept = null;
                List<string> hidden = null;
                for (int i = 0; i < row.HubTags.Count; i++)
                {
                    var tag = row.HubTags[i];
                    if (!prefs.IsHidden(kvp.Key, tag))
                    {
                        kept?.Add(tag);
                        continue;
                    }

                    if (kept == null)
                    {
                        kept = new List<string>(row.HubTags.Count - 1);
                        for (int j = 0; j < i; j++) kept.Add(row.HubTags[j]);
                        hidden = new List<string>(1);
                    }
                    hidden.Add(tag);
                }

                if (kept == null) continue;
                row.HubTags = kept;
                row.HiddenHubTags = hidden;
            }
        }

        private sealed class ReadSnapshot : IDisposable
        {
            private Microsoft.Data.Sqlite.SqliteConnection _conn;

            public ReadSnapshot(Microsoft.Data.Sqlite.SqliteConnection conn)
            {
                if (conn == null) return;
                if (!Execute(conn, "BEGIN DEFERRED")) return;
                _conn = conn;
            }

            public void Dispose()
            {
                var conn = _conn;
                _conn = null;
                if (conn != null) Execute(conn, "ROLLBACK");
            }

            private static bool Execute(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static VpbLookRow Slot(Dictionary<string, VpbLookRow> rows, string uid)
        {
            if (!rows.TryGetValue(uid, out var row))
            {
                row = new VpbLookRow();
                rows[uid] = row;
            }
            return row;
        }
    }
}
