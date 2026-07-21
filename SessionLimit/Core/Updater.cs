using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SessionLimit;

/// <summary>A release newer than the running build.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, long Size, string PageUrl)
{
    public string Display => $"v{Version.Major}.{Version.Minor}.{Version.Build}";
}

/// <summary>Outcome of a check: <paramref name="Status"/> is always safe to show the user.</summary>
public sealed record CheckResult(bool Available, UpdateInfo? Info, string Status);

/// <summary>
/// Self-update from GitHub Releases.
///
/// Windows will not let a running executable be deleted, but it *will* let it be renamed —
/// so the swap is: current -> .old, downloaded -> current, relaunch, exit. The leftover
/// .old is deleted on the next start, once nothing has it mapped.
/// </summary>
public static class Updater
{
    public const string Repo = "SuperCatCraze/SessionLimit";
    public const string RepoUrl = "https://github.com/" + Repo;
    private const string AssetName = "SessionLimit.exe";

    /// <summary>Passed to the relaunched process so it waits for this one to let go.</summary>
    public const string UpdatedFlag = "--updated";

    // ==================================================================
    //  version
    // ==================================================================
    // Declared before Http: static initialisers run in textual order, and the
    // User-Agent is built from this.
    public static Version Current { get; } = ReadCurrentVersion();

    public static string Display => $"v{Current.Major}.{Current.Minor}.{Current.Build}";

    private static Version ReadCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // The SDK appends "+<commit sha>" to the informational version; trim it off.
            var plus = info.IndexOf('+');
            if (plus > 0) info = info[..plus];
            if (Version.TryParse(info, out var v)) return Normalise(v);
        }
        return Normalise(asm?.GetName().Version ?? new Version(0, 0, 0));
    }

    /// <summary>Drops the revision field so 0.3.0.0 and 0.3.0 compare equal.</summary>
    private static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? Normalise(v) : null;
    }

    // ==================================================================
    //  http
    // ==================================================================
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub rejects API requests with no User-Agent outright.
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"SessionLimit/{Display}");
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    // ==================================================================
    //  check
    // ==================================================================
    public static async Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);

            if (res.StatusCode == HttpStatusCode.NotFound)
                return new CheckResult(false, null, "no releases published yet");

            // Anonymous API calls are capped at 60/hour per IP. Say so rather than
            // reporting a generic failure, because it resolves itself.
            if (res.StatusCode == HttpStatusCode.Forbidden &&
                res.Headers.TryGetValues("x-ratelimit-remaining", out var rem) &&
                rem.FirstOrDefault() == "0")
                return new CheckResult(false, null, "GitHub rate limit — try again later");

            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var version = ParseTag(tag);
            if (version is null)
                return new CheckResult(false, null, $"unreadable release tag '{tag}'");

            if (version <= Current)
                return new CheckResult(false, null, $"up to date · {Display}");

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? RepoUrl : RepoUrl;

            string url = "";
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                    url = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "";
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    break;
                }
            }

            if (string.IsNullOrEmpty(url))
                return new CheckResult(false, null, $"v{version} published without a {AssetName}");

            Log.Info($"update available: {tag} ({size / 1024 / 1024} MB)");
            return new CheckResult(true, new UpdateInfo(version, tag, notes, url, size, page), $"{tag} available");
        }
        catch (TaskCanceledException)
        {
            return new CheckResult(false, null, "check timed out");
        }
        catch (Exception ex)
        {
            Log.Error("update check failed", ex);
            return new CheckResult(false, null, "check failed — see log");
        }
    }

    // ==================================================================
    //  download
    // ==================================================================
    /// <summary>Downloads the release asset beside the current executable. Returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress,
                                                   CancellationToken ct = default)
    {
        var staged = StagedPath;
        // Staging next to the executable keeps the later swap a same-volume rename,
        // which is atomic; staging in %TEMP% would be a cross-volume copy.
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        if (File.Exists(staged)) File.Delete(staged);

        using (var res = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                                   .ConfigureAwait(false))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? info.Size;

            await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                                 81920, useAsync: true);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(done * 100.0 / total);
            }
        }

        Verify(staged, info);
        Log.Info($"update staged: {staged}");
        return staged;
    }

    /// <summary>
    /// Refuses anything that isn't the executable we asked for. A self-updater runs whatever
    /// it downloads, so a truncated or mismatched file must not reach the swap.
    /// </summary>
    private static void Verify(string path, UpdateInfo info)
    {
        var actual = new FileInfo(path).Length;
        if (info.Size > 0 && actual != info.Size)
            throw new InvalidDataException($"size mismatch: got {actual} bytes, expected {info.Size}");

        using (var fs = File.OpenRead(path))
        {
            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                throw new InvalidDataException("downloaded file is not a Windows executable");
        }

        var stamped = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (Version.TryParse(stamped, out var v) && Normalise(v) != info.Version)
            throw new InvalidDataException($"version mismatch: binary is {Normalise(v)}, release says {info.Version}");
    }

    // ==================================================================
    //  swap
    // ==================================================================
    private static string ExePath => Environment.ProcessPath
                                     ?? Process.GetCurrentProcess().MainModule?.FileName
                                     ?? "";

    private static string StagedPath => ExePath + ".new";
    private static string OldPath => ExePath + ".old";

    /// <summary>
    /// False when the running build isn't one we can safely replace — a framework-dependent
    /// dev build leaves its DLLs beside the exe, and overwriting just the exe would strand them.
    /// </summary>
    public static bool CanSelfUpdate(out string reason)
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe))
        {
            reason = "cannot locate the running executable";
            return false;
        }
        var dir = Path.GetDirectoryName(exe)!;
        if (File.Exists(Path.Combine(dir, "SessionLimit.dll")))
        {
            reason = "running a development build — update from source";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>Swaps in the staged build and relaunches. Does not return on success.</summary>
    public static void ApplyAndRestart(string staged)
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("no executable path");

        TryDelete(OldPath);

        // Renaming a running image is permitted; deleting one is not. This is what makes
        // in-place self-update possible without a separate helper process.
        File.Move(exe, OldPath);
        try
        {
            File.Move(staged, exe);
        }
        catch
        {
            File.Move(OldPath, exe);   // put the working build back before giving up
            throw;
        }

        Log.Info($"updated in place, relaunching {exe}");
        Process.Start(new ProcessStartInfo(exe, UpdatedFlag) { UseShellExecute = false });
    }

    /// <summary>Clears the previous build and any half-finished download. Call at startup.</summary>
    public static void CleanupPrevious()
    {
        TryDelete(OldPath);
        TryDelete(StagedPath);
    }

    /// <summary>
    /// After an update the replaced process is still shutting down. Wait briefly so the two
    /// never tail the transcripts — or write the ledger — at the same time.
    /// </summary>
    public static void WaitForPreviousExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var self = Environment.ProcessId;
        while (DateTime.UtcNow < deadline)
        {
            Process[] others;
            try { others = Process.GetProcessesByName("SessionLimit"); }
            catch { return; }

            var any = others.Any(p => p.Id != self);
            foreach (var p in others) p.Dispose();
            if (!any) return;

            Thread.Sleep(200);
        }
        Log.Warn("previous instance still running after update; continuing anyway");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Error($"could not delete {Path.GetFileName(path)}", ex); }
    }
}
