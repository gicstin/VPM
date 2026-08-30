using System;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;

namespace VPM.Services.Vpb
{
    public sealed class PackageUsageRow
    {
        public string Uid { get; init; }
        public int UseCount { get; init; }
        public long LastUsedBinary { get; init; }
        public int OnDemandHits { get; init; }
        public long OnDemandLastUtc { get; init; }
        public bool CleanupExcluded { get; init; }
    }

    /// <summary>Read-only VPB SQLite index. WAL so VaM and VPM never block each other; queries gate on Schema rather than an assumed version.</summary>
    public sealed class VpbLocalDbReader : IDisposable
    {
        private const int BusyTimeoutMs = 4000;

        private readonly string _path;
        private SqliteConnection _conn;
        private VpbDbSchema _schema;
        private bool _openAttempted;
        private bool _disposed;

        public VpbLocalDbReader(string vamRoot)
        {
            _path = VpbPaths.DatabasePath(vamRoot);
        }

        /// <summary>True once the database has been opened successfully.</summary>
        public bool IsAvailable => Connection() != null;

        /// <summary>What this particular VPB build's database actually contains.</summary>
        public VpbDbSchema Schema
        {
            get
            {
                Connection();
                return _schema ?? VpbDbSchema.Empty;
            }
        }

        /// <summary>Kept for callers that only need to know whether the file opened.</summary>
        public bool TryOpen() => Connection() != null;

        /// <summary>Per-package usage rolled up from item play history, on-demand hits and cleanup locks.</summary>
        public Dictionary<string, PackageUsageRow> LoadUsage()
        {
            var acc = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
            var conn = Connection();
            if (conn == null) return Materialize(acc);

            if (_schema.HasColumns("item_usage", "item_key", "use_count", "last_used"))
            {
                Query(conn, "SELECT item_key, use_count, last_used FROM item_usage", r =>
                {
                    var uid = ExtractUid(GetString(r, 0));
                    if (uid.Length == 0) return;
                    var row = Slot(acc, uid);
                    row.UseCount += (int)GetInt64(r, 1);
                    row.LastUsedBinary = Math.Max(row.LastUsedBinary, GetInt64(r, 2));
                });
            }

            if (_schema.HasColumns("cleanup_exclude", "uid"))
            {
                Query(conn, "SELECT uid FROM cleanup_exclude", r =>
                {
                    var uid = GetString(r, 0);
                    if (uid.Length == 0) return;
                    Slot(acc, uid).CleanupExcluded = true;
                });
            }

            // Present only in VPB builds that track on-demand loading; absent in others.
            if (_schema.HasColumns("ondemand_hit", "uid", "count", "last_utc"))
            {
                Query(conn, "SELECT uid, count, last_utc FROM ondemand_hit", r =>
                {
                    var uid = GetString(r, 0);
                    if (uid.Length == 0) return;
                    var row = Slot(acc, uid);
                    row.OnDemandHits = (int)GetInt64(r, 1);
                    row.OnDemandLastUtc = GetInt64(r, 2);
                });
            }

            return Materialize(acc);
        }

        /// <summary>Package uids VPB has pinned against cleanup.</summary>
        public HashSet<string> LoadCleanupExclude()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var conn = Connection();
            if (conn == null || !_schema.HasColumns("cleanup_exclude", "uid")) return set;
            Query(conn, "SELECT uid FROM cleanup_exclude", r =>
            {
                var uid = GetString(r, 0);
                if (uid.Length != 0) set.Add(uid);
            });
            return set;
        }

