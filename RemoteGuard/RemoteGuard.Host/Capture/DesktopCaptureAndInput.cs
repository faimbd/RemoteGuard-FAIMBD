using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteGuard.Host.Capture;

public sealed class DesktopCaptureAndInput
{
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_CREATEWINDOW = 0x0002;
    private const uint DESKTOP_WRITEOBJECTS = 0x0080;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const uint GENERIC_ALL = 0x10000000;
    private const int UOI_NAME = 2;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, uint nLength, out uint lpnLengthNeeded);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public sealed record MonitorDescriptor(int Index, string Name, int Left, int Top, int Width, int Height, bool IsPrimary);

    public static List<MonitorDescriptor> GetMonitors()
    {
        SwitchToActiveInputDesktop(out _);
        var list = new List<MonitorDescriptor>();
        int idx = 0;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                bool isPrimary = (mi.dwFlags & 1) != 0;
                int w = mi.rcMonitor.Width;
                int h = mi.rcMonitor.Height;
                string label = isPrimary
                    ? $"Screen {idx + 1} (Primary {w}x{h})"
                    : $"Screen {idx + 1} ({w}x{h})";
                list.Add(new MonitorDescriptor(idx, label, mi.rcMonitor.Left, mi.rcMonitor.Top, w, h, isPrimary));
                idx++;
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);
            list.Add(new MonitorDescriptor(0, $"Screen 1 (Primary {w}x{h})", 0, 0, Math.Max(1, w), Math.Max(1, h), true));
        }

        // Always order Primary monitor first as Screen 1 (index 0), then secondary monitors
        var ordered = list
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Left)
            .ThenBy(m => m.Top)
            .Select((m, i) => m with
            {
                Index = i,
                Name = m.IsPrimary
                    ? $"Screen {i + 1} (Primary {m.Width}x{m.Height})"
                    : $"Screen {i + 1} ({m.Width}x{m.Height})"
            })
            .ToList();
        return ordered;
    }

    private static (int X, int Y, int W, int H) GetCaptureBounds(int monitorIndex)
    {
        if (monitorIndex >= 0)
        {
            var mons = GetMonitors();
            if (monitorIndex < mons.Count)
            {
                var m = mons[monitorIndex];
                return (m.Left, m.Top, m.Width, m.Height);
            }
        }

        int srcX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int srcY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int srcW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int srcH = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (srcW <= 0 || srcH <= 0)
        {
            srcX = 0;
            srcY = 0;
            srcW = GetSystemMetrics(SM_CXSCREEN);
            srcH = GetSystemMetrics(SM_CYSCREEN);
        }
        return (srcX, srcY, srcW, srcH);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

    [DllImport("sas.dll")]
    private static extern void SendSAS(bool asUser);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public static void EnablePerMonitorDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo
        .GetImageEncoders()
        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// Attaches the current thread to the active input desktop (Default, Winlogon / Lock Screen, or UAC).
    /// Returns true if the active desktop is the Lock Screen / Secure Desktop.
    /// </summary>
    public static bool SwitchToActiveInputDesktop(out string desktopName)
    {
        desktopName = "Default";
        IntPtr hDesk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP);
        if (hDesk == IntPtr.Zero)
            hDesk = OpenInputDesktop(0, false, GENERIC_ALL);

        if (hDesk == IntPtr.Zero)
            return false;

        try
        {
            var sb = new StringBuilder(256);
            if (GetUserObjectInformation(hDesk, UOI_NAME, sb, 256, out _))
                desktopName = sb.ToString();

            SetThreadDesktop(hDesk);
            return !string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(hDesk);
        }
    }

    public static bool IsLockScreenActive()
    {
        IntPtr hDesk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (hDesk == IntPtr.Zero)
            return true; // Non-elevated process cannot open Winlogon desktop -> usually locked/UAC
        try
        {
            var sb = new StringBuilder(256);
            if (GetUserObjectInformation(hDesk, UOI_NAME, sb, 256, out _))
                return !string.Equals(sb.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            return false;
        }
        finally
        {
            CloseDesktop(hDesk);
        }
    }

    public static (byte[] JpegBytes, int Width, int Height, bool IsLocked)? CaptureFrame(int maxWidth = 1280, int jpegQuality = 55, int monitorIndex = -1)
    {
        bool isLocked = SwitchToActiveInputDesktop(out _);

        var (srcX, srcY, srcW, srcH) = GetCaptureBounds(monitorIndex);
        if (srcW <= 0 || srcH <= 0) return null;

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return null;

        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;

        try
        {
            hdcMem = CreateCompatibleDC(hdcScreen);
            hBitmap = CreateCompatibleBitmap(hdcScreen, srcW, srcH);
            hOld = SelectObject(hdcMem, hBitmap);

            if (!BitBlt(hdcMem, 0, 0, srcW, srcH, hdcScreen, srcX, srcY, SRCCOPY | CAPTUREBLT))
                return null;

            // Draw mouse cursor onto frame
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(ref ci) && ci.flags == 1 && ci.hCursor != IntPtr.Zero)
            {
                DrawIcon(hdcMem, ci.ptScreenPos.X - srcX, ci.ptScreenPos.Y - srcY, ci.hCursor);
            }

            SelectObject(hdcMem, hOld);
            hOld = IntPtr.Zero;

            using var rawBmp = Image.FromHbitmap(hBitmap);
            Bitmap finalBmp = rawBmp;
            bool disposeFinal = false;

            int outW = srcW;
            int outH = srcH;
            if (maxWidth > 0 && srcW > maxWidth)
            {
                double scale = (double)maxWidth / srcW;
                outW = maxWidth;
                outH = Math.Max(1, (int)(srcH * scale));
                finalBmp = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
                disposeFinal = true;
                using var g = Graphics.FromImage(finalBmp);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.DrawImage(rawBmp, 0, 0, outW, outH);
            }

            try
            {
                using var ms = new MemoryStream();
                if (JpegCodec != null)
                {
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(jpegQuality, 15, 95));
                    finalBmp.Save(ms, JpegCodec, ep);
                }
                else
                {
                    finalBmp.Save(ms, ImageFormat.Jpeg);
                }

                return (ms.ToArray(), outW, outH, isLocked);
            }
            finally
            {
                if (disposeFinal) finalBmp.Dispose();
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero) SelectObject(hdcMem, hOld);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    public static void HandleMouseEvent(string action, double normX, double normY, int wheelDelta = 0, int monitorIndex = -1)
    {
        SwitchToActiveInputDesktop(out _);

        int virtX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int virtY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int virtW = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int virtH = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var (mX, mY, mW, mH) = GetCaptureBounds(monitorIndex);
        int px = mX + (int)Math.Round(Math.Clamp(normX, 0.0, 1.0) * Math.Max(1, mW - 1));
        int py = mY + (int)Math.Round(Math.Clamp(normY, 0.0, 1.0) * Math.Max(1, mH - 1));

        double globalNormX = (double)(px - virtX) / Math.Max(1, virtW - 1);
        double globalNormY = (double)(py - virtY) / Math.Max(1, virtH - 1);
        int absX = Math.Clamp((int)Math.Round(globalNormX * 65535.0), 0, 65535);
        int absY = Math.Clamp((int)Math.Round(globalNormY * 65535.0), 0, 65535);

        void MoveCursor()
        {
            SetCursorPos(px, py);
            mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, absX, absY, 0, UIntPtr.Zero);
        }

        switch (action.ToLowerInvariant())
        {
            case "move":
                MoveCursor();
                break;
            case "down":
                MoveCursor();
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                break;
            case "up":
                MoveCursor();
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                break;
            case "click":
                MoveCursor();
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(20);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                break;
            case "dblclick":
                MoveCursor();
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(20);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(50);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(20);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                break;
            case "rclick":
                MoveCursor();
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(20);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                break;
            case "mclick":
                MoveCursor();
                mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(20);
                mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
                break;
            case "wheel":
                MoveCursor();
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)wheelDelta), UIntPtr.Zero);
                break;
        }
    }

    public static void HandleText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SwitchToActiveInputDesktop(out _);

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\n')
            {
                SendSpecialKey("ENTER");
                continue;
            }
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE }
                }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
                }
            });
        }
        if (inputs.Count > 0)
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    public static void SendSpecialKey(string keyName)
    {
        SwitchToActiveInputDesktop(out _);

        switch (keyName.ToUpperInvariant())
        {
            case "CTRL_ALT_DEL":
                try { SendSAS(false); } catch { }
                return;
            case "LOCK":
                LockWorkStation();
                return;
            case "WAKE_SCREEN":
                // Tiny mouse wiggle wakes display / dismisses lock screen curtain
                SendMouse(0, 0, MOUSEEVENTF_MOVE, 0);
                SendKeyPress(0x20); // Space
                return;
            case "ENTER": SendKeyPress(0x0D); return;
            case "BACKSPACE": SendKeyPress(0x08); return;
            case "TAB": SendKeyPress(0x09); return;
            case "ESC": SendKeyPress(0x1B); return;
            case "SPACE": SendKeyPress(0x20); return;
            case "DELETE": SendKeyPress(0x2E); return;
            case "HOME": SendKeyPress(0x24); return;
            case "END": SendKeyPress(0x23); return;
            case "PGUP": SendKeyPress(0x21); return;
            case "PGDN": SendKeyPress(0x22); return;
            case "LEFT": SendKeyPress(0x25); return;
            case "UP": SendKeyPress(0x26); return;
            case "RIGHT": SendKeyPress(0x27); return;
            case "DOWN": SendKeyPress(0x28); return;
            case "WIN": SendKeyPress(0x5B); return;
            case "ALT_TAB": SendKeyCombo(0x12, 0x09); return;
            case "ALT_F4": SendKeyCombo(0x12, 0x73); return;
            case "CTRL_C": SendKeyCombo(0x11, 0x43); return;
            case "CTRL_V": SendKeyCombo(0x11, 0x56); return;
            case "CTRL_X": SendKeyCombo(0x11, 0x58); return;
            case "CTRL_A": SendKeyCombo(0x11, 0x41); return;
            case "CTRL_Z": SendKeyCombo(0x11, 0x5A); return;
            case "CTRL_S": SendKeyCombo(0x11, 0x53); return;
            case "WIN_D": SendKeyCombo(0x5B, 0x44); return;
            case "TASK_MGR": SendKeyCombo3(0x11, 0x10, 0x1B); return;
        }
    }

    private static void SendMouse(int dx, int dy, uint flags, uint mouseData)
    {
        var inp = new INPUT[]
        {
            new()
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = mouseData }
                }
            }
        };
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyPress(ushort vk)
    {
        var inp = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } }
        };
        SendInput(2, inp, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyCombo(ushort modVk, ushort keyVk)
    {
        var inp = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = modVk } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = keyVk } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = keyVk, dwFlags = KEYEVENTF_KEYUP } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = modVk, dwFlags = KEYEVENTF_KEYUP } } }
        };
        SendInput(4, inp, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyCombo3(ushort mod1, ushort mod2, ushort keyVk)
    {
        var inp = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = mod1 } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = mod2 } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = keyVk } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = keyVk, dwFlags = KEYEVENTF_KEYUP } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = mod2, dwFlags = KEYEVENTF_KEYUP } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = mod1, dwFlags = KEYEVENTF_KEYUP } } }
        };
        SendInput(6, inp, Marshal.SizeOf<INPUT>());
    }
}
