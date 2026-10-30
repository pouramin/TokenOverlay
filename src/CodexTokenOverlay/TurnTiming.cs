using System.Globalization;
using System.Text.Json;

namespace CodexTokenOverlay;

/// <summary>
/// Read-only parser for the most recent root-turn lifecycle event in a Codex JSONL tail.
/// Values come from task_started / task_complete / turn_aborted, not from token estimates.
/// </summary>
internal sealed record TurnTimingSnapshot(DateTimeOffset? StartedUtc, TimeSpan? CompletedDuration, bool IsRunning)
{
    public TimeSpan? Elapsed(DateTimeOffset nowUtc)
    {
        if (CompletedDuration is { } complete) return complete;
        if (IsRunning && StartedUtc is { } start)
            return nowUtc >= start ? nowUtc - start : TimeSpan.Zero;
        return null;
    }
}

internal static class TurnTimingParser
{
    public static TurnTimingSnapshot? ParseLatest(string text)
    {
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.Contains("\"task_started\"", StringComparison.Ordinal) &&
                !line.Contains("\"task_complete\"", StringComparison.Ordinal) &&
                !line.Contains("\"turn_aborted\"", StringComparison.Ordinal))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!IsString(root, "type", "event_msg") ||
                    !root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object ||
                    !payload.TryGetProperty("type", out var eventType) ||
                    eventType.ValueKind != JsonValueKind.String)
                    continue;

                var kind = eventType.GetString();
                if (kind == "task_started")
                {
                    var start = GetTime(payload, "started_at") ?? GetTime(root, "timestamp");
                    if (start is not null)
                        return new TurnTimingSnapshot(start, null, true);
                }
                else if (kind is "task_complete" or "turn_aborted")
                {
                    var start = GetTime(payload, "started_at");
                    var end = GetTime(payload, "completed_at") ?? GetTime(root, "timestamp");
                    TimeSpan? duration = null;
                    if (payload.TryGetProperty("duration_ms", out var durationMs)
                        && durationMs.ValueKind == JsonValueKind.Number
                        && durationMs.TryGetDouble(out var millis)
                        && double.IsFinite(millis) && millis >= 0)
                        duration = TimeSpan.FromMilliseconds(millis);
                    else if (start is not null && end is not null && end >= start)
                        duration = end - start;
                    if (duration is not null)
                        return new TurnTimingSnapshot(start, duration, false);
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or OverflowException or FormatException)
            {
                // A partially-written last JSONL line is not a valid event.
            }
        }
        return null;
    }

    private static bool IsString(JsonElement element, string key, string expected) =>
        element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() == expected;

    private static DateTimeOffset? GetTime(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToUniversalTime();
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var unixSeconds)
            && double.IsFinite(unixSeconds)
            && unixSeconds > 946684800 && unixSeconds < 4102444800)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000));
        return null;
    }
}
