using System.Diagnostics;
using System.IO;
using WinFixCleanPortable.Helpers;

namespace WinFixCleanPortable.Services;

public class FileImpactAnalysis
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string SizeFormatted => FormatHelper.FormatBytes(SizeBytes);
    public string RiskLevel { get; set; } = "CAUTION"; // "SAFE", "CAUTION", "HIGH RISK"
    public string RiskBadgeColor { get; set; } = "#D97706";
    public string RiskTitle { get; set; } = string.Empty;
    public string WhatIsThisFile { get; set; } = string.Empty;
    public string WhatHappensIfDeleted { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public string GoogleWhatIsUrl { get; set; } = string.Empty;
    public string GoogleDeleteImpactUrl { get; set; } = string.Empty;
}

public static class FileImpactService
{
    private static readonly HashSet<string> CriticalSystemExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sys", ".dll", ".drv", ".ocx", ".cpl", ".efi", ".mui", ".cat", ".inf", ".winmd"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".zst"
    };

    private static readonly HashSet<string> DiskImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".img", ".dmg", ".nrg", ".mdf", ".bin", ".cue"
    };

    private static readonly HashSet<string> VirtualDiskExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vhd", ".vhdx", ".vmdk", ".vdi", ".ova", ".ovf"
    };

    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".msi", ".msix", ".msp", ".exe"
    };

    private static readonly HashSet<string> TempLogExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".log", ".bak", ".old", ".dmp", ".chk", ".crdownload", ".part"
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mp3", ".wav", ".flac", ".jpg", ".jpeg", ".png", ".webp", ".heic", ".raw"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".psd", ".ai", ".fig", ".blend", ".prproj"
    };

    public static FileImpactAnalysis Analyze(string fullPath, long sizeBytes = -1)
    {
        string fileName = Path.GetFileName(fullPath);
        string ext = Path.GetExtension(fullPath);
        string dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        string upperPath = fullPath.ToUpperInvariant();

        if (sizeBytes < 0 && File.Exists(fullPath))
        {
            try { sizeBytes = new FileInfo(fullPath).Length; } catch { sizeBytes = 0; }
        }
        sizeBytes = Math.Max(0, sizeBytes);

        string qWhat = Uri.EscapeDataString($"what is \"{fileName}\" file windows");
        string qSafe = Uri.EscapeDataString($"is it safe to delete \"{fileName}\" what happens if deleted windows");

        var result = new FileImpactAnalysis
        {
            FileName = fileName,
            FullPath = fullPath,
            Extension = string.IsNullOrEmpty(ext) ? "(none)" : ext.ToUpperInvariant(),
            SizeBytes = sizeBytes,
            GoogleWhatIsUrl = $"https://www.google.com/search?q={qWhat}",
            GoogleDeleteImpactUrl = $"https://www.google.com/search?q={qSafe}"
        };

        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToUpperInvariant();
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToUpperInvariant();
        string progFiles86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).ToUpperInvariant();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ToUpperInvariant();
        string downloads = Path.Combine(userProfile, "DOWNLOADS");
        string tempPath = Path.GetTempPath().ToUpperInvariant();

        bool inWindows = !string.IsNullOrEmpty(winDir) && upperPath.StartsWith(winDir);
        bool inProgramFiles = (!string.IsNullOrEmpty(progFiles) && upperPath.StartsWith(progFiles)) ||
                              (!string.IsNullOrEmpty(progFiles86) && upperPath.StartsWith(progFiles86));
        bool inTemp = upperPath.StartsWith(tempPath) || upperPath.Contains(@"\TEMP\") || upperPath.Contains(@"\CACHE\");
        bool inDownloads = upperPath.StartsWith(downloads);
        bool inAppData = upperPath.Contains(@"\APPDATA\") || upperPath.Contains(@"\PROGRAMDATA\");

        // 1. Critical OS files
        if (fileName.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase) ||
            (inWindows && !inTemp))
        {
            result.RiskLevel = "HIGH RISK";
            result.RiskBadgeColor = "#DC2626";
            result.RiskTitle = "HIGH RISK — Protected Windows System File";
            result.WhatIsThisFile = $"'{fileName}' is located inside the Windows OS folder ({dir}). Windows relies on files here to boot and run properly.";
            result.WhatHappensIfDeleted = "Deleting this file can cause Windows crashes (BSOD), missing DLL errors, broken features, or boot failure.";
            result.Recommendation = "DO NOT delete this file manually. Click 'Google Delete Impact' below to verify before touching it.";
            return result;
        }

        // 2. Program Files binaries
        if (inProgramFiles)
        {
            result.RiskLevel = "HIGH RISK";
            result.RiskBadgeColor = "#DC2626";
            result.RiskTitle = "HIGH RISK — Installed Application File";
            result.WhatIsThisFile = $"'{fileName}' belongs to an installed program inside Program Files.";
            result.WhatHappensIfDeleted = "Deleting individual files inside Program Files will break or corrupt that installed program instead of uninstalling it properly.";
            result.Recommendation = "Use the 'App Uninstaller' tab to cleanly remove the whole program instead of deleting this single file.";
            return result;
        }

        // 3. Temp / Crash dump / Incomplete downloads -> SAFE!
        if (inTemp || TempLogExtensions.Contains(ext))
        {
            result.RiskLevel = "SAFE";
            result.RiskBadgeColor = "#10B981";
            result.RiskTitle = "SAFE TO DELETE — Temporary / Log / Cache File";
            result.WhatIsThisFile = $"'{fileName}' is a temporary, crash dump, log, or incomplete download file ({result.Extension}).";
            result.WhatHappensIfDeleted = $"Nothing important will break. Deleting it immediately frees {result.SizeFormatted} of disk space; Windows or apps recreate needed caches automatically.";
            result.Recommendation = "Safe to move to Recycle Bin or delete permanently.";
            return result;
        }

        // 4. ISO / Disk Images
        if (DiskImageExtensions.Contains(ext))
        {
            result.RiskLevel = "SAFE";
            result.RiskBadgeColor = "#10B981";
            result.RiskTitle = "USUALLY SAFE — Disk Image / OS / Game ISO";
            result.WhatIsThisFile = $"'{fileName}' is a disc image ({result.Extension}, {result.SizeFormatted}) typically used to install Windows, Linux, software, or games.";
            result.WhatHappensIfDeleted = "Already-installed software or Windows will continue working 100% normally. You will only lose the installer image itself (you would have to re-download it if you need to reinstall later).";
            result.Recommendation = "If you already finished installing from this ISO, it is safe to delete and reclaim huge disk space.";
            return result;
        }

        // 5. Installers in Downloads / user folders
        if (InstallerExtensions.Contains(ext) && (inDownloads || !inAppData))
        {
            result.RiskLevel = "SAFE";
            result.RiskBadgeColor = "#10B981";
            result.RiskTitle = "USUALLY SAFE — Setup / Installer Package";
            result.WhatIsThisFile = $"'{fileName}' is an executable installer ({result.Extension}) located outside Program Files.";
            result.WhatHappensIfDeleted = "If the app is already installed on your PC, deleting the downloaded setup file does NOT uninstall or affect the installed app. (Note: if this is a portable app you run directly, deleting it removes that portable tool.)";
            result.Recommendation = "Safe to delete if it's an old setup installer you no longer need.";
            return result;
        }

        // 6. ZIP / RAR / 7Z Archives
        if (ArchiveExtensions.Contains(ext))
        {
            bool safeHint = inDownloads;
            result.RiskLevel = safeHint ? "SAFE" : "CAUTION";
            result.RiskBadgeColor = safeHint ? "#10B981" : "#D97706";
            result.RiskTitle = safeHint ? "USUALLY SAFE — Downloaded Archive (ZIP/RAR/7Z)" : "CAUTION — Compressed Archive / Backup";
            result.WhatIsThisFile = $"'{fileName}' is a compressed archive ({result.Extension}, {result.SizeFormatted}) containing bundled files or backups.";
            result.WhatHappensIfDeleted = "Deleting this archive will NOT affect Windows. However, if you haven't extracted important personal files/backups inside it, those archived contents will be removed.";
            result.Recommendation = "Double-check you already extracted what you need (or move to Recycle Bin first so you can restore it if needed).";
            return result;
        }

        // 7. Virtual machine disks
        if (VirtualDiskExtensions.Contains(ext))
        {
            result.RiskLevel = "CAUTION";
            result.RiskBadgeColor = "#D97706";
            result.RiskTitle = "CAUTION — Virtual Machine / WSL Disk Image";
            result.WhatIsThisFile = $"'{fileName}' is a virtual hard disk ({result.Extension}, {result.SizeFormatted}) used by Hyper-V, WSL2, VirtualBox, or VMware.";
            result.WhatHappensIfDeleted = "Deleting this permanently destroys the virtual machine or Linux/Docker container filesystem stored inside it.";
            result.Recommendation = "Only delete if you no longer use that VM / WSL distribution. Click 'Google Delete Impact' to verify.";
            return result;
        }

        // 8. Critical DLL/SYS outside Windows
        if (CriticalSystemExtensions.Contains(ext) || inAppData)
        {
            result.RiskLevel = "CAUTION";
            result.RiskBadgeColor = "#D97706";
            result.RiskTitle = "CAUTION — App Data or Runtime Library";
            result.WhatIsThisFile = $"'{fileName}' appears to be application configuration, saved data, or a library file ({result.Extension}).";
            result.WhatHappensIfDeleted = "Deleting it may reset an application's settings, saved logins/profiles, or stop a specific app from opening.";
            result.Recommendation = "Search Google using the button below to confirm which app owns this file before deleting.";
            return result;
        }

        // 9. Personal Media / Documents
        if (MediaExtensions.Contains(ext) || DocumentExtensions.Contains(ext))
        {
            result.RiskLevel = "CAUTION";
            result.RiskBadgeColor = "#D97706";
            result.RiskTitle = "PERSONAL FILE — Media or Document";
            result.WhatIsThisFile = $"'{fileName}' is a personal media or document file ({result.Extension}, {result.SizeFormatted}).";
            result.WhatHappensIfDeleted = "Windows and installed apps will NOT be harmed at all, but this personal video/photo/document will be removed (keep a backup if it's important to you!).";
            result.Recommendation = "Safe for Windows; delete only if you no longer want this personal file (Recycle Bin recommended).";
            return result;
        }

        // Default fallback
        result.RiskLevel = "CAUTION";
        result.RiskBadgeColor = "#D97706";
        result.RiskTitle = "CHECK BEFORE DELETING — General File";
        result.WhatIsThisFile = $"'{fileName}' ({result.Extension}, {result.SizeFormatted}) stored in '{dir}'.";
        result.WhatHappensIfDeleted = "If this belongs to a project, game, or software folder, removing it may affect that project/app. If it is an old download or unused file, deleting it safely frees space.";
        result.Recommendation = "Use the 'Google Delete Impact Summary' button below to check online what this exact file does before deleting.";
        return result;
    }

    public static void OpenUrlInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
