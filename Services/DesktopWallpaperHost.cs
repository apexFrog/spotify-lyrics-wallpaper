using System.Runtime.InteropServices;

namespace SpotifyLyricsWallpaper.Services;

public static class DesktopWallpaperHost
{
    private const uint SMTO_NORMAL = 0x0000;
    private const uint WM_SPAWN_WORKER = 0x052C;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private static readonly IntPtr HWND_BOTTOM = new(1);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64Val(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, System.Text.StringBuilder pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string? pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Finds or spawns the desktop WorkerW layer behind desktop icons.
    /// Supports both Windows 10/11 (21H2-23H2) sibling WorkerW and Windows 11 24H2+ Progman-child WorkerW.
    /// </summary>
    public static IntPtr GetWorkerW()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        // Ask Progman to spawn the WorkerW wallpaper host layer
        SendMessageTimeout(progman, WM_SPAWN_WORKER, new IntPtr(0x0D), new IntPtr(0x01), SMTO_NORMAL, 1000, out _);

        IntPtr workerW = IntPtr.Zero;

        // Classic Win10 / Win11 21H2-23H2: find top-level window owning SHELLDLL_DefView, then its sibling WorkerW
        EnumWindows((topHandle, _) =>
        {
            IntPtr defView = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                workerW = FindWindowEx(IntPtr.Zero, topHandle, "WorkerW", null);
                return false;
            }
            return true;
        }, IntPtr.Zero);

        // Windows 11 24H2+: WorkerW can be a direct child of Progman behind SHELLDLL_DefView
        if (workerW == IntPtr.Zero)
        {
            workerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        }

        // Fallback to Progman itself if WorkerW wasn't created
        return workerW != IntPtr.Zero ? workerW : progman;
    }

    public static bool AttachToDesktop(Form form, Screen targetScreen)
    {
        IntPtr workerW = GetWorkerW();
        if (workerW == IntPtr.Zero) return false;

        IntPtr hwnd = form.Handle;

        // Convert to child borderless window so it lives cleanly inside WorkerW
        long style = GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64();
        style &= ~WS_POPUP;
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr64Val(hwnd, GWL_STYLE, new IntPtr(style));

        long exStyle = GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr64Val(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        SetParent(hwnd, workerW);

        // WorkerW coordinate space starts at VirtualScreen.Location
        var virtualBounds = SystemInformation.VirtualScreen;
        int relX = targetScreen.Bounds.X - virtualBounds.X;
        int relY = targetScreen.Bounds.Y - virtualBounds.Y;

        SetWindowPos(
            hwnd,
            HWND_BOTTOM,
            relX,
            relY,
            targetScreen.Bounds.Width,
            targetScreen.Bounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);

        return true;
    }

    public static void DetachFromDesktop(Form form, Screen targetScreen)
    {
        IntPtr hwnd = form.Handle;
        SetParent(hwnd, IntPtr.Zero);

        long style = GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64();
        style &= ~WS_CHILD;
        SetWindowLongPtr64Val(hwnd, GWL_STYLE, new IntPtr(style));

        long exStyle = GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle &= ~(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        SetWindowLongPtr64Val(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        form.FormBorderStyle = FormBorderStyle.Sizable;
        form.StartPosition = FormStartPosition.Manual;
        int w = Math.Min(1280, targetScreen.WorkingArea.Width - 80);
        int h = Math.Min(720, targetScreen.WorkingArea.Height - 80);
        form.Bounds = new Rectangle(
            targetScreen.WorkingArea.X + (targetScreen.WorkingArea.Width - w) / 2,
            targetScreen.WorkingArea.Y + (targetScreen.WorkingArea.Height - h) / 2,
            w, h);

        RestoreDesktopWallpaper();
    }

    /// <summary>
    /// Forces Windows Explorer to redraw the native desktop wallpaper so WorkerW
    /// does not remain as a black screen after detaching or exiting.
    /// </summary>
    public static void RestoreDesktopWallpaper(Form? attachedForm = null)
    {
        try
        {
            if (attachedForm != null && !attachedForm.IsDisposed && attachedForm.IsHandleCreated)
            {
                SetParent(attachedForm.Handle, IntPtr.Zero);
                attachedForm.Hide();
            }

            // Read current Windows wallpaper path and re-apply it to dismiss the blank WorkerW layer
            var sb = new System.Text.StringBuilder(512);
            string? wallpaperPath = null;
            if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
            {
                var p = sb.ToString().TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    wallpaperPath = p;
            }

            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, wallpaperPath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
                RedrawWindow(progman, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        }
        catch { }
    }

    public static Screen ResolveScreen(string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = Screen.AllScreens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    /// <summary>
    /// Detects if another foreground application covers the target screen in fullscreen.
    /// </summary>
    public static bool IsFullscreenAppOnScreen(Screen targetScreen, IntPtr ignoreHwnd1, IntPtr ignoreHwnd2)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ignoreHwnd1 || fg == ignoreHwnd2) return false;
        if (!IsWindowVisible(fg)) return false;

        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        string className = cls.ToString();
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        if (!GetWindowRect(fg, out var r)) return false;
        var b = targetScreen.Bounds;
        return r.Left <= b.Left + 2 && r.Top <= b.Top + 2 && r.Right >= b.Right - 2 && r.Bottom >= b.Bottom - 2;
    }
}
