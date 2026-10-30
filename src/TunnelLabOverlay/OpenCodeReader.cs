using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace TunnelLab.TokenOverlay;

/// <summary>Reads V1 and V2 OpenCode SQLite stores. All connections are strictly read-only.</summary>
internal static class OpenCodeReader
{
    private sealed record Row(string Id,string? Parent,string Title,long Updated,
        long Input,long Read,long Write,long Output,long Reasoning,double Cost,bool HasAggregates);
    private sealed record Counts(long Input,long Read,long Write,long Output,long Reasoning,double Cost,int Messages)
    {
        public long Total=>Input+Read+Write+Output+Reasoning;
        public static Counts Empty=>new(0,0,0,0,0,0,0);
    }

    public static UsageSnapshot? Read(string? databasePath=null,string? windowTitle=null,string? selectedSessionId=null)
    {
        var paths=databasePath is { Length: > 0 }?new []{databasePath}:LocalPaths.OpenCodeDatabases;
        foreach(var path in paths.Where(File.Exists).OrderByDescending(p=>File.GetLastWriteTimeUtc(p)))
        {
            var item=ReadOne(path,windowTitle,selectedSessionId);
            if(item is not null)return item;
        }
        return null;
    }

    private static UsageSnapshot? ReadOne(string path,string? windowTitle,string? selectedSessionId)
    {
        try
        {
            var cs=new SqliteConnectionStringBuilder {DataSource=path,Mode=SqliteOpenMode.ReadOnly,
                Cache=SqliteCacheMode.Private,Pooling=false}.ToString();
            using var db=new SqliteConnection(cs);
            db.Open();
            using(var cmd=db.CreateCommand())
            {
                cmd.CommandText="PRAGMA query_only=ON; PRAGMA busy_timeout=900;";
                cmd.ExecuteNonQuery();
            }
            // V2 is a separate session/message family; prefer the one with recent activity.
            var candidates=new List<(string Sessions,string Messages,List<Row> Rows)>();
            foreach(var (st,mt) in new[]{("session_v2","session_message"),("session","message")})
            {
                if(!Columns(db,st).Contains("id"))continue;
                var rows=Sessions(db,st);
                if(rows.Count>0)candidates.Add((st,mt,rows));
            }
            if(candidates.Count==0)return null;
            foreach(var source in candidates.OrderByDescending(c=>c.Rows.Max(s=>s.Updated)))
            {
                var rows=source.Rows;
                var pinned=selectedSessionId is null?null:rows.FirstOrDefault(s=>s.Id==selectedSessionId);
                if(selectedSessionId is not null && pinned is null)continue;
                var match=rows.Where(s=>string.IsNullOrWhiteSpace(s.Parent))
                    .OrderByDescending(s=>s.Updated)
                    .FirstOrDefault(s=>TitleMatches(s.Title,windowTitle));
                var newest=pinned??match??rows.OrderByDescending(s=>s.Updated).First();
                var byId=rows.ToDictionary(s=>s.Id,StringComparer.Ordinal);
                var root=newest;
                var seen=new HashSet<string>(StringComparer.Ordinal){root.Id};
                while(root.Parent is {Length:>0} pid && byId.TryGetValue(pid,out var parent) && seen.Add(pid))
                    root=parent;
                var family=Descendants(rows,root.Id);
                var messagesTable=Columns(db,source.Messages);
                if(!messagesTable.Contains("session_id")||!messagesTable.Contains("data"))continue;
                var all=Counts.Empty;
                var main=Counts.Empty;
                foreach(var entry in family)
                {
                    var message=Messages(db,source.Messages,entry.Id);
                    // Session rollups may be absent, stale, or zero after migration.
                    // Message rows provide the authoritative source when available.
                    var count=message.Messages>0?message:entry.HasAggregates
                        ?new Counts(entry.Input,entry.Read,entry.Write,entry.Output,entry.Reasoning,entry.Cost,0)
                        :Counts.Empty;
                    if(entry.Id==root.Id)main=count;
                    all=new Counts(all.Input+count.Input,all.Read+count.Read,
                        all.Write+count.Write,all.Output+count.Output,
                        all.Reasoning+count.Reasoning,all.Cost+count.Cost,
                        all.Messages+count.Messages);
                }
                // A session without a recorded response is not a measured zero-token session.
                if(all.Messages==0 && all.Total==0)continue;
                var timing=Turn(db,source.Messages,root.Id);
                var model=Model(db,source.Messages,root.Id);
                var note=(pinned is not null?"Selected session"
                    :match is not null?"Matched window title":"Latest local session")
                    +" | "+Path.GetFileName(path)+" ("+source.Sessions+")";
                return new UsageSnapshot(SourceKind.OpenCode,root.Id,root.Title,model,
                    all.Input,all.Read,all.Write,all.Output,all.Reasoning,0,0,
                    timing.Completed,timing.RunningSince,all.Cost,
                    family.Count-1,main.Total,note);
            }
        }
        catch(Exception ex) when(ex is SqliteException or IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException or FormatException) { }
        return null;
    }

