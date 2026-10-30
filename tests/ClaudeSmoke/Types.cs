namespace CodexTokenOverlay;
internal sealed record TurnTimingSnapshot(DateTimeOffset? StartedUtc, TimeSpan? CompletedDuration, bool IsRunning)
{
    public TimeSpan? Elapsed(DateTimeOffset now) => CompletedDuration ?? (StartedUtc is { } started && IsRunning ? now - started : null);
}
internal sealed record TokenSnapshot(string ThreadId,string LogPath,long TotalTokens,long InputTokens,long CachedInputTokens,long OutputTokens,long ReasoningOutputTokens,long ContextUsedTokens,long ContextWindowTokens,DateTime UpdatedAtUtc)
{
    public TurnTimingSnapshot? TurnTiming {get;init;}
}
