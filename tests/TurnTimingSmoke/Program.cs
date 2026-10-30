using CodexTokenOverlay;

static void Assert(bool value, string message)
{
    if (!value) throw new Exception("FAILED: " + message);
}

static string Event(string eventType, string extra = "", string timestamp = "2026-10-30T10:00:00Z") =>
    "{\"timestamp\":\"" + timestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"" + eventType + "\"" + extra + "}}";

var running = TurnTimingParser.ParseLatest(Event("task_started", ",\"started_at\":1793354400"));
Assert(running?.IsRunning == true && running.Elapsed(DateTimeOffset.FromUnixTimeSeconds(1793354456)) == TimeSpan.FromSeconds(56), "running timer");
var finished = TurnTimingParser.ParseLatest(Event("task_complete", ",\"duration_ms\":56001,\"turn_id\":\"abc\""));
Assert(finished?.IsRunning == false && finished.Elapsed(DateTimeOffset.UtcNow) == TimeSpan.FromMilliseconds(56001), "authoritative duration");
var fallback = TurnTimingParser.ParseLatest(Event("task_complete", ",\"started_at\":1793354400,\"completed_at\":1793354456"));
Assert(fallback?.Elapsed(DateTimeOffset.UtcNow) == TimeSpan.FromSeconds(56), "timestamp fallback");
var aborted = TurnTimingParser.ParseLatest(Event("turn_aborted", ",\"duration_ms\":10000"));
Assert(aborted?.Elapsed(DateTimeOffset.UtcNow) == TimeSpan.FromSeconds(10), "aborted turn");
var last = TurnTimingParser.ParseLatest(Event("task_complete", ",\"duration_ms\":9000") + "\n" +
    Event("task_started", ",\"started_at\":1793354500"));
Assert(last?.IsRunning == true, "new active turn supersedes previous");
Assert(TurnTimingParser.ParseLatest("irrelevant text\n{\"type\":\"event_msg\"}") == null, "malformed and missing");
Assert(TurnTimingParser.ParseLatest(Event("task_complete", ",\"duration_ms\":-10")) == null, "negative duration");
Assert(TurnTimingParser.ParseLatest(Event("task_started", ",\"started_at\":\"2026-10-30T10:00:00Z\""))?.IsRunning == true, "ISO timestamp");
Console.WriteLine("TurnTiming parser: 8 smoke tests passed.");
