using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionLimit;

public sealed class AppConfig
{
    // ---- data sources -------------------------------------------------
    public bool EnableTranscripts { get; set; } = true;
    public bool EnableOtel        { get; set; } = false;
    public bool EnableUsageCmd    { get; set; } = true;
    public bool EnableAdminApi    { get; set; } = false;

    public int OtelPort { get; set; } = 4318;

    /// <summary>Path to claude.exe. Empty = auto-discover.</summary>
    public string ClaudeExePath { get; set; } = "";
    public int UsageCmdIntervalMinutes { get; set; } = 10;

    /// <summary>Admin key (sk-ant-admin...). Stored locally, never transmitted anywhere but Anthropic.</summary>
    public string AdminApiKey { get; set; } = "";
    public int AdminApiIntervalMinutes { get; set; } = 30;

    // ---- budgets (used when real plan % is unavailable) ---------------
    // Calibrated against real measurements: cache reads dominate totals, so a heavy
    // 5 h session runs into the hundreds of millions of tokens and a week into billions.
    public long SessionTokenBudget { get; set; } = 220_000_000;
    public long WeeklyTokenBudget  { get; set; } = 3_600_000_000;
    public decimal SessionCostBudget { get; set; } = 0m;   // 0 = not tracked
    public decimal WeeklyCostBudget  { get; set; } = 0m;

    // ---- notifications ------------------------------------------------
    public bool NotificationsEnabled { get; set; } = true;
    public bool NotificationSound { get; set; } = true;
    public List<int> SessionThresholds { get; set; } = new() { 50, 75, 90 };
    public List<int> WeeklyThresholds  { get; set; } = new() { 50, 75, 90 };

    // ---- what to display ----------------------------------------------
    public bool ShowWeekly         { get; set; } = true;
    public bool ShowTokenBreakdown { get; set; } = true;
    public bool ShowRates          { get; set; } = true;
    public bool ShowModels         { get; set; } = true;
    public bool ShowProjects       { get; set; } = true;
    /// <summary>Pad the model list with known-but-unused models (Fable, Sonnet, …) at zero.</summary>
    public bool ShowUnusedModels   { get; set; } = true;
    public bool ShowSources        { get; set; } = true;
    public bool ShowCost           { get; set; } = true;
    /// <summary>Fable's weekly allowance is separate from the all-models pool.</summary>
    public bool ShowFable          { get; set; } = true;
    public bool ShowAccount        { get; set; } = true;
    /// <summary>Off by default: this window sits on top of everything, including screen shares.</summary>
    public bool ShowAccountEmail   { get; set; } = false;
    public int  MaxModelRows       { get; set; } = 8;
    public int  MaxProjectRows     { get; set; } = 5;

    // ---- updates -------------------------------------------------------
    /// <summary>Poll GitHub Releases in the background. Checking is anonymous — nothing is sent.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    public int UpdateCheckIntervalHours { get; set; } = 6;
    public DateTimeOffset? LastUpdateCheck { get; set; }

    // ---- appearance ---------------------------------------------------
    public double Opacity { get; set; } = 0.94;
    // Nullable, not NaN: System.Text.Json refuses to write NaN/Infinity.
    public double? Left { get; set; }
    public double? Top  { get; set; }
    public bool Compact { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;

    // -------------------------------------------------------------------
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.ConfigFile), Opts);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex) { Log.Error("config load failed, using defaults", ex); }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(this, Opts));
        }
        catch (Exception ex) { Log.Error("config save failed", ex); }
    }

    public string ResolveClaudeExe() => ResolveClaudeExe(out _);

    /// <summary>
    /// Finds the Claude Code binary. There is no single canonical install location — it
    /// ships via npm, a native installer, and bundled inside several editor extensions —
    /// so this checks all of them and reports where it looked, because "not found" is
    /// useless to someone who has it installed somewhere unexpected.
    /// </summary>
    public string ResolveClaudeExe(out List<string> searched)
    {
        searched = new List<string>();

        if (!string.IsNullOrWhiteSpace(ClaudeExePath))
        {
            searched.Add($"configured: {ClaudeExePath}");
            if (File.Exists(ClaudeExePath)) return ClaudeExePath;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // PATH first: a normal `npm i -g` or installer puts it there, and it is what the
        // user gets when they type `claude` themselves.
        searched.Add("PATH");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd", "claude.bat" })
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry */ }
            }
        }

        var direct = new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(home, ".local", "bin", "claude.cmd"),
            Path.Combine(home, ".claude", "local", "claude.exe"),
            Path.Combine(appData, "npm", "claude.cmd"),
            Path.Combine(localApp, "Programs", "claude", "claude.exe"),
        };
        foreach (var p in direct)
        {
            searched.Add(p);
            if (File.Exists(p)) return p;
        }

        // Editor extensions bundle a native binary; take the highest version present.
        foreach (var editor in new[] { ".vscode", ".vscode-insiders", ".vscode-server",
                                       ".cursor", ".windsurf", ".vscodium" })
        {
            var extRoot = Path.Combine(home, editor, "extensions");
            searched.Add(extRoot);
            if (!Directory.Exists(extRoot)) continue;
            try
            {
                var best = Directory.GetDirectories(extRoot, "anthropic.claude-code-*")
                    .Select(d => new { Dir = d, Exe = Path.Combine(d, "resources", "native-binary", "claude.exe") })
                    .Where(x => File.Exists(x.Exe))
                    .OrderByDescending(x => x.Dir, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (best != null) return best.Exe;
            }
            catch (Exception ex) { Log.Error("claude.exe discovery failed", ex); }
        }
        return "";
    }
}
