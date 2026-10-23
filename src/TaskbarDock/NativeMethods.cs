using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarDock
{
    internal static class NativeMethods
    {
        internal const int SW_SHOWMAXIMIZED = 3;
        internal const int DWMWA_CLOAKED = 14;
        internal const int RGN_OR = 2;
        internal const uint MONITOR_DEFAULTTONEAREST = 2;
        internal const int SW_RESTORE = 9;
        internal const uint WM_GETICON = 0x007F;
        internal const int ICON_SMALL = 0;
        internal const int ICON_BIG = 1;
        internal const int ICON_SMALL2 = 2;
        internal const int GCLP_HICON = -14;
        internal const int GCLP_HICONSM = -34;
        internal const uint SMTO_ABORTIFHUNG = 0x0002;

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindowEx(
            IntPtr parent, IntPtr childAfter, string className, string windowName);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WINDOWPLACEMENT
        {
            public int Length;
            public int Flags;
            public int ShowCmd;
            public POINT MinPosition;
            public POINT MaxPosition;
            public RECT NormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out IntPtr result);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        internal static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        internal static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateRoundRectRgn(
            int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("gdi32.dll")]
        internal static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr handle);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(
            IntPtr hWnd, int attribute, out int value, int valueSize);

        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TRANSPARENT = 0x00000020;
        internal const int WS_EX_LAYERED = 0x00080000;
        internal const int SW_HIDE = 0;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal const int ULW_ALPHA = 0x00000002;
        internal const byte AC_SRC_OVER = 0x00;
        internal const byte AC_SRC_ALPHA = 0x01;
        internal const uint DIB_RGB_COLORS = 0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFOHEADER
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAP
        {
            public int Type;
            public int Width;
            public int Height;
            public int WidthBytes;
            public short Planes;
            public short BitsPixel;
            public IntPtr Bits;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UpdateLayeredWindow(
            IntPtr hWnd, IntPtr screenDc, ref POINT destination, ref SIZE size,
            IntPtr sourceDc, ref POINT source, int colorKey, ref BLENDFUNCTION blend, int flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        private static extern uint GetDpiForWindowNative(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr icon);

        internal const uint GA_ROOT = 2;

        internal const int SW_MINIMIZE = 6;
        internal const uint WM_CLOSE = 0x0010;
        internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        internal const uint ABM_NEW = 0x00;
        internal const uint ABM_REMOVE = 0x01;
        internal const uint ABM_QUERYPOS = 0x02;
        internal const uint ABM_SETPOS = 0x03;
        internal const uint ABE_TOP = 1;
        internal const int ABN_POSCHANGED = 0x01;
        internal const int ABN_FULLSCREENAPP = 0x02;

        internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        internal struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")]
        internal static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        internal static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        internal static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        /// <summary>Blocks until the next desktop composition (display refresh).</summary>
        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateDIBSection(
            IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        internal static extern int GetDIBits(
            IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] int[] bits,
            ref BITMAPINFOHEADER info, uint usage);

        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
        internal static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        internal static int GetDpiForWindow(IntPtr hWnd)
        {
            try
            {
                uint dpi = GetDpiForWindowNative(hWnd);
                return dpi == 0 ? 96 : (int)dpi;
            }
            catch (EntryPointNotFoundException)
            {
                return 96;
            }
        }

        /// <summary>
        /// True while a full-screen application, game or presentation owns the screen.
        /// </summary>
        internal static bool IsFullScreenAppRunning()
        {
            const int QUNS_BUSY = 2;
            const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
            const int QUNS_PRESENTATION_MODE = 4;
            int state;
            if (SHQueryUserNotificationState(out state) == 0 &&
                (state == QUNS_BUSY || state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE))
                return true;
            // Browsers in full screen (a YouTube video, for example) do not always report it,
            // so also treat a foreground window that covers its whole monitor as full screen.
            return ForegroundCoversMonitor();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int Size;
            public RECT Monitor;
            public RECT Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        private static bool ForegroundCoversMonitor()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero || !IsWindowVisible(window))
                return false;
            string className = WindowClass(window);
            if (className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd")
                return false;
            // A maximized window also overhangs the monitor by its invisible borders.
            if (IsZoomed(window))
                return false;
            RECT bounds;
            if (!GetWindowRect(window, out bounds))
                return false;
            var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), ref info))
                return false;
            return bounds.Left <= info.Monitor.Left && bounds.Top <= info.Monitor.Top &&
                   bounds.Right >= info.Monitor.Right && bounds.Bottom >= info.Monitor.Bottom;
        }

        [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        internal static void TryEnablePerMonitorDpi()
        {
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch (EntryPointNotFoundException)
            {
                // The manifest still provides DPI awareness on older systems.
            }
        }

        internal static string WindowClass(IntPtr hWnd)
        {
            var buffer = new StringBuilder(256);
            GetClassName(hWnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        internal static string WindowText(IntPtr hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            if (length <= 0)
                return string.Empty;
            var buffer = new StringBuilder(length + 1);
            GetWindowText(hWnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }
    }
}
