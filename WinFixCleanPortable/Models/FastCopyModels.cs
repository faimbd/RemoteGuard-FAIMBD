namespace WinFixCleanPortable.Models;

public enum ConflictPolicy
{
    KeepBetterQuality,
    AskUserToChoose,
    KeepLargerSize,
    KeepNewerDate,
    SkipDuplicatesAndExisting,
    OverwriteExisting,
    KeepBothRename
}

public enum CopyDecision
{
    CopyNew,
    SkipIdenticalDuplicate,
    UseSourceReplaceDest,
    KeepDestinationSkip,
    KeepBothRename
}

public class FastCopyOptions
{
    public List<string> SourcePaths { get; set; } = new();
    public string DestinationFolder { get; set; } = string.Empty;
    public bool IsMoveMode { get; set; } = false; // false = Copy, true = Cut/Move
    public bool PreserveFolderStructure { get; set; } = true;
    public int ThreadCount { get; set; } = 4;
    public bool SkipContentDuplicatesByHash { get; set; } = true;
    public bool VerifyAfterCopy { get; set; } = true;
    public ConflictPolicy Policy { get; set; } = ConflictPolicy.KeepBetterQuality;
    public int MaxRetries { get; set; } = 3;
}

public class CopyPlanItem : ObservableObject
{
    private CopyDecision _decision;
    private string _status = "Planned";
    private string _statusColor = "#64748B";
    private string _reason = string.Empty;

    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    public long SourceSize { get; set; }
    public DateTime SourceModified { get; set; }
    public string SourceQualityInfo { get; set; } = string.Empty;
    public long SourcePixels { get; set; }

    public bool DestinationExists { get; set; }
    public string? ExistingDuplicatePath { get; set; }
    public long DestSize { get; set; }
    public DateTime? DestModified { get; set; }
    public string DestQualityInfo { get; set; } = "—";
    public long DestPixels { get; set; }

    public bool IsExactContentDuplicate { get; set; }
    public bool IsConflict => DestinationExists && !IsExactContentDuplicate;
    public string RecommendedWinner { get; set; } = string.Empty;

    public string SourceSummary => $"{Helpers.FormatHelper.FormatBytes(SourceSize)}" +
                                   (string.IsNullOrEmpty(SourceQualityInfo) ? "" : $" ({SourceQualityInfo})") +
                                   $" • {SourceModified:yyyy-MM-dd HH:mm}";

    public string DestSummary => DestinationExists
        ? $"{Helpers.FormatHelper.FormatBytes(DestSize)}" +
          (string.IsNullOrEmpty(DestQualityInfo) || DestQualityInfo == "—" ? "" : $" ({DestQualityInfo})") +
          (DestModified.HasValue ? $" • {DestModified:yyyy-MM-dd HH:mm}" : "")
        : (IsExactContentDuplicate ? $"Identical copy exists: {ExistingDuplicatePath}" : "New file");

    public CopyDecision Decision
    {
        get => _decision;
        set
        {
            if (SetProperty(ref _decision, value))
            {
                OnPropertyChanged(nameof(DecisionText));
                OnPropertyChanged(nameof(DecisionIndex));
            }
        }
    }

    public int DecisionIndex
    {
        get => Decision switch
        {
            CopyDecision.UseSourceReplaceDest => 0,
            CopyDecision.KeepDestinationSkip => 1,
            CopyDecision.KeepBothRename => 2,
            CopyDecision.SkipIdenticalDuplicate => 1,
            _ => 0
        };
        set
        {
            Decision = value switch
            {
                0 => DestinationExists ? CopyDecision.UseSourceReplaceDest : CopyDecision.CopyNew,
                1 => CopyDecision.KeepDestinationSkip,
                2 => CopyDecision.KeepBothRename,
                _ => Decision
            };
            Status = Decision switch
            {
                CopyDecision.CopyNew => "Ready to Copy",
                CopyDecision.UseSourceReplaceDest => "Replace (Chosen)",
                CopyDecision.KeepDestinationSkip => "Keep Dest (Chosen)",
                CopyDecision.KeepBothRename => "Rename (Keep Both)",
                _ => Status
            };
            StatusColor = Decision switch
            {
                CopyDecision.CopyNew => "#3B82F6",
                CopyDecision.UseSourceReplaceDest => "#10B981",
                CopyDecision.KeepDestinationSkip => "#F59E0B",
                CopyDecision.KeepBothRename => "#8B5CF6",
                _ => StatusColor
            };
        }
    }

    public string DecisionText => Decision switch
    {
        CopyDecision.CopyNew => "Copy (New)",
        CopyDecision.SkipIdenticalDuplicate => "Skip (Exact Duplicate)",
        CopyDecision.UseSourceReplaceDest => "Replace (Use Source)",
        CopyDecision.KeepDestinationSkip => "Keep Dest (Skip)",
        CopyDecision.KeepBothRename => "Keep Both (Rename)",
        _ => Decision.ToString()
    };

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    public string Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value);
    }
}

public class FastCopyProgress
{
    public string Stage { get; set; } = string.Empty;
    public int CompletedFiles { get; set; }
    public int TotalFiles { get; set; }
    public long BytesTransferred { get; set; }
    public long TotalBytesToTransfer { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public double PercentComplete { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
}

public class FastCopySummary
{
    public int TotalScanned { get; set; }
    public int CopiedNew { get; set; }
    public int ReplacedBetterQuality { get; set; }
    public int RenamedKeptBoth { get; set; }
    public int SkippedDuplicates { get; set; }
    public int SkippedKeptDestination { get; set; }
    public int FailedCount { get; set; }
    public int VerifiedMissingCount { get; set; }
    public long TotalBytesCopied { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<string> Errors { get; set; } = new();
}
