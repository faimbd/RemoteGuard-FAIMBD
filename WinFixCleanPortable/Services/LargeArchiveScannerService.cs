using System.IO;
using WinFixCleanPortable.Models;

namespace WinFixCleanPortable.Services;

public class LargeArchiveScanOptions
{
    public List<string> RootPaths { get; set; } = new();
    public long MinLargeFileBytes { get; set; } = 100L * 1024 * 1024; // 100 MB default
    public bool IncludeLargeFiles { get; set; } = true;
    public bool IncludeArchives { get; set; } = true;
    public bool IncludeDiskImages { get; set; } = true;
    public bool IncludeInstallersAndMedia { get; set; } = false;
    public bool SkipSystemFolders { get; set; } = true;
    public long MinArchiveSizeBytes { get; set; } = 0; // List ALL zip/iso by default regardless of size!
}

public class ScanProgressInfo
{
    public int FilesScanned { get; set; }
    public int MatchesFound { get; set; }
    public long TotalMatchedBytes { get; set; }
    public string CurrentFolder { get; set; } = string.Empty;
    public string StageText { get; set; } = string.Empty;
}

public class LargeArchiveScannerService
{
    public static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".zst", ".lzma", ".zipx"
    };

    public static readonly HashSet<string> DiskImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".img", ".vhd", ".vhdx", ".wim", ".esd", ".dmg", ".bin", ".cue", ".nrg", ".mdf", ".vmdk", ".ova"
    };

    public static readonly HashSet<string> InstallerMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msp", ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v"
    };

    private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "Windows", "ProgramData",
        "Program Files", "Program Files (x86)", "Recovery", "PerfLogs",
        "$WinREAgent", "Config.Msi", "MSOCache", "Windows.old", "WinSxS"
    };

    public async Task<List<ScannedFileItem>> ScanAsync(
        LargeArchiveScanOptions options,
        IProgress<ScanProgressInfo>? progress,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var results = new List<ScannedFileItem>();
            var roots = NormalizeRoots(options.RootPaths);
            int filesScanned = 0;
            long totalMatchedBytes = 0;
            var lastReport = DateTime.UtcNow;

            var stack = new Stack<string>();
            foreach (var r in roots)
            {
                if (Directory.Exists(r))
                    stack.Push(r);
            }

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var currentDir = stack.Pop();

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(currentDir);
                }
                catch
                {
                    continue;
                }

                foreach (var filePath in files)
                {
                    ct.ThrowIfCancellationRequested();
                    filesScanned++;

                    try
                    {
                        var fi = new FileInfo(filePath);
                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;

                        long len = fi.Length;
                        string ext = fi.Extension;

                        bool isDiskImg = DiskImageExtensions.Contains(ext);
                        bool isArchive = ArchiveExtensions.Contains(ext);
                        bool isInstallerMedia = InstallerMediaExtensions.Contains(ext) && len >= 20L * 1024 * 1024;
                        bool isLarge = len >= options.MinLargeFileBytes;

                        bool match =
                            (options.IncludeDiskImages && isDiskImg && len >= options.MinArchiveSizeBytes) ||
                            (options.IncludeArchives && isArchive && len >= options.MinArchiveSizeBytes) ||
                            (options.IncludeLargeFiles && isLarge) ||
                            (options.IncludeInstallersAndMedia && isInstallerMedia);

                        if (match)
                        {
                            var (category, color) = Classify(ext, isDiskImg, isArchive, isInstallerMedia);
                            results.Add(new ScannedFileItem
                            {
                                FileName = fi.Name,
                                FullPath = fi.FullName,
                                DirectoryPath = fi.DirectoryName ?? string.Empty,
                                Extension = string.IsNullOrEmpty(ext) ? "(none)" : ext.ToUpperInvariant(),
                                Category = category,
                                BadgeColor = color,
                                SizeBytes = len,
                                LastModified = fi.LastWriteTime,
                                IsArchive = isArchive,
                                IsDiskImage = isDiskImg,
                                IsLarge = isLarge,
                                IsInstallerOrMedia = isInstallerMedia
                            });
                            totalMatchedBytes += len;
                        }
                    }
                    catch
                    {
                        // Skip inaccessible or deleted files mid-scan
                    }

                    if ((filesScanned & 255) == 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 120)
                    {
                        lastReport = DateTime.UtcNow;
                        progress?.Report(new ScanProgressInfo
                        {
                            FilesScanned = filesScanned,
                            MatchesFound = results.Count,
                            TotalMatchedBytes = totalMatchedBytes,
                            CurrentFolder = currentDir,
                            StageText = $"Scanning files... ({filesScanned:N0} checked)"
                        });
                    }
                }

                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var di = new DirectoryInfo(subDir);
                            if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                                continue;

                            if (options.SkipSystemFolders && ShouldSkipSystemFolder(di))
                                continue;

                            stack.Push(subDir);
                        }
                        catch
                        {
                            // Skip inaccessible directory
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible directory
                }
            }

            results.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

            progress?.Report(new ScanProgressInfo
            {
                FilesScanned = filesScanned,
                MatchesFound = results.Count,
                TotalMatchedBytes = totalMatchedBytes,
                CurrentFolder = string.Empty,
                StageText = $"Completed: {results.Count:N0} items found ({filesScanned:N0} files scanned)"
            });

            return results;
        }, ct);
    }

    private static (string Category, string Color) Classify(string ext, bool isDiskImg, bool isArchive, bool isInstallerMedia)
    {
        if (isDiskImg)
            return ("ISO / Disk Image", "#EC4899"); // Pink/Magenta
        if (isArchive)
            return ("ZIP / Archive", "#F59E0B"); // Amber
        if (isInstallerMedia)
        {
            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".msix", StringComparison.OrdinalIgnoreCase))
                return ("Installer", "#8B5CF6"); // Purple
            return ("Large Video", "#06B6D4"); // Cyan
        }
        return ("Large File", "#3B82F6"); // Blue
    }

    private static bool ShouldSkipSystemFolder(DirectoryInfo di)
    {
        if (SystemFolderNames.Contains(di.Name))
        {
            // Only skip root-level OS directories like C:\Windows, C:\Program Files, or any drive's $Recycle.Bin / System Volume Information
            if (di.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                di.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                return true;

            if (di.Parent?.Parent == null) // Direct child of drive root (e.g., C:\Windows)
                return true;
        }
        return false;
    }

    public static List<string> NormalizeRoots(IEnumerable<string> paths)
    {
        var cleaned = paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)
            .ToList();

        var result = new List<string>();
        foreach (var path in cleaned)
        {
            bool isNested = result.Any(parent =>
                path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!isNested)
                result.Add(path.EndsWith(':') ? path + Path.DirectorySeparatorChar : path);
        }
        return result;
    }
}
