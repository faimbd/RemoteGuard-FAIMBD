using System.IO;
using System.Text.RegularExpressions;
using WinFixCleanPortable.Helpers;
using WinFixCleanPortable.Models;

namespace WinFixCleanPortable.Services;

public sealed class BcdSection
{
    public string Title { get; set; } = string.Empty;
    public List<(string Key, List<string> Values)> Items { get; } = new();
    public string Identifier => Items.Count > 0 && Items[0].Values.Count > 0 ? Items[0].Values[0] : string.Empty;

    public string Get(params string[] keys)
    {
        foreach (var (k, v) in Items)
            if (keys.Any(x => x.Equals(k, StringComparison.OrdinalIgnoreCase)) && v.Count > 0)
                return v[0];
        return string.Empty;
    }

    public List<string> GetAll(params string[] keys)
    {
        foreach (var (k, v) in Items)
            if (keys.Any(x => x.Equals(k, StringComparison.OrdinalIgnoreCase)))
                return v;
        return new List<string>();
    }
}

public sealed class BootScanResult
{
    public List<BootEntryItem> Entries { get; } = new();
    public int TimeoutSeconds { get; set; } = -1;
    public string CurrentId { get; set; } = string.Empty;
    public string DefaultId { get; set; } = string.Empty;
    public bool IsUefi { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Manages boot entries (UEFI firmware menu + Windows Boot Manager menu) through the official bcdedit tool.
/// The BCD store is itself a registry hive (HKLM\BCD00000000); bcdedit is the safe, supported way to edit it.
/// Every change is preceded by an automatic BCD backup, and Windows-critical entries are locked.
/// </summary>
public class BootManagerService
{
    public const string FwBootMgrGuid = "{a5a30fa2-3d06-4e9f-b5f4-a01df9d1fcba}";
    public const string BootMgrGuid = "{9dea862c-5cdd-4e70-acc1-f32b344d4795}";
    public const string MemDiagGuid = "{b2721d73-1db4-4c62-bf78-c548a880142d}";

    private static readonly Regex GuidRegex = new(@"^\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}$", RegexOptions.Compiled);
    private static readonly Regex AnyGuidRegex = new(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}", RegexOptions.Compiled);

    // English + common localized bcdedit element names
    private static readonly string[] DescKeys = { "description", "beschreibung", "descripción", "descrizione", "descrição", "описание", "açıklama", "opis", "beschrijving", "popis", "leírás", "描述", "説明", "설명" };
    private static readonly string[] DeviceKeys = { "device", "gerät", "dispositivo", "périphérique", "устройство", "urządzenie", "apparaat", "zařízení", "eszköz", "aygıt" };
    private static readonly string[] PathKeys = { "path", "pfad", "ruta", "percorso", "chemin", "caminho", "путь", "ścieżka", "pad", "cesta", "elérési út", "yol" };
    private static readonly string[] DisplayOrderKeys = { "displayorder", "anzeigereihenfolge" };
    private static readonly string[] DefaultKeys = { "default", "standard", "predeterminado", "predefinito", "par défaut", "padrão", "по умолчанию", "domyślny", "standaard", "výchozí" };
    private static readonly string[] TimeoutKeys = { "timeout", "zeitlimit", "tiempo de espera", "timeout", "délai d'attente", "tempo limite", "время ожидания", "limit czasu", "time-out" };

    public static readonly string[] LinuxKeywords =
    {
        "ubuntu", "linux", "grub", "fedora", "debian", "opensuse", "suse", "manjaro", "arch", "mint", "kali", "pop!_os", "pop_os",
        "centos", "red hat", "redhat", "rhel", "elementary", "zorin", "deepin", "endeavour", "garuda", "mx linux", "rocky",
        "alma", "gentoo", "slackware", "solus", "void", "nixos", "refind", "systemd-boot", "shim", "kubuntu", "xubuntu", "lubuntu", "parrot"
    };

    private static readonly string[] FirmwareDeviceKeywords =
    {
        "usb", "network", "pxe", "ipv4", "ipv6", "lan", "cd", "dvd", "hard drive", "hdd", "ssd", "nvme", "sata", "onboard",
        "uefi:", "efi network", "efi usb", "efi dvd", "removable", "floppy", "internal shell", "efi shell", "diskette", "m.2"
    };

    public static bool ContainsLinuxKeyword(string text) => ContainsWord(text, LinuxKeywords);

    private static bool ContainsWord(string text, IEnumerable<string> words)
    {
        string t = (text ?? string.Empty).ToLowerInvariant();
        return words.Any(w => Regex.IsMatch(t, $@"(?<![a-z0-9]){Regex.Escape(w)}(?![a-z])"));
    }

    // ------------------------------ Parsing ------------------------------

    public static List<BcdSection> ParseBcdeditOutput(string text)
    {
        var sections = new List<BcdSection>();
        var lines = text.Replace("\r", "").Split('\n');
        BcdSection? cur = null;
        int lastIdx = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string next = i + 1 < lines.Length ? lines[i + 1] : string.Empty;

            if (!string.IsNullOrWhiteSpace(line) && Regex.IsMatch(next.Trim(), @"^-{3,}$"))
            {
                cur = new BcdSection { Title = line.Trim() };
                sections.Add(cur);
                lastIdx = -1;
                i++; // skip dashes
                continue;
            }

            if (cur == null || string.IsNullOrWhiteSpace(line))
                continue;

            if (char.IsWhiteSpace(line[0]) && lastIdx >= 0)
            {
                cur.Items[lastIdx].Values.Add(line.Trim());
                continue;
            }

            var m = Regex.Match(line, @"^(?<k>\S.*?)\s{2,}(?<v>\S.*)$");
            if (m.Success)
            {
                cur.Items.Add((m.Groups["k"].Value.Trim(), new List<string> { m.Groups["v"].Value.Trim() }));
                lastIdx = cur.Items.Count - 1;
            }
        }
        return sections;
    }

    private static List<string> FindDisplayOrder(BcdSection s)
    {
        var known = s.GetAll(DisplayOrderKeys);
        if (known.Count > 0) return known;
        // Localized fallback: the element (other than identifier) holding the most GUIDs
        return s.Items.Skip(1)
            .Where(it => it.Values.Count > 0 && it.Values.All(v => GuidRegex.IsMatch(v)))
            .OrderByDescending(it => it.Values.Count)
            .Select(it => it.Values)
            .FirstOrDefault() ?? new List<string>();
    }

    private static int FindTimeout(BcdSection s)
    {
        string t = s.Get(TimeoutKeys);
        if (int.TryParse(t, out int v)) return v;
        foreach (var (_, vals) in s.Items.Skip(1))
            if (vals.Count == 1 && int.TryParse(vals[0], out int n) && n >= 0 && n <= 999) return n;
        return -1;
    }

    // ------------------------------ Scan ------------------------------

    public async Task<BootScanResult> ScanAsync(string? espRoot, CancellationToken ct = default)
    {
        var result = new BootScanResult();

        var cur = await ProcessRunner.RunAsync("bcdedit.exe", "/enum {current} /v", null, ct);
        if (!cur.Success)
        {
            result.Error = cur.Combined.Contains("denied", StringComparison.OrdinalIgnoreCase) || cur.ExitCode != 0
                ? "bcdedit needs Administrator rights — click 'Restart as Admin'. (" + cur.Combined.Trim() + ")"
                : cur.Combined;
            return result;
        }
        result.CurrentId = ParseBcdeditOutput(cur.StdOut).FirstOrDefault()?.Identifier ?? string.Empty;

        var bm = await ProcessRunner.RunAsync("bcdedit.exe", "/enum {bootmgr} /v", null, ct);
        var bmSec = ParseBcdeditOutput(bm.StdOut).FirstOrDefault();
        var winMenuOrder = new List<string>();
        if (bmSec != null)
        {
            winMenuOrder = FindDisplayOrder(bmSec);
            result.DefaultId = bmSec.Get(DefaultKeys);
            result.TimeoutSeconds = FindTimeout(bmSec);
            string bmPath = bmSec.Get(PathKeys);
            result.IsUefi = bmPath.EndsWith(".efi", StringComparison.OrdinalIgnoreCase);
        }

        var os = await ProcessRunner.RunAsync("bcdedit.exe", "/enum osloader /v", null, ct);
        foreach (var s in ParseBcdeditOutput(os.StdOut))
        {
            if (!GuidRegex.IsMatch(s.Identifier)) continue;
            var e = BuildEntry(s, isFirmware: false, result.CurrentId, result.DefaultId, winMenuOrder, null);
            result.Entries.Add(e);
        }

        var fw = await ProcessRunner.RunAsync("bcdedit.exe", "/enum firmware /v", null, ct);
        if (fw.Success)
        {
            var fwSecs = ParseBcdeditOutput(fw.StdOut);
            var fwMgr = fwSecs.FirstOrDefault(s => s.Identifier.Equals(FwBootMgrGuid, StringComparison.OrdinalIgnoreCase));
            var fwOrder = fwMgr != null ? FindDisplayOrder(fwMgr) : new List<string>();
            foreach (var s in fwSecs)
            {
                if (!GuidRegex.IsMatch(s.Identifier) || s.Identifier.Equals(FwBootMgrGuid, StringComparison.OrdinalIgnoreCase)) continue;
                result.Entries.Add(BuildEntry(s, isFirmware: true, result.CurrentId, result.DefaultId, fwOrder, espRoot));
            }
            result.IsUefi = result.IsUefi || fwSecs.Count > 0;
        }

        return result;
    }

    public static BootEntryItem BuildEntry(BcdSection s, bool isFirmware, string currentId, string defaultId, List<string> displayOrder, string? espRoot)
    {
        var e = new BootEntryItem
        {
            IsFirmware = isFirmware,
            Store = isFirmware ? "UEFI Firmware Menu" : "Windows Boot Menu",
            Identifier = s.Identifier,
            Description = s.Get(DescKeys),
            Device = s.Get(DeviceKeys),
            Path = s.Get(PathKeys),
            IsCurrent = !isFirmware && s.Identifier.Equals(currentId, StringComparison.OrdinalIgnoreCase),
            IsDefault = !isFirmware && s.Identifier.Equals(defaultId, StringComparison.OrdinalIgnoreCase),
            InMenu = displayOrder.Any(d => d.Equals(s.Identifier, StringComparison.OrdinalIgnoreCase))
        };
        Classify(e, espRoot);
        return e;
    }

    public static void Classify(BootEntryItem e, string? espRoot)
    {
        string text = $"{e.Description} {e.Path}".ToLowerInvariant();
        bool isWinBootMgr = e.Identifier.Equals(BootMgrGuid, StringComparison.OrdinalIgnoreCase) ||
                            e.Path.Contains("bootmgfw.efi", StringComparison.OrdinalIgnoreCase);
        bool isRecovery = e.Device.StartsWith("ramdisk", StringComparison.OrdinalIgnoreCase) ||
                          e.Description.Contains("Recovery", StringComparison.OrdinalIgnoreCase) ||
                          e.Path.Contains("winre", StringComparison.OrdinalIgnoreCase);

        if (isWinBootMgr)
            Set(e, "Windows Boot Manager", "#1D4ED8", true, "Starts Windows — deleting it makes the PC unbootable", "Never remove this entry.");
        else if (e.Identifier.Equals(MemDiagGuid, StringComparison.OrdinalIgnoreCase))
            Set(e, "Memory Diagnostic", "#475569", true, "Windows memory test tool", string.Empty);
        else if (e.IsCurrent)
            Set(e, "Windows (running now)", "#15803D", true, "This is the Windows you are using right now", string.Empty);
        else if (isRecovery)
            Set(e, "Windows Recovery", "#0E7490", true, "Windows Recovery Environment (repair/reset tools)", "Keep it — needed for 'Reset this PC' and startup repair.");
        else if (ContainsLinuxKeyword(text))
            Set(e, "Linux / Other OS (GRUB)", "#B45309", false, string.Empty,
                "If you already removed this OS, delete this entry AND its leftover EFI folder (EFI Leftovers list) so it doesn't come back.");
        else if (e.IsFirmware && string.IsNullOrWhiteSpace(e.Path) && ContainsWord(text, FirmwareDeviceKeywords))
            Set(e, "Hardware Boot Device (USB/Network/Disk)", "#6D28D9", false, string.Empty,
                "Created by the BIOS for USB/network/disk boot. Usually harmless; the BIOS may re-create it.");
        else if (!e.IsFirmware || text.Contains("windows"))
            Set(e, "Windows Installation", "#1E40AF", false, string.Empty, "Another Windows entry (e.g. old/duplicate install).");
        else
            Set(e, "Other / Unknown", "#6B531D", false, string.Empty, "Search Google with the entry name if unsure.");

        if (e.IsDefault && !e.IsProtected)
        {
            e.IsProtected = true;
            e.ProtectReason = "Default Windows entry — set another entry as default first";
        }

        // Status (stale / broken detection)
        e.Status = "OK"; e.StatusColor = "#15803D";
        if (!e.IsFirmware && !e.IsCurrent && !isRecovery)
        {
            var m = Regex.Match(e.Device, @"partition=([A-Za-z]):", RegexOptions.IgnoreCase);
            if (e.Device.Contains("unknown", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(e.Device))
                SetStatus(e, "Broken — target disk not found", "#B91C1C");
            else if (m.Success)
            {
                string root = m.Groups[1].Value.ToUpperInvariant() + ":\\";
                bool hasWin = File.Exists(Path.Combine(root, "Windows", "System32", "winload.efi")) ||
                              File.Exists(Path.Combine(root, "Windows", "System32", "winload.exe"));
                if (!hasWin) SetStatus(e, $"Broken — no Windows found on {root}", "#B91C1C");
            }
        }
        else if (e.IsFirmware && !string.IsNullOrWhiteSpace(e.Path) && !string.IsNullOrWhiteSpace(espRoot) && !isWinBootMgr)
        {
            string file = Path.Combine(espRoot, e.Path.TrimStart('\\'));
            if (!File.Exists(file))
                SetStatus(e, "Stale — boot file missing (OS removed?)", "#B91C1C");
        }

        if (!e.InMenu && e.Status == "OK")
            SetStatus(e, "Hidden from menu", "#6B531D");
    }

    private static void Set(BootEntryItem e, string kind, string color, bool prot, string reason, string advice)
    {
        e.Kind = kind; e.KindColor = color; e.IsProtected = prot; e.ProtectReason = reason; e.Advice = advice;
    }

    private static void SetStatus(BootEntryItem e, string status, string color)
    {
        e.Status = status; e.StatusColor = color;
    }

    // ------------------------------ Safety ------------------------------

    public enum BootAction { Delete, Hide, Show, SetDefault, BootFirst, Rename }

    public static (bool Allowed, string Reason) ValidateAction(BootEntryItem e, BootAction action)
    {
        if (!GuidRegex.IsMatch(e.Identifier) || e.Identifier.Equals(FwBootMgrGuid, StringComparison.OrdinalIgnoreCase))
            return (false, "Invalid or protected boot manager identifier.");

        bool isWinBootMgr = e.Identifier.Equals(BootMgrGuid, StringComparison.OrdinalIgnoreCase) ||
                            e.Path.Contains("bootmgfw.efi", StringComparison.OrdinalIgnoreCase);

        switch (action)
        {
            case BootAction.Delete:
                if (e.IsProtected) return (false, $"Blocked: {e.ProtectReason}.");
                return (true, string.Empty);
            case BootAction.Hide:
                if (isWinBootMgr || e.IsCurrent || e.IsDefault) return (false, "Blocked: Windows needs this entry in the menu.");
                return (true, string.Empty);
            case BootAction.Show:
                return (true, string.Empty);
            case BootAction.SetDefault:
                if (e.IsFirmware) return (false, "'Set Default' is for Windows Boot Menu entries. For UEFI entries use 'Boot First'.");
                if (e.Status.StartsWith("Broken")) return (false, "Blocked: this entry is broken — it would make Windows fail to start.");
                if (e.Kind == "Windows Recovery") return (false, "Recovery can't be the default entry.");
                return (true, string.Empty);
            case BootAction.BootFirst:
                if (!e.IsFirmware) return (false, "'Boot First' changes the UEFI firmware order — select a UEFI Firmware Menu entry.");
                if (e.Status.StartsWith("Stale")) return (false, "Blocked: the boot file for this entry is missing.");
                return (true, string.Empty);
            case BootAction.Rename:
                return (true, string.Empty);
        }
        return (false, "Unknown action.");
    }

    public static string SanitizeDescription(string text)
    {
        string clean = Regex.Replace(text ?? string.Empty, "[\"\\r\\n%^&|<>]", "").Trim();
        return clean.Length > 60 ? clean[..60] : clean;
    }

    // ------------------------------ Backup / restore ------------------------------

    public static string BackupFolder => Path.Combine(FormatHelper.GetPortableBaseDirectory(), "BCD_Backups");

    public async Task<(bool Ok, string File, string Output)> BackupAsync(Action<string>? log = null)
    {
        Directory.CreateDirectory(BackupFolder);
        string file = Path.Combine(BackupFolder, $"BCD_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.bcd");
        var r = await ProcessRunner.RunAsync("bcdedit.exe", $"/export \"{file}\"", log);
        bool ok = r.Success && File.Exists(file);
        AppLogger.LogInfo($"BCD backup {(ok ? "OK" : "FAILED")}: {file} {r.Combined.Trim()}");
        return (ok, file, r.Combined.Trim());
    }

    public Task<ProcessResult> RestoreAsync(string file, Action<string>? log = null) =>
        ProcessRunner.RunAsync("bcdedit.exe", $"/import \"{file}\"", log);

    // ------------------------------ Actions ------------------------------

    public static string BuildCommandArgs(BootEntryItem e, BootAction action, string newName = "")
    {
        string id = e.Identifier;
        return action switch
        {
            BootAction.Delete => $"/delete {id} /cleanup",
            BootAction.Hide => e.IsFirmware ? $"/set {FwBootMgrGuid} displayorder {id} /remove" : $"/displayorder {id} /remove",
            BootAction.Show => e.IsFirmware ? $"/set {FwBootMgrGuid} displayorder {id} /addlast" : $"/displayorder {id} /addlast",
            BootAction.SetDefault => $"/default {id}",
            BootAction.BootFirst => $"/set {FwBootMgrGuid} displayorder {id} /addfirst",
            BootAction.Rename => $"/set {id} description \"{SanitizeDescription(newName)}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    /// <summary>Validates, auto-backs-up the BCD store, then runs the change.</summary>
    public async Task<(bool Ok, string Message)> ApplyActionAsync(BootEntryItem e, BootAction action, Action<string> log, string newName = "")
    {
        var (allowed, reason) = ValidateAction(e, action);
        if (!allowed) return (false, reason);
        if (action == BootAction.Rename && string.IsNullOrWhiteSpace(SanitizeDescription(newName)))
            return (false, "Please enter a valid name.");

        var (bok, bfile, bout) = await BackupAsync(log);
        if (!bok) return (false, $"Change cancelled — automatic BCD backup failed: {bout}");
        log($"Backup saved: {bfile}");

        string args = BuildCommandArgs(e, action, newName);
        log($"> bcdedit {args}");
        var r = await ProcessRunner.RunAsync("bcdedit.exe", args, log);
        AppLogger.LogInfo($"Boot action {action} on {e.Identifier} '{e.Description}': exit={r.ExitCode} {r.Combined.Trim()}");
        return (r.Success, r.Success ? $"{action} completed for '{e.DisplayName}'." : $"bcdedit failed: {r.Combined.Trim()}");
    }

    public async Task<(bool Ok, string Message)> SetTimeoutAsync(int seconds, Action<string> log)
    {
        seconds = Math.Clamp(seconds, 0, 30);
        var (bok, _, bout) = await BackupAsync(log);
        if (!bok) return (false, $"Cancelled — backup failed: {bout}");
        var r = await ProcessRunner.RunAsync("bcdedit.exe", $"/timeout {seconds}", log);
        return (r.Success, r.Success ? $"Boot menu timeout set to {seconds} seconds." : r.Combined.Trim());
    }

    /// <summary>Adds a new Windows boot entry by copying the current one (optionally as Safe Mode).</summary>
    public async Task<(bool Ok, string Message)> AddWindowsEntryAsync(string name, string? safeBoot, Action<string> log)
    {
        string desc = SanitizeDescription(name);
        if (string.IsNullOrWhiteSpace(desc)) return (false, "Please enter a name.");
        var (bok, _, bout) = await BackupAsync(log);
        if (!bok) return (false, $"Cancelled — backup failed: {bout}");

        var r = await ProcessRunner.RunAsync("bcdedit.exe", $"/copy {{current}} /d \"{desc}\"", log);
        var m = AnyGuidRegex.Match(r.StdOut);
        if (!r.Success || !m.Success) return (false, $"Could not create entry: {r.Combined.Trim()}");
        string newId = m.Value;

        if (!string.IsNullOrEmpty(safeBoot))
        {
            var s = await ProcessRunner.RunAsync("bcdedit.exe", $"/set {newId} safeboot {safeBoot}", log);
            if (!s.Success) return (false, $"Entry created ({newId}) but Safe Mode flag failed: {s.Combined.Trim()}");
        }
        await ProcessRunner.RunAsync("bcdedit.exe", $"/displayorder {newId} /addlast", log);
        AppLogger.LogInfo($"Added boot entry '{desc}' {newId} safeboot={safeBoot}");
        return (true, $"Added boot entry '{desc}'. It will appear in the Windows boot menu at next restart.");
    }

    // ------------------------------ EFI System Partition ------------------------------

    public static readonly HashSet<string> ProtectedEfiFolders = new(StringComparer.OrdinalIgnoreCase) { "Microsoft", "Boot" };

    /// <summary>Temporarily mounts the EFI System Partition to a free drive letter (mountvol X: /S) and unmounts on dispose.</summary>
    public sealed class EspMount : IAsyncDisposable
    {
        public string Root { get; private set; } = string.Empty;
        private string _letter = string.Empty;

        public static async Task<(EspMount? Mount, string Error)> MountAsync()
        {
            var used = DriveInfo.GetDrives().Select(d => d.Name[..1].ToUpperInvariant()).ToHashSet();
            string? free = "ZYXWVUTSRQPON".Select(c => c.ToString()).FirstOrDefault(c => !used.Contains(c));
            if (free == null) return (null, "No free drive letter to mount the EFI partition.");

            var r = await ProcessRunner.RunAsync("mountvol.exe", $"{free}: /S");
            string root = $"{free}:\\";
            if (!Directory.Exists(root))
                return (null, $"Could not mount the EFI partition (needs Admin, UEFI PC): {r.Combined.Trim()}");
            return (new EspMount { Root = root, _letter = free }, string.Empty);
        }

        public async ValueTask DisposeAsync()
        {
            if (!string.IsNullOrEmpty(_letter))
                await ProcessRunner.RunAsync("mountvol.exe", $"{_letter}: /D");
        }
    }

    public static List<EfiFolderItem> ListEfiFolders(string espRoot, IEnumerable<BootEntryItem> entries)
    {
        var list = new List<EfiFolderItem>();
        string efiDir = Path.Combine(espRoot, "EFI");
        if (!Directory.Exists(efiDir)) return list;

        foreach (var dir in Directory.GetDirectories(efiDir))
        {
            string name = Path.GetFileName(dir);
            var item = new EfiFolderItem { Name = name, SizeBytes = FolderSize(dir) };
            string lower = name.ToLowerInvariant();
            var refs = entries.Where(e => e.IsFirmware && e.Path.Contains($"\\{name}\\", StringComparison.OrdinalIgnoreCase)).Select(e => e.DisplayName).ToList();
            item.ReferencedBy = refs.Count == 0 ? "No boot entry uses it" : "Used by: " + string.Join(", ", refs);

            if (ProtectedEfiFolders.Contains(name))
            {
                item.IsProtected = true;
                item.Kind = name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ? "Windows Boot Files" : "Fallback Boot Loader";
                item.KindColor = "#1D4ED8";
                item.Note = "🔒 Required to start Windows — locked.";
            }
            else if (ContainsLinuxKeyword(lower))
            {
                item.Kind = "Linux / GRUB leftover";
                item.KindColor = "#B45309";
                item.Note = "Leftover from a Linux install. Safe to delete if that Linux is no longer installed on ANY disk.";
                item.IsSelected = refs.Count == 0;
            }
            else
            {
                item.Kind = "OEM / Other";
                item.KindColor = "#6B531D";
                item.Note = "May be a laptop vendor tool (HP/Dell/Lenovo diagnostics). Delete only if you know what it is.";
            }
            list.Add(item);
        }
        return list.OrderBy(i => i.IsProtected).ThenBy(i => i.Name).ToList();
    }

    public static (bool Ok, string Message) BackupAndDeleteEfiFolder(string espRoot, EfiFolderItem item)
    {
        if (item.IsProtected || ProtectedEfiFolders.Contains(item.Name) || item.Name.Contains("..") || item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return (false, $"Blocked: '{item.Name}' is protected.");

        string src = Path.Combine(espRoot, "EFI", item.Name);
        if (!Directory.Exists(src)) return (false, "Folder not found.");

        string backup = Path.Combine(FormatHelper.GetPortableBaseDirectory(), "EFI_Backups", $"{item.Name}_{DateTime.Now:yyyyMMdd_HHmmss}");
        try
        {
            CopyDirectory(src, backup);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(src, recursive: true);
            AppLogger.LogInfo($"Deleted EFI folder \\EFI\\{item.Name} (backup: {backup})");
            return (true, $"Deleted \\EFI\\{item.Name} (backup saved to {backup}).");
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"EFI folder delete failed: {item.Name}", ex);
            return (false, $"Failed: {ex.Message}");
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private static long FolderSize(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }
}
