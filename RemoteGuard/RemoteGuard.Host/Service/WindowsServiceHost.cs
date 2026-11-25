using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using RemoteGuard.Host.Power;
using RemoteGuard.Host.Security;

namespace RemoteGuard.Host.Service;

/// <summary>
/// Windows Service running as LocalSystem (Session 0).
/// Keeps PC awake 24/7 (Lid = Do Nothing) and launches RemoteGuard.Host.exe --agent
/// inside the active console session with SYSTEM privileges so it works on the Windows Lock Screen.
/// </summary>
public sealed class RemoteGuardWindowsService : BackgroundService
{
    public const string ServiceName = "RemoteGuardService";
    private readonly PowerGuardService _powerGuard = new();
    private readonly SecurityStore _store = new();
    private Process? _agentProcess;
    private uint _lastConsoleSessionId = uint.MaxValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.Reload();
        if (_store.Config.KeepAwake24x7 || _store.Config.LidCloseDoNothing)
        {
            PowerGuardService.ApplyAlwaysOnAndLidDoNothing(_store.Config.LidCloseDoNothing, _store.Config.KeepAwake24x7);
            _powerGuard.StartKeepAwake(_store.Config.KeepAwake24x7);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                uint activeSession = WTSGetActiveConsoleSessionId();
                if (activeSession != 0xFFFFFFFF)
                {
                    bool needSpawn = _agentProcess == null || _agentProcess.HasExited || activeSession != _lastConsoleSessionId;
                    if (needSpawn)
                    {
                        StopAgent();
                        _agentProcess = LaunchAgentInSession(activeSession);
                        _lastConsoleSessionId = activeSession;
                    }
                }
            }
            catch { }

            try { await Task.Delay(3000, stoppingToken); } catch { break; }
        }

        StopAgent();
        _powerGuard.Dispose();
    }

    private void StopAgent()
    {
        try
        {
            if (_agentProcess != null && !_agentProcess.HasExited)
                _agentProcess.Kill(entireProcessTree: true);
        }
        catch { }
        _agentProcess?.Dispose();
        _agentProcess = null;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, ref uint TokenInformation, uint TokenInformationLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private static Process? LaunchAgentInSession(uint sessionId)
    {
        const uint TOKEN_DUPLICATE = 0x0002;
        const uint TOKEN_QUERY = 0x0008;
        const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        const uint TOKEN_ADJUST_SESSIONID = 0x0100;
        const uint MAXIMUM_ALLOWED = 0x02000000;
        const int TokenSessionId = 12;

        IntPtr hToken = IntPtr.Zero;
        IntPtr hDupToken = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_SESSIONID, out hToken))
                return null;

            if (!DuplicateTokenEx(hToken, MAXIMUM_ALLOWED, IntPtr.Zero, 2, 1, out hDupToken))
                return null;

            if (!SetTokenInformation(hDupToken, TokenSessionId, ref sessionId, sizeof(uint)))
                return null;

            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath)) return null;

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default"
            };

            if (!CreateProcessAsUser(hDupToken, null, $"\"{exePath}\" --agent",
                IntPtr.Zero, IntPtr.Zero, false, 0x08000000 /* CREATE_NO_WINDOW */,
                IntPtr.Zero, Path.GetDirectoryName(exePath), ref si, out var pi))
            {
                return null;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return Process.GetProcessById(pi.dwProcessId);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hDupToken != IntPtr.Zero) CloseHandle(hDupToken);
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
        }
    }

    public static (bool Ok, string Message) InstallAndStartService()
    {
        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(exePath)) return (false, "Cannot locate executable path.");

        // Remove old if exists
        RunSc($"stop {ServiceName}");
        RunSc($"delete {ServiceName}");
        Thread.Sleep(500);

        var (okCreate, outCreate) = RunSc($"create {ServiceName} binPath= \"\\\"{exePath}\\\" --service\" start= auto obj= LocalSystem DisplayName= \"RemoteGuard 24/7 Lock-Screen Host\"");
        if (!okCreate) return (false, outCreate);

        RunSc($"description {ServiceName} \"Provides 24/7 always-on guard and lock-screen remote control for RemoteGuard.\"");
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/10000");

        // Enable Software SAS (Ctrl+Alt+Del) policy so SendSAS works on Lock Screen
        try
        {
            Microsoft.Win32.Registry.SetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
                "SoftwareSASGeneration", 3, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }

        var (okStart, outStart) = RunSc($"start {ServiceName}");
        return (okStart, okStart ? "Windows Service installed & started (Lock Screen + 24/7 active)." : outStart);
    }

    public static (bool Ok, string Message) UninstallService()
    {
        RunSc($"stop {ServiceName}");
        Thread.Sleep(500);
        var (ok, msg) = RunSc($"delete {ServiceName}");
        return (ok, ok ? "Windows Service stopped and removed." : msg);
    }

    public static string GetServiceStatus()
    {
        var (ok, output) = RunSc($"query {ServiceName}");
        if (!ok) return "Not Installed";
        if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return "Running (Lock-Screen Ready)";
        if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return "Installed (Stopped)";
        return "Installed";
    }

    private static (bool Ok, string Output) RunSc(string args)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode == 0, string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
