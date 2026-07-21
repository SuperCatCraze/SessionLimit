using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionLimit;

/// <summary>
/// The usage ledger and the window maths on top of it.
///
/// Storage is tiered, because a busy machine produces ~100k events across the retention
/// window and keeping them all as objects cost ~250 MB:
///   • recent events (last 12 h) are kept whole — the 5 h session window needs per-event
///     timestamps to find its anchor;
///   • everything older collapses into hourly per-model buckets, which is all the rolling
///     7-day total ever needs (a few hundred rows instead of ~100k objects);
///   • dedup keys are stored as 64-bit hashes rather than strings.
///
/// File read offsets live in the same state file, so a restart tails only new bytes
/// instead of re-parsing the entire ~1 GB transcript corpus.
///
/// Precedence rule: transcripts are the authoritative token ledger (stable message ids to
/// dedupe on). OTEL only contributes token counts when transcripts are off, otherwise the
/// same request would be counted twice.
/// </summary>
public sealed class UsageStore
{
    public static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);
    public static readonly TimeSpan WeeklyWindow  = TimeSpan.FromDays(7);
    private static readonly TimeSpan RawRetention = TimeSpan.FromHours(12);
    private static readonly TimeSpan Retention    = TimeSpan.FromDays(9);

    private readonly object _gate = new();
    private readonly List<UsageEvent> _recent = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly HashSet<long> _seen = new();
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);

    private bool _needsSort;
    private bool _dirty;
    private WindowStats? _sessionCache, _weekCache;
    private DateTimeOffset _cacheStamp = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    public event Action? Changed;
    public DateTimeOffset? LastActivity { get; private set; }

    public UsageStore() => LoadState();

    // ==================================================================
    //  ingest
    // ==================================================================
    public bool Add(UsageEvent e)
    {
        bool added;
        lock (_gate) { added = AddLocked(e); }
        if (added) Changed?.Invoke();
        return added;
    }

    public void AddRange(IEnumerable<UsageEvent> events)
    {
        var added = false;
        lock (_gate)
            foreach (var e in events)
                added |= AddLocked(e);

        if (added) Changed?.Invoke();
    }

    private bool AddLocked(UsageEvent e)
    {
        if (string.IsNullOrEmpty(e.Id)) e.Id = $"{e.Source}:{e.Timestamp.UtcTicks}:{e.OutputTokens}";
        if (!_seen.Add(Hash(e.Id))) return false;
        if (e.Timestamp < DateTimeOffset.Now - Retention) return false;

        // Always bucket it — buckets carry the weekly total.
        BucketFor(e.Timestamp, e.Model).Add(e);

        // Keep it whole only while the session window might still need it.
        if (e.Timestamp >= DateTimeOffset.Now - RawRetention)
        {
            if (_recent.Count > 0 && e.Timestamp < _recent[^1].Timestamp) _needsSort = true;
            _recent.Add(e);
        }

        if (LastActivity is null || e.Timestamp > LastActivity) LastActivity = e.Timestamp;
        _sessionCache = _weekCache = null;
        _dirty = true;
        return true;
    }

    private Bucket BucketFor(DateTimeOffset ts, string model)
    {
        var hour = new DateTimeOffset(ts.UtcDateTime.Date.AddHours(ts.UtcDateTime.Hour), TimeSpan.Zero);
        var key = $"{hour.UtcTicks}|{model}";
        if (!_buckets.TryGetValue(key, out var b))
            _buckets[key] = b = new Bucket { HourTicks = hour.UtcTicks, Model = model };
        return b;
    }

    // ==================================================================
    //  windows
    // ==================================================================
    public WindowStats CurrentSession()
    {
        lock (_gate) { Recompute(); return _sessionCache!; }
    }

    public WindowStats CurrentWeek()
    {
        lock (_gate) { Recompute(); return _weekCache!; }
    }

    private void Recompute()
    {
        if (_sessionCache != null && _weekCache != null &&
            DateTimeOffset.Now - _cacheStamp < CacheTtl) return;

        _cacheStamp = DateTimeOffset.Now;
        Prune();

        if (_needsSort)
        {
            _recent.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            _needsSort = false;
        }

        // ---- weekly: rolling 7 days, from the hourly buckets ----
        var weekFrom = DateTimeOffset.Now - WeeklyWindow;
        _weekCache = new WindowStats { WindowStart = weekFrom, WindowEnd = null };
        foreach (var b in _buckets.Values)
        {
            if (b.HourTicks < weekFrom.UtcTicks) continue;
            b.MergeInto(_weekCache);
        }

        // ---- session: 5 h window anchored on the first event after a >=5 h gap ----
        if (_recent.Count == 0)
        {
            _sessionCache = new WindowStats();
            return;
        }

        // Windows CHAIN: the first event opens a 5 h window, and the first event after
        // that window expires opens the next one. (Anchoring only on a >=5 h *gap between
        // consecutive events* is wrong — under continuous use there is never such a gap,
        // so the anchor would stay pinned to the oldest event and its window would read
        // as long expired.)
        var anchorIdx = 0;
        var anchor = _recent[0].Timestamp;
        for (var i = 1; i < _recent.Count; i++)
        {
            if (_recent[i].Timestamp < anchor + SessionWindow) continue;
            anchor = _recent[i].Timestamp;
            anchorIdx = i;
        }

        var end = anchor + SessionWindow;

        // Anchor expired — we're between sessions, so nothing is consumed.
        if (DateTimeOffset.Now >= end)
        {
            _sessionCache = new WindowStats();
            return;
        }

        var s = new WindowStats { WindowStart = anchor, WindowEnd = end };
        for (var i = anchorIdx; i < _recent.Count; i++)
        {
            var e = _recent[i];
            s.InputTokens      += e.InputTokens;
            s.OutputTokens     += e.OutputTokens;
            s.CacheReadTokens  += e.CacheReadTokens;
            s.CacheWriteTokens += e.CacheWrite5mTokens + e.CacheWrite1hTokens;
            s.Cost             += e.Cost;
            s.Requests++;

            var key = Pricing.Pretty(e.Model);
            if (!s.ByModel.TryGetValue(key, out var slice))
                s.ByModel[key] = slice = new ModelSlice();
            slice.Tokens += e.TotalTokens;
            slice.Cost   += e.Cost;
            slice.Requests++;

            var proj = string.IsNullOrEmpty(e.Project) ? "(unknown)" : e.Project;
            if (!s.ByProject.TryGetValue(proj, out var pslice))
                s.ByProject[proj] = pslice = new ModelSlice();
            pslice.Tokens += e.TotalTokens;
            pslice.Cost   += e.Cost;
            pslice.Requests++;
        }
        _sessionCache = s;
    }

    private void Prune()
    {
        var rawCutoff = (DateTimeOffset.Now - RawRetention).UtcTicks;
        var dropped = _recent.RemoveAll(e => e.Timestamp.UtcTicks < rawCutoff);

        var oldCutoff = (DateTimeOffset.Now - Retention).UtcTicks;
        var stale = _buckets.Where(kv => kv.Value.HourTicks < oldCutoff).Select(kv => kv.Key).ToList();
        foreach (var k in stale) _buckets.Remove(k);

        if (dropped > 0 || stale.Count > 0) _dirty = true;
    }

    // ==================================================================
    //  transcript read offsets (persisted so restarts don't re-parse)
    // ==================================================================
    public long GetOffset(string path)
    {
        lock (_gate) return _offsets.TryGetValue(path, out var v) ? v : 0;
    }

    public void SetOffset(string path, long offset)
    {
        lock (_gate)
        {
            _offsets[path] = offset;
            _dirty = true;
        }
    }

    // ==================================================================
    //  persistence
    // ==================================================================
    private sealed class State
    {
        [JsonPropertyName("buckets")] public List<Bucket> Buckets { get; set; } = new();
        [JsonPropertyName("recent")]  public List<UsageEvent> Recent { get; set; } = new();
        [JsonPropertyName("seen")]    public List<long> Seen { get; set; } = new();
        [JsonPropertyName("offsets")] public Dictionary<string, long> Offsets { get; set; } = new();
    }

    private static string StateFile => Path.Combine(Paths.DataDir, "state.json");

    private void LoadState()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);

            // Legacy append-only ledger from earlier builds — remove, it's superseded.
            if (File.Exists(Paths.LedgerFile))
            {
                try { File.Delete(Paths.LedgerFile); } catch { /* in use; harmless */ }
            }

            if (!File.Exists(StateFile)) { Log.Info("state: none, starting fresh"); return; }

            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile));
            if (state is null) return;

            var oldCutoff = (DateTimeOffset.Now - Retention).UtcTicks;
            var rawCutoff = (DateTimeOffset.Now - RawRetention).UtcTicks;

            foreach (var b in state.Buckets)
            {
                if (b.HourTicks < oldCutoff) continue;
                b.Model = string.Intern(b.Model ?? "unknown");
                _buckets[$"{b.HourTicks}|{b.Model}"] = b;
            }
            foreach (var e in state.Recent)
            {
                if (e.Timestamp.UtcTicks < rawCutoff) continue;
                e.Model = string.Intern(e.Model ?? "unknown");
                _recent.Add(e);
            }
            foreach (var h in state.Seen) _seen.Add(h);
            foreach (var (k, v) in state.Offsets) _offsets[k] = v;

            _recent.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            LastActivity = _recent.Count > 0 ? _recent[^1].Timestamp : null;

            Log.Info($"state loaded: {_buckets.Count} buckets, {_recent.Count} recent, " +
                     $"{_seen.Count} ids, {_offsets.Count} file offsets");
        }
        catch (Exception ex) { Log.Error("state load failed, starting fresh", ex); }
    }

    /// <summary>Writes state at most every 30 s, and unconditionally on <paramref name="force"/>.</summary>
    public void SaveState(bool force = false)
    {
        try
        {
            State snapshot;
            lock (_gate)
            {
                if (!force && (!_dirty || DateTimeOffset.Now - _lastSave < SaveInterval)) return;
                _lastSave = DateTimeOffset.Now;
                _dirty = false;

                snapshot = new State
                {
                    Buckets = _buckets.Values.ToList(),
                    Recent = _recent.ToList(),
                    Seen = _seen.ToList(),
                    Offsets = new Dictionary<string, long>(_offsets)
                };
            }

            // Write-then-replace so a crash mid-write can't leave a truncated state file.
            var tmp = StateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            File.Move(tmp, StateFile, overwrite: true);
        }
        catch (Exception ex) { Log.Error("state save failed", ex); }
    }

    private static long Hash(string s)
    {
        // FNV-1a 64-bit: cheap, stable across runs (unlike string.GetHashCode).
        unchecked
        {
            const long prime = 1099511628211;
            var hash = unchecked((long)14695981039346656037);
            foreach (var c in s) { hash ^= c; hash *= prime; }
            return hash;
        }
    }
}

