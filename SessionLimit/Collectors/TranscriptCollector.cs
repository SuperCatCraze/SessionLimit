using System.IO;
using System.Text;
using System.Text.Json;

namespace SessionLimit;

/// <summary>
/// Tails Claude Code session transcripts under ~/.claude/projects/**/*.jsonl.
///
/// The transcript format is internal to Claude Code and Anthropic warns it can change
/// on any release, so every field read here is optional and failures are per-line:
/// one unparseable record is skipped, it never kills the collector.
/// </summary>
public sealed class TranscriptCollector : IDisposable
{
    private readonly UsageStore _store;
    private readonly SourceStatus _status;
    private readonly Dictionary<string, DateTime> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Timers.Timer _timer = new(2000);
    private FileSystemWatcher? _watcher;
    private int _polling;

    public TranscriptCollector(UsageStore store, SourceStatus status)
    {
        _store = store;
        _status = status;
    }

    public void Start()
    {
        if (!Directory.Exists(Paths.ClaudeProjectsDir))
        {
            _status.Healthy = false;
            _status.Detail = "no ~/.claude/projects";
            Log.Warn("transcripts: projects dir missing");
            return;
        }

        // A full backfill can mean parsing ~1 GB of transcripts. Never on the UI thread.
        Task.Run(() => Poll(backfill: true));

        try
        {
            _watcher = new FileSystemWatcher(Paths.ClaudeProjectsDir, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) => Poll(false);
            _watcher.Created += (_, _) => Poll(false);
        }
        catch (Exception ex) { Log.Error("transcripts: watcher failed, falling back to polling", ex); }

        _timer.Elapsed += (_, _) => Poll(false);
        _timer.AutoReset = true;
        _timer.Start();
    }

    private void Poll(bool backfill)
    {
        // FileSystemWatcher fires in bursts; collapse concurrent passes.
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var cutoff = DateTime.Now - TimeSpan.FromDays(9);
            var batch = new List<UsageEvent>();
            var files_seen = 0;

            foreach (var file in Directory.EnumerateFiles(
                         Paths.ClaudeProjectsDir, "*.jsonl", SearchOption.AllDirectories))
            {
                var written = File.GetLastWriteTime(file);
                if (written < cutoff) continue;
                files_seen++;

                // Skip untouched files so a 2 s tick doesn't reopen the whole corpus.
                if (_stamps.TryGetValue(file, out var seen) && seen == written) continue;
                _stamps[file] = written;

                try { ReadNew(file, batch); }
                catch (Exception ex) { Log.Error($"transcripts: read failed {Path.GetFileName(file)}", ex); }
            }

            if (batch.Count > 0) _store.AddRange(batch);

            _status.Healthy = true;
            _status.Detail = $"{files_seen} transcript(s)";
            if (batch.Count > 0) _status.LastData = DateTimeOffset.Now;
            if (backfill)
            {
                Log.Info($"transcripts: backfilled {batch.Count} events from {files_seen} files");
                // Parsing ~1 GB leaves a lot of dead gen2/LOH behind. One compacting
                // collect here hands it back rather than sitting on it all session.
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
        }
        catch (Exception ex)
        {
            _status.Healthy = false;
            _status.Detail = "error";
            Log.Error("transcripts: poll failed", ex);
        }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private const int MaxLineBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Streams new bytes from a transcript one line at a time.
    ///
    /// Deliberately NOT ReadToEnd/Split: the transcript corpus runs to ~1 GB, and
    /// materialising whole files (plus a string[] per split) pushed the process to
    /// multiple GB of working set. Memory here stays bounded by the 64 KB read buffer
    /// plus the longest single line.
    /// </summary>
    private void ReadNew(string path, List<UsageEvent> batch)
    {
        // Offsets are persisted by the store, so a restart tails only new bytes.
        var offset = _store.GetOffset(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        if (fs.Length < offset) offset = 0;          // file truncated or rotated
        if (fs.Length <= offset) return;

        fs.Seek(offset, SeekOrigin.Begin);

        var project = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream(16 * 1024);
        long consumed = 0;
        long lineBytes = 0;          // true length, even when we stop buffering
        var overlong = false;
        int read;

        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    lineBytes++;
                    // Guard against a pathological single line eating the heap.
                    if (line.Length < MaxLineBytes) line.WriteByte(buffer[i]);
                    else overlong = true;
                    continue;
                }

                if (!overlong && line.Length > 1)
                {
                    var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length)
                                       .TrimEnd('\r');
                    var e = TryParse(text, project);
                    if (e != null) batch.Add(e);
                }

                // Only advance past lines we've fully consumed; a trailing partial
                // line stays unread and is picked up once the writer flushes it.
                consumed += lineBytes + 1;   // +1 for the newline itself
                lineBytes = 0;
                line.SetLength(0);
                overlong = false;
            }
        }

        _store.SetOffset(path, offset + consumed);
    }

    private static UsageEvent? TryParse(string line, string project)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant")
                return null;
            if (!root.TryGetProperty("message", out var msg)) return null;
            if (!msg.TryGetProperty("usage", out var usage)) return null;

            // Prefer the API message id — stable and unique per billed request.
            var id = Str(msg, "id") ?? Str(root, "requestId") ?? Str(root, "uuid");
            if (id is null) return null;

            var ts = root.TryGetProperty("timestamp", out var t) &&
                     DateTimeOffset.TryParse(t.GetString(), out var parsed)
                     ? parsed.ToLocalTime()
                     : DateTimeOffset.Now;

            long cw5 = 0, cw1h = 0;
            if (usage.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
            {
                cw5  = Num(cc, "ephemeral_5m_input_tokens");
                cw1h = Num(cc, "ephemeral_1h_input_tokens");
            }
            var totalWrite = Num(usage, "cache_creation_input_tokens");
            // If the breakdown is absent or disagrees, trust the flat total (as 5m).
            if (cw5 + cw1h == 0 && totalWrite > 0) cw5 = totalWrite;

            // Model/project/session repeat across ~100k events — intern them so the
            // ledger holds one shared instance instead of 100k copies.
            return new UsageEvent
            {
                Id                 = "tx:" + id,
                Timestamp          = ts,
                Model              = string.Intern(Str(msg, "model") ?? "unknown"),
                InputTokens        = Num(usage, "input_tokens"),
                OutputTokens       = Num(usage, "output_tokens"),
                CacheReadTokens    = Num(usage, "cache_read_input_tokens"),
                CacheWrite5mTokens = cw5,
                CacheWrite1hTokens = cw1h,
                Fast               = Str(usage, "speed") == "fast",
                Source             = "transcript",
                SessionId          = Str(root, "sessionId") is { } sid ? string.Intern(sid) : null,
                Project            = project
            };
        }
        catch { return null; }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _watcher?.Dispose();
    }
}
