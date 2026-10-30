using System.Globalization;

namespace TunnelLab.TokenOverlay;

internal enum SourceKind { Codex, Claude, OpenCode }

internal sealed record UsageSnapshot(
    SourceKind Source,
    string SessionId,
    string SessionName,
    string Model,
    long Input,
    long CacheRead,
    long CacheWrite,
    long Output,
    long Reasoning,
    long ContextUsed,
    long ContextLimit,
    TimeSpan? LastDuration,
    DateTimeOffset? ActiveSince,
    double? Cost,
    int ChildSessions = 0,
    long MainSessionTokens = 0,
    string? Note = null)
{
    // Categories are disjoint. Never count cached input or reasoning output twice.
    public long Total => Input + CacheRead + CacheWrite + Output + Reasoning;
    public TimeSpan? WorkedFor => ActiveSince is { } start
        ? (DateTimeOffset.UtcNow > start ? DateTimeOffset.UtcNow - start : TimeSpan.Zero)
        : LastDuration;
    public string TimeText => WorkedFor is { } duration ? FormatTime(duration) : "—";
    public static string FormatCount(long value)
    {
        if (value >= 1_000_000) return (value / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "M";
        if (value >= 1000) return (value / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
    public static string FormatTime(TimeSpan t)
    {
        var seconds = Math.Max(0L, (long)t.TotalSeconds);
        if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + "s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds / 3600}h {seconds % 3600 / 60}m";
    }
}

internal static class LocalPaths
{
    public static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string CodexRoot
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CODEX_HOME");
            return Path.Combine(string.IsNullOrWhiteSpace(env) ? Path.Combine(UserHome, ".codex") : env, "sessions");
        }
    }
    public static string ClaudeRoot => Path.Combine(UserHome, ".claude", "projects");
    public static string OpenCodeDatabase
    {
        get
        {
            var overrideDb = Environment.GetEnvironmentVariable("OPENCODE_DB");
            if (!string.IsNullOrWhiteSpace(overrideDb)) return Path.GetFullPath(overrideDb);
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(UserHome, ".local", "share") : xdg, "opencode", "opencode.db");
        }
    }
}
