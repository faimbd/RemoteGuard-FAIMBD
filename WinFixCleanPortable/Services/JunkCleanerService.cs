using System.IO;
using WinFixCleanPortable.Helpers;
using WinFixCleanPortable.Models;

namespace WinFixCleanPortable.Services;

public class JunkCleanerService
{
    public List<JunkCategoryItem> BuildDefaultCategories()
    {
        string userTemp = Path.GetTempPath();
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new List<JunkCategoryItem>
        {
            new()
            {
                Id = "user_temp",
                Name = "User Temporary Files",
                Description = "App installers, cache files, and temporary working data created by programs.",
                PathsSummary = userTemp,
                RequiresAdmin = false,
                IsSelected = true
            },
            new()
            {
                Id = "win_temp",
                Name = "Windows System Temp",
                Description = "Temporary files created by Windows services and installers.",
                PathsSummary = Path.Combine(winDir, "Temp"),
                RequiresAdmin = true,
                IsSelected = true
            },
            new()
            {
                Id = "win_update",
                Name = "Windows Update Download Cache",
                Description = "Old downloaded Windows Update packages stored in SoftwareDistribution\\Download.",
                PathsSummary = Path.Combine(winDir, "SoftwareDistribution", "Download"),
                RequiresAdmin = true,
                IsSelected = false
            },
            new()
            {
                Id = "crash_dumps",
                Name = "Crash Dumps & Error Logs",
                Description = "Application crash dump (.dmp) files and Windows minidumps.",
                PathsSummary = Path.Combine(localAppData, "CrashDumps"),
                RequiresAdmin = false,
                IsSelected = true
            },
            new()
            {
                Id = "prefetch",
                Name = "Windows Prefetch Cache",
                Description = "Old application launch trace files (Windows rebuilds needed ones automatically).",
                PathsSummary = Path.Combine(winDir, "Prefetch"),
                RequiresAdmin = true,
                IsSelected = false
            },
            new()
            {
                Id = "recycle_bin",
                Name = "Windows Recycle Bin",
                Description = "Deleted files currently waiting inside the Recycle Bin across all drives.",
                PathsSummary = "All Drives ($Recycle.Bin)",
                RequiresAdmin = false,
                IsSelected = false
            }
        };
    }

    public async Task AnalyzeAsync(List<JunkCategoryItem> categories, IProgress<string>? progress, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            foreach (var cat in categories)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Analyzing {cat.Name}...");
                cat.FilePaths.Clear();

                if (cat.Id == "recycle_bin")
                {
                    var (size, count) = ShellFileHelper.QueryRecycleBin();
                    cat.SizeBytes = size;
                    cat.FileCount = (int)Math.Min(count, int.MaxValue);
                    cat.Status = count > 0 ? $"Ready ({count:N0} items)" : "Empty";
                    continue;
                }

                var folders = cat.Id switch
                {
                    "user_temp" => new[] { Path.GetTempPath() },
                    "win_temp" => new[] { Path.Combine(winDir, "Temp") },
                    "win_update" => new[] { Path.Combine(winDir, "SoftwareDistribution", "Download") },
                    "crash_dumps" => new[] { Path.Combine(localAppData, "CrashDumps"), Path.Combine(winDir, "Minidump") },
                    "prefetch" => new[] { Path.Combine(winDir, "Prefetch") },
                    _ => Array.Empty<string>()
                };

                long totalSize = 0;
                int countFiles = 0;
                bool accessDenied = false;

                foreach (var folder in folders)
                {
                    if (!Directory.Exists(folder))
                        continue;

                    var stack = new Stack<string>();
                    stack.Push(folder);

                    while (stack.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var cur = stack.Pop();
                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(cur))
                            {
                                try
                                {
                                    var fi = new FileInfo(file);
                                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                                        continue;
                                    totalSize += fi.Length;
                                    countFiles++;
                                    cat.FilePaths.Add(file);
                                }
                                catch { }
                            }
                            foreach (var sub in Directory.EnumerateDirectories(cur))
                            {
                                try
                                {
                                    var di = new DirectoryInfo(sub);
                                    if ((di.Attributes & FileAttributes.ReparsePoint) == 0)
                                        stack.Push(sub);
                                }
                                catch { }
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            accessDenied = true;
                        }
                        catch { }
                    }
                }

                cat.SizeBytes = totalSize;
                cat.FileCount = countFiles;
                cat.Status = countFiles > 0
                    ? $"Ready ({countFiles:N0} files)"
                    : (accessDenied ? "Needs Admin" : "Clean");
            }
        }, ct);
    }

    public async Task<(int DeletedCount, int SkippedCount, long FreedBytes)> CleanSelectedAsync(
        List<JunkCategoryItem> categories,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            int deleted = 0;
            int skipped = 0;
            long freed = 0;

            foreach (var cat in categories.Where(c => c.IsSelected))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Cleaning {cat.Name}...");

                if (cat.Id == "recycle_bin")
                {
                    var (beforeSize, beforeCount) = ShellFileHelper.QueryRecycleBin();
                    if (beforeCount > 0 && ShellFileHelper.EmptyRecycleBin())
                    {
                        freed += beforeSize;
                        deleted += (int)Math.Min(beforeCount, int.MaxValue);
                        cat.SizeBytes = 0;
                        cat.FileCount = 0;
                        cat.Status = "Emptied";
                    }
                    else
                    {
                        cat.Status = beforeCount == 0 ? "Already empty" : "Skipped";
                    }
                    continue;
                }

                int catDeleted = 0;
                int catSkipped = 0;
                long catFreed = 0;

                foreach (var path in cat.FilePaths)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (!File.Exists(path))
                            continue;
                        long len = new FileInfo(path).Length;
                        File.Delete(path);
                        catDeleted++;
                        catFreed += len;
                    }
                    catch
                    {
                        // Locked or in-use temp file — safely skip
                        catSkipped++;
                    }
                }

                deleted += catDeleted;
                skipped += catSkipped;
                freed += catFreed;

                cat.SizeBytes = Math.Max(0, cat.SizeBytes - catFreed);
                cat.FileCount = catSkipped;
                cat.Status = $"Cleaned {catDeleted:N0} ({catSkipped:N0} in-use skipped)";
                cat.FilePaths.Clear();
            }

            return (deleted, skipped, freed);
        }, ct);
    }
}
