using System.Globalization;

namespace CodexTokenOverlay;

internal sealed record OverlayMetric(
    DisplayField Field,
    string CompactLabel,
    string ExpandedLabel,
    string Value,
    bool HasValue);

internal sealed record OverlayPresentation(
    OverlayMetric Primary,
    OverlayMetric Secondary,
    IReadOnlyList<OverlayMetric> ExpandedRows,
    double ContextPercent,
    bool ShowContextProgress,
    string? StatusText);

internal static class OverlayPresentationBuilder
{
    private const string NoValue = "—";

    public static OverlayPresentation CreateWaiting(
        string statusText,
        DisplayField primaryField,
        DisplayField secondaryField,
        DisplayField visibleFields)
    {
        ValidateHighlightedFields(primaryField, secondaryField);
        return new OverlayPresentation(
            CreateWaitingMetric(primaryField),
            CreateWaitingMetric(secondaryField),
            CreateExpandedRows(visibleFields, primaryField, secondaryField, CreateWaitingMetric),
            0,
            false,
            SanitizeSingleLine(statusText));
    }

    public static OverlayPresentation Create(
        TokenSnapshot snapshot,
        DisplayField primaryField,
        DisplayField secondaryField,
        DisplayField visibleFields)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateHighlightedFields(primaryField, secondaryField);
        var contextPercent = Math.Clamp(snapshot.ContextPercent, 0, 100);
        return new OverlayPresentation(
            CreateMetric(snapshot, primaryField, contextPercent),
            CreateMetric(snapshot, secondaryField, contextPercent),
            CreateExpandedRows(
                visibleFields,
                primaryField,
                secondaryField,
                field => CreateMetric(snapshot, field, contextPercent)),
            contextPercent,
            (visibleFields & DisplayField.ContextPercent) != 0 && snapshot.ContextWindowTokens > 0,
            null);
    }

    public static string FormatTokenCount(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    public static string ShortThreadId(string threadId, int maximumLength = 12)
    {
        var singleLineThreadId = SanitizeSingleLine(threadId);
        if (singleLineThreadId.Length <= maximumLength)
        {
            return singleLineThreadId;
        }

        if (maximumLength <= 1)
        {
            return maximumLength == 1 ? "…" : string.Empty;
        }

        var prefixLength = Math.Min(4, maximumLength - 1);
        var suffixLength = Math.Min(6, maximumLength - prefixLength - 1);
        return string.Concat(
            singleLineThreadId.AsSpan(0, prefixLength),
            "…",
            singleLineThreadId.AsSpan(singleLineThreadId.Length - suffixLength, suffixLength));
    }

    public static string GetFieldMenuText(DisplayField field)
    {
        return field switch
        {
            DisplayField.Total => "Total tokens",
            DisplayField.Input => "Input tokens",
            DisplayField.Output => "Output tokens",
            DisplayField.CacheHit => "Cache hits",
            DisplayField.CacheHitRate => "Cache hit rate",
            DisplayField.CacheMiss => "Cache misses (calculated)",
            DisplayField.Context => "Context usage",
            DisplayField.ContextPercent => "Context percentage",
            DisplayField.Reasoning => "Reasoning output",
            DisplayField.Thread => "Session ID",
            DisplayField.Worked => "Response time",
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported display field.")
        };
    }

    private static void ValidateHighlightedFields(DisplayField primaryField, DisplayField secondaryField)
    {
        if (!DisplayFieldRules.IsSingleSupported(primaryField)
            || !DisplayFieldRules.IsSingleSupported(secondaryField))
        {
            throw new ArgumentException("Collapsed metrics must be single supported fields.");
        }
    }

    private static IReadOnlyList<OverlayMetric> CreateExpandedRows(
        DisplayField visibleFields,
        DisplayField primaryField,
        DisplayField secondaryField,
        Func<DisplayField, OverlayMetric> createMetric)
    {
        return DisplayFieldRules.Ordered
            .Where(field => (visibleFields & field) != 0)
            .Where(field => field != primaryField && field != secondaryField)
            .Select(createMetric)
            .ToArray();
    }

    private static OverlayMetric CreateWaitingMetric(DisplayField field)
    {
        var labels = GetLabels(field);
        return new OverlayMetric(field, labels.Compact, labels.Expanded, NoValue, false);
    }

    private static OverlayMetric CreateMetric(
        TokenSnapshot snapshot,
        DisplayField field,
        double contextPercent)
    {
        var labels = GetLabels(field);
        var value = field switch
        {
            DisplayField.Total => FormatTokenCount(snapshot.TotalTokens),
            DisplayField.Input => FormatTokenCount(snapshot.InputTokens),
            DisplayField.Output => FormatTokenCount(snapshot.OutputTokens),
            DisplayField.CacheHit => FormatTokenCount(snapshot.CachedInputTokens),
            DisplayField.CacheHitRate => $"{snapshot.CacheHitPercent:0}%",
            DisplayField.CacheMiss => FormatTokenCount(snapshot.UncachedInputTokens),
            DisplayField.Context => snapshot.ContextWindowTokens > 0
                ? $"{FormatTokenCount(snapshot.ContextUsedTokens)} / {FormatTokenCount(snapshot.ContextWindowTokens)}"
                : $"{FormatTokenCount(snapshot.ContextUsedTokens)} / N/A",
            DisplayField.ContextPercent => snapshot.ContextWindowTokens > 0 ? $"{contextPercent:0}%" : NoValue,
            DisplayField.Reasoning => FormatTokenCount(snapshot.ReasoningOutputTokens),
            DisplayField.Thread => ShortThreadId(snapshot.ThreadId),
            DisplayField.Worked => FormatElapsed(snapshot.TurnTiming?.Elapsed(DateTimeOffset.UtcNow)),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported display field.")
        };
        var hasValue = field switch
        {
            DisplayField.Thread => !string.IsNullOrWhiteSpace(snapshot.ThreadId),
            DisplayField.Worked => snapshot.TurnTiming?.Elapsed(DateTimeOffset.UtcNow) is not null,
            DisplayField.ContextPercent => snapshot.ContextWindowTokens > 0,
            _ => true
        };
        return new OverlayMetric(field, labels.Compact, labels.Expanded, value, hasValue);
    }

    private static (string Compact, string Expanded) GetLabels(DisplayField field)
    {
        return field switch
        {
            DisplayField.Total => ("Total", "Total tokens"),
            DisplayField.Input => ("In", "Input"),
            DisplayField.Output => ("Out", "Output"),
            DisplayField.CacheHit => ("Hit", "Cache hits"),
            DisplayField.CacheHitRate => ("Hit rate", "Cache hit rate"),
            DisplayField.CacheMiss => ("Miss", "Cache miss"),
            DisplayField.Context => ("Context", "Context usage"),
            DisplayField.ContextPercent => ("Context", "Context used"),
            DisplayField.Reasoning => ("Reasoning", "Reasoning output"),
            DisplayField.Thread => ("Session", "Session"),
            DisplayField.Worked => ("Time", "Worked for"),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported display field.")
        };
    }

    public static string FormatElapsed(TimeSpan? elapsed)
    {
        if (elapsed is null) return NoValue;
        var seconds = Math.Max(0L, (long)Math.Floor(elapsed.Value.TotalSeconds));
        if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + "s";
        var minutes = seconds / 60;
        var remainder = seconds % 60;
        if (minutes < 60) return $"{minutes}m {remainder}s";
        return $"{minutes / 60}h {minutes % 60}m";
    }

    private static string SanitizeSingleLine(string value)
    {
        return value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}

internal sealed class PresentationProbeRequest
{
    public List<PresentationProbeCase> Cases { get; set; } = new();
}

internal sealed class PresentationProbeCase
{
    public string Name { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public TokenSnapshot? Snapshot { get; set; }
    public int? PrimaryField { get; set; }
    public int? SecondaryField { get; set; }
    public int? VisibleFields { get; set; }
    public string? StatusText { get; set; }
}

internal sealed record PresentationProbeCaseResult(string Name, OverlayPresentation Presentation);

internal sealed record PresentationProbeResult(IReadOnlyList<PresentationProbeCaseResult> Cases);

internal static class PresentationProbe
{
    public static PresentationProbeResult Execute(PresentationProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<PresentationProbeCaseResult>();
        foreach (var probeCase in request.Cases)
        {
            var primaryField = RequireField(probeCase.PrimaryField, nameof(probeCase.PrimaryField));
            var secondaryField = RequireField(probeCase.SecondaryField, nameof(probeCase.SecondaryField));
            var visibleFields = (DisplayField)(probeCase.VisibleFields ?? 0);
            var presentation = probeCase.Operation switch
            {
                "Create" => OverlayPresentationBuilder.Create(
                    probeCase.Snapshot ?? throw new ArgumentException("Create requires a snapshot.", nameof(probeCase)),
                    primaryField,
                    secondaryField,
                    visibleFields),
                "Waiting" => OverlayPresentationBuilder.CreateWaiting(
                    probeCase.StatusText ?? string.Empty,
                    primaryField,
                    secondaryField,
                    visibleFields),
                _ => throw new ArgumentException($"Unsupported presentation probe operation: {probeCase.Operation}", nameof(probeCase))
            };
            results.Add(new PresentationProbeCaseResult(probeCase.Name, presentation));
        }

        return new PresentationProbeResult(results);
    }

    private static DisplayField RequireField(int? value, string parameterName)
    {
        if (!value.HasValue)
        {
            throw new ArgumentException("Display probe requires a field.", parameterName);
        }

        return (DisplayField)value.Value;
    }
}
