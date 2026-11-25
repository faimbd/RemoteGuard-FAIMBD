namespace WinFixCleanPortable.Models;

/// <summary>One row in the Drive Fix grid (a partition, or the whole disk when it has no usable partition).</summary>
public class DriveRow : ObservableObject
{
    public int DiskNumber { get; set; }
    public string DiskName { get; set; } = string.Empty;
    public string BusType { get; set; } = string.Empty;
    public long DiskSizeBytes { get; set; }
    public string PartitionStyle { get; set; } = string.Empty;
    public bool DiskOffline { get; set; }
    public string OfflineReason { get; set; } = string.Empty;
    public bool DiskReadOnly { get; set; }
    public bool DiskIsSystem { get; set; }
    public bool DiskIsBoot { get; set; }
    public string DiskHealth { get; set; } = string.Empty;

    public int PartitionNumber { get; set; } // 0 = whole-disk row
    public string DriveLetter { get; set; } = string.Empty; // "E" or ""
    public string Label { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public long PartitionSizeBytes { get; set; }
    public long FreeBytes { get; set; }
    public string PartitionType { get; set; } = string.Empty;
    public bool IsSpecialPartition { get; set; } // EFI / MSR / Recovery / hidden
    public string VolumeHealth { get; set; } = string.Empty;

    public bool IsProtected { get; set; }
    public string ProtectReason { get; set; } = string.Empty;
    public List<string> Problems { get; set; } = new();

    public bool IsWholeDiskRow => PartitionNumber == 0;
    public bool IsRemovableBus => BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                                  BusType.Equals("SD", StringComparison.OrdinalIgnoreCase) ||
                                  BusType.Equals("MMC", StringComparison.OrdinalIgnoreCase);

    public string DiskTitle => $"Disk {DiskNumber}: {DiskName}";
    public string LetterDisplay => string.IsNullOrEmpty(DriveLetter) ? (IsWholeDiskRow ? "—" : "(none)") : $"{DriveLetter}:";
    public string SizeDisplay => Helpers.FormatHelper.FormatBytes(IsWholeDiskRow ? DiskSizeBytes : PartitionSizeBytes);
    public string FreeDisplay => FreeBytes > 0 ? Helpers.FormatHelper.FormatBytes(FreeBytes) : "—";
    public string FileSystemDisplay => string.IsNullOrWhiteSpace(FileSystem) ? (IsWholeDiskRow ? "—" : "RAW") : FileSystem;
    public string ProblemsText => Problems.Count == 0 ? "No problems detected" : string.Join("  •  ", Problems);

    public bool IsNoMedia => IsWholeDiskRow && DiskSizeBytes <= 0;

    public string StatusText => IsProtected
        ? "🔒 Protected"
        : IsNoMedia ? "∅ No Media"
        : Problems.Count == 0 ? "✓ OK" : "⚠ Needs Fix";

    public string StatusColor => IsProtected ? "#6B531D" : IsNoMedia ? "#475569" : Problems.Count == 0 ? "#15803D" : "#B91C1C";

    public string InfoText =>
        $"{DiskTitle} ({BusType}, {Helpers.FormatHelper.FormatBytes(DiskSizeBytes)}, {PartitionStyle})\n" +
        (IsWholeDiskRow ? "Whole disk\n" : $"Partition {PartitionNumber}  •  {LetterDisplay}  •  {FileSystemDisplay}  •  {Label}\n") +
        (IsProtected ? $"🔒 {ProtectReason}\n" : string.Empty) +
        $"Status: {ProblemsText}";
}

/// <summary>One boot entry from the UEFI firmware boot menu or the Windows Boot Manager menu (BCD store).</summary>
public class BootEntryItem : ObservableObject
{
    public string Store { get; set; } = string.Empty;        // "UEFI Firmware Menu" / "Windows Boot Menu"
    public bool IsFirmware { get; set; }
    public string Identifier { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public bool IsDefault { get; set; }
    public bool InMenu { get; set; }

    public string Kind { get; set; } = "Other";
    public string KindColor { get; set; } = "#6B531D";
    public string Status { get; set; } = "OK";
    public string StatusColor { get; set; } = "#15803D";
    public bool IsProtected { get; set; }
    public string ProtectReason { get; set; } = string.Empty;
    public string Advice { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? "(no name)" : Description;
    public string MenuText => InMenu ? "Shown" : "Hidden";
    public string MarkerText => IsCurrent ? "▶ Running now" : IsDefault ? "★ Default" : string.Empty;
    public string TargetText => string.IsNullOrWhiteSpace(Path) ? Device : $"{Path}   [{Device}]";

    public string InfoText =>
        $"{DisplayName}\n{Store}  •  {Kind}\nStatus: {Status}\n" +
        (string.IsNullOrWhiteSpace(Path) ? string.Empty : $"Boot file: {Path}\n") +
        (string.IsNullOrWhiteSpace(Device) ? string.Empty : $"Device: {Device}\n") +
        (IsProtected ? $"🔒 Protected: {ProtectReason}\n" : string.Empty) +
        (string.IsNullOrWhiteSpace(Advice) ? string.Empty : $"💡 {Advice}");
}

/// <summary>A top-level folder on the EFI System Partition (\EFI\&lt;name&gt;), e.g. leftover "ubuntu" GRUB files.</summary>
public class EfiFolderItem : ObservableObject
{
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string SizeDisplay => Helpers.FormatHelper.FormatBytes(SizeBytes);
    public string Kind { get; set; } = string.Empty;
    public string KindColor { get; set; } = "#6B531D";
    public bool IsProtected { get; set; }
    public string Note { get; set; } = string.Empty;
    public string ReferencedBy { get; set; } = string.Empty;
}
