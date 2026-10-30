using System.Globalization;
using System.Text.Json;

namespace CodexTokenOverlay;

/// <summary>
/// Read-only Claude Code local transcript monitor. No network, hooks, or Claude process injection.
/// </summary>
internal sealed class ClaudeSessionMonitor
{
    private readonly string[] _roots;
    private string? _selected;
    private DateTime _lastScanUtc;
    private DateTime _lastWriteUtc;
    private TokenSnapshot? _cached;

    public long ActiveSessionVersion { get; private set; }
    public string? ActiveThreadId => _selected is null ? null : Path.GetFileNameWithoutExtension(_selected);

    public ClaudeSessionMonitor()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var locations = new List<string> {
            Path.Combine(user, ".claude", "projects"),
            Path.Combine(appData, "Claude", "local-agent-mode-sessions"),
            Path.Combine(appData, "Claude", "claude-code-sessions")
        };

        // MSIX package installations redirect their Roaming folders under LocalCache.
        var packages = Path.Combine(localAppData, "Packages");
        try
        {
            if (Directory.Exists(packages))
                foreach (var pkg in Directory.EnumerateDirectories(packages, "Claude_*"))
                {
                    locations.Add(Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "local-agent-mode-sessions"));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        _roots = locations.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public TokenSnapshot? Poll(bool forceFullScan = false)
    {
        if (forceFullScan || DateTime.UtcNow - _lastScanUtc >= TimeSpan.FromSeconds(2))
        {
            _lastScanUtc = DateTime.UtcNow;
            var mostRecent = FindNewestSession();
            if (!string.Equals(_selected, mostRecent, StringComparison.OrdinalIgnoreCase))
            {
                _selected = mostRecent;
                _lastWriteUtc = DateTime.MinValue;
                _cached = null;
                ActiveSessionVersion++;
            }
        }

        if (_selected is null) return null;
        try
        {
            var modified = File.GetLastWriteTimeUtc(_selected);
            if (_cached is not null && _lastWriteUtc == modified) return _cached;
            var snapshot = ParseSession(_selected, modified);
            if (snapshot is not null)
            {
                _lastWriteUtc = modified;
                _cached = snapshot;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return _cached;
    }

    private string? FindNewestSession()
    {
        string? best = null;
        var time = DateTime.MinValue;
        foreach (var root in _roots)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    if (path.Split(Path.DirectorySeparatorChar).Any(x => x.Equals("subagents", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _)) continue;
                    var write = File.GetLastWriteTimeUtc(path);
                    if (write > time)
                    {
                        time = write;
                        best = path;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return best;
    }

    public static TokenSnapshot? ParseSession(string path, DateTime modifiedUtc)
    {
        var seen = new Dictionary<string, (long Input, long Output, long Cache, long Context)>(StringComparer.Ordinal);
        var sessionId = Path.GetFileNameWithoutExtension(path);
        DateTimeOffset? lastUser = null;
        TurnTimingSnapshot? timing = null;
        long context = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length < 35 || line.Length > 16_000_000) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("isSidechain", out var sidechain)
                        && sidechain.ValueKind == JsonValueKind.True) continue;
                    if (!GetString(root, "type", out var kind)) continue;
                    if (kind == "assistant"
                        && root.TryGetProperty("message", out var msg)
                        && msg.ValueKind == JsonValueKind.Object
                        && msg.TryGetProperty("usage", out var usage)
                        && usage.ValueKind == JsonValueKind.Object)
                    {
                        var id = GetString(msg, "id", out var messageId)
                            ? messageId! : (GetString(root, "uuid", out var uuid) ? uuid! : null);
                        if (id is null) continue;
                        var uncached = GetNumber(usage, "input_tokens");
                        var cacheRead = GetNumber(usage, "cache_read_input_tokens");
                        var cacheWrite = GetNumber(usage, "cache_creation_input_tokens");
                        var output = GetNumber(usage, "output_tokens");
                        var input = uncached + cacheRead + cacheWrite;
                        // Streaming blocks repeat message.id; latest recorded usage replaces earlier estimates.
                        seen[id] = (input, output, cacheRead, input);
                        context = input;
                    }
                    else if (kind == "system" && GetString(root, "subtype", out var subtype)
                             && subtype == "turn_duration")
                    {
                        if (root.TryGetProperty("durationMs", out var ms)
                            && ms.ValueKind == JsonValueKind.Number && ms.TryGetDouble(out var duration)
                            && duration >= 0 && double.IsFinite(duration))
                        {
                            timing = new TurnTimingSnapshot(null, TimeSpan.FromMilliseconds(duration), false);
                        }
                        lastUser = null;
                    }
                    else if (kind == "user" && IsHumanUser(root))
                    {
                        var timestamp = GetTimestamp(root);
                        if (timestamp is not null)
                        {
                            lastUser = timestamp;
                            timing = new TurnTimingSnapshot(timestamp, null, true);
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or FormatException or ArgumentOutOfRangeException or OverflowException)
                {
                    // The last transcript line may still be incomplete while Claude is writing.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (seen.Count == 0 && timing is null) return null;
        var totalInput = seen.Values.Sum(x => x.Input);
        var totalOutput = seen.Values.Sum(x => x.Output);
        var totalCache = seen.Values.Sum(x => x.Cache);
        return new TokenSnapshot(sessionId, path, totalInput + totalOutput, totalInput, totalCache,
            totalOutput, 0, context, 0, modifiedUtc) { TurnTiming = timing };
    }

    private static bool IsHumanUser(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return false;
        if (!msg.TryGetProperty("content", out var content)) return true;
        if (content.ValueKind == JsonValueKind.String) return true;
        if (content.ValueKind != JsonValueKind.Array) return false;
        // Tool responses in Claude's transcripts are also user records, but not human turns.
        return !content.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.Object
            && GetString(item, "type", out var t) && t == "tool_result");
    }

    private static DateTimeOffset? GetTimestamp(JsonElement root)
    {
        if (GetString(root, "timestamp", out var raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp))
            return timestamp.ToUniversalTime();
        return null;
    }
    private static bool GetString(JsonElement obj, string key, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(key, out var member) || member.ValueKind != JsonValueKind.String)
            return false;
        value = member.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }
    private static long GetNumber(JsonElement obj, string key)
    {
        if (obj.TryGetProperty(key, out var member) && member.ValueKind == JsonValueKind.Number
            && member.TryGetInt64(out var number)) return Math.Max(0, number);
        return 0;
    }
}
