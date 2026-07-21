using System.IO;
using System.Text.Json;

namespace SessionLimit;

/// <summary>Who Claude Code is signed in as, as recorded on this machine.</summary>
public sealed record ClaudeAccount(
    string Id, string DisplayName, string Email, string Organization, string Plan, bool ExtraUsage)
{
    /// <summary>Falls back through the identifying fields, never showing a bare UUID.</summary>
    public string Label =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName :
        !string.IsNullOrWhiteSpace(Email) ? Email : "signed in";
}

/// <summary>
/// Reads the signed-in account out of <c>~/.claude.json</c>.
///
/// This is local state written by Claude Code, not an API — it says who this machine is
/// authenticated as, which is not the same question as who else is using the account.
/// Anthropic exposes no session or device list, so a switch here is the only sign-in
/// change that can honestly be reported.
/// </summary>
public sealed class AccountWatcher
{
    private static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    private DateTime _lastWrite = DateTime.MinValue;
    private DateTimeOffset _nextPoll = DateTimeOffset.MinValue;

    public ClaudeAccount? Current { get; private set; }

    /// <summary>Raised when the signed-in account changes (old, new). Not raised on first read.</summary>
    public event Action<ClaudeAccount?, ClaudeAccount>? Switched;

    public void Poll()
    {
        // The file is rewritten constantly while Claude Code runs, so gate on both a clock
        // and the write stamp: re-parsing it every tick would be pure waste.
        if (DateTimeOffset.Now < _nextPoll) return;
        _nextPoll = DateTimeOffset.Now.AddSeconds(30);

        try
        {
            if (!System.IO.File.Exists(File)) return;

            var stamp = System.IO.File.GetLastWriteTimeUtc(File);
            if (stamp == _lastWrite) return;
            _lastWrite = stamp;

            var account = Read();
            if (account is null) return;

            var previous = Current;
            Current = account;

            if (previous is null)
                Log.Info($"account: signed in as {account.Label} ({account.Plan})");
            else if (previous.Id != account.Id)
            {
                Log.Info($"account: switched from {previous.Label} to {account.Label}");
                Switched?.Invoke(previous, account);
            }
        }
        catch (Exception ex) { Log.Error("account: read failed", ex); }
    }

    private static ClaudeAccount? Read()
    {
        using var stream = System.IO.File.Open(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("oauthAccount", out var a) ||
            a.ValueKind != JsonValueKind.Object)
            return null;

        return new ClaudeAccount(
            Id: Str(a, "accountUuid"),
            DisplayName: Str(a, "displayName"),
            Email: Str(a, "emailAddress"),
            Organization: Str(a, "organizationName"),
            Plan: PrettyPlan(Str(a, "organizationRateLimitTier"), Str(a, "organizationType")),
            ExtraUsage: a.TryGetProperty("hasExtraUsageEnabled", out var e) &&
                        e.ValueKind == JsonValueKind.True);
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>"default_claude_max_5x" -> "Max 5×". Unknown tiers are tidied, not dropped.</summary>
    internal static string PrettyPlan(string tier, string type)
    {
        var t = tier.ToLowerInvariant();
        if (t.Contains("max_20x")) return "Max 20×";
        if (t.Contains("max_5x")) return "Max 5×";

        var o = type.ToLowerInvariant();
        if (o.Contains("max")) return "Max";
        if (o.Contains("pro")) return "Pro";
        if (o.Contains("team")) return "Team";
        if (o.Contains("enterprise")) return "Enterprise";

        var raw = !string.IsNullOrWhiteSpace(type) ? type : tier;
        if (string.IsNullOrWhiteSpace(raw)) return "";
        raw = raw.Replace("claude_", "").Replace("default_", "").Replace('_', ' ').Trim();
        return raw.Length == 0 ? "" : char.ToUpperInvariant(raw[0]) + raw[1..];
    }
}
