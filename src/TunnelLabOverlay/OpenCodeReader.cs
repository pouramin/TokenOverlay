using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace TunnelLab.TokenOverlay;

/// <summary>Read-only SQLite session counters from OpenCode, including separately recorded child sessions.</summary>
internal static class OpenCodeReader
{
    private sealed record Row(string Id, string? Parent, string Title, long Updated,
        long Input, long CacheRead, long CacheWrite, long Output, long Reasoning,
        double Cost, bool HasCounters);

    public static UsageSnapshot? Read(string? databasePath = null)
    {
        var path = databasePath ?? LocalPaths.OpenCodeDatabase;
        if (!File.Exists(path)) return null;
        try
        {
            // Read-only: never create or upgrade the OpenCode database.
            var cs = new SqliteConnectionStringBuilder {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using (var mode = conn.CreateCommand())
            {
                mode.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 900;";
                mode.ExecuteNonQuery();
            }
            var columns = Columns(conn, "session");
            if (!columns.Contains("id")) return null;
            var entries = LoadSessions(conn, columns);
            if (entries.Count == 0) return null;
            var byId = entries.ToDictionary(x => x.Id, StringComparer.Ordinal);
            // Most recently edited session can be a subagent; walk to the root.
            var newest = entries.OrderByDescending(x => x.Updated).First();
            var root = newest;
            var guarded = new HashSet<string>(StringComparer.Ordinal) { root.Id };
            while (root.Parent is { Length: > 0 } pid && byId.TryGetValue(pid, out var parent) && guarded.Add(pid))
                root = parent;
            var children = Descendants(entries, root.Id);
            if (children.Count == 0) return null;
            long input = 0, read = 0, write = 0, output = 0, reasoning = 0;
            double cost = 0;
            foreach (var node in children)
            {
                var c = node.HasCounters ? node : ReadMessageCounters(conn, node);
                input += c.Input; read += c.CacheRead; write += c.CacheWrite;
                output += c.Output; reasoning += c.Reasoning; cost += c.Cost;
            }
            var main = root.HasCounters ? root : ReadMessageCounters(conn, root);
            long mainTotal = main.Input + main.CacheRead + main.CacheWrite + main.Output + main.Reasoning;
            var model = LatestModel(conn, root.Id);
            var timing = LastTurn(conn, root.Id);
            // OpenCode session counters are cumulative, not the current context window.
            // Do not invent a context percent from them.
            return new UsageSnapshot(SourceKind.OpenCode, root.Id, root.Title, model,
                input, read, write, output, reasoning, 0, 0,
                timing.Completed, timing.RunningSince, cost, children.Count - 1, mainTotal,
                children.Count > 1 ? "Includes separate subagent sessions" : "Cumulative session tokens");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static HashSet<string> Columns(SqliteConnection conn, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static List<Row> LoadSessions(SqliteConnection conn, HashSet<string> cols)
    {
        var fields = new[] { "id", "parent_id", "title", "time_updated", "time_created",
            "tokens_input", "tokens_output", "tokens_reasoning", "tokens_cache_read", "tokens_cache_write", "cost" };
        using var cmd = conn.CreateCommand();
        // Only fixed, known SQL identifiers are ever included in the SELECT list.
        cmd.CommandText = "SELECT " + string.Join(", ", fields.Select(f => cols.Contains(f) ? f : "NULL AS " + f)) + " FROM session";
        var result = new List<Row>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = Str(reader, 0);
            if (string.IsNullOrWhiteSpace(id)) continue;
            long updated = Long(reader, 3);
            if (updated == 0) updated = Long(reader, 4);
            result.Add(new Row(id, Str(reader, 1), Str(reader, 2) ?? "OpenCode session", updated,
                Long(reader, 5), Long(reader, 8), Long(reader, 9), Long(reader, 6),
                Long(reader, 7), Double(reader, 10),
                cols.Contains("tokens_input") && cols.Contains("tokens_output")));
        }
        return result;
    }

    private static List<Row> Descendants(List<Row> all, string rootId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { rootId };
        bool changed;
        do
        {
            changed = false;
            foreach (var row in all)
                if (row.Parent != null && seen.Contains(row.Parent) && seen.Add(row.Id)) changed = true;
        } while (changed);
        return all.Where(r => seen.Contains(r.Id)).ToList();
    }

    private static Row ReadMessageCounters(SqliteConnection conn, Row row)
    {
        if (!Columns(conn, "message").Contains("data")) return row;
        long input=0, read=0, write=0, output=0, reasoning=0;
        double cost=0;
        using var cmd=conn.CreateCommand();
        cmd.CommandText="SELECT data FROM message WHERE session_id=$sid";
        cmd.Parameters.AddWithValue("$sid", row.Id);
        using var rdr=cmd.ExecuteReader();
        while (rdr.Read())
        {
            if (rdr.IsDBNull(0)) continue;
            try
            {
                using var doc=JsonDocument.Parse(rdr.GetString(0));
                var info=doc.RootElement;
                if (PropString(info,"role")!="assistant" || !info.TryGetProperty("tokens",out var t)) continue;
                input+=PropLong(t,"input"); output+=PropLong(t,"output"); reasoning+=PropLong(t,"reasoning");
                cost+=PropDouble(info,"cost");
                if (t.TryGetProperty("cache",out var cache))
                { read+=PropLong(cache,"read"); write+=PropLong(cache,"write"); }
            }
            catch(JsonException) { }
        }
        return row with { Input=input,CacheRead=read,CacheWrite=write,Output=output,
            Reasoning=reasoning,Cost=cost,HasCounters=true };
    }

    private static string LatestModel(SqliteConnection conn,string session)
    {
        if (!Columns(conn,"message").Contains("data")) return "";
        using var cmd=conn.CreateCommand();
        cmd.CommandText="SELECT data FROM message WHERE session_id=$sid ORDER BY time_created DESC LIMIT 50";
        cmd.Parameters.AddWithValue("$sid",session);
        using var rdr=cmd.ExecuteReader();
        while(rdr.Read())
        {
            if(rdr.IsDBNull(0))continue;
            try
            {
                using var d=JsonDocument.Parse(rdr.GetString(0));
                var j=d.RootElement;
                if(PropString(j,"role")=="assistant")
                    return PropString(j,"modelID") ?? PropString(j,"model") ?? "";
            }
            catch(JsonException) { }
        }
        return "";
    }
    private static (TimeSpan? Completed,DateTimeOffset? RunningSince) LastTurn(SqliteConnection conn,string session)
    {
        if (!Columns(conn,"message").Contains("data")) return (null,null);
        using var cmd=conn.CreateCommand();
        cmd.CommandText="SELECT data FROM message WHERE session_id=$sid ORDER BY time_created DESC LIMIT 80";
        cmd.Parameters.AddWithValue("$sid",session);
        var rows=new List<(string Role,long Created,long Completed)>();
        using(var rdr=cmd.ExecuteReader())
            while(rdr.Read())
            {
                if(rdr.IsDBNull(0))continue;
                try
                {
                    using var doc=JsonDocument.Parse(rdr.GetString(0));
                    var j=doc.RootElement;
                    var role=PropString(j,"role");
                    if(role is not ("user" or "assistant")) continue;
                    long created=0,completed=0;
                    if(j.TryGetProperty("time",out var time)){created=PropLong(time,"created");completed=PropLong(time,"completed");}
                    rows.Add((role!,created,completed));
                }
                catch(JsonException) { }
            }
        if(rows.Count==0)return (null,null);
        // Last user turn and last assistant completion time, using stored metadata.
        var latestUser=rows.FirstOrDefault(x=>x.Role=="user");
        if(latestUser.Created<=0)return (null,null);
        var lastAssistant=rows.FirstOrDefault(x=>x.Role=="assistant" && x.Created>=latestUser.Created);
        if(lastAssistant.Completed>latestUser.Created)
            return (TimeSpan.FromMilliseconds(lastAssistant.Completed-latestUser.Created),null);
        if(lastAssistant.Created>0 && lastAssistant.Completed==0 || latestUser.Created>0 && lastAssistant.Created==0)
            return (null,DateTimeOffset.FromUnixTimeMilliseconds(latestUser.Created));
        return (null,null);
    }
    private static string? Str(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture);
    private static long Long(SqliteDataReader r,int i)=>r.IsDBNull(i)?0:Convert.ToInt64(r.GetValue(i),CultureInfo.InvariantCulture);
    private static double Double(SqliteDataReader r,int i)=>r.IsDBNull(i)?0:Convert.ToDouble(r.GetValue(i),CultureInfo.InvariantCulture);
    private static string? PropString(JsonElement j,string p)=>j.ValueKind==JsonValueKind.Object && j.TryGetProperty(p,out var x)&&x.ValueKind==JsonValueKind.String?x.GetString():null;
    private static long PropLong(JsonElement j,string p)=>j.ValueKind==JsonValueKind.Object&&j.TryGetProperty(p,out var x)&&x.ValueKind==JsonValueKind.Number&&x.TryGetInt64(out var n)?Math.Max(0,n):0;
    private static double PropDouble(JsonElement j,string p)=>j.ValueKind==JsonValueKind.Object&&j.TryGetProperty(p,out var x)&&x.ValueKind==JsonValueKind.Number&&x.TryGetDouble(out var n)?Math.Max(0,n):0;
}
