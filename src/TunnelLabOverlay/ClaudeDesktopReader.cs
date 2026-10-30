using System.Text.Json;
using System.Globalization;

namespace TunnelLab.TokenOverlay;

/// <summary>
/// Claude Desktop Code-tab manifest reader. Does not confuse unrelated CLI transcripts
/// with the currently selected desktop session.
/// </summary>
internal static class ClaudeDesktopReader
{
    internal static string Diagnostics()
    {
        var manifests=ManifestRoots();
        var transcripts=TranscriptRoots(manifests);
        var report=new List<string>{"Claude Desktop: read-only local diagnostic", "", "Manifest folders:"};
        var indexes=0;var transcriptsFound=0;
        foreach(var root in manifests.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int count=0;
            try
            {
                if(Directory.Exists(root))
                    count=Directory.EnumerateFiles(root,"local_*.json*",SearchOption.AllDirectories)
                        .Count(file=>file.EndsWith(".json",StringComparison.OrdinalIgnoreCase)
                            ||file.EndsWith(".json.tmp",StringComparison.OrdinalIgnoreCase));
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
            indexes+=count;
            report.Add((count==0?"[0] ":"["+count+"] ")+root);
        }
        report.Add("");report.Add("Transcript folders:");
        foreach(var root in transcripts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int count=0;
            try
            {
                if(Directory.Exists(root))
                    count=Directory.EnumerateFiles(root,"*.jsonl",SearchOption.AllDirectories)
                        .Take(2000).Count();
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
            transcriptsFound+=count;
            report.Add("["+count+"] "+root);
        }
        report.Add("");
        report.Add("Metadata files found: "+indexes);
        report.Add("JSONL files found: "+transcriptsFound);
        report.Add("No file contents, credentials, or messages were read into this report.");
        return string.Join(Environment.NewLine,report);
    }

    internal static UsageSnapshot? Read(string? windowTitle=null,string[]? manifestRoots=null,
        string[]? transcriptRoots=null)
    {
        var metadataRoots=manifestRoots??ManifestRoots();
        var logs=transcriptRoots??TranscriptRoots(metadataRoots);
        var candidates=new List<(string Title,string CliId,DateTimeOffset Updated)>();
        foreach(var root in metadataRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!Directory.Exists(root))continue;
            try
            {
                foreach(var file in Directory.EnumerateFiles(root,"local_*.json*",SearchOption.AllDirectories))
                {
                    if(!file.EndsWith(".json",StringComparison.OrdinalIgnoreCase)
                        && !file.EndsWith(".json.tmp",StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        using var doc=JsonDocument.Parse(File.ReadAllText(file));
                        var j=doc.RootElement;
                        var cliId=Prop(j,"cliSessionId");
                        if(cliId is null && j.TryGetProperty("state",out var state))
                            cliId=Prop(state,"cliSessionId");
                        // Some active Desktop sessions have not received a CLI ID yet:
                        // don't invent one. Attempt the index UUID only as a last resort.
                        if(cliId is null)
                        {
                            var filename=Path.GetFileName(file);
                            if(filename.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase))
                                filename=filename[..^4];
                            var indexId=filename.StartsWith("local_",StringComparison.OrdinalIgnoreCase)
                                ?Path.GetFileNameWithoutExtension(filename)[6..]:null;
                            if(Guid.TryParse(indexId,out _))cliId=indexId;
                        }
                        if(cliId is null || !Guid.TryParse(cliId,out _))continue;
                        var title=Prop(j,"title")??Prop(j,"customTitle")??"Claude Code session";
                        var updated=Timestamp(j,"lastActivityAt")??Timestamp(j,"updatedAt")
                            ??new DateTimeOffset(File.GetLastWriteTimeUtc(file),TimeSpan.Zero);
                        candidates.Add((title,cliId,updated));
                    }
                    catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException
                        or ArgumentOutOfRangeException){}
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
        }
        if(candidates.Count==0)return null; // Do not display unrelated CLI sessions in the Desktop Code tab.
        var unique=candidates.GroupBy(c=>c.CliId,StringComparer.OrdinalIgnoreCase)
            .Select(g=>g.OrderByDescending(x=>x.Updated).First());
        var candidateList=unique.OrderByDescending(c=>Match(c.Title,windowTitle))
            .ThenByDescending(c=>c.Updated).Take(20);
        foreach(var item in candidateList)
        {
            var transcript=FindTranscript(logs,item.CliId);
            if(transcript is null)continue;
            var parsed=TranscriptReader.ReadFile(SourceKind.Claude,transcript);
            if(parsed is null)continue;
            var exactTitle=Match(item.Title,windowTitle);
            var age=DateTimeOffset.UtcNow-item.Updated;
            var label=exactTitle?"Title matched; local Code transcript"
                : age>=TimeSpan.Zero && age<TimeSpan.FromMinutes(20)
                    ?"Recent local Code transcript (tab not verified)"
                    :"Historical local Code transcript (not current tab)";
            return parsed with {SessionName=item.Title,Note=label};
        }
        return null;
    }

    private static string[] ManifestRoots()
    {
        var roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var result=new List<string> {
            Path.Combine(roaming,"Claude","claude-code-sessions"),
            Path.Combine(local,"Claude","claude-code-sessions"),
            Path.Combine(roaming,"Claude","local-agent-mode-sessions"),
            Path.Combine(local,"Claude","local-agent-mode-sessions")
        };
        var packages=Path.Combine(local,"Packages");
        if(Directory.Exists(packages))
        {
            try
            {
                foreach(var p in Directory.EnumerateDirectories(packages))
                {
                    var name=Path.GetFileName(p);
                    if(!name.StartsWith("Claude_",StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith("AnthropicPBC.Claude_",StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add(Path.Combine(p,"LocalCache","Roaming","Claude","claude-code-sessions"));
                    result.Add(Path.Combine(p,"LocalCache","Roaming","Claude","local-agent-mode-sessions"));
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
        }
        return result.ToArray();
    }
    private static string[] TranscriptRoots(IEnumerable<string> manifests)
    {
        var result=new List<string> {
            LocalPaths.ClaudeRoot,
            Path.Combine(LocalPaths.UserHome,".claude","desktop")
        };
        // Legacy desktop installs can keep transcripts in per-session sandboxes.
        foreach(var m in manifests)
        {
            var parent=Directory.GetParent(m)?.FullName;
            if(parent is not null)
            {
                result.Add(Path.Combine(parent,"local-agent-mode-sessions"));
                result.Add(Path.Combine(parent,"claude-code-sessions"));
            }
            // When the manifest root itself is the legacy local-agent tree,
            // its RPM session subfolders may contain JSONL transcripts.
            result.Add(m);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string? FindTranscript(string[] roots,string id)
    {
        var fileName=id+".jsonl";
        foreach(var root in roots)
        {
            if(!Directory.Exists(root))continue;
            try
            {
                foreach(var path in Directory.EnumerateFiles(root,fileName,SearchOption.AllDirectories))
                {
                    if(path.Contains(Path.DirectorySeparatorChar+"subagents"+Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))continue;
                    return path;
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
        }
        return null;
    }
    private static bool Match(string title,string? query)=>
        !string.IsNullOrWhiteSpace(query) && title.Length>7 &&
        (query.Contains(title,StringComparison.OrdinalIgnoreCase)
         ||title.Contains(query,StringComparison.OrdinalIgnoreCase));
    private static string? Prop(JsonElement j,string key)=>
        j.ValueKind==JsonValueKind.Object && j.TryGetProperty(key,out var v)
        && v.ValueKind==JsonValueKind.String?v.GetString():null;
    private static DateTimeOffset? Timestamp(JsonElement j,string key)
    {
        if(j.ValueKind!=JsonValueKind.Object||!j.TryGetProperty(key,out var v))return null;
        if(v.ValueKind==JsonValueKind.String &&
           DateTimeOffset.TryParse(v.GetString(),CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,out var dt))
            return dt.ToUniversalTime();
        if(v.ValueKind==JsonValueKind.Number && v.TryGetInt64(out var t))
        {
            try
            {
                if(t>1577836800000 && t<4102444800000)return DateTimeOffset.FromUnixTimeMilliseconds(t);
                if(t>1577836800 && t<4102444800)return DateTimeOffset.FromUnixTimeSeconds(t);
            }
            catch(ArgumentOutOfRangeException){}
        }
        return null;
    }
}