    public static IReadOnlyList<(string Id,string Title)> RecentSessions()
    {
        var entries=new List<Row>();
        foreach(var path in LocalPaths.OpenCodeDatabases.Where(File.Exists))
        {
            try
            {
                using var db=new SqliteConnection(new SqliteConnectionStringBuilder {
                    DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false
                }.ToString());
                db.Open();
                foreach(var table in new[]{"session_v2","session"})
                    if(Columns(db,table).Contains("id"))
                        entries.AddRange(Sessions(db,table)
                            .Where(x=>string.IsNullOrWhiteSpace(x.Parent)).Take(30));
            }
            catch(Exception ex) when(ex is SqliteException or IOException
                or UnauthorizedAccessException or InvalidOperationException){}
        }
        return entries.OrderByDescending(s=>s.Updated)
            .DistinctBy(s=>s.Id)
            .Take(20).Select(s=>(s.Id,s.Title)).ToArray();
    }

    private static bool TitleMatches(string sessionTitle,string? windowTitle)
    {
        if(string.IsNullOrWhiteSpace(windowTitle)||sessionTitle.Length<8)return false;
        return windowTitle.Contains(sessionTitle,StringComparison.OrdinalIgnoreCase)
            || sessionTitle.Contains(windowTitle,StringComparison.OrdinalIgnoreCase);
    }
    private static HashSet<string> Columns(SqliteConnection db,string table)
    {
        var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd=db.CreateCommand();
        cmd.CommandText="PRAGMA table_info("+table+")";
        using var r=cmd.ExecuteReader();
        while(r.Read())set.Add(r.GetString(1));
        return set;
    }
    private static List<Row> Sessions(SqliteConnection db,string table)
    {
        var columns=Columns(db,table);
        var fields=new[]{"id","parent_id","title","time_updated","time_created",
            "tokens_input","tokens_output","tokens_reasoning","tokens_cache_read","tokens_cache_write","cost"};
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT "+string.Join(", ",fields.Select(f=>columns.Contains(f)?f:"NULL AS "+f))+
            " FROM "+table+" ORDER BY time_updated DESC LIMIT 500";
        var list=new List<Row>();
        using var r=cmd.ExecuteReader();
        while(r.Read())
        {
            var id=Str(r,0);
            if(string.IsNullOrWhiteSpace(id))continue;
            list.Add(new Row(id,Str(r,1),Str(r,2)??"OpenCode session",Long(r,3)==0?Long(r,4):Long(r,3),
                Long(r,5),Long(r,8),Long(r,9),Long(r,6),Long(r,7),Double(r,10),
                columns.Contains("tokens_input") && columns.Contains("tokens_output")));
        }
        return list;
    }
    private static List<Row> Descendants(List<Row> all,string id)
    {
        var selected=new HashSet<string>(StringComparer.Ordinal){id};
        bool changed;
        do
        {
            changed=false;
            foreach(var r in all)
                if(r.Parent is not null && selected.Contains(r.Parent) && selected.Add(r.Id))changed=true;
        }while(changed);
        return all.Where(r=>selected.Contains(r.Id)).ToList();
    }
    private static IEnumerable<JsonElement> Rows(SqliteConnection db,string table,string id)
    {
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT data FROM "+table+" WHERE session_id=$id ORDER BY time_created ASC";
        cmd.Parameters.AddWithValue("$id",id);
        using var reader=cmd.ExecuteReader();
        while(reader.Read())
        {
            if(reader.IsDBNull(0))continue;
            JsonDocument? doc=null;
            try{doc=JsonDocument.Parse(reader.GetString(0));}
            catch(JsonException){}
            if(doc is null)continue;
            using(doc){yield return doc.RootElement.Clone();}
        }
    }
    private static JsonElement Message(JsonElement root)
    {
        if(root.ValueKind==JsonValueKind.Object && root.TryGetProperty("info",out var info)
            && info.ValueKind==JsonValueKind.Object)return info;
        if(root.ValueKind==JsonValueKind.Object && root.TryGetProperty("data",out var data)
            && data.ValueKind==JsonValueKind.Object)return data;
        return root;
    }
    private static Counts Messages(SqliteConnection db,string table,string id)
    {
        long input=0,read=0,write=0,output=0,reasoning=0;
        double cost=0;int count=0;
        foreach(var raw in Rows(db,table,id))
        {
            var j=Message(raw);
            if(Property(j,"role")!="assistant")continue;
            if(!j.TryGetProperty("tokens",out var t)||t.ValueKind!=JsonValueKind.Object)continue;
            count++;
            input+=Number(t,"input");
            output+=Number(t,"output");
            reasoning+=Number(t,"reasoning");
            cost+=DecimalNumber(j,"cost");
            if(t.TryGetProperty("cache",out var cache)&&cache.ValueKind==JsonValueKind.Object)
            {read+=Number(cache,"read");write+=Number(cache,"write");}
        }
        return new Counts(input,read,write,output,reasoning,cost,count);
    }
    private static string Model(SqliteConnection db,string table,string id)
    {
        string latest="";
        foreach(var raw in Rows(db,table,id))
        {
            var j=Message(raw);
            if(Property(j,"role")=="assistant")
                latest=Property(j,"modelID")??Property(j,"model")??latest;
        }
        return latest;
    }
    private static (TimeSpan? Completed,DateTimeOffset? RunningSince) Turn(SqliteConnection db,string table,string id)
    {
        long latestUser=0,finish=0;
        foreach(var raw in Rows(db,table,id))
        {
            var j=Message(raw);
            if(!j.TryGetProperty("time",out var tm)||tm.ValueKind!=JsonValueKind.Object)continue;
            var created=Number(tm,"created");
            if(!ValidEpoch(created))continue;
            var role=Property(j,"role");
            if(role=="user")
            {
                if(created>=latestUser){latestUser=created;finish=0;}
            }
            else if(role=="assistant"&&created>=latestUser && latestUser>0)
            {
                var completed=Number(tm,"completed");
                if(completed>=created && ValidEpoch(completed))
                    finish=Math.Max(finish,completed);
            }
        }
        if(latestUser==0)return (null,null);
        if(finish>latestUser)
        {
            var duration=TimeSpan.FromMilliseconds(finish-latestUser);
            if(duration>TimeSpan.Zero && duration<TimeSpan.FromHours(24))return(duration,null);
        }
        // Treat only recent user prompts as actively running. Never display multi-day clocks.
        var start=DateTimeOffset.FromUnixTimeMilliseconds(latestUser);
        var age=DateTimeOffset.UtcNow-start;
        return age>=TimeSpan.Zero && age<TimeSpan.FromHours(4) ?(null,start):(null,null);
    }
    private static bool ValidEpoch(long milliseconds)=>milliseconds>=1577836800000 && milliseconds<4102444800000;
    private static string? Property(JsonElement j,string name)=>
        j.ValueKind==JsonValueKind.Object && j.TryGetProperty(name,out var x)&&x.ValueKind==JsonValueKind.String?x.GetString():null;
    private static long Number(JsonElement j,string name)=>
        j.ValueKind==JsonValueKind.Object && j.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt64(out var n)?Math.Max(0,n):0;
    private static double DecimalNumber(JsonElement j,string name)=>
        j.ValueKind==JsonValueKind.Object && j.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n)&&double.IsFinite(n)?Math.Max(0,n):0;
    private static string? Str(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture);
    private static long Long(SqliteDataReader r,int i)=>r.IsDBNull(i)?0:Convert.ToInt64(r.GetValue(i),CultureInfo.InvariantCulture);
    private static double Double(SqliteDataReader r,int i)=>r.IsDBNull(i)?0:Convert.ToDouble(r.GetValue(i),CultureInfo.InvariantCulture);
}
