using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace VPM.Services.Vpb
{
    /// <summary>Probes which tables/columns this VPB SQLite file actually has. Queries gate on that, not on an assumed schema_version.</summary>
    public sealed class VpbDbSchema
    {
        private static readonly string[] NoColumns = Array.Empty<string>();

        private readonly Dictionary<string, HashSet<string>> _columnsByTable;

        public int SchemaVersion { get; }

        private VpbDbSchema(Dictionary<string, HashSet<string>> columnsByTable, int schemaVersion)
        {
            _columnsByTable = columnsByTable;
            SchemaVersion = schemaVersion;
        }

        public static VpbDbSchema Empty { get; } =
            new(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase), 0);

        public bool HasTable(string table) =>
            !string.IsNullOrEmpty(table) && _columnsByTable.ContainsKey(table);

        /// <summary>True only if the table exists and carries every named column.</summary>
        public bool HasColumns(string table, params string[] columns)
        {
            if (string.IsNullOrEmpty(table)) return false;
            if (!_columnsByTable.TryGetValue(table, out var present)) return false;
            if (columns == null) return true;
            foreach (var c in columns)
            {
                if (!present.Contains(c)) return false;
            }
            return true;
        }

        /// <summary>Columns of <paramref name="table"/>, empty if the table is absent.</summary>
        public IReadOnlyCollection<string> Columns(string table)
        {
            if (!string.IsNullOrEmpty(table) && _columnsByTable.TryGetValue(table, out var present))
                return present;
            return NoColumns;
        }

        public static VpbDbSchema Probe(SqliteConnection conn)
        {
            if (conn == null) return Empty;
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT m.name, p.name FROM sqlite_master m JOIN pragma_table_info(m.name) p WHERE m.type='table'";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var table = r.IsDBNull(0) ? "" : r.GetString(0);
                    var column = r.IsDBNull(1) ? "" : r.GetString(1);
                    if (string.IsNullOrEmpty(table)) continue;
                    if (!map.TryGetValue(table, out var cols))
                    {
                        cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        map[table] = cols;
                    }
                    if (!string.IsNullOrEmpty(column)) cols.Add(column);
                }
            }
            catch
            {
                map.Clear();
                if (!TryProbeTableNamesOnly(conn, map)) return Empty;
            }

            return new VpbDbSchema(map, ReadSchemaVersion(conn, map));
        }

        /// <summary>Fallback for a SQLite build without pragma functions: table names only, no columns.</summary>
        private static bool TryProbeTableNamesOnly(SqliteConnection conn, Dictionary<string, HashSet<string>> map)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var table = r.IsDBNull(0) ? "" : r.GetString(0);
                    if (string.IsNullOrEmpty(table)) continue;
                    map[table] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                foreach (var table in new List<string>(map.Keys))
                    TryProbeColumns(conn, table, map[table]);
                return map.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void TryProbeColumns(SqliteConnection conn, string table, HashSet<string> cols)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                // Table names come from sqlite_master, so quoting is enough; they are never user input.
                cmd.CommandText = "PRAGMA table_info(\"" + table.Replace("\"", "\"\"") + "\")";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var column = r.IsDBNull(1) ? "" : r.GetString(1);
                    if (!string.IsNullOrEmpty(column)) cols.Add(column);
                }
            }
            catch { }
        }

        private static int ReadSchemaVersion(SqliteConnection conn, Dictionary<string, HashSet<string>> map)
        {
            if (!map.ContainsKey("meta")) return 0;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT v FROM meta WHERE k='schema_version'";
                var value = cmd.ExecuteScalar() as string;
                return int.TryParse(value, out var v) ? v : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
