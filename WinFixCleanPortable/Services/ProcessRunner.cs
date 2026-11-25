using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace WinFixCleanPortable.Services;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool Success => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}\n{StdErr}";
}

/// <summary>Runs console tools (bcdedit, mountvol, chkdsk, PowerShell) with captured + streamed output and no stdin (never hangs on prompts).</summary>
public static class ProcessRunner
{
    static ProcessRunner()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding OemEncoding
    {
        get
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.UTF8; }
        }
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        Action<string>? onLine = null,
        CancellationToken ct = default,
        Encoding? encoding = null)
    {
        encoding ??= OemEncoding;
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };

        using var proc = new Process { StartInfo = psi };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            onLine?.Invoke($"[ERROR] Could not start {fileName}: {ex.Message}");
            return new ProcessResult { ExitCode = -1, StdErr = ex.Message };
        }

        // Close stdin immediately: tools that ask "Y/N?" get EOF instead of hanging forever
        try { proc.StandardInput.Close(); } catch { }

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        async Task Pump(StreamReader reader, StringBuilder sb, bool isErr)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                string clean = line.Replace("\0", string.Empty).TrimEnd();
                sb.AppendLine(clean);
                if (onLine != null && !string.IsNullOrWhiteSpace(clean))
                    onLine(isErr ? $"[ERR] {clean}" : clean);
            }
        }

        await Task.WhenAll(Pump(proc.StandardOutput, outSb, false), Pump(proc.StandardError, errSb, true));
        await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        return new ProcessResult { ExitCode = proc.ExitCode, StdOut = outSb.ToString(), StdErr = errSb.ToString() };
    }

    /// <summary>Runs a Windows PowerShell 5.1 script via -EncodedCommand (no quoting/injection issues), UTF-8 output.</summary>
    public static Task<ProcessResult> RunPowerShellAsync(string script, Action<string>? onLine = null, CancellationToken ct = default)
    {
        string full = "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + script;
        string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {b64}", onLine, ct, Encoding.UTF8);
    }
}
