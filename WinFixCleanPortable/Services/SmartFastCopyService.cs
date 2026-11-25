using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using WinFixCleanPortable.Helpers;
using WinFixCleanPortable.Models;

namespace WinFixCleanPortable.Services;

public class SmartFastCopyService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico"
    };

    private static readonly HashSet<string> SkippedSystemDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information"
    };

    /// <summary>
    /// Phase 1: Scans all source files + indexes destination files for duplicates and quality conflicts.
    /// Returns a complete CopyPlan where every file has an automatic recommendation and user-editable decision.
    /// </summary>
    public async Task<List<CopyPlanItem>> BuildPlanAsync(
        FastCopyOptions options,
        IProgress<FastCopyProgress>? progress,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(options.DestinationFolder))
                throw new InvalidOperationException("Destination folder is required.");

            string destRoot = Path.GetFullPath(options.DestinationFolder);

            // Step 1: Index existing destination files by size (for fast zero-duplicate hash check)
            progress?.Report(new FastCopyProgress { Stage = "Analyzing destination folder for existing files & duplicates...", PercentComplete = 10 });
            var destBySize = new Dictionary<long, List<string>>();
            var destHashCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (options.SkipContentDuplicatesByHash && Directory.Exists(destRoot))
            {
                foreach (var df in SafeEnumerateFiles(destRoot, ct))
                {
                    try
                    {
                        var fi = new FileInfo(df);
                        if (!destBySize.TryGetValue(fi.Length, out var list))
                        {
                            list = new List<string>(1);
                            destBySize[fi.Length] = list;
                        }
                        list.Add(fi.FullName);
                    }
                    catch { }
                }
            }

            // Step 2: Collect all source files preserving relative paths
            progress?.Report(new FastCopyProgress { Stage = "Discovering all source files (zero-missing crawl)...", PercentComplete = 25 });
            var discoveredSources = new List<(string FullPath, string RelativePath)>();
            var seenSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool multipleFolders = options.SourcePaths.Count(Directory.Exists) > 1;

            foreach (var rawSrc in options.SourcePaths)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(rawSrc))
                {
                    string full = Path.GetFullPath(rawSrc);
                    if (seenSourcePaths.Add(full))
                        discoveredSources.Add((full, Path.GetFileName(full)));
                }
                else if (Directory.Exists(rawSrc))
                {
                    string srcRoot = Path.GetFullPath(rawSrc).TrimEnd(Path.DirectorySeparatorChar);
                    string rootFolderName = Path.GetFileName(srcRoot);
                    if (string.IsNullOrWhiteSpace(rootFolderName))
                        rootFolderName = srcRoot.Replace(":", "").Replace("\\", "");

                    foreach (var file in SafeEnumerateFiles(srcRoot, ct))
                    {
                        string full = Path.GetFullPath(file);
                        if (!seenSourcePaths.Add(full))
                            continue;

                        string rel;
                        if (!options.PreserveFolderStructure)
                        {
                            rel = Path.GetFileName(full);
                        }
                        else
                        {
                            rel = Path.GetRelativePath(srcRoot, full);
                            if (multipleFolders)
                                rel = Path.Combine(rootFolderName, rel);
                        }

                        discoveredSources.Add((full, rel));
                    }
                }
            }

            // Step 3: Evaluate every file for exact duplicates & quality conflicts
            var plan = new List<CopyPlanItem>(discoveredSources.Count);
            // Track hashes already planned within this batch so source-to-source duplicates are also eliminated in O(1)!
            var plannedBatchHashes = new Dictionary<string, CopyPlanItem>(StringComparer.OrdinalIgnoreCase);
            var plannedBySize = new Dictionary<long, List<CopyPlanItem>>();
            var plannedItemHashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var plannedByDestPath = new Dictionary<string, CopyPlanItem>(StringComparer.OrdinalIgnoreCase);
            var destKnownHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int total = discoveredSources.Count;
            var lastReport = DateTime.UtcNow;

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (srcFull, relPath) = discoveredSources[i];

                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 120)
                {
                    lastReport = DateTime.UtcNow;
                    double pct = 30.0 + (total > 0 ? (i * 68.0 / total) : 68.0);
                    progress?.Report(new FastCopyProgress
                    {
                        Stage = $"Comparing duplicates & file quality ({i + 1:N0}/{total:N0})...",
                        CompletedFiles = i,
                        TotalFiles = total,
                        PercentComplete = pct,
                        CurrentFile = Path.GetFileName(srcFull)
                    });
                }

                FileInfo srcInfo;
                try
                {
                    srcInfo = new FileInfo(srcFull);
                    if (!srcInfo.Exists) continue;
                }
                catch
                {
                    continue;
                }

                string targetPath = Path.GetFullPath(Path.Combine(destRoot, relPath));

                // Skip copying a file onto itself!
                if (srcFull.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var (srcPixels, srcQualLabel) = InspectQuality(srcFull, srcInfo.Extension);

                var item = new CopyPlanItem
                {
                    SourcePath = srcFull,
                    DestinationPath = targetPath,
                    RelativePath = relPath,
                    FileName = srcInfo.Name,
                    SourceSize = srcInfo.Length,
                    SourceModified = srcInfo.LastWriteTime,
                    SourcePixels = srcPixels,
                    SourceQualityInfo = srcQualLabel
                };

                // Check 1: Does an exact SHA-256 content duplicate already exist in destination?
                string? srcHash = null;
                if (options.SkipContentDuplicatesByHash)
                {
                    if (destBySize.TryGetValue(srcInfo.Length, out var sameSizeDestFiles))
                    {
                        srcHash = ComputeSha256(srcFull, ct);
                        if (srcHash != null)
                        {
                            foreach (var df in sameSizeDestFiles)
                            {
                                string? dHash = destHashCache.GetOrAdd(df, p => ComputeSha256(p, ct) ?? string.Empty);
                                if (!string.IsNullOrEmpty(dHash))
                                    destKnownHashes.TryAdd(dHash, df);
                            }

                            if (destKnownHashes.TryGetValue(srcHash, out var existingDupPath))
                            {
                                item.IsExactContentDuplicate = true;
                                item.ExistingDuplicatePath = existingDupPath;
                                item.Decision = CopyDecision.SkipIdenticalDuplicate;
                                item.RecommendedWinner = "Skip (100% Identical)";
                                item.Reason = $"Exact SHA-256 match already exists at destination: {Path.GetFileName(existingDupPath)}";
                                item.Status = "Duplicate (Skip)";
                                item.StatusColor = "#64748B";
                                plan.Add(item);
                                continue;
                            }
                        }
                    }

                    // Also check source-to-source duplicate within the same copy batch (O(1) size bucket + lazy SHA-256)
                    if (plannedBySize.TryGetValue(srcInfo.Length, out var sameSizePlanned))
                    {
                        srcHash ??= ComputeSha256(srcFull, ct);
                        if (srcHash != null)
                        {
                            foreach (var prev in sameSizePlanned)
                            {
                                if (!plannedItemHashCache.TryGetValue(prev.SourcePath, out var prevHash))
                                {
                                    prevHash = ComputeSha256(prev.SourcePath, ct) ?? string.Empty;
                                    plannedItemHashCache[prev.SourcePath] = prevHash;
                                    if (!string.IsNullOrEmpty(prevHash))
                                        plannedBatchHashes.TryAdd(prevHash, prev);
                                }
                            }

                            if (plannedBatchHashes.TryGetValue(srcHash, out var prevBatchItem))
                            {
                                item.IsExactContentDuplicate = true;
                                item.ExistingDuplicatePath = prevBatchItem.SourcePath;
                                item.Decision = CopyDecision.SkipIdenticalDuplicate;
                                item.RecommendedWinner = "Skip (Duplicate in Source)";
                                item.Reason = $"Exact SHA-256 duplicate of earlier source file: {prevBatchItem.RelativePath}";
                                item.Status = "Duplicate (Skip)";
                                item.StatusColor = "#64748B";
                                plan.Add(item);
                                continue;
                            }
                        }
                    }
                }

                // Check 2: Same destination filename already exists (either on disk or earlier in flattened batch!)
                bool destExistsOnDisk = File.Exists(targetPath);
                bool destCollisionInBatch = plannedByDestPath.TryGetValue(targetPath, out var earlierBatchItem);

                if (destExistsOnDisk)
                {
                    var destInfo = new FileInfo(targetPath);
                    var (destPixels, destQualLabel) = InspectQuality(targetPath, destInfo.Extension);
                    item.DestinationExists = true;
                    item.DestSize = destInfo.Length;
                    item.DestModified = destInfo.LastWriteTime;
                    item.DestPixels = destPixels;
                    item.DestQualityInfo = destQualLabel;

                    // Quick check: if same size and hash wasn't computed yet, verify if byte-identical
                    if (srcInfo.Length == destInfo.Length)
                    {
                        srcHash ??= ComputeSha256(srcFull, ct);
                        string? dHash = destHashCache.GetOrAdd(targetPath, p => ComputeSha256(p, ct) ?? string.Empty);
                        if (!string.IsNullOrEmpty(srcHash) && srcHash == dHash)
                        {
                            item.IsExactContentDuplicate = true;
                            item.ExistingDuplicatePath = targetPath;
                            item.Decision = CopyDecision.SkipIdenticalDuplicate;
                            item.RecommendedWinner = "Skip (100% Identical)";
                            item.Reason = "Destination already has the exact same file (SHA-256 verified).";
                            item.Status = "Identical (Skip)";
                            item.StatusColor = "#64748B";
                            plan.Add(item);
                            continue;
                        }
                    }

                    ApplyConflictDecision(item, options.Policy);
                }
                else if (destCollisionInBatch && earlierBatchItem != null)
                {
                    // Two DIFFERENT source files map to the same destination filename (e.g. flattened mode!)
                    item.DestinationExists = true;
                    item.DestSize = earlierBatchItem.SourceSize;
                    item.DestModified = earlierBatchItem.SourceModified;
                    item.DestPixels = earlierBatchItem.SourcePixels;
                    item.DestQualityInfo = earlierBatchItem.SourceQualityInfo;
                    ApplyConflictDecision(item, options.Policy);

                    if (item.Decision == CopyDecision.UseSourceReplaceDest)
                    {
                        // New source beats earlier batch item in quality! Mark earlier as skipped so only the best quality copies.
                        earlierBatchItem.Decision = CopyDecision.KeepDestinationSkip;
                        earlierBatchItem.Status = "Lower Quality (Skip)";
                        earlierBatchItem.StatusColor = "#64748B";
                        earlierBatchItem.Reason = $"Replaced by higher-quality source: {item.SourcePath}";
                        plannedByDestPath[targetPath] = item;
                    }
                }
                else
                {
                    item.Decision = CopyDecision.CopyNew;
                    item.RecommendedWinner = "Copy (New File)";
                    item.Reason = "New file — will be copied and verified.";
                    item.Status = "Ready to Copy";
                    item.StatusColor = "#3B82F6";
                    plannedByDestPath[targetPath] = item;
                }

                if (item.Decision != CopyDecision.SkipIdenticalDuplicate && item.Decision != CopyDecision.KeepDestinationSkip)
                {
                    if (!plannedBySize.TryGetValue(srcInfo.Length, out var sList))
                    {
                        sList = new List<CopyPlanItem>(1);
                        plannedBySize[srcInfo.Length] = sList;
                    }
                    sList.Add(item);

                    if (srcHash != null)
                    {
                        plannedItemHashCache[item.SourcePath] = srcHash;
                        plannedBatchHashes.TryAdd(srcHash, item);
                    }
                }

                plan.Add(item);
            }

            progress?.Report(new FastCopyProgress
            {
                Stage = $"Analysis complete: {plan.Count:N0} files planned.",
                CompletedFiles = total,
                TotalFiles = total,
                PercentComplete = 100
            });

            return plan;
        }, ct);
    }

    public static void ApplyConflictDecision(CopyPlanItem item, ConflictPolicy policy)
    {
        var (sourceBetter, qualityReason) = CompareQuality(
            item.SourcePixels, item.SourceSize, item.SourceModified,
            item.DestPixels, item.DestSize, item.DestModified ?? DateTime.MinValue);

        item.RecommendedWinner = sourceBetter ? "Source (Better Quality)" : "Destination (Better Quality)";

        switch (policy)
        {
            case ConflictPolicy.KeepBetterQuality:
            case ConflictPolicy.AskUserToChoose:
                if (sourceBetter)
                {
                    item.Decision = CopyDecision.UseSourceReplaceDest;
                    item.Reason = $"Better quality in Source: {qualityReason}";
                    item.Status = "Replace (Better Quality)";
                    item.StatusColor = "#10B981";
                }
                else
                {
                    item.Decision = CopyDecision.KeepDestinationSkip;
                    item.Reason = $"Better/equal quality in Destination: {qualityReason}";
                    item.Status = "Keep Dest (Better)";
                    item.StatusColor = "#F59E0B";
                }
                break;

            case ConflictPolicy.KeepLargerSize:
                if (item.SourceSize > item.DestSize)
                {
                    item.Decision = CopyDecision.UseSourceReplaceDest;
                    item.Reason = $"Source is larger ({FormatHelper.FormatBytes(item.SourceSize)} > {FormatHelper.FormatBytes(item.DestSize)})";
                    item.Status = "Replace (Larger)";
                    item.StatusColor = "#10B981";
                }
                else
                {
                    item.Decision = CopyDecision.KeepDestinationSkip;
                    item.Reason = $"Destination is larger or equal ({FormatHelper.FormatBytes(item.DestSize)})";
                    item.Status = "Keep Dest (Larger)";
                    item.StatusColor = "#F59E0B";
                }
                break;

            case ConflictPolicy.KeepNewerDate:
                if (item.SourceModified > (item.DestModified ?? DateTime.MinValue))
                {
                    item.Decision = CopyDecision.UseSourceReplaceDest;
                    item.Reason = $"Source is newer ({item.SourceModified:yyyy-MM-dd HH:mm})";
                    item.Status = "Replace (Newer)";
                    item.StatusColor = "#10B981";
                }
                else
                {
                    item.Decision = CopyDecision.KeepDestinationSkip;
                    item.Reason = $"Destination is newer or equal ({item.DestModified:yyyy-MM-dd HH:mm})";
                    item.Status = "Keep Dest (Newer)";
                    item.StatusColor = "#F59E0B";
                }
                break;

            case ConflictPolicy.SkipDuplicatesAndExisting:
                item.Decision = CopyDecision.KeepDestinationSkip;
                item.Reason = "Policy: Skip existing files at destination.";
                item.Status = "Skip (Exists)";
                item.StatusColor = "#64748B";
                break;

            case ConflictPolicy.OverwriteExisting:
                item.Decision = CopyDecision.UseSourceReplaceDest;
                item.Reason = "Policy: Always overwrite destination with source.";
                item.Status = "Overwrite";
                item.StatusColor = "#3B82F6";
                break;

            case ConflictPolicy.KeepBothRename:
                item.Decision = CopyDecision.KeepBothRename;
                item.Reason = "Policy: Keep both files by auto-renaming incoming file.";
                item.Status = "Rename (Keep Both)";
                item.StatusColor = "#8B5CF6";
                break;
        }
    }

    /// <summary>
    /// Phase 2: Executes the plan using multi-threaded parallel buffered I/O, atomic writes, automatic retries,
    /// optional SHA-256 post-copy verification, and an end-of-transfer missing-file audit.
    /// </summary>
    public async Task<FastCopySummary> ExecutePlanAsync(
        List<CopyPlanItem> plan,
        FastCopyOptions options,
        IProgress<FastCopyProgress>? progress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var summary = new FastCopySummary { TotalScanned = plan.Count };
        var errors = new ConcurrentBag<string>();
        var renameLock = new object();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var activeItems = plan.Where(p =>
            p.Decision == CopyDecision.CopyNew ||
            p.Decision == CopyDecision.UseSourceReplaceDest ||
            p.Decision == CopyDecision.KeepBothRename).ToList();

        summary.SkippedDuplicates = plan.Count(p => p.Decision == CopyDecision.SkipIdenticalDuplicate);
        summary.SkippedKeptDestination = plan.Count(p => p.Decision == CopyDecision.KeepDestinationSkip);

        long totalBytesToCopy = activeItems.Sum(x => x.SourceSize);
        long bytesTransferred = 0;
        int completedCount = 0;
        int copiedNew = 0;
        int replaced = 0;
        int renamed = 0;
        int failed = 0;
        long lastReportTicks = 0;

        int threads = Math.Clamp(options.ThreadCount, 1, 16);
        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = threads,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(activeItems, parallelOpts, async (item, token) =>
        {
            string finalDest = item.DestinationPath;
            if (item.Decision == CopyDecision.KeepBothRename)
            {
                lock (renameLock)
                {
                    finalDest = GetUniqueDestinationPath(item.DestinationPath, reservedPaths);
                    reservedPaths.Add(finalDest);
                    var dir = Path.GetDirectoryName(finalDest);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                }
                item.DestinationPath = finalDest;
            }

            item.Status = "Copying...";
            item.StatusColor = "#3B82F6";

            bool success = false;
            string? lastError = null;

            for (int attempt = 1; attempt <= Math.Max(1, options.MaxRetries); attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var destDir = Path.GetDirectoryName(finalDest);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);

                    string tempDest = finalDest + $".wfc_{Environment.CurrentManagedThreadId}_{Guid.NewGuid():N}.tmp";

                    try
                    {
                        await CopyFileBufferedAsync(
                            item.SourcePath,
                            tempDest,
                            chunkBytes =>
                            {
                                long cur = Interlocked.Add(ref bytesTransferred, chunkBytes);
                                long nowTicks = Stopwatch.GetTimestamp();
                                long prev = Interlocked.Read(ref lastReportTicks);
                                if ((nowTicks - prev) > Stopwatch.Frequency / 8) // ~8 updates/sec
                                {
                                    if (Interlocked.CompareExchange(ref lastReportTicks, nowTicks, prev) == prev)
                                    {
                                        double secs = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                                        double pct = totalBytesToCopy > 0
                                            ? Math.Min(99.0, cur * 100.0 / totalBytesToCopy)
                                            : (activeItems.Count > 0 ? completedCount * 100.0 / activeItems.Count : 100.0);
                                        progress?.Report(new FastCopyProgress
                                        {
                                            Stage = $"Copying with {threads} threads ({completedCount:N0}/{activeItems.Count:N0} files)...",
                                            CompletedFiles = completedCount,
                                            TotalFiles = activeItems.Count,
                                            BytesTransferred = cur,
                                            TotalBytesToTransfer = totalBytesToCopy,
                                            SpeedBytesPerSec = cur / secs,
                                            PercentComplete = pct,
                                            CurrentFile = item.FileName
                                        });
                                    }
                                }
                            },
                            token);

                        // Verify integrity BEFORE finalizing (zero corrupt/missing guarantee!)
                        if (options.VerifyAfterCopy)
                        {
                            item.Status = "Verifying...";
                            string? srcSha = ComputeSha256(item.SourcePath, token);
                            string? tmpSha = ComputeSha256(tempDest, token);
                            if (string.IsNullOrEmpty(srcSha) || srcSha != tmpSha)
                                throw new IOException("Post-copy SHA-256 verification mismatch!");
                        }
                        else
                        {
                            if (new FileInfo(tempDest).Length != item.SourceSize)
                                throw new IOException("Post-copy file size mismatch!");
                        }

                        // Preserve original timestamps
                        File.SetLastWriteTimeUtc(tempDest, File.GetLastWriteTimeUtc(item.SourcePath));

                        // Atomic swap into final destination path
                        File.Move(tempDest, finalDest, overwrite: true);

                        // If Move mode (Cut & Paste), delete source ONLY after verified destination exists!
                        if (options.IsMoveMode && File.Exists(finalDest))
                        {
                            try { File.Delete(item.SourcePath); } catch { }
                        }

                        success = true;
                        break;
                    }
                    finally
                    {
                        if (File.Exists(tempDest))
                        {
                            try { File.Delete(tempDest); } catch { }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    if (attempt < options.MaxRetries)
                        await Task.Delay(250 * attempt, token);
                }
            }

            if (success)
            {
                if (item.Decision == CopyDecision.UseSourceReplaceDest)
                    Interlocked.Increment(ref replaced);
                else if (item.Decision == CopyDecision.KeepBothRename)
                    Interlocked.Increment(ref renamed);
                else
                    Interlocked.Increment(ref copiedNew);

                item.Status = options.VerifyAfterCopy ? "Verified ✓" : "Copied ✓";
                item.StatusColor = "#10B981";
            }
            else
            {
                Interlocked.Increment(ref failed);
                item.Status = "Failed";
                item.StatusColor = "#EF4444";
                item.Reason = lastError ?? "Unknown I/O error";
                errors.Add($"{item.SourcePath} -> {lastError}");
            }

            Interlocked.Increment(ref completedCount);
        });

        // Final Zero-Missing Audit: Confirm every succeeded file physically exists at destination
        int missingAudit = 0;
        foreach (var item in activeItems)
        {
            if (item.Status.Contains('✓') && !File.Exists(item.DestinationPath))
            {
                missingAudit++;
                item.Status = "Missing after audit!";
                item.StatusColor = "#EF4444";
                errors.Add($"Audit failed — missing at destination: {item.DestinationPath}");
            }
        }

        sw.Stop();
        summary.CopiedNew = copiedNew;
        summary.ReplacedBetterQuality = replaced;
        summary.RenamedKeptBoth = renamed;
        summary.FailedCount = failed;
        summary.VerifiedMissingCount = missingAudit;
        summary.TotalBytesCopied = bytesTransferred;
        summary.Elapsed = sw.Elapsed;
        summary.Errors = errors.ToList();

        double finalSpeed = sw.Elapsed.TotalSeconds > 0 ? bytesTransferred / sw.Elapsed.TotalSeconds : 0;
        progress?.Report(new FastCopyProgress
        {
            Stage = $"Completed in {sw.Elapsed:mm\\:ss\\.f}  •  Avg Speed: {FormatHelper.FormatBytes((long)finalSpeed)}/s  •  Missing: {missingAudit}",
            CompletedFiles = activeItems.Count,
            TotalFiles = activeItems.Count,
            BytesTransferred = bytesTransferred,
            TotalBytesToTransfer = totalBytesToCopy,
            SpeedBytesPerSec = finalSpeed,
            PercentComplete = 100
        });

        return summary;
    }

    private static async Task CopyFileBufferedAsync(
        string sourcePath,
        string destPath,
        Action<int> onBytesCopied,
        CancellationToken ct)
    {
        const int bufferSize = 1024 * 1024; // 1 MB fast sequential buffer
        await using var src = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var dst = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[bufferSize];
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            onBytesCopied(read);
        }
        await dst.FlushAsync(ct);
    }

    public static (bool SourceIsBetter, string Reason) CompareQuality(
        long srcPixels, long srcSize, DateTime srcDate,
        long destPixels, long destSize, DateTime destDate)
    {
        // 1. If both are images with readable resolution, higher pixel resolution wins!
        if (srcPixels > 0 && destPixels > 0 && srcPixels != destPixels)
        {
            bool win = srcPixels > destPixels;
            double srcMp = srcPixels / 1_000_000.0;
            double dstMp = destPixels / 1_000_000.0;
            return (win, win
                ? $"Higher resolution ({srcMp:0.##} MP vs {dstMp:0.##} MP)"
                : $"Destination has higher resolution ({dstMp:0.##} MP vs {srcMp:0.##} MP)");
        }

        // 2. Same resolution (or non-image like Video/Audio/PDF/ZIP): larger file size = higher bitrate / less compression / fuller version
        if (srcSize != destSize)
        {
            bool win = srcSize > destSize;
            return (win, win
                ? $"Higher quality / larger size ({FormatHelper.FormatBytes(srcSize)} vs {FormatHelper.FormatBytes(destSize)})"
                : $"Destination has larger size ({FormatHelper.FormatBytes(destSize)} vs {FormatHelper.FormatBytes(srcSize)})");
        }

        // 3. Same size (different content): newer modification timestamp wins
        bool newer = srcDate > destDate;
        return (newer, newer
            ? $"Same size, Source is newer ({srcDate:yyyy-MM-dd HH:mm})"
            : $"Same size, Destination is newer or same ({destDate:yyyy-MM-dd HH:mm})");
    }

    private static (long Pixels, string QualityLabel) InspectQuality(string filePath, string ext)
    {
        if (!ImageExtensions.Contains(ext))
            return (0, string.Empty);

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                int w = frame.PixelWidth;
                int h = frame.PixelHeight;
                if (w > 0 && h > 0)
                    return ((long)w * h, $"{w}x{h}");
            }
        }
        catch
        {
            // Non-standard or corrupted image header — fall back to file size comparison
        }
        return (0, string.Empty);
    }

    private static string GetUniqueDestinationPath(string originalPath, HashSet<string> reservedPaths)
    {
        if (!File.Exists(originalPath) && !reservedPaths.Contains(originalPath))
            return originalPath;

        string dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(originalPath);
        string ext = Path.GetExtension(originalPath);

        int counter = 2;
        while (true)
        {
            string candidate = Path.Combine(dir, $"{name} ({counter}){ext}");
            if (!File.Exists(candidate) && !reservedPaths.Contains(candidate))
                return candidate;
            counter++;
        }
    }

    private static string? ComputeSha256(string filePath, CancellationToken ct)
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

    private static IEnumerable<string> SafeEnumerateFiles(string rootDir, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                yield return f;
            }

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
                        if (SkippedSystemDirs.Contains(di.Name))
                            continue;
                        stack.Push(sub);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
