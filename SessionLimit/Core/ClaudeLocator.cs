using System.IO;
using Microsoft.Win32;

namespace SessionLimit;

/// <summary>
/// Finds the Claude Code binary.
///
/// There is no canonical install location. It ships through npm, a native installer, the
/// desktop app and several editor extensions, and on plenty of machines it is not on PATH
/// at all — so "not found" is a routine outcome that has to stay diagnosable rather than
/// fatal. Every probe is recorded so the settings panel can show where it looked.
/// </summary>
public static class ClaudeLocator
{
    private static readonly string[] Names = { "claude.exe", "claude.cmd", "claude.bat" };

    /// <summary>Big, slow, and never the answer.</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "Temp", "Cache", "Caches", "logs", "Crashpad", "GPUCache",
        "$RECYCLE.BIN", "System Volume Information", "WinSxS", "Installer", "assembly",
        ".git", ".cache", ".npm", ".nuget", ".gradle", ".m2", ".venv", ".vs",
    };

    private static string? _cached;
    private static List<string> _searched = new();

    public static IReadOnlyList<string> LastSearched => _searched;

    /// <summary>Discovery result, cached — the probes touch the registry and the disk.</summary>
    public static string Resolve(AppConfig cfg, bool force = false)
    {
        if (!force && _cached is not null) return _cached;

        var searched = new List<string>();
        var found = Probe(cfg, searched);
        _cached = found;
        _searched = searched;
        return found;
    }

    public static void Invalidate() => _cached = null;

    private static string Probe(AppConfig cfg, List<string> searched)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ClaudeExePath))
        {
            searched.Add($"configured: {cfg.ClaudeExePath}");
            if (File.Exists(cfg.ClaudeExePath)) return cfg.ClaudeExePath;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // PATH from the registry as well as this process. An app launched at login holds
        // whatever PATH existed then, so a Claude Code installed afterwards is invisible to
        // the process copy until the next sign-out.
        searched.Add("PATH (process + user + machine)");
        foreach (var dir in PathDirectories())
        {
            foreach (var name in Names)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry */ }
            }
        }

        var fixedPaths = new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(home, ".local", "bin", "claude.cmd"),
            Path.Combine(home, ".claude", "bin", "claude.exe"),
            Path.Combine(home, ".claude", "local", "claude.exe"),
            Path.Combine(home, ".bun", "bin", "claude.exe"),
            Path.Combine(appData, "npm", "claude.cmd"),
            Path.Combine(localApp, "Programs", "claude", "claude.exe"),
            Path.Combine(localApp, "Programs", "claude-code", "claude.exe"),
            Path.Combine(localApp, "Programs", "Claude Code", "claude.exe"),
            Path.Combine(localApp, "pnpm", "claude.cmd"),
            Path.Combine(localApp, "Volta", "bin", "claude.exe"),
        };
        foreach (var p in fixedPaths)
        {
            searched.Add(p);
            if (File.Exists(p)) return p;
        }

        // Editors bundle their own copy. Rather than name them — there are more than anyone
        // can list, and this machine turned up a ".antigravity" nobody would have guessed —
        // check every dot-directory in the profile and take the newest build across all of them.
        searched.Add(Path.Combine(home, ".*", "extensions"));
        var newest = EditorBinaries(home).OrderByDescending(x => x.Version).FirstOrDefault().Path;
        if (newest is not null) return newest;

        // One budget shared by every remaining probe. This runs on the UI thread during
        // startup and whenever settings opens, so the whole tail has to stay imperceptible;
        // anything slower belongs in DeepSearch, which the user asks for explicitly.
        var deadline = DateTime.UtcNow.AddMilliseconds(600);

        var registry = FromRegistry(searched, deadline);
        if (registry is not null) return registry;

        foreach (var root in new[]
                 {
                     Path.Combine(localApp, "AnthropicClaude"),
                     Path.Combine(localApp, "Programs"),
                     Path.Combine(localApp, "Anthropic"),
                     Path.Combine(appData, "Anthropic"),
                 })
        {
            searched.Add(root + @"\** (shallow)");
            var hit = ScanDirectory(root, maxDepth: 3, deadline);
            if (hit is not null) return hit;
        }

        return "";
    }

    /// <summary>PATH entries from the process plus both registry scopes, in order, deduped.</summary>
    private static IEnumerable<string> PathDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[]
                 {
                     Environment.GetEnvironmentVariable("PATH"),
                     SafeEnv(EnvironmentVariableTarget.User),
                     SafeEnv(EnvironmentVariableTarget.Machine),
                 })
        {
            if (string.IsNullOrEmpty(source)) continue;
            foreach (var raw in source.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (dir.Length > 0 && seen.Add(dir)) yield return dir;
            }
        }
    }

    private static string? SafeEnv(EnvironmentVariableTarget target)
    {
        try { return Environment.GetEnvironmentVariable("PATH", target); }
        catch { return null; }
    }

    /// <summary>Claude Code binaries under every editor directory in the user's profile.</summary>
    private static IEnumerable<(Version Version, string Path)> EditorBinaries(string home)
    {
        List<(Version, string)> all = new();
        try
        {
            // Editors keep extensions in a dot-directory: .vscode, .cursor, .windsurf, and
            // others that do not exist yet. Enumerating beats maintaining a list.
            foreach (var dir in Directory.EnumerateDirectories(home, ".*"))
                all.AddRange(ExtensionBinaries(System.IO.Path.Combine(dir, "extensions")));
        }
        catch (Exception ex) { Log.Error("claude discovery: profile scan failed", ex); }
        return all;
    }

    private static string? NewestExtensionBinary(string extRoot) =>
        ExtensionBinaries(extRoot).OrderByDescending(x => x.Version).FirstOrDefault().Path;

    /// <summary>
    /// Claude Code binaries under one extensions directory, with versions parsed properly.
    /// Sorting these as strings picks 2.1.96 over 2.1.217, because '9' sorts above '2'.
    /// </summary>
    private static IEnumerable<(Version Version, string Path)> ExtensionBinaries(string extRoot)
    {
        List<(Version, string)> found = new();
        try
        {
            if (!Directory.Exists(extRoot)) return found;

            foreach (var dir in Directory.GetDirectories(extRoot, "anthropic.claude-code-*"))
            {
                var exe = System.IO.Path.Combine(dir, "resources", "native-binary", "claude.exe");
                if (!File.Exists(exe)) continue;

                // anthropic.claude-code-2.1.217-win32-x64
                var name = System.IO.Path.GetFileName(dir);
                var parts = name.Split('-');
                var version = parts.Select(p => Version.TryParse(p, out var v) ? v : null)
                                   .FirstOrDefault(v => v is not null) ?? new Version(0, 0);
                found.Add((version, exe));
            }
        }
        catch (Exception ex) { Log.Error($"claude discovery: {extRoot} failed", ex); }
        return found;
    }

    /// <summary>App Paths, then any uninstall entry that looks like Claude.</summary>
    private static string? FromRegistry(List<string> searched, DateTime deadline)
    {
        searched.Add("registry (App Paths, uninstall entries)");
        try
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using (var appPath = root.OpenSubKey(
                           @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\claude.exe"))
                {
                    if (appPath?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
                }

                using var uninstall = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    var display = entry?.GetValue("DisplayName") as string;
                    if (display is null || display.IndexOf("claude", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (entry!.GetValue("InstallLocation") as string is { Length: > 0 } loc)
                    {
                        var hit = ScanDirectory(loc, maxDepth: 3, deadline);
                        if (hit is not null) return hit;
                    }
                }
            }
        }
        catch (Exception ex) { Log.Error("claude discovery: registry probe failed", ex); }
        return null;
    }

    /// <summary>
    /// Depth- and time-limited walk. Both limits matter: these roots can contain enormous
    /// trees, and this runs while someone is waiting on a settings panel.
    /// </summary>
    private static string? ScanDirectory(string root, int maxDepth, DateTime deadline)
    {
        if (!Directory.Exists(root) || maxDepth < 0 || DateTime.UtcNow > deadline) return null;
        try
        {
            foreach (var name in Names)
            {
                var candidate = Path.Combine(root, name);
                if (File.Exists(candidate)) return candidate;
            }

            foreach (var sub in Directory.EnumerateDirectories(root))
            {
                if (DateTime.UtcNow > deadline) return null;
                // Named exclusions only. Skipping every dot-directory looks tidy and is
                // wrong: .vscode and .cursor are the likeliest places for it to be.
                if (Skip.Contains(Path.GetFileName(sub))) continue;
                var hit = ScanDirectory(sub, maxDepth - 1, deadline);
                if (hit is not null) return hit;
            }
        }
        catch { /* permissions, junction loops, vanished directories */ }
        return null;
    }

    /// <summary>
    /// Last resort, user-initiated: a wider sweep of the profile and program directories.
    /// Never runs on its own — it is slow enough to be noticed.
    /// </summary>
    public static string DeepSearch(TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Editor extension directories first: the most common home, and reachable directly
        // instead of waiting for a general walk to stumble across it.
        if (EditorBinaries(home).OrderByDescending(x => x.Version).FirstOrDefault().Path is { } fromEditor)
        {
            Log.Info($"claude discovery: deep search found {fromEditor}");
            return fromEditor;
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            home,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        foreach (var root in roots.Distinct().Where(r => !string.IsNullOrEmpty(r)))
        {
            var hit = ScanDirectory(root, maxDepth: 6, deadline);
            if (hit is not null)
            {
                Log.Info($"claude discovery: deep search found {hit}");
                return hit;
            }
            if (DateTime.UtcNow > deadline) break;
        }
        Log.Warn("claude discovery: deep search found nothing");
        return "";
    }

    /// <summary>True when Claude Code has clearly run here even though no binary was found.</summary>
    public static int TranscriptCount()
    {
        try
        {
            return Directory.Exists(Paths.ClaudeProjectsDir)
                ? Directory.EnumerateFiles(Paths.ClaudeProjectsDir, "*.jsonl", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch { return 0; }
    }
}
