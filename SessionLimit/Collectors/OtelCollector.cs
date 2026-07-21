using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SessionLimit;

/// <summary>
/// Minimal OTLP/HTTP JSON receiver for Claude Code telemetry.
///
/// Uses a raw TcpListener rather than HttpListener: HttpListener goes through HTTP.SYS
/// and can demand a URL ACL reservation (i.e. elevation), which a tray widget must never
/// require. This speaks just enough HTTP/1.1 to accept OTLP exports on loopback.
///
/// Claude Code emits <c>claude_code.token.usage</c> and <c>claude_code.cost.usage</c> as
/// cumulative counters, so values are diffed against the previous export per series.
/// </summary>
public sealed class OtelCollector : IDisposable
{
    private readonly UsageStore _store;
    private readonly SourceStatus _status;
    private readonly int _port;
    private readonly Func<bool> _countTokens;   // false when transcripts own the ledger
    private readonly Dictionary<string, double> _cumulative = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public double CostSinceStart { get; private set; }

    public OtelCollector(UsageStore store, SourceStatus status, int port, Func<bool> countTokens)
    {
        _store = store;
        _status = status;
        _port = port;
        _countTokens = countTokens;
    }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _status.Healthy = true;
            _status.Detail = $"listening :{_port}";
            Log.Info($"otel: listening on 127.0.0.1:{_port}");
            _ = Task.Run(AcceptLoop);
        }
        catch (Exception ex)
        {
            _status.Healthy = false;
            _status.Detail = $"port {_port} unavailable";
            Log.Error("otel: listener failed to start", ex);
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => Handle(client));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) break;
                Log.Error("otel: accept failed", ex);
                await Task.Delay(500);
            }
        }
    }

    private void Handle(TcpClient client)
    {
        try
        {
            using (client)
            using (var ns = client.GetStream())
            {
                ns.ReadTimeout = 10_000;
                ns.WriteTimeout = 10_000;

                var (path, headers, body) = ReadRequest(ns);
                if (path.Length > 0)
                {
                    try { Ingest(path, body); }
                    catch (Exception ex) { Log.Error("otel: ingest failed", ex); }
                }

                var resp = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                ns.Write(resp, 0, resp.Length);
                ns.Flush();
            }
        }
        catch (Exception ex) { Log.Error("otel: connection handling failed", ex); }
    }

    private static (string Path, Dictionary<string, string> Headers, byte[] Body) ReadRequest(NetworkStream ns)
    {
        var head = new List<byte>(2048);
        var one = new byte[1];
        // Read byte-wise until the header terminator. Headers are small; body follows.
        while (head.Count < 64 * 1024)
        {
            if (ns.Read(one, 0, 1) <= 0) break;
            head.Add(one[0]);
            var n = head.Count;
            if (n >= 4 && head[n - 4] == 13 && head[n - 3] == 10 && head[n - 2] == 13 && head[n - 1] == 10) break;
        }

        var text = Encoding.ASCII.GetString(head.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return ("", new(), Array.Empty<byte>());

        var parts = lines[0].Split(' ');
        var path = parts.Length >= 2 ? parts[1] : "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }

        byte[] body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) && len > 0)
        {
            body = new byte[len];
            var read = 0;
            while (read < len)
            {
                var got = ns.Read(body, read, len - read);
                if (got <= 0) break;
                read += got;
            }
            if (read < len) body = body[..read];
        }
        else if (headers.TryGetValue("Transfer-Encoding", out var te) &&
                 te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = ReadChunked(ns);
        }

        if (headers.TryGetValue("Content-Encoding", out var ce) &&
            ce.Contains("gzip", StringComparison.OrdinalIgnoreCase) && body.Length > 0)
        {
            using var src = new MemoryStream(body);
            using var gz = new GZipStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            gz.CopyTo(dst);
            body = dst.ToArray();
        }

        return (path, headers, body);
    }

    private static byte[] ReadChunked(NetworkStream ns)
    {
        using var outp = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var sizeLine = new StringBuilder();
            while (true)
            {
                if (ns.Read(one, 0, 1) <= 0) return outp.ToArray();
                if (one[0] == (byte)'\n') break;
                if (one[0] != (byte)'\r') sizeLine.Append((char)one[0]);
            }
            var hex = sizeLine.ToString().Split(';')[0].Trim();
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var size) || size == 0)
                return outp.ToArray();

            var buf = new byte[size];
            var read = 0;
            while (read < size)
            {
                var got = ns.Read(buf, read, size - read);
                if (got <= 0) break;
                read += got;
            }
            outp.Write(buf, 0, read);
            ns.Read(one, 0, 1);   // CR
            ns.Read(one, 0, 1);   // LF
        }
    }

    // ------------------------------------------------------------------
    private void Ingest(string path, byte[] body)
    {
        if (body.Length == 0) return;
        _status.LastData = DateTimeOffset.Now;
        _status.Healthy = true;

        if (!path.Contains("/v1/metrics", StringComparison.OrdinalIgnoreCase))
        {
            // Logs/traces still prove the pipe is alive, but carry no counters we need.
            _status.Detail = "receiving";
            return;
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("resourceMetrics", out var resMetrics)) return;

        var batch = new List<UsageEvent>();
        foreach (var rm in resMetrics.EnumerateArray())
        {
            if (!rm.TryGetProperty("scopeMetrics", out var scopes)) continue;
            foreach (var sm in scopes.EnumerateArray())
            {
                if (!sm.TryGetProperty("metrics", out var metrics)) continue;
                foreach (var metric in metrics.EnumerateArray())
                {
                    var name = metric.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name is not ("claude_code.token.usage" or "claude_code.cost.usage")) continue;

                    foreach (var dp in DataPoints(metric))
                        HandleDataPoint(name, dp, batch);
                }
            }
        }

        if (batch.Count > 0) _store.AddRange(batch);
        _status.Detail = _countTokens() ? "counting" : "live signal";
    }

    private static IEnumerable<JsonElement> DataPoints(JsonElement metric)
    {
        foreach (var kind in new[] { "sum", "gauge", "histogram" })
            if (metric.TryGetProperty(kind, out var body) &&
                body.TryGetProperty("dataPoints", out var dps) &&
                dps.ValueKind == JsonValueKind.Array)
                foreach (var dp in dps.EnumerateArray())
                    yield return dp;
    }

    private void HandleDataPoint(string metricName, JsonElement dp, List<UsageEvent> batch)
    {
        var attrs = ReadAttributes(dp);
        var value = ReadValue(dp);
        if (double.IsNaN(value)) return;

        attrs.TryGetValue("model", out var model);
        attrs.TryGetValue("type", out var tokenType);
        model ??= "unknown";

        // Counters are cumulative — convert to a delta for this export.
        var key = $"{metricName}|{model}|{tokenType}|{attrs.GetValueOrDefault("session.id")}";
        var previous = _cumulative.GetValueOrDefault(key, 0);
        _cumulative[key] = value;
        var delta = value >= previous ? value - previous : value;   // counter reset
        if (delta <= 0) return;

        if (metricName == "claude_code.cost.usage")
        {
            CostSinceStart += delta;
            return;
        }

        if (!_countTokens()) return;   // transcripts own the token ledger

        var amount = (long)Math.Round(delta);
        var e = new UsageEvent
        {
            Id        = $"otel:{key}:{DateTimeOffset.Now.UtcTicks}",
            Timestamp = DateTimeOffset.Now,
            Model     = model,
            Source    = "otel",
            SessionId = attrs.GetValueOrDefault("session.id")
        };

        switch ((tokenType ?? "").ToLowerInvariant())
        {
            case "input":          e.InputTokens = amount; break;
            case "output":         e.OutputTokens = amount; break;
            case "cacheread":      e.CacheReadTokens = amount; break;
            case "cachecreation":  e.CacheWrite5mTokens = amount; break;
            default:               e.InputTokens = amount; break;
        }
        batch.Add(e);
    }

    private static Dictionary<string, string> ReadAttributes(JsonElement dp)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!dp.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var a in attrs.EnumerateArray())
        {
            var k = a.TryGetProperty("key", out var kv) ? kv.GetString() : null;
            if (k is null || !a.TryGetProperty("value", out var v)) continue;

            string? s = null;
            if (v.TryGetProperty("stringValue", out var sv)) s = sv.GetString();
            else if (v.TryGetProperty("intValue", out var iv)) s = iv.ToString();
            else if (v.TryGetProperty("doubleValue", out var dv)) s = dv.ToString();
            else if (v.TryGetProperty("boolValue", out var bv)) s = bv.ToString();
            if (s != null) result[k] = s;
        }
        return result;
    }

    private static double ReadValue(JsonElement dp)
    {
        if (dp.TryGetProperty("asInt", out var ai))
        {
            if (ai.ValueKind == JsonValueKind.String && long.TryParse(ai.GetString(), out var l)) return l;
            if (ai.ValueKind == JsonValueKind.Number && ai.TryGetInt64(out var n)) return n;
        }
        if (dp.TryGetProperty("asDouble", out var ad) && ad.ValueKind == JsonValueKind.Number)
            return ad.GetDouble();
        if (dp.TryGetProperty("sum", out var s) && s.ValueKind == JsonValueKind.Number)
            return s.GetDouble();
        return double.NaN;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); _listener?.Stop(); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