        /// <summary>VPB's package index: license, version family, newest-version winner and on-disk facts VPM would otherwise have to reopen every var to recompute.</summary>
        public Dictionary<string, VpbPackageRow> LoadPackages()
        {
            var result = new Dictionary<string, VpbPackageRow>(StringComparer.OrdinalIgnoreCase);
            var conn = Connection();
            if (conn == null || !_schema.HasColumns("pkg", "uid")) return result;

            // Most of these columns arrived via ALTER over VPB's life, so select only what this file has.
            var ordinals = SelectableColumns(
                "pkg",
                out var sql,
                "uid", "creator", "var_path", "license", "family", "ver",
                "is_newest", "loaded", "no_cat", "psize", "wtime", "first_scanned");

            Query(conn, sql, r =>
            {
                var uid = Text(r, ordinals, "uid");
                if (uid.Length == 0) return;
                result[uid] = new VpbPackageRow
                {
                    Uid = uid,
                    Creator = Text(r, ordinals, "creator"),
                    VarPath = Text(r, ordinals, "var_path"),
                    License = Text(r, ordinals, "license"),
                    Family = Text(r, ordinals, "family"),
                    Version = (int)Number(r, ordinals, "ver"),
                    IsNewest = Number(r, ordinals, "is_newest") != 0,
                    IsLoaded = Number(r, ordinals, "loaded") != 0,
                    HasNoCategory = Number(r, ordinals, "no_cat") != 0,
                    SizeBytes = Number(r, ordinals, "psize"),
                    LastWriteBinary = Number(r, ordinals, "wtime"),
                    FirstScannedBinary = Number(r, ordinals, "first_scanned")
                };
            });

            return result;
        }

        /// <summary>Gallery items per package per category, aggregated in SQL so the membership table never crosses into managed memory.</summary>
        public Dictionary<string, VpbCategoryCounts> LoadCategoryCounts()
        {
            var result = new Dictionary<string, VpbCategoryCounts>(StringComparer.OrdinalIgnoreCase);
            var conn = Connection();
            if (conn == null || !_schema.HasColumns("cat_mem", "category", "pkg_uid")) return result;

            Query(conn, "SELECT pkg_uid, category, COUNT(*) FROM cat_mem GROUP BY pkg_uid, category", r =>
            {
                var uid = GetString(r, 0);
                var category = GetString(r, 1);
                if (uid.Length == 0 || category.Length == 0) return;
                if (!result.TryGetValue(uid, out var counts))
                {
                    counts = new VpbCategoryCounts
                    {
                        Uid = uid,
                        CountsByCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    };
                    result[uid] = counts;
                }
                counts.CountsByCategory[category] = (int)GetInt64(r, 2);
            });

            return result;
        }

        /// <summary>Package uid to the uids it depends on, from VPB's resolved dependency edges.</summary>
        public Dictionary<string, List<string>> LoadDependencies()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var conn = Connection();
            if (conn == null || !_schema.HasColumns("pkg_dep", "src_uid", "dep_uid")) return result;

            Query(conn, "SELECT src_uid, dep_uid FROM pkg_dep", r =>
            {
                var src = GetString(r, 0);
                var dep = GetString(r, 1);
                if (src.Length == 0 || dep.Length == 0) return;
                if (!result.TryGetValue(src, out var list))
                {
                    list = new List<string>();
                    result[src] = list;
                }
                list.Add(dep);
            });

