using System.IO;
using System.Security.Cryptography;
using WinFixCleanPortable.Helpers;
using WinFixCleanPortable.Models;

namespace WinFixCleanPortable.Services;

public class DuplicateScanOptions
{
    public List<string> TargetFolders { get; set; } = new();
    public long MinFileSizeBytes { get; set; } = 1024; // 1 KB default to ignore 0-byte empties
    public bool IncludeSubfolders { get; set; } = true;
    public bool SkipHiddenAndSystem { get; set; } = true;
    public HashSet<string>? AllowedExtensions { get; set; } // null = all files
}

public class DuplicateScanProgress
{
    public string Stage { get; set; } = string.Empty;
    public int FilesScanned { get; set; }
    public int CandidatesCount { get; set; }
    public int HashedCount { get; set; }
    public int DuplicateGroups { get; set; }
    public long WastedBytes { get; set; }
    public double PercentComplete { get; set; }
}

public class DuplicateFinderService
{
    private static readonly string[] GroupPalette =
    {
        "#1E293B", "#172554", "#1E1B4B", "#14262E", "#271F33", "#1F2937"
    };

    private static readonly HashSet<string> SkippedSystemDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "Windows", "WinSxS"
    };

    public async Task<(List<DuplicateFileItem> Items, int GroupCount, long WastedBytes)> FindDuplicatesAsync(
        DuplicateScanOptions options,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var roots = LargeArchiveScannerService.NormalizeRoots(options.TargetFolders);
            var sizeBuckets = new Dictionary<long, List<FileInfo>>();
            int filesScanned = 0;
            var lastReport = DateTime.UtcNow;

            // STAGE 1: Collect files and bucket by exact byte size
            var stack = new Stack<string>();
            foreach (var r in roots)
                stack.Push(r);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir);
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

                        if (options.SkipHiddenAndSystem &&
                            ((fi.Attributes & FileAttributes.System) != 0 || (fi.Attributes & FileAttributes.Hidden) != 0))
                            continue;

                        if (fi.Length < options.MinFileSizeBytes || fi.Length == 0)
                            continue;

                        if (options.AllowedExtensions != null && options.AllowedExtensions.Count > 0 &&
                            !options.AllowedExtensions.Contains(fi.Extension))
                            continue;

                        if (!sizeBuckets.TryGetValue(fi.Length, out var list))
                        {
                            list = new List<FileInfo>(2);
                            sizeBuckets[fi.Length] = list;
                        }
                        list.Add(fi);
                    }
                    catch
                    {
                        // Ignore unreadable file
                    }

                    if ((filesScanned & 255) == 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 120)
                    {
                        lastReport = DateTime.UtcNow;
                        progress?.Report(new DuplicateScanProgress
                        {
                            Stage = $"Stage 1/3: Scanning folders... ({filesScanned:N0} files)",
                            FilesScanned = filesScanned,
                            PercentComplete = 15
                        });
                    }
                }

                if (options.IncludeSubfolders)
                {
                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                var di = new DirectoryInfo(sub);
                                if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                                    continue;
                                if (options.SkipHiddenAndSystem && (di.Attributes & FileAttributes.System) != 0)
                                    continue;
                                if (SkippedSystemDirs.Contains(di.Name) &&
                                    (di.Parent?.Parent == null || di.Name.StartsWith('$') || di.Name.StartsWith("System Volume", StringComparison.OrdinalIgnoreCase)))
                                    continue;

                                stack.Push(sub);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }

            // Filter only sizes with 2+ files
            var sizeCandidateGroups = sizeBuckets.Values.Where(g => g.Count > 1).ToList();
            int totalSizeCandidates = sizeCandidateGroups.Sum(g => g.Count);

            progress?.Report(new DuplicateScanProgress
            {
                Stage = $"Stage 2/3: Quick partial-hash check on {totalSizeCandidates:N0} same-size candidates...",
                FilesScanned = filesScanned,
                CandidatesCount = totalSizeCandidates,
                PercentComplete = 35
            });

            // STAGE 2: Partial Hash (first 64KB + last 64KB)
            var partialCandidates = new List<List<FileInfo>>();
            int processedPartial = 0;

            foreach (var group in sizeCandidateGroups)
            {
                ct.ThrowIfCancellationRequested();
                var partialMap = new Dictionary<string, List<FileInfo>>();

                foreach (var fi in group)
                {
                    ct.ThrowIfCancellationRequested();
                    processedPartial++;

                    var pHash = ComputePartialHash(fi.FullName, fi.Length);
                    if (pHash != null)
                    {
                        if (!partialMap.TryGetValue(pHash, out var plist))
                        {
                            plist = new List<FileInfo>(2);
                            partialMap[pHash] = plist;
                        }
                        plist.Add(fi);
                    }

                    if ((processedPartial & 31) == 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 120)
                    {
                        lastReport = DateTime.UtcNow;
                        double pct = totalSizeCandidates > 0 ? 35.0 + (processedPartial * 30.0 / totalSizeCandidates) : 65.0;
                        progress?.Report(new DuplicateScanProgress
                        {
                            Stage = $"Stage 2/3: Quick fingerprinting ({processedPartial:N0}/{totalSizeCandidates:N0})...",
                            FilesScanned = filesScanned,
                            CandidatesCount = totalSizeCandidates,
                            HashedCount = processedPartial,
                            PercentComplete = pct
                        });
                    }
                }

                foreach (var pg in partialMap.Values)
                {
                    if (pg.Count > 1)
                        partialCandidates.Add(pg);
                }
            }

            // STAGE 3: Full SHA-256 verification
            int totalFullCandidates = partialCandidates.Sum(g => g.Count);
            int processedFull = 0;
            var confirmedGroups = new List<(string Hash, List<FileInfo> Files)>();

            foreach (var pGroup in partialCandidates)
            {
                ct.ThrowIfCancellationRequested();
                var fullMap = new Dictionary<string, List<FileInfo>>();

                foreach (var fi in pGroup)
                {
                    ct.ThrowIfCancellationRequested();
                    processedFull++;

                    var fHash = ComputeFullSha256(fi.FullName, ct);
                    if (fHash != null)
                    {
                        if (!fullMap.TryGetValue(fHash, out var flist))
                        {
                            flist = new List<FileInfo>(2);
                            fullMap[fHash] = flist;
                        }
                        flist.Add(fi);
                    }

                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 120)
                    {
                        lastReport = DateTime.UtcNow;
                        double pct = totalFullCandidates > 0 ? 65.0 + (processedFull * 35.0 / totalFullCandidates) : 99.0;
                        progress?.Report(new DuplicateScanProgress
                        {
                            Stage = $"Stage 3/3: SHA-256 verifying ({processedFull:N0}/{totalFullCandidates:N0})...",
                            FilesScanned = filesScanned,
                            CandidatesCount = totalFullCandidates,
                            HashedCount = processedFull,
                            DuplicateGroups = confirmedGroups.Count,
                            PercentComplete = pct
                        });
                    }
                }

                foreach (var kvp in fullMap)
                {
                    if (kvp.Value.Count > 1)
                        confirmedGroups.Add((kvp.Key, kvp.Value));
                }
            }

            // Order groups by wasted space descending
            confirmedGroups.Sort((a, b) =>
            {
                long wasteA = a.Files[0].Length * (a.Files.Count - 1);
                long wasteB = b.Files[0].Length * (b.Files.Count - 1);
                return wasteB.CompareTo(wasteA);
            });

            var resultItems = new List<DuplicateFileItem>();
            long totalWasted = 0;
            int groupId = 1;

            foreach (var (hash, files) in confirmedGroups)
            {
                files.Sort((a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime));
                long unitSize = files[0].Length;
                long groupWaste = unitSize * (files.Count - 1);
                totalWasted += groupWaste;

                string shortHash = hash.Length >= 10 ? hash[..10] : hash;
                string header = $"Group #{groupId}  •  {files.Count} identical copies  •  {FormatHelper.FormatBytes(unitSize)} each  •  Wasted: {FormatHelper.FormatBytes(groupWaste)}  (SHA-256: {shortHash})";
                string color = GroupPalette[(groupId - 1) % GroupPalette.Length];

                foreach (var fi in files)
                {
                    resultItems.Add(new DuplicateFileItem
                    {
                        GroupId = groupId,
                        GroupHeader = header,
                        GroupColor = color,
                        HashShort = shortHash,
                        FileName = fi.Name,
                        FullPath = fi.FullName,
                        DirectoryPath = fi.DirectoryName ?? string.Empty,
                        Extension = string.IsNullOrEmpty(fi.Extension) ? "(none)" : fi.Extension.ToUpperInvariant(),
                        SizeBytes = fi.Length,
                        LastModified = fi.LastWriteTime,
                        GroupCount = files.Count,
                        IsSelected = false
                    });
                }
                groupId++;
            }

            progress?.Report(new DuplicateScanProgress
            {
                Stage = $"Done! Found {confirmedGroups.Count:N0} duplicate groups ({FormatHelper.FormatBytes(totalWasted)} wasted).",
                FilesScanned = filesScanned,
                CandidatesCount = totalFullCandidates,
                HashedCount = processedFull,
                DuplicateGroups = confirmedGroups.Count,
                WastedBytes = totalWasted,
                PercentComplete = 100
            });

            return (resultItems, confirmedGroups.Count, totalWasted);
        }, ct);
    }

    private static string? ComputePartialHash(string filePath, long fileLength)
    {
        const int chunkSize = 65536; // 64 KB
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
            using var sha = SHA256.Create();

            if (fileLength <= chunkSize * 2)
            {
                var hash = sha.ComputeHash(fs);
                return Convert.ToHexString(hash);
            }

            var buffer = new byte[chunkSize * 2];
            int readHead = fs.Read(buffer, 0, chunkSize);
            fs.Seek(-chunkSize, SeekOrigin.End);
            int readTail = fs.Read(buffer, chunkSize, chunkSize);
            var combined = sha.ComputeHash(buffer, 0, readHead + readTail);
            return Convert.ToHexString(combined);
        }
        catch
        {
            return null;
        }
    }

    private static string? ComputeFullSha256(string filePath, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 131072);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072];
            int bytesRead;
            while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, bytesRead);
            }
            return Convert.ToHexString(sha.GetCurrentHash());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
