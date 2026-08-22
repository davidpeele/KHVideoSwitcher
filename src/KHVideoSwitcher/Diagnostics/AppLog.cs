using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace KHVideoSwitcher.Diagnostics;

/// <summary>
/// Rolling plain-text log for diagnosing tester-reported issues, written to
/// %APPDATA%\KHVideoSwitcher\logs\app-yyyyMMdd.log. Every line is scrubbed of the
/// Windows user profile path (e.g. C:\Users\david\...) before it touches disk -
/// that path is the one place a real name routinely leaks into .NET exception
/// stack traces (via embedded source-file paths) or capture/device names, and
/// this app is meant to run from a personal OneDrive folder. Scrubbing at write
/// time means the file on disk is always safe to attach, not just what's shown
/// on screen.
/// </summary>
public static class AppLog
{
    private static readonly object _lock = new();
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KHVideoSwitcher", "logs");

    private static readonly Regex UsersPathPattern = new(
        @"([A-Za-z]:)\\Users\\[^\\""\s]+\\",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string FilePathFor(DateTime day) => Path.Combine(_dir, $"app-{day:yyyyMMdd}.log");

    /// <summary>Deletes log files older than 14 days. Call once at startup - best-effort, never throws.</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(_dir))
                return;
            var cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (var file in Directory.GetFiles(_dir, "app-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // Housekeeping must never block startup.
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Logs a message plus the exception's scrubbed ToString() (type, message, and stack trace).</summary>
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {Scrub(message)}";
            lock (_lock)
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(FilePathFor(DateTime.Now), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never be the reason the app crashes.
        }
    }

    /// <summary>Replaces "&lt;drive&gt;:\Users\&lt;name&gt;\" with "&lt;drive&gt;:\Users\&lt;redacted&gt;\", case-insensitive.</summary>
    public static string Scrub(string text) => UsersPathPattern.Replace(text, @"$1\Users\<redacted>\");

    /// <summary>Returns the last N lines from today's and yesterday's log (oldest first), already scrubbed.</summary>
    public static string TailRecentLines(int maxLines = 200)
    {
        try
        {
            var files = new[] { FilePathFor(DateTime.Now.AddDays(-1)), FilePathFor(DateTime.Now) }
                .Where(File.Exists);
            var lines = new List<string>();
            foreach (var file in files)
                lines.AddRange(File.ReadAllLines(file));
            return lines.Count == 0 ? "(no log entries yet)" : string.Join(Environment.NewLine, lines.TakeLast(maxLines));
        }
        catch (Exception ex)
        {
            return $"(could not read log: {ex.Message})";
        }
    }
}
