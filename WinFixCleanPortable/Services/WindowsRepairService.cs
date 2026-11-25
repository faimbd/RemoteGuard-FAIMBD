using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WinFixCleanPortable.Models;

namespace WinFixCleanPortable.Services;

public class WindowsRepairService
{
    private static readonly (string FileName, string Description, bool CheckSysWow64)[] CriticalDllList =
    {
        ("vcruntime140.dll", "Visual C++ 2015-2022 Runtime", true),
        ("vcruntime140_1.dll", "Visual C++ 2019-2022 x64 Runtime", false),
        ("msvcp140.dll", "Visual C++ Standard C++ Library", true),
        ("ucrtbase.dll", "Universal C Runtime (Windows Core)", true),
        ("msvcr120.dll", "Visual C++ 2013 Runtime", true),
        ("msvcp120.dll", "Visual C++ 2013 C++ Library", true),
        ("msvcr100.dll", "Visual C++ 2010 Runtime", true),
        ("mfc140u.dll", "Microsoft Foundation Classes Runtime", true),
        ("d3dcompiler_47.dll", "Direct3D Shader Compiler", true),
        ("d3dx9_43.dll", "DirectX 9 Legacy Runtime (Games/Apps)", true),
        ("xinput1_3.dll", "DirectX XInput Controller Library", true),
        ("xapofx1_5.dll", "DirectX Audio Effects Library", true),
        ("dbghelp.dll", "Windows Image Helper / Crash Debug DLL", true),
        ("kernel32.dll", "Windows Core Kernel Base", true),
        ("ntdll.dll", "Windows NT Layer Core DLL", true),
        ("user32.dll", "Windows User Interface Core DLL", true),
        ("advapi32.dll", "Windows Advanced Security/Registry API", true)
    };

    public List<MissingFileReportItem> CheckCommonSystemDlls()
    {
        var results = new List<MissingFileReportItem>();
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sysWow64 = Path.Combine(winDir, "SysWOW64");
        bool hasWow64 = Directory.Exists(sysWow64);

        foreach (var (dll, desc, checkWow) in CriticalDllList)
        {
            string p64 = Path.Combine(sys32, dll);
            bool exists64 = File.Exists(p64);

            results.Add(new MissingFileReportItem
            {
                FileName = dll,
                Status = exists64 ? "Present" : "Missing",
                StatusColor = exists64 ? "#22C55E" : "#EF4444",
                Source = "System32 (64-bit)",
                Details = exists64 ? $"{desc} — OK" : $"{desc} — Missing in System32! Install VC++ Redistributable / DirectX or run SFC."
            });

            if (checkWow && hasWow64)
            {
                string p32 = Path.Combine(sysWow64, dll);
                bool exists32 = File.Exists(p32);
                if (!exists32)
                {
                    results.Add(new MissingFileReportItem
                    {
                        FileName = dll,
                        Status = "Missing",
                        StatusColor = "#F59E0B",
                        Source = "SysWOW64 (32-bit)",
                        Details = $"{desc} — Missing 32-bit copy in SysWOW64 (needed by 32-bit apps/games)."
                    });
                }
            }
        }

        // Order missing first
        return results
            .OrderBy(r => r.Status == "Missing" ? 0 : 1)
            .ThenBy(r => r.FileName)
            .ToList();
    }

