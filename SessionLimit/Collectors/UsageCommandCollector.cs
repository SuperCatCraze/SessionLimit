using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SessionLimit;

/// <summary>
/// Scrapes real plan percentages by shelling out to <c>claude /usage</c>.
///
/// EXPERIMENTAL BY NATURE. There is no public API for Pro/Max quota, so this parses
/// human-facing CLI output — the only route to Anthropic's actual session/weekly
/// percentages. It can break on any Claude Code release, so every failure is soft:
/// the collector reports unavailable and the overlay falls back to budget-relative %.
/// </summary>
public sealed class UsageCommandCollector : IDisposable
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Percent = new(@"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    private static readonly Regex SessionLine = new(
        @"current\s+session\s*:\s*(\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WeekLine = new(
        @"current\s+week\s*(?:\(\s*([^)]*?)\s*\))?\s*:\s*(\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ResetPart = new(
        @"resets\s+(.+?)\s*(?:\(([^)]+)\)\s*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppConfig _cfg;
    private readonly SourceStatus _status;
    private readonly System.Timers.Timer _timer;
    private int _running;

    public PlanUsage Plan { get; } = new();
    public event Action? Updated;

    public UsageCommandCollector(AppConfig cfg, SourceStatus status)
    {
        _cfg = cfg;
        _status = status;
        _timer = new System.Timers.Timer(Math.Max(2, cfg.UsageCmdIntervalMinutes) * 60_000);
    }

    public void Start()
    {
        var exe = _cfg.ResolveClaudeExe();
        if (string.IsNullOrEmpty(exe))
        {
            _status.Healthy = false;
            _status.Detail = "claude.exe not found";
            Log.Warn("usage-cmd: claude.exe not found");
            return;
        }

        Log.Info($"usage-cmd: using {exe}");
        _timer.Elapsed += (_, _) => _ = RefreshAsync();
        _timer.AutoReset = true;
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var exe = _cfg.ResolveClaudeExe();
            if (string.IsNullOrEmpty(exe)) return;

            var output = await RunAsync(exe, UsageArgs);
            if (string.IsNullOrWhiteSpace(output))
            {
                _status.Healthy = false;
                _status.Detail = "no output";
                return;
            }

            var clean = Ansi.Replace(output, "");

            // Being signed out looks identical to a parse failure unless you check for it,
            // and it is the one failure the user can actually do something about.
            if (LooksSignedOut(clean))
            {
                _status.Healthy = false;
                _status.Detail = "not signed in";
                Plan.Raw = Trim(clean);
                Log.Warn("usage-cmd: Claude Code reports no active login");
                Updated?.Invoke();
                return;
            }
            var r = Parse(clean);

            if (r.Session is null && r.Weekly is null && r.Fable is null)
            {
                _status.Healthy = false;
                _status.Detail = "unparseable";
                Plan.Raw = Trim(clean);
                Log.Warn("usage-cmd: no percentages found in /usage output");
            }
            else
            {
                Plan.SessionPercent    = r.Session;
                Plan.WeeklyPercent     = r.Weekly;
                Plan.FableWeeklyPercent = r.Fable;
                Plan.SessionResetText  = r.SessionReset;
                Plan.WeeklyResetText   = r.WeeklyReset;
                Plan.SessionResetAt    = r.SessionResetAt;
                Plan.WeeklyResetAt     = r.WeeklyResetAt;
                Plan.CapturedAt = DateTimeOffset.Now;
                Plan.Raw = Trim(clean);
                _status.Healthy = true;
                _status.LastData = DateTimeOffset.Now;
                _status.Detail = $"session {r.Session?.ToString("0") ?? "-"}% / week {r.Weekly?.ToString("0") ?? "-"}%"
                               + (r.Fable is { } f ? $" / fable {f:0}%" : "");
                Log.Info($"usage-cmd: {_status.Detail}");
            }
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            _status.Healthy = false;
            _status.Detail = "error";
            Log.Error("usage-cmd: refresh failed", ex);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    internal readonly record struct PlanReading(
        double? Session, double? Weekly, double? Fable,
        string? SessionReset, string? WeeklyReset,
        DateTimeOffset? SessionResetAt, DateTimeOffset? WeeklyResetAt);

    /// <summary>
    /// Reads the limit lines out of <c>/usage</c>. The current shape is
    ///
    ///   Current session: 82% used · resets Jul 21, 1:39am (America/Chicago)
    ///   Current week (all models): 80% used · resets Jul 22, 4:59pm (America/Chicago)
    ///   Current week (Fable): 26% used · resets Jul 22, 4:59pm (America/Chicago)
    ///
    /// Matching is anchored on "current session" / "current week" rather than on any
    /// percentage, because the paragraphs underneath are full of percentages
    /// ("88% of your usage came from sessions active for 8+ hours") that would otherwise
    /// be mistaken for limits. If none of those lines are found, fall back to the older
    /// loose scan so a wording change degrades instead of going blank.
    /// </summary>
    internal static PlanReading Parse(string text)
    {
        double? session = null, weekly = null, fable = null;
        string? sessionReset = null, weeklyReset = null;
        DateTimeOffset? sessionResetAt = null, weeklyResetAt = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (SessionLine.Match(line) is { Success: true } sm &&
                double.TryParse(sm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var sp))
            {
                session = sp;
                (sessionReset, sessionResetAt) = ParseReset(line);
                continue;
            }

            if (WeekLine.Match(line) is { Success: true } wm &&
                double.TryParse(wm.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var wp))
            {
                var scope = wm.Groups[1].Value;
                if (scope.Contains("fable", StringComparison.OrdinalIgnoreCase))
                {
                    fable = wp;
                }
                else
                {
                    weekly = wp;
                    (weeklyReset, weeklyResetAt) = ParseReset(line);
                }
            }
        }

        if (session is null && weekly is null && fable is null)
            (session, weekly) = ParseLoose(text);

        return new PlanReading(session, weekly, fable, sessionReset, weeklyReset,
                               sessionResetAt, weeklyResetAt);
    }

    /// <summary>Pre-2026 wording: first percentage on any line mentioning session or week.</summary>
    private static (double? Session, double? Weekly) ParseLoose(string text)
    {
        double? session = null, weekly = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var m = Percent.Match(line);
            if (!m.Success) continue;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct)) continue;

            var lower = line.ToLowerInvariant();
            if (session is null && (lower.Contains("session") || lower.Contains("5-hour") ||
                                    lower.Contains("5 hour")))
                session = pct;
            else if (weekly is null && (lower.Contains("week") || lower.Contains("7-day") ||
                                        lower.Contains("7 day")))
                weekly = pct;
        }
        return (session, weekly);
    }

    /// <summary>
    /// "· resets Jul 22, 4:59pm (America/Chicago)" -> the text as printed, plus the instant
    /// when it can be resolved. The year is absent from the output, so the nearest one is
    /// assumed; the text is kept regardless so a countdown is a bonus, never a dependency.
    /// </summary>
    private static (string?, DateTimeOffset?) ParseReset(string line)
    {
        var m = ResetPart.Match(line);
        if (!m.Success) return (null, null);

        var when = m.Groups[1].Value.Trim();
        var zone = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
        return (when, ResolveReset(when, zone));
    }

    private static readonly string[] ResetFormats =
    {
        "MMM d, h:mmtt", "MMM d, h:mm tt", "MMM d, htt", "MMM d, H:mm",
        "MMM d yyyy, h:mmtt", "MMM d, yyyy h:mmtt"
    };

    private static DateTimeOffset? ResolveReset(string when, string? zoneId)
    {
        try
        {
            if (!DateTime.TryParseExact(when.ToUpperInvariant(), ResetFormats,
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.AllowWhiteSpaces, out var naive))
                return null;

            TimeZoneInfo? zone = null;
            if (!string.IsNullOrEmpty(zoneId))
                try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); } catch { /* unknown id */ }

            var year = naive.Year > 1 && when.Contains(naive.Year.ToString()) ? naive.Year : DateTime.Now.Year;
            var local = new DateTime(year, naive.Month, naive.Day, naive.Hour, naive.Minute, 0,
                                     DateTimeKind.Unspecified);

            var result = zone is null
                ? new DateTimeOffset(local, DateTimeOffset.Now.Offset)
                : new DateTimeOffset(local, zone.GetUtcOffset(local));

            // A reset far in the past means the year rolled over between print and parse.
            if (result < DateTimeOffset.Now.AddDays(-180)) result = result.AddYears(1);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"usage-cmd: could not resolve reset '{when}'", ex);
            return null;
        }
    }

    /// <summary>
    /// <c>--no-session-persistence</c> matters more than it looks: without it every poll
    /// files a new saved conversation, and a day of ten-minute checks leaves a wall of
    /// one-line "/usage" chats in the user's history that this app then reads back as usage.
    /// </summary>
    private const string UsageArgs = "-p \"/usage\" --no-session-persistence";

    private static readonly string[] SignedOutMarkers =
    {
        "/login", "please log in", "not logged in", "no active session",
        "authentication", "sign in to", "invalid api key", "unauthorized"
    };

    private static bool LooksSignedOut(string text)
    {
        var lower = text.ToLowerInvariant();
        // Only when there is nothing usable in it — the real output can mention these words.
        if (SessionLine.IsMatch(text) || WeekLine.IsMatch(text)) return false;
        return SignedOutMarkers.Any(m => lower.Contains(m));
    }

    /// <summary>
    /// One-shot check for the settings panel: says plainly what happened, in the words
    /// someone fixing their own install needs.
    /// </summary>
    public static async Task<string> DiagnoseAsync(AppConfig cfg)
    {
        var exe = cfg.ResolveClaudeExe(out var searched);
        if (string.IsNullOrEmpty(exe))
            return "Claude Code not found. Install it, or use Find… to point at claude.exe.\n" +
                   "Looked in: " + string.Join("; ", searched.Take(4)) + " …";

        try
        {
            var raw = await RunAsync(exe, UsageArgs);
            if (string.IsNullOrWhiteSpace(raw))
                return $"Found {Path.GetFileName(exe)} but it returned nothing. " +
                       "Run `claude` in a terminal once — a first run may need you to sign in or approve the folder.";

            var clean = Ansi.Replace(raw, "");
            if (LooksSignedOut(clean))
                return "Claude Code is installed but not signed in. Press Sign in, then run /login.";

            var r = Parse(clean);
            if (r.Session is null && r.Weekly is null && r.Fable is null)
                return "Connected, but the /usage wording has changed and no percentages were found. " +
                       "Update Session Limit, or report it.";

            return $"Connected. Session {r.Session:0}% · week {r.Weekly:0}%" +
                   (r.Fable is { } f ? $" · Fable {f:0}%" : "") + $"\nUsing {exe}";
        }
        catch (Exception ex)
        {
            Log.Error("usage-cmd: diagnose failed", ex);
            return $"Could not run {exe}: {ex.Message}";
        }
    }

    private static async Task<string> RunAsync(string exe, string args)
    {
        // A .cmd/.bat shim (how npm installs it) is not a PE image, so it cannot be started
        // directly with UseShellExecute off — it has to go through the command processor.
        var isShim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                     exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo
        {
            FileName = isShim ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : exe,
            Arguments = isShim ? $"/c \"\"{exe}\" {args}\"" : args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // never let a prompt block on the parent's console
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return "";

        // Close stdin immediately: anything waiting for input gets EOF and exits rather
        // than hanging until the timeout.
        try { proc.StandardInput.Close(); } catch { /* already gone */ }

        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            Log.Warn("usage-cmd: timed out");
            return "";
        }

        var o = await stdout;
        var e = await stderr;
        return string.IsNullOrWhiteSpace(o) ? e : o;
    }

    private static string Trim(string s) => s.Length > 4000 ? s[..4000] : s;

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
