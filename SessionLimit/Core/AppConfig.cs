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
    public int  MaxModelRows       { get; set; } = 8;
    public int  MaxProjectRows     { get; set; } = 5;

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

    /// <summary>Finds the bundled Claude Code binary, preferring the newest extension version.</summary>
    public string ResolveClaudeExe()
    {
        if (!string.IsNullOrWhiteSpace(ClaudeExePath) && File.Exists(ClaudeExePath))
            return ClaudeExePath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var direct = new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(home, ".claude", "local", "claude.exe"),
        };
        foreach (var p in direct)
            if (File.Exists(p)) return p;

        // VS Code extension ships a native binary; pick the highest version folder.
        foreach (var extRoot in new[] { Path.Combine(home, ".vscode", "extensions"),
                                        Path.Combine(home, ".vscode-insiders", "extensions") })
        {
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
