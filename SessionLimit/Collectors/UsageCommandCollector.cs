using System.Diagnostics;
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

            var output = await RunAsync(exe, "-p \"/usage\"");
            if (string.IsNullOrWhiteSpace(output))
            {
                _status.Healthy = false;
                _status.Detail = "no output";
                return;
            }

            var clean = Ansi.Replace(output, "");
            var (session, weekly) = Parse(clean);

            if (session is null && weekly is null)
            {
                _status.Healthy = false;
                _status.Detail = "unparseable";
                Plan.Raw = Trim(clean);
                Log.Warn("usage-cmd: no percentages found in /usage output");
            }
            else
            {
                Plan.SessionPercent = session;
                Plan.WeeklyPercent = weekly;
                Plan.CapturedAt = DateTimeOffset.Now;
                Plan.Raw = Trim(clean);
                _status.Healthy = true;
                _status.LastData = DateTimeOffset.Now;
                _status.Detail = $"session {session?.ToString("0") ?? "-"}% / week {weekly?.ToString("0") ?? "-"}%";
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

    /// <summary>
    /// Pulls the first percentage off whichever line mentions the session or the week.
    /// Deliberately loose — the surrounding wording changes more often than the numbers.
    /// </summary>
    internal static (double? Session, double? Weekly) Parse(string text)
    {
        double? session = null, weekly = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var m = Percent.Match(line);
            if (!m.Success) continue;
            if (!double.TryParse(m.Groups[1].Value, out var pct)) continue;

            var lower = line.ToLowerInvariant();
            if (session is null && (lower.Contains("session") || lower.Contains("5-hour") ||
                                    lower.Contains("5 hour") || lower.Contains("current session")))
                session = pct;
            else if (weekly is null && (lower.Contains("week") || lower.Contains("7-day") ||
                                        lower.Contains("7 day")))
                weekly = pct;
        }
        return (session, weekly);
    }

    private static async Task<string> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return "";

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