/// <summary>One hour of usage for one model — the compact form of old events.</summary>
public sealed class Bucket
{
    [JsonPropertyName("h")]  public long HourTicks { get; set; }
    [JsonPropertyName("m")]  public string Model { get; set; } = "";
    [JsonPropertyName("i")]  public long InputTokens { get; set; }
    [JsonPropertyName("o")]  public long OutputTokens { get; set; }
    [JsonPropertyName("cr")] public long CacheReadTokens { get; set; }
    [JsonPropertyName("cw")] public long CacheWriteTokens { get; set; }
    [JsonPropertyName("c")]  public decimal Cost { get; set; }
    [JsonPropertyName("n")]  public int Requests { get; set; }

    public void Add(UsageEvent e)
    {
        InputTokens      += e.InputTokens;
        OutputTokens     += e.OutputTokens;
        CacheReadTokens  += e.CacheReadTokens;
        CacheWriteTokens += e.CacheWrite5mTokens + e.CacheWrite1hTokens;
        Cost             += e.Cost;
        Requests++;
    }

    public void MergeInto(WindowStats s)
    {
        s.InputTokens      += InputTokens;
        s.OutputTokens     += OutputTokens;
        s.CacheReadTokens  += CacheReadTokens;
        s.CacheWriteTokens += CacheWriteTokens;
        s.Cost             += Cost;
        s.Requests         += Requests;

        var key = Pricing.Pretty(Model);
        if (!s.ByModel.TryGetValue(key, out var slice))
            s.ByModel[key] = slice = new ModelSlice();
        slice.Tokens += InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
        slice.Cost   += Cost;
        slice.Requests += Requests;
    }
}
