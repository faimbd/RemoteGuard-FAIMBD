using System.Text.RegularExpressions;
using WinFixCleanPortable.Models;

namespace WinFixCleanPortable.Services;

/// <summary>
/// Offline knowledge base that explains "what does this installed app do?" and whether it is safe to uninstall.
/// Used for the hover info popup in the App Uninstaller.
/// </summary>
public static class AppKnowledgeService
{
    private const string Keep = "KEEP";
    private const string Check = "CHECK";
    private const string Safe = "SAFE";

    private sealed record Rule(string Pattern, string Category, string Description, string Level, string Advice);

    // Ordered: more specific patterns first. Pattern = case-insensitive regex on DisplayName.
    private static readonly Rule[] Rules =
    {
        // ---------- Runtimes & frameworks (needed by other apps) ----------
        new(@"Visual C\+\+.*Redistributable|Visual C\+\+ \d{4}.*Runtime", "Runtime Library",
            "Microsoft Visual C++ runtime files. Many games and programs (Adobe, Steam games, drivers) need these DLLs to start.",
            Keep, "Do NOT uninstall — apps and games may show 'MSVCP140.dll / VCRUNTIME140.dll missing' errors."),
        new(@"\.NET( Framework| Runtime| Desktop Runtime|Core)|Microsoft ASP\.NET|Windows Desktop Runtime", "Runtime Framework",
            "Microsoft .NET runtime that lets .NET-based applications run (including many utilities and this kind of software).",
            Keep, "Keep it unless you are sure no app needs this .NET version."),
        new(@"WebView2", "Runtime Component",
            "Microsoft Edge WebView2 Runtime — lets apps like Teams, Outlook, Widgets and many installers show web content.",
            Keep, "Do NOT uninstall — many modern Windows apps depend on it."),
        new(@"DirectX", "Graphics Runtime",
            "DirectX runtime components used by games and 3D/graphics applications.", Keep, "Keep for games and graphics apps."),
        new(@"\bJava\b|Java\(TM\)|OpenJDK|Temurin|Zulu", "Runtime (Java)",
            "Java runtime/JDK that runs Java-based programs (Minecraft Java, some banking/government tools, IDEs).",
            Check, "Safe to remove if you don't run any Java programs."),
        new(@"^Python \d|Python Launcher", "Programming Runtime",
            "Python programming language interpreter used by scripts and developer tools.",
            Check, "Remove only if you don't write/run Python scripts or tools that need it."),
        new(@"Node\.js", "Programming Runtime", "Node.js JavaScript runtime used for web development tools.",
            Check, "Remove only if you don't do JavaScript/web development."),
        new(@"Microsoft Update Health Tools|Windows PC Health Check|Update for Windows|KB\d{6,}", "Windows Component",
            "Windows Update helper / health check component installed by Microsoft.", Keep, "Leave it — Windows Update uses it."),
        new(@"GameInput|Gaming Services|Xbox", "Windows Gaming Component",
            "Microsoft gaming / Xbox services used by Xbox app, Game Pass and controller support.",
            Check, "Keep if you play Game Pass / Xbox games or use controllers."),

        // ---------- Drivers & hardware utilities ----------
        new(@"NVIDIA (Graphics Driver|Display|PhysX|HD Audio|FrameView|App|GeForce)|GeForce Experience", "GPU Driver / Utility",
            "NVIDIA graphics driver or companion tool that makes your NVIDIA GPU work properly (display, games, video).",
            Keep, "Do NOT uninstall the graphics driver — screen resolution and game performance can break."),
        new(@"AMD (Software|Radeon|Chipset)|Radeon Software|AMD Adrenalin", "GPU / Chipset Driver",
            "AMD graphics or chipset driver software for your AMD hardware.", Keep, "Keep — required for AMD GPU/CPU features."),
        new(@"Intel\(R\)|Intel (Graphics|Driver|Management|Rapid|Wireless|Bluetooth|Chipset|Arc)", "Hardware Driver (Intel)",
            "Intel driver or hardware management component (graphics, Wi-Fi, Bluetooth, chipset, storage).",
            Keep, "Keep — removing can break Wi-Fi, Bluetooth, display or storage features."),
        new(@"Realtek", "Audio / Network Driver",
            "Realtek audio, card reader or network driver that powers your sound or LAN/Wi-Fi hardware.",
            Keep, "Keep — removing can stop your speakers/headphones or internet adapter working."),
        new(@"Synaptics|ELAN|Precision Touchpad", "Touchpad Driver", "Laptop touchpad driver and gesture settings.",
            Keep, "Keep — touchpad gestures may stop working."),
        new(@"Logitech|Razer|Corsair|SteelSeries|HyperX", "Peripheral Utility",
            "Software for your mouse/keyboard/headset (RGB lighting, macros, DPI settings).",
            Check, "Safe to remove if you don't need the RGB/macro customization."),
        new(@"MSI Afterburner|RivaTuner|HWiNFO|CPU-Z|GPU-Z|CrystalDisk", "Hardware Monitoring",
            "Tool to monitor or tune CPU/GPU temperatures, clocks and disk health.", Safe, "Safe to remove if you don't use it."),
        new(@"^(HP|Dell|Lenovo|ASUS|Acer|MSI) ", "OEM Laptop Utility",
            "Manufacturer utility from your PC brand (updates, battery, function keys, support).",
            Check, "Some control hotkeys/battery modes — remove only if you don't need them."),

        // ---------- Browsers ----------
        new(@"^Google Chrome", "Web Browser", "Google Chrome web browser — also hosts Chrome web apps you installed (YouTube, Gmail, etc.).",
            Check, "Uninstalling removes Chrome AND all Chrome web apps. To remove only one Chrome app, uninstall that app instead."),
        new(@"^Microsoft Edge", "Web Browser (Windows)", "Microsoft Edge browser built into Windows; many Windows features open links in it.",
            Keep, "Windows needs Edge — keep it."),
        new(@"Mozilla Firefox", "Web Browser", "Mozilla Firefox web browser.", Check, "Safe to remove if you use another browser (export bookmarks first)."),
        new(@"^Brave|^Vivaldi|^Opera", "Web Browser", "Alternative Chromium-based web browser.", Check, "Safe to remove if you use another browser."),

        // ---------- Productivity ----------
        new(@"Microsoft 365|Microsoft Office|Office 16|Office 15", "Office Suite", "Microsoft Word, Excel, PowerPoint, Outlook office apps.",
            Check, "Keep if you use Word/Excel. Reinstalling requires your Microsoft account/license."),
        new(@"Microsoft Teams|^Teams", "Communication", "Microsoft Teams chat, meeting and video call app.", Check, "Safe to remove if you don't use Teams."),
        new(@"OneDrive", "Cloud Storage", "Microsoft OneDrive — syncs your Desktop/Documents/Pictures to the cloud.",
            Check, "Make sure your files are fully synced/downloaded before uninstalling."),
        new(@"Adobe Acrobat|Acrobat Reader", "PDF Reader", "Adobe software to open, fill and sign PDF files.",
            Safe, "Safe to remove if you use another PDF reader (Edge/Chrome can open PDFs)."),
        new(@"Adobe (Photoshop|Premiere|After Effects|Illustrator|Lightroom|Creative Cloud)", "Creative Software",
            "Adobe creative application for photo/video/design editing.", Check, "Remove via Creative Cloud if possible; keep if you use it."),
        new(@"Notepad\+\+|Sublime Text", "Text Editor", "Advanced text/code editor.", Safe, "Safe to remove if unused."),
        new(@"Visual Studio Code|^Microsoft Visual Studio|JetBrains|IntelliJ|PyCharm|Android Studio", "Developer IDE",
            "Code editor / development environment for programming.", Check, "Remove only if you no longer develop software."),
        new(@"Windows Software Development Kit|Windows SDK|Windows Kits", "Developer SDK",
            "Windows SDK tools used to build software.", Check, "Safe to remove if you don't compile Windows software."),
        new(@"^Git( version|$| \d)|GitHub Desktop", "Developer Tool", "Git version control for source code.", Check, "Remove only if you don't use Git."),
        new(@"Docker", "Developer Tool", "Docker containers platform.", Check, "Removing deletes local containers/images."),
        new(@"VirtualBox|VMware", "Virtual Machine", "Runs other operating systems inside a virtual PC.", Check, "Your virtual machines won't open after uninstall."),

        // ---------- Media ----------
        new(@"VLC", "Media Player", "VLC media player — plays almost any video/audio format.", Safe, "Safe to remove if you use another player."),
        new(@"K-Lite|Codec", "Video Codecs", "Codec pack that lets players open extra video/audio formats.", Check, "Some videos may stop playing in old players."),
        new(@"Spotify", "Music Streaming", "Spotify music streaming app.", Safe, "Safe to remove (web player still works)."),
        new(@"iTunes|Apple (Mobile|Application) Support|Bonjour", "Apple Component",
            "Apple iTunes / iPhone connection support components.", Check, "Keep if you connect an iPhone/iPad to this PC."),
        new(@"OBS Studio", "Screen Recording", "OBS Studio for streaming and screen recording.", Safe, "Safe to remove if unused."),

        // ---------- Communication / remote ----------
        new(@"Zoom", "Video Meetings", "Zoom video conferencing app.", Safe, "Safe to remove (can join via browser)."),
        new(@"Discord", "Chat / Voice", "Discord voice and text chat app (gaming communities).", Safe, "Safe to remove if unused."),
        new(@"Telegram|WhatsApp|Messenger|Skype|Viber", "Messaging", "Desktop messaging app.", Safe, "Safe to remove; chats stay in your phone/cloud."),
        new(@"AnyDesk|TeamViewer|RustDesk|Chrome Remote Desktop", "Remote Access",
            "Remote desktop tool — lets someone control this PC remotely.",
            Check, "Remove if you don't use it (unused remote tools are a security risk)."),

        // ---------- Gaming ----------
        new(@"^Steam$|Steam Client", "Game Launcher", "Steam game store and launcher.", Check, "Uninstalling Steam can also remove installed Steam games."),
        new(@"Epic Games|EA app|Origin|Ubisoft Connect|Battle\.net|GOG Galaxy|Riot", "Game Launcher",
            "Game store/launcher required to start its games.", Check, "Its games won't launch without it."),

        // ---------- Archive / downloads ----------
        new(@"7-Zip|WinRAR|PeaZip|Bandizip", "Archive Tool", "Opens and creates ZIP/RAR/7Z compressed files.",
            Safe, "Safe to remove (Windows can open basic ZIP files)."),
        new(@"Internet Download Manager|IDM", "Download Manager", "Accelerates downloads and grabs videos from browsers.", Safe, "Safe to remove if unused."),
        new(@"qBittorrent|uTorrent|BitTorrent", "Torrent Client", "Downloads files via torrent.", Safe, "Safe to remove if unused."),

        // ---------- Security ----------
        new(@"Avast|AVG|McAfee|Norton|Kaspersky|ESET|Bitdefender|Malwarebytes|Avira|Sophos|Trend Micro", "Antivirus / Security",
            "Third-party antivirus or security suite protecting this PC.",
            Check, "If removed, make sure Windows Defender turns back on. Use the vendor's removal tool if uninstall fails."),

        // ---------- Utilities ----------
        new(@"PowerToys", "Windows Utility", "Microsoft PowerToys productivity tools (FancyZones, Color Picker...).", Safe, "Safe to remove if unused."),
        new(@"CCleaner|IObit|Driver Booster|Advanced SystemCare|Wise ", "System Utility",
            "Third-party cleanup/optimizer utility (often bundles ads/background services).", Safe, "Safe to remove."),
        new(@"Dropbox|Google Drive|MEGA", "Cloud Storage", "Cloud file sync app.", Check, "Make sure files are synced before removing."),
    };

