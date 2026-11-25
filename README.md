# WinFix & Clean Portable

A single-file, zero-install **Portable Windows Utility** combining Windows System Repair (Missing & Corrupted File Checker), Large & ZIP/ISO File Finder, Custom-Location Duplicate Cleaner (SHA-256 verified), and Safe System Junk Cleaner.

## Portable Executable

Run directly from any folder or USB drive — **no installation, no registry writes**:

- `Portable\WinFixCleanPortable.exe`

## Features

1. **Dashboard**
   - Live disk usage cards for all connected drives (`C:`, `D:`, `E:`, etc.)
   - One-click shortcuts to scan any drive for large/ZIP/ISO files or launch Windows Repair.
2. **Windows Repair & Missing File Checker**
   - **Instant Missing DLL Checker**: Checks `System32` & `SysWOW64` for critical Windows & runtime DLLs (`vcruntime140.dll`, `msvcp140.dll`, `d3dx9_43.dll`, `xinput1_3.dll`, `ucrtbase.dll`, etc.) — no admin needed!
   - **Smart Full Auto-Repair**: Runs `DISM /RestoreHealth` -> `sfc /scannow` -> Automatically parses `C:\Windows\Logs\CBS\CBS.log` into a structured table showing exactly which files were missing, corrupted, or repaired.
   - Individual tools: `SFC /verifyonly`, `SFC /scannow`, `DISM CheckHealth`, `DISM RestoreHealth`, Online `CHKDSK /scan`, Network/Winsock Reset, CBS.log inspector, and `.txt` report export.
   - One-click **Restart as Administrator** button built in.
3. **Large Files & ZIP / ISO Finder**
   - Scan selected drives or custom folders.
   - Lists **Large Files** (configurable threshold: `25 MB` to `2 GB`), **ZIP / RAR / 7Z / TAR / GZ Archives**, **ISO / IMG / VHD / WIM Disk Images**, and optional large installers/videos — sorted largest-first!
   - **Hover / Click Instant Actions:** Hover over or click any file row to instantly access **`ℹ Safe?` (Delete Impact Summary + 1-Click Google Search)**, **`♻` (Recycle Bin)**, and **`🗑 Del` (Permanent Delete)** right beside the file.
   - Instant filter chips (`All`, `ISO / Disk Images`, `ZIP / Archives`, `Large Files`, `> 500 MB`, `> 1 GB`) + live filename/path search.
4. **Duplicate File Cleaner (Custom Locations)**
   - Choose exact folders (or quick-add Downloads, Desktop, Documents, Pictures, Videos).
   - Fast 3-stage verification: Exact Size -> 64KB Head+Tail Partial Hash -> Full **SHA-256** verification (zero false positives).
   - Smart Auto-Select: **Keep Oldest**, **Keep Newest**, or **Keep Shortest Path**.
   - Built-in **Safety Guard** warns if all copies in any duplicate group are selected.
5. **Smart Fast Copy (Multi-Threaded, Zero Missing, Zero Duplicates, Better Quality)**
   - Parallel multi-threaded copy/move (`Auto` or `2`–`16` threads) with 1 MB buffered async streams, live speed (`MB/s`), atomic temp-file writes, and automatic retries.
   - **Zero Missing Files**: Optional SHA-256 post-copy verification + final destination audit.
   - **Zero Duplicates**: Automatically detects and skips byte-identical SHA-256 duplicates already in destination or within source folders.
   - **Better Quality Auto-Selection & Manual Choice**: Automatically compares real image resolution (`Width × Height`), file size (bitrate), and timestamp — or lets you inspect every conflict side-by-side and choose `Use Source`, `Keep Destination`, or `Keep Both (Rename)` per file!
6. **Clean App Uninstaller (Revo-Style Leftover Removal)**
   - Lists all installed 64-bit, 32-bit, MSI, and User applications with search, publisher, version, size, and install date.
   - Runs the official Windows uninstaller and then automatically scans leftover `AppData`, `LocalAppData`, `ProgramData`, `Install Location`, Desktop/Start Menu shortcuts, and `HKCU`/`HKLM` Registry traces.
   - Strict safety guardrails protect Windows and vendor root folders; every leftover is previewed before cleaning (safe Recycle Bin default for files/folders).
7. **Drive Fix & Format (Pen Drive / Hard Disk)**
   - Detects drives that don't show up: **offline**, **read-only / write-protected**, **no drive letter**, **RAW / corrupted file system**, **uninitialized**, unhealthy disks, and empty card-reader slots.
   - One-click fixes: Bring Online, Clear Read-Only, Assign Drive Letter, Repair (`CHKDSK /F`), Rescan Hardware, and removal of USB write-protection registry/Group Policy keys.
   - Format a partition (exFAT / NTFS / FAT32, quick or full) or **Full Reset & Format Whole Disk** (MBR/GPT) for RAW or wrong-size pen drives — requires typing the drive letter or `FORMAT` to confirm.
   - The Windows disk, the EFI disk, and the disk the portable app runs from are locked and can never be formatted. These checks are repeated in the format script itself, right before anything is written.
8. **Boot Menu Manager (BCD / UEFI)**
   - Lists UEFI firmware (BIOS) boot entries and Windows Boot Manager entries. It marks entries as **Stale** (boot file missing after an OS was removed), **Broken** (target Windows gone), Linux/GRUB, hardware devices, Recovery, etc.
   - Delete, Hide/Show, Set Default, Boot First (UEFI), Rename, plus **Add Safe Mode / Safe Mode + Networking / Windows entry**, and set the menu timeout.
   - **EFI Partition Leftovers:** finds and deletes leftover `\EFI\ubuntu`-style folders (with backup) so removed OS entries stop coming back.
   - Uses Microsoft's official `bcdedit` (the BCD store is a registry hive). An automatic `.bcd` backup is made before every change, and backups can be restored. Windows Boot Manager, the running Windows, and Recovery are locked.
9. **System Junk Cleaner**
   - Analyze & clean User Temp, Windows Temp, Windows Update Download Cache, Crash Dumps, Prefetch, and Recycle Bin safely (locked files skipped automatically).

## Rebuild Portable EXE from Source

```powershell
dotnet publish "WinFixCleanPortable\WinFixCleanPortable.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "Portable"
```

## Privacy, Transparency & Code Signing Policy

- **100% Open Source (MIT License):** Full source code is publicly auditable with zero proprietary components, zero telemetry, zero ads, and zero bundled third-party installers or PUPs.
- **Code Signing Policy:** Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org) (once enabled via automated GitHub Actions release builds in `.github/workflows/release.yml`).
- **Build & Approval Team:** Releases are built directly from tagged commits on this public repository via GitHub Actions and verified with published SHA-256 checksums (`SHA256SUMS.txt`).
- **Setup Guide:** See [SIGNPATH_SETUP_GUIDE.md](SIGNPATH_SETUP_GUIDE.md) for step-by-step GitHub Actions + SignPath Foundation + free Microsoft Store setup.

