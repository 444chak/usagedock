using System.Text;

namespace UsageDock.Services;

/// <summary>
/// Minimal append only file log with daily rotation. Callers must never pass credentials in.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static DateOnly _currentDay;
    private static string? _currentFile;

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_currentFile is null || today != _currentDay)
                {
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    _currentDay = today;
                    _currentFile = Path.Combine(AppPaths.LogDirectory, $"usagedock-{today:yyyy-MM-dd}.log");
                    PruneOldLogs();
                }

                var line = $"{DateTime.Now:HH:mm:ss} {level} {message}{Environment.NewLine}";
                File.AppendAllText(_currentFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private static void PruneOldLogs()
    {
        try
        {
            var stale = Directory
                .EnumerateFiles(AppPaths.LogDirectory, "usagedock-*.log")
                .Order()
                .SkipLast(7);

            foreach (var file in stale)
            {
                File.Delete(file);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
