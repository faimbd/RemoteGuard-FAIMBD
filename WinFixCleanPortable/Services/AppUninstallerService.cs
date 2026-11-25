using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinFixCleanPortable.Helpers;
using WinFixCleanPortable.Models;

namespace WinFixCleanPortable.Services;

public class AppUninstallerService
{
    private const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // Strict safety blocklist: NEVER suggest deleting broad vendor or OS directories
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "windowsapps", "system32", "syswow64", "program files",
        "program files (x86)", "programdata", "common files", "users", "appdata", "roaming",
        "local", "locallow", "temp", "desktop", "documents", "downloads", "pictures", "videos",
        "google", "apple", "intel", "amd", "nvidia", "adobe", "oracle", "dotnet", "packages",
        "drivers", "fonts", "logs", "cache", "config", "public", "default", "all users",
        "start menu", "programs", "startup", "application data", "windows defender", "edge",
        // Generic install-folder leaf names that must NEVER be used as an app-name match token
        "application", "app", "apps", "bin", "setup", "installer", "install", "update", "updater",
        "data", "files", "common", "shared", "runtime", "tools", "client", "launcher", "service",
        "services", "plugins", "x64", "x86", "current", "latest", "user data",
        // Browser shortcut containers & browser vendor roots (protect all other web apps + the browser)
        "chrome apps", "edge apps", "chrome", "chromium", "bravesoftware", "brave", "vivaldi",
        "opera software", "mozilla", "firefox", "edgeupdate", "edgewebview", "googleupdate"
    };

    // Any path containing one of these is the browser's own install / profile data -> never deleted as a "leftover"
    private static readonly string[] BrowserProtectedPathFragments =
    {
        @"\google\chrome\", @"\google\update\", @"\chromium\",
        @"\microsoft\edge\", @"\microsoft\edgeupdate\", @"\microsoft\edgewebview\", @"\microsoft\edgecore\",
        @"\bravesoftware\", @"\vivaldi\", @"\opera software\", @"\mozilla firefox\", @"\mozilla\firefox\"
    };

    private static readonly Regex WebAppIdRegex = new(@"--uninstall-app-id=([a-z0-9]{16,64})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BrowserCoreNameRegex = new(
        @"^(Google Chrome|Microsoft Edge|Brave|Vivaldi|Opera( Stable| GX| Browser)?|Mozilla Firefox|Chromium)(\s|$|\()",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Classifies an Uninstall entry as a browser Web App (PWA like YouTube/Gmail installed via Chrome),
    /// the browser core itself (Chrome/Edge/Brave), or a normal program.
    /// </summary>
    public static (bool IsWebApp, bool IsBrowserCore, string HostBrowser, string AppId) ClassifyBrowserEntry(
        string displayName, string uninstallString)
    {
        displayName ??= string.Empty;
        uninstallString ??= string.Empty;

        var m = WebAppIdRegex.Match(uninstallString);
        if (m.Success)
            return (true, false, DetectBrowserFromCommand(uninstallString), m.Groups[1].Value);

        bool isCore = BrowserCoreNameRegex.IsMatch(displayName.Trim()) &&
                      !displayName.Contains("WebView", StringComparison.OrdinalIgnoreCase) &&
                      !displayName.Contains("Update", StringComparison.OrdinalIgnoreCase) &&
                      !displayName.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase);

        return (false, isCore, isCore ? NormalizeBrowserName(displayName) : string.Empty, string.Empty);
    }

    private static string DetectBrowserFromCommand(string cmd)
    {
        string c = cmd.ToLowerInvariant();
        if (c.Contains("msedge")) return "Microsoft Edge";
        if (c.Contains("brave")) return "Brave";
        if (c.Contains("vivaldi")) return "Vivaldi";
        if (c.Contains("opera")) return "Opera";
        if (c.Contains("chromium")) return "Chromium";
        if (c.Contains("chrome")) return "Google Chrome";
        return "Web Browser";
    }

    private static string NormalizeBrowserName(string displayName)
    {
        string d = displayName.ToLowerInvariant();
        if (d.StartsWith("google chrome")) return "Google Chrome";
        if (d.StartsWith("microsoft edge")) return "Microsoft Edge";
        if (d.StartsWith("brave")) return "Brave";
        if (d.StartsWith("vivaldi")) return "Vivaldi";
        if (d.StartsWith("opera")) return "Opera";
        if (d.StartsWith("mozilla firefox")) return "Mozilla Firefox";
        if (d.StartsWith("chromium")) return "Chromium";
        return displayName;
    }

    /// <summary>
    /// Final safety gate before launching any uninstall command.
    /// A web app may ONLY run "browser.exe --uninstall-app-id=..." — never the browser's own setup.exe --uninstall.
    /// </summary>
    public static (bool Allowed, string Reason) ValidateUninstallCommandSafety(InstalledAppItem app, string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd))
            return (false, "No uninstall command found for this application.");

        if (app.IsWebApp)
        {
            bool hasAppId = WebAppIdRegex.IsMatch(cmd);
            bool looksLikeBrowserRemoval = cmd.Contains("setup.exe", StringComparison.OrdinalIgnoreCase) ||
                                           Regex.IsMatch(cmd, @"--uninstall(\s|$|"")", RegexOptions.IgnoreCase) ||
                                           cmd.Contains("--force-uninstall", StringComparison.OrdinalIgnoreCase);
            if (!hasAppId || looksLikeBrowserRemoval)
                return (false, $"Blocked for safety: this command could remove the whole {app.HostBrowserName} browser instead of only the '{app.DisplayName}' web app.");
        }

        return (true, string.Empty);
    }

    public async Task<List<InstalledAppItem>> GetInstalledAppsAsync(bool includeSystemComponents = false)
    {
        return await Task.Run(() =>
        {
            var apps = new List<InstalledAppItem>();
            var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64", "System (64-bit)", "#B8860B", includeSystemComponents, apps, dedup);
            ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32", "System (32-bit)", "#8B6914", includeSystemComponents, apps, dedup);
            ReadHive(RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU", "User App", "#15803D", includeSystemComponents, apps, dedup);

            // Link browser cores with the number of web apps that depend on them
            foreach (var core in apps.Where(a => a.IsBrowserCore))
                core.RelatedWebAppCount = apps.Count(w => w.IsWebApp && w.HostBrowserName == core.HostBrowserName);

            foreach (var app in apps)
                AppKnowledgeService.Enrich(app);

            return apps.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        });
    }

    private static void ReadHive(
        RegistryHive hive,
        RegistryView view,
        string hiveTag,
        string scopeLabel,
        string scopeColor,
        bool includeSystemComponents,
        List<InstalledAppItem> output,
        HashSet<string> dedup)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallSubKey, writable: false);
            if (uninstallKey == null) return;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var appKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                    if (appKey == null) continue;

                    string displayName = (appKey.GetValue("DisplayName") as string ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(displayName))
                        continue;

                    int sysComp = Convert.ToInt32(appKey.GetValue("SystemComponent") ?? 0);
                    string parentKey = appKey.GetValue("ParentKeyName") as string ?? string.Empty;
                    string releaseType = appKey.GetValue("ReleaseType") as string ?? string.Empty;

                    if (!includeSystemComponents && (sysComp == 1 || !string.IsNullOrEmpty(parentKey) ||
                        releaseType.Equals("Security Update", StringComparison.OrdinalIgnoreCase) ||
                        releaseType.Equals("Update Rollup", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string uninstallStr = (appKey.GetValue("UninstallString") as string ?? string.Empty).Trim();
                    string quietUninstallStr = (appKey.GetValue("QuietUninstallString") as string ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(uninstallStr) && string.IsNullOrWhiteSpace(quietUninstallStr))
                        continue;

                    string version = (appKey.GetValue("DisplayVersion") as string ?? string.Empty).Trim();
                    string publisher = (appKey.GetValue("Publisher") as string ?? string.Empty).Trim();
                    string installLoc = (appKey.GetValue("InstallLocation") as string ?? string.Empty).Trim().Trim('"');
                    string rawDate = (appKey.GetValue("InstallDate") as string ?? string.Empty).Trim();

                    string formattedDate = FormatInstallDate(rawDate);

                    long sizeBytes = 0;
                    var rawSize = appKey.GetValue("EstimatedSize");
                    if (rawSize != null && long.TryParse(rawSize.ToString(), out long sizeKb) && sizeKb > 0)
                        sizeBytes = sizeKb * 1024L;

                    bool isMsi = Convert.ToInt32(appKey.GetValue("WindowsInstaller") ?? 0) == 1 ||
                                 uninstallStr.Contains("msiexec", StringComparison.OrdinalIgnoreCase);

                    string dedupKey = $"{displayName}|{version}|{hiveTag}";
                    if (!dedup.Add(dedupKey))
                        continue;

                    var (isWebApp, isBrowserCore, hostBrowser, appId) = ClassifyBrowserEntry(displayName, uninstallStr);

                    string label = isWebApp ? $"{ShortBrowser(hostBrowser)} Web App"
                                 : isBrowserCore ? "Browser"
                                 : isMsi ? "MSI Package" : scopeLabel;
                    string color = isWebApp ? "#0E7490"
                                 : isBrowserCore ? "#B91C1C"
                                 : isMsi ? "#7C3AED" : scopeColor;

                    output.Add(new InstalledAppItem
                    {
                        DisplayName = displayName,
                        Publisher = string.IsNullOrWhiteSpace(publisher) ? "Unknown Publisher" : publisher,
                        DisplayVersion = string.IsNullOrWhiteSpace(version) ? "—" : version,
                        InstallDate = formattedDate,
                        InstallLocation = installLoc,
                        UninstallString = uninstallStr,
                        QuietUninstallString = quietUninstallStr,
                        RegistryHive = hiveTag,
                        RegistryKeyPath = $@"{UninstallSubKey}\{subKeyName}",
                        ScopeLabel = label,
                        ScopeColor = color,
                        EstimatedSizeBytes = sizeBytes,
                        IsWindowsInstaller = isMsi,
                        IsWebApp = isWebApp,
                        IsBrowserCore = isBrowserCore,
                        HostBrowserName = hostBrowser,
                        WebAppId = appId
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private static string ShortBrowser(string browser) => browser switch
    {
        "Google Chrome" => "Chrome",
        "Microsoft Edge" => "Edge",
        _ => browser
    };

    public async Task<(bool Launched, int ExitCode, string Message)> RunStandardUninstallerAsync(
        InstalledAppItem app,
        bool preferQuiet = false)
    {
        string cmd = preferQuiet && !string.IsNullOrWhiteSpace(app.QuietUninstallString)
            ? app.QuietUninstallString
            : (!string.IsNullOrWhiteSpace(app.UninstallString) ? app.UninstallString : app.QuietUninstallString);

        // For browser web apps, ALWAYS use the per-app UninstallString (never a quiet/alternate command)
        if (app.IsWebApp)
            cmd = app.UninstallString;

        var (allowed, reason) = ValidateUninstallCommandSafety(app, cmd);
        if (!allowed)
        {
            AppLogger.LogWarn($"Uninstall blocked for '{app.DisplayName}': {reason} | cmd={cmd}");
            return (false, -1, reason);
        }

        // Normalize msiexec /I {GUID} to /X {GUID} so it uninstalls instead of opening repair/install wizard
        if (cmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            cmd = Regex.Replace(cmd, @"(?i)(/I|-I)\s*\{", "/X {");
            if (preferQuiet && !cmd.Contains("/q", StringComparison.OrdinalIgnoreCase))
                cmd += " /qb";
        }

        var (fileName, arguments) = SplitCommandLine(cmd);
        if (string.IsNullOrWhiteSpace(fileName))
            return (false, -1, $"Could not parse uninstall command: {cmd}");

        try
        {
            AppLogger.LogInfo($"Launching uninstaller for '{app.DisplayName}': {fileName} {arguments}");
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return (false, -1, "Uninstaller process did not start.");

            if (app.IsWebApp)
            {
                // Browsers hand the request to their running instance (and may stay open), so never block forever.
                await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(8000));
                AppLogger.LogInfo($"Web app removal requested for '{app.DisplayName}' via {app.HostBrowserName}.");
                return (true, proc.HasExited ? proc.ExitCode : 0, $"{app.HostBrowserName} received the request to remove only '{app.DisplayName}'.");
            }

            await proc.WaitForExitAsync();
            AppLogger.LogInfo($"Uninstaller exited for '{app.DisplayName}' with ExitCode={proc.ExitCode}.");
            return (true, proc.ExitCode, $"Uninstaller finished (Exit Code: {proc.ExitCode}).");
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Uninstaller launch failed for '{app.DisplayName}'", ex);
            return (false, -1, $"Failed to run uninstaller: {ex.Message}");
        }
    }

    /// <summary>
    /// Safely scans for leftover folders, Start Menu/Desktop shortcuts, and Registry keys belonging to the app.
    /// Uses strict protected-name and path-depth guards so Windows or other apps are NEVER harmed.
    /// </summary>
    public async Task<List<LeftoverItem>> ScanLeftoversAsync(InstalledAppItem app, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var results = new List<LeftoverItem>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // BROWSER WEB APP SAFE MODE: never touch browser folders / profile / registry —
            // only exact-name shortcuts of THIS web app + its own orphaned uninstall entry.
            if (app.IsWebApp)
            {
                ScanWebAppShortcutsOnly(app, results, seenPaths, ct);
                AddOrphanUninstallEntryIfPresent(app, results, seenPaths);
                return results;
            }

            var candidateNames = BuildSafeAppNameTokens(app);

            // 1. Original InstallLocation (ONLY if safe leaf app folder!)
            if (!string.IsNullOrWhiteSpace(app.InstallLocation) &&
                Directory.Exists(app.InstallLocation) &&
                IsSafeAppDirectoryToRemove(app.InstallLocation))
            {
                string full = Path.GetFullPath(app.InstallLocation).TrimEnd('\\');
                if (seenPaths.Add(full))
                {
                    results.Add(new LeftoverItem
                    {
                        Kind = "Folder",
                        BadgeColor = "#F59E0B",
                        LocationCategory = "Install Folder",
                        TargetPath = full,
                        SizeBytes = ComputeFolderSizeSafe(full, ct)
                    });
                }
            }

            // 2. AppData / LocalAppData / ProgramData / Program Files matching app-specific folder names
            var rootsToScan = new (string Path, string Label)[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData (Roaming)"),
                (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppData (Local)"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData"),
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files"),
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)")
            };

            string safePublisher = NormalizeToken(app.Publisher);

            foreach (var (rootPath, label) in rootsToScan)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                    continue;

                // Direct child match: e.g., %APPDATA%\AppName
                foreach (var dir in SafeGetDirectories(rootPath))
                {
                    ct.ThrowIfCancellationRequested();
                    string dirName = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(dirName)) continue;

                    if (MatchesAppToken(dirName, candidateNames) && IsSafeAppDirectoryToRemove(dir))
                    {
                        string full = Path.GetFullPath(dir).TrimEnd('\\');
                        if (seenPaths.Add(full))
                        {
                            results.Add(new LeftoverItem
                            {
                                Kind = "Folder",
                                BadgeColor = "#F59E0B",
                                LocationCategory = label,
                                TargetPath = full,
                                SizeBytes = ComputeFolderSizeSafe(full, ct)
                            });
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(safePublisher) &&
                             NormalizeToken(dirName).Equals(safePublisher, StringComparison.OrdinalIgnoreCase))
                    {
                        // Check nested folder: e.g., %APPDATA%\Publisher\AppName (NEVER delete Publisher root itself!)
                        foreach (var childDir in SafeGetDirectories(dir))
                        {
                            string childName = Path.GetFileName(childDir);
                            if (MatchesAppToken(childName, candidateNames) && IsSafeAppDirectoryToRemove(childDir))
                            {
                                string full = Path.GetFullPath(childDir).TrimEnd('\\');
                                if (seenPaths.Add(full))
                                {
                                    results.Add(new LeftoverItem
                                    {
                                        Kind = "Folder",
                                        BadgeColor = "#F59E0B",
                                        LocationCategory = $"{label} (Vendor Subfolder)",
                                        TargetPath = full,
                                        SizeBytes = ComputeFolderSizeSafe(full, ct)
                                    });
                                }
                            }
                        }
                    }
                }
            }

            // 3. Start Menu & Desktop leftover shortcuts matching app name
            var shortcutDirs = new (string Path, string Label)[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop Shortcut"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Public Desktop Shortcut"),
                (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"), "Start Menu"),
                (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"), "All Users Start Menu")
            };

            foreach (var (sDir, label) in shortcutDirs)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(sDir) || !Directory.Exists(sDir))
                    continue;

                try
                {
                    foreach (var lnk in Directory.EnumerateFiles(sDir, "*.lnk"))
                    {
                        string baseName = Path.GetFileNameWithoutExtension(lnk);
                        if (MatchesAppToken(baseName, candidateNames) && seenPaths.Add(lnk))
                        {
                            results.Add(new LeftoverItem
                            {
                                Kind = "File",
                                BadgeColor = "#06B6D4",
                                LocationCategory = label,
                                TargetPath = lnk,
                                SizeBytes = new FileInfo(lnk).Length
                            });
                        }
                    }

                    if (label.Contains("Start Menu", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var sub in SafeGetDirectories(sDir))
                        {
                            string subName = Path.GetFileName(sub);
                            if (MatchesAppToken(subName, candidateNames) && IsSafeAppDirectoryToRemove(sub) && seenPaths.Add(sub))
                            {
                                results.Add(new LeftoverItem
                                {
                                    Kind = "Folder",
                                    BadgeColor = "#06B6D4",
                                    LocationCategory = label,
                                    TargetPath = sub,
                                    SizeBytes = ComputeFolderSizeSafe(sub, ct)
                                });
                            }
                        }
                    }
                }
                catch { }
            }

            // 4. Leftover Registry Keys (Software\<AppName>, Software\<Publisher>\<AppName>, and orphaned Uninstall entry)
            ScanRegistryLeftovers(RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU", "Registry (HKCU\\Software)", candidateNames, safePublisher, results, seenPaths);
            ScanRegistryLeftovers(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64", "Registry (HKLM\\Software)", candidateNames, safePublisher, results, seenPaths);
            ScanRegistryLeftovers(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32", "Registry (WOW6432Node)", candidateNames, safePublisher, results, seenPaths);

            // Check if the original Uninstall registry key is still present (useful after broken/partial uninstall)
            AddOrphanUninstallEntryIfPresent(app, results, seenPaths);

            return results;
        }, ct);
    }

    private static void AddOrphanUninstallEntryIfPresent(InstalledAppItem app, List<LeftoverItem> results, HashSet<string> seenPaths)
    {
        if (!string.IsNullOrWhiteSpace(app.RegistryKeyPath) && RegistryKeyExists(app.RegistryHive, app.RegistryKeyPath))
        {
            string regFull = $"{app.RegistryHive}\\{app.RegistryKeyPath}";
            if (seenPaths.Add(regFull))
            {
                results.Add(new LeftoverItem
                {
                    Kind = "Registry Key",
                    BadgeColor = "#EC4899",
                    LocationCategory = "Uninstall Registry Entry",
                    TargetPath = app.RegistryKeyPath,
                    RegistryHive = app.RegistryHive,
                    IsSelected = false // Unchecked by default until user confirms app is already uninstalled!
                });
            }
        }
    }

    private static void ScanWebAppShortcutsOnly(InstalledAppItem app, List<LeftoverItem> results, HashSet<string> seenPaths, CancellationToken ct)
    {
        string exactName = app.DisplayName.Trim();
        if (exactName.Length < 2) return;

        var roots = new (string Path, string Label)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop Shortcut"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"), "Start Menu"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Chrome Apps"), "Start Menu (Chrome Apps)"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Edge Apps"), "Start Menu (Edge Apps)")
        };

        foreach (var (dir, label) in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
                {
                    // EXACT name match only (e.g. "YouTube.lnk") — never fuzzy, never "Google Chrome.lnk"
                    if (Path.GetFileNameWithoutExtension(lnk).Equals(exactName, StringComparison.OrdinalIgnoreCase) &&
                        !BrowserCoreNameRegex.IsMatch(exactName) &&
                        seenPaths.Add(lnk))
                    {
                        results.Add(new LeftoverItem
                        {
                            Kind = "File",
                            BadgeColor = "#06B6D4",
                            LocationCategory = $"{label} (Web App)",
                            TargetPath = lnk,
                            SizeBytes = new FileInfo(lnk).Length
                        });
                    }
                }
            }
            catch { }
        }
    }

    public async Task<(int CleanedCount, int FailedCount, long FreedBytes)> CleanLeftoversAsync(
        List<LeftoverItem> leftovers,
        bool sendFilesToRecycleBin,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            int cleaned = 0;
            int failed = 0;
            long freed = 0;

            foreach (var item in leftovers.Where(x => x.IsSelected))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (item.Kind == "Registry Key")
                    {
                        bool ok = DeleteRegistryKeySafe(item.RegistryHive, item.TargetPath);
                        if (ok)
                        {
                            cleaned++;
                            item.Status = "Removed ✓";
                            AppLogger.LogInfo($"Removed leftover registry key: {item.RegistryHive}\\{item.TargetPath}");
                        }
                        else
                        {
                            failed++;
                            item.Status = "Failed (Needs Admin?)";
                        }
                    }
                    else if (item.Kind == "Folder")
                    {
                        if (!Directory.Exists(item.TargetPath))
                        {
                            item.Status = "Already gone";
                            continue;
                        }
                        if (!IsSafeAppDirectoryToRemove(item.TargetPath))
                        {
                            failed++;
                            item.Status = "Blocked (Safety Guard)";
                            continue;
                        }

                        var (ok, err) = DeleteFolderSafe(item.TargetPath, sendFilesToRecycleBin);
                        if (ok)
                        {
                            cleaned++;
                            freed += item.SizeBytes;
                            item.Status = sendFilesToRecycleBin ? "Recycled ✓" : "Deleted ✓";
                            AppLogger.LogInfo($"Cleaned leftover folder: {item.TargetPath}");
                        }
                        else
                        {
                            failed++;
                            item.Status = $"Failed ({err})";
                        }
                    }
                    else // File / shortcut
                    {
                        var (ok, err) = ShellFileHelper.DeleteFile(item.TargetPath, sendFilesToRecycleBin);
                        if (ok)
                        {
                            cleaned++;
                            freed += item.SizeBytes;
                            item.Status = sendFilesToRecycleBin ? "Recycled ✓" : "Deleted ✓";
                            AppLogger.LogInfo($"Cleaned leftover file: {item.TargetPath}");
                        }
                        else
                        {
                            failed++;
                            item.Status = $"Failed ({err})";
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    item.Status = $"Error: {ex.Message}";
                }
            }

            return (cleaned, failed, freed);
        }, ct);
    }

    private static (bool Success, string? Error) DeleteFolderSafe(string folderPath, bool sendToRecycleBin)
    {
        if (sendToRecycleBin)
        {
            // SHFileOperation handles directories natively too!
            try
            {
                return ShellFileHelper.DeleteDirectoryOrFile(folderPath, sendToRecycleBin: true);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
        else
        {
            try
            {
                Directory.Delete(folderPath, recursive: true);
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }

    private static void ScanRegistryLeftovers(
        RegistryHive hive,
        RegistryView view,
        string hiveTag,
        string categoryLabel,
        HashSet<string> candidateNames,
        string safePublisher,
        List<LeftoverItem> results,
        HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var swKey = baseKey.OpenSubKey("SOFTWARE", writable: false);
            if (swKey == null) return;

            foreach (var subName in swKey.GetSubKeyNames())
            {
                if (ProtectedNames.Contains(subName))
                    continue;

                if (MatchesAppToken(subName, candidateNames))
                {
                    string rel = $@"SOFTWARE\{subName}";
                    if (seen.Add($"{hiveTag}\\{rel}"))
                    {
                        results.Add(new LeftoverItem
                        {
                            Kind = "Registry Key",
                            BadgeColor = "#EC4899",
                            LocationCategory = categoryLabel,
                            TargetPath = rel,
                            RegistryHive = hiveTag
                        });
                    }
                }
                else if (!string.IsNullOrWhiteSpace(safePublisher) &&
                         NormalizeToken(subName).Equals(safePublisher, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var pubKey = swKey.OpenSubKey(subName, writable: false);
                        if (pubKey == null) continue;
                        foreach (var childName in pubKey.GetSubKeyNames())
                        {
                            if (ProtectedNames.Contains(childName)) continue;
                            if (MatchesAppToken(childName, candidateNames))
                            {
                                string rel = $@"SOFTWARE\{subName}\{childName}";
                                if (seen.Add($"{hiveTag}\\{rel}"))
                                {
                                    results.Add(new LeftoverItem
                                    {
                                        Kind = "Registry Key",
                                        BadgeColor = "#EC4899",
                                        LocationCategory = categoryLabel,
                                        TargetPath = rel,
                                        RegistryHive = hiveTag
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static bool RegistryKeyExists(string hiveTag, string subKeyPath)
    {
        try
        {
            var (hive, view) = ParseHiveTag(hiveTag);
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var k = baseKey.OpenSubKey(subKeyPath, writable: false);
            return k != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteRegistryKeySafe(string hiveTag, string subKeyPath)
    {
        if (string.IsNullOrWhiteSpace(subKeyPath)) return false;
        // Safety guard: never allow deleting root SOFTWARE or Uninstall hives directly
        if (subKeyPath.Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase) ||
            subKeyPath.Equals(UninstallSubKey, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var (hive, view) = ParseHiveTag(hiveTag);
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            baseKey.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (RegistryHive Hive, RegistryView View) ParseHiveTag(string tag) => tag switch
    {
        "HKCU" => (RegistryHive.CurrentUser, RegistryView.Registry64),
        "HKLM32" => (RegistryHive.LocalMachine, RegistryView.Registry32),
        _ => (RegistryHive.LocalMachine, RegistryView.Registry64)
    };

    public static bool IsSafeAppDirectoryToRemove(string path)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\');
            var di = new DirectoryInfo(full);

            // Must be at least 2 levels deep (e.g., C:\Program Files\SomeApp or C:\Users\X\AppData\Roaming\SomeApp)
            if (di.Parent == null || di.Parent.Parent == null)
                return false;

            string leaf = di.Name.Trim();
            if (leaf.Length < 3 || ProtectedNames.Contains(leaf))
                return false;

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (full.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                return false;

            // Never delete browser install folders or browser profile data (protects Chrome + all its web apps, bookmarks, passwords)
            string lowered = full.ToLowerInvariant() + "\\";
            if (BrowserProtectedPathFragments.Any(f => lowered.Contains(f)))
                return false;

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (full.Equals(userProfile, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static HashSet<string> BuildSafeAppNameTokens(InstalledAppItem app)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string raw)
        {
            string trimmed = raw.Trim();
            if (trimmed.Length >= 4 && !ProtectedNames.Contains(trimmed))
                tokens.Add(NormalizeToken(trimmed));
        }

        // Full DisplayName
        TryAdd(app.DisplayName);

        // DisplayName without trailing version numbers or (64-bit) / x64 suffixes
        string stripped = Regex.Replace(app.DisplayName, @"\s*(\(?x64\)?|\(?x86\)?|\(?64-bit\)?|\(?32-bit\)?|v?\d+(\.\d+)+.*$)", "", RegexOptions.IgnoreCase).Trim();
        TryAdd(stripped);

        // InstallLocation leaf folder name (if safe)
        if (!string.IsNullOrWhiteSpace(app.InstallLocation) && IsSafeAppDirectoryToRemove(app.InstallLocation))
        {
            string leaf = Path.GetFileName(app.InstallLocation.TrimEnd('\\'));
            TryAdd(leaf);
        }

        return tokens;
    }

    private static bool MatchesAppToken(string folderOrKeyName, HashSet<string> appTokens)
    {
        if (string.IsNullOrWhiteSpace(folderOrKeyName)) return false;
        if (ProtectedNames.Contains(folderOrKeyName.Trim())) return false;

        string norm = NormalizeToken(folderOrKeyName);
        if (norm.Length < 4) return false;

        return appTokens.Contains(norm);
    }

    private static string NormalizeToken(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return Regex.Replace(input.Trim().ToLowerInvariant(), @"[^a-z0-9]", "");
    }

    private static string FormatInstallDate(string raw)
    {
        if (raw.Length == 8 && int.TryParse(raw[..4], out int y) && int.TryParse(raw[4..6], out int m) && int.TryParse(raw[6..8], out int d))
        {
            try { return new DateTime(y, m, d).ToString("yyyy-MM-dd"); } catch { }
        }
        return "—";
    }

    private static (string FileName, string Arguments) SplitCommandLine(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int endQuote = cmd.IndexOf('"', 1);
            if (endQuote > 1)
            {
                string exe = cmd[1..endQuote];
                string args = cmd[(endQuote + 1)..].Trim();
                return (exe, args);
            }
        }

        // Handle unquoted paths containing spaces (e.g., C:\Program Files\App\unins000.exe /silent)
        int exeIdx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
        {
            string exe = cmd[..(exeIdx + 4)].Trim();
            string args = cmd[(exeIdx + 4)..].Trim();
            return (exe, args);
        }

        int space = cmd.IndexOf(' ');
        if (space > 0)
            return (cmd[..space], cmd[(space + 1)..].Trim());

        return (cmd, string.Empty);
    }

    private static IEnumerable<string> SafeGetDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    private static long ComputeFolderSizeSafe(string path, CancellationToken ct)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        int fileCap = 0;

        while (stack.Count > 0 && fileCap < 20000)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    fileCap++;
                    try { total += new FileInfo(f).Length; } catch { }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) == 0)
                            stack.Push(sub);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return total;
    }
}
