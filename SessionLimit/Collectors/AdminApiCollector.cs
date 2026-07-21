using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SessionLimit;

/// <summary>
/// Reads organization-level token/cost totals from the Anthropic Admin API.
///
/// This is a different universe from a Pro/Max subscription: it reports pay-as-you-go
/// API billing for a Console organization and knows nothing about session or weekly
/// seat quota. Shown as a separate figure in the overlay for exactly that reason.
/// Requires an admin key (sk-ant-admin...).
/// </summary>
public sealed class AdminApiCollector : IDisposable
{
    private const string Base = "https://api.anthropic.com";

    private readonly AppConfig _cfg;
    private readonly SourceStatus _status;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly System.Timers.Timer _timer;
    private int _running;

    public decimal OrgCost7d { get; private set; }
    public long OrgTokens7d { get; private set; }
    public event Action? Updated;

    public AdminApiCollector(AppConfig cfg, SourceStatus status)
    {
        _cfg = cfg;
        _status = status;
        _timer = new System.Timers.Timer(Math.Max(5, cfg.AdminApiIntervalMinutes) * 60_000);
    }

    public void Start()
    {
        if (string.IsNullOrWhiteSpace(_cfg.AdminApiKey))
        {
            _status.Healthy = false;
            _status.Detail = "no admin key";
            return;
        }
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
            var key = _cfg.AdminApiKey.Trim();
            if (string.IsNullOrEmpty(key)) return;

            var from = DateTimeOffset.UtcNow.AddDays(-7);
            var to = DateTimeOffset.UtcNow;

            var cost = await FetchAsync("/v1/organizations/cost_report", key, from, to);
            if (cost != null) OrgCost7d = SumCost(cost.Value);

            var usage = await FetchAsync("/v1/organizations/usage_report/messages", key, from, to);
            if (usage != null) OrgTokens7d = SumTokens(usage.Value);

            if (cost is null && usage is null)
            {
                _status.Healthy = false;
                if (_status.Detail is not ("unauthorized" or "forbidden")) _status.Detail = "no data";
            }
            else
            {
                _status.Healthy = true;
                _status.LastData = DateTimeOffset.Now;
                _status.Detail = $"${OrgCost7d:0.00} / 7d";
            }
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            _status.Healthy = false;
            _status.Detail = "error";
            Log.Error("admin-api: refresh failed", ex);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    /// <summary>
    /// Report endpoints are documented inconsistently as GET-with-query and POST-with-body,
    /// so try GET and fall back to POST rather than betting on one shape.
    /// </summary>
    private async Task<JsonElement?> FetchAsync(string path, string key, DateTimeOffset from, DateTimeOffset to)
    {
        var qs = $"?starting_at={Uri.EscapeDataString(from.ToString("o"))}" +
                 $"&ending_at={Uri.EscapeDataString(to.ToString("o"))}" +
                 $"&bucket_width=1d&limit=31";

        var res = await SendAsync(HttpMethod.Get, path + qs, key, null);
        if (res is null)
        {
            var body = JsonSerializer.Serialize(new
            {
                starting_at = from.ToString("o"),
                ending_at = to.ToString("o"),
                bucket_width = "1d",
                limit = 31
            });
            res = await SendAsync(HttpMethod.Post, path, key, body);
        }
        return res;
    }

    private async Task<JsonElement?> SendAsync(HttpMethod method, string url, string key, string? body)
    {
        try
        {
            using var req = new HttpRequestMessage(method, Base + url);
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _status.Detail = (int)resp.StatusCode switch
                {
                    401 => "unauthorized",
                    403 => "forbidden (needs admin key)",
                    404 or 405 => "endpoint shape mismatch",
                    429 => "rate limited",
                    _ => $"http {(int)resp.StatusCode}"
                };
                Log.Warn($"admin-api: {method} {url} -> {(int)resp.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            Log.Error($"admin-api: {method} {url} failed", ex);
            return null;
        }
    }

    // The report payloads nest results under data[].results[]; walk defensively and
    // total whatever numeric cost/token fields are present.
    private static decimal SumCost(JsonElement root)
    {
        decimal total = 0;
        foreach (var result in Results(root))
            foreach (var name in new[] { "amount", "cost", "cost_usd", "total_cost" })
                if (result.TryGetProperty(name, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) { total += d; break; }
                    if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var ds)) { total += ds; break; }
                }
        return total;
    }

    private static long SumTokens(JsonElement root)
    {
        long total = 0;
        string[] fields =
        {
            "uncached_input_tokens", "input_tokens", "output_tokens",
            "cache_read_input_tokens", "cache_creation_input_tokens"
        };
        foreach (var result in Results(root))
            foreach (var f in fields)
                if (result.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.Number &&
                    v.TryGetInt64(out var n)) total += n;
        return total;
    }

    private static IEnumerable<JsonElement> Results(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var bucket in data.EnumerateArray())
        {
            if (bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                foreach (var r in results.EnumerateArray()) yield return r;
            else
                yield return bucket;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _http.Dispose();
    }
}