            return result;
        }

        /// <summary>User tags per package: distinct union over every gallery item VPB stores as (category, pkg_uid, internal_path).</summary>
        public Dictionary<string, HashSet<string>> LoadPackageUserTags()
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var conn = Connection();
            if (conn == null) return result;
            if (!_schema.HasColumns("gallery_item_user_tag", "pkg_uid", "tag_id")) return result;
            if (!_schema.HasColumns("gallery_user_tag", "tag_id", "name")) return result;

            Query(conn,
                "SELECT DISTINCT gut.pkg_uid, gt.name FROM gallery_item_user_tag gut " +
                "INNER JOIN gallery_user_tag gt ON gt.tag_id = gut.tag_id", r =>
                {
                    var uid = GetString(r, 0);
                    var name = GetString(r, 1);
                    if (uid.Length == 0 || name.Length == 0) return;
                    if (!result.TryGetValue(uid, out var tags))
                    {
                        tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        result[uid] = tags;
                    }
                    tags.Add(name);
                });

            return result;
        }

        /// <summary>User tags on loose Custom/Saves files, keyed by internal_path. Empty pkg_uid — aggregating by it would merge every user file into one bucket.</summary>
        public Dictionary<string, HashSet<string>> LoadUserFileUserTags()
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var conn = Connection();
            if (conn == null) return result;
            if (!_schema.HasColumns("gallery_item_user_tag", "pkg_uid", "internal_path", "tag_id")) return result;
            if (!_schema.HasColumns("gallery_user_tag", "tag_id", "name")) return result;

            Query(conn,
                "SELECT DISTINCT gut.internal_path, gt.name FROM gallery_item_user_tag gut " +
                "INNER JOIN gallery_user_tag gt ON gt.tag_id = gut.tag_id " +
                "WHERE gut.pkg_uid = '' OR gut.pkg_uid IS NULL", r =>
                {
                    var path = VpbUserFileKeys.NormalizePath(GetString(r, 0));
                    var name = GetString(r, 1);
                    if (path.Length == 0 || name.Length == 0) return;
                    if (!result.TryGetValue(path, out var tags))
                    {
                        tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        result[path] = tags;
                    }
                    tags.Add(name);
                });

            return result;
        }

        /// <summary>VPB's content audit per package. Empty on builds that do not ship <c>pkg_insight</c>.</summary>
        public Dictionary<string, VpbPackageInsightRow> LoadInsights()
        {
            var result = new Dictionary<string, VpbPackageInsightRow>(StringComparer.OrdinalIgnoreCase);
            var conn = Connection();
            if (conn == null || !_schema.HasColumns("pkg_insight", "uid")) return result;

            var ordinals = SelectableColumns(
                "pkg_insight",
                out var sql,
                "uid", "issue_flags", "risk_flags", "morph_count",
                "script_count", "dll_count", "bundle_count", "undeclared");

            Query(conn, sql, r =>
            {
                var uid = Text(r, ordinals, "uid");
                if (uid.Length == 0) return;
                result[uid] = new VpbPackageInsightRow
                {
                    Uid = uid,
                    IssueFlags = Number(r, ordinals, "issue_flags"),
                    RiskFlags = Number(r, ordinals, "risk_flags"),
                    MorphCount = (int)Number(r, ordinals, "morph_count"),
                    ScriptCount = (int)Number(r, ordinals, "script_count"),
                    DllCount = (int)Number(r, ordinals, "dll_count"),
                    BundleCount = (int)Number(r, ordinals, "bundle_count"),
                    Undeclared = Text(r, ordinals, "undeclared")
                };
            });

            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _conn?.Dispose(); } catch { }
            _conn = null;
        }

        private SqliteConnection Connection()
        {
            if (_disposed) return null;
            if (_openAttempted) return _conn;
            _openAttempted = true;

            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return null;

            // ReadOnly first. It fails when SQLite cannot attach the WAL shm while VaM holds the db; ReadWrite attaches it, query_only keeps this reader honest.
            _conn = TryConnect(SqliteOpenMode.ReadOnly, queryOnly: false)
                 ?? TryConnect(SqliteOpenMode.ReadWrite, queryOnly: true);
            if (_conn == null) return null;

            _schema = VpbDbSchema.Probe(_conn);
            return _conn;
        }

        private SqliteConnection TryConnect(SqliteOpenMode mode, bool queryOnly)
        {
            SqliteConnection conn = null;
            try
            {
                var cs = new SqliteConnectionStringBuilder
                {
                    DataSource = _path,
                    Mode = mode,
                    Pooling = false
                };
                conn = new SqliteConnection(cs.ToString());
                conn.DefaultTimeout = Math.Max(1, BusyTimeoutMs / 1000);
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA busy_timeout=" + BusyTimeoutMs + ";";
                    if (queryOnly) cmd.CommandText += "PRAGMA query_only=1;";
                    cmd.ExecuteNonQuery();
                }
                using (var probe = conn.CreateCommand())
                {
                    probe.CommandText = "SELECT 1";
                    probe.ExecuteScalar();
                }
                return conn;
            }
            catch
            {
                try { conn?.Dispose(); } catch { }
                return null;
            }
        }

        /// <summary>Builds "SELECT a, b FROM table" over the subset of columns this database actually has.</summary>
        private Dictionary<string, int> SelectableColumns(string table, out string sql, params string[] wanted)
        {
            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var builder = new StringBuilder("SELECT ");
            foreach (var column in wanted)
            {
                if (!_schema.HasColumns(table, column)) continue;
                if (ordinals.Count > 0) builder.Append(", ");
                ordinals[column] = ordinals.Count;
                builder.Append(column);
            }
            builder.Append(" FROM ").Append(table);
            sql = builder.ToString();
            return ordinals;
        }

        private static void Query(SqliteConnection conn, string sql, Action<IDataRecord> onRow)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                while (r.Read()) onRow(r);
            }
            catch { }
        }

        private sealed class Accumulator
        {
            public int UseCount;
            public long LastUsedBinary;
            public int OnDemandHits;
            public long OnDemandLastUtc;
            public bool CleanupExcluded;
        }

        private static Accumulator Slot(Dictionary<string, Accumulator> acc, string uid)
        {
            if (!acc.TryGetValue(uid, out var row))
            {
                row = new Accumulator();
                acc[uid] = row;
            }
            return row;
        }

        private static Dictionary<string, PackageUsageRow> Materialize(Dictionary<string, Accumulator> acc)
        {
            var result = new Dictionary<string, PackageUsageRow>(acc.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in acc)
            {
                var a = kvp.Value;
                result[kvp.Key] = new PackageUsageRow
                {
                    Uid = kvp.Key,
                    UseCount = a.UseCount,
                    LastUsedBinary = a.LastUsedBinary,
                    OnDemandHits = a.OnDemandHits,
                    OnDemandLastUtc = a.OnDemandLastUtc,
                    CleanupExcluded = a.CleanupExcluded
                };
            }
            return result;
        }

        /// <summary>item_usage keys are VaM file uids; reduce them to the owning package uid.</summary>
        private static string ExtractUid(string itemKey)
        {
            if (string.IsNullOrEmpty(itemKey)) return "";
            var k = itemKey.Replace('\\', '/');
            var sep = k.IndexOf(":/", StringComparison.Ordinal);
            if (sep > 0) k = k.Substring(0, sep);
            var slash = k.LastIndexOf('/');
            if (slash >= 0) k = k.Substring(slash + 1);
            if (k.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                k = k.Substring(0, k.Length - 4);
            return k.Trim();
        }

        private static string GetString(IDataRecord r, int ordinal) =>
            r.IsDBNull(ordinal) ? "" : Convert.ToString(r.GetValue(ordinal)) ?? "";

        // Some pkg columns were declared with TEXT affinity but hold integers, so never assume a CLR type.
        private static long GetInt64(IDataRecord r, int ordinal)
        {
            if (r.IsDBNull(ordinal)) return 0L;
            var value = r.GetValue(ordinal);
            if (value is long l) return l;
            if (value is string s) return long.TryParse(s, out var parsed) ? parsed : 0L;
            try { return Convert.ToInt64(value); } catch { return 0L; }
        }

        private static string Text(IDataRecord r, Dictionary<string, int> ordinals, string column) =>
            ordinals.TryGetValue(column, out var i) ? GetString(r, i) : "";

        private static long Number(IDataRecord r, Dictionary<string, int> ordinals, string column) =>
            ordinals.TryGetValue(column, out var i) ? GetInt64(r, i) : 0L;
    }
}
