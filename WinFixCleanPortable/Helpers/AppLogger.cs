using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinFixCleanPortable.Helpers;

public static class AppLogger
{
    private static readonly object SyncLock = new();
    private static string? _logFilePath;

    public static string LogFilePath
    {
        get
        {
            if (_logFilePath != null)
                return _logFilePath;

            // Try portable folder next to EXE first; fallback to %TEMP% if read-only (e.g. CD/locked USB)
            string portableDir = FormatHelper.GetPortableBaseDirectory();
            string candidate = Path.Combine(portableDir, "WinFixClean_Log.txt");
            try
            {
                File.AppendAllText(candidate, string.Empty);
                _logFilePath = candidate;
            }
            catch
            {
                _logFilePath = Path.Combine(Path.GetTempPath(), "WinFixClean_Log.txt");
            }
            return _logFilePath;
        }
    }

    public static void LogInfo(string message) => WriteEntry("INFO", message);
    public static void LogWarn(string message) => WriteEntry("WARN", message);

    public static void LogError(string context, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{context}: {ex.GetType().FullName}: {ex.Message}");
        sb.AppendLine(ex.StackTrace);
        var inner = ex.InnerException;
        int depth = 0;
        while (inner != null && depth < 5)
        {
            sb.AppendLine($"  ---> Inner[{depth}]: {inner.GetType().FullName}: {inner.Message}");
            sb.AppendLine(inner.StackTrace);
            inner = inner.InnerException;
            depth++;
        }
        WriteEntry("ERROR", sb.ToString().TrimEnd());
    }

    private static void WriteEntry(string level, string message)
    {
        try
        {
            lock (SyncLock)
            {
                string path = LogFilePath;
                // Keep portable log tidy (trim if > 3 MB)
                if (File.Exists(path) && new FileInfo(path).Length > 3 * 1024 * 1024)
                {
                    string backup = path + ".old";
                    File.Move(path, backup, overwrite: true);
                }

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Never crash because of logging
        }
    }

    public static void OpenLogInNotepad()
    {
        try
        {
            string path = LogFilePath;
            if (!File.Exists(path))
                LogInfo("Log file opened by user.");

            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            FormatHelper.RevealInExplorer(LogFilePath);
        }
    }
}