    public static void Enrich(InstalledAppItem app)
    {
        // 1) Browser web apps (PWA): explain clearly that the browser stays
        if (app.IsWebApp)
        {
            app.InfoCategory = $"{app.HostBrowserName} Web App";
            app.InfoDescription = $"'{app.DisplayName}' is a website installed as an app through {app.HostBrowserName} " +
                                  "(it opens in its own window with its own Start menu/taskbar shortcut).";
            app.InfoAdviceLevel = Safe;
            app.InfoAdvice = "Safe to uninstall — only this web app shortcut is removed.";
            app.InfoExtraNote = $"🛡 {app.HostBrowserName} itself, your other web apps, bookmarks, passwords and history will NOT be removed.";
            app.InfoAdviceColor = LevelColor(app.InfoAdviceLevel);
            app.InfoAdviceLabel = LevelLabel(app.InfoAdviceLevel);
            return;
        }

        // 2) Knowledge base rules
        foreach (var rule in Rules)
        {
            if (Regex.IsMatch(app.DisplayName, rule.Pattern, RegexOptions.IgnoreCase))
            {
                app.InfoCategory = rule.Category;
                app.InfoDescription = rule.Description;
                app.InfoAdviceLevel = rule.Level;
                app.InfoAdvice = rule.Advice;
                break;
            }
        }

        // 3) Heuristic fallback for unknown apps
        if (string.IsNullOrWhiteSpace(app.InfoDescription))
        {
            string n = app.DisplayName;
            if (Regex.IsMatch(n, @"driver|chipset|firmware", RegexOptions.IgnoreCase))
            {
                app.InfoCategory = "Hardware Driver";
                app.InfoDescription = $"Driver software from {app.Publisher} that makes a hardware device in this PC work.";
                app.InfoAdviceLevel = Keep;
                app.InfoAdvice = "Keep — removing a driver can disable the related device.";
            }
            else if (Regex.IsMatch(n, @"runtime|redistributable|framework|library|SDK", RegexOptions.IgnoreCase))
            {
                app.InfoCategory = "Runtime / Library";
                app.InfoDescription = $"Shared component from {app.Publisher} that other programs may need in order to run.";
                app.InfoAdviceLevel = Keep;
                app.InfoAdvice = "Keep unless you are sure nothing depends on it.";
            }
            else if (Regex.IsMatch(n, @"update|helper|service|agent", RegexOptions.IgnoreCase))
            {
                app.InfoCategory = "Background Helper";
                app.InfoDescription = $"Background updater/helper service from {app.Publisher} for one of its main apps.";
                app.InfoAdviceLevel = Check;
                app.InfoAdvice = "Usually removed automatically with its main app; check before removing alone.";
            }
            else
            {
                app.InfoCategory = "Application";
                app.InfoDescription = $"Program published by {app.Publisher}" +
                                      (app.InstallDate != "—" ? $", installed on {app.InstallDate}." : ".") +
                                      " Click 'ℹ Info' → Google for full details about what it does.";
                app.InfoAdviceLevel = Check;
                app.InfoAdvice = "Safe to remove if you don't recognize or use it (search Google first if unsure).";
            }
        }

        // 4) Browser core: extra warning about dependent web apps
        if (app.IsBrowserCore)
        {
            app.InfoExtraNote = app.RelatedWebAppCount > 0
                ? $"⚠ Uninstalling {app.HostBrowserName} will ALSO remove {app.RelatedWebAppCount} web app(s) installed from it. To remove just one, uninstall that web app (teal '{ShortName(app.HostBrowserName)} Web App' badge) instead."
                : $"⚠ This removes the whole {app.HostBrowserName} browser.";
        }

        app.InfoAdviceColor = LevelColor(app.InfoAdviceLevel);
        app.InfoAdviceLabel = LevelLabel(app.InfoAdviceLevel);
    }

    private static string ShortName(string browser) =>
        browser == "Google Chrome" ? "Chrome" : browser == "Microsoft Edge" ? "Edge" : browser;

    public static string LevelColor(string level) => level switch
    {
        Keep => "#B91C1C",
        Safe => "#15803D",
        _ => "#B8860B"
    };

    public static string LevelLabel(string level) => level switch
    {
        Keep => "KEEP — Needed",
        Safe => "SAFE to remove",
        _ => "CHECK before removing"
    };
}
