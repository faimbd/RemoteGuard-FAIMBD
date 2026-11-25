namespace WinFixCleanPortable.Models;

public class InstalledAppItem : ObservableObject
{
    public string DisplayName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string InstallDate { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;
    public string QuietUninstallString { get; set; } = string.Empty;
    public string RegistryHive { get; set; } = string.Empty; // "HKLM64", "HKLM32", "HKCU"
    public string RegistryKeyPath { get; set; } = string.Empty;
    public string ScopeLabel { get; set; } = "Machine";
    public string ScopeColor { get; set; } = "#3B82F6";
    public long EstimatedSizeBytes { get; set; }
    public string SizeFormatted => EstimatedSizeBytes > 0 ? Helpers.FormatHelper.FormatBytes(EstimatedSizeBytes) : "—";
    public bool IsWindowsInstaller { get; set; }

    // Browser web-app (PWA) safety classification
    public bool IsWebApp { get; set; }              // e.g. YouTube / Gmail installed through Chrome/Edge
    public bool IsBrowserCore { get; set; }         // the actual Google Chrome / Edge / Brave browser itself
    public string HostBrowserName { get; set; } = string.Empty;
    public string WebAppId { get; set; } = string.Empty;
    public int RelatedWebAppCount { get; set; }     // for browser core: number of web apps that depend on it

    // Hover info popup ("what does this app do?")
    public string InfoCategory { get; set; } = "Application";
    public string InfoDescription { get; set; } = string.Empty;
    public string InfoAdvice { get; set; } = string.Empty;
    public string InfoAdviceLevel { get; set; } = "CHECK";     // KEEP / CHECK / SAFE
    public string InfoAdviceColor { get; set; } = "#D4AF37";
    public string InfoAdviceLabel { get; set; } = "CHECK before removing";
    public string InfoExtraNote { get; set; } = string.Empty;  // e.g. "Chrome itself will NOT be removed"
    public bool HasExtraNote => !string.IsNullOrWhiteSpace(InfoExtraNote);
    public string InfoFooter => $"{Publisher}  •  v{DisplayVersion}  •  {SizeFormatted}  •  {ScopeLabel}";

    public string GoogleWhatIsUrl =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString($"what is \"{DisplayName}\" {Publisher} windows app");
    public string GoogleSafeUninstallUrl =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString($"is it safe to uninstall \"{DisplayName}\" what happens if I uninstall it");
}

public class LeftoverItem : ObservableObject
{
    private bool _isSelected = true;
    private string _status = "Found";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Kind { get; set; } = "Folder"; // "Folder", "File", "Registry Key"
    public string BadgeColor { get; set; } = "#F59E0B";
    public string LocationCategory { get; set; } = string.Empty; // "Install Dir", "AppData", "LocalAppData", "ProgramData", "Registry (HKCU)", "Registry (HKLM)"
    public string TargetPath { get; set; } = string.Empty;
    public string RegistryHive { get; set; } = string.Empty; // "HKCU", "HKLM64", "HKLM32"
    public long SizeBytes { get; set; }
    public string SizeFormatted => Kind == "Registry Key" ? "Registry" : Helpers.FormatHelper.FormatBytes(SizeBytes);

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}
