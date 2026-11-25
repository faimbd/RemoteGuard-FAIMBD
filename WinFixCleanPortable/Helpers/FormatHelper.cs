using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace WinFixCleanPortable.Helpers;

public static class FormatHelper
{
    private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB", "TB" };

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        if (bytes == 0) return "0 B";

        double size = bytes;
        int order = 0;
        while (size >= 1024 && order < SizeSuffixes.Length - 1)
        {
            order++;
            size /= 1024.0;
        }

        return order == 0 ? $"{bytes} B" : $"{size:0.##} {SizeSuffixes[order]}";
    }

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool RestartAsAdministrator()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void RevealInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = true
                    });
                }
            }
        }
        catch
        {
            // Ignore explorer launch errors
        }
    }

    public static void OpenFolderInExplorer(string folderPath)
    {
        try
        {
            if (Directory.Exists(folderPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    public static string GetPortableBaseDirectory()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;
        }
        return AppContext.BaseDirectory;
    }

    public static string GetWindowsFriendlyName()
    {
        var v = Environment.OSVersion.Version;
        if (v.Major == 10 && v.Build >= 22000)
            return $"Windows 11 (Build {v.Build})";
        if (v.Major == 10)
            return $"Windows 10 (Build {v.Build})";
        return $"Windows {v.Major}.{v.Minor} (Build {v.Build})";
    }
}
