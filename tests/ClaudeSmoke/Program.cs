using CodexTokenOverlay;
var path=Path.Combine(Path.GetTempPath(), "claude-smoke-" + Guid.NewGuid() + ".jsonl");
try
{
    static void Assert(bool ok,string msg) { if(!ok) throw new Exception("FAIL: "+msg); }
    string User(string date,string text) => System.Text.Json.JsonSerializer.Serialize(new {type="user",timestamp=date,message=new {content=text}});
    string Assistant(string id,long input,long output,long read,long write) =>
        System.Text.Json.JsonSerializer.Serialize(new {type="assistant",message=new {id,usage=new {
            input_tokens=input,output_tokens=output,cache_read_input_tokens=read,cache_creation_input_tokens=write}}});
    string Time(int ms) => System.Text.Json.JsonSerializer.Serialize(new {type="system",subtype="turn_duration",durationMs=ms,timestamp="2026-10-30T10:01:00Z"});
    File.WriteAllLines(path,new[] {User("2026-10-30T10:00:00Z","write a game"),
        Assistant("msg-1",10,15,100,20),Assistant("msg-1",10,25,100,20),
        Assistant("msg-2",3,4,0,0),Time(56000)});
    var actual=ClaudeSessionMonitor.ParseSession(path,DateTime.UtcNow)!;
    Assert(actual.InputTokens==133,"count input+cache+write once per unique message id");
    Assert(actual.CachedInputTokens==100,"cache read");
    Assert(actual.OutputTokens==29,"last usage for repeated streaming message");
    Assert(actual.TotalTokens==162,"total");
    Assert(actual.TurnTiming?.CompletedDuration==TimeSpan.FromSeconds(56),"turn duration");
    File.AppendAllLines(path,new [] {User("2026-10-30T10:01:01Z","new turn")});
    var running=ClaudeSessionMonitor.ParseSession(path,DateTime.UtcNow)!;
    Assert(running.TurnTiming?.IsRunning==true,"live turn");
    File.AppendAllLines(path,new [] {System.Text.Json.JsonSerializer.Serialize(new {type="user",message=new {content=new[]{new {type="tool_result"}}}})});
    var stillRunning=ClaudeSessionMonitor.ParseSession(path,DateTime.UtcNow)!;
    Assert(stillRunning.TurnTiming?.IsRunning==true,"ignore tool result as human user");
    Console.WriteLine("Claude parser: 7 checks passed.");
}
finally { if(File.Exists(path)) File.Delete(path); }
