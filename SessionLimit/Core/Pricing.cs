namespace SessionLimit;

/// <summary>
/// Per-model USD pricing, dollars per million tokens. Cache multipliers follow the
/// documented Anthropic model: reads ~0.1x input, 5m writes 1.25x, 1h writes 2x.
/// </summary>
public static class Pricing
{
    private sealed record Rate(decimal In, decimal Out);

    // Longest-prefix match, so dated/suffixed variants resolve to their family.
    private static readonly (string Prefix, Rate Rate)[] Table =
    {
        ("claude-fable-5",   new Rate(10m, 50m)),
        ("claude-mythos",    new Rate(10m, 50m)),
        ("claude-opus-4-8",  new Rate(5m,  25m)),
        ("claude-opus-4-7",  new Rate(5m,  25m)),
        ("claude-opus-4-6",  new Rate(5m,  25m)),
        ("claude-opus-4-5",  new Rate(5m,  25m)),
        ("claude-opus",      new Rate(5m,  25m)),
        ("claude-sonnet-5",  new Rate(3m,  15m)),
        ("claude-sonnet",    new Rate(3m,  15m)),
        ("claude-haiku",     new Rate(1m,   5m)),
    };

    private static readonly Rate Fallback = new(5m, 25m);

    // Fast mode is a premium tier; treated as a flat multiplier since exact
    // published rates vary. Configurable if it ever needs tuning.
    private const decimal FastMultiplier = 1.5m;

    private static Rate RateFor(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return Fallback;
        var m = model.ToLowerInvariant();
        // Strip provider prefixes (Bedrock's "anthropic.", Vertex's "@" suffix).
        if (m.StartsWith("anthropic.")) m = m[10..];
        var at = m.IndexOf('@');
        if (at > 0) m = m[..at];

        Rate? best = null;
        var bestLen = -1;
        foreach (var (prefix, rate) in Table)
            if (m.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > bestLen)
            {
                best = rate;
                bestLen = prefix.Length;
            }
        return best ?? Fallback;
    }

    public static decimal CostOf(UsageEvent e)
    {
        var r = RateFor(e.Model);
        const decimal M = 1_000_000m;

        var cost =
            e.InputTokens       / M * r.In +
            e.OutputTokens      / M * r.Out +
            e.CacheReadTokens   / M * r.In * 0.10m +
            e.CacheWrite5mTokens/ M * r.In * 1.25m +
            e.CacheWrite1hTokens/ M * r.In * 2.00m;

        return e.Fast ? cost * FastMultiplier : cost;
    }

    /// <summary>
    /// Models worth showing a row for even at zero usage, so it's visible that they're
    /// tracked. Ordered most- to least-capable.
    /// </summary>
    public static readonly string[] KnownModels =
        { "Fable 5", "Opus 4.8", "Sonnet 5", "Haiku 4.5" };

    /// <summary>Short display name, e.g. "claude-opus-4-8" -> "Opus 4.8".</summary>
    public static string Pretty(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "unknown";
        var m = model.ToLowerInvariant();
        if (m.Contains("fable"))  return "Fable 5";
        if (m.Contains("mythos")) return "Mythos 5";
        if (m.Contains("haiku"))  return "Haiku 4.5";

        string family = m.Contains("opus") ? "Opus" : m.Contains("sonnet") ? "Sonnet" : "Claude";
        // Pull the version digits out of e.g. "claude-opus-4-8" -> "4.8"
        var parts = m.Split('-', StringSplitOptions.RemoveEmptyEntries)
                     .Where(p => p.Length <= 2 && p.All(char.IsDigit)).ToArray();
        return parts.Length >= 2 ? $"{family} {parts[0]}.{parts[1]}"
             : parts.Length == 1 ? $"{family} {parts[0]}"
             : family;
    }
}
