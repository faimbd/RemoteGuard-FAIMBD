using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using RemoteGuard.Host.Security;

namespace RemoteGuard.Host.Network;

public sealed class TunnelManager : IDisposable
{
    private Process? _cloudflaredProcess;
    public string? PublicTunnelUrl { get; private set; }
    public bool IsTunnelRunning => _cloudflaredProcess != null && !_cloudflaredProcess.HasExited;

    public event Action<string>? TunnelUrlReady;
    public event Action<string>? TunnelLog;

    private static string BundledCloudflaredPath =>
        Path.Combine(SecurityStore.DataDirectory, "cloudflared.exe");

    public static string? FindCloudflaredPath()
    {
        if (File.Exists(BundledCloudflaredPath)) return BundledCloudflaredPath;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir.Trim(), "cloudflared.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static async Task<string> EnsureCloudflaredAsync(Action<string>? onProgress = null)
    {
        var existing = FindCloudflaredPath();
        if (existing != null) return existing;

        Directory.CreateDirectory(SecurityStore.DataDirectory);
        onProgress?.Invoke("Downloading official cloudflared.exe from Cloudflare GitHub...");
        const string url = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        string tmpPath = BundledCloudflaredPath + ".tmp";
        await using (var fs = File.Create(tmpPath))
        {
            await resp.Content.CopyToAsync(fs);
        }
        File.Move(tmpPath, BundledCloudflaredPath, overwrite: true);
        onProgress?.Invoke("cloudflared.exe downloaded successfully.");
        return BundledCloudflaredPath;
    }

    public async Task StartQuickTunnelAsync(int localPort, string? customTunnelToken = null)
    {
        StopTunnel();

        string exePath = await EnsureCloudflaredAsync(msg => TunnelLog?.Invoke(msg));
        string args = string.IsNullOrWhiteSpace(customTunnelToken)
            ? $"tunnel --no-autoupdate --url http://localhost:{localPort}"
            : $"tunnel --no-autoupdate run --token {customTunnelToken.Trim()}";

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _cloudflaredProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var urlRegex = new Regex(@"https://[a-zA-Z0-9\-]+\.trycloudflare\.com", RegexOptions.Compiled);

        void HandleLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            TunnelLog?.Invoke(line);
            var m = urlRegex.Match(line);
            if (m.Success)
            {
                PublicTunnelUrl = m.Value;
                TunnelUrlReady?.Invoke(PublicTunnelUrl);
            }
        }

        _cloudflaredProcess.OutputDataReceived += (_, e) => HandleLine(e.Data);
        _cloudflaredProcess.ErrorDataReceived += (_, e) => HandleLine(e.Data);
        _cloudflaredProcess.Start();
        _cloudflaredProcess.BeginOutputReadLine();
        _cloudflaredProcess.BeginErrorReadLine();
    }

    public void StopTunnel()
    {
        PublicTunnelUrl = null;
        try
        {
            if (_cloudflaredProcess != null && !_cloudflaredProcess.HasExited)
                _cloudflaredProcess.Kill(entireProcessTree: true);
        }
        catch { }
        _cloudflaredProcess?.Dispose();
        _cloudflaredProcess = null;
    }

    public static (string LanIp, string? TailscaleIp) GetLocalAddresses()
    {
        string lanIp = "127.0.0.1";
        string? tailscaleIp = null;

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("100.") && ni.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
                        tailscaleIp = ip;
                    else if (lanIp == "127.0.0.1" && !ip.StartsWith("169.254.") && !ip.StartsWith("100."))
                        lanIp = ip;
                }
            }
        }
        catch { }

        return (lanIp, tailscaleIp);
    }

    public static void EnsureFirewallRule(int port)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"advfirewall firewall add rule name=\"RemoteGuard Port {port}\" dir=in action=allow protocol=TCP localport={port}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(3000);
        }
        catch { }
    }

    public void Dispose()
    {
        StopTunnel();
    }
}
