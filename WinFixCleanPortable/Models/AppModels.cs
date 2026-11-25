namespace WinFixCleanPortable.Models;

public class DriveSummaryItem : ObservableObject
{
    private bool _isSelected;

    public string Name { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string DisplayTitle => string.IsNullOrWhiteSpace(VolumeLabel) ? $"Local Disk ({Name.TrimEnd('\\')})" : $"{VolumeLabel} ({Name.TrimEnd('\\')})";
    public string DriveFormat { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes > 0 ? Math.Round((double)UsedBytes / TotalBytes * 100.0, 1) : 0;
    public string UsageText => $"{Helpers.FormatHelper.FormatBytes(UsedBytes)} used of {Helpers.FormatHelper.FormatBytes(TotalBytes)}";
    public string FreeText => $"{Helpers.FormatHelper.FormatBytes(FreeBytes)} free";
    public bool IsHighUsage => UsedPercent >= 85;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public class ScannedFileItem : ObservableObject
{
    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string Category { get; set; } = "Large File"; // "ISO / Disk Image", "ZIP / Archive", "Large File", "Installer", "Media"
    public string BadgeColor { get; set; } = "#3B82F6";
    public long SizeBytes { get; set; }
    public string SizeFormatted => Helpers.FormatHelper.FormatBytes(SizeBytes);
    public DateTime LastModified { get; set; }
    public string LastModifiedText => LastModified.ToString("yyyy-MM-dd HH:mm");
    public bool IsArchive { get; set; }
    public bool IsDiskImage { get; set; }
    public bool IsLarge { get; set; }
    public bool IsInstallerOrMedia { get; set; }
}

public class DuplicateFileItem : ObservableObject
{
    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public int GroupId { get; set; }
    public string GroupHeader { get; set; } = string.Empty;
    public string GroupColor { get; set; } = "#1E293B";
    public string HashShort { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string SizeFormatted => Helpers.FormatHelper.FormatBytes(SizeBytes);
    public DateTime LastModified { get; set; }
    public string LastModifiedText => LastModified.ToString("yyyy-MM-dd HH:mm");
    public int GroupCount { get; set; }
}

public class MissingFileReportItem
{
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // "Missing", "Corrupted", "Repaired", "Present"
    public string StatusColor { get; set; } = "#EF4444";
    public string Source { get; set; } = string.Empty; // "CBS.log / SFC", "System32 Check", "SysWOW64 Check"
    public string Details { get; set; } = string.Empty;
}

public class JunkCategoryItem : ObservableObject
{
    private bool _isSelected = true;
    private long _sizeBytes;
    private int _fileCount;
    private string _status = "Not scanned";

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PathsSummary { get; set; } = string.Empty;
    public bool RequiresAdmin { get; set; }
    public List<string> FilePaths { get; set; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetProperty(ref _sizeBytes, value))
                OnPropertyChanged(nameof(SizeFormatted));
        }
    }

    public string SizeFormatted => Helpers.FormatHelper.FormatBytes(SizeBytes);

    public int FileCount
    {
        get => _fileCount;
        set => SetProperty(ref _fileCount, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}