    public List<MissingFileReportItem> ParseCbsLogForSystemFileIssues()
    {
        var findings = new List<MissingFileReportItem>();
        string cbsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS", "CBS.log");

        if (!File.Exists(cbsPath))
        {
            findings.Add(new MissingFileReportItem
            {
                FileName = "CBS.log",
                Status = "Info",
                StatusColor = "#3B82F6",
                Source = "Windows CBS",
                Details = "CBS.log not found yet. Run 'Verify Missing System Files (SFC)' first."
            });
            return findings;
        }

        try
        {
            // Read last ~4 MB of CBS.log safely using FileShare.ReadWrite
            var lines = ReadTailLines(cbsPath, 4 * 1024 * 1024);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Regexes for CBS [SR] lines
            var rxCannotRepair = new Regex(@"Cannot repair member file.*?\""(?<file>[^\""]+)\""|Cannot repair member file\s+\[.*?\]\s+(?<file2>\S+)", RegexOptions.IgnoreCase);
            var rxRepaired = new Regex(@"Repairing corrupted file\s+(?<file>\S+)|Repaired file\s+\[.*?\]\s+(?<file2>\S+)|Successfully repaired\s+(?<file3>\S+)", RegexOptions.IgnoreCase);
            var rxCorrupt = new Regex(@"Hashes for file member.*?\""(?<file>[^\""]+)\""|Could not reproject corrupted file\s+(?<file2>\S+)", RegexOptions.IgnoreCase);
            var rxMissing = new Regex(@"Missing file.*?\""(?<file>[^\""]+)\""|File\s+(?<file2>\S+)\s+is missing", RegexOptions.IgnoreCase);

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (!line.Contains("[SR]", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("Cannot repair", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("corrupted file", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("Hashes for file member", StringComparison.OrdinalIgnoreCase))
                    continue;

                string status = string.Empty;
                string color = "#EF4444";
                string fileName = string.Empty;

                var m1 = rxCannotRepair.Match(line);
                var m2 = rxRepaired.Match(line);
                var m3 = rxCorrupt.Match(line);
                var m4 = rxMissing.Match(line);

                if (m1.Success)
                {
                    status = "Unrepaired / Missing";
                    color = "#EF4444";
                    fileName = FirstNonEmpty(m1.Groups["file"].Value, m1.Groups["file2"].Value);
                }
                else if (m2.Success)
                {
                    status = "Repaired";
                    color = "#22C55E";
                    fileName = FirstNonEmpty(m2.Groups["file"].Value, m2.Groups["file2"].Value, m2.Groups["file3"].Value);
                }
                else if (m3.Success)
                {
                    status = "Corrupted";
                    color = "#F59E0B";
                    fileName = FirstNonEmpty(m3.Groups["file"].Value, m3.Groups["file2"].Value);
                }
                else if (m4.Success)
                {
                    status = "Missing";
                    color = "#EF4444";
                    fileName = FirstNonEmpty(m4.Groups["file"].Value, m4.Groups["file2"].Value);
                }

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    string key = $"{status}|{fileName}";
                    if (seen.Add(key))
                    {
                        findings.Add(new MissingFileReportItem
                        {
                            FileName = fileName.Trim('"', ',', ';', '[', ']'),
                            Status = status,
                            StatusColor = color,
                            Source = "CBS.log (SFC)",
                            Details = line.Length > 220 ? line[..220] + "..." : line
                        });
                    }
                }

                if (findings.Count >= 200)
                    break;
            }
        }
        catch (Exception ex)
        {
            findings.Add(new MissingFileReportItem
            {
                FileName = "CBS.log",
                Status = "Access Error",
                StatusColor = "#F59E0B",
                Source = "CBS.log",
                Details = $"Could not read CBS.log ({ex.Message}). Run as Administrator for full CBS log inspection."
            });
        }

        return findings;
    }

    public async Task<int> RunSystemCommandAsync(
        string fileName,
        string arguments,
        Action<string> onOutput,
        CancellationToken ct,
        bool isUtf16Output = false)
    {
        onOutput($"> {fileName} {arguments}");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = isUtf16Output ? Encoding.Unicode : Encoding.UTF8,
            StandardErrorEncoding = isUtf16Output ? Encoding.Unicode : Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            onOutput($"[ERROR] Failed to launch {fileName}: {ex.Message}");
            return -1;
        }

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        });

        var outTask = ReadStreamLinesAsync(process.StandardOutput, onOutput, ct);
        var errTask = ReadStreamLinesAsync(process.StandardError, line => onOutput($"[STDERR] {line}"), ct);

        await Task.WhenAll(outTask, errTask);
        await process.WaitForExitAsync(ct);

        onOutput($"--- Exit Code: {process.ExitCode} ---");
        return process.ExitCode;
    }

    private static async Task ReadStreamLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken ct)
    {
        string? lastProgressLine = null;
        while (!ct.IsCancellationRequested)
        {
            string? raw = await reader.ReadLineAsync(ct);
            if (raw == null)
                break;

            // Clean control chars / stray null bytes from SFC or DISM progress bars
            string cleaned = raw.Replace("\0", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;

            // Collapse noisy repeated percentage lines
            if (cleaned == lastProgressLine)
                continue;
            lastProgressLine = cleaned;

            onLine(cleaned);
        }
    }

    private static List<string> ReadTailLines(string path, int maxBytes)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length > maxBytes)
            fs.Seek(-maxBytes, SeekOrigin.End);

        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (fs.Position > 0)
            reader.ReadLine(); // discard partial first line

        string? line;
        while ((line = reader.ReadLine()) != null)
            lines.Add(line);

        return lines;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }
        return string.Empty;
    }
}
