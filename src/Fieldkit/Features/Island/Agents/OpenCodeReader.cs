// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Fieldkit.Features.Island.Agents;

/// <summary>
/// Reads OpenCode's SQLite database read-only through the SQLite that ships with Windows
/// (winsqlite3.dll), handing each message row to the parser as one JSON record (port of upstream
/// <c>AgentOpenCodeReader</c>). Only types, times, token counts and folder names are read.
/// </summary>
public static class OpenCodeReader
{
    private static readonly (string Name, string Path)[] Values =
    [
        ("parentID", "$.parentID"), ("modelID", "$.modelID"), ("model_id", "$.model_id"), ("model", "$.model"),
        ("tokens", "$.tokens"), ("cost", "$.cost"), ("finish", "$.finish"), ("time", "$.time"), ("cwd", "$.path.cwd"),
        ("status", "$.status"), ("outcome", "$.outcome"),
    ];

    /// <summary>Rows unchanged this long while "being written" are let go.</summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromHours(24);

    public static List<string> ReadAppended(AgentLogCursor cursor, DateTimeOffset horizon)
    {
        var lines = new List<string>();
        IntPtr db = IntPtr.Zero;
        try
        {
            if (Sqlite.sqlite3_open_v2(Encoding.UTF8.GetBytes(cursor.Path + "\0"), out db, Sqlite.SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) return lines;
            Sqlite.sqlite3_busy_timeout(db, 2000);
            var version2 = Exists(db, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'session_v2'");
            var messageTable = version2 ? "session_message" : "message";
            var sessionTable = version2 ? "session_v2" : "session";
            if (!Exists(db, $"SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '{messageTable}'")) return lines;
            var maxRow = Scalar(db, $"SELECT max(rowid) FROM {messageTable}") ?? 0;
            if (maxRow < cursor.Offset)
            {
                cursor.StartOver();
                lines.Add("{\"type\":\"reset\"}");
            }
            var floor = Math.Max(0, horizon.ToUnixTimeMilliseconds());
            if (cursor.Offset == 0)
                cursor.Offset = Math.Max(0, (Scalar(db, $"SELECT min(rowid) FROM {messageTable} WHERE time_created >= {floor}") ?? maxRow + 1) - 1);

            var hasParent = Exists(db, $"SELECT 1 FROM pragma_table_info('{sessionTable}') WHERE name = 'parent_id'");
            var hasParts = Exists(db, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'part'");
            var role = version2 ? "m.type" : Field("m.data", "$.role");
            var toolCalls = version2
                ? $"CASE WHEN {role} = 'assistant' AND {Field("m.data", "$.finish")} NOT IN ('tool-calls', 'unknown') THEN EXISTS (SELECT 1 FROM json_each(CASE WHEN json_valid(m.data) THEN json_extract(m.data, '$.content') ELSE '[]' END) c WHERE json_extract(c.value, '$.type') = 'tool' AND coalesce(json_extract(c.value, '$.executed'), 0) = 0) ELSE 0 END"
                : hasParts
                    ? $"CASE WHEN {Field("m.data", "$.role")} = 'assistant' AND {Field("m.data", "$.finish")} NOT IN ('tool-calls', 'unknown') THEN EXISTS (SELECT 1 FROM part p WHERE p.message_id = m.id AND {Field("p.data", "$.type")} = 'tool' AND coalesce({Field("p.data", "$.metadata.providerExecuted")}, 0) = 0 AND NOT (coalesce({Field("p.data", "$.state.status")}, '') = 'error' AND coalesce({Field("p.data", "$.state.metadata.interrupted")}, 0) = 1)) ELSE 0 END"
                    : "0";
            var autoCompaction = !version2 && hasParts
                ? $"CASE WHEN {Field("m.data", "$.role")} = 'assistant' AND {Field("m.data", "$.summary")} = 1 THEN EXISTS (SELECT 1 FROM part p WHERE p.message_id = {Field("m.data", "$.parentID")} AND {Field("p.data", "$.type")} = 'compaction' AND {Field("p.data", "$.auto")} = 1) ELSE 0 END"
                : "0";
            var writing = $"(coalesce({role} = 'assistant' AND {Field("m.data", "$.time.completed")} IS NULL, 0) OR coalesce({role} = 'compaction' AND {Field("m.data", "$.status")} = 'running', 0))";
            var fields = string.Join(", ", Values.Select(v => $"json_extract(m.data, '{v.Path}')"));
            var select = $"SELECT m.rowid, m.id, m.session_id, m.time_created, m.time_updated, length(m.data), s.directory, {(hasParent ? "s.parent_id" : "NULL")}, " +
                         $"CASE WHEN json_valid(m.data) THEN json_array({role}, {fields}) END, {toolCalls}, {autoCompaction}, {writing}, " +
                         $"CASE WHEN json_valid(m.data) THEN json_type(m.data, '$.error') END FROM {messageTable} m JOIN {sessionTable} s ON m.session_id = s.id";
            var now = DateTime.UtcNow;

            if (cursor.OpenRows.Count > 0)
            {
                var open = cursor.OpenRows.Keys.ToList();
                var found = new HashSet<string>(StringComparer.Ordinal);
                using (var stmt = Statement.Prepare(db, select + $" WHERE m.id IN ({string.Join(", ", open.Select(_ => "?"))}) ORDER BY m.rowid"))
                {
                    if (stmt is null) return lines;
                    for (var i = 0; i < open.Count; i++) stmt.Bind(i + 1, open[i]);
                    while (stmt.Step())
                    {
                        if (stmt.Text(1) is not { } id || !cursor.OpenRows.TryGetValue(id, out var known)) continue;
                        found.Add(id);
                        if (stmt.Int(4) == known.Updated && stmt.Int(5) == known.Length)
                        {
                            if (now - known.Changed > Abandoned) cursor.OpenRows.Remove(id);
                            continue;
                        }
                        HandOver(stmt, cursor, now, version2, lines);
                    }
                }
                foreach (var id in open.Where(id => !found.Contains(id))) cursor.OpenRows.Remove(id);
            }

            using (var stmt = Statement.Prepare(db, select + " WHERE m.rowid > ? ORDER BY m.rowid"))
            {
                if (stmt is null) return lines;
                stmt.Bind(1, cursor.Offset);
                while (stmt.Step())
                {
                    cursor.Offset = Math.Max(cursor.Offset, stmt.Int(0));
                    HandOver(stmt, cursor, now, version2, lines);
                }
            }
        }
        catch (DllNotFoundException)
        {
            Core.Log.Warn("winsqlite3.dll is missing; OpenCode usage is unavailable");
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Reading OpenCode's database failed: {ex.Message}");
        }
        finally
        {
            if (db != IntPtr.Zero) Sqlite.sqlite3_close_v2(db);
        }
        return lines;
    }

    private static void HandOver(Statement stmt, AgentLogCursor cursor, DateTime now, bool version2, List<string> lines)
    {
        if (stmt.Text(1) is not { } id || stmt.Text(2) is not { } session || stmt.IsNull(3)) return;
        var created = stmt.Int(3);
        var updated = stmt.IsNull(4) ? created : stmt.Int(4);
        if (stmt.Int(11) != 0) cursor.OpenRows[id] = (stmt.Int(4), stmt.Int(5), now);
        else cursor.OpenRows.Remove(id);
        if (stmt.Text(8) is not { } extracted) return;
        JsonArray? found;
        try { found = JsonNode.Parse(extracted) as JsonArray; }
        catch { return; }
        if (found is null || found.Count != Values.Length + 1) return;
        var json = new JsonObject();
        if (found[0] is not null) json["role"] = found[0]!.DeepClone();
        for (var i = 0; i < Values.Length; i++)
            if (found[i + 1] is { } v) json[Values[i].Name] = v.DeepClone();
        if (json.Remove("cwd", out var cwd)) json["path"] = new JsonObject { ["cwd"] = cwd };
        json["id"] = id;
        json["session_id"] = session;
        json["parent_session_id"] = stmt.Text(7) ?? string.Empty;
        json["directory"] = stmt.Text(6) ?? string.Empty;
        json["time_created"] = created;
        json["time_updated"] = updated;
        if (stmt.Int(9) != 0) json["tool_calls"] = true;
        if (stmt.Int(10) != 0) json["auto_compaction"] = true;
        if (stmt.Text(12) is { } error && error != "null") json["error"] = true;
        if (version2 && (string?)json["role"] == "assistant" && json["finish"] is null && json["error"] is null && json["tokens"] is null &&
            json["time"]?["completed"] is not null) return;
        if (version2) json["version"] = 2;
        lines.Add(json.ToJsonString());
    }

    private static string Field(string column, string path) => $"(CASE WHEN json_valid({column}) THEN json_extract({column}, '{path}') END)";

    private static bool Exists(IntPtr db, string sql)
    {
        using var stmt = Statement.Prepare(db, sql);
        return stmt?.Step() == true;
    }

    private static long? Scalar(IntPtr db, string sql)
    {
        using var stmt = Statement.Prepare(db, sql);
        return stmt is not null && stmt.Step() && !stmt.IsNull(0) ? stmt.Int(0) : null;
    }

    private sealed class Statement : IDisposable
    {
        private IntPtr _stmt;
        private Statement(IntPtr stmt) => _stmt = stmt;

        public static Statement? Prepare(IntPtr db, string sql)
        {
            var bytes = Encoding.UTF8.GetBytes(sql);
            return Sqlite.sqlite3_prepare_v2(db, bytes, bytes.Length, out var stmt, IntPtr.Zero) == 0 && stmt != IntPtr.Zero ? new Statement(stmt) : null;
        }

        public void Bind(int index, long value) => Sqlite.sqlite3_bind_int64(_stmt, index, value);

        public void Bind(int index, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Sqlite.sqlite3_bind_text(_stmt, index, bytes, bytes.Length, new IntPtr(-1));
        }

        public bool Step() => Sqlite.sqlite3_step(_stmt) == Sqlite.SQLITE_ROW;
        public long Int(int column) => Sqlite.sqlite3_column_int64(_stmt, column);
        public bool IsNull(int column) => Sqlite.sqlite3_column_type(_stmt, column) == Sqlite.SQLITE_NULL;

        public string? Text(int column)
        {
            if (IsNull(column)) return null;
            var ptr = Sqlite.sqlite3_column_text(_stmt, column);
            var len = Sqlite.sqlite3_column_bytes(_stmt, column);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr, len);
        }

        public void Dispose()
        {
            if (_stmt != IntPtr.Zero) Sqlite.sqlite3_finalize(_stmt);
            _stmt = IntPtr.Zero;
        }
    }

    private static class Sqlite
    {
        private const string Lib = "winsqlite3.dll";
        public const int SQLITE_OPEN_READONLY = 0x1;
        public const int SQLITE_ROW = 100;
        public const int SQLITE_NULL = 5;

        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern long sqlite3_column_int64(IntPtr stmt, int column);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_column_bytes(IntPtr stmt, int column);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_column_type(IntPtr stmt, int column);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] public static extern int sqlite3_finalize(IntPtr stmt);
    }
}
