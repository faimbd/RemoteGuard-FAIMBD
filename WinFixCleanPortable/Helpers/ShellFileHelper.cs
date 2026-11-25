using System.IO;
using System.Runtime.InteropServices;

namespace WinFixCleanPortable.Helpers;

public static class ShellFileHelper
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    public static (bool Success, string? Error) DeleteFile(string path, bool sendToRecycleBin)
        => DeleteDirectoryOrFile(path, sendToRecycleBin);

    public static (bool Success, string? Error) DeleteDirectoryOrFile(string path, bool sendToRecycleBin)
    {
        try
        {
            bool isFile = File.Exists(path);
            bool isDir = !isFile && Directory.Exists(path);
            if (!isFile && !isDir)
                return (true, null);

            if (sendToRecycleBin)
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = path + "\0\0",
                    fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
                };
                int result = SHFileOperation(ref op);
                if (result == 0 && !op.fAnyOperationsAborted)
                    return (true, null);

                return (false, $"Recycle Bin error code: {result}");
            }
            else if (isFile)
            {
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);

                File.Delete(path);
                return (true, null);
            }
            else
            {
                Directory.Delete(path, recursive: true);
                return (true, null);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (long SizeBytes, long ItemCount) QueryRecycleBin()
    {
        try
        {
            var info = new SHQUERYRBINFO
            {
                cbSize = Marshal.SizeOf<SHQUERYRBINFO>()
            };
            int hr = SHQueryRecycleBin(null, ref info);
            if (hr == 0)
                return (Math.Max(0, info.i64Size), Math.Max(0, info.i64NumItems));
        }
        catch
        {
            // Ignore
        }
        return (0, 0);
    }

    public static bool EmptyRecycleBin()
    {
        try
        {
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }
}
