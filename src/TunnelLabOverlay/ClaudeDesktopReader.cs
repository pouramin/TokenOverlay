using System.Text.Json;
using System.Globalization;

namespace TunnelLab.TokenOverlay;

/// <summary>
/// Claude Desktop Code-tab manifest reader. Does not confuse unrelated CLI transcripts
/// with the currently selected desktop session.
/// </summary>
internal sealed record ClaudeLocalSession(string Path,string SessionId,string Project,DateTime UpdatedUtc);

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
        report.Add("Most recently written Claude transcripts (not necessarily the active tab):");
        foreach(var item in RecentSessions(15))
            report.Add(item.UpdatedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss")
                +" | "+item.SessionId[..8]+" | "+item.Project);
        report.Add("");
        report.Add("Select the correct transcript via tray > Claude session.");
        report.Add("No message contents or credentials are included in this report.");
        return string.Join(Environment.NewLine,report);
    }

    // Transcripts are the source of usage. A Desktop manifest is useful but not
    // mandatory: MSIX versions may omit or move the manifest entirely.
    internal static IReadOnlyList<ClaudeLocalSession> RecentSessions(int max=20,
        string[]? transcriptRoots=null)
    {
        var roots=transcriptRoots??new[]{
            LocalPaths.ClaudeRoot,Path.Combine(LocalPaths.UserHome,".claude","desktop")
        };
        var found=new List<ClaudeLocalSession>();
        foreach(var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!Directory.Exists(root))continue;
            try
            {
                foreach(var file in Directory.EnumerateFiles(root,"*.jsonl",SearchOption.AllDirectories))
                {
                    if(file.Contains(Path.DirectorySeparatorChar+"subagents"+Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))continue;
                    var id=Path.GetFileNameWithoutExtension(file);
                    if(!Guid.TryParse(id,out _))continue;
                    try
                    {
                        var updated=File.GetLastWriteTimeUtc(file);
                        var project=Path.GetFileName(Path.GetDirectoryName(file))??"Claude";
                        found.Add(new ClaudeLocalSession(file,id,project,updated));
                    }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
        }
        return found.OrderByDescending(x=>x.UpdatedUtc)
            .DistinctBy(x=>x.Path,StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1,max)).ToArray();
    }

    internal static UsageSnapshot? Read(string? windowTitle=null,string[]? manifestRoots=null,
        string[]? transcriptRoots=null,string? selectedTranscriptPath=null)
    {
        var metadataRoots=manifestRoots??ManifestRoots();
        var logs=transcriptRoots??TranscriptRoots(metadataRoots);
        if(!string.IsNullOrWhiteSpace(selectedTranscriptPath))
        {
            // Only permit local JSONL files below a recognized read-only transcript root.
            string full;
            try { full=Path.GetFullPath(selectedTranscriptPath); }
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException
                or PathTooLongException){return null;}
            if(!full.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase)||
                !Guid.TryParse(Path.GetFileNameWithoutExtension(full),out _)||
                !logs.Any(root=>full.StartsWith(
                    Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                        +Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))
                return null;
            var selected=TranscriptReader.ReadFile(SourceKind.Claude,full);
            return selected is null?null:selected with {
                SessionName=Path.GetFileName(Path.GetDirectoryName(full))??"Claude Code",
                Note="Manually selected local transcript (active tab not verified)"
            };
        }
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
        // No Desktop metadata is not equivalent to no Claude Code usage.
        // The fallback below is explicitly labelled and restricted to recent files.
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
        // Explicitly disclose that newest-file selection can pick CLI/background work.
        var latest=RecentSessions(1,logs).FirstOrDefault();
        if(latest is not null && DateTime.UtcNow-latest.UpdatedUtc<TimeSpan.FromHours(4))
        {
            var fallback=TranscriptReader.ReadFile(SourceKind.Claude,latest.Path);
            if(fallback is not null)
                return fallback with {
                    SessionName=latest.Project,
                    Note="Latest local Claude transcript (active tab NOT verified)"
                };
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
