using System.IO;

namespace SessionLimit;

/// <summary>
/// Housekeeping for the conversations this app used to leave behind.
///
/// Before <c>--no-session-persistence</c>, every <c>claude -p "/usage"</c> poll saved a
/// conversation, so a day of ten-minute checks left dozens of one-line "/usage" chats in
/// the user's Claude Code history. New builds no longer create them; this clears the ones
/// already on disk.
/// </summary>
public static class UsageChats
{
    private const string Marker = "<command-name>/usage</command-name>";
    private const long MaxSize = 256 * 1024;

    /// <summary>
    /// Transcripts that are a bare /usage run and nothing else.
    ///
    /// The test is deliberately strict: the file must carry the /usage command marker AND
    /// contain no assistant message at all. A real conversation always has one, so nothing
    /// the user actually said to Claude can match — this deletes files, so a false positive
    /// is unacceptable in a way a false negative is not.
    /// </summary>
    public static List<string> Find()
    {
        var found = new List<string>();
        try
        {
            if (!Directory.Exists(Paths.ClaudeProjectsDir)) return found;

            foreach (var path in Directory.EnumerateFiles(Paths.ClaudeProjectsDir, "*.jsonl",
                                                          SearchOption.AllDirectories))
            {
                try
                {
                    if (new FileInfo(path).Length > MaxSize) continue;

                    var marked = false;
                    var hasAssistant = false;
                    foreach (var line in File.ReadLines(path))
                    {
                        if (!marked && line.Contains(Marker, StringComparison.Ordinal)) marked = true;
                        if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
                        {
                            hasAssistant = true;
                            break;
                        }
                    }
                    if (marked && !hasAssistant) found.Add(path);
                }
                catch { /* unreadable or vanished mid-scan */ }
            }
        }
        catch (Exception ex) { Log.Error("usage-chats: scan failed", ex); }
        return found;
    }

    public static (int Deleted, long Bytes) Delete(IEnumerable<string> paths)
    {
        var count = 0;
        long bytes = 0;
        foreach (var p in paths)
        {
            try
            {
                var size = new FileInfo(p).Length;
                File.Delete(p);
                count++;
                bytes += size;
            }
            catch (Exception ex) { Log.Error($"usage-chats: could not delete {Path.GetFileName(p)}", ex); }
        }
        if (count > 0) Log.Info($"usage-chats: removed {count} transcript(s), {bytes / 1024} KB");
        return (count, bytes);
    }
}
