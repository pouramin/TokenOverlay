using System.Globalization;
using System.Text.Json;

namespace TunnelLab.TokenOverlay;

internal static class TranscriptReader
{
    public static UsageSnapshot? Read(SourceKind source,string? windowTitle=null)
    {
        if (source == SourceKind.OpenCode) return OpenCodeReader.Read(windowTitle:windowTitle);
        if (source == SourceKind.Claude) return ClaudeDesktopReader.Read(windowTitle);
        var root = LocalPaths.CodexRoot;
        if (!Directory.Exists(root)) return null;
        string? newest = null;
        var lastWrite = DateTime.MinValue;
        try
        {
            foreach(var path in Directory.EnumerateFiles(root,"*.jsonl",SearchOption.AllDirectories))
            {
                if(source==SourceKind.Claude && path.Contains(Path.DirectorySeparatorChar+"subagents"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    continue;
                var time=File.GetLastWriteTimeUtc(path);
                if(time <= lastWrite)continue;
                newest=path;lastWrite=time;
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
        if(newest is null)return null;
        return ReadFile(source,newest);
    }

    public static UsageSnapshot? ReadFile(SourceKind source,string path)
    {
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var reader=new StreamReader(stream);
            return source == SourceKind.Codex ? ReadCodex(reader,path) : ReadClaude(reader,path);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static UsageSnapshot? ReadCodex(TextReader reader,string path)
    {
        long input=0,read=0,output=0,reasoning=0,context=0,limit=0;
        DateTimeOffset? running=null;
        TimeSpan? lastDuration=null;
        string? session=null;
        bool seenUsage=false,confirmedDesktop=false;
        string? line;
        while((line=reader.ReadLine())!=null)
        {
            try
            {
                using var doc=JsonDocument.Parse(line);
                var root=doc.RootElement;
                var kind=Str(root,"type");
                if(kind=="session_meta" && root.TryGetProperty("payload",out var meta))
                {
                    session=Str(meta,"id");
                    confirmedDesktop=Str(meta,"originator")=="Codex Desktop"||Str(meta,"source")=="vscode";
                }
                if(kind!="event_msg"||!root.TryGetProperty("payload",out var p))continue;
                var action=Str(p,"type");
                if(action=="token_count"&&p.TryGetProperty("info",out var info)&&info.ValueKind==JsonValueKind.Object)
                {
                    if(info.TryGetProperty("total_token_usage",out var total)&&total.ValueKind==JsonValueKind.Object)
                    {
                        input=Num(total,"input_tokens");
                        read=Num(total,"cached_input_tokens");
                        output=Num(total,"output_tokens");
                        reasoning=Num(total,"reasoning_output_tokens");
                        limit=Num(info,"model_context_window");
                        if(info.TryGetProperty("last_token_usage",out var recent)&&recent.ValueKind==JsonValueKind.Object)
                            context=Num(recent,"total_tokens");
                        seenUsage=true;
                    }
                }
                else if(action=="task_started")
                {
                    running=ParseDate(p,"started_at")??ParseDate(root,"timestamp");
                    lastDuration=null;
                }
                else if(action is "task_complete" or "turn_aborted")
                {
                    var elapsed=Num(p,"duration_ms");
                    var finished=ParseDate(p,"completed_at")??ParseDate(root,"timestamp");
                    lastDuration=elapsed>0?TimeSpan.FromMilliseconds(elapsed):
                        running!=null&&finished!=null&&finished>=running
                            ?finished-running:null;
                    running=null;
                }
            }
            catch(JsonException) { }
        }
        if(!seenUsage&&!confirmedDesktop)return null;
        var id=session??Path.GetFileNameWithoutExtension(path);
        return new UsageSnapshot(SourceKind.Codex,id,id,"",
            Math.Max(0,input-read),read,0,Math.Max(0,output-reasoning),reasoning,
            context,limit,lastDuration,running,null,
            0,0,"Tokens recorded by Codex session log");
    }

    private static UsageSnapshot? ReadClaude(TextReader reader,string path)
    {
        var usage=new Dictionary<string,(long Input,long Read,long Write,long Output)>(StringComparer.Ordinal);
        DateTimeOffset? running=null;
        TimeSpan? lastDuration=null;
        string? line;
        while((line=reader.ReadLine())!=null)
        {
            try
            {
                using var doc=JsonDocument.Parse(line);
                var j=doc.RootElement;
                if(j.TryGetProperty("isSidechain",out var side)&&side.ValueKind==JsonValueKind.True)continue;
                var kind=Str(j,"type");
                if(kind=="assistant"&&j.TryGetProperty("message",out var msg)&&msg.ValueKind==JsonValueKind.Object
                    &&msg.TryGetProperty("usage",out var used)&&used.ValueKind==JsonValueKind.Object)
                {
                    var messageId=Str(msg,"id")??Str(j,"uuid");
                    if(messageId is null)continue;
                    var current=(Input:Num(used,"input_tokens"),
                        Read:Num(used,"cache_read_input_tokens"),
                        Write:Num(used,"cache_creation_input_tokens"),
                        Output:Num(used,"output_tokens"));
                    if(usage.TryGetValue(messageId,out var previous))
                        usage[messageId]=(Math.Max(previous.Input,current.Input),
                            Math.Max(previous.Read,current.Read),
                            Math.Max(previous.Write,current.Write),
                            Math.Max(previous.Output,current.Output));
                    else usage[messageId]=current;
                }
                else if(kind=="user"&&j.TryGetProperty("message",out var userMessage)&&userMessage.ValueKind==JsonValueKind.Object
                         &&userMessage.TryGetProperty("content",out var content)&&
                         (content.ValueKind==JsonValueKind.String
                          ||content.ValueKind==JsonValueKind.Array&&!content.EnumerateArray().Any(x=>Str(x,"type")=="tool_result")))
                {
                    running=ParseDate(j,"timestamp");
                    lastDuration=null;
                }
                else if(kind=="system"&&Str(j,"subtype")=="turn_duration")
                {
                    var milliseconds=Num(j,"durationMs");
                    lastDuration=milliseconds>0?TimeSpan.FromMilliseconds(milliseconds):null;
                    running=null;
                }
            }
            catch(JsonException) { }
        }
        if(usage.Count==0&&running==null&&lastDuration==null)return null;
        long input=0,read=0,write=0,output=0;
        foreach(var u in usage.Values){input+=u.Input;read+=u.Read;write+=u.Write;output+=u.Output;}
        var id=Path.GetFileNameWithoutExtension(path);
        return new UsageSnapshot(SourceKind.Claude,id,id,"",input,read,write,output,0,0,0,
            lastDuration,running,null,0,0,"Latest local Claude Code session");
    }

    private static string? Str(JsonElement j,string name)=>
        j.ValueKind==JsonValueKind.Object&&j.TryGetProperty(name,out var e)&&e.ValueKind==JsonValueKind.String
        ?e.GetString():null;
    private static long Num(JsonElement j,string name)=>
        j.ValueKind==JsonValueKind.Object&&j.TryGetProperty(name,out var e)&&e.ValueKind==JsonValueKind.Number
        &&e.TryGetInt64(out var n)?Math.Max(0,n):0;
    private static DateTimeOffset? ParseDate(JsonElement j,string name)
    {
        if(j.ValueKind!=JsonValueKind.Object||!j.TryGetProperty(name,out var value))return null;
        if(value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var seconds)&&double.IsFinite(seconds)
            &&seconds>946684800&&seconds<4102444800)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds*1000));
        if(value.ValueKind==JsonValueKind.String
           &&DateTimeOffset.TryParse(value.GetString(),CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,out var time))return time.ToUniversalTime();
        return null;
    }
}
