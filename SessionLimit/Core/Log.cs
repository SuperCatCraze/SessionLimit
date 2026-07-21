using System.IO;

namespace SessionLimit;

/// <summary>Tiny append-only logger. Never throws — logging must not break collectors.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string Path { get; } = System.IO.Path.Combine(Paths.DataDir, "session-limit.log");

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);

    public static void Error(string msg, Exception? ex = null) =>
        Write("ERROR", ex is null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDir);
                // Keep the log from growing without bound across long-running sessions.
                if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000)
                    File.WriteAllText(Path, "");
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging is best-effort by design */ }
    }
}

public static class Paths
{
    public static string DataDir { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionLimit");

    public static string ConfigFile => System.IO.Path.Combine(DataDir, "config.json");
    public static string LedgerFile => System.IO.Path.Combine(DataDir, "ledger.jsonl");

    public static string ClaudeProjectsDir { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
}
