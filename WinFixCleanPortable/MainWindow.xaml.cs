using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WinFixCleanPortable.Helpers;
using WinFixCleanPortable.Models;
using WinFixCleanPortable.Services;
using WinFixCleanPortable.Views;

using WpfMessageBox = System.Windows.MessageBox;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfColor = System.Windows.Media.Color;
using WpfButton = System.Windows.Controls.Button;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using WpfClipboard = System.Windows.Clipboard;
using WinForms = System.Windows.Forms;

namespace WinFixCleanPortable;

public partial class MainWindow : Window
{
    private readonly WindowsRepairService _repairService = new();
    private readonly LargeArchiveScannerService _largeScanner = new();
    private readonly DuplicateFinderService _dupService = new();
    private readonly JunkCleanerService _junkService = new();
    private readonly SmartFastCopyService _fastCopyService = new();
    private readonly AppUninstallerService _uninstallerService = new();
    private readonly DriveRepairService _driveService = new();
    private readonly BootManagerService _bootService = new();
    private List<DriveRow> _driveRows = new();
    private List<BootEntryItem> _bootEntries = new();
    private List<EfiFolderItem> _efiFolders = new();
    private bool _drivesScannedOnce;
    private bool _bootScannedOnce;

    private readonly ObservableCollection<DriveSummaryItem> _drives = new();
    private readonly ObservableCollection<string> _largeCustomFolders = new();
    private readonly ObservableCollection<string> _dupFolders = new();
    private readonly ObservableCollection<string> _copySources = new();
    private readonly ObservableCollection<JunkCategoryItem> _junkCategories = new();
    private readonly ObservableCollection<MissingFileReportItem> _missingFiles = new();
    private readonly ObservableCollection<LeftoverItem> _leftoverItems = new();

    private List<InstalledAppItem> _allInstalledApps = new();
    private bool _appsLoadedOnce;
    private InstalledAppItem? _lastScannedLeftoverApp;

    private List<ScannedFileItem> _allLargeResults = new();
    private List<ScannedFileItem> _visibleLargeResults = new();
    private string _activeLargeChip = "ALL";

    private List<CopyPlanItem> _allCopyPlan = new();
    private string _activeCopyChip = "ALL";
    private string _lastAnalyzedSignature = string.Empty;

    private ObservableCollection<DuplicateFileItem> _duplicateItems = new();
    private int _dupGroupCount;
    private long _dupWastedBytes;

    private CancellationTokenSource? _repairCts;
    private CancellationTokenSource? _largeCts;
    private CancellationTokenSource? _dupCts;
    private CancellationTokenSource? _copyCts;
    private bool _isAdmin;

    public MainWindow()
    {
        InitializeComponent();
        InitializeAppState();
        AppLogger.LogInfo("MainWindow initialized and ready.");
        if (App.IsSelfTest)
            Loaded += async (_, _) => await RunSelfTestAsync();
    }

    /// <summary>
    /// Hidden "--selftest" diagnostic: renders every page, the uninstaller hover popup and both dialogs,
    /// then logs PASS/FAIL and exits. Used to verify releases never crash on startup/navigation.
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        int failures = 0;
        void Check(bool ok, string name)
        {
            AppLogger.LogInfo($"SELFTEST {(ok ? "PASS" : "FAIL")}: {name}");
            if (!ok) failures++;
        }

        try
        {
            foreach (var nav in new[] { NavRepair, NavLargeFiles, NavDuplicates, NavFastCopy, NavJunk, NavDriveFix, NavBoot, NavUninstaller })
            {
                nav.IsChecked = true;
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Check(true, $"Page rendered: {nav.Tag}");
            }

            await LoadInstalledAppsAsync();
            UpdateLayout();
            Check(_allInstalledApps.Count > 0, $"Installed apps loaded: {_allInstalledApps.Count}");
            Check(_allInstalledApps.All(a => !string.IsNullOrWhiteSpace(a.InfoDescription)), "Every app has hover info description");

            int webApps = _allInstalledApps.Count(a => a.IsWebApp);
            int cores = _allInstalledApps.Count(a => a.IsBrowserCore);
            AppLogger.LogInfo($"SELFTEST INFO: Browser web apps detected = {webApps}, browser cores = {cores}");
            foreach (var w in _allInstalledApps.Where(a => a.IsWebApp).Take(10))
                AppLogger.LogInfo($"SELFTEST INFO:   WebApp '{w.DisplayName}' host={w.HostBrowserName} id={w.WebAppId} safeCmd={AppUninstallerService.ValidateUninstallCommandSafety(w, w.UninstallString).Allowed}");
            foreach (var c in _allInstalledApps.Where(a => a.IsBrowserCore))
                AppLogger.LogInfo($"SELFTEST INFO:   BrowserCore '{c.DisplayName}' dependentWebApps={c.RelatedWebAppCount}");

            // Hover popup: instantiate the per-row tooltip with real data and measure it
            var tipOk = true;
            foreach (var app in _allInstalledApps.Take(25))
            {
                if (InstalledAppsGrid.FindResource("AppInfoTip") is FrameworkElement tip)
                {
                    tip.DataContext = app;
                    tip.Measure(new System.Windows.Size(460, double.PositiveInfinity));
                    tipOk &= tip.DesiredSize.Height > 20;
                }
                else tipOk = false;
            }
            Check(tipOk, "Hover info popup renders for apps");

            InstalledAppsGrid.ScrollIntoView(_allInstalledApps.First());
            InstalledAppsGrid.UpdateLayout();
            var row = InstalledAppsGrid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
            Check(row?.ToolTip is FrameworkElement, "Grid row has hover tooltip attached");

            var sample = _allInstalledApps.FirstOrDefault(a => a.IsWebApp) ?? _allInstalledApps.First();
            var infoDlg = new AppInfoDialog(sample) { Owner = this, ShowActivated = false };
            infoDlg.Show(); infoDlg.UpdateLayout(); infoDlg.Close();
            Check(true, $"AppInfoDialog rendered for '{sample.DisplayName}'");

            var impact = FileImpactService.Analyze(Environment.ProcessPath ?? "C:\\test.iso", 1024);
            var impactDlg = new FileImpactDialog(impact) { Owner = this, ShowActivated = false };
            impactDlg.Show(); impactDlg.UpdateLayout(); impactDlg.Close();
            Check(true, "FileImpactDialog rendered");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("SELFTEST exception", ex);
            failures++;
        }

