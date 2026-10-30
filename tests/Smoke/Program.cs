using Microsoft.Data.Sqlite;
using TunnelLab.TokenOverlay;

static void Check(bool ok,string message) {if(!ok)throw new Exception("FAILED "+message);}
var file=Path.Combine(Path.GetTempPath(),"tokenoverlay-tests-"+Guid.NewGuid().ToString("N")+".db");
try
{
    using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file}.ToString()))
    {
        db.Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText=@"
CREATE TABLE session(id TEXT PRIMARY KEY, parent_id TEXT, title TEXT,time_updated INTEGER,time_created INTEGER,
tokens_input INTEGER,tokens_output INTEGER,tokens_reasoning INTEGER,tokens_cache_read INTEGER,
tokens_cache_write INTEGER,cost REAL);
CREATE TABLE message(id TEXT,session_id TEXT,time_created INTEGER,data TEXT);
INSERT INTO session VALUES('root',NULL,'Tunnel Coffee',2000000,1000000,644,6359,34,16308,0,0.01);
INSERT INTO session VALUES('child','root','worker',2000001,1000001,2100,1555,0,7000,0,0.002);
INSERT INTO message VALUES('msg1','root',2000000,'{""role"":""assistant"",""modelID"":""mercury-3"",""time"":{""created"":1000000,""completed"":1056000}}');
INSERT INTO message VALUES('msg0','root',1000000,'{""role"":""user"",""time"":{""created"":1000000}}');";
        cmd.ExecuteNonQuery();
    }
    var open=OpenCodeReader.Read(file);
    Check(open!=null,"SQLite session opened");
    Check(open!.MainSessionTokens==23345,"main session counts all 5 disjoint token categories");
    Check(open.Total==34000,"root + child sessions includes 10,655 extra tokens");
    Check(open.ChildSessions==1,"child counted exactly once");
    Check(open.Input==2744&&open.CacheRead==23308,"input and cache remain separate");
    Check(open.Model=="mercury-3","model extracted");
    Check(open.Cost>0.011 && open.Cost<0.013,"cost sum");
    Check(open.SessionId=="root","resolve parent from newest child");

    string user=System.Text.Json.JsonSerializer.Serialize(new{type="user",timestamp="2026-10-30T10:00:00Z",
        message=new{content="Build app"}});
    string assistant=System.Text.Json.JsonSerializer.Serialize(new{type="assistant",
        message=new{id="m1",usage=new{input_tokens=8,output_tokens=12,cache_read_input_tokens=50,cache_creation_input_tokens=20}}});
    var claudePath=Path.Combine(Path.GetTempPath(),"test-"+Guid.NewGuid().ToString("N")+".jsonl");
    File.WriteAllLines(claudePath,new[]{user,assistant,assistant,
        System.Text.Json.JsonSerializer.Serialize(new{type="system",subtype="turn_duration",durationMs=56000})});
    var claude=TranscriptReader.ReadFile(SourceKind.Claude,claudePath);
    Check(claude?.Total==90,"Claude repeated streamed message dedup");
    Check(claude?.LastDuration==TimeSpan.FromSeconds(56),"Claude turn_duration");
    File.Delete(claudePath);

    var codexPath=Path.Combine(Path.GetTempPath(),"rollout-"+Guid.NewGuid().ToString("D")+".jsonl");
    File.WriteAllLines(codexPath,new[]{
        "{\"type\":\"session_meta\",\"payload\":{\"originator\":\"Codex Desktop\",\"id\":\"root\"}}",
        "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":128000,\"total_token_usage\":{\"input_tokens\":500,\"cached_input_tokens\":400,\"output_tokens\":80,\"reasoning_output_tokens\":20},\"last_token_usage\":{\"total_tokens\":400}}}}",
        "{\"type\":\"event_msg\",\"timestamp\":\"2026-10-30T10:01:00Z\",\"payload\":{\"type\":\"task_complete\",\"duration_ms\":56000}}"
    });
    var codex=TranscriptReader.ReadFile(SourceKind.Codex,codexPath);
    Check(codex?.Total==580,"Codex cached input and reasoning not double counted");
    Check(codex?.LastDuration==TimeSpan.FromSeconds(56),"Codex completed duration");
    File.Delete(codexPath);
    Console.WriteLine("PASS: 12 independently authored parser and aggregation checks");
}
finally{SqliteConnection.ClearAllPools();if(File.Exists(file))File.Delete(file);}