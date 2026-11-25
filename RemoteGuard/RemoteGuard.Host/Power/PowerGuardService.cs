using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteGuard.Host.Power;

/// <summary>
/// Enforces 24/7 always-on mode and Laptop Lid Close = "Do Nothing"
/// while still allowing the user to manually Sleep, Hibernate, or Shut Down.
/// </summary>
public sealed class PowerGuardService : IDisposable
{
    [Flags]
    private enum ExecutionState : uint
    {
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_SYSTEM_REQUIRED = 0x00000001
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    private System.Threading.Timer? _keepAwakeTimer;
    private bool _keepAwakeEnabled;

    public bool IsKeepAwakeActive => _keepAwakeEnabled;

    public void StartKeepAwake(bool enabled)
    {
        _keepAwakeEnabled = enabled;
        if (enabled)
        {
            PulseKeepAwake();
            _keepAwakeTimer ??= new System.Threading.Timer(_ => PulseKeepAwake(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
        else
        {
            _keepAwakeTimer?.Dispose();
            _keepAwakeTimer = null;
            SetThreadExecutionState(ExecutionState.ES_CONTINUOUS);
        }
    }

    private void PulseKeepAwake()
    {
        if (!_keepAwakeEnabled) return;
        // ES_SYSTEM_REQUIRED + ES_AWAYMODE_REQUIRED prevents automatic idle sleep/hibernation
        // without blocking manual user-initiated Sleep/Hibernate/Shutdown.
        SetThreadExecutionState(
            ExecutionState.ES_CONTINUOUS |
            ExecutionState.ES_SYSTEM_REQUIRED |
            ExecutionState.ES_AWAYMODE_REQUIRED);
    }

    /// <summary>
    /// Applies Windows Power Options:
    /// - Lid close action on AC & DC = 0 (Do Nothing)
    /// - Automatic Sleep timeout on AC & DC = 0 (Never)
    /// - Automatic Hibernate timeout on AC & DC = 0 (Never)
    /// Manual sleep/hibernate/shutdown from Start Menu remains fully available.
    /// </summary>
    public static (bool Success, string Output) ApplyAlwaysOnAndLidDoNothing(bool lidDoNothing, bool neverAutoSleep)
    {
        var cmds = new List<string>();

        if (lidDoNothing)
        {
            // SUB_BUTTONS GUID: 4f971e89-eebd-4455-a8de-9e59040e7347
            // LIDACTION GUID:   5ca83367-6e45-459f-a27b-476b1d01c936
            // 0 = Do nothing
            cmds.Add("/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0");
            cmds.Add("/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0");
        }

        if (neverAutoSleep)
        {
            cmds.Add("/change standby-timeout-ac 0");
            cmds.Add("/change standby-timeout-dc 0");
            cmds.Add("/change hibernate-timeout-ac 0");
            cmds.Add("/change hibernate-timeout-dc 0");
            // Prevent unattended sleep timeout (2-minute wake-to-sleep timer)
            cmds.Add("/setacvalueindex SCHEME_CURRENT SUB_SLEEP 7bc4a2f9-d8fc-4469-b07b-33eb785aaca0 0");
            cmds.Add("/setdcvalueindex SCHEME_CURRENT SUB_SLEEP 7bc4a2f9-d8fc-4469-b07b-33eb785aaca0 0");
        }

        cmds.Add("/setactive SCHEME_CURRENT");

        var log = new List<string>();
        bool allOk = true;

        foreach (var args in cmds)
        {
            var (ok, outText) = RunPowerCfg(args);
            if (!ok) allOk = false;
            if (!string.IsNullOrWhiteSpace(outText))
                log.Add(outText.Trim());
        }

        return (allOk, log.Count == 0 ? "Power policy applied successfully (Lid = Do Nothing, Auto Sleep/Hibernate = Never)." : string.Join(Environment.NewLine, log));
    }

    public static (bool Success, string Output) RestoreDefaultPowerPolicy()
    {
        var cmds = new[]
        {
            "/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1", // Sleep on AC lid close
            "/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1", // Sleep on DC lid close
            "/change standby-timeout-ac 30",
            "/change standby-timeout-dc 15",
            "/change hibernate-timeout-ac 0",
            "/change hibernate-timeout-dc 60",
            "/setactive SCHEME_CURRENT"
        };

        bool allOk = true;
        foreach (var args in cmds)
        {
            var (ok, _) = RunPowerCfg(args);
            if (!ok) allOk = false;
        }
        return (allOk, allOk ? "Restored standard Windows sleep & lid-close defaults." : "Some powercfg commands failed (run as Administrator).");
    }

    private static (bool Success, string Output) RunPowerCfg(string arguments)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = arguments,
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
            return (p.ExitCode == 0, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Dispose()
    {
        StartKeepAwake(false);
    }
}