        AppLogger.LogInfo($"SELFTEST RESULT: {(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)")}");
        System.Windows.Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }

    private void InitializeAppState()
    {
        _isAdmin = FormatHelper.IsRunningAsAdministrator();
        WindowsVersionText.Text = FormatHelper.GetWindowsFriendlyName();

        if (_isAdmin)
        {
            AdminStatusText.Text = "Administrator Mode";
            AdminDot.Fill = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22C55E"));
            SidebarElevateBtn.Visibility = Visibility.Collapsed;
            RepairAdminBanner.Visibility = Visibility.Collapsed;
            DriveAdminBanner.Visibility = Visibility.Collapsed;
            BootAdminBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminStatusText.Text = "Standard Mode";
            AdminDot.Fill = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#F59E0B"));
            SidebarElevateBtn.Visibility = Visibility.Visible;
            RepairAdminBanner.Visibility = Visibility.Visible;
        }

        DashboardDrivesList.ItemsSource = _drives;
        LargeDrivesItems.ItemsSource = _drives;
        DupFoldersList.ItemsSource = _dupFolders;
        CopySourcesList.ItemsSource = _copySources;
        MissingFilesGrid.ItemsSource = _missingFiles;
        LeftoversGrid.ItemsSource = _leftoverItems;

        foreach (var cat in _junkService.BuildDefaultCategories())
            _junkCategories.Add(cat);
        JunkGrid.ItemsSource = _junkCategories;

        RefreshDrives();
        HighlightActiveChip("ALL");
        HighlightCopyChip("ALL");
    }

    private void RefreshDrives()
    {
        var previousSelection = _drives.Where(d => d.IsSelected).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _drives.Clear();

        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                if (!di.IsReady)
                    continue;
                if (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable)
                    continue;

                bool selected = previousSelection.Count > 0
                    ? previousSelection.Contains(di.Name)
                    : true;

                _drives.Add(new DriveSummaryItem
                {
                    Name = di.Name,
                    VolumeLabel = di.VolumeLabel,
                    DriveFormat = di.DriveFormat,
                    TotalBytes = di.TotalSize,
                    FreeBytes = di.AvailableFreeSpace,
                    IsSelected = selected
                });
            }
        }
        catch { }
    }

    // ==================== NAVIGATION ====================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageDashboard == null) return;
        if (sender is not WpfRadioButton rb || rb.Tag is not string page) return;

        PageDashboard.Visibility = Visibility.Collapsed;
        PageRepair.Visibility = Visibility.Collapsed;
        PageLargeFiles.Visibility = Visibility.Collapsed;
        PageDuplicates.Visibility = Visibility.Collapsed;
        PageFastCopy.Visibility = Visibility.Collapsed;
        PageUninstaller.Visibility = Visibility.Collapsed;
        PageDriveFix.Visibility = Visibility.Collapsed;
        PageBoot.Visibility = Visibility.Collapsed;
        PageJunk.Visibility = Visibility.Collapsed;

        switch (page)
        {
            case "Dashboard":
                PageDashboard.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Dashboard";
                HeaderSubtitle.Text = "System overview, drive storage, and one-click maintenance shortcuts";
                break;
            case "Repair":
                PageRepair.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Windows Repair & Missing File Checker";
                HeaderSubtitle.Text = "Detect missing/corrupted Windows system files (SFC, DISM, CBS.log, System32 DLLs) and repair automatically";
                break;
            case "LargeFiles":
                PageLargeFiles.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Large Files & ZIP / ISO Finder";
                HeaderSubtitle.Text = "Find huge files, ZIP/RAR/7Z archives, and ISO disk images sorted largest-first for easy cleanup";
                break;
            case "Duplicates":
                PageDuplicates.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Duplicate File Cleaner";
                HeaderSubtitle.Text = "Scan any folders you choose with 100% SHA-256 verification and smart auto-selection";
                break;
            case "FastCopy":
                PageFastCopy.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Smart Fast Copy (Multi-Threaded)";
                HeaderSubtitle.Text = "High-speed parallel copy/move with zero missing files, zero duplicates, and better-quality auto or manual selection";
                break;
            case "Uninstaller":
                PageUninstaller.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Clean App Uninstaller";
                HeaderSubtitle.Text = "Uninstall programs cleanly and remove leftover AppData folders, shortcuts, and Registry traces";
                if (!_appsLoadedOnce)
                    _ = LoadInstalledAppsAsync();
                break;
            case "DriveFix":
                PageDriveFix.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Drive Fix & Format";
                HeaderSubtitle.Text = "Fix pen drives / hard disks that don't show up (offline, read-only, no letter, RAW) and format them safely";
                if (!_drivesScannedOnce && _isAdmin && !App.IsSelfTest)
                    _ = ScanDrivesAsync();
                break;
            case "Boot":
                PageBoot.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Boot Menu Manager";
                HeaderSubtitle.Text = "Remove leftover boot entries (old Linux/Windows), add Safe Mode entries, and set default/timeout — with automatic backup";
                if (!_bootScannedOnce && _isAdmin && !App.IsSelfTest)
                    _ = ScanBootAsync();
                break;
            case "Junk":
                PageJunk.Visibility = Visibility.Visible;
                HeaderTitle.Text = "System Junk Cleaner";
                HeaderSubtitle.Text = "Safely inspect and clean Windows temporary files, crash dumps, update cache, and Recycle Bin";
                break;
        }
    }

    private void GoToRepair_Click(object sender, RoutedEventArgs e) => NavRepair.IsChecked = true;
    private void GoToLargeFiles_Click(object sender, RoutedEventArgs e) => NavLargeFiles.IsChecked = true;
    private void GoToDuplicates_Click(object sender, RoutedEventArgs e) => NavDuplicates.IsChecked = true;
    private void GoToFastCopy_Click(object sender, RoutedEventArgs e) => NavFastCopy.IsChecked = true;
    private void RefreshDrives_Click(object sender, RoutedEventArgs e)
    {
        RefreshDrives();
        AppLogger.LogInfo($"Drives refreshed: {_drives.Count} ready drive(s).");
    }

    private void OpenAppLog_Click(object sender, RoutedEventArgs e) => AppLogger.OpenLogInNotepad();

    private void DashboardScanDrive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is string driveName)
        {
            foreach (var d in _drives)
                d.IsSelected = d.Name.Equals(driveName, StringComparison.OrdinalIgnoreCase);

            _largeCustomFolders.Clear();
            UpdateLargeCustomLabel();
            NavLargeFiles.IsChecked = true;
            LargeStartScan_Click(sender, e);
        }
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (FormatHelper.RestartAsAdministrator())
        {
            Close();
        }
        else
        {
            WpfMessageBox.Show(
                "Elevation was cancelled or denied by Windows UAC.",
                "Administrator Mode",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    // ==================== PAGE 2: WINDOWS REPAIR ====================

    private bool EnsureAdminOrPrompt(string actionName)
    {
        if (_isAdmin) return true;

        var res = WpfMessageBox.Show(
            $"'{actionName}' requires Administrator privileges to repair protected Windows system files.\n\nWould you like to restart WinFix & Clean Portable as Administrator now?",
            "Administrator Required",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res == MessageBoxResult.Yes)
        {
            if (FormatHelper.RestartAsAdministrator())
                Close();
        }
        return false;
    }

    private void SetRepairBusy(bool busy, string status)
    {
        RepairStatusText.Text = status;
        RepairProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RepairProgressBar.IsIndeterminate = busy;
        RepairStopBtn.IsEnabled = busy;

        BtnAutoRepair.IsEnabled = !busy;
        BtnCheckDlls.IsEnabled = !busy;
        BtnSfcVerify.IsEnabled = !busy;
        BtnSfcRepair.IsEnabled = !busy;
        BtnDismCheck.IsEnabled = !busy;
        BtnDismRestore.IsEnabled = !busy;
        BtnChkdsk.IsEnabled = !busy;
        BtnNetReset.IsEnabled = !busy;
        BtnParseCbs.IsEnabled = !busy;
    }

    private void AppendRepairLog(string line)
    {
        Dispatcher.Invoke(() =>
        {
            RepairLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            RepairLogBox.ScrollToEnd();
        });
    }

    private void PopulateMissingFilesTable(List<MissingFileReportItem> items, string headerTitle)
    {
        Dispatcher.Invoke(() =>
        {
            _missingFiles.Clear();
            foreach (var it in items)
                _missingFiles.Add(it);

            MissingFilesHeader.Text = headerTitle;
            int missingCount = items.Count(i => i.Status.Contains("Missing", StringComparison.OrdinalIgnoreCase) ||
                                                i.Status.Contains("Corrupt", StringComparison.OrdinalIgnoreCase) ||
                                                i.Status.Contains("Unrepaired", StringComparison.OrdinalIgnoreCase));
            int repairedCount = items.Count(i => i.Status.Contains("Repaired", StringComparison.OrdinalIgnoreCase));

            if (items.Count == 0)
                MissingFilesSummary.Text = "No missing or corrupted files detected in recent logs ✓";
            else
                MissingFilesSummary.Text = $"{items.Count} entries  •  {missingCount} missing/corrupt  •  {repairedCount} repaired";
        });
    }

    private void RepairCheckDlls_Click(object sender, RoutedEventArgs e)
    {
        AppendRepairLog("Running instant System32 & SysWOW64 critical DLL verification...");
        var items = _repairService.CheckCommonSystemDlls();
        PopulateMissingFilesTable(items, "Critical Windows & Runtime DLL Check (System32 / SysWOW64)");

        int missing = items.Count(x => x.Status == "Missing");
        if (missing == 0)
        {
            AppendRepairLog("All checked core Windows & runtime DLLs are present.");
            RepairStatusText.Text = "DLL Check complete: All core System32/SysWOW64 DLLs are present.";
        }
        else
        {
            AppendRepairLog($"Found {missing} missing optional/runtime DLL(s). See table above for exact names.");
            RepairStatusText.Text = $"DLL Check complete: {missing} missing DLL(s) listed above.";
        }
    }

    private void RepairParseCbs_Click(object sender, RoutedEventArgs e)
    {
        AppendRepairLog("Reading C:\\Windows\\Logs\\CBS\\CBS.log for missing/corrupted/repaired system files...");
        var items = _repairService.ParseCbsLogForSystemFileIssues();
        PopulateMissingFilesTable(items, "Windows CBS.log Integrity Report (Missing / Corrupted / Repaired)");
        RepairStatusText.Text = items.Count == 0
            ? "CBS.log inspection complete: No broken or missing Windows system files reported."
            : $"CBS.log inspection complete: {items.Count} file record(s) found.";
        AppendRepairLog(RepairStatusText.Text);
    }

    private async void RepairAutoFull_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Smart Full Auto-Repair")) return;

        _repairCts = new CancellationTokenSource();
        var ct = _repairCts.Token;
        SetRepairBusy(true, "Running Smart Auto-Repair (Step 1/3: DISM RestoreHealth, Step 2/3: SFC /scannow, Step 3/3: CBS.log analysis)...");

        try
        {
            AppendRepairLog("=== STARTING SMART FULL AUTO-REPAIR ===");
            AppendRepairLog("Step 1/3: Checking & repairing Windows Component Store (DISM /RestoreHealth)...");
            await _repairService.RunSystemCommandAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", AppendRepairLog, ct);

            AppendRepairLog("Step 2/3: Scanning & repairing missing/corrupted Windows system files (sfc /scannow)...");
            await _repairService.RunSystemCommandAsync("sfc.exe", "/scannow", AppendRepairLog, ct, isUtf16Output: true);

            AppendRepairLog("Step 3/3: Analyzing CBS.log & critical DLLs for detailed file report...");
            var cbsFindings = await Task.Run(() => _repairService.ParseCbsLogForSystemFileIssues(), ct);
            var dllMissing = await Task.Run(() => _repairService.CheckCommonSystemDlls().Where(d => d.Status == "Missing").ToList(), ct);
            var combined = cbsFindings.Concat(dllMissing).ToList();
            PopulateMissingFilesTable(combined, "Auto-Repair Missing / Corrupted / Repaired Files Summary");

            AppendRepairLog("=== SMART AUTO-REPAIR COMPLETED ===");
            SetRepairBusy(false, "Smart Auto-Repair finished! Review the Missing/Repaired Files table and console output below.");
        }
        catch (OperationCanceledException)
        {
            AppendRepairLog("[CANCELLED] Repair operation was stopped by user.");
            SetRepairBusy(false, "Repair operation cancelled.");
        }
        catch (Exception ex)
        {
            AppendRepairLog($"[ERROR] {ex.Message}");
            SetRepairBusy(false, $"Repair failed: {ex.Message}");
        }
        finally
        {
            _repairCts?.Dispose();
            _repairCts = null;
        }
    }

    private async void RepairSfcVerify_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("SFC /verifyonly")) return;
        await RunSingleRepairCommandAsync("sfc.exe", "/verifyonly", "Scanning Windows for missing/corrupted files (read-only)...", isUtf16: true, parseCbsAfter: true);
    }

    private async void RepairSfcScannow_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("SFC /scannow")) return;
        await RunSingleRepairCommandAsync("sfc.exe", "/scannow", "Scanning and repairing missing/corrupted Windows files (sfc /scannow)...", isUtf16: true, parseCbsAfter: true);
    }

    private async void RepairDismCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("DISM CheckHealth")) return;
        await RunSingleRepairCommandAsync("DISM.exe", "/Online /Cleanup-Image /CheckHealth", "Checking Windows image health (DISM /CheckHealth)...");
    }

    private async void RepairDismRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("DISM RestoreHealth")) return;
        await RunSingleRepairCommandAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", "Restoring Windows system image (DISM /RestoreHealth) — may take several minutes...");
    }

    private async void RepairChkdsk_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("CHKDSK Online Scan")) return;
        string sysDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        await RunSingleRepairCommandAsync("chkdsk.exe", $"{sysDrive} /scan", $"Running online NTFS disk scan on {sysDrive} (no reboot required)...");
    }

    private async void RepairNetworkReset_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Network & Winsock Reset")) return;

        _repairCts = new CancellationTokenSource();
        var ct = _repairCts.Token;
        SetRepairBusy(true, "Resetting DNS cache, Winsock catalog, and TCP/IP stack...");
        try
        {
            await _repairService.RunSystemCommandAsync("ipconfig.exe", "/flushdns", AppendRepairLog, ct);
            await _repairService.RunSystemCommandAsync("netsh.exe", "winsock reset", AppendRepairLog, ct);
            await _repairService.RunSystemCommandAsync("netsh.exe", "int ip reset", AppendRepairLog, ct);
            SetRepairBusy(false, "Network & Winsock reset complete. Restart PC if internet issues persist.");
        }
        catch (OperationCanceledException)
        {
            AppendRepairLog("[CANCELLED] Network reset stopped.");
            SetRepairBusy(false, "Cancelled.");
        }
        finally
        {
            _repairCts?.Dispose();
            _repairCts = null;
        }
    }

    private async Task RunSingleRepairCommandAsync(string exe, string args, string statusMessage, bool isUtf16 = false, bool parseCbsAfter = false)
    {
        _repairCts = new CancellationTokenSource();
        var ct = _repairCts.Token;
        SetRepairBusy(true, statusMessage);

        try
        {
            int code = await _repairService.RunSystemCommandAsync(exe, args, AppendRepairLog, ct, isUtf16);
            if (parseCbsAfter)
            {
                AppendRepairLog("Parsing CBS.log for missing/corrupted/repaired system files...");
                var cbs = await Task.Run(() => _repairService.ParseCbsLogForSystemFileIssues(), ct);
                PopulateMissingFilesTable(cbs, $"CBS.log Findings after {exe} {args}");
            }
            SetRepairBusy(false, $"Completed '{exe} {args}' (Exit Code: {code}).");
        }
        catch (OperationCanceledException)
        {
            AppendRepairLog("[CANCELLED] Task stopped by user.");
            SetRepairBusy(false, "Task cancelled.");
        }
        catch (Exception ex)
        {
            AppendRepairLog($"[ERROR] {ex.Message}");
            SetRepairBusy(false, $"Error: {ex.Message}");
        }
        finally
        {
            _repairCts?.Dispose();
            _repairCts = null;
        }
    }

    private void RepairStop_Click(object sender, RoutedEventArgs e)
    {
        _repairCts?.Cancel();
        RepairStatusText.Text = "Stopping current repair process...";
    }

    private void RepairClearLog_Click(object sender, RoutedEventArgs e) => RepairLogBox.Clear();

    private void RepairExportReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string baseDir = FormatHelper.GetPortableBaseDirectory();
            string reportFile = Path.Combine(baseDir, $"WinFixRepairReport_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine("==========================================================");
            sb.AppendLine("  WinFix & Clean Portable — Windows Repair Report");
            sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  OS: {FormatHelper.GetWindowsFriendlyName()}");
            sb.AppendLine("==========================================================");
            sb.AppendLine();
            sb.AppendLine("--- MISSING / CORRUPTED / CHECKED FILES ---");
            if (_missingFiles.Count == 0)
            {
                sb.AppendLine("(None recorded)");
            }
            else
            {
                foreach (var item in _missingFiles)
                    sb.AppendLine($"[{item.Status}] {item.FileName} ({item.Source}) — {item.Details}");
            }
            sb.AppendLine();
            sb.AppendLine("--- CONSOLE LOG ---");
            sb.AppendLine(RepairLogBox.Text);

            File.WriteAllText(reportFile, sb.ToString(), Encoding.UTF8);
            WpfMessageBox.Show($"Report saved next to portable app:\n{reportFile}", "Report Exported", MessageBoxButton.OK, MessageBoxImage.Information);
            FormatHelper.RevealInExplorer(reportFile);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Could not save report: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== PAGE 3: LARGE & ZIP/ISO FILES ====================

    private void LargeAddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select a folder to scan for Large Files, ZIPs, and ISOs",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            if (!_largeCustomFolders.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
            {
                _largeCustomFolders.Add(dialog.SelectedPath);
                UpdateLargeCustomLabel();
            }
        }
    }

    private void LargeClearCustom_Click(object sender, RoutedEventArgs e)
    {
        _largeCustomFolders.Clear();
        UpdateLargeCustomLabel();
    }

    private void UpdateLargeCustomLabel()
    {
        LargeCustomFoldersLabel.Text = _largeCustomFolders.Count == 0
            ? "Custom folders: (none added — selected drives above will be scanned)"
            : $"Custom folders ({_largeCustomFolders.Count}): {string.Join("  |  ", _largeCustomFolders)}";
    }

    private async void LargeStartScan_Click(object sender, RoutedEventArgs e)
    {
        var roots = new List<string>();
        if (_largeCustomFolders.Count > 0)
            roots.AddRange(_largeCustomFolders);
        else
            roots.AddRange(_drives.Where(d => d.IsSelected).Select(d => d.Name));

        if (roots.Count == 0)
        {
            WpfMessageBox.Show("Please check at least one drive or add a custom folder to scan.", "No Scan Location", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long minLargeMb = 100;
        if (CmbMinLargeSize.SelectedItem is ComboBoxItem cbi && long.TryParse(cbi.Tag?.ToString(), out long parsedMb))
            minLargeMb = parsedMb;

        var options = new LargeArchiveScanOptions
        {
            RootPaths = roots,
            MinLargeFileBytes = minLargeMb * 1024L * 1024L,
            IncludeLargeFiles = ChkIncludeLarge.IsChecked == true,
            IncludeArchives = ChkIncludeArchives.IsChecked == true,
            IncludeDiskImages = ChkIncludeIso.IsChecked == true,
            IncludeInstallersAndMedia = ChkIncludeMedia.IsChecked == true,
            SkipSystemFolders = ChkSkipSystem.IsChecked == true
        };

        if (!options.IncludeLargeFiles && !options.IncludeArchives && !options.IncludeDiskImages && !options.IncludeInstallersAndMedia)
        {
            WpfMessageBox.Show("Please check at least one category (Large Files, ZIP/Archives, or ISO/Disk Images).", "No Filter Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _largeCts = new CancellationTokenSource();
        LargeScanBtn.IsEnabled = false;
        LargeStopBtn.IsEnabled = true;
        LargeProgressBar.Visibility = Visibility.Visible;
        LargeProgressBar.IsIndeterminate = true;
        LargeStatusText.Text = "Starting scan...";

        var progress = new Progress<ScanProgressInfo>(p =>
        {
            LargeStatusText.Text = $"{p.StageText}  •  Found {p.MatchesFound:N0} items ({FormatHelper.FormatBytes(p.TotalMatchedBytes)})  •  {p.CurrentFolder}";
        });

        try
        {
            _allLargeResults = await _largeScanner.ScanAsync(options, progress, _largeCts.Token);
            ApplyLargeFilter();
            AppLogger.LogInfo($"Large/ZIP/ISO scan completed: {_allLargeResults.Count} items ({FormatHelper.FormatBytes(_allLargeResults.Sum(x => x.SizeBytes))}).");
            LargeStatusText.Text = $"Scan complete! Found {_allLargeResults.Count:N0} items totaling {FormatHelper.FormatBytes(_allLargeResults.Sum(x => x.SizeBytes))}. Sorted largest-first.";
        }
        catch (OperationCanceledException)
        {
            LargeStatusText.Text = "Scan cancelled by user.";
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Large/ZIP/ISO scan failed", ex);
            LargeStatusText.Text = $"Scan error: {ex.Message}";
        }
        finally
        {
            LargeScanBtn.IsEnabled = true;
            LargeStopBtn.IsEnabled = false;
            LargeProgressBar.Visibility = Visibility.Collapsed;
            _largeCts?.Dispose();
            _largeCts = null;
        }
    }

    private void LargeStopScan_Click(object sender, RoutedEventArgs e)
    {
        _largeCts?.Cancel();
        LargeStatusText.Text = "Stopping scan...";
    }

    private void LargeFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is string tag)
        {
            _activeLargeChip = tag;
            HighlightActiveChip(tag);
            ApplyLargeFilter();
        }
    }

    private void HighlightActiveChip(string activeTag)
    {
        var chips = new[] { ChipAll, ChipIso, ChipZip, ChipLarge, ChipOver500, ChipOver1GB };
        var activeBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#D4AF37"));
        var activeFg = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#09090B"));
        var normalBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#1B1811"));
        var normalFg = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#F5E6C4"));

        foreach (var chip in chips)
        {
            if (chip == null) continue;
            bool isActive = chip.Tag?.ToString() == activeTag;
            chip.Background = isActive ? activeBrush : normalBrush;
            chip.Foreground = isActive ? activeFg : normalFg;
        }
    }

    private void LargeSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyLargeFilter();

    private void ApplyLargeFilter()
    {
        string query = LargeSearchBox.Text.Trim();
        IEnumerable<ScannedFileItem> filtered = _allLargeResults;

        filtered = _activeLargeChip switch
        {
            "ISO" => filtered.Where(x => x.IsDiskImage),
            "ARCHIVE" => filtered.Where(x => x.IsArchive),
            "LARGE" => filtered.Where(x => x.IsLarge),
            "500MB" => filtered.Where(x => x.SizeBytes >= 500L * 1024 * 1024),
            "1GB" => filtered.Where(x => x.SizeBytes >= 1024L * 1024 * 1024),
            _ => filtered
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(x =>
                x.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.DirectoryPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Extension.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _visibleLargeResults = filtered.ToList();
        LargeFilesGrid.ItemsSource = _visibleLargeResults;
        UpdateLargeSelectionSummary();
    }

    private void LargeRowCheck_Click(object sender, RoutedEventArgs e) => UpdateLargeSelectionSummary();

    private void LargeSelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _visibleLargeResults)
            item.IsSelected = true;
        UpdateLargeSelectionSummary();
    }

    private void LargeClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _allLargeResults)
            item.IsSelected = false;
        UpdateLargeSelectionSummary();
    }

    private void UpdateLargeSelectionSummary()
    {
        var selected = _allLargeResults.Where(x => x.IsSelected).ToList();
        long selectedBytes = selected.Sum(x => x.SizeBytes);
        long visibleBytes = _visibleLargeResults.Sum(x => x.SizeBytes);
        LargeSelectionSummary.Text =
            $"{_visibleLargeResults.Count:N0} visible ({FormatHelper.FormatBytes(visibleBytes)})  •  {selected.Count:N0} selected ({FormatHelper.FormatBytes(selectedBytes)} to reclaim)";
    }

    private void LargeOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (LargeFilesGrid.SelectedItem is ScannedFileItem item)
            FormatHelper.RevealInExplorer(item.FullPath);
        else
            WpfMessageBox.Show("Click a file row first to open its folder location.", "No Row Selected", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LargeFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LargeFilesGrid.SelectedItem is ScannedFileItem item)
            FormatHelper.RevealInExplorer(item.FullPath);
    }

    private void LargeCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (LargeFilesGrid.SelectedItem is ScannedFileItem item)
            WpfClipboard.SetText(item.FullPath);
    }

    private async void LargeInspectImpact_Click(object sender, RoutedEventArgs e)
    {
        if (LargeFilesGrid.SelectedItem is ScannedFileItem item)
            await ShowLargeFileImpactPopupAsync(item);
        else
            WpfMessageBox.Show("Click a file row first to view its delete safety summary & Google search.", "No File Selected", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void LargeRowInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is ScannedFileItem item)
            await ShowLargeFileImpactPopupAsync(item);
    }

    private async Task ShowLargeFileImpactPopupAsync(ScannedFileItem item)
    {
        var analysis = FileImpactService.Analyze(item.FullPath, item.SizeBytes);
        var dlg = new FileImpactDialog(analysis) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            if (dlg.RequestedAction == FileImpactDialogAction.MoveToRecycleBin)
                await DeleteSingleLargeFileAsync(item, sendToRecycleBin: true, skipConfirm: true);
            else if (dlg.RequestedAction == FileImpactDialogAction.DeletePermanently)
                await DeleteSingleLargeFileAsync(item, sendToRecycleBin: false, skipConfirm: true);
        }
    }

    private async void LargeRowRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is ScannedFileItem item)
            await DeleteSingleLargeFileAsync(item, sendToRecycleBin: true, skipConfirm: false);
    }

    private async void LargeRowPermDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is ScannedFileItem item)
            await DeleteSingleLargeFileAsync(item, sendToRecycleBin: false, skipConfirm: false);
    }

    private async void LargeContextRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (LargeFilesGrid.SelectedItem is ScannedFileItem item)
            await DeleteSingleLargeFileAsync(item, sendToRecycleBin: true, skipConfirm: false);
    }

    private async void LargeContextPermDelete_Click(object sender, RoutedEventArgs e)
    {
        if (LargeFilesGrid.SelectedItem is ScannedFileItem item)
            await DeleteSingleLargeFileAsync(item, sendToRecycleBin: false, skipConfirm: false);
    }

    private async Task DeleteSingleLargeFileAsync(ScannedFileItem item, bool sendToRecycleBin, bool skipConfirm)
    {
        if (!skipConfirm && !sendToRecycleBin)
        {
            var confirm = WpfMessageBox.Show(
                $"PERMANENTLY DELETE this file?\n\n{item.FileName}\nSize: {item.SizeFormatted}\nPath: {item.DirectoryPath}\n\nThis CANNOT be undone!",
                "Confirm Instant Permanent Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var (ok, err) = await Task.Run(() => ShellFileHelper.DeleteFile(item.FullPath, sendToRecycleBin));
        if (ok)
        {
            _allLargeResults.Remove(item);
            ApplyLargeFilter();
            RefreshDrives();
            string verb = sendToRecycleBin ? "Moved to Recycle Bin" : "Permanently deleted";
            LargeStatusText.Text = $"{verb}: {item.FileName} ({item.SizeFormatted} freed).";
            AppLogger.LogInfo($"{verb} single file: {item.FullPath} ({item.SizeFormatted})");
        }
        else
        {
            WpfMessageBox.Show($"Could not delete '{item.FileName}': {err}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LargeRecycleSelected_Click(object sender, RoutedEventArgs e)
        => await DeleteLargeSelectedAsync(sendToRecycleBin: true);

    private async void LargePermanentDelete_Click(object sender, RoutedEventArgs e)
        => await DeleteLargeSelectedAsync(sendToRecycleBin: false);

    private async Task DeleteLargeSelectedAsync(bool sendToRecycleBin)
    {
        var selected = _allLargeResults.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            WpfMessageBox.Show("Please check one or more files to delete.", "No Files Checked", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long totalBytes = selected.Sum(x => x.SizeBytes);
        string modeText = sendToRecycleBin
            ? "move these files to the Windows Recycle Bin (can be restored)"
            : "PERMANENTLY DELETE these files (CANNOT be undone!)";

        var confirm = WpfMessageBox.Show(
            $"Are you sure you want to {modeText}?\n\nFiles selected: {selected.Count:N0}\nTotal space: {FormatHelper.FormatBytes(totalBytes)}",
            sendToRecycleBin ? "Confirm Move to Recycle Bin" : "Confirm Permanent Delete",
            MessageBoxButton.YesNo,
            sendToRecycleBin ? MessageBoxImage.Question : MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        int deleted = 0;
        int failed = 0;
        long freed = 0;
        var deletedSet = new HashSet<ScannedFileItem>();

        LargeStatusText.Text = "Deleting selected files...";
        await Task.Run(() =>
        {
            foreach (var item in selected)
            {
                var (ok, _) = ShellFileHelper.DeleteFile(item.FullPath, sendToRecycleBin);
                if (ok)
                {
                    deleted++;
                    freed += item.SizeBytes;
                    deletedSet.Add(item);
                }
                else
                {
                    failed++;
                }
            }
        });

        _allLargeResults.RemoveAll(x => deletedSet.Contains(x));
        ApplyLargeFilter();
        RefreshDrives();

        string msg = $"Removed {deleted:N0} file(s), freed {FormatHelper.FormatBytes(freed)}." +
                     (failed > 0 ? $" ({failed} skipped — in use or access denied)" : "");
        LargeStatusText.Text = msg;
        WpfMessageBox.Show(msg, "Cleanup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ==================== PAGE 4: DUPLICATE FINDER ====================

    private void DupAddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Choose a folder to scan for duplicate files",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            AddDupFolderIfNew(dialog.SelectedPath);
    }

    private void DupQuickAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn || btn.Tag is not string tag) return;
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = tag switch
        {
            "Downloads" => Path.Combine(userProfile, "Downloads"),
            "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => string.Empty
        };

        if (Directory.Exists(path))
            AddDupFolderIfNew(path);
    }

    private void AddDupFolderIfNew(string folderPath)
    {
        if (!_dupFolders.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
            _dupFolders.Add(folderPath);
    }

    private void DupRemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DupFoldersList.SelectedItem is string selected)
            _dupFolders.Remove(selected);
    }

    private void DupClearFolders_Click(object sender, RoutedEventArgs e) => _dupFolders.Clear();

    private async void DupStartScan_Click(object sender, RoutedEventArgs e)
    {
        if (_dupFolders.Count == 0)
        {
            WpfMessageBox.Show("Please add at least one folder to scan for duplicates (e.g., Downloads, Documents, or a custom folder).",
                "No Locations Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long minSize = 102400;
        if (CmbDupMinSize.SelectedItem is ComboBoxItem sizeItem && long.TryParse(sizeItem.Tag?.ToString(), out long sVal))
            minSize = sVal;

        HashSet<string>? allowedExt = null;
        if (CmbDupFileType.SelectedItem is ComboBoxItem typeItem)
        {
            allowedExt = (typeItem.Tag?.ToString()) switch
            {
                "IMG" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tiff", ".raw" },
                "VID" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" },
                "ARC" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".iso", ".img", ".tar", ".gz", ".cab", ".wim" },
                "DOC" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv" },
                _ => null
            };
        }

        var options = new DuplicateScanOptions
        {
            TargetFolders = _dupFolders.ToList(),
            MinFileSizeBytes = minSize,
            IncludeSubfolders = ChkDupSubfolders.IsChecked == true,
            SkipHiddenAndSystem = ChkDupSkipHidden.IsChecked == true,
            AllowedExtensions = allowedExt
        };

        _dupCts = new CancellationTokenSource();
        DupScanBtn.IsEnabled = false;
        DupStopBtn.IsEnabled = true;
        DupProgressBar.Value = 5;
        DupStatusText.Text = "Starting 3-stage duplicate scan...";

        var progress = new Progress<DuplicateScanProgress>(p =>
        {
            DupStatusText.Text = p.Stage;
            DupProgressBar.Value = Math.Clamp(p.PercentComplete, 0, 100);
        });

        try
        {
            var (items, groupCount, wastedBytes) = await _dupService.FindDuplicatesAsync(options, progress, _dupCts.Token);
            _dupGroupCount = groupCount;
            _dupWastedBytes = wastedBytes;
            BindDuplicateGrid(items);
            AppLogger.LogInfo($"Duplicate scan completed: {_dupGroupCount} groups, {FormatHelper.FormatBytes(_dupWastedBytes)} wasted.");
            DupStatsLabel.Text = $"{_dupGroupCount:N0} duplicate groups  •  {FormatHelper.FormatBytes(_dupWastedBytes)} wasted";
            UpdateDupSelectionSummary();
        }
        catch (OperationCanceledException)
        {
            DupStatusText.Text = "Duplicate scan cancelled.";
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Duplicate scan failed", ex);
            DupStatusText.Text = $"Duplicate scan error: {ex.Message}";
        }
        finally
        {
            DupScanBtn.IsEnabled = true;
            DupStopBtn.IsEnabled = false;
            _dupCts?.Dispose();
            _dupCts = null;
        }
    }

    private void BindDuplicateGrid(List<DuplicateFileItem> items)
    {
        _duplicateItems = new ObservableCollection<DuplicateFileItem>(items);
        var view = CollectionViewSource.GetDefaultView(_duplicateItems);
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DuplicateFileItem.GroupHeader)));
        DuplicatesGrid.ItemsSource = view;
    }

    private void DupStopScan_Click(object sender, RoutedEventArgs e)
    {
        _dupCts?.Cancel();
        DupStatusText.Text = "Stopping duplicate scan...";
    }

    private void DupKeepOldest_Click(object sender, RoutedEventArgs e) => SmartSelectDuplicates("OLDEST");
    private void DupKeepNewest_Click(object sender, RoutedEventArgs e) => SmartSelectDuplicates("NEWEST");
    private void DupKeepShortest_Click(object sender, RoutedEventArgs e) => SmartSelectDuplicates("SHORTEST");

    private void SmartSelectDuplicates(string strategy)
    {
        foreach (var group in _duplicateItems.GroupBy(x => x.GroupId))
        {
            var ordered = strategy switch
            {
                "OLDEST" => group.OrderBy(x => x.LastModified).ThenBy(x => x.FullPath.Length).ToList(),
                "NEWEST" => group.OrderByDescending(x => x.LastModified).ThenBy(x => x.FullPath.Length).ToList(),
                "SHORTEST" => group.OrderBy(x => x.FullPath.Length).ThenBy(x => x.LastModified).ToList(),
                _ => group.ToList()
            };

            for (int i = 0; i < ordered.Count; i++)
                ordered[i].IsSelected = i > 0; // Keep index 0, mark all extra copies for deletion!
        }
        UpdateDupSelectionSummary();
    }

    private void DupClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _duplicateItems)
            item.IsSelected = false;
        UpdateDupSelectionSummary();
    }

    private void DupRowCheck_Click(object sender, RoutedEventArgs e) => UpdateDupSelectionSummary();

    private void UpdateDupSelectionSummary()
    {
        var selected = _duplicateItems.Where(x => x.IsSelected).ToList();
        long reclaim = selected.Sum(x => x.SizeBytes);
        DupSelectionSummary.Text = $"{selected.Count:N0} duplicate file(s) selected ({FormatHelper.FormatBytes(reclaim)} to reclaim)";
    }

    private void DupOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (DuplicatesGrid.SelectedItem is DuplicateFileItem item)
            FormatHelper.RevealInExplorer(item.FullPath);
        else
            WpfMessageBox.Show("Select a duplicate file row first.", "No Row Selected", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DuplicatesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DuplicatesGrid.SelectedItem is DuplicateFileItem item)
            FormatHelper.RevealInExplorer(item.FullPath);
    }

    private async void DupInspectImpact_Click(object sender, RoutedEventArgs e)
    {
        if (DuplicatesGrid.SelectedItem is DuplicateFileItem item)
            await ShowDupFileImpactPopupAsync(item);
        else
            WpfMessageBox.Show("Click a duplicate file row first to inspect its safety summary & Google search.", "No File Selected", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void DupRowInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is DuplicateFileItem item)
            await ShowDupFileImpactPopupAsync(item);
    }

    private async Task ShowDupFileImpactPopupAsync(DuplicateFileItem item)
    {
        var analysis = FileImpactService.Analyze(item.FullPath, item.SizeBytes);
        var dlg = new FileImpactDialog(analysis) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            if (dlg.RequestedAction == FileImpactDialogAction.MoveToRecycleBin)
                await DeleteSingleDupFileAsync(item, sendToRecycleBin: true, skipConfirm: true);
            else if (dlg.RequestedAction == FileImpactDialogAction.DeletePermanently)
                await DeleteSingleDupFileAsync(item, sendToRecycleBin: false, skipConfirm: true);
        }
    }

    private async void DupRowRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is DuplicateFileItem item)
            await DeleteSingleDupFileAsync(item, sendToRecycleBin: true, skipConfirm: false);
    }

    private async void DupRowPermDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is DuplicateFileItem item)
            await DeleteSingleDupFileAsync(item, sendToRecycleBin: false, skipConfirm: false);
    }

    private async Task DeleteSingleDupFileAsync(DuplicateFileItem item, bool sendToRecycleBin, bool skipConfirm)
    {
        if (!skipConfirm && !sendToRecycleBin)
        {
            var confirm = WpfMessageBox.Show(
                $"PERMANENTLY DELETE this duplicate copy?\n\n{item.FileName}\nSize: {item.SizeFormatted}\nFolder: {item.DirectoryPath}\n\nThis CANNOT be undone!",
                "Confirm Instant Permanent Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var (ok, err) = await Task.Run(() => ShellFileHelper.DeleteFile(item.FullPath, sendToRecycleBin));
        if (ok)
        {
            var remaining = _duplicateItems.Where(x => x != item).ToList();
            var stillDup = remaining.GroupBy(x => x.GroupId).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
            BindDuplicateGrid(stillDup);
            _dupGroupCount = stillDup.Select(x => x.GroupId).Distinct().Count();
            _dupWastedBytes = stillDup.GroupBy(x => x.GroupId).Sum(g => g.First().SizeBytes * (g.Count() - 1));
            DupStatsLabel.Text = $"{_dupGroupCount:N0} duplicate groups remaining  •  {FormatHelper.FormatBytes(_dupWastedBytes)} wasted";
            UpdateDupSelectionSummary();
            RefreshDrives();
            DupStatusText.Text = $"{(sendToRecycleBin ? "Moved to Recycle Bin" : "Permanently deleted")}: {item.FileName} ({item.SizeFormatted} freed).";
        }
        else
        {
            WpfMessageBox.Show($"Could not delete '{item.FileName}': {err}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DupRecycleSelected_Click(object sender, RoutedEventArgs e)
        => await DeleteSelectedDuplicatesAsync(sendToRecycleBin: true);

    private async void DupPermanentDelete_Click(object sender, RoutedEventArgs e)
        => await DeleteSelectedDuplicatesAsync(sendToRecycleBin: false);

    private async Task DeleteSelectedDuplicatesAsync(bool sendToRecycleBin)
    {
        var selected = _duplicateItems.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            WpfMessageBox.Show("Please select duplicate copies to delete (or click 'Keep Oldest' / 'Keep Newest' to auto-select safely).",
                "No Duplicates Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Safety Guard: Check if ALL copies in any duplicate group are checked!
        var fullySelectedGroups = _duplicateItems
            .GroupBy(x => x.GroupId)
            .Where(g => g.All(item => item.IsSelected))
            .ToList();

        if (fullySelectedGroups.Count > 0)
        {
            var warn = WpfMessageBox.Show(
                $"SAFETY WARNING: You have selected ALL copies in {fullySelectedGroups.Count} duplicate group(s)!\n" +
                $"Deleting all copies means NO original copy will remain.\n\n" +
                $"Click 'No' to cancel and use 'Keep Oldest' instead, or 'Yes' to continue anyway.",
                "All Copies Selected Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (warn != MessageBoxResult.Yes)
                return;
        }

        long totalBytes = selected.Sum(x => x.SizeBytes);
        string modeDesc = sendToRecycleBin ? "move to Recycle Bin" : "PERMANENTLY DELETE";
        var confirm = WpfMessageBox.Show(
            $"Confirm {modeDesc} {selected.Count:N0} duplicate file(s) totaling {FormatHelper.FormatBytes(totalBytes)}?",
            sendToRecycleBin ? "Move Duplicates to Recycle Bin" : "Permanent Delete Duplicates",
            MessageBoxButton.YesNo,
            sendToRecycleBin ? MessageBoxImage.Question : MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        int deleted = 0;
        int failed = 0;
        long freed = 0;
        var deletedSet = new HashSet<DuplicateFileItem>();

        DupStatusText.Text = "Removing selected duplicates...";
        await Task.Run(() =>
        {
            foreach (var item in selected)
            {
                var (ok, _) = ShellFileHelper.DeleteFile(item.FullPath, sendToRecycleBin);
                if (ok)
                {
                    deleted++;
                    freed += item.SizeBytes;
                    deletedSet.Add(item);
                }
                else
                {
                    failed++;
                }
            }
        });

        // Keep only remaining groups that still have 2+ copies (or remove deleted items cleanly)
        var remaining = _duplicateItems.Where(x => !deletedSet.Contains(x)).ToList();
        var stillDuplicated = remaining
            .GroupBy(x => x.GroupId)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();

        BindDuplicateGrid(stillDuplicated);
        _dupGroupCount = stillDuplicated.Select(x => x.GroupId).Distinct().Count();
        _dupWastedBytes = stillDuplicated.GroupBy(x => x.GroupId).Sum(g => g.First().SizeBytes * (g.Count() - 1));
        DupStatsLabel.Text = $"{_dupGroupCount:N0} duplicate groups remaining  •  {FormatHelper.FormatBytes(_dupWastedBytes)} wasted";
        UpdateDupSelectionSummary();
        RefreshDrives();

        string msg = $"Removed {deleted:N0} duplicate file(s) and freed {FormatHelper.FormatBytes(freed)}." +
                     (failed > 0 ? $" ({failed} skipped)" : "");
        DupStatusText.Text = msg;
        WpfMessageBox.Show(msg, "Duplicate Cleanup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ==================== PAGE 5: SMART FAST COPY ====================

    private void CopyAddSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select source folder to copy/move from",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            if (!_copySources.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
            {
                _copySources.Add(dialog.SelectedPath);
                _lastAnalyzedSignature = string.Empty;
            }
        }
    }

    private void CopyAddSourceFiles_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Select source file(s) to copy/move",
            Multiselect = true
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            foreach (var file in dialog.FileNames)
            {
                if (!_copySources.Contains(file, StringComparer.OrdinalIgnoreCase))
                {
                    _copySources.Add(file);
                    _lastAnalyzedSignature = string.Empty;
                }
            }
        }
    }

    private void CopyRemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (CopySourcesList.SelectedItem is string s)
        {
            _copySources.Remove(s);
            _lastAnalyzedSignature = string.Empty;
        }
    }

    private void CopyClearSources_Click(object sender, RoutedEventArgs e)
    {
        _copySources.Clear();
        _allCopyPlan.Clear();
        _lastAnalyzedSignature = string.Empty;
        ApplyCopyFilter();
    }

    private void CopyBrowseDest_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select destination folder to paste into",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            CopyDestTextBox.Text = dialog.SelectedPath;
            _lastAnalyzedSignature = string.Empty;
        }
    }

    private FastCopyOptions BuildFastCopyOptions()
    {
        int threads = Math.Clamp(Environment.ProcessorCount, 2, 8);
        if (CmbCopyThreads.SelectedItem is ComboBoxItem tItem && int.TryParse(tItem.Tag?.ToString(), out int tVal) && tVal > 0)
            threads = tVal;

        ConflictPolicy policy = ConflictPolicy.KeepBetterQuality;
        if (CmbCopyPolicy.SelectedItem is ComboBoxItem pItem)
        {
            policy = (pItem.Tag?.ToString()) switch
            {
                "ASK" => ConflictPolicy.AskUserToChoose,
                "LARGER" => ConflictPolicy.KeepLargerSize,
                "NEWER" => ConflictPolicy.KeepNewerDate,
                "SKIP" => ConflictPolicy.SkipDuplicatesAndExisting,
                "OVERWRITE" => ConflictPolicy.OverwriteExisting,
                "RENAME" => ConflictPolicy.KeepBothRename,
                _ => ConflictPolicy.KeepBetterQuality
            };
        }

        bool isMove = CmbCopyMode.SelectedItem is ComboBoxItem mItem && mItem.Tag?.ToString() == "MOVE";

        return new FastCopyOptions
        {
            SourcePaths = _copySources.ToList(),
            DestinationFolder = CopyDestTextBox.Text.Trim(),
            IsMoveMode = isMove,
            PreserveFolderStructure = ChkCopyKeepStructure.IsChecked == true,
            ThreadCount = threads,
            SkipContentDuplicatesByHash = ChkCopyZeroDuplicates.IsChecked == true,
            VerifyAfterCopy = ChkCopyVerify.IsChecked == true,
            Policy = policy
        };
    }

    private string BuildCopySignature(FastCopyOptions o)
        => $"{string.Join("|", o.SourcePaths)}=>{o.DestinationFolder}|S={o.PreserveFolderStructure}|D={o.SkipContentDuplicatesByHash}";

    private bool ValidateFastCopyInputs(FastCopyOptions options)
    {
        if (options.SourcePaths.Count == 0)
        {
            WpfMessageBox.Show("Please add at least one source folder or file.", "No Source Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(options.DestinationFolder))
        {
            WpfMessageBox.Show("Please select a destination folder.", "No Destination Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void SetFastCopyBusy(bool busy)
    {
        CopyAnalyzeBtn.IsEnabled = !busy;
        CopyStartBtn.IsEnabled = !busy;
        CopyStopBtn.IsEnabled = busy;
    }

    private async void CopyAnalyze_Click(object sender, RoutedEventArgs e)
    {
        var options = BuildFastCopyOptions();
        if (!ValidateFastCopyInputs(options)) return;

        await RunCopyAnalyzeInternalAsync(options);
    }

    private async Task<bool> RunCopyAnalyzeInternalAsync(FastCopyOptions options)
    {
        _copyCts = new CancellationTokenSource();
        SetFastCopyBusy(true);
        CopyProgressBar.Value = 5;
        CopySpeedText.Text = "Analyzing...";

        var progress = new Progress<FastCopyProgress>(p =>
        {
            CopyStatusText.Text = p.Stage;
            CopyProgressBar.Value = Math.Clamp(p.PercentComplete, 0, 100);
        });

        try
        {
            _allCopyPlan = await _fastCopyService.BuildPlanAsync(options, progress, _copyCts.Token);
            _lastAnalyzedSignature = BuildCopySignature(options);

            int conflicts = _allCopyPlan.Count(x => x.IsConflict);
            int dups = _allCopyPlan.Count(x => x.IsExactContentDuplicate);
            int newFiles = _allCopyPlan.Count(x => x.Decision == CopyDecision.CopyNew);

            if (conflicts > 0 && options.Policy == ConflictPolicy.AskUserToChoose)
            {
                _activeCopyChip = "CONFLICTS";
                HighlightCopyChip("CONFLICTS");
            }
            else
            {
                _activeCopyChip = "ALL";
                HighlightCopyChip("ALL");
            }

            ApplyCopyFilter();
            CopyStatusText.Text = $"Analysis ready: {newFiles:N0} new  •  {conflicts:N0} quality conflicts  •  {dups:N0} exact duplicates skipped. Review choices or click 'Start Fast Copy'.";
            CopySpeedText.Text = "Ready";
            return true;
        }
        catch (OperationCanceledException)
        {
            CopyStatusText.Text = "Analysis cancelled.";
            CopySpeedText.Text = "Cancelled";
            return false;
        }
        catch (Exception ex)
        {
            CopyStatusText.Text = $"Analysis error: {ex.Message}";
            CopySpeedText.Text = "Error";
            return false;
        }
        finally
        {
            SetFastCopyBusy(false);
            _copyCts?.Dispose();
            _copyCts = null;
        }
    }

    private async void CopyStart_Click(object sender, RoutedEventArgs e)
    {
        var options = BuildFastCopyOptions();
        if (!ValidateFastCopyInputs(options)) return;

        string sig = BuildCopySignature(options);
        bool needsAnalyze = _allCopyPlan.Count == 0 || _lastAnalyzedSignature != sig;

        if (needsAnalyze)
        {
            bool ok = await RunCopyAnalyzeInternalAsync(options);
            if (!ok) return;

            int conflicts = _allCopyPlan.Count(x => x.IsConflict);
            if (options.Policy == ConflictPolicy.AskUserToChoose && conflicts > 0)
            {
                WpfMessageBox.Show(
                    $"Found {conflicts:N0} file conflict(s) where the destination has a different version of the same file.\n\n" +
                    $"Review the 'Your Choice / Action' column (or use the bulk buttons above), then click 'Start Fast Copy' again to execute your choices!",
                    "Choose Conflict Quality",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
        }

        if (_allCopyPlan.Count == 0)
        {
            WpfMessageBox.Show("No files found to copy in the selected source location(s).", "Nothing to Copy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (options.IsMoveMode)
        {
            var confirmMove = WpfMessageBox.Show(
                "You selected MOVE (Cut & Paste) mode.\nSource files will be deleted ONLY after verified copy at destination.\n\nContinue?",
                "Confirm Move Mode",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmMove != MessageBoxResult.Yes) return;
        }

        _copyCts = new CancellationTokenSource();
        SetFastCopyBusy(true);
        CopyProgressBar.Value = 0;

        var progress = new Progress<FastCopyProgress>(p =>
        {
            CopyStatusText.Text = $"{p.Stage}  ({p.CurrentFile})";
            CopyProgressBar.Value = Math.Clamp(p.PercentComplete, 0, 100);
            CopySpeedText.Text = $"Speed: {FormatHelper.FormatBytes((long)p.SpeedBytesPerSec)}/s  •  {FormatHelper.FormatBytes(p.BytesTransferred)} / {FormatHelper.FormatBytes(p.TotalBytesToTransfer)}";
        });

        try
        {
            var summary = await _fastCopyService.ExecutePlanAsync(_allCopyPlan, options, progress, _copyCts.Token);
            ApplyCopyFilter();
            RefreshDrives();
            AppLogger.LogInfo($"FastCopy completed: Copied={summary.CopiedNew}, Replaced={summary.ReplacedBetterQuality}, Renamed={summary.RenamedKeptBoth}, DupsSkipped={summary.SkippedDuplicates}, Failed={summary.FailedCount}, Missing={summary.VerifiedMissingCount}.");

            string resultMsg =
                $"Fast {(options.IsMoveMode ? "Move" : "Copy")} Completed in {summary.Elapsed:mm\\:ss\\.f}!\n\n" +
                $"• Copied (New): {summary.CopiedNew:N0}\n" +
                $"• Replaced (Better Quality / Chosen): {summary.ReplacedBetterQuality:N0}\n" +
                $"• Kept Both (Renamed): {summary.RenamedKeptBoth:N0}\n" +
                $"• Skipped Exact Duplicates: {summary.SkippedDuplicates:N0}\n" +
                $"• Kept Existing Destination: {summary.SkippedKeptDestination:N0}\n" +
                $"• Total Data Copied: {FormatHelper.FormatBytes(summary.TotalBytesCopied)}\n" +
                $"• Failed: {summary.FailedCount:N0}  |  Missing Audit: {summary.VerifiedMissingCount:N0}";

            CopyStatusText.Text = $"Done! Copied: {summary.CopiedNew + summary.ReplacedBetterQuality + summary.RenamedKeptBoth:N0}  •  Duplicates skipped: {summary.SkippedDuplicates:N0}  •  Failed: {summary.FailedCount}  •  Missing: {summary.VerifiedMissingCount}";
            WpfMessageBox.Show(resultMsg, "Smart Fast Copy Complete", MessageBoxButton.OK,
                summary.FailedCount == 0 && summary.VerifiedMissingCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            CopyStatusText.Text = "Transfer stopped by user (no half-written temp files left behind).";
            CopySpeedText.Text = "Stopped";
        }
        catch (Exception ex)
        {
            CopyStatusText.Text = $"Transfer error: {ex.Message}";
        }
        finally
        {
            SetFastCopyBusy(false);
            _copyCts?.Dispose();
            _copyCts = null;
        }
    }

    private void CopyStop_Click(object sender, RoutedEventArgs e)
    {
        _copyCts?.Cancel();
        CopyStatusText.Text = "Stopping transfer safely...";
    }

    private void CmbCopyPolicy_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_allCopyPlan == null || _allCopyPlan.Count == 0 || CopyPlanGrid == null) return;
        var opts = BuildFastCopyOptions();
        foreach (var item in _allCopyPlan.Where(x => x.IsConflict))
            SmartFastCopyService.ApplyConflictDecision(item, opts.Policy);
        ApplyCopyFilter();
    }

    private void CopyFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is string tag)
        {
            _activeCopyChip = tag;
            HighlightCopyChip(tag);
            ApplyCopyFilter();
        }
    }

    private void HighlightCopyChip(string activeTag)
    {
        var chips = new[] { CopyChipAll, CopyChipConflicts, CopyChipDuplicates, CopyChipNew, CopyChipFailed };
        var activeBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#D4AF37"));
        var activeFg = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#09090B"));
        var normalBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#1B1811"));
        var normalFg = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#F5E6C4"));
        foreach (var chip in chips)
        {
            if (chip == null) continue;
            bool isActive = chip.Tag?.ToString() == activeTag;
            chip.Background = isActive ? activeBrush : normalBrush;
            chip.Foreground = isActive ? activeFg : normalFg;
        }
    }

    private void ApplyCopyFilter()
    {
        IEnumerable<CopyPlanItem> q = _allCopyPlan;
        q = _activeCopyChip switch
        {
            "CONFLICTS" => q.Where(x => x.IsConflict),
            "DUPS" => q.Where(x => x.IsExactContentDuplicate),
            "NEW" => q.Where(x => !x.DestinationExists && !x.IsExactContentDuplicate),
            "FAILED" => q.Where(x => x.Status.Contains("Failed", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Missing", StringComparison.OrdinalIgnoreCase)),
            _ => q
        };

        var list = q.ToList();
        CopyPlanGrid.ItemsSource = list;

        int total = _allCopyPlan.Count;
        int conflicts = _allCopyPlan.Count(x => x.IsConflict);
        int dups = _allCopyPlan.Count(x => x.IsExactContentDuplicate);
        int toCopy = _allCopyPlan.Count(x => x.Decision == CopyDecision.CopyNew || x.Decision == CopyDecision.UseSourceReplaceDest || x.Decision == CopyDecision.KeepBothRename);
        long bytesToCopy = _allCopyPlan.Where(x => x.Decision == CopyDecision.CopyNew || x.Decision == CopyDecision.UseSourceReplaceDest || x.Decision == CopyDecision.KeepBothRename).Sum(x => x.SourceSize);

        CopySummaryText.Text = $"Total: {total:N0}  •  Will Copy/Replace: {toCopy:N0} ({FormatHelper.FormatBytes(bytesToCopy)})  •  Conflicts: {conflicts:N0}  •  Duplicates Skipped: {dups:N0}";
    }

    private void CopyBulkBetterQuality_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _allCopyPlan.Where(x => x.IsConflict))
            SmartFastCopyService.ApplyConflictDecision(item, ConflictPolicy.KeepBetterQuality);
        ApplyCopyFilter();
    }

    private void CopyBulkUseSource_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _allCopyPlan.Where(x => x.IsConflict))
        {
            item.Decision = CopyDecision.UseSourceReplaceDest;
            item.Status = "Replace (Manual)";
            item.StatusColor = "#10B981";
        }
        ApplyCopyFilter();
    }

    private void CopyBulkKeepDest_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _allCopyPlan.Where(x => x.IsConflict))
        {
            item.Decision = CopyDecision.KeepDestinationSkip;
            item.Status = "Keep Dest (Manual)";
            item.StatusColor = "#F59E0B";
        }
        ApplyCopyFilter();
    }

    private void CopyBulkKeepBoth_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _allCopyPlan.Where(x => x.IsConflict))
        {
            item.Decision = CopyDecision.KeepBothRename;
            item.Status = "Rename (Keep Both)";
            item.StatusColor = "#8B5CF6";
        }
        ApplyCopyFilter();
    }

    private void CopyOpenDest_Click(object sender, RoutedEventArgs e)
    {
        string dest = CopyDestTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(dest) && Directory.Exists(dest))
            FormatHelper.OpenFolderInExplorer(dest);
        else
            WpfMessageBox.Show("Destination folder does not exist yet.", "Destination Folder", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyExportLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string baseDir = FormatHelper.GetPortableBaseDirectory();
            string logFile = Path.Combine(baseDir, $"WinFixFastCopyLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine("==========================================================");
            sb.AppendLine("  WinFix & Clean Portable — Smart Fast Copy Report");
            sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  Destination: {CopyDestTextBox.Text}");
            sb.AppendLine("==========================================================");
            sb.AppendLine();
            foreach (var item in _allCopyPlan)
                sb.AppendLine($"[{item.Status}] [{item.DecisionText}] {item.SourcePath} -> {item.DestinationPath} | {item.Reason}");

            File.WriteAllText(logFile, sb.ToString(), Encoding.UTF8);
            WpfMessageBox.Show($"Transfer log saved:\n{logFile}", "Log Exported", MessageBoxButton.OK, MessageBoxImage.Information);
            FormatHelper.RevealInExplorer(logFile);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Could not export log: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== PAGE 6: CLEAN APP UNINSTALLER ====================

    private void SetUninstBusy(bool busy, string status)
    {
        UninstStatusText.Text = status;
        UninstProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UninstProgressBar.IsIndeterminate = busy;
        UninstRefreshBtn.IsEnabled = !busy;
        UninstCleanBtn.IsEnabled = !busy;
        UninstScanLeftoversBtn.IsEnabled = !busy;
        LeftoversRecycleBtn.IsEnabled = !busy;
        LeftoversPermBtn.IsEnabled = !busy;
    }

    private async Task LoadInstalledAppsAsync()
    {
        SetUninstBusy(true, "Loading installed applications from Windows Registry...");
        try
        {
            bool showSystem = ChkUninstShowSystem.IsChecked == true;
            _allInstalledApps = await _uninstallerService.GetInstalledAppsAsync(showSystem);
            _appsLoadedOnce = true;
            ApplyUninstSearchFilter();
            AppLogger.LogInfo($"Loaded {_allInstalledApps.Count} installed application(s).");
            SetUninstBusy(false, $"Found {_allInstalledApps.Count:N0} installed program(s). Select any app to Clean Uninstall or scan leftovers.");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Failed to load installed apps", ex);
            SetUninstBusy(false, $"Error loading apps: {ex.Message}");
        }
    }

    private void UninstRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (PageUninstaller == null || PageUninstaller.Visibility != Visibility.Visible) return;
        _ = LoadInstalledAppsAsync();
    }

    private void UninstSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyUninstSearchFilter();

    private void ApplyUninstSearchFilter()
    {
        string q = UninstSearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(q)
            ? _allInstalledApps
            : _allInstalledApps.Where(a =>
                a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        InstalledAppsGrid.ItemsSource = filtered;
    }

    private void UninstOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledAppsGrid.SelectedItem is not InstalledAppItem app)
        {
            WpfMessageBox.Show("Select an application from the list first.", "No App Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
            FormatHelper.OpenFolderInExplorer(app.InstallLocation);
        else
            WpfMessageBox.Show($"Install folder is not recorded or does not exist for '{app.DisplayName}'.", "Folder Unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void UninstCleanUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledAppsGrid.SelectedItem is not InstalledAppItem app)
        {
            WpfMessageBox.Show("Please select an application to Clean Uninstall.", "No App Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunCleanUninstallAsync(app);
    }

    private async void UninstRowUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is InstalledAppItem app)
        {
            InstalledAppsGrid.SelectedItem = app;
            await RunCleanUninstallAsync(app);
        }
    }

    private async void UninstInfo_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledAppsGrid.SelectedItem is InstalledAppItem app)
            await ShowAppInfoAsync(app);
        else
            WpfMessageBox.Show("Select (or hover) an app first to see what it does.", "No App Selected", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void UninstRowInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is InstalledAppItem app)
        {
            InstalledAppsGrid.SelectedItem = app;
            await ShowAppInfoAsync(app);
        }
    }

    private async Task ShowAppInfoAsync(InstalledAppItem app)
    {
        var dlg = new AppInfoDialog(app) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.UninstallRequested)
            await RunCleanUninstallAsync(app);
    }

    private async Task RunCleanUninstallAsync(InstalledAppItem app)
    {
        if (app.IsWebApp)
        {
            await RunWebAppUninstallAsync(app);
            return;
        }

        string confirmText =
            $"Start Clean Uninstall for:\n\n{app.DisplayName} ({app.DisplayVersion})\nPublisher: {app.Publisher}\n\n" +
            $"Step 1: Runs the official Windows uninstaller.\n" +
            $"Step 2: Automatically scans leftover AppData, Install folders, shortcuts, and Registry traces for safe review & cleanup.";

        if (app.IsBrowserCore)
        {
            var webApps = _allInstalledApps.Where(w => w.IsWebApp && w.HostBrowserName == app.HostBrowserName).Select(w => w.DisplayName).Take(12).ToList();
            string list = webApps.Count > 0 ? "\n\nWeb apps that will ALSO be removed:\n• " + string.Join("\n• ", webApps) : string.Empty;

            var browserWarn = WpfMessageBox.Show(
                $"⚠ WARNING: You selected the {app.HostBrowserName} BROWSER itself.\n\n" +
                $"This will uninstall the ENTIRE {app.HostBrowserName} browser together with all of its web apps.{list}\n\n" +
                $"If you only want to remove ONE app (for example YouTube or Gmail), click 'No' and choose that app — it has a teal 'Web App' badge and removing it keeps {app.HostBrowserName} installed.\n\n" +
                $"Do you really want to uninstall the whole {app.HostBrowserName} browser?",
                $"Uninstall the entire {app.HostBrowserName}?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (browserWarn != MessageBoxResult.Yes) return;
        }
        else if (app.InfoAdviceLevel == "KEEP")
        {
            var keepWarn = WpfMessageBox.Show(
                $"'{app.DisplayName}' is marked as KEEP ({app.InfoCategory}).\n\n{app.InfoAdvice}\n\nUninstall anyway?",
                "Important Component",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (keepWarn != MessageBoxResult.Yes) return;
        }

        var confirm = WpfMessageBox.Show(confirmText, "Confirm Clean Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetUninstBusy(true, $"Step 1/2: Running uninstaller for '{app.DisplayName}' — complete the uninstall wizard if prompted...");
        try
        {
            var (launched, exitCode, msg) = await _uninstallerService.RunStandardUninstallerAsync(app);
            if (!launched)
            {
                WpfMessageBox.Show(
                    $"{msg}\n\nYou can still run 'Scan Leftovers Only' to inspect and clean broken remnants of this app.",
                    "Uninstaller Notice",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            SetUninstBusy(true, $"Step 2/2: Scanning leftover files, AppData folders, and Registry keys for '{app.DisplayName}'...");
            var leftovers = await _uninstallerService.ScanLeftoversAsync(app, CancellationToken.None);
            PopulateLeftovers(app, leftovers);

            await ReloadInstalledAppsQuietAsync();
            RefreshDrives();

            if (leftovers.Count == 0)
            {
                SetUninstBusy(false, $"Clean Uninstall complete for '{app.DisplayName}' — zero leftovers found! ✓");
                WpfMessageBox.Show($"'{app.DisplayName}' was uninstalled cleanly with zero leftover files or registry traces found!",
                    "Clean Uninstall Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                long totalSize = leftovers.Sum(l => l.SizeBytes);
                SetUninstBusy(false, $"Uninstaller finished. Found {leftovers.Count} leftover trace(s) ({FormatHelper.FormatBytes(totalSize)}). Review below and click 'Clean Selected Leftovers'.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Clean Uninstall error for '{app.DisplayName}'", ex);
            SetUninstBusy(false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes ONLY a browser web app (e.g. YouTube installed via Chrome) using "chrome.exe --uninstall-app-id=...".
    /// The browser, its profile, bookmarks, passwords and other web apps are never touched.
    /// </summary>
    private async Task RunWebAppUninstallAsync(InstalledAppItem app)
    {
        var confirm = WpfMessageBox.Show(
            $"Remove the web app '{app.DisplayName}'?\n\n" +
            $"✔ Only this web app (installed through {app.HostBrowserName}) will be removed.\n" +
            $"✔ {app.HostBrowserName} itself stays installed.\n" +
            $"✔ Your other web apps, bookmarks, passwords and history are NOT touched.\n\n" +
            $"{app.HostBrowserName} may show its own small 'Remove app?' confirmation — click 'Remove' there.",
            "Remove Web App Only",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetUninstBusy(true, $"Asking {app.HostBrowserName} to remove only the '{app.DisplayName}' web app...");
        try
        {
            var (launched, _, msg) = await _uninstallerService.RunStandardUninstallerAsync(app);
            if (!launched)
            {
                SetUninstBusy(false, msg);
                WpfMessageBox.Show(msg, "Web App Removal Blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // The browser removes the app asynchronously (after its own confirmation) — poll the registry for up to ~60s
            bool removed = false;
            for (int i = 0; i < 30 && !removed; i++)
            {
                await Task.Delay(2000);
                await ReloadInstalledAppsQuietAsync();
                removed = !_allInstalledApps.Any(a => a.IsWebApp && a.RegistryKeyPath == app.RegistryKeyPath && a.RegistryHive == app.RegistryHive);
                SetUninstBusy(true, $"Waiting for {app.HostBrowserName} to finish removing '{app.DisplayName}'... (confirm in the browser popup if shown)");
            }

            var leftovers = await _uninstallerService.ScanLeftoversAsync(app, CancellationToken.None);
            PopulateLeftovers(app, leftovers);

            string done = removed
                ? $"'{app.DisplayName}' web app removed ✓ — {app.HostBrowserName} is still installed."
                : $"'{app.DisplayName}' is still listed. If you cancelled in {app.HostBrowserName}, nothing was removed. Click Refresh after confirming.";
            SetUninstBusy(false, done);
            AppLogger.LogInfo($"Web app uninstall '{app.DisplayName}' via {app.HostBrowserName}: removed={removed}");
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Web app uninstall error for '{app.DisplayName}'", ex);
            SetUninstBusy(false, $"Error: {ex.Message}");
        }
    }

    private async Task ReloadInstalledAppsQuietAsync()
    {
        bool showSystem = ChkUninstShowSystem.IsChecked == true;
        _allInstalledApps = await _uninstallerService.GetInstalledAppsAsync(showSystem);
        ApplyUninstSearchFilter();
    }

    private async void UninstScanLeftovers_Click(object sender, RoutedEventArgs e)
    {
        if (InstalledAppsGrid.SelectedItem is not InstalledAppItem app)
        {
            WpfMessageBox.Show("Select an application first to scan its folders and registry traces.", "No App Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetUninstBusy(true, $"Scanning leftover folders, shortcuts & registry traces for '{app.DisplayName}'...");
        try
        {
            var leftovers = await _uninstallerService.ScanLeftoversAsync(app, CancellationToken.None);
            PopulateLeftovers(app, leftovers);
            SetUninstBusy(false, leftovers.Count == 0
                ? $"No leftover files or registry keys matched '{app.DisplayName}'."
                : $"Found {leftovers.Count} trace(s) ({FormatHelper.FormatBytes(leftovers.Sum(x => x.SizeBytes))}) for '{app.DisplayName}'. Review carefully before cleaning.");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Leftover scan failed", ex);
            SetUninstBusy(false, $"Scan error: {ex.Message}");
        }
    }

    private void PopulateLeftovers(InstalledAppItem app, List<LeftoverItem> items)
    {
        _lastScannedLeftoverApp = app;
        _leftoverItems.Clear();
        foreach (var it in items)
            _leftoverItems.Add(it);

        long totalBytes = items.Sum(x => x.SizeBytes);
        LeftoversHeader.Text = $"Leftovers for '{app.DisplayName}' — {items.Count} item(s) ({FormatHelper.FormatBytes(totalBytes)})";
    }

    private void LeftoversSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _leftoverItems)
            item.IsSelected = true;
    }

    private void LeftoversClearSel_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _leftoverItems)
            item.IsSelected = false;
    }

    private async void LeftoversCleanSafe_Click(object sender, RoutedEventArgs e)
        => await CleanSelectedLeftoversAsync(sendToRecycleBin: true);

    private async void LeftoversCleanPermanent_Click(object sender, RoutedEventArgs e)
        => await CleanSelectedLeftoversAsync(sendToRecycleBin: false);

    private async Task CleanSelectedLeftoversAsync(bool sendToRecycleBin)
    {
        var selected = _leftoverItems.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            WpfMessageBox.Show("Check at least one leftover item below to clean.", "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int regCount = selected.Count(x => x.Kind == "Registry Key");
        int fileDirCount = selected.Count - regCount;
        long totalBytes = selected.Sum(x => x.SizeBytes);

        string fileAction = sendToRecycleBin ? "move to Recycle Bin" : "PERMANENTLY DELETE";
        var confirm = WpfMessageBox.Show(
            $"Clean {selected.Count} selected leftover item(s) for '{_lastScannedLeftoverApp?.DisplayName ?? "App"}'?\n\n" +
            $"• Folders/Files: {fileDirCount} ({FormatHelper.FormatBytes(totalBytes)}) — will {fileAction}\n" +
            $"• Registry Keys: {regCount} — will be removed cleanly",
            sendToRecycleBin ? "Confirm Safe Leftover Cleanup" : "Confirm Permanent Leftover Delete",
            MessageBoxButton.YesNo,
            sendToRecycleBin ? MessageBoxImage.Question : MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        SetUninstBusy(true, "Cleaning selected leftover files and registry traces...");
        try
        {
            var (cleaned, failed, freed) = await _uninstallerService.CleanLeftoversAsync(_leftoverItems.ToList(), sendToRecycleBin, CancellationToken.None);
            RefreshDrives();

            // Refresh apps list in case an orphaned uninstall registry entry was removed
            bool showSystem = ChkUninstShowSystem.IsChecked == true;
            _allInstalledApps = await _uninstallerService.GetInstalledAppsAsync(showSystem);
            ApplyUninstSearchFilter();

            string msg = $"Cleaned {cleaned} leftover item(s), freed {FormatHelper.FormatBytes(freed)}." +
                         (failed > 0 ? $" ({failed} skipped — try 'Restart as Admin' for protected HKLM/Program Files items)" : "");
            SetUninstBusy(false, msg);
            WpfMessageBox.Show(msg, "Leftover Cleanup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Leftover cleanup failed", ex);
            SetUninstBusy(false, $"Cleanup error: {ex.Message}");
        }
    }

    // ==================== DRIVE FIX & FORMAT ====================

    private void DriveLog(string line) => Dispatcher.Invoke(() =>
    {
        DriveLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        DriveLogBox.ScrollToEnd();
    });

    private void SetDriveBusy(bool busy, string status)
    {
        DriveStatusText.Text = status;
        DriveProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DriveProgressBar.IsIndeterminate = busy;
        DriveScanBtn.IsEnabled = !busy;
    }

    private async Task ScanDrivesAsync()
    {
        SetDriveBusy(true, "Scanning all disks, partitions and volumes...");
        var (rows, error) = await _driveService.GetDrivesAsync();
        _drivesScannedOnce = true;
        _driveRows = rows;
        DrivesGrid.ItemsSource = _driveRows;
        if (error != null)
        {
            DriveLog(error);
            SetDriveBusy(false, error);
            return;
        }
        int issues = rows.Count(r => r.Problems.Count > 0 && !r.IsProtected && !r.IsNoMedia);
        SetDriveBusy(false, $"Found {rows.Select(r => r.DiskNumber).Distinct().Count()} disk(s), {rows.Count} item(s) — {issues} need fixing.");
        DriveLog($"Scan complete: {rows.Count} rows, {issues} with problems.");
    }

    private void DriveScan_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Drive Fix & Format")) return;
        _ = ScanDrivesAsync();
    }

    private DriveRow? SelectedDrive()
    {
        if (DrivesGrid.SelectedItem is DriveRow r) return r;
        WpfMessageBox.Show("Select a drive row first.", "No Drive Selected", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async Task RunDriveActionAsync(string title, Func<Task<ProcessResult>> action)
    {
        SetDriveBusy(true, $"{title}...");
        DriveLog($"=== {title} ===");
        try
        {
            var r = await action();
            DriveLog(r.Success ? $"✓ {title} finished." : $"✗ {title} failed (exit {r.ExitCode}).");
            AppLogger.LogInfo($"Drive action '{title}': exit={r.ExitCode} {r.Combined.Trim()}");
            await ScanDrivesAsync();
            if (!r.Success)
                WpfMessageBox.Show($"{title} failed:\n\n{r.Combined.Trim()}", "Drive Fix", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Drive action '{title}'", ex);
            SetDriveBusy(false, $"Error: {ex.Message}");
        }
    }

    private async void DriveRescanHw_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Rescan Hardware")) return;
        await RunDriveActionAsync("Rescan storage hardware", () => _driveService.RescanHardwareAsync(DriveLog));
    }

    private void DriveWriteProtect_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Remove USB Write-Protection")) return;
        var report = DriveRepairService.RemoveUsbWriteProtectionPolicies();
        foreach (var line in report) DriveLog(line);
        bool fixedAny = report.Any(l => l.StartsWith("FIXED"));
        WpfMessageBox.Show(
            string.Join("\n", report) + (fixedAny ? "\n\nUnplug and re-plug the pen drive now." : "\n\nNo registry write-protection was active. If the drive is still read-only, use 'Clear Read-Only' or check the physical lock switch on the drive/SD card."),
            "USB Write-Protection", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void DriveOnline_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive() is not { } r || !EnsureAdminOrPrompt("Bring Online")) return;
        if (!r.DiskOffline) { WpfMessageBox.Show("This disk is already online.", "Drive Fix"); return; }
        await RunDriveActionAsync($"Bring Disk {r.DiskNumber} online", () => _driveService.BringOnlineAsync(r, DriveLog));
    }

    private async void DriveReadOnly_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive() is not { } r || !EnsureAdminOrPrompt("Clear Read-Only")) return;
        await RunDriveActionAsync($"Clear read-only on Disk {r.DiskNumber}", () => _driveService.ClearReadOnlyAsync(r, DriveLog));
    }

    private async void DriveAssignLetter_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive() is not { } r || !EnsureAdminOrPrompt("Assign Drive Letter")) return;
        var (ok, reason) = DriveRepairService.ValidateAssignLetter(r);
        if (!ok) { WpfMessageBox.Show(reason, "Assign Drive Letter", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        await RunDriveActionAsync($"Assign drive letter to Disk {r.DiskNumber} Partition {r.PartitionNumber}", () => _driveService.AssignLetterAsync(r, DriveLog));
    }

    private async void DriveRepair_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive() is not { } r || !EnsureAdminOrPrompt("Repair File System")) return;
        var (ok, reason) = DriveRepairService.ValidateRepair(r);
        if (!ok) { WpfMessageBox.Show(reason, "Repair", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (WpfMessageBox.Show($"Run CHKDSK /F on {r.DriveLetter}: ({r.Label})?\n\nFiles open on this drive will be closed. Data is kept — only file system errors are repaired.",
                "Repair File System", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunDriveActionAsync($"CHKDSK /F on {r.DriveLetter}:", () => _driveService.RepairAsync(r, DriveLog, CancellationToken.None));
    }

    private string SelectedFs() => (CmbFormatFs.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "exFAT";

    private async void DriveFormat_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive() is not { } r || !EnsureAdminOrPrompt("Format")) return;
        string fs = SelectedFs();
        var (ok, reason) = DriveRepairService.ValidateFormatPartition(r, fs);
        if (!ok) { WpfMessageBox.Show(reason, "Format Blocked", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        string confirmWord = string.IsNullOrEmpty(r.DriveLetter) ? "FORMAT" : r.DriveLetter;
        var dlg = new InputDialog("Confirm Format",
            $"⚠ ALL DATA on this partition will be ERASED:\n\n{r.DiskTitle} ({r.BusType})\nPartition {r.PartitionNumber}  •  {r.LetterDisplay}  •  {r.Label}  •  {r.SizeDisplay}\n\nNew format: {fs}, label '{DriveRepairService.SanitizeLabel(FormatLabelBox.Text, fs)}', {(ChkQuickFormat.IsChecked == true ? "Quick" : "Full")} format.",
            "", confirmWord, danger: true) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        await RunDriveActionAsync($"Format {r.LetterDisplay} as {fs}",
            () => _driveService.FormatPartitionAsync(r, fs, FormatLabelBox.Text, ChkQuickFormat.IsChecked == true, DriveLog, CancellationToken.None));
    }

    private async void DriveReset_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive() is not { } r || !EnsureAdminOrPrompt("Full Reset & Format")) return;
        string fs = SelectedFs();
        var (ok, reason) = DriveRepairService.ValidateResetDisk(r, fs);
        if (!ok) { WpfMessageBox.Show(reason, "Reset Blocked", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        string style = CmbPartStyle.SelectedIndex == 1 ? "GPT" : "MBR";
        var allLetters = _driveRows.Where(x => x.DiskNumber == r.DiskNumber && x.DriveLetter.Length > 0).Select(x => x.DriveLetter + ":").ToList();
        var dlg = new InputDialog("Confirm FULL DISK RESET",
            $"⚠ The WHOLE disk will be wiped — every partition and all files:\n\n{r.DiskTitle} ({r.BusType}, {FormatHelper.FormatBytes(r.DiskSizeBytes)})\n" +
            $"Drive letters on this disk: {(allLetters.Count == 0 ? "none" : string.Join(", ", allLetters))}\n\n" +
            $"Result: one {fs} partition ({style}), label '{DriveRepairService.SanitizeLabel(FormatLabelBox.Text, fs)}'.",
            "", "FORMAT", danger: true) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        await RunDriveActionAsync($"Full reset & format Disk {r.DiskNumber}",
            () => _driveService.ResetAndFormatDiskAsync(r, fs, FormatLabelBox.Text, style, DriveLog, CancellationToken.None));
    }

    // ==================== BOOT MENU MANAGER ====================

    private void BootLog(string line) => Dispatcher.Invoke(() =>
    {
        BootLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        BootLogBox.ScrollToEnd();
    });

    private async Task ScanBootAsync()
    {
        BootStatusText.Text = "Reading boot entries (bcdedit)...";
        BootScanBtn.IsEnabled = false;
        try
        {
            string? espRoot = null;
            BootManagerService.EspMount? esp = null;
            var (mount, _) = await BootManagerService.EspMount.MountAsync();
            esp = mount;
            espRoot = esp?.Root;
            try
            {
                var result = await _bootService.ScanAsync(espRoot);
                _bootScannedOnce = true;
                if (result.Error != null)
                {
                    BootStatusText.Text = result.Error;
                    BootLog(result.Error);
                    return;
                }
                _bootEntries = result.Entries.OrderBy(x => x.IsFirmware).ThenByDescending(x => x.IsCurrent).ToList();
                BootGrid.ItemsSource = _bootEntries;

                if (espRoot != null)
                {
                    _efiFolders = BootManagerService.ListEfiFolders(espRoot, _bootEntries);
                    EfiGrid.ItemsSource = _efiFolders;
                }

                int timeoutIdx = new[] { 0, 3, 5, 10, 15, 30 }.ToList().IndexOf(result.TimeoutSeconds);
                if (timeoutIdx >= 0) CmbBootTimeout.SelectedIndex = timeoutIdx;

                int problems = _bootEntries.Count(x => x.Status.StartsWith("Broken") || x.Status.StartsWith("Stale"));
                int linux = _bootEntries.Count(x => x.Kind.StartsWith("Linux"));
                BootStatusText.Text = $"{(result.IsUefi ? "UEFI" : "Legacy BIOS")} PC  •  {_bootEntries.Count} entries  •  {problems} broken/stale  •  {linux} Linux/other OS  •  timeout {result.TimeoutSeconds}s  •  hover a row for details";
                BootLog($"Scan: {_bootEntries.Count} entries, {problems} broken/stale, EFI folders: {_efiFolders.Count}");
            }
            finally
            {
                if (esp != null) await esp.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Boot scan failed", ex);
            BootStatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            BootScanBtn.IsEnabled = true;
        }
    }

    private void BootScan_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Boot Menu Manager")) return;
        _ = ScanBootAsync();
    }

    private BootEntryItem? SelectedBoot()
    {
        if (BootGrid.SelectedItem is BootEntryItem b) return b;
        WpfMessageBox.Show("Select a boot entry first.", "No Entry Selected", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async Task RunBootActionAsync(BootManagerService.BootAction action, string question, string newName = "")
    {
        if (SelectedBoot() is not { } b || !EnsureAdminOrPrompt("Boot Menu Manager")) return;
        var (allowed, reason) = BootManagerService.ValidateAction(b, action);
        if (!allowed) { WpfMessageBox.Show(reason, "Blocked for Safety", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        if (action == BootManagerService.BootAction.Rename)
        {
            var dlg = new InputDialog("Rename Boot Entry", $"New name for '{b.DisplayName}':", b.Description) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            newName = dlg.ResponseText;
        }
        else if (WpfMessageBox.Show(question.Replace("{name}", b.DisplayName) + "\n\nAn automatic backup of the boot configuration is made first.",
                     "Boot Menu Manager", MessageBoxButton.YesNo, action == BootManagerService.BootAction.Delete ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        BootLog($"=== {action}: {b.DisplayName} ({b.Identifier}) ===");
        var (ok, msg) = await _bootService.ApplyActionAsync(b, action, BootLog, newName);
        BootLog(msg);
        await ScanBootAsync();
        WpfMessageBox.Show(msg, ok ? "Done" : "Failed", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void BootDelete_Click(object sender, RoutedEventArgs e) =>
        await RunBootActionAsync(BootManagerService.BootAction.Delete,
            "Permanently DELETE the boot entry '{name}'?\n\nIt will disappear from the boot menu. (If it belongs to a removed Linux, also delete its folder in 'EFI Partition Leftovers'.)");

    private async void BootHide_Click(object sender, RoutedEventArgs e) =>
        await RunBootActionAsync(BootManagerService.BootAction.Hide, "Hide '{name}' from the boot menu? (It is kept and can be shown again.)");

    private async void BootShow_Click(object sender, RoutedEventArgs e) =>
        await RunBootActionAsync(BootManagerService.BootAction.Show, "Add '{name}' back to the boot menu?");

    private async void BootDefault_Click(object sender, RoutedEventArgs e) =>
        await RunBootActionAsync(BootManagerService.BootAction.SetDefault, "Make '{name}' the DEFAULT Windows entry (starts automatically after the timeout)?");

    private async void BootFirst_Click(object sender, RoutedEventArgs e) =>
        await RunBootActionAsync(BootManagerService.BootAction.BootFirst, "Move '{name}' to the TOP of the UEFI (BIOS) boot order?\n\nThe PC will try to boot it first at next restart.");

    private async void BootRename_Click(object sender, RoutedEventArgs e) =>
        await RunBootActionAsync(BootManagerService.BootAction.Rename, string.Empty);

    private async Task AddBootEntryAsync(string defaultName, string? safeBoot)
    {
        if (!EnsureAdminOrPrompt("Add Boot Entry")) return;
        var dlg = new InputDialog("Add Boot Entry", "Name shown in the Windows boot menu:", defaultName) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var (ok, msg) = await _bootService.AddWindowsEntryAsync(dlg.ResponseText, safeBoot, BootLog);
        BootLog(msg);
        await ScanBootAsync();
        WpfMessageBox.Show(msg, ok ? "Entry Added" : "Failed", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void BootAddSafe_Click(object sender, RoutedEventArgs e) => await AddBootEntryAsync("Windows (Safe Mode)", "minimal");
    private async void BootAddSafeNet_Click(object sender, RoutedEventArgs e) => await AddBootEntryAsync("Windows (Safe Mode with Networking)", "network");
    private async void BootAddWindows_Click(object sender, RoutedEventArgs e) => await AddBootEntryAsync("Windows (copy)", null);

    private async void BootSetTimeout_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Boot Timeout")) return;
        int seconds = new[] { 0, 3, 5, 10, 15, 30 }[Math.Max(0, CmbBootTimeout.SelectedIndex)];
        var (ok, msg) = await _bootService.SetTimeoutAsync(seconds, BootLog);
        BootLog(msg);
        WpfMessageBox.Show(msg, ok ? "Done" : "Failed");
    }

    private async void BootBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Backup Boot Config")) return;
        var (ok, file, output) = await _bootService.BackupAsync(BootLog);
        BootLog(ok ? $"Backup saved: {file}" : $"Backup failed: {output}");
        if (ok) FormatHelper.RevealInExplorer(file);
        else WpfMessageBox.Show(output, "Backup Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void BootRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Restore Boot Config")) return;
        using var ofd = new WinForms.OpenFileDialog
        {
            Title = "Select a BCD backup to restore",
            Filter = "BCD backup (*.bcd)|*.bcd|All files|*.*",
            InitialDirectory = Directory.Exists(BootManagerService.BackupFolder) ? BootManagerService.BackupFolder : FormatHelper.GetPortableBaseDirectory()
        };
        if (ofd.ShowDialog() != WinForms.DialogResult.OK) return;

        var dlg = new InputDialog("Restore Boot Configuration",
            $"Replace the current boot configuration with:\n{ofd.FileName}\n\nUse this to undo boot changes. The PC must be restarted afterwards.",
            "", "RESTORE", danger: true) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        await _bootService.BackupAsync(BootLog); // safety: backup current state first
        var r = await _bootService.RestoreAsync(ofd.FileName, BootLog);
        BootLog(r.Success ? "Restore completed." : $"Restore failed: {r.Combined.Trim()}");
        await ScanBootAsync();
        WpfMessageBox.Show(r.Success ? "Boot configuration restored. Restart the PC to apply." : r.Combined.Trim(), "Restore", MessageBoxButton.OK,
            r.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void EfiScan_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Scan EFI Partition")) return;
        _ = ScanBootAsync();
    }

    private async void EfiDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminOrPrompt("Delete EFI Leftovers")) return;
        var selected = _efiFolders.Where(f => f.IsSelected && !f.IsProtected).ToList();
        if (selected.Count == 0)
        {
            WpfMessageBox.Show("Tick the leftover folder(s) to delete (e.g. 'ubuntu'). Microsoft and Boot folders are locked.", "Nothing Selected");
            return;
        }

        var dlg = new InputDialog("Delete EFI Leftovers",
            $"Delete these folders from the EFI boot partition?\n\n• \\EFI\\{string.Join("\n• \\EFI\\", selected.Select(s => s.Name))}\n\n" +
            "⚠ Only do this if that operating system is no longer installed on ANY disk — otherwise it will stop booting.\nA backup copy is saved to the EFI_Backups folder first.",
            "", "DELETE", danger: true) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var (mount, err) = await BootManagerService.EspMount.MountAsync();
        if (mount == null) { WpfMessageBox.Show(err, "EFI Partition", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            foreach (var item in selected)
            {
                var (_, msg) = BootManagerService.BackupAndDeleteEfiFolder(mount.Root, item);
                BootLog(msg);
            }
        }
        finally
        {
            await mount.DisposeAsync();
        }
        await ScanBootAsync();
    }

    // ==================== PAGE 7: JUNK CLEANER ====================

    private async void JunkAnalyze_Click(object sender, RoutedEventArgs e)
    {
        JunkAnalyzeBtn.IsEnabled = false;
        JunkCleanBtn.IsEnabled = false;
        var progress = new Progress<string>(s => JunkStatusText.Text = s);

        try
        {
            await _junkService.AnalyzeAsync(_junkCategories.ToList(), progress, CancellationToken.None);
            long total = _junkCategories.Sum(c => c.SizeBytes);
            int files = _junkCategories.Sum(c => c.FileCount);
            JunkStatusText.Text = $"Analysis complete: {files:N0} junk items found.";
            JunkSummaryText.Text = $"Total reclaimable across all categories: {FormatHelper.FormatBytes(total)}";
        }
        catch (Exception ex)
        {
            JunkStatusText.Text = $"Error analyzing junk: {ex.Message}";
        }
        finally
        {
            JunkAnalyzeBtn.IsEnabled = true;
            JunkCleanBtn.IsEnabled = true;
        }
    }

    private async void JunkClean_Click(object sender, RoutedEventArgs e)
    {
        var selectedCats = _junkCategories.Where(c => c.IsSelected).ToList();
        if (selectedCats.Count == 0)
        {
            WpfMessageBox.Show("Check at least one junk category to clean.", "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selectedCats.Sum(c => c.FileCount) == 0)
        {
            WpfMessageBox.Show("Click 'Analyze System Junk' first to discover junk files.", "Analyze First", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = WpfMessageBox.Show(
            $"Clean {selectedCats.Count} selected junk category(ies)?\nLocked/in-use files are automatically skipped safely.",
            "Confirm System Junk Cleanup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        JunkAnalyzeBtn.IsEnabled = false;
        JunkCleanBtn.IsEnabled = false;
        var progress = new Progress<string>(s => JunkStatusText.Text = s);

        try
        {
            var (deleted, skipped, freed) = await _junkService.CleanSelectedAsync(_junkCategories.ToList(), progress, CancellationToken.None);
            RefreshDrives();
            string msg = $"Cleaned {deleted:N0} junk file(s) and freed {FormatHelper.FormatBytes(freed)} ({skipped:N0} in-use files safely skipped).";
            JunkStatusText.Text = msg;
            JunkSummaryText.Text = $"Last cleanup freed: {FormatHelper.FormatBytes(freed)}";
            WpfMessageBox.Show(msg, "Junk Cleanup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            JunkStatusText.Text = $"Cleanup error: {ex.Message}";
        }
        finally
        {
            JunkAnalyzeBtn.IsEnabled = true;
            JunkCleanBtn.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _repairCts?.Cancel();
        _largeCts?.Cancel();
        _dupCts?.Cancel();
        _copyCts?.Cancel();
        base.OnClosed(e);
    }
}
