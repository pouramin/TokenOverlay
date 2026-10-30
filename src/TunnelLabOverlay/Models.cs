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
    public TimeSpan? WorkedFor
    {
        get
        {
            if (ActiveSince is { } start)
            {
                var elapsed = DateTimeOffset.UtcNow - start;
                return elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromHours(24) ? elapsed : null;
            }
            return LastDuration is { } duration && duration >= TimeSpan.Zero
                && duration < TimeSpan.FromHours(24) ? duration : null;
        }
    }
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
    public static string[] OpenCodeDatabases
    {
        get
        {
            var candidates = new List<string>();
            var @override = Environment.GetEnvironmentVariable("OPENCODE_DB");
            if (!string.IsNullOrWhiteSpace(@override))
                candidates.Add(Path.GetFullPath(@override));
            var dataDir = Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(dataDir))
                candidates.Add(Path.Combine(dataDir, "opencode.db"));
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdg))
                candidates.Add(Path.Combine(xdg, "opencode", "opencode.db"));
            candidates.Add(Path.Combine(UserHome, ".local", "share", "opencode", "opencode.db"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "opencode", "opencode.db"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "opencode", "opencode.db"));
            // A user may run the preview/beta channel with a separate database.
            var folders = candidates.Select(Path.GetDirectoryName).Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    candidates.AddRange(Directory.EnumerateFiles(folder, "opencode-*.db",
                        SearchOption.TopDirectoryOnly));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
