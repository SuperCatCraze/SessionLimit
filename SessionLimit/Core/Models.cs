using System.Text.Json.Serialization;

namespace SessionLimit;

/// <summary>One billed model request. The atom of the ledger.</summary>
public sealed class UsageEvent
{
    [JsonPropertyName("ts")]      public DateTimeOffset Timestamp { get; set; }
    [JsonPropertyName("id")]      public string Id { get; set; } = "";      // dedup key
    [JsonPropertyName("model")]   public string Model { get; set; } = "";
    [JsonPropertyName("in")]      public long InputTokens { get; set; }
    [JsonPropertyName("out")]     public long OutputTokens { get; set; }
    [JsonPropertyName("cr")]      public long CacheReadTokens { get; set; }
    [JsonPropertyName("cw5")]     public long CacheWrite5mTokens { get; set; }
    [JsonPropertyName("cw1h")]    public long CacheWrite1hTokens { get; set; }
    [JsonPropertyName("fast")]    public bool Fast { get; set; }
    [JsonPropertyName("src")]     public string Source { get; set; } = "";
    [JsonPropertyName("sid")]     public string? SessionId { get; set; }
    [JsonPropertyName("proj")]    public string? Project { get; set; }

    [JsonIgnore] public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWrite5mTokens + CacheWrite1hTokens;
    [JsonIgnore] public decimal Cost => Pricing.CostOf(this);
}

/// <summary>Aggregated totals over one time window.</summary>
public sealed class WindowStats
{
    public long InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens;
    public decimal Cost;
    public int Requests;
    public DateTimeOffset? WindowStart;
    public DateTimeOffset? WindowEnd;
    public readonly Dictionary<string, ModelSlice> ByModel = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, ModelSlice> ByProject = new(StringComparer.OrdinalIgnoreCase);

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>Time left in the window, or null when the window has no known end.</summary>
    public TimeSpan? Remaining =>
        WindowEnd is { } end && end > DateTimeOffset.Now ? end - DateTimeOffset.Now : null;

    /// <summary>How long the window has been running.</summary>
    public TimeSpan? Elapsed =>
        WindowStart is { } start ? DateTimeOffset.Now - start : null;

    /// <summary>Share of input that came from cache rather than fresh tokens.</summary>
    public double CacheHitRate
    {
        get
        {
            var considered = InputTokens + CacheReadTokens;
            return considered == 0 ? 0 : CacheReadTokens * 100.0 / considered;
        }
    }

    /// <summary>Tokens per minute so far in this window.</summary>
    public double TokensPerMinute =>
        Elapsed is { TotalMinutes: > 0.5 } el ? TotalTokens / el.TotalMinutes : 0;

    /// <summary>
    /// Time until this window reaches 100% at the current burn rate, or null when the
    /// rate is unknown or the window won't be exhausted before it resets anyway.
    /// </summary>
    public TimeSpan? ProjectedExhaustion(double percentUsed)
    {
        if (Elapsed is not { TotalMinutes: > 1 } el || percentUsed <= 0) return null;
        var perMinute = percentUsed / el.TotalMinutes;
        if (perMinute <= 0) return null;
        var minutesLeft = (100 - percentUsed) / perMinute;
        if (minutesLeft <= 0 || double.IsInfinity(minutesLeft)) return null;
        return TimeSpan.FromMinutes(minutesLeft);
    }
}

public sealed class ModelSlice
{
    public long Tokens;
    public decimal Cost;
    public int Requests;
}

/// <summary>Health of one data source, surfaced as a status dot in the overlay.</summary>
public sealed class SourceStatus
{
    public string Name { get; init; } = "";
    public bool Enabled { get; set; }
    public bool Healthy { get; set; }
    public string Detail { get; set; } = "off";
    public DateTimeOffset? LastData { get; set; }
}

/// <summary>
/// Real plan percentages scraped from <c>claude /usage</c>. These are the only
/// figures that reflect Anthropic's actual quota rather than a local budget.
/// </summary>
public sealed class PlanUsage
{
    public double? SessionPercent { get; set; }
    public double? WeeklyPercent { get; set; }
    /// <summary>Fable bills against its own weekly allowance, separate from the all-models pool.</summary>
    public double? FableWeeklyPercent { get; set; }

    // Anthropic prints the real reset moments; keep the text verbatim for display and the
    // parsed instant for a countdown, since the text is the more durable of the two.
    public string? SessionResetText { get; set; }
    public string? WeeklyResetText { get; set; }
    public DateTimeOffset? SessionResetAt { get; set; }
    public DateTimeOffset? WeeklyResetAt { get; set; }

    public DateTimeOffset? CapturedAt { get; set; }
    public string? Raw { get; set; }

    /// <summary>True once a real reading has ever landed, however old.</summary>
    public bool HasReading => CapturedAt is not null &&
                              (SessionPercent ?? WeeklyPercent ?? FableWeeklyPercent) is not null;

    public bool IsFresh => CapturedAt is { } t && (DateTimeOffset.Now - t) < TimeSpan.FromMinutes(20);

    public TimeSpan? Age => CapturedAt is { } t ? DateTimeOffset.Now - t : null;
}
