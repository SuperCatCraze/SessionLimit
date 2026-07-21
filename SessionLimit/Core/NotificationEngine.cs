namespace SessionLimit;

/// <summary>
/// Fires threshold alerts at most once per threshold per window. Session state is keyed
/// to the 5-hour window anchor, so a new window naturally re-arms every threshold.
/// </summary>
public sealed class NotificationEngine
{
    private readonly AppConfig _cfg;
    private readonly HashSet<string> _fired = new(StringComparer.Ordinal);
    private DateTimeOffset? _sessionAnchor;
    private DateOnly _weekKey = DateOnly.FromDateTime(DateTime.Today);

    public NotificationEngine(AppConfig cfg) => _cfg = cfg;

    public void Evaluate(double? sessionPercent, double? weeklyPercent,
                         WindowStats session, bool percentIsReal)
    {
        if (!_cfg.NotificationsEnabled) return;

        // Re-arm on window rollover.
        if (session.WindowStart != _sessionAnchor)
        {
            _sessionAnchor = session.WindowStart;
            _fired.RemoveWhere(k => k.StartsWith("s:", StringComparison.Ordinal));
        }
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today != _weekKey)
        {
            _weekKey = today;
            _fired.RemoveWhere(k => k.StartsWith("w:", StringComparison.Ordinal));
        }

        var basis = percentIsReal ? "plan" : "budget";

        if (sessionPercent is { } sp &&
            HighestNewlyCrossed(sp, _cfg.SessionThresholds, t => $"s:{_sessionAnchor:O}:{t}") is { } st)
        {
            var left = session.Remaining is { } r ? $" Window resets in {Fmt(r)}." : "";
            ToastWindow.Show(
                $"Session at {sp:0}%",
                $"You've used {sp:0}% of your 5-hour {basis} allowance.{left}",
                Sev(st), _cfg.NotificationSound);
        }

        if (weeklyPercent is { } wp &&
            HighestNewlyCrossed(wp, _cfg.WeeklyThresholds, t => $"w:{t}") is { } wt)
        {
            ToastWindow.Show(
                $"Weekly at {wp:0}%",
                $"You've used {wp:0}% of your weekly {basis} allowance.",
                Sev(wt), _cfg.NotificationSound);
        }
    }

    /// <summary>
    /// Marks every newly crossed threshold as fired but returns only the highest, so
    /// starting up already past several thresholds announces once instead of once each.
    /// </summary>
    private int? HighestNewlyCrossed(double percent, List<int> thresholds, Func<int, string> keyFor)
    {
        int? highest = null;
        foreach (var t in thresholds.OrderBy(x => x))
        {
            if (percent < t) continue;
            if (!_fired.Add(keyFor(t))) continue;
            highest = t;                       // ascending order, so the last wins
        }
        return highest;
    }

    /// <summary>Called when the user edits thresholds so new ones can fire immediately.</summary>
    public void ResetFired() => _fired.Clear();

    private static ToastWindow.Severity Sev(int threshold) =>
        threshold >= 90 ? ToastWindow.Severity.Danger :
        threshold >= 75 ? ToastWindow.Severity.Warn :
                          ToastWindow.Severity.Info;

    public static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
}
