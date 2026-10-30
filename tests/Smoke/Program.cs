using Microsoft.Data.Sqlite;
using System.Text.Json;
using TunnelLab.TokenOverlay;

static void Check(bool ok,string label){if(!ok)throw new Exception("FAILED: "+label);}
static string Temp()=>Path.Combine(Path.GetTempPath(),"tl-overlay-"+Guid.NewGuid().ToString("N")+".db");
static void Execute(SqliteConnection db,string sql)
{
    using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();
}
static void Message(SqliteConnection db,string table,string sid,string role,object value,long timestamp)
{
    using var cmd=db.CreateCommand();
    cmd.CommandText="INSERT INTO "+table+"(session_id,time_created,data) VALUES($sid,$time,$data)";
    cmd.Parameters.AddWithValue("$sid",sid);
    cmd.Parameters.AddWithValue("$time",timestamp);
    cmd.Parameters.AddWithValue("$data",JsonSerializer.Serialize(value));
    cmd.ExecuteNonQuery();
}
var v1=Temp();
var v2=Temp();
try
{
    using(var db=new SqliteConnection("Data Source="+v1))
    {
        db.Open();
        Execute(db,@"CREATE TABLE session(id TEXT PRIMARY KEY,parent_id TEXT,title TEXT,
            time_updated INTEGER,time_created INTEGER,tokens_input INTEGER,
            tokens_output INTEGER,tokens_reasoning INTEGER,tokens_cache_read INTEGER,
            tokens_cache_write INTEGER,cost REAL);
            CREATE TABLE message(id TEXT,session_id TEXT,time_created INTEGER,data TEXT);
            INSERT INTO session VALUES('root',NULL,'Tunnel Coffee',2000000,1000000,644,6359,34,16308,0,0.01);
            INSERT INTO session VALUES('child','root','worker',2000001,1000001,2100,1555,0,7000,0,0.002);");
        Message(db,"message","root","assistant",new {role="assistant",modelID="mercury-3"},1000000);
    }
    var aggregate=OpenCodeReader.Read(v1);
    Check(aggregate is not null,"V1 session rollups");
    Check(aggregate!.Total==34000 && aggregate.MainSessionTokens==23345,"V1 parent + child totals");
    Check(aggregate.ChildSessions==1,"child session");
    Check(aggregate.Cost>0.011&&aggregate.Cost<0.013,"cost");
    Check(aggregate.Model=="mercury-3","model identity");
    Check(aggregate.WorkedFor==null,"do not invent a 456h task timer");

    var epoch=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-65000;
    using(var db=new SqliteConnection("Data Source="+v2))
    {
        db.Open();
        Execute(db,@"CREATE TABLE session_v2(id TEXT PRIMARY KEY,parent_id TEXT,title TEXT,
            time_updated INTEGER,time_created INTEGER);
            CREATE TABLE session_message(id TEXT,session_id TEXT,time_created INTEGER,data TEXT);");
        using(var cmd=db.CreateCommand())
        {
            cmd.CommandText="INSERT INTO session_v2 VALUES('modern',NULL,'Tunnel Coffee landing page',$ts,$ts)";
            cmd.Parameters.AddWithValue("$ts",epoch);cmd.ExecuteNonQuery();
        }
        var user=new {role="user",time=new {created=epoch}};
        var assistant=new {role="assistant",modelID="mercury-3",
            tokens=new {input=644,output=6359,reasoning=34,
                cache=new {read=16308,write=0}},
            cost=0.01,time=new {created=epoch+1000,completed=epoch+56000}};
        Message(db,"session_message","modern","user",user,epoch);
        Message(db,"session_message","modern","assistant",new {info=assistant},epoch+1000);
    }
    var modern=OpenCodeReader.Read(v2,windowTitle:"Tunnel Coffee landing page");
    Check(modern is not null,"V2 tables recognized without session token columns");
    Check(modern!.Total==23345,"V2 message JSON counters");
    Check(modern.Input==644 && modern.CacheRead==16308 && modern.Output==6359 && modern.Reasoning==34,
        "separate buckets");
    Check(modern.LastDuration==TimeSpan.FromSeconds(56),"V2 validated last turn");
    Check(modern.Model=="mercury-3","V2 model extraction");
    Check(modern.Note?.Contains("Matched window title")==true,"window title selection");

    string userLine=JsonSerializer.Serialize(new {type="user",timestamp="2026-10-30T10:00:00Z",
        message=new {content="Write HTML"}});
    string assistantLine=JsonSerializer.Serialize(new {type="assistant",
        message=new {id="m1",usage=new {input_tokens=8,output_tokens=12,
            cache_read_input_tokens=50,cache_creation_input_tokens=20}}});
    var transcript=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".jsonl");
    try
    {
        File.WriteAllLines(transcript,new []{userLine,assistantLine,assistantLine,
            JsonSerializer.Serialize(new {type="system",subtype="turn_duration",durationMs=56000})});
        var claude=TranscriptReader.ReadFile(SourceKind.Claude,transcript);
        Check(claude?.Total==90,"Claude de-duplicates streamed message");
        Check(claude?.LastDuration==TimeSpan.FromSeconds(56),"Claude turn duration");
    }
    finally{File.Delete(transcript);}
    var codexLog=Path.Combine(Path.GetTempPath(),"rollout-"+Guid.NewGuid()+".jsonl");
    try
    {
        File.WriteAllLines(codexLog,new[]{
            "{\"type\":\"session_meta\",\"payload\":{\"originator\":\"Codex Desktop\",\"id\":\"root\"}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":128000,\"total_token_usage\":{\"input_tokens\":500,\"cached_input_tokens\":400,\"output_tokens\":80,\"reasoning_output_tokens\":20},\"last_token_usage\":{\"total_tokens\":400}}}}",
            "{\"type\":\"event_msg\",\"timestamp\":\"2026-10-30T10:01:00Z\",\"payload\":{\"type\":\"task_complete\",\"duration_ms\":56000}}"
        });
        var codex=TranscriptReader.ReadFile(SourceKind.Codex,codexLog);
        Check(codex?.Total==580,"Codex does not double count cached reasoning buckets");
        Check(codex?.LastDuration==TimeSpan.FromSeconds(56),"Codex timer");
    }
    finally{File.Delete(codexLog);}

    var manifestRoot=Path.Combine(Path.GetTempPath(),"tl-manifest-"+Guid.NewGuid().ToString("N"));
    var transcriptRoot=Path.Combine(Path.GetTempPath(),"tl-project-"+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(manifestRoot);Directory.CreateDirectory(transcriptRoot);
    try
    {
        var transcriptId=Guid.NewGuid().ToString();
        File.WriteAllLines(Path.Combine(transcriptRoot,transcriptId+".jsonl"),
            new[]{userLine,assistantLine,
                JsonSerializer.Serialize(new{type="system",subtype="turn_duration",durationMs=12300})});
        var metadata=JsonSerializer.Serialize(new {
            cliSessionId=transcriptId,title="Tunnel Coffee landing page",
            lastActivityAt=DateTimeOffset.UtcNow.ToString("O")
        });
        File.WriteAllText(Path.Combine(manifestRoot,"local_test.json"),metadata);
        var c=ClaudeDesktopReader.Read("Tunnel Coffee landing page",
            new[]{manifestRoot},new[]{transcriptRoot});
        Check(c?.SessionName=="Tunnel Coffee landing page","Claude Desktop manifest title mapping");
        Check(c?.Total==90,"Claude Desktop correct manifest transcript");
        Check(c?.Note?.Contains("Title matched")==true,"Claude title match disclosure");

        // An active Windows MSIX session can leave an unrenamed .json.tmp index.
        File.Delete(Path.Combine(manifestRoot,"local_test.json"));
        var temporaryIndex=Path.Combine(manifestRoot,"local_"+transcriptId+".json.tmp");
        File.WriteAllText(temporaryIndex,JsonSerializer.Serialize(new {
            title="Tunnel Coffee landing page",lastActivityAt=DateTimeOffset.UtcNow.ToString("O")
        }));
        var tmp=ClaudeDesktopReader.Read("Tunnel Coffee landing page",
            new[]{manifestRoot},new[]{transcriptRoot});
        Check(tmp?.Total==90,"recover active temp index without cliSessionId when UUID matches transcript");

        // Some versions store the CLI session ID under state.
        File.Delete(temporaryIndex);
        File.WriteAllText(Path.Combine(manifestRoot,"local_index.json"),
            JsonSerializer.Serialize(new {
                title="Tunnel Coffee landing page",state=new {cliSessionId=transcriptId},
                lastActivityAt=DateTimeOffset.UtcNow.ToString("O")
            }));
        var nested=ClaudeDesktopReader.Read("Tunnel Coffee landing page",
            new[]{manifestRoot},new[]{transcriptRoot});
        Check(nested?.Total==90,"nested CLI ID index resolves to JSONL");
    }
    finally
    {
        Directory.Delete(manifestRoot,true);Directory.Delete(transcriptRoot,true);
    }

    Console.WriteLine("PASS: OpenCode V1/V2, manual mapping, Claude Desktop metadata and Codex.");
}
finally
{
    SqliteConnection.ClearAllPools();
    if(File.Exists(v1))File.Delete(v1);
    if(File.Exists(v2))File.Delete(v2);
}
