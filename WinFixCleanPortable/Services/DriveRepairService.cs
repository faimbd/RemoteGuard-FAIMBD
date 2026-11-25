using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinFixCleanPortable.Helpers;
using WinFixCleanPortable.Models;

namespace WinFixCleanPortable.Services;

/// <summary>
/// Fixes pen drives / hard disks that don't show up (offline, read-only, no drive letter, RAW, corrupted)
/// and formats them — with hard safety gates so the Windows/system disk can never be formatted.
/// </summary>
public class DriveRepairService
{
    public static readonly string[] AllowedFileSystems = { "exFAT", "NTFS", "FAT32" };
    public const long Fat32MaxBytes = 32L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> SpecialPartitionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Reserved", "Recovery", "Unknown"
    };

    private const string ListScript = @"
$ErrorActionPreference='Stop'
$out = @()
foreach ($d in @(Get-Disk)) {
  $pl = @()
  $parts = @()
  try { $parts = @(Get-Partition -DiskNumber $d.Number -ErrorAction Stop) } catch {}
  foreach ($p in $parts) {
    $v = $null
    try { $v = $p | Get-Volume -ErrorAction Stop } catch {}
    $dl = ''
    if ($p.DriveLetter -and [int][char]$p.DriveLetter -ne 0) { $dl = [string]$p.DriveLetter }
    $pl += [pscustomobject]@{
      PartitionNumber = [int]$p.PartitionNumber; DriveLetter = $dl; Size = [int64]$p.Size
      Type = [string]$p.Type; IsHidden = [bool]$p.IsHidden; IsSystem = [bool]$p.IsSystem; IsBoot = [bool]$p.IsBoot
      FileSystem = $(if ($v) { [string]$v.FileSystem } else { '' })
      Label = $(if ($v) { [string]$v.FileSystemLabel } else { '' })
      Free = $(if ($v) { [int64]$v.SizeRemaining } else { [int64]0 })
      Health = $(if ($v) { [string]$v.HealthStatus } else { '' })
    }
  }
  $out += [pscustomobject]@{
    Number = [int]$d.Number; Name = [string]$d.FriendlyName; Bus = [string]$d.BusType; Size = [int64]$d.Size
    Style = [string]$d.PartitionStyle; IsOffline = [bool]$d.IsOffline; IsReadOnly = [bool]$d.IsReadOnly
    IsSystem = [bool]$d.IsSystem; IsBoot = [bool]$d.IsBoot; Health = [string]$d.HealthStatus
    OfflineReason = [string]$d.OfflineReason; Partitions = $pl
  }
}
'@@JSON@@' + (ConvertTo-Json -InputObject @($out) -Depth 6 -Compress)
";

    public async Task<(List<DriveRow> Rows, string? Error)> GetDrivesAsync(CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunPowerShellAsync(ListScript, null, ct);
        int idx = result.StdOut.IndexOf("@@JSON@@", StringComparison.Ordinal);
        if (idx < 0)
        {
            string err = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            if (err.Contains("Access", StringComparison.OrdinalIgnoreCase) || err.Contains("denied", StringComparison.OrdinalIgnoreCase))
                err = "Access denied — please click 'Restart as Admin' to scan and fix drives.";
            return (new List<DriveRow>(), $"Could not read disks: {Shorten(err)}");
        }

        string json = result.StdOut[(idx + 8)..].Trim();
        try
        {
            return (ParseDrivesJson(json, GetSystemDriveLetter(), GetAppDriveLetter()), null);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Drive JSON parse failed", ex);
            return (new List<DriveRow>(), $"Could not parse disk list: {ex.Message}");
        }
    }

    public static string GetSystemDriveLetter() =>
        (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\', ':').ToUpperInvariant();

    public static string GetAppDriveLetter()
    {
        string root = Path.GetPathRoot(FormatHelper.GetPortableBaseDirectory()) ?? string.Empty;
        return root.Length >= 2 && root[1] == ':' ? root[..1].ToUpperInvariant() : string.Empty;
    }

    public static List<DriveRow> ParseDrivesJson(string json, string systemLetter, string appLetter)
    {
        var rows = new List<DriveRow>();
        using var doc = JsonDocument.Parse(json);
        var disks = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : new List<JsonElement> { doc.RootElement };

        foreach (var d in disks)
        {
            var parts = new List<JsonElement>();
            if (d.TryGetProperty("Partitions", out var pe))
            {
                if (pe.ValueKind == JsonValueKind.Array) parts = pe.EnumerateArray().ToList();
                else if (pe.ValueKind == JsonValueKind.Object) parts.Add(pe);
            }

            var letters = parts.Select(p => Str(p, "DriveLetter").ToUpperInvariant()).Where(l => l.Length == 1).ToList();
            bool isSys = Bool(d, "IsSystem"), isBoot = Bool(d, "IsBoot");
            bool hostsWindows = letters.Contains(systemLetter);
            bool hostsApp = !string.IsNullOrEmpty(appLetter) && letters.Contains(appLetter);

            string protectReason = isBoot || hostsWindows ? "Windows system disk — cannot be formatted or reset"
                                 : isSys ? "Contains the boot (EFI) partition — cannot be formatted or reset"
                                 : hostsApp ? "This portable app is running from this disk — copy it elsewhere first"
                                 : string.Empty;

            DriveRow MakeBase() => new()
            {
                DiskNumber = Int(d, "Number"),
                DiskName = Str(d, "Name"),
                BusType = Str(d, "Bus"),
                DiskSizeBytes = Long(d, "Size"),
                PartitionStyle = Str(d, "Style"),
                DiskOffline = Bool(d, "IsOffline"),
                OfflineReason = Str(d, "OfflineReason"),
                DiskReadOnly = Bool(d, "IsReadOnly"),
                DiskIsSystem = isSys,
                DiskIsBoot = isBoot,
                DiskHealth = Str(d, "Health"),
                IsProtected = protectReason.Length > 0,
                ProtectReason = protectReason
            };

            bool protectedDisk = protectReason.Length > 0;
            int added = 0;

            foreach (var p in parts)
            {
                string type = Str(p, "Type");
                bool special = Bool(p, "IsHidden") || Bool(p, "IsSystem") || SpecialPartitionTypes.Contains(type);
                string letter = Str(p, "DriveLetter").ToUpperInvariant();

                // On system disks hide EFI/MSR/Recovery clutter; on other disks only hide MSR "Reserved"
                if (special && (protectedDisk || type.Equals("Reserved", StringComparison.OrdinalIgnoreCase)) && letter.Length == 0)
                    continue;

                var row = MakeBase();
                row.PartitionNumber = Int(p, "PartitionNumber");
                row.DriveLetter = letter;
                row.Label = Str(p, "Label");
                row.FileSystem = Str(p, "FileSystem");
                row.PartitionSizeBytes = Long(p, "Size");
                row.FreeBytes = Long(p, "Free");
                row.PartitionType = type;
                row.IsSpecialPartition = special;
                row.VolumeHealth = Str(p, "Health");
                DetectProblems(row);
                rows.Add(row);
                added++;
            }

            if (added == 0)
            {
                var row = MakeBase();
                if (row.DiskSizeBytes <= 0)
                {
                    // Empty card-reader slot / device with no media: nothing to fix
                    row.Problems.Add("No media inserted (empty card reader slot or disconnected device) — insert the card/drive and Rescan");
                    rows.Add(row);
                    continue;
                }
                DetectProblems(row);
                if (row.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase))
                    row.Problems.Add("Not initialized (no partition table) — use 'Full Reset & Format'");
                else if (parts.Count == 0)
                    row.Problems.Add("No partitions (unallocated) — use 'Full Reset & Format'");
                rows.Add(row);
            }
        }

        return rows.OrderBy(r => r.DiskNumber).ThenBy(r => r.PartitionNumber).ToList();
    }

    private static void DetectProblems(DriveRow r)
    {
        if (r.DiskOffline)
            r.Problems.Add($"Disk is OFFLINE{(string.IsNullOrWhiteSpace(r.OfflineReason) ? "" : $" ({r.OfflineReason})")} — click 'Bring Online'");
        if (r.DiskReadOnly)
            r.Problems.Add("Disk is READ-ONLY / write-protected — click 'Clear Read-Only'");
        if (!string.IsNullOrWhiteSpace(r.DiskHealth) && !r.DiskHealth.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
            r.Problems.Add($"Disk health: {r.DiskHealth} — hardware may be failing, back up data now");

        if (r.IsWholeDiskRow) return;

        if (string.IsNullOrEmpty(r.DriveLetter) && !r.IsSpecialPartition)
            r.Problems.Add("No drive letter (not visible in Explorer) — click 'Assign Drive Letter'");
        if (!r.IsSpecialPartition && (string.IsNullOrWhiteSpace(r.FileSystem) || r.FileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase)))
            r.Problems.Add("RAW / unreadable file system — try 'Repair', otherwise 'Format'");
        if (!string.IsNullOrWhiteSpace(r.VolumeHealth) && !r.VolumeHealth.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
            r.Problems.Add($"File system errors ({r.VolumeHealth}) — click 'Repair (CHKDSK)'");
    }

    // ------------------------------ Safety gates ------------------------------

    public static (bool Allowed, string Reason) ValidateFormatPartition(DriveRow r, string fileSystem)
    {
        if (r.IsProtected) return (false, $"Blocked: {r.ProtectReason}.");
        if (r.IsWholeDiskRow) return (false, "This disk has no partition — use 'Full Reset & Format Whole Disk'.");
        if (r.IsSpecialPartition) return (false, "Blocked: this is a special (EFI/Recovery/hidden) partition.");
        if (!AllowedFileSystems.Contains(fileSystem)) return (false, "Unsupported file system.");
        if (fileSystem == "FAT32" && r.PartitionSizeBytes > Fat32MaxBytes)
            return (false, "FAT32 cannot be used on partitions larger than 32 GB — choose exFAT (works on TV/phones/Mac/Windows) or NTFS.");
        return (true, string.Empty);
    }

    public static (bool Allowed, string Reason) ValidateResetDisk(DriveRow r, string fileSystem)
    {
        if (r.DiskSizeBytes <= 0) return (false, "No media in this device (empty card reader?) — insert the card/drive and click Rescan.");
        if (r.IsProtected || r.DiskIsBoot || r.DiskIsSystem) return (false, $"Blocked: {(string.IsNullOrEmpty(r.ProtectReason) ? "system disk" : r.ProtectReason)}.");
        if (!AllowedFileSystems.Contains(fileSystem)) return (false, "Unsupported file system.");
        if (fileSystem == "FAT32" && r.DiskSizeBytes > Fat32MaxBytes)
            return (false, "FAT32 cannot be used on disks larger than 32 GB — choose exFAT or NTFS.");
        return (true, string.Empty);
    }

    public static (bool Allowed, string Reason) ValidateRepair(DriveRow r)
    {
        if (string.IsNullOrEmpty(r.DriveLetter)) return (false, "Assign a drive letter first, then run Repair.");
        if (r.DriveLetter == GetSystemDriveLetter()) return (false, "The Windows drive can't be repaired while running — use 'Windows Repair' → CHKDSK online scan instead.");
        if (r.DriveLetter == GetAppDriveLetter()) return (false, "This app is running from this drive — copy it to another drive first.");
        return (true, string.Empty);
    }

    public static (bool Allowed, string Reason) ValidateAssignLetter(DriveRow r)
    {
        if (r.IsWholeDiskRow) return (false, "This disk has no partition yet — use 'Full Reset & Format'.");
        if (!string.IsNullOrEmpty(r.DriveLetter)) return (false, $"Already has drive letter {r.DriveLetter}:.");
        if (r.IsSpecialPartition) return (false, "Special (EFI/Recovery/hidden) partitions should not get a drive letter.");
        return (true, string.Empty);
    }

    public static string SanitizeLabel(string label, string fileSystem)
    {
        string clean = Regex.Replace(label ?? string.Empty, @"[^\w\s\-\.]", "").Trim();
        int max = fileSystem == "NTFS" ? 32 : 11;
        if (clean.Length > max) clean = clean[..max];
        return string.IsNullOrWhiteSpace(clean) ? "USB_DRIVE" : clean;
    }

    // Re-verified INSIDE PowerShell right before the destructive command (disk numbers can change after re-plugging)
    private static string GuardScript(DriveRow r) => $@"
$ErrorActionPreference='Stop'
$d = Get-Disk -Number {r.DiskNumber}
if ($d.IsSystem -or $d.IsBoot) {{ throw 'BLOCKED: protected system/boot disk' }}
if ([int64]$d.Size -ne {r.DiskSizeBytes}) {{ throw 'BLOCKED: the disk changed since the last scan (re-plugged?) - click Rescan and try again' }}
";

    // ------------------------------ Actions ------------------------------

    public Task<ProcessResult> BringOnlineAsync(DriveRow r, Action<string> log) =>
        ProcessRunner.RunPowerShellAsync($"$ErrorActionPreference='Stop'; Set-Disk -Number {r.DiskNumber} -IsOffline $false; 'Disk {r.DiskNumber} is now ONLINE'", log);

    public Task<ProcessResult> ClearReadOnlyAsync(DriveRow r, Action<string> log) =>
        ProcessRunner.RunPowerShellAsync($"$ErrorActionPreference='Stop'; Set-Disk -Number {r.DiskNumber} -IsReadOnly $false; 'Read-only flag cleared on disk {r.DiskNumber}'", log);

    public Task<ProcessResult> AssignLetterAsync(DriveRow r, Action<string> log) =>
        ProcessRunner.RunPowerShellAsync(
            $"$ErrorActionPreference='Stop'; Add-PartitionAccessPath -DiskNumber {r.DiskNumber} -PartitionNumber {r.PartitionNumber} -AssignDriveLetter; " +
            $"$p = Get-Partition -DiskNumber {r.DiskNumber} -PartitionNumber {r.PartitionNumber}; \"Assigned drive letter: $($p.DriveLetter):\"", log);

    public Task<ProcessResult> RescanHardwareAsync(Action<string> log) =>
        ProcessRunner.RunPowerShellAsync("Update-HostStorageCache; 'Storage hardware rescanned'", log);

    public Task<ProcessResult> RepairAsync(DriveRow r, Action<string> log, CancellationToken ct) =>
        ProcessRunner.RunAsync("chkdsk.exe", $"{r.DriveLetter}: /f /x", log, ct);

    public Task<ProcessResult> FormatPartitionAsync(DriveRow r, string fs, string label, bool quick, Action<string> log, CancellationToken ct)
    {
        string safeLabel = SanitizeLabel(label, fs);
        string fullFlag = quick ? string.Empty : "-Full";
        string script = GuardScript(r) + $@"
$r = Format-Partition -DiskNumber {r.DiskNumber} -PartitionNumber {r.PartitionNumber} -FileSystem {fs} -NewFileSystemLabel '{safeLabel}' {fullFlag} -Confirm:$false -Force
$p = Get-Partition -DiskNumber {r.DiskNumber} -PartitionNumber {r.PartitionNumber}
if (-not $p.DriveLetter -or [int][char]$p.DriveLetter -eq 0) {{ $p | Add-PartitionAccessPath -AssignDriveLetter; $p = Get-Partition -DiskNumber {r.DiskNumber} -PartitionNumber {r.PartitionNumber} }}
""FORMAT OK: $($p.DriveLetter): {fs} '{safeLabel}'""
";
        return ProcessRunner.RunPowerShellAsync(script, log, ct);
    }

    public Task<ProcessResult> ResetAndFormatDiskAsync(DriveRow r, string fs, string label, string partitionStyle, Action<string> log, CancellationToken ct)
    {
        string safeLabel = SanitizeLabel(label, fs);
        string style = partitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase) ? "GPT" : "MBR";
        string script = GuardScript(r) + $@"
if ($d.IsOffline) {{ Set-Disk -Number {r.DiskNumber} -IsOffline $false; 'Disk brought online' }}
if ($d.IsReadOnly) {{ Set-Disk -Number {r.DiskNumber} -IsReadOnly $false; 'Read-only cleared' }}
$d = Get-Disk -Number {r.DiskNumber}
if ($d.PartitionStyle -ne 'RAW') {{ Clear-Disk -Number {r.DiskNumber} -RemoveData -RemoveOEM -Confirm:$false; 'Old partitions removed' }}
Initialize-Disk -Number {r.DiskNumber} -PartitionStyle {style} -ErrorAction SilentlyContinue
'Disk initialized ({style})'
$p = New-Partition -DiskNumber {r.DiskNumber} -UseMaximumSize -AssignDriveLetter
Start-Sleep -Seconds 2
$v = Format-Volume -Partition $p -FileSystem {fs} -NewFileSystemLabel '{safeLabel}' -Confirm:$false -Force
""RESET + FORMAT OK: $($v.DriveLetter): {fs} '{safeLabel}' ($([math]::Round($v.Size/1GB,2)) GB)""
";
        return ProcessRunner.RunPowerShellAsync(script, log, ct);
    }

    /// <summary>Removes registry/Group-Policy write-protection that blocks writing to USB drives.</summary>
    public static List<string> RemoveUsbWriteProtectionPolicies()
    {
        var report = new List<string>();
        void Fix(string subKey, string valueName)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(subKey, writable: true);
                if (k == null) { report.Add($"OK (not present): HKLM\\{subKey}\\{valueName}"); return; }
                var v = k.GetValue(valueName);
                if (v != null && Convert.ToInt32(v) != 0)
                {
                    k.SetValue(valueName, 0, RegistryValueKind.DWord);
                    report.Add($"FIXED: HKLM\\{subKey}\\{valueName} was {v} → set to 0");
                }
                else report.Add($"OK: HKLM\\{subKey}\\{valueName} = {(v ?? "not set")}");
            }
            catch (Exception ex) { report.Add($"FAILED: HKLM\\{subKey}\\{valueName} ({ex.Message}) — run as Admin"); }
        }

        Fix(@"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies", "WriteProtect");
        Fix(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}", "Deny_Write");
        Fix(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}", "Deny_Read");
        Fix(@"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices", "Deny_All");
        AppLogger.LogInfo("USB write-protection policy check: " + string.Join(" | ", report));
        return report;
    }

    // ------------------------------ JSON helpers ------------------------------
    private static string Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Replace("\0", "").Trim() : string.Empty;
    private static bool Bool(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && (v.ValueKind == JsonValueKind.True);
    private static int Int(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private static long Long(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "..." : s.Trim();
}
